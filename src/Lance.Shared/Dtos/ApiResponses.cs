namespace Lance.Shared.Dtos;

public sealed record SlotsResponse
{
    public required SlotDto[] Slots { get; init; }
}

public sealed record HealthResponse
{
    public required string Status { get; init; }
    public required string Version { get; init; }
    public required long UptimeSeconds { get; init; }
    public required int MaxSlots { get; init; }
    public required string TemplatePath { get; init; }
    public required bool TemplateExists { get; init; }
}

public sealed record ErrorResponse
{
    public required string Error { get; init; }
    public required string Message { get; init; }
}

public sealed record ConfigUrlResponse
{
    public required string Url { get; init; }
}
