namespace Lance.Client;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Generic = 1;
    public const int SessionActive = 2;
    public const int AgentUnreachable = 3;
    public const int AgentError = 4;
    public const int MoonlightFailed = 5;
    public const int SlotNotReady = 6;
    public const int ConfigResolutionFailed = 7;
}
