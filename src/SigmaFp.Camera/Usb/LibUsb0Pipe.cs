using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace SigmaFp.Camera.Usb;

/// <summary>
/// <see cref="IUsbPipe"/> over libusb-win32 (System32\libusb0.dll, x64). Every transfer has a
/// timeout. Never calls usb_reset: a port reset kills a running exposure (old driver's failure).
/// The camera must be on a USB 2.0 port; on SuperSpeed every transfer fails with -5.
/// </summary>
public sealed unsafe class LibUsb0Pipe : IUsbPipe
{
    readonly IntPtr _h;
    readonly int _iface, _epIn, _epOut;

    public int MaxPacketSize { get; }

    LibUsb0Pipe(IntPtr h, int iface, int epIn, int epOut, int maxPacket)
    {
        (_h, _iface, _epIn, _epOut, MaxPacketSize) = (h, iface, epIn, epOut, maxPacket);
    }

    public static LibUsb0Pipe Open(ushort vendorId, ILogger log)
    {
        Native.usb_init();
        Native.usb_find_busses();
        Native.usb_find_devices();
        IntPtr dev = FindDevice(vendorId);
        if (dev == IntPtr.Zero)
            throw new CameraCommException($"No libusb0 device with VID {vendorId:X4} (camera off, unplugged, or not on the libusb-win32 driver?)");

        IntPtr h = Native.usb_open(dev);
        if (h == IntPtr.Zero) throw new CameraCommException($"usb_open failed: {LastError()}");
        try
        {
            var (iface, epIn, epOut, epInt, maxPacket) = FindPtpEndpoints(h);
            Check(Native.usb_set_configuration(h, 1), "usb_set_configuration", allowFail: true, log);
            Check(Native.usb_claim_interface(h, iface), "usb_claim_interface (another program holding the camera?)");
            foreach (int ep in new[] { epIn, epOut, epInt }.Where(e => e != 0))
                Check(Native.usb_clear_halt(h, (uint)ep), $"usb_clear_halt(0x{ep:X2})",
                    allowFail: false, log, hint: "USB transfers fail: is the camera on a USB 2.0 port? (SuperSpeed does not work with libusb0)");
            log.LogInformation("USB opened: interface {Iface}, bulk-in 0x{In:X2}, bulk-out 0x{Out:X2}, maxPacket {Max}", iface, epIn, epOut, maxPacket);
            return new LibUsb0Pipe(h, iface, epIn, epOut, maxPacket);
        }
        catch
        {
            Native.usb_close(h);
            throw;
        }
    }

    public void Write(ReadOnlySpan<byte> data, int timeoutMs)
    {
        int rc;
        fixed (byte* p = data) rc = Native.usb_bulk_write(_h, _epOut, p, data.Length, timeoutMs);
        if (rc < 0) throw Fail("bulk write", rc);
        if (rc != data.Length) throw new CameraCommException($"Short bulk write {rc}/{data.Length}");
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        int rc;
        fixed (byte* p = buffer) rc = Native.usb_bulk_read(_h, _epIn, p, buffer.Length, timeoutMs);
        if (rc < 0) throw Fail("bulk read", rc);
        return rc;
    }

    public void Dispose()
    {
        Native.usb_release_interface(_h, _iface);
        Native.usb_close(_h);
    }

    static Exception Fail(string what, int rc)
    {
        string err = LastError();
        return err.Contains("timeout", StringComparison.OrdinalIgnoreCase) || rc is -116 or -138
            ? new UsbTimeoutException($"{what} timed out ({rc}): {err.Trim()}")
            : new CameraCommException($"{what} failed ({rc}): {err.Trim()}");
    }

    static void Check(int rc, string what, bool allowFail = false, ILogger? log = null, string? hint = null)
    {
        if (rc >= 0) return;
        string msg = $"{what}: {rc} {LastError().Trim()}";
        if (allowFail) { log?.LogDebug("{Msg} (ignored)", msg); return; }
        throw new CameraCommException(hint is null ? msg : $"{msg}. {hint}");
    }

    static string LastError() => Marshal.PtrToStringAnsi(Native.usb_strerror()) ?? "";

    // Offsets in libusb-win32's usb_bus / usb_device (x64): next, prev, char[512], ...
    // All are pointer-aligned, so they don't depend on struct packing.
    const int PathMax = 512;
    const int BusDevicesOffset = 8 + 8 + PathMax;
    const int DeviceDescriptorOffset = 8 + 8 + PathMax + 8;

    static IntPtr FindDevice(ushort vid)
    {
        for (IntPtr bus = Native.usb_get_busses(); bus != IntPtr.Zero; bus = Marshal.ReadIntPtr(bus))
            for (IntPtr dev = Marshal.ReadIntPtr(bus, BusDevicesOffset); dev != IntPtr.Zero; dev = Marshal.ReadIntPtr(dev))
                if ((ushort)Marshal.ReadInt16(dev, DeviceDescriptorOffset + 8) == vid)
                    return dev;
        return IntPtr.Zero;
    }

    static (int iface, int epIn, int epOut, int epInt, int maxPacket) FindPtpEndpoints(IntPtr h)
    {
        var buf = new byte[1024];
        int n;
        fixed (byte* p = buf) n = Native.usb_get_descriptor(h, 2, 0, p, buf.Length);
        if (n < 9) throw new CameraCommException($"Reading configuration descriptor failed: {n} {LastError()}");
        return DescriptorParser.FindPtpEndpoints(buf.AsSpan(0, n));
    }

    static class Native
    {
        const string Dll = "libusb0.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void usb_init();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_find_busses();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_find_devices();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr usb_get_busses();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr usb_open(IntPtr device);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_close(IntPtr handle);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_set_configuration(IntPtr handle, int configuration);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_claim_interface(IntPtr handle, int iface);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_release_interface(IntPtr handle, int iface);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_clear_halt(IntPtr handle, uint ep);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_bulk_write(IntPtr handle, int ep, byte* bytes, int size, int timeout);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_bulk_read(IntPtr handle, int ep, byte* bytes, int size, int timeout);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_get_descriptor(IntPtr handle, byte type, byte index, byte* buf, int size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr usb_strerror();
    }
}

/// <summary>Finds the PTP (still-image class 6) interface and its endpoints in a configuration descriptor.</summary>
internal static class DescriptorParser
{
    public static (int iface, int epIn, int epOut, int epInt, int maxPacket) FindPtpEndpoints(ReadOnlySpan<byte> d)
    {
        int iface = -1, epIn = 0, epOut = 0, epInt = 0, maxPacket = 512;
        bool inPtp = false;
        for (int i = 0; i + 2 <= d.Length && d[i] > 0; i += d[i])
        {
            byte type = d[i + 1];
            if (type == 4 && i + 6 <= d.Length)
            {
                inPtp = d[i + 5] == 6;
                if (inPtp && iface < 0) iface = d[i + 2];
            }
            else if (type == 5 && inPtp && i + 6 <= d.Length)
            {
                byte addr = d[i + 2], attr = (byte)(d[i + 3] & 3);
                ushort mps = BinaryPrimitives.ReadUInt16LittleEndian(d[(i + 4)..]);
                if (attr == 2 && (addr & 0x80) != 0) { epIn = addr; maxPacket = mps; }
                else if (attr == 2) epOut = addr;
                else if (attr == 3 && (addr & 0x80) != 0) epInt = addr;
            }
        }
        if (iface < 0 || epIn == 0 || epOut == 0)
            throw new CameraCommException("No PTP (class 6) interface with bulk endpoints in the configuration descriptor.");
        return (iface, epIn, epOut, epInt, maxPacket);
    }
}
