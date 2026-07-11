namespace Lance.Agent.Sessions;

// Agent-side lifecycle of one session (one `lance connect`). See ARCHITECTURE
// "Sessions & tool orchestration".
internal enum SessionState
{
    Provisioned,   // slots allocated, no stream yet
    Connected,     // at least one slot's stream is live
    Ended          // teardown ran, record deleted
}
