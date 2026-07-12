using Lance.Shared.Dtos;

namespace Lance.Agent.Services;

// Shared connected-state rule: a slot has a connected client iff its Apollo process
// owns one of its streaming UDP ports (see IStreamingPortMap, [VALIDATE-UDP]). Used by
// the query-time slot scan, the session-detection loop, and startup reconciliation.
internal static class SlotConnectivity
{
    public static bool IsConnected(int processId, int basePort, IReadOnlyDictionary<int, IReadOnlySet<int>> udpByProcess, IStreamingPortMap portMap)
    {
        if (!udpByProcess.TryGetValue(processId, out IReadOnlySet<int>? heldPorts))
        {
            return false;
        }

        return portMap.Resolve(basePort).ActivePorts(heldPorts).Count > 0;
    }

    // One UDP snapshot → each running slot's connected state, keyed by slot id.
    public static IReadOnlyDictionary<int, bool> Snapshot(ISlotScanner scanner, IUdpEndpointProbe probe, IStreamingPortMap portMap)
    {
        IReadOnlyDictionary<int, IReadOnlySet<int>> udpByProcess = probe.SnapshotByPid();
        Dictionary<int, bool> connected = [];
        foreach (SlotDto slot in scanner.Scan())
        {
            if (slot.ProcessId is int processId)
            {
                connected[slot.Id] = IsConnected(processId, slot.Port, udpByProcess, portMap);
            }
        }

        return connected;
    }
}
