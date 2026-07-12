using Lance.Hooks;
using Xunit;

namespace Lance.Hooks.Tests;

public sealed class VariableSubstitutorTests
{
    [Fact]
    public void Substitute_ReplacesKnownVariable()
    {
        Dictionary<string, string> env = new() { [LanceEnv.SessionId] = "abc123" };

        Assert.Equal("id=abc123", VariableSubstitutor.Substitute("id=${LANCE_SESSION_ID}", env));
    }

    [Fact]
    public void Substitute_UnknownVariable_BecomesEmpty()
    {
        Dictionary<string, string> env = new();

        Assert.Equal("peer=", VariableSubstitutor.Substitute("peer=${LANCE_AGENT_IP}", env));
    }

    [Fact]
    public void Substitute_LeavesNonVariableTextUntouched()
    {
        Dictionary<string, string> env = new() { [LanceEnv.ClientIp] = "10.0.0.9" };

        Assert.Equal("--peer 10.0.0.9 --verbose", VariableSubstitutor.Substitute("--peer ${LANCE_CLIENT_IP} --verbose", env));
    }
}
