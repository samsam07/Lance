namespace Lance.Agent.Services;

internal sealed record LifecycleResult
{
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int HttpStatus { get; init; } = 200;
    public bool IsSuccess => ErrorCode is null;
}

internal interface ISlotLifecycle
{
    Task<LifecycleResult> StartAsync(int slotId, CancellationToken cancellationToken = default);
    Task<LifecycleResult> StopAsync(int slotId, CancellationToken cancellationToken = default);
}
