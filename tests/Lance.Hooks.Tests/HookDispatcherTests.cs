using Lance.Hooks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lance.Hooks.Tests;

public sealed class HookDispatcherTests
{
    [Fact]
    public async Task Dispatch_RunsFilesByPriorityThenLoadOrder_CommandsInArrayOrder()
    {
        FakeHookProcessRunner runner = new();
        HookDispatcher dispatcher = new(runner, NullLogger<HookDispatcher>.Instance);

        LoadedHook later = Hook(priority: 2000, loadOrder: 0, Cmd("x", "a1"), Cmd("x", "a2"));
        LoadedHook earlier = Hook(priority: 1000, loadOrder: 1, Cmd("y", "b1"));

        await dispatcher.DispatchAsync(LanceEvents.SessionStarted, [later, earlier], Env(), TestContext.Current.CancellationToken);

        Assert.Equal(["y:b1", "x:a1", "x:a2"], runner.Ran);
    }

    [Fact]
    public async Task Dispatch_EqualPriority_BreaksTieByLoadOrder()
    {
        FakeHookProcessRunner runner = new();
        HookDispatcher dispatcher = new(runner, NullLogger<HookDispatcher>.Instance);

        LoadedHook second = Hook(priority: 1000, loadOrder: 1, Cmd("y", "b1"));
        LoadedHook first = Hook(priority: 1000, loadOrder: 0, Cmd("x", "a1"));

        await dispatcher.DispatchAsync(LanceEvents.SessionStarted, [second, first], Env(), TestContext.Current.CancellationToken);

        Assert.Equal(["x:a1", "y:b1"], runner.Ran);
    }

    [Fact]
    public async Task Dispatch_OnErrorTerminate_StopsRemainingCommandsInThatFile()
    {
        FakeHookProcessRunner runner = new(fail: ["x:a1"]);
        HookDispatcher dispatcher = new(runner, NullLogger<HookDispatcher>.Instance);

        LoadedHook file = Hook(1000, 0, Cmd("x", "a1"), Cmd("x", "a2"));

        await dispatcher.DispatchAsync(LanceEvents.SessionStarted, [file], Env(), TestContext.Current.CancellationToken);

        Assert.Equal(["x:a1"], runner.Ran);
    }

    [Fact]
    public async Task Dispatch_OnErrorContinue_RunsRemainingCommands()
    {
        FakeHookProcessRunner runner = new(fail: ["x:a1"]);
        HookDispatcher dispatcher = new(runner, NullLogger<HookDispatcher>.Instance);

        LoadedHook file = Hook(1000, 0, Cmd("x", "a1", onError: "continue"), Cmd("x", "a2"));

        await dispatcher.DispatchAsync(LanceEvents.SessionStarted, [file], Env(), TestContext.Current.CancellationToken);

        Assert.Equal(["x:a1", "x:a2"], runner.Ran);
    }

    [Fact]
    public async Task Dispatch_TerminateInOneFile_DoesNotStopOtherFiles()
    {
        FakeHookProcessRunner runner = new(fail: ["x:a1"]);
        HookDispatcher dispatcher = new(runner, NullLogger<HookDispatcher>.Instance);

        LoadedHook failing = Hook(1000, 0, Cmd("x", "a1"), Cmd("x", "a2"));
        LoadedHook other = Hook(1000, 1, Cmd("y", "b1"));

        await dispatcher.DispatchAsync(LanceEvents.SessionStarted, [failing, other], Env(), TestContext.Current.CancellationToken);

        Assert.Equal(["x:a1", "y:b1"], runner.Ran);
    }

    [Fact]
    public async Task Dispatch_AsyncCommand_IsStartedNotWaited_AndChainContinues()
    {
        FakeHookProcessRunner runner = new();
        HookDispatcher dispatcher = new(runner, NullLogger<HookDispatcher>.Instance);

        LoadedHook file = Hook(1000, 0, Cmd("x", "a1", async: true), Cmd("x", "a2"));

        await dispatcher.DispatchAsync(LanceEvents.SessionStarted, [file], Env(), TestContext.Current.CancellationToken);

        Assert.Equal(["x:a1"], runner.Started);
        Assert.Equal(["x:a2"], runner.Ran);
    }

