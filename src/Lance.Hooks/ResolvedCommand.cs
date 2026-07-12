namespace Lance.Hooks;

// A hook command with its ${VAR}s substituted and working directory resolved, ready
// to run or to persist for crash-recovery replay (SPEC "Session record"). Snapshotting
// the resolved form means replay never re-reads the hook file (which may have changed)
// or recomputes env that is gone once the client is.
public sealed record ResolvedCommand
{
    public required string Command { get; init; }
    public string[] Args { get; init; } = [];
    public string? WorkingDir { get; init; }
    public bool Async { get; init; }
    public string OnError { get; init; } = "terminate";
    public int TimeoutSeconds { get; init; } = 30;
}
