using System.CommandLine;
using Lance.Client.Configuration;

namespace Lance.Client.Commands;

internal sealed record GlobalOptions(
    Option<string?> AgentOption,
    Option<string?> TokenOption,
    Option<bool> NoColorOption,
    Func<ClientConfig?> GetConfig
);
