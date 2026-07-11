using Lance.Agent.Configuration;
using Lance.Agent.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lance.Agent.Tests;

public sealed class SessionRecordStoreTests
{
    [Fact]
    public async Task SaveThenLoadAll_RoundTripsTheRecord()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using SessionTempDir dir = new();
        FileSessionRecordStore store = BuildStore(dir.Path);

        SessionRecord record = new()
        {
            SessionId = "session-1",
            ClientIp = "192.168.1.10",
            SlotIds = [1, 3],
            TeardownCommands =
            [
                new ResolvedCommand { Command = "audiohelper", Args = ["restore", "audio-config"] }
            ],
            Env = new Dictionary<string, string> { ["LANCE_SESSION_ID"] = "session-1" },
            CreatedAt = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(record, ct);
        IReadOnlyList<SessionRecord> loaded = await store.LoadAllAsync(ct);

        Assert.Single(loaded);
        Assert.Equal("session-1", loaded[0].SessionId);
        Assert.Equal([1, 3], loaded[0].SlotIds);
        Assert.Equal("audiohelper", loaded[0].TeardownCommands[0].Command);
        Assert.Equal("session-1", loaded[0].Env["LANCE_SESSION_ID"]);
    }

    [Fact]
    public async Task Save_Twice_OverwritesAtomicallyLeavingOneRecord()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using SessionTempDir dir = new();
        FileSessionRecordStore store = BuildStore(dir.Path);

        await store.SaveAsync(NewRecord("session-1", "10.0.0.1"), ct);
        await store.SaveAsync(NewRecord("session-1", "10.0.0.2"), ct);

        IReadOnlyList<SessionRecord> loaded = await store.LoadAllAsync(ct);

        Assert.Single(loaded);
        Assert.Equal("10.0.0.2", loaded[0].ClientIp);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public async Task Delete_RemovesTheRecord()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using SessionTempDir dir = new();
        FileSessionRecordStore store = BuildStore(dir.Path);
        await store.SaveAsync(NewRecord("session-1", "10.0.0.1"), ct);

        await store.DeleteAsync("session-1", ct);

        Assert.Empty(await store.LoadAllAsync(ct));
    }

    [Fact]
    public async Task LoadAll_SkipsAnUnreadableRecord()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using SessionTempDir dir = new();
        FileSessionRecordStore store = BuildStore(dir.Path);
        await store.SaveAsync(NewRecord("good", "10.0.0.1"), ct);
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "corrupt.json"), "{ not json", ct);

        IReadOnlyList<SessionRecord> loaded = await store.LoadAllAsync(ct);

        Assert.Single(loaded);
        Assert.Equal("good", loaded[0].SessionId);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("bad/slash")]
    [InlineData("")]
    public async Task Save_RejectsUnsafeSessionId(string id)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using SessionTempDir dir = new();
        FileSessionRecordStore store = BuildStore(dir.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(NewRecord(id, "10.0.0.1"), ct));
    }

    private static FileSessionRecordStore BuildStore(string directory)
    {
        AgentConfig config = new()
        {
            Sessions = new SessionsConfig { RecordDir = directory }
        };
        return new FileSessionRecordStore(config, NullLogger<FileSessionRecordStore>.Instance);
    }

    private static SessionRecord NewRecord(string id, string clientIp)
    {
        return new SessionRecord
        {
            SessionId = id,
            ClientIp = clientIp,
            SlotIds = [1],
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}

file sealed class SessionTempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lance-session-test-{Guid.NewGuid():N}");

    public SessionTempDir()
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
