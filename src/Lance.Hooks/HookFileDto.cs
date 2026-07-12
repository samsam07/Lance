namespace Lance.Hooks;

// The raw JSON shape of a hook file. Every field is optional/nullable so an omitted
// property is distinguishable from an explicit value. HookLoader maps this to the
// clean HookFile domain model, applying the SPEC defaults.
//
// Why a separate type: System.Text.Json source generation does NOT apply a C#
// property-initializer default to a property that is absent from the JSON (it leaves
// the CLR default — 0 / null / false). Binding to nullable fields here lets the mapper
// tell "absent" from "explicitly set" and fill defaults deliberately.
internal sealed record HookFileDto
{
    public string? Name { get; init; }
    public Dictionary<string, HookEventDto>? Events { get; init; }
}

internal sealed record HookEventDto
{
    public int? Priority { get; init; }
    public HookCommandDto[]? Commands { get; init; }
}

internal sealed record HookCommandDto
{
    public string? Command { get; init; }
    public string[]? Args { get; init; }
    public bool? Async { get; init; }
    public string? OnError { get; init; }
    public int? TimeoutSeconds { get; init; }
    public string? WorkingDir { get; init; }
}
