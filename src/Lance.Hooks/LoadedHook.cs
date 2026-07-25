namespace Lance.Hooks;

// A hook file to load: its path and whether it is active. Each side assembles these
// from its own sources (client `--hook` flags + config `hooks`; agent config `hooks`).
public sealed record HookFileRef
{
    public required string Path { get; init; }
    public bool Active { get; init; } = true;

    // Directory a relative Path is resolved against — the config file's own directory,
    // so `hooks/foo.json` in a config is found beside that config regardless of the
    // process's current directory. Null → Path is used as-is (already absolute, or a
    // CLI `--hook` arg, which stays relative to the current directory).
    public string? BaseDirectory { get; init; }
}

// A successfully loaded hook file plus where it came from: the containing directory
// (the default working directory for its commands) and its load order (the tie-breaker
// when two files bind the same event at equal priority).
public sealed record LoadedHook
{
    public required HookFile File { get; init; }
    public required string Directory { get; init; }
    public required int LoadOrder { get; init; }
}
