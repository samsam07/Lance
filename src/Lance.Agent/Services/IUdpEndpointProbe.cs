using Lance.Agent.Infrastructure;

namespace Lance.Agent.Services;

// Host-agnostic UDP liveness probe. Reads the OS UDP endpoint table and groups the
// bound local ports by owning PID, so the caller can scope by both PID and port
// (the [VALIDATE-UDP] rule: a slot is connected iff its Apollo process owns UDP
// endpoints at that slot's streaming ports).
internal interface IUdpEndpointProbe
{
    IReadOnlyDictionary<int, IReadOnlySet<int>> SnapshotByPid();
}

internal sealed class UdpEndpointProbe : IUdpEndpointProbe
{
    public IReadOnlyDictionary<int, IReadOnlySet<int>> SnapshotByPid()
    {
        Dictionary<int, HashSet<int>> byPid = [];
        foreach ((int pid, int port) in NativeMethods.GetUdpEndpoints())
        {
            if (!byPid.TryGetValue(pid, out HashSet<int>? ports))
            {
                ports = [];
                byPid[pid] = ports;
            }
            ports.Add(port);
        }

        Dictionary<int, IReadOnlySet<int>> result = new(byPid.Count);
        foreach (KeyValuePair<int, HashSet<int>> pair in byPid)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }
}
