using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Serilog;

namespace Lance.Client.Infrastructure;

// Linux on KDE Plasma: puts each Moonlight window on its target monitor. A Wayland
// client cannot position itself and Moonlight has no display option, so Lance asks the
// compositor instead — it loads a small KWin script over D-Bus (through the stock
// `gdbus` tool, so no D-Bus library is needed) that sends every window of each Moonlight
// process to that process's output. The script handles windows that already exist and
// any that appear later (the stream window only opens once the stream connects), and
// re-places a window that goes fullscreen on another display afterwards: SDL under
// XWayland picks its own display when it goes fullscreen, undoing the first move.
// Monitors map to KWin outputs by name — the Xrandr names Lance enumerates are KWin's
// output names. The script stays loaded for the session; RemoveAsync unloads it.
[SupportedOSPlatform("linux")]
internal static class KWinWindowPlacer
{
    private const string Gdbus = "gdbus";
    private const int CommandTimeoutSeconds = 5;

    // __PLACEMENTS__ becomes a JS object literal mapping PID → output name.
    private const string ScriptTemplate = """
        // Written by Lance: moves each Moonlight window onto its target monitor.
        const placements = __PLACEMENTS__;

        function place(window) {
            const outputName = placements[window.pid];
            if (outputName === undefined || !window.normalWindow) {
                return;
            }

            const output = workspace.screens.find(o => o.name === outputName);
            if (!output) {
                return;
            }

            workspace.sendClientToScreen(window, output);
            window.fullScreenChanged.connect(() => {
                if (window.fullScreen && window.output !== output) {
                    workspace.sendClientToScreen(window, output);
                }
            });
        }

        workspace.windowList().forEach(place);
        workspace.windowAdded.connect(place);
        """;

    internal static async Task<bool> PlaceAsync(string sessionId, IReadOnlyList<(int Pid, string OutputName)> placements)
    {
        if (placements.Count == 0)
        {
            return false;
        }

        string scriptName = ScriptNameFor(sessionId);
        string scriptPath = ScriptPathFor(sessionId);

        try
        {
            await File.WriteAllTextAsync(scriptPath, BuildScript(placements));

            // A script left behind by a crashed session with the same id would make the load fail.
            await RunGdbusAsync("/Scripting", "org.kde.kwin.Scripting.unloadScript", scriptName);

            GdbusResult load = await RunGdbusAsync("/Scripting", "org.kde.kwin.Scripting.loadScript", scriptPath, scriptName);
            int scriptId = load.IsSuccess ? ParseIntReply(load.Output) : -1;
            if (scriptId < 0)
            {
                WarnNotPlaced(load.IsSuccess ? "KDE refused the placement script" : load.Error);
                File.Delete(scriptPath);
                return false;
            }

            GdbusResult run = await RunGdbusAsync($"/Scripting/Script{scriptId}", "org.kde.kwin.Script.run");
            if (!run.IsSuccess)
            {
                WarnNotPlaced(run.Error);
                await RemoveAsync(sessionId);
                return false;
            }
        }
        catch (Win32Exception)
        {
            WarnNotPlaced("the 'gdbus' tool was not found");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WarnNotPlaced(ex.Message);
            return false;
        }

        Log.Debug("Asked KDE to put {Count} Moonlight window(s) on their monitors", placements.Count);
        return true;
    }

    internal static async Task RemoveAsync(string sessionId)
    {
        try
        {
            await RunGdbusAsync("/Scripting", "org.kde.kwin.Scripting.unloadScript", ScriptNameFor(sessionId));
            File.Delete(ScriptPathFor(sessionId));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log.Debug("Could not remove the window placement script for session {SessionId}: {Reason}", sessionId, ex.Message);
        }
    }

    private static string ScriptNameFor(string sessionId)
    {
        return $"lance-{sessionId}";
    }

    private static string ScriptPathFor(string sessionId)
    {
        return Path.Combine(Path.GetTempPath(), $"lance-placement-{sessionId}.js");
    }

    private static string BuildScript(IReadOnlyList<(int Pid, string OutputName)> placements)
    {
        StringBuilder entries = new("{ ");
        for (int i = 0; i < placements.Count; i++)
        {
            if (i > 0)
            {
                entries.Append(", ");
            }

            // A JSON string literal is a valid JS string literal, with any quote escaped.
            entries.Append(placements[i].Pid).Append(": \"").Append(JsonEncodedText.Encode(placements[i].OutputName)).Append('"');
        }

        entries.Append(" }");
        return ScriptTemplate.Replace("__PLACEMENTS__", entries.ToString());
    }

    private static async Task<GdbusResult> RunGdbusAsync(string objectPath, string method, params string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Gdbus,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("call");
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add(CommandTimeoutSeconds.ToString());
        startInfo.ArgumentList.Add("--dest");
        startInfo.ArgumentList.Add("org.kde.KWin");
        startInfo.ArgumentList.Add("--object-path");
        startInfo.ArgumentList.Add(objectPath);
        startInfo.ArgumentList.Add("--method");
        startInfo.ArgumentList.Add(method);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new Win32Exception("gdbus could not be started");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new GdbusResult(process.ExitCode == 0, (await output).Trim(), DescribeError(await error));
    }

    // gdbus reports "Error: GDBus.Error:<dbus error name>: <message>"; reword the common
    // case and drop the D-Bus prefix from the rest.
    private static string DescribeError(string error)
    {
        string firstLine = error.Split('\n', 2)[0].Trim();
        if (firstLine.Contains("org.freedesktop.DBus.Error.ServiceUnknown", StringComparison.Ordinal))
        {
            return "KDE's window manager (KWin) is not running in this session";
        }

        int messageStart = firstLine.LastIndexOf(": ", StringComparison.Ordinal);
        return messageStart >= 0 ? firstLine[(messageStart + 2)..] : firstLine;
    }

    // gdbus prints a single int reply as "(3,)".
    private static int ParseIntReply(string reply)
    {
        string digits = reply.Trim('(', ')', ',', ' ');
        return int.TryParse(digits, out int value) ? value : -1;
    }

    private static void WarnNotPlaced(string reason)
    {
        Log.Warning("Could not move the Moonlight windows to their monitors (this needs KDE Plasma): {Reason}", reason);
    }

    private sealed record GdbusResult(bool IsSuccess, string Output, string Error);
}
