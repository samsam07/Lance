using System.Diagnostics;
using System.Net.Sockets;
using Lance.Agent.Configuration;
using Lance.Agent.Infrastructure;

namespace Lance.Agent.Services;

internal sealed class SlotLifecycle : ISlotLifecycle
{
    private readonly AgentConfig _config;
    private readonly IProcessTracker _tracker;
    private readonly ILogger<SlotLifecycle> _logger;

    public SlotLifecycle(AgentConfig config, IProcessTracker tracker, ILogger<SlotLifecycle> logger)
    {
        _config = config;
        _tracker = tracker;
        _logger = logger;
    }

    public async Task<LifecycleResult> StartAsync(int slotId, CancellationToken cancellationToken = default)
    {
        if (slotId >= 1000)
        {
            _logger.LogWarning("Slot {SlotId} start refused: {ErrorCode}", slotId, "cannot_start_adopted");
            return new LifecycleResult
            {
                ErrorCode = "cannot_start_adopted",
                ErrorMessage = $"Slot {slotId} is a non-standard adopted slot and cannot be started.",
                HttpStatus = 409
            };
        }

        if (_tracker.TryGet(slotId, out SlotProcess? existing) && ProcessHelper.IsAlive(existing!.Pid))
        {
            _logger.LogDebug("Slot {SlotId} already running (PID {Pid}) — idempotent return", slotId, existing.Pid);
            return new LifecycleResult();
        }

        string configName = slotId == 0
            ? _config.RemoteServer.TemplateConfigName
            : _config.Slots.ConfigNamePattern.Replace("{id}", slotId.ToString());
        string configPath = Path.Combine(_config.RemoteServer.ConfigDir, configName);

        if (!File.Exists(configPath))
        {
            _logger.LogWarning("Slot {SlotId} start refused: {ErrorCode}", slotId, "slot_not_found");
            return new LifecycleResult
            {
                ErrorCode = "slot_not_found",
                ErrorMessage = $"Slot {slotId} config not found: {configPath}",
                HttpStatus = 404
            };
        }

        Dictionary<string, string> configValues = InitializationFileReader.Read(configPath);
        if (!int.TryParse(configValues.GetValueOrDefault("port", ""), out int port))
        {
            port = SunshineDefaults.StreamingPort;
            _logger.LogInformation("Slot {SlotId} config has no 'port' value; using Sunshine default {Port}", slotId, port);
        }

        _logger.LogInformation("Starting slot {SlotId} (config {ConfigName}, port {Port})", slotId, configName, port);

        string exePath = Path.Combine(_config.RemoteServer.InstallDir, _config.RemoteServer.Executable);
        ProcessStartInfo psi = new()
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _config.RemoteServer.InstallDir
        };
        psi.ArgumentList.Add(configPath);

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null.");

        bool ready = await PollPortAsync(port, _config.RemoteServer.StartupTimeoutSeconds, cancellationToken);

        if (!ready)
        {
            _logger.LogWarning(
                "Slot {SlotId} startup timeout — Apollo did not bind port {Port} within {TimeoutSeconds}s; killed",
                slotId, port, _config.RemoteServer.StartupTimeoutSeconds);
            try { process.Kill(); } catch { }
            return new LifecycleResult
            {
                ErrorCode = "apollo_launch_failed",
                ErrorMessage = $"Slot {slotId} did not bind its port within {_config.RemoteServer.StartupTimeoutSeconds}s.",
                HttpStatus = 500
            };
        }

        _tracker.Add(slotId, new SlotProcess
        {
            Pid = process.Id,
            StartedAt = DateTimeOffset.UtcNow,
            ObservedPort = port,
            ConfigPath = configPath,
            ConfigName = configName
        });

        _logger.LogInformation("Slot {SlotId} started (PID {Pid})", slotId, process.Id);
        return new LifecycleResult();
    }

    public async Task<LifecycleResult> StopAsync(int slotId, CancellationToken cancellationToken = default)
    {
        if (!_tracker.TryGet(slotId, out SlotProcess? entry))
        {
            _logger.LogDebug("Slot {SlotId} stop skipped — not in tracker (idempotent)", slotId);
            return new LifecycleResult();
        }

        _tracker.Remove(slotId);

        using Process? process = TryGetProcess(entry!.Pid);
        if (process is null || process.HasExited)
        {
            return new LifecycleResult();
        }

        _logger.LogInformation("Stopping slot {SlotId} (PID {Pid})", slotId, entry.Pid);

        if (OperatingSystem.IsWindows())
        {
            process.CloseMainWindow();
        }
        // [DEFER-LINUX-SIGTERM] Linux graceful stop via SIGTERM requires P/Invoke.
        // Deferred to Phase 2. Process falls through to Kill() after timeout.

        using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(_config.Slots.StopTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // timeout — fall through to Kill
        }

        if (!process.HasExited)
        {
            _logger.LogWarning(
                "Slot {SlotId} (PID {Pid}) did not exit within {StopTimeoutSeconds}s — force killed",
                slotId, entry.Pid, _config.Slots.StopTimeoutSeconds);
            try { process.Kill(); } catch { }
        }
        else
        {
            _logger.LogInformation("Slot {SlotId} stopped gracefully", slotId);
        }

        return new LifecycleResult();
    }

    private static Process? TryGetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }

    private static async Task<bool> PollPortAsync(int port, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(timeoutSeconds));
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        CancellationToken token = linked.Token;

        while (true)
        {
            if (token.IsCancellationRequested) return false;

            try
            {
                using TcpClient client = new();
                await client.ConnectAsync("127.0.0.1", port, token);
                return true;
            }
            catch when (token.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                // connection refused — not ready yet
            }

            try
            {
                await Task.Delay(500, token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
