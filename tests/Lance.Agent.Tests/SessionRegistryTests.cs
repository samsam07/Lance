using Lance.Agent.Sessions;
using Xunit;

namespace Lance.Agent.Tests;

public sealed class SessionRegistryTests
{
    [Fact]
    public void TryAdd_DuplicateId_IsRefused()
    {
        SessionRegistry registry = new();

        Assert.True(registry.TryAdd(NewSession("abc")));
        Assert.False(registry.TryAdd(NewSession("abc")));
    }

    [Fact]
    public void TryMarkConnected_FromProvisioned_SetsConnectedState()
    {
        SessionRegistry registry = new();
        registry.TryAdd(NewSession("abc"));

        DateTimeOffset when = DateTimeOffset.UtcNow;
        bool changed = registry.TryMarkConnected("abc", when);

        Assert.True(changed);
        registry.TryGet("abc", out Session? session);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Connected, session.State);
        Assert.Equal(when, session.ConnectedAt);
    }

    [Fact]
    public void TryMarkConnected_WhenAlreadyConnected_IsNoOp()
    {
        SessionRegistry registry = new();
        registry.TryAdd(NewSession("abc"));
        registry.TryMarkConnected("abc", DateTimeOffset.UtcNow);

        Assert.False(registry.TryMarkConnected("abc", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TryMarkEnded_TransitionsOnceThenRefuses()
    {
        SessionRegistry registry = new();
        registry.TryAdd(NewSession("abc"));

        Assert.True(registry.TryMarkEnded("abc"));
        registry.TryGet("abc", out Session? session);
        Assert.Equal(SessionState.Ended, session!.State);
        Assert.False(registry.TryMarkEnded("abc"));
    }

    [Fact]
    public void TryMarkConnected_UnknownId_ReturnsFalse()
    {
        SessionRegistry registry = new();

        Assert.False(registry.TryMarkConnected("missing", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Remove_DropsSession()
    {
        SessionRegistry registry = new();
        registry.TryAdd(NewSession("abc"));

        registry.Remove("abc");

        Assert.False(registry.TryGet("abc", out _));
        Assert.Empty(registry.GetAll());
    }

    private static Session NewSession(string id)
    {
        return new Session
        {
            Id = id,
            ClientIp = "192.168.1.10",
            SlotIds = [1],
            State = SessionState.Provisioned,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
