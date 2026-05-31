using Lance.Agent.Configuration;
using Lance.Agent.Services;
using Lance.Shared.Dtos;
using Xunit;

namespace Lance.Agent.Tests;

public sealed class SlotScannerTests
{
    [Fact]
    public void Scan_SlotWithNoProcess_StatusIsAllocated()
    {
        using TempDir dir = new();
        File.WriteAllText(Path.Combine(dir.Path, "sunshine.conf"), "port = 47989\n");

        SlotScanner scanner = BuildScanner(dir.Path, new ProcessTracker(), new FakeTcpProbe());

        IReadOnlyList<SlotDto> slots = scanner.Scan();

        Assert.Single(slots);
        Assert.Equal("Allocated", slots[0].Status);
    }

    [Fact]
    public void Scan_RunningSlotNoConnection_StatusIsRunning()
    {
        using TempDir dir = new();
        File.WriteAllText(Path.Combine(dir.Path, "sunshine.conf"), "port = 47989\n");

        ProcessTracker tracker = new();
        tracker.Add(0, new SlotProcess { Pid = Environment.ProcessId, StartedAt = DateTimeOffset.UtcNow });

        SlotScanner scanner = BuildScanner(dir.Path, tracker, new FakeTcpProbe());

        IReadOnlyList<SlotDto> slots = scanner.Scan();

        Assert.Single(slots);
        Assert.Equal("Running", slots[0].Status);
    }

    [Fact]
    public void Scan_RunningSlotWithConnection_StatusIsConnected()
    {
        using TempDir dir = new();
        const int port = 47989;
        File.WriteAllText(Path.Combine(dir.Path, "sunshine.conf"), $"port = {port}\n");

        ProcessTracker tracker = new();
        tracker.Add(0, new SlotProcess { Pid = Environment.ProcessId, StartedAt = DateTimeOffset.UtcNow });

        SlotScanner scanner = BuildScanner(dir.Path, tracker, new FakeTcpProbe(port));

        IReadOnlyList<SlotDto> slots = scanner.Scan();

        Assert.Single(slots);
        Assert.Equal("Connected", slots[0].Status);
    }

    [Fact]
    public void Scan_AdoptedSlotNoConnection_StatusIsRunning()
    {
        using TempDir dir = new();
        File.WriteAllText(Path.Combine(dir.Path, "sunshine.conf"), "port = 47989\n");

        ProcessTracker tracker = new();
        tracker.Add(1000, new SlotProcess
        {
            Pid = Environment.ProcessId,
            StartedAt = DateTimeOffset.UtcNow,
            ObservedPort = 46000,
            ConfigName = "other.conf",
            ConfigPath = "/some/other.conf"
        });

        SlotScanner scanner = BuildScanner(dir.Path, tracker, new FakeTcpProbe());

        IReadOnlyList<SlotDto> slots = scanner.Scan();

        SlotDto? adopted = null;
        foreach (SlotDto s in slots)
        {
            if (s.Id == 1000) { adopted = s; break; }
        }
        Assert.NotNull(adopted);
        Assert.Equal("Running", adopted.Status);
    }

    [Fact]
    public void Scan_AdoptedSlotWithConnection_StatusIsConnected()
    {
        using TempDir dir = new();
        File.WriteAllText(Path.Combine(dir.Path, "sunshine.conf"), "port = 47989\n");

        const int adoptedPort = 46000;
        ProcessTracker tracker = new();
        tracker.Add(1000, new SlotProcess
        {
            Pid = Environment.ProcessId,
            StartedAt = DateTimeOffset.UtcNow,
            ObservedPort = adoptedPort,
            ConfigName = "other.conf",
            ConfigPath = "/some/other.conf"
        });

        SlotScanner scanner = BuildScanner(dir.Path, tracker, new FakeTcpProbe(adoptedPort));

        IReadOnlyList<SlotDto> slots = scanner.Scan();

        SlotDto? adopted = null;
        foreach (SlotDto s in slots)
        {
            if (s.Id == 1000) { adopted = s; break; }
        }
        Assert.NotNull(adopted);
        Assert.Equal("Connected", adopted.Status);
    }

    private static SlotScanner BuildScanner(string configDir, ProcessTracker tracker, ITcpProbe probe)
    {
        AgentConfig config = new()
        {
            RemoteServer = new RemoteServerConfig
            {
                ConfigDir = configDir,
                InstallDir = configDir
            }
        };
        return new SlotScanner(config, tracker, probe);
    }
}

file sealed class FakeTcpProbe : ITcpProbe
{
    private readonly HashSet<int> _connectedPorts;

    public FakeTcpProbe(params int[] connectedPorts)
    {
        _connectedPorts = new HashSet<int>(connectedPorts);
    }

    public bool HasEstablishedConnection(int port)
    {
        return _connectedPorts.Contains(port);
    }
}

file sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lance-test-{Guid.NewGuid():N}");

    public TempDir()
    {
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
