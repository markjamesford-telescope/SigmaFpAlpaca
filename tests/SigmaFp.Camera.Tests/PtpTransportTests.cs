using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using SigmaFp.Camera.Ptp;
using SigmaFp.Camera.Usb;

namespace SigmaFp.Camera.Tests;

public class PtpTransportTests
{
    /// <summary>Fake bulk pipe: records writes, returns scripted reads (each element = one USB read).</summary>
    sealed class FakePipe(int maxPacket = 512) : IUsbPipe
    {
        public readonly List<byte[]> Writes = new();
        public readonly Queue<byte[]> Reads = new();
        public int MaxPacketSize => maxPacket;
        public void Write(ReadOnlySpan<byte> data, int timeoutMs) => Writes.Add(data.ToArray());
        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (Reads.Count == 0) throw new UsbTimeoutException("no scripted data");
            var next = Reads.Dequeue();
            if (next.Length > buffer.Length) // split like a real bulk read
            {
                next.AsSpan(0, buffer.Length).CopyTo(buffer);
                var rest = new Queue<byte[]>(new[] { next[buffer.Length..] }.Concat(Reads));
                Reads.Clear();
                foreach (var r in rest) Reads.Enqueue(r);
                return buffer.Length;
            }
            next.CopyTo(buffer);
            return next.Length;
        }
        public void Dispose() { }
    }

    static byte[] Container(ushort type, ushort code, uint tid, byte[]? payload = null)
    {
        payload ??= [];
        var b = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)b.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), type);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), code);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), tid);
        payload.CopyTo(b, 12);
        return b;
    }

    static PtpTransport Open(FakePipe pipe)
    {
        pipe.Reads.Enqueue(Container(3, 0x2001, 0)); // OpenSession response
        var t = new PtpTransport(pipe, NullLogger.Instance);
        t.OpenSession();
        pipe.Writes.Clear();
        return t;
    }

    [Fact]
    public void Command_container_layout()
    {
        var pipe = new FakePipe();
        var t = Open(pipe);
        pipe.Reads.Enqueue(Container(3, 0x2001, 1));
        t.Transaction(0x9015, [7]);
        Assert.Equal(Convert.FromHexString("10000000" + "0100" + "1590" + "01000000" + "07000000"), pipe.Writes[0]);
    }

    [Fact]
    public void Data_in_split_over_many_reads()
    {
        var pipe = new FakePipe();
        var t = Open(pipe);
        var payload = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        var data = Container(2, 0x9022, 1, payload);
        pipe.Reads.Enqueue(data[..500]);
        pipe.Reads.Enqueue(data[500..2000]);
        pipe.Reads.Enqueue(data[2000..]);
        pipe.Reads.Enqueue(Container(3, 0x2001, 1));
        var r = t.Transaction(0x9022, [0, 0, 3000]);
        Assert.True(r.Ok);
        Assert.Equal(payload, r.Data);
    }

    [Fact]
    public void Data_and_response_in_one_read_without_zlp()
    {
        var pipe = new FakePipe();
        var t = Open(pipe);
        var both = Container(2, 0x9012, 1, [1, 2, 3]).Concat(Container(3, 0x2001, 1)).ToArray();
        pipe.Reads.Enqueue(both);
        var r = t.Transaction(0x9012, []);
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Data);
    }

    [Fact]
    public void Stray_zero_length_packet_is_skipped()
    {
        var pipe = new FakePipe();
        var t = Open(pipe);
        pipe.Reads.Enqueue(Container(2, 0x9012, 1, [9]));
        pipe.Reads.Enqueue([]);
        pipe.Reads.Enqueue(Container(3, 0x2001, 1));
        Assert.Equal(new byte[] { 9 }, t.Transaction(0x9012, []).Data);
    }

    [Fact]
    public void Data_out_multiple_of_max_packet_sends_zlp()
    {
        var pipe = new FakePipe(maxPacket: 16);
        var t = Open(pipe);
        pipe.Reads.Enqueue(Container(3, 0x2001, 1));
        t.Transaction(0x9016, [], new byte[4]); // 12 + 4 = 16 bytes
        Assert.Equal(3, pipe.Writes.Count);
        Assert.Empty(pipe.Writes[2]);
    }

    [Fact]
    public void Transaction_id_mismatch_is_comm_error_not_reset()
    {
        var pipe = new FakePipe();
        var t = Open(pipe);
        pipe.Reads.Enqueue(Container(3, 0x2001, 99));
        Assert.Throws<CameraCommException>(() => t.Transaction(0x9012, []));
    }

    [Fact]
    public void Timeout_propagates()
    {
        var pipe = new FakePipe();
        var t = Open(pipe);
        Assert.Throws<UsbTimeoutException>(() => t.Transaction(0x9012, []));
    }

    [Fact]
    public void Session_not_open_after_camera_restart_is_comm_error()
    {
        // Seen 2026-10-07 after a battery pull and after the camera's delayed power-off: GetCaptStatus -> 0x2003.
        var pipe = new FakePipe();
        var deviceInfo = new byte[35]; // empty strings and arrays: enough for ParseDeviceInfo
        pipe.Reads.Enqueue(Container(2, 0x1001, 1, deviceInfo));
        pipe.Reads.Enqueue(Container(3, 0x2001, 1));
        pipe.Reads.Enqueue(Container(3, 0x2001, 0));       // OpenSession
        pipe.Reads.Enqueue(Container(3, 0x2001, 1));       // ConfigApi (no data)
        pipe.Reads.Enqueue(Container(3, 0x2003, 2));       // GetCaptStatus: session not open
        var s = new SigmaFp.Camera.Session.SigmaPtpSession(NullLogger.Instance, () => pipe);
        s.Open();
        var ex = Assert.Throws<CameraCommException>(() => s.GetStatus(0));
        Assert.Contains("no longer open", ex.Message);
    }

    [Fact]
    public void Response_code_and_params_returned()
    {
        var pipe = new FakePipe();
        var t = Open(pipe);
        pipe.Reads.Enqueue(Container(3, 0x2019, 1, [5, 0, 0, 0]));
        var r = t.Transaction(0x901B, [], [0, 2, 1, 0]);
        Assert.False(r.Ok);
        Assert.Equal((ushort)0x2019, r.Code);
        Assert.Equal(new uint[] { 5 }, r.Params);
    }
}
