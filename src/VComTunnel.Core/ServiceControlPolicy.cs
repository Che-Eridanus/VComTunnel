namespace VComTunnel.Core;

public static class ServiceControlPolicy
{
    /// <summary>
    /// Only idempotent reads remain available on loopback HTTP. Every request
    /// that can change service, mapping, driver, log, or discovery state must
    /// arrive over the Windows named-pipe control channel.
    /// </summary>
    public static bool RequiresProtectedTransport(string method) =>
        !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);
}
