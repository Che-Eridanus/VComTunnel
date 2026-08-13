using System.Net.Sockets;

namespace VComTunnel.Core;

public static class TunnelTcpOptions
{
    internal const int KeepAliveTimeSeconds = 5;
    internal const int KeepAliveIntervalSeconds = 1;
    internal const int KeepAliveRetryCount = 3;

    public static void ConfigureLowLatency(TcpClient client)
    {
        client.NoDelay = true;
        var socket = client.Client;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveTimeSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveIntervalSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveRetryCount);
    }
}
