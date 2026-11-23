using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VComTunnel.Core;

internal sealed class PtyPortEndpointFactory : ISerialPortEndpointFactory
{
    public ISerialPortEndpoint Open(string portName, bool exclusive = false) =>
        PtyPortEndpoint.Open(portName);
}

internal sealed class PtyPortEndpoint : ISerialPortEndpoint
{
    private const int Interrupted = 4;
    private const int ReadWrite = 2;
    private static readonly object PtsNameLock = new();
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(5);
    private static readonly uint[] LinuxBaudRates =
    [
        0, 50, 75, 110, 134, 150, 200, 300, 600, 1200, 1800, 2400,
        4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800,
        500000, 576000, 921600, 1000000, 1152000, 1500000, 2000000,
        2500000, 3000000, 3500000, 4000000
    ];

    private readonly SafeFileHandle _master;
    // Retain the slave descriptor so a terminal closing and reopening the
    // published path does not tear down the RFC2217 connection.
    private readonly SafeFileHandle _slave;
    private readonly string _linkPath;
    private readonly string _slavePath;
    private int _disposed;

    private PtyPortEndpoint(SafeFileHandle master, SafeFileHandle slave, string linkPath, string slavePath)
    {
        _master = master;
        _slave = slave;
        _linkPath = linkPath;
        _slavePath = slavePath;
    }

    public static PtyPortEndpoint Open(string linkPath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("PTY tunnels require Linux or macOS.");
        }

