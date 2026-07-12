namespace Lance.Hooks;

// The four events raised locally on each side (see ARCHITECTURE "Sessions & tool
// orchestration"). Used both as hook `events` keys and as the LANCE_EVENT value.
public static class LanceEvents
{
    public const string SessionStarted = "session_started";
    public const string SessionEnded = "session_ended";
    public const string SlotConnected = "slot_connected";
    public const string SlotDisconnected = "slot_disconnected";
}
