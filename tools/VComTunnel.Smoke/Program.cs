using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VComTunnel.Core;

var portName = args.FirstOrDefault() ?? "COM27";
var tcpPort = args.Skip(1).Select(value => int.TryParse(value, out var parsed) ? parsed : 0).FirstOrDefault();
if (tcpPort <= 0)
{
    tcpPort = 44000;
}

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var server = FakeRfc2217EchoServer.RunAsync(tcpPort, cts.Token);
var log = new InMemoryLog();
using var session = new KmdfTunnelSession(
    new TunnelMapping
    {
        Id = "smoke",
        Name = "KMDF Smoke",
        Backend = TunnelBackend.Kmdf,
        VisiblePort = portName,
        BackingPort = null,
        Host = "127.0.0.1",
        Port = tcpPort,
        Protocol = TunnelProtocol.Rfc2217,
        RestartOnFailure = false
    },
    log,
    (_, error) => Console.WriteLine($"fault: {error}"));

await session.StartAsync(cts.Token);
await Task.Delay(300, cts.Token);

var payload = new byte[] { 0x56, 0x43, 0x54, 0xFF, 0x4F, 0x4B };
using var serial = NativeSerial.Open(portName);
serial.Write(payload);
await Task.Delay(300, cts.Token);
Console.WriteLine($"inQueue before read: {serial.GetInQueue()}");
var received = await serial.ReadUntilAsync(payload.Length, TimeSpan.FromSeconds(3), cts.Token);
Console.WriteLine($"inQueue after read: {serial.GetInQueue()}");

Console.WriteLine($"tx: {Convert.ToHexString(payload)}");
Console.WriteLine($"rx: {Convert.ToHexString(received)}");
foreach (var entry in log.Snapshot(20))
{
    Console.WriteLine($"{entry.Level} {entry.Source}: {entry.Message}");
}

if (!payload.SequenceEqual(received))
{
    throw new InvalidOperationException("RFC2217 smoke echo mismatch.");
}

session.Dispose();
cts.Cancel();
try
{
    await server;
}
catch (OperationCanceledException)
{
}

Console.WriteLine("RFC2217 smoke passed.");

internal sealed class NativeSerial : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private readonly SafeFileHandle _handle;

    private NativeSerial(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static NativeSerial Open(string portName)
    {
        var path = portName.StartsWith(@"\\.\", StringComparison.Ordinal) ? portName : $@"\\.\{portName}";
        var handle = CreateFileW(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open {path}.");
        }

        return new NativeSerial(handle);
    }

    public void Write(byte[] data)
    {
        if (!WriteFile(_handle, data, (uint)data.Length, out var written, IntPtr.Zero) ||
            written != data.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteFile wrote {written}/{data.Length} byte(s).");
        }
    }

    public async Task<byte[]> ReadUntilAsync(int byteCount, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var received = new List<byte>(byteCount);
        var buffer = new byte[byteCount];

        while (DateTimeOffset.UtcNow < deadline && received.Count < byteCount)
        {
            if (!ReadFile(_handle, buffer, (uint)(byteCount - received.Count), out var read, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadFile failed.");
            }

            for (var i = 0; i < read; i++)
            {
                received.Add(buffer[i]);
            }

            if (read == 0)
            {
                await Task.Delay(20, cancellationToken);
            }
        }

        return received.ToArray();
    }

    public uint GetInQueue()
    {
        var output = new byte[24];
        if (!DeviceIoControl(_handle, IoctlSerialGetCommStatus, null, 0, output, output.Length, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "IOCTL_SERIAL_GET_COMMSTATUS failed.");
        }

        return BitConverter.ToUInt32(output, 8);
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle file,
        byte[] buffer,
        uint bytesToRead,
        out uint bytesRead,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle file,
        byte[] buffer,
        uint bytesToWrite,
        out uint bytesWritten,
        IntPtr overlapped);

    private const uint IoctlSerialGetCommStatus = (0x1Bu << 16) | (27u << 2);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle file,
        uint controlCode,
        byte[]? inBuffer,
        int inBufferSize,
        byte[]? outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}

internal static class FakeRfc2217EchoServer
{
    public static async Task RunAsync(int port, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            var negotiation = new byte[] { 255, 251, 44, 255, 253, 0, 255, 251, 0 };
            await stream.WriteAsync(negotiation, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var buffer = new byte[4096];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }
}
