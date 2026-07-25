using Lance.Hooks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lance.Hooks.Tests;

public sealed class HookLoaderTests
{
    [Fact]
    public void Load_ParsesActiveHookFile_WithDirectoryAndOrder()
    {
        using HooksTempDir dir = new();
        string path = Path.Combine(dir.Path, "vox.json");
        File.WriteAllText(path, """
            {
              "name": "vox",
              "events": {
                "session_started": { "priority": 500, "commands": [ { "command": "audiohelper", "args": ["launch"] } ] }
              }
            }
            """);

        HookLoader loader = new(NullLogger<HookLoader>.Instance);
        IReadOnlyList<LoadedHook> loaded = loader.Load([new HookFileRef { Path = path }]);

        Assert.Single(loaded);
        Assert.Equal("vox", loaded[0].File.Name);
        Assert.Equal(500, loaded[0].File.Events[LanceEvents.SessionStarted].Priority);
        Assert.Equal("audiohelper", loaded[0].File.Events[LanceEvents.SessionStarted].Commands[0].Command);
        Assert.Equal(Path.GetFullPath(dir.Path), Path.GetFullPath(loaded[0].Directory));
        Assert.Equal(0, loaded[0].LoadOrder);
    }

    [Fact]
    public void Load_SkipsInactiveReference()
    {
        using HooksTempDir dir = new();
        string path = Path.Combine(dir.Path, "vox.json");
        File.WriteAllText(path, """{ "events": {} }""");

        HookLoader loader = new(NullLogger<HookLoader>.Instance);
        IReadOnlyList<LoadedHook> loaded = loader.Load([new HookFileRef { Path = path, Active = false }]);

        Assert.Empty(loaded);
    }

    [Fact]
    public void Load_RelativePath_ResolvesAgainstBaseDirectory()
    {
        using HooksTempDir dir = new();
        File.WriteAllText(Path.Combine(dir.Path, "vox.json"), """{ "name": "vox", "events": {} }""");

        HookLoader loader = new(NullLogger<HookLoader>.Instance);
        // Relative path found via BaseDirectory, not the process's current directory.
        IReadOnlyList<LoadedHook> loaded = loader.Load([new HookFileRef { Path = "vox.json", BaseDirectory = dir.Path }]);

        Assert.Single(loaded);
        Assert.Equal("vox", loaded[0].File.Name);
        Assert.Equal(Path.GetFullPath(dir.Path), Path.GetFullPath(loaded[0].Directory));
    }

    [Fact]
    public void Load_SkipsUnreadableFile()
    {
        using HooksTempDir dir = new();
        string missing = Path.Combine(dir.Path, "nope.json");

        HookLoader loader = new(NullLogger<HookLoader>.Instance);
        IReadOnlyList<LoadedHook> loaded = loader.Load([new HookFileRef { Path = missing }]);

        Assert.Empty(loaded);
    }

    [Fact]
    public void Load_DefaultsMatchSpec()
    {
        using HooksTempDir dir = new();
        string path = Path.Combine(dir.Path, "min.json");
        File.WriteAllText(path, """
            { "events": { "session_ended": { "commands": [ { "command": "cleanup" } ] } } }
            """);

        HookLoader loader = new(NullLogger<HookLoader>.Instance);
        IReadOnlyList<LoadedHook> loaded = loader.Load([new HookFileRef { Path = path }]);

        HookEventDefinition definition = loaded[0].File.Events[LanceEvents.SessionEnded];
        HookCommand command = definition.Commands[0];
        Assert.Equal(1000, definition.Priority);
        Assert.Equal("terminate", command.OnError);
        Assert.Equal(30, command.TimeoutSeconds);
        Assert.False(command.Async);
        Assert.Empty(command.Args);
    }
}

file sealed class HooksTempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lance-hooks-test-{Guid.NewGuid():N}");

    public HooksTempDir()
    {
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
