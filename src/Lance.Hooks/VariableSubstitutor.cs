using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Lance.Hooks;

// Resolves ${VAR} references in hook args against the event payload. There is no
// shell, so Lance does this itself. An unknown variable substitutes to empty (safer
// than passing a literal ${...} to a tool) and is logged when a logger is supplied.
internal static partial class VariableSubstitutor
{
    public static string Substitute(string input, IReadOnlyDictionary<string, string> env, ILogger? logger = null)
    {
        return VariableToken().Replace(input, match =>
        {
            string name = match.Groups[1].Value;
            if (env.TryGetValue(name, out string? value))
            {
                return value;
            }

            logger?.LogDebug("Hook variable {Variable} is not set; using an empty value.", name);
            return string.Empty;
        });
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex VariableToken();
}
