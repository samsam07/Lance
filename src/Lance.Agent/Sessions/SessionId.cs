namespace Lance.Agent.Sessions;

// Session ids become file names (the crash-recovery record) and are client-supplied,
// so they are constrained to a safe character set — this also blocks path traversal
// via a hostile --session-id. Both the connect handshake and the record store vet ids
// through here.
internal static class SessionId
{
    public const int MaxLength = 64;

    public static bool IsValid(string sessionId)
    {
        if (sessionId.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (char c in sessionId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
