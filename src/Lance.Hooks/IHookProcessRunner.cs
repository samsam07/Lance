namespace Lance.Hooks;

// Where the hook engine actually launches a process. Abstracted so the engine's
// ordering / onError / timeout sequencing can be unit-tested without spawning real
// processes; ProcessHookRunner is the real implementation.
public interface IHookProcessRunner
{
    // Synchronous command (`async: false`): start it and wait for exit within the
    // timeout, returning the outcome so the engine can sequence the next command.
    Task<HookRunResult> RunAndWaitAsync(HookProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken);

    // Asynchronous command (`async: true`): start it and do not wait. Lance never
    // supervises the spawned process afterwards. Returns null on a successful launch,
    // or a description of why the process could not be started.
    string? Start(HookProcessSpec spec);
}

public sealed record HookProcessSpec
{
    public required string Command { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
    public required string WorkingDirectory { get; init; }
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
}

public sealed record HookRunResult
{
    public required bool TimedOut { get; init; }
    public int ExitCode { get; init; }

    // Non-null when the process could not be started at all (e.g. the executable does
    // not exist) — distinct from a process that ran and exited non-zero.
    public string? LaunchError { get; init; }

    public bool IsSuccess => LaunchError is null && !TimedOut && ExitCode == 0;
}
