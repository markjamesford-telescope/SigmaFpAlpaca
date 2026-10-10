namespace SigmaFp.Camera;

/// <summary>Communication with the camera failed (USB error, timeout, framing). Triggers reconnect.</summary>
public class CameraCommException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A USB transfer timed out.</summary>
public sealed class UsbTimeoutException(string message) : CameraCommException(message);

/// <summary>The camera answered, but refused or failed the request (PTP response code or capture status).</summary>
public sealed class CameraProtocolException(string message, ushort code = 0) : Exception(message)
{
    public ushort Code { get; } = code;
}

/// <summary>An operation needs a connected camera.</summary>
public sealed class NotConnectedException(string message) : InvalidOperationException(message);
