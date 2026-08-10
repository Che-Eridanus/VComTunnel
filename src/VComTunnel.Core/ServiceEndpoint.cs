using System.IO.Pipes;
using System.Net;

namespace VComTunnel.Core;

public static class ServiceEndpoint
{
    public const string DefaultUrl = "http://127.0.0.1:44817";
    public const string EnvironmentVariable = "VCOMTUNNEL_SERVICE_URL";
    public const string ControlPipeName = "VComTunnel.Control.v1";
    public const string ControlPipeEnvironmentVariable = "VCOMTUNNEL_CONTROL_PIPE";

    public static string GetBaseUrl()
    {
        var configuredUrl = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            return DefaultUrl;
        }

        return NormalizeLoopbackHttpUrl(configuredUrl);
    }

    public static Uri GetBaseUri() => new(GetBaseUrl());

    public static string GetControlPipeName()
    {
        var configured = Environment.GetEnvironmentVariable(ControlPipeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return ControlPipeName;
        }

        if (configured.Length > 128
            || configured.Any(value =>
                value is not (>= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '.' or '_' or '-')))
        {
            throw new InvalidOperationException(
                $"{ControlPipeEnvironmentVariable} must be a simple local pipe name.");
        }

        return configured;
    }

    /// <summary>
    /// Creates the client used for state-changing service operations. On
    /// Windows these requests travel over the service ACL-protected named pipe,
    /// so the desktop application can use the already elevated service without
    /// prompting for UAC on every operation. The loopback fallback is retained
    /// only for non-Windows development hosts.
    /// </summary>
    public static HttpClient CreateControlClient()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new HttpClient { BaseAddress = GetBaseUri() };
        }

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new NamedPipeClientStream(
                    ".",
                    GetControlPipeName(),
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(cancellationToken);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    private static string NormalizeLoopbackHttpUrl(string configuredUrl)
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must be an absolute HTTP loopback URL.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must use http.");
        }

        if (!uri.IsLoopback)
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must point to a loopback host.");
        }

        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must not include a path.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must not include a query or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
