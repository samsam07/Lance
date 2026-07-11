namespace Lance.Agent.Services;

// Host-adapter seam ([VALIDATE-UDP], SPEC §5 port resolution). Maps a slot's base
// streaming port to the UDP endpoints the host (Apollo) binds while a client is
// connected. This is the one host-specific table; swap the implementation to target
// Sunshine or another host without touching the probe logic. Ports explicitly
// present in a slot's cloned config would win over the derived values — Apollo
// exposes no such per-stream config keys today, so the base+offset map is
// authoritative; the override path is left as a documented extension point.
internal interface IStreamingPortMap
{
    StreamingUdpPorts Resolve(int basePort);
}

internal sealed record StreamingUdpPorts
{
    public required int Video { get; init; }
    public required int Control { get; init; }
    public required int Audio { get; init; }

    // The Apollo process owning any of these means a client stream is live.
    public IReadOnlyList<int> Ports => [Video, Control, Audio];

    // The subset of streaming ports currently held by the Apollo process; a
    // non-empty result means a client is connected.
    public IReadOnlyList<int> ActivePorts(IReadOnlySet<int> heldPorts)
    {
        List<int> active = [];
        foreach (int port in Ports)
        {
            if (heldPorts.Contains(port))
            {
                active.Add(port);
            }
        }

        return active;
    }
}

internal sealed class ApolloStreamingPortMap : IStreamingPortMap
{
    // Apollo/Sunshine fixed UDP offsets from the base streaming port (the `port`
    // config value; base+1 is the web UI per SPEC). Documented defaults, to be
    // confirmed empirically by [VALIDATE-UDP] — the probe logs every UDP port the
    // Apollo process actually owns, so a wrong offset here surfaces in the logs.
    private const int VideoOffset = 9;
    private const int ControlOffset = 10;
    private const int AudioOffset = 11;

    public StreamingUdpPorts Resolve(int basePort)
    {
        return new StreamingUdpPorts
        {
            Video = basePort + VideoOffset,
            Control = basePort + ControlOffset,
            Audio = basePort + AudioOffset
        };
    }
}
