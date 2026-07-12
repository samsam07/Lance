namespace Lance.Hooks;

// Environment-variable names injected into every spawned hook process; the same set
// backs ${VAR} substitution in hook args (see SPEC "Event payload").
public static class LanceEnv
{
    public const string Event = "LANCE_EVENT";
    public const string EventSource = "LANCE_EVENT_SOURCE";
    public const string SessionId = "LANCE_SESSION_ID";
    public const string Side = "LANCE_SIDE";
    public const string AgentIp = "LANCE_AGENT_IP";
    public const string ClientIp = "LANCE_CLIENT_IP";
    public const string SlotIds = "LANCE_SLOT_IDS";
    public const string SlotId = "LANCE_SLOT_ID";
}
