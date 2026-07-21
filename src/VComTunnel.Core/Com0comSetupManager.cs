using System.Diagnostics;
using Microsoft.Win32;

namespace VComTunnel.Core;

public sealed class Com0comSetupManager
{
    private static readonly TimeSpan SetupcRunTimeout = TimeSpan.FromSeconds(90);
    internal const string VisiblePortOptions = "EmuBR=yes,EmuOverrun=yes";
    internal const string BackingPortOptions = "EmuBR=no,EmuOverrun=yes";
    private const string SerialCommKey = @"HARDWARE\DEVICEMAP\SERIALCOMM";
    private const string Com0comPortEnumKey = @"SYSTEM\CurrentControlSet\Enum\COM0COM\PORT";

    private readonly ConfigStore _configStore;
    private readonly DependencyDetector _dependencyDetector;
    private readonly IComPortInventory _comPortInventory;

    public Com0comSetupManager(
        ConfigStore configStore,
        DependencyDetector dependencyDetector,
        IComPortInventory comPortInventory)
    {
        _configStore = configStore;
        _dependencyDetector = dependencyDetector;
        _comPortInventory = comPortInventory;
    }

    public IReadOnlyList<Com0comPairInfo> GetPairs() => _comPortInventory.GetCom0comPairs();

    internal static string BuildInstallArguments(string visiblePort, string backingPort) =>
        $"install PortName={visiblePort},{VisiblePortOptions} PortName={backingPort},{BackingPortOptions}";

    public async Task<SetupcCommandPlan> BuildCreatePlanAsync(string mappingId, CancellationToken cancellationToken = default)
    {
        var mapping = await GetMappingAsync(mappingId, cancellationToken);
        if (mapping.Backend is not (TunnelBackend.Com0comHub4com or TunnelBackend.Com0comService))
        {
            throw new InvalidOperationException("Only com0com mappings can create com0com pairs.");
        }

        if (string.IsNullOrWhiteSpace(mapping.BackingPort))
        {
            throw new InvalidOperationException("backingPort is required for com0com pair creation.");
        }

        if (_comPortInventory.GetCom0comPairs().Any(pair => PairMatchesMapping(pair, mapping)))
        {
            throw new InvalidOperationException($"com0com pair {mapping.VisiblePort} <-> {mapping.BackingPort} already exists.");
        }

        return BuildPlan(
            BuildInstallArguments(mapping.VisiblePort, mapping.BackingPort),
            $"Create com0com pair {mapping.VisiblePort} <-> {mapping.BackingPort}");
    }

    public async Task<SetupcCommandRunResult> CreatePairAsync(string mappingId, CancellationToken cancellationToken = default)
    {
        var plan = await BuildCreatePlanAsync(mappingId, cancellationToken).ConfigureAwait(false);
        return await RunPlanAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<SetupcCommandPlan> BuildRepairPlans(Com0comPairInfo pair, TunnelMapping mapping)
    {
        if (pair.PairNumber < 0)
        {
            throw new InvalidOperationException("pairNumber must be zero or greater.");
        }

        var portAOptions = BuildPortOptions(pair.PortA, mapping);
        var portBOptions = BuildPortOptions(pair.PortB, mapping);
        return
        [
            BuildPlan(
                $"change CNCA{pair.PairNumber} {portAOptions}",
                $"Repair com0com pair {pair.PairNumber} endpoint CNCA{pair.PairNumber}"),
            BuildPlan(
                $"change CNCB{pair.PairNumber} {portBOptions}",
                $"Repair com0com pair {pair.PairNumber} endpoint CNCB{pair.PairNumber}")
        ];
    }

    public async Task<IReadOnlyList<SetupcCommandPlan>> BuildConfiguredPairRepairPlansAsync(
        CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mappings = config.Mappings
            .Where(mapping => mapping.Backend is TunnelBackend.Com0comHub4com or TunnelBackend.Com0comService)
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.BackingPort))
            .ToArray();

