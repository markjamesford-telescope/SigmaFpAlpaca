using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SigmaFp.Camera.Usb;

namespace SigmaFp.Camera.Ptp;

public readonly record struct PtpResponse(ushort Code, uint[] Params, byte[] Data)
{
    public const ushort OkCode = 0x2001;
    public bool Ok => Code == OkCode;
}

/// <summary>
/// PTP (PIMA 15740) bulk-container framing over an <see cref="IUsbPipe"/>. One transaction at a
/// time; only the camera worker thread may use it. Any framing problem throws
/// <see cref="CameraCommException"/>: the caller closes and reopens; nothing is "reset".
/// </summary>
public sealed class PtpTransport : IDisposable
{
    const ushort ContainerCommand = 1, ContainerData = 2, ContainerResponse = 3;
    const int HeaderSize = 12;
    const int ChunkSize = 1024 * 1024;
    public const ushort OpGetDeviceInfo = 0x1001, OpOpenSession = 0x1002, OpCloseSession = 0x1003;
    const ushort RespSessionAlreadyOpen = 0x201E;

    readonly IUsbPipe _pipe;
    readonly ILogger _log;
    uint _transactionId;
    bool _sessionOpen;
    byte[] _pending = [];

    public int DefaultTimeoutMs { get; set; } = 5000;

    public PtpTransport(IUsbPipe pipe, ILogger log) => (_pipe, _log) = (pipe, log);

    public void OpenSession(uint sessionId = 1)
    {
        _transactionId = 0;
        var r = Transaction(OpOpenSession, [sessionId]);
        if (r.Code == RespSessionAlreadyOpen) _log.LogInformation("PTP session already open; reusing it");
        else if (!r.Ok) throw new CameraProtocolException($"OpenSession failed: 0x{r.Code:X4}", r.Code);
        _sessionOpen = true;
    }

    public void CloseSession()
    {
        if (!_sessionOpen) return;
        _sessionOpen = false;
        var r = Transaction(OpCloseSession, []);
        if (!r.Ok) _log.LogWarning("CloseSession returned 0x{Code:X4}", r.Code);
    }

    /// <summary>Command, optional data-out phase, optional data-in phase, response.</summary>
    public PtpResponse Transaction(ushort opcode, uint[] parameters, byte[]? dataOut = null, int? timeoutMs = null)
    {
        int timeout = timeoutMs ?? DefaultTimeoutMs;
        uint tid = opcode == OpOpenSession ? 0 : ++_transactionId;
        var sw = Stopwatch.StartNew();

        var cmd = new byte[HeaderSize + 4 * parameters.Length];
        WriteHeader(cmd, (uint)cmd.Length, ContainerCommand, opcode, tid);
        for (int i = 0; i < parameters.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(cmd.AsSpan(HeaderSize + 4 * i), parameters[i]);
        _pipe.Write(cmd, timeout);

        if (dataOut != null)
        {
            var data = new byte[HeaderSize + dataOut.Length];
            WriteHeader(data, (uint)data.Length, ContainerData, opcode, tid);
            dataOut.CopyTo(data, HeaderSize);
            _pipe.Write(data, timeout);
            if (data.Length % _pipe.MaxPacketSize == 0) _pipe.Write([], timeout); // ZLP terminates the transfer
        }

        byte[] dataIn = [];
        while (true)
        {
            var (type, code, rtid, payload) = ReadContainer(timeout);
            if (rtid != tid)
                throw new CameraCommException($"PTP transaction ID mismatch on op 0x{opcode:X4}: sent {tid}, got {rtid}");
            if (type == ContainerData) { dataIn = payload; continue; }
            if (type != ContainerResponse) throw new CameraCommException($"Unexpected PTP container type {type}");
            var ps = new uint[payload.Length / 4];
            for (int i = 0; i < ps.Length; i++) ps[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4 * i));
            _log.LogDebug("PTP 0x{Op:X4} tid={Tid} -> 0x{Code:X4} in={Len} {Ms} ms", opcode, tid, code, dataIn.Length, sw.ElapsedMilliseconds);
            return new PtpResponse(code, ps, dataIn);
        }
    }

    (ushort type, ushort code, uint tid, byte[] payload) ReadContainer(int timeout)
    {
        byte[] head = _pending;
        _pending = [];
        if (head.Length < HeaderSize)
        {
            var buf = new byte[Math.Max(_pipe.MaxPacketSize, 512) * 64];
            head.CopyTo(buf, 0);
            int n = head.Length, zlps = 0;
            while (n < HeaderSize)
            {
                int got = _pipe.Read(buf.AsSpan(n), timeout); // 0 = stray zero-length packet
                if (got == 0 && ++zlps > 4) throw new CameraCommException("Only zero-length packets received");
                n += got;
            }
            head = buf[..n];
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(head);
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(4));
        ushort code = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6));
        uint tid = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
        if (length < HeaderSize || length > 512 * 1024 * 1024)
            throw new CameraCommException($"Bad PTP container length {length}");

        var payload = new byte[length - HeaderSize];
        int have = Math.Min(head.Length - HeaderSize, payload.Length);
        Array.Copy(head, HeaderSize, payload, 0, have);
        if (head.Length > length) _pending = head[(int)length..]; // next container already read
        int empty = 0;
        while (have < payload.Length)
        {
            int got = _pipe.Read(payload.AsSpan(have, Math.Min(ChunkSize, payload.Length - have)), timeout);
            if (got == 0 && ++empty > 3) throw new CameraCommException($"PTP data ended early: {have} of {payload.Length} bytes");
            have += got;
        }
        return (type, code, tid, payload);
    }

    static void WriteHeader(byte[] b, uint length, ushort type, ushort code, uint tid)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b, length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), type);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), code);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), tid);
    }

    public void Dispose() => _pipe.Dispose();
}
