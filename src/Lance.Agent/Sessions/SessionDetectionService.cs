using Lance.Agent.Configuration;
using Lance.Agent.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lance.Agent.Sessions;

// Drives session lifecycle from UDP endpoint presence (see [VALIDATE-UDP]). Each tick
// it derives which slots have a connected client and advances each session:
//   Provisioned + a slot connected            -> Connected
//   Provisioned + grace window elapsed, idle   -> session_ended(provision_timeout)
//   Connected  + all slots idle                -> session_ended(probe_watch)
// This is the agent-side detector that works even when the client machine is gone.
// Windows-only for now ([VERIFY-APOLLO] Linux).
internal sealed class SessionDetectionService : BackgroundService
{
    private readonly ISlotScanner _scanner;
    private readonly IUdpEndpointProbe _probe;
    private readonly IStreamingPortMap _portMap;
    private readonly ISessionRegistry _registry;
    private readonly ISessionOrchestrator _orchestrator;
    private readonly ILogger<SessionDetectionService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _provisionGrace;

    public SessionDetectionService(
        AgentConfig config,
        ISlotScanner scanner,
        IUdpEndpointProbe probe,
        IStreamingPortMap portMap,
        ISessionRegistry registry,
        ISessionOrchestrator orchestrator,
        ILogger<SessionDetectionService> logger)
    {
        _scanner = scanner;
        _probe = probe;
        _portMap = portMap;
        _registry = registry;
        _orchestrator = orchestrator;
        _logger = logger;
        int pollSeconds = config.Sessions.ProbePollSeconds > 0 ? config.Sessions.ProbePollSeconds : 1;
        int graceSeconds = config.Sessions.ProvisionGraceSeconds > 0 ? config.Sessions.ProvisionGraceSeconds : 30;
        _pollInterval = TimeSpan.FromSeconds(pollSeconds);
        _provisionGrace = TimeSpan.FromSeconds(graceSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("Detecting client connections is only supported on Windows; sessions will not end automatically on this computer.");
            return;
        }

        _logger.LogInformation("Watching sessions for client connections (checking every {Interval} second(s)).", _pollInterval.TotalSeconds);

        using PeriodicTimer timer = new(_pollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not check sessions for client connections this time; will try again.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Session> sessions = _registry.GetAll();
        if (sessions.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<int, bool> slotConnected = SlotConnectivity.Snapshot(_scanner, _probe, _portMap);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (Session session in sessions)
        {
            await AdvanceAsync(session, slotConnected, now, cancellationToken);
        }
    }

    private async Task AdvanceAsync(Session session, IReadOnlyDictionary<int, bool> slotConnected, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (session.State == SessionState.Ended)
        {
            return;
        }

        bool anyConnected = false;
        foreach (int slotId in session.SlotIds)
        {
            if (slotConnected.TryGetValue(slotId, out bool connected) && connected)
            {
                anyConnected = true;
                break;
            }
        }

        if (session.State == SessionState.Provisioned)
        {
            if (anyConnected)
            {
                if (_registry.TryMarkConnected(session.Id, now))
                {
                    _logger.LogInformation("Session {SessionId}: a client connected.", session.Id);
                }
            }
            else if (now - session.CreatedAt >= _provisionGrace)
            {
                _logger.LogInformation("Session {SessionId}: no client connected within {Grace}s; ending it.", session.Id, _provisionGrace.TotalSeconds);
                await _orchestrator.EndSessionAsync(session.Id, "provision_timeout", cancellationToken: cancellationToken);
            }
        }
        else if (!anyConnected)
        {
            _logger.LogInformation("Session {SessionId}: the client disconnected; ending it.", session.Id);
            await _orchestrator.EndSessionAsync(session.Id, "probe_watch", cancellationToken: cancellationToken);
        }
    }
}