        var noControllingTerminal = OperatingSystem.IsMacOS() ? 0x20000 : 0x100;
        var closeOnExec = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
        var nonBlocking = OperatingSystem.IsMacOS() ? 0x04 : 0x800;
        var flags = ReadWrite | noControllingTerminal | closeOnExec;
        var masterFd = OpenMaster(flags | nonBlocking);
        Check(masterFd, "open PTY master");
        var master = new SafeFileHandle(masterFd, ownsHandle: true);
        SafeFileHandle? slave = null;
        try
        {
            Check(GrantSlave(master), "grant PTY slave");
            Check(UnlockSlave(master), "unlock PTY slave");
            string slavePath;
            lock (PtsNameLock)
            {
                // ptsname can return a process-wide static buffer.
                var pointer = GetSlavePath(master);
                slavePath = Marshal.PtrToStringUTF8(pointer)
                    ?? throw NativeError("read PTY slave path", Marshal.GetLastPInvokeError());
            }
            var slaveFd = OpenSlave(slavePath, flags);
            Check(slaveFd, "open PTY slave");
            slave = new SafeFileHandle(slaveFd, ownsHandle: true);
            Check(GetTerminalAttributes(slave, out var attributes), "read PTY configuration");
            MakeRaw(ref attributes);
            var initialSpeed = (nuint)(OperatingSystem.IsMacOS() ? 115200 : 4098);
            Check(SetInputSpeed(ref attributes, initialSpeed), "set PTY input speed");
            Check(SetOutputSpeed(ref attributes, initialSpeed), "set PTY output speed");
            Check(SetTerminalAttributes(slave, 0, ref attributes), "set raw PTY configuration");

            // Never replace existing files, directories or links.
            File.CreateSymbolicLink(linkPath, slavePath);
            return new PtyPortEndpoint(master, slave, linkPath, slavePath);
        }
        catch
        {
            slave?.Dispose();
            master.Dispose();
            throw;
        }
    }

    public bool SupportsModemStatusEvents => false;
    public SerialPeerSettingsInsertion EnablePeerSettingsInsertion(byte escapeChar) =>
        SerialPeerSettingsInsertion.Unavailable();
    public int GetPendingReadBytes() => 0;
    public uint GetModemStatus() => 0;
    public uint WaitForModemStatusChange(CancellationToken cancellationToken) =>
        throw new NotSupportedException("PTY terminals do not expose modem-control events.");

    public ValueTask<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken) =>
        ReadAsync(buffer, 0, buffer.Length, cancellationToken);

    public async ValueTask<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = ReadNative(_master, ref buffer[offset], (nuint)count);
            if (length >= 0)
            {
                return (int)length;
            }
            var error = Marshal.GetLastPInvokeError();
            if (!CanRetry(error))
            {
                throw NativeError("read PTY", error);
            }
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask WriteAsync(
        byte[] bytes, CancellationToken cancellationToken, Action<SerialPortBackpressureInfo>? backpressure = null)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = WriteNative(_master, ref bytes[offset], (nuint)(bytes.Length - offset));
            if (length > 0)
            {
                offset += (int)length;
                continue;
            }
            var error = Marshal.GetLastPInvokeError();
            if (length < 0 && !CanRetry(error))
            {
                throw NativeError("write PTY", error);
            }
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    public void PurgePendingWrites() =>
        Check(FlushTerminal(_master, OperatingSystem.IsMacOS() ? 2 : 1), "flush PTY output");

    public SerialPortSnapshot GetSnapshot()
    {
        var settings = GetSettings();
        return new SerialPortSnapshot(0, settings.BaudRate, settings.ByteSize, settings.Parity, settings.StopBits);
    }

    public SerialPortSettings GetSettings()
    {
        Check(GetTerminalAttributes(_slave, out var attributes), "read PTY settings");
        var speed = (uint)GetOutputSpeed(ref attributes);
        if (OperatingSystem.IsMacOS())
        {
            var control = attributes.MacControlFlags;
            return new SerialPortSettings(speed, (byte)(5 + ((control >> 8) & 3)),
                Parity(control, 0x1000, 0x2000), (byte)((control & 0x400) == 0 ? 0 : 2));
        }

        var linuxControl = attributes.LinuxControlFlags;
        var speedIndex = speed >= 4097 ? speed - 4081 : speed;
        var baudRate = speedIndex < LinuxBaudRates.Length ? LinuxBaudRates[speedIndex] : 0;
        return new SerialPortSettings(baudRate, (byte)(5 + ((linuxControl >> 4) & 3)),
            Parity(linuxControl, 0x100, 0x200), (byte)((linuxControl & 0x40) == 0 ? 0 : 2));
    }

    public void SetSettings(SerialPortSettings settings) =>
        throw new NotSupportedException("Set serial settings through the local PTY slave.");
    public void SetControlLines(bool? dtr, bool? rts) =>
        throw new NotSupportedException("PTY terminals do not expose modem-control lines.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            if (new FileInfo(_linkPath).LinkTarget == _slavePath)
            {
                File.Delete(_linkPath);
            }
        }
        finally
        {
            _slave.Dispose();
            _master.Dispose();
        }
    }

    private static byte Parity(ulong flags, ulong enabled, ulong odd) =>
        (flags & enabled) == 0 ? (byte)0 : (flags & odd) != 0 ? (byte)1 : (byte)2;

    private static bool CanRetry(int error) =>
        error == Interrupted || error == (OperatingSystem.IsMacOS() ? 35 : 11);

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw NativeError(operation, Marshal.GetLastPInvokeError());
        }
    }

    private static IOException NativeError(string operation, int error) =>
        new($"Could not {operation}: {new Win32Exception(error).Message} (errno {error}).");

    // The native tcflag_t fields are 32 bits on Linux and unsigned long on
    // 64-bit Darwin. The buffer leaves room for both native termios layouts.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct TerminalAttributes
    {
        [FieldOffset(8)] public uint LinuxControlFlags;
        [FieldOffset(16)] public ulong MacControlFlags;
    }

    [DllImport("libc", EntryPoint = "posix_openpt", SetLastError = true)]
    private static extern int OpenMaster(int flags);
    [DllImport("libc", EntryPoint = "grantpt", SetLastError = true)]
    private static extern int GrantSlave(SafeFileHandle master);
    [DllImport("libc", EntryPoint = "unlockpt", SetLastError = true)]
    private static extern int UnlockSlave(SafeFileHandle master);
    [DllImport("libc", EntryPoint = "ptsname", SetLastError = true)]
    private static extern nint GetSlavePath(SafeFileHandle master);
    // O_CREAT is absent, so the POSIX open call has no variadic mode argument.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenSlave([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    private static extern int GetTerminalAttributes(SafeFileHandle fd, out TerminalAttributes attributes);
    [DllImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
    private static extern int SetTerminalAttributes(SafeFileHandle fd, int action, ref TerminalAttributes attributes);
    [DllImport("libc", EntryPoint = "cfmakeraw")]
    private static extern void MakeRaw(ref TerminalAttributes attributes);
    [DllImport("libc", EntryPoint = "cfsetispeed", SetLastError = true)]
    private static extern int SetInputSpeed(ref TerminalAttributes attributes, nuint speed);
    [DllImport("libc", EntryPoint = "cfsetospeed", SetLastError = true)]
    private static extern int SetOutputSpeed(ref TerminalAttributes attributes, nuint speed);
    [DllImport("libc", EntryPoint = "cfgetospeed")]
    private static extern nuint GetOutputSpeed(ref TerminalAttributes attributes);
    [DllImport("libc", EntryPoint = "tcflush", SetLastError = true)]
    private static extern int FlushTerminal(SafeFileHandle fd, int queue);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadNative(SafeFileHandle fd, ref byte buffer, nuint length);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint WriteNative(SafeFileHandle fd, ref byte buffer, nuint length);
}
