namespace Lance.Client.Http;

internal sealed record AgentResult<T>
{
    public bool IsSuccess { get; init; }
    public bool IsUnreachable { get; init; }
    public T? Value { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public int ExitCode
    {
        get
        {
            if (IsUnreachable) return ExitCodes.AgentUnreachable;
            if (!IsSuccess) return ExitCodes.AgentError;
            return ExitCodes.Success;
        }
    }
}