    [Fact]
    public async Task Dispatch_Timeout_IsTreatedAsFailure_AndTerminates()
    {
        FakeHookProcessRunner runner = new(timeout: ["x:a1"]);
        HookDispatcher dispatcher = new(runner, NullLogger<HookDispatcher>.Instance);

        LoadedHook file = Hook(1000, 0, Cmd("x", "a1"), Cmd("x", "a2"));

        await dispatcher.DispatchAsync(LanceEvents.SessionStarted, [file], Env(), TestContext.Current.CancellationToken);

        Assert.Equal(["x:a1"], runner.Ran);
    }

    [Fact]
    public async Task Dispatch_LaunchFailure_IsTreatedAsFailure_AndTerminates()
    {
        FakeHookProcessRunner runner = new(launchFail: ["x:a1"]);
        HookDispatcher dispatcher = new(runner, NullLogger<HookDispatcher>.Instance);

        LoadedHook file = Hook(1000, 0, Cmd("x", "a1"), Cmd("x", "a2"));

        await dispatcher.DispatchAsync(LanceEvents.SessionStarted, [file], Env(), TestContext.Current.CancellationToken);

        // A command that never launched is a failure: with onError=terminate the chain stops.
        Assert.Equal(["x:a1"], runner.Ran);
    }

    [Fact]
    public void Resolve_SubstitutesArgs_AndResolvesWorkingDir()
    {
        HookDispatcher dispatcher = new(new FakeHookProcessRunner(), NullLogger<HookDispatcher>.Instance);

        HookCommand command = new() { Command = "audiohelper", Args = ["launch", "--peer", "${LANCE_AGENT_IP}"] };
        LoadedHook file = new()
        {
            File = new HookFile { Events = new() { [LanceEvents.SessionEnded] = new HookEventDefinition { Commands = [command] } } },
            Directory = "/work",
            LoadOrder = 0
        };

        Dictionary<string, string> env = new() { [LanceEnv.AgentIp] = "10.0.0.5" };
        IReadOnlyList<ResolvedCommand> resolved = dispatcher.Resolve(LanceEvents.SessionEnded, [file], env);

        Assert.Single(resolved);
        Assert.Equal(["launch", "--peer", "10.0.0.5"], resolved[0].Args);
        Assert.Equal("/work", resolved[0].WorkingDir);
    }

    private static LoadedHook Hook(int priority, int loadOrder, params HookCommand[] commands)
    {
        return new LoadedHook
        {
            File = new HookFile
            {
                Events = new() { [LanceEvents.SessionStarted] = new HookEventDefinition { Priority = priority, Commands = commands } }
            },
            Directory = "/work",
            LoadOrder = loadOrder
        };
    }

    private static HookCommand Cmd(string command, string firstArg, bool async = false, string onError = "terminate")
    {
        return new HookCommand { Command = command, Args = [firstArg], Async = async, OnError = onError };
    }

    private static IReadOnlyDictionary<string, string> Env()
    {
        return new Dictionary<string, string>();
    }
}

file sealed class FakeHookProcessRunner : IHookProcessRunner
{
    private readonly HashSet<string> _fail;
    private readonly HashSet<string> _timeout;
    private readonly HashSet<string> _launchFail;

    public List<string> Ran { get; } = [];
    public List<string> Started { get; } = [];

    public FakeHookProcessRunner(IEnumerable<string>? fail = null, IEnumerable<string>? timeout = null, IEnumerable<string>? launchFail = null)
    {
        _fail = new HashSet<string>(fail ?? []);
        _timeout = new HashSet<string>(timeout ?? []);
        _launchFail = new HashSet<string>(launchFail ?? []);
    }

    public Task<HookRunResult> RunAndWaitAsync(HookProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string key = Key(spec);
        Ran.Add(key);

        if (_launchFail.Contains(key))
        {
            return Task.FromResult(new HookRunResult { TimedOut = false, LaunchError = "executable not found" });
        }

        if (_timeout.Contains(key))
        {
            return Task.FromResult(new HookRunResult { TimedOut = true });
        }

        int exitCode = _fail.Contains(key) ? 1 : 0;
        return Task.FromResult(new HookRunResult { TimedOut = false, ExitCode = exitCode });
    }

    public string? Start(HookProcessSpec spec)
    {
        Started.Add(Key(spec));
        return _launchFail.Contains(Key(spec)) ? "executable not found" : null;
    }

    private static string Key(HookProcessSpec spec)
    {
        return spec.Args.Count > 0 ? $"{spec.Command}:{spec.Args[0]}" : spec.Command;
    }
}
