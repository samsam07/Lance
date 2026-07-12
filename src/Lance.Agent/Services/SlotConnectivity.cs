namespace Lance.Agent.Services;

// Shared connected-state rule: a slot has a connected client iff its Apollo process
// owns one of its streaming UDP ports (see IStreamingPortMap, [VALIDATE-UDP]). Used by
// both the query-time slot scan and the background session-detection loop, off one UDP
// snapshot.
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
}
