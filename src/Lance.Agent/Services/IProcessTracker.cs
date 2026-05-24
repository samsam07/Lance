using System.Collections.Concurrent;

namespace Lance.Agent.Services;

internal sealed record SlotProcess
{
    public required int Pid { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public int ObservedPort { get; init; }
    public string ConfigPath { get; init; } = string.Empty;
    public string ConfigName { get; init; } = string.Empty;
}

internal interface IProcessTracker
{
    void Add(int slotId, SlotProcess entry);
    void Remove(int slotId);
    bool TryGet(int slotId, out SlotProcess? entry);
    IReadOnlyList<(int SlotId, SlotProcess Entry)> GetAll();
}

internal sealed class ProcessTracker : IProcessTracker
{
    private readonly ConcurrentDictionary<int, SlotProcess> _table = new();

    public void Add(int slotId, SlotProcess entry)
    {
        _table[slotId] = entry;
    }

    public void Remove(int slotId)
    {
        _table.TryRemove(slotId, out _);
    }

    public bool TryGet(int slotId, out SlotProcess? entry)
    {
        return _table.TryGetValue(slotId, out entry);
    }

    public IReadOnlyList<(int SlotId, SlotProcess Entry)> GetAll()
    {
        List<(int, SlotProcess)> result = [];
        foreach (KeyValuePair<int, SlotProcess> kvp in _table)
        {
            result.Add((kvp.Key, kvp.Value));
        }
        return result;
    }
}
