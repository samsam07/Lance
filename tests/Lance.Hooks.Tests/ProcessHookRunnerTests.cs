using Lance.Hooks;
using Xunit;

namespace Lance.Hooks.Tests;

public sealed class ProcessHookRunnerTests
{
    [Fact]
    public async Task RunAndWait_MissingExecutable_ReportsLaunchError_DoesNotThrow()
    {
        ProcessHookRunner runner = new();
        HookProcessSpec spec = Spec("lance-nonexistent-tool-a1b2c3");

        HookRunResult result = await runner.RunAndWaitAsync(spec, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.LaunchError);
    }

    [Fact]
    public void Start_MissingExecutable_ReturnsLaunchError_DoesNotThrow()
    {
        ProcessHookRunner runner = new();

        string? launchError = runner.Start(Spec("lance-nonexistent-tool-a1b2c3"));

        Assert.NotNull(launchError);
    }

    private static HookProcessSpec Spec(string command)
    {
        return new HookProcessSpec
        {
            Command = command,
            Args = [],
            WorkingDirectory = ".",
            Environment = new Dictionary<string, string>()
        };
    }
}
