using System.Net.Sockets;

namespace VComTunnel.Core;

public static class TunnelTcpOptions
{
    public static void ConfigureLowLatency(TcpClient client)
    {
        client.NoDelay = true;
    }
}