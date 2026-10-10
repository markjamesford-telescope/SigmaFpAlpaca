namespace SigmaFp.Camera.Usb;

/// <summary>Bulk-in/bulk-out pair of one claimed USB interface. Implementations are not thread-safe.</summary>
public interface IUsbPipe : IDisposable
{
    int MaxPacketSize { get; }

    /// <summary>Writes all bytes or throws <see cref="CameraCommException"/>. Empty span = zero-length packet.</summary>
    void Write(ReadOnlySpan<byte> data, int timeoutMs);

    /// <summary>Reads up to <c>buffer.Length</c> bytes; returns the count (0 = zero-length packet).</summary>
    int Read(Span<byte> buffer, int timeoutMs);
}
