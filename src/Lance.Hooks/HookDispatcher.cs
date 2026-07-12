using Microsoft.Extensions.Logging;

namespace Lance.Hooks;

// The hook engine. For a raised event it runs every bound command in order — files by
// priority then load order, commands within a file in array order — honoring the
// async / onError / timeout semantics from SPEC. It never supervises spawned processes
// beyond optionally waiting for a synchronous command's exit to sequence the next.
public sealed class HookDispatcher
{
    private const string OnErrorContinue = "continue";

    private readonly IHookProcessRunner _runner;
    private readonly ILogger<HookDispatcher> _logger;

    public HookDispatcher(IHookProcessRunner runner, ILogger<HookDispatcher> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task DispatchAsync(string eventName, IReadOnlyList<LoadedHook> hooks, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken = default)
    {
        foreach (BoundEvent bound in Bind(eventName, hooks))
        {
            // Each file's commands are their own chain — a terminate in one file does
            // not stop other files (independent tools).
            IReadOnlyList<ResolvedCommand> commands = ResolveFile(bound, env);
            await RunSequenceAsync(commands, env, cancellationToken);
        }
    }

    public IReadOnlyList<ResolvedCommand> Resolve(string eventName, IReadOnlyList<LoadedHook> hooks, IReadOnlyDictionary<string, string> env)
    {
        List<ResolvedCommand> resolved = [];
        foreach (BoundEvent bound in Bind(eventName, hooks))
        {
            resolved.AddRange(ResolveFile(bound, env));
        }

        return resolved;
    }

    // Run a pre-resolved command sequence — a persisted teardown snapshot at normal
    // session end or crash-recovery replay. Same per-command semantics as a file chain.
    public async Task RunResolvedAsync(IReadOnlyList<ResolvedCommand> commands, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken = default)
    {
        await RunSequenceAsync(commands, env, cancellationToken);
    }

    private async Task RunSequenceAsync(IReadOnlyList<ResolvedCommand> commands, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        foreach (ResolvedCommand command in commands)
        {
            HookProcessSpec spec = new()
            {
                Command = command.Command,
                Args = command.Args,
                WorkingDirectory = command.WorkingDir ?? ".",
                Environment = env
            };

            if (command.Async)
            {
                _runner.Start(spec);
                continue;
            }

            HookRunResult result = await _runner.RunAndWaitAsync(spec, TimeSpan.FromSeconds(command.TimeoutSeconds), cancellationToken);
            if (result.IsSuccess)
            {
                continue;
            }

            LogFailure(command, result);
            if (!string.Equals(command.OnError, OnErrorContinue, StringComparison.Ordinal))
            {
                return;   // onError=terminate: stop this sequence
            }
        }
    }

    private void LogFailure(ResolvedCommand command, HookRunResult result)
    {
        if (result.TimedOut)
        {
            _logger.LogWarning("Hook command '{Command}' did not finish within {Timeout}s; moving on.", command.Command, command.TimeoutSeconds);
        }
        else
        {
            _logger.LogWarning("Hook command '{Command}' exited with code {ExitCode}.", command.Command, result.ExitCode);
        }
    }

    private static IReadOnlyList<ResolvedCommand> ResolveFile(BoundEvent bound, IReadOnlyDictionary<string, string> env)
    {
        List<ResolvedCommand> resolved = [];
        foreach (HookCommand command in bound.Definition.Commands)
        {
            resolved.Add(ResolveCommand(command, bound.Hook, env));
        }

        return resolved;
    }

    private static ResolvedCommand ResolveCommand(HookCommand command, LoadedHook hook, IReadOnlyDictionary<string, string> env)
    {
        string[] args = new string[command.Args.Length];
        for (int i = 0; i < command.Args.Length; i++)
        {
            args[i] = VariableSubstitutor.Substitute(command.Args[i], env);
        }

        return new ResolvedCommand
        {
            Command = command.Command,
            Args = args,
            WorkingDir = command.WorkingDir ?? hook.Directory,
            Async = command.Async,
            OnError = command.OnError,
            TimeoutSeconds = command.TimeoutSeconds
        };
    }

    private static IReadOnlyList<BoundEvent> Bind(string eventName, IReadOnlyList<LoadedHook> hooks)
    {
        List<BoundEvent> bound = [];
        foreach (LoadedHook hook in hooks)
        {
            if (hook.File.Events.TryGetValue(eventName, out HookEventDefinition? definition))
            {
                bound.Add(new BoundEvent(hook, definition));
            }
        }

        bound.Sort(static (a, b) =>
        {
            int byPriority = a.Definition.Priority.CompareTo(b.Definition.Priority);
            return byPriority != 0 ? byPriority : a.Hook.LoadOrder.CompareTo(b.Hook.LoadOrder);
        });

        return bound;
    }

    private readonly record struct BoundEvent(LoadedHook Hook, HookEventDefinition Definition);
}
