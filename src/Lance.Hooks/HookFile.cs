namespace Lance.Hooks;

// A parsed hook file (SPEC "Hook file format"). One file may bind several events;
// `name` is descriptive only.
public sealed record HookFile
{
    public string? Name { get; init; }
    public Dictionary<string, HookEventDefinition> Events { get; init; } = new();
}

public sealed record HookEventDefinition
{
    public int Priority { get; init; } = 1000;
    public HookCommand[] Commands { get; init; } = [];
}

public sealed record HookCommand
{
    public required string Command { get; init; }
    public string[] Args { get; init; } = [];
    public bool Async { get; init; }
    public string OnError { get; init; } = "terminate";
    public int TimeoutSeconds { get; init; } = 30;
    public string? WorkingDir { get; init; }
}
