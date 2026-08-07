using Lance.Client.Infrastructure;

namespace Lance.Client.Commands;

// One resolved entry from --monitors: the id the user asked for, and the local
// monitor it maps to. Monitor is null when that id is not present on this machine —
// the id is still carried so per-monitor options can be reported against it.
internal sealed record TargetMonitor
{
    public int Id { get; init; }
    public MonitorInfo? Monitor { get; init; }
}
