namespace VComTunnel.Core;

public interface IManagedTunnelSession : IDisposable
{
    TunnelRunState State { get; }
    string? LastError { get; }
    Task StartAsync(CancellationToken cancellationToken);
    void UpdateMapping(TunnelMapping mapping) { }
}