        return _comPortInventory.GetCom0comPairs()
            .Where(pair => pair.IsComplete && mappings.Any(mapping => PairMatchesMapping(pair, mapping)))
            .Select(pair => new
            {
                Pair = pair,
                Mapping = mappings.First(mapping => PairMatchesMapping(pair, mapping))
            })
            .OrderBy(item => item.Pair.PairNumber)
            .SelectMany(item => BuildRepairPlans(item.Pair, item.Mapping))
            .ToArray();
    }

    public async Task<IReadOnlyList<SetupcCommandRunResult>> RepairConfiguredPairsAsync(
        CancellationToken cancellationToken = default)
    {
        var plans = await BuildConfiguredPairRepairPlansAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<SetupcCommandRunResult>(plans.Count);
        foreach (var plan in plans)
        {
            results.Add(await RunPlanAsync(plan, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public SetupcCommandPlan BuildRemovePlan(int pairNumber)
    {
        if (pairNumber < 0)
        {
            throw new InvalidOperationException("pairNumber must be zero or greater.");
        }

        return BuildPlan($"remove {pairNumber}", $"Remove com0com pair {pairNumber}");
    }

    public async Task<SetupcCommandRunResult> RemovePairAsync(int pairNumber, CancellationToken cancellationToken = default)
    {
        var pair = _comPortInventory.GetCom0comPairs().FirstOrDefault(item => item.PairNumber == pairNumber);
        var staleCleanup = TryRemoveStaleSerialCommValues(pair);
        if (staleCleanup > 0)
        {
            return new SetupcCommandRunResult(
                true,
                0,
                null,
                $"Removed {staleCleanup} stale com0com registry value(s) for pair {pairNumber}");
        }

        var plan = BuildRemovePlan(pairNumber);
        return await RunPlanAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TunnelMapping> GetMappingAsync(string mappingId, CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        return config.Mappings.FirstOrDefault(m => string.Equals(m.Id, mappingId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Mapping '{mappingId}' was not found.");
    }

    private SetupcCommandPlan BuildPlan(string arguments, string description)
    {
        var setupc = _dependencyDetector.FindSetupc()
            ?? throw new FileNotFoundException("com0com setupc.exe was not found.");

        return new SetupcCommandPlan(
            setupc,
            Path.GetDirectoryName(setupc),
            arguments,
            RequiresElevation: true,
            description);
    }

    private static async Task<SetupcCommandRunResult> RunPlanAsync(
        SetupcCommandPlan plan,
        CancellationToken cancellationToken)
    {
        // The installed Windows service owns driver-level changes so the UI does
        // not need to raise a UAC prompt for every COM add/remove operation.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SetupcRunTimeout);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = plan.FileName,
                Arguments = plan.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(plan.WorkingDirectory)
                    ? Environment.CurrentDirectory
                    : plan.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = (await stderr.ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                output = (await stdout.ConfigureAwait(false)).Trim();
            }

            return new SetupcCommandRunResult(
                process.ExitCode == 0,
                process.ExitCode,
                process.ExitCode == 0
                    ? null
                    : string.IsNullOrWhiteSpace(output)
                        ? $"setupc exited with code {process.ExitCode}."
                        : output,
                plan.Description);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new SetupcCommandRunResult(
                false,
                null,
                $"setupc timed out after {SetupcRunTimeout.TotalSeconds:0} seconds.",
                plan.Description);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new SetupcCommandRunResult(false, null, ex.Message, plan.Description);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static bool PairMatchesMapping(Com0comPairInfo pair, TunnelMapping mapping)
    {
        return PairHasPort(pair, mapping.VisiblePort)
            && PairHasPort(pair, mapping.BackingPort);
    }

    private static bool PairHasPort(Com0comPairInfo pair, string? port)
    {
        return !string.IsNullOrWhiteSpace(port)
            && (string.Equals(pair.PortA, port, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.PortB, port, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildPortOptions(string? pairPort, TunnelMapping mapping)
    {
        // The remote UART already provides physical baud pacing. Enabling com0com
        // baud emulation on the service-owned backing port adds timer latency to
        // small RFC2217 packets and can break short esptool response deadlines.
        return string.Equals(pairPort, mapping.BackingPort, StringComparison.OrdinalIgnoreCase)
            ? BackingPortOptions
            : VisiblePortOptions;
    }

    private static int TryRemoveStaleSerialCommValues(Com0comPairInfo? pair)
    {
        if (!OperatingSystem.IsWindows() || pair is null || pair.IsComplete)
        {
            return 0;
        }

        var staleValueNames = new[] { pair.DeviceA, pair.DeviceB }
            .Where(valueName => !string.IsNullOrWhiteSpace(valueName))
            .Where(valueName => !Com0comPortInstanceExists(valueName!))
            .Select(valueName => valueName!)
            .ToArray();

        if (staleValueNames.Length == 0)
        {
            return 0;
        }

        try
        {
#pragma warning disable CA1416
            using var key = Registry.LocalMachine.OpenSubKey(SerialCommKey, writable: true);
            if (key is null)
            {
                return 0;
            }

            var removed = 0;
            foreach (var valueName in staleValueNames)
            {
                if (key.GetValue(valueName) is null)
                {
                    continue;
                }

                key.DeleteValue(valueName, throwOnMissingValue: false);
                removed += 1;
            }

            return removed;
#pragma warning restore CA1416
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }

    private static bool Com0comPortInstanceExists(string deviceName)
    {
        if (!TryBuildCom0comPortInstanceName(deviceName, out var instanceName))
        {
            return true;
        }

        try
        {
#pragma warning disable CA1416
            using var key = Registry.LocalMachine.OpenSubKey($@"{Com0comPortEnumKey}\{instanceName}");
            return key is not null;
#pragma warning restore CA1416
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static bool TryBuildCom0comPortInstanceName(string deviceName, out string instanceName)
    {
        instanceName = "";
        const string prefix = @"\Device\com0com";
        if (!deviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || deviceName.Length <= prefix.Length + 1)
        {
            return false;
        }

        var side = deviceName[prefix.Length];
        if (side is not ('1' or '2'))
        {
            return false;
        }

        var pairNumberText = deviceName[(prefix.Length + 1)..];
        if (!int.TryParse(pairNumberText, out var pairNumber))
        {
            return false;
        }

        instanceName = $"{(side == '1' ? "CNCA" : "CNCB")}{pairNumber}";
        return true;
    }
}
