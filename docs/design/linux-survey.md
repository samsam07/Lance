# Linux survey — running the Lance client on Linux

> **Status: survey, not a design.** Findings and open questions only — nothing here
> is decided. Started 2026-11-21 after the client machine moved from Windows to
> Fedora. Once decisions land, behavior goes into ARCHITECTURE and values into
> SPEC; this file records the *why*.

## Scope

**Owner decision (2026-11-21): only the client moves to Linux. The agent stays on
Windows.** The target topology is therefore a **Linux client → Windows agent**.

Consequences:

- All agent-side Linux work is **out of scope**: the Linux UDP endpoint probe,
  Apollo's Linux privilege model and paths (`[VERIFY-APOLLO]`), agent Linux paths
  (`[DEFER-PATHS]`, agent half), a systemd daemon, and a Linux agent publish target.
  Upstream Apollo's virtual display is Windows-only anyway (it relies on SudoVDA),
  so a Linux agent would not work with Lance's headless-slot model today.
- Nothing on the wire changes. The client already talks to the agent over HTTPS
  with TLS validation disabled, and every Apollo `host:port` comes from the agent.

## Verified on this machine

Fedora 44, kernel 7.3.3, KDE Plasma on **Wayland** (XWayland present, `DISPLAY=:0`),
.NET SDK 10.0.113 from the Fedora RPM (`/usr/lib64/dotnet`). GPU: NVIDIA RTX 2060
SUPER, proprietary driver 620.70 (RPM Fusion). One monitor: DP-3, 2560×1440 @
143.99 Hz, scale 1.

| Check | Result |
|---|---|
| `dotnet build` | ✅ 0 errors — but **not warning-clean**: `NU1903` on `Microsoft.DotNet.ILCompiler 10.0.13` (high-severity advisory GHSA-p3jp-9v83-76w9). Comes from the installed SDK's runtime pack, not from `Directory.Packages.props`; fix is a newer SDK. `[VERIFY-VERSIONS]` |
| `dotnet test` | ✅ 137/137 pass (Agent 30, Client 88, Hooks 19) |
| `make publish` | ✅ produces `dist/client-linux/lance` (AOT, 9.6 MB); runs, reports `0.1.0+<sha>` |
| `lance monitors` (JIT and AOT) | ✅ DP-3, 2560×1440, 0,0, primary — via XWayland. Refresh `—` (`[DEFER-LINUX-REFRESH]`) |
| Hardware video decode | ✅ VA-API (NVDEC backend) reports H.264 and HEVC incl. Main10/444 — the config's `--video-codec HEVC` is fine. Fedora's codec-stripped Mesa does not apply (NVIDIA). |
| Moonlight installed | ❌ not yet. **Fedora's own `updates` repo ships `moonlight-qt`** (6.2.0, installs `/usr/bin/moonlight`) — chosen over Flatpak (owner, 2026-11-22). ffmpeg here is the full RPM Fusion build. |
| `wmctrl` / `xdotool` | ❌ not installed |
| Agent reachability | ⏸ not tested — no real `lance.json` on this machine (samples carry `<agent-host>`) |

**Environment (not repo code):**
- ✅ *Fixed 2026-11-22.* `~/.bashrc` exported `DOTNET_ROOT=$HOME/.dotnet`, a leftover
  from a script install, so framework-dependent apphosts failed to start. Line
  removed (backup `~/.bashrc.bak-lance`).
- `NU1903` is fixed in runtime **10.0.14** (advisory: `>= 10.0.0, < 10.0.14`). Fedora
  `updates` still has SDK 10.0.113; `updates-testing` has **10.0.115**.

## Already works on Linux

- Config, HTTP/TLS to the agent, all slot commands, `status`, sessions, hooks.
- `--config <path>` loads a config from anywhere (workaround for C7 below).
- Moonlight discovery by command line via `/proc/{pid}/cmdline` (detection method 1).
- Teardown via `Process.Kill(entireProcessTree: true)` — the designed Linux path
  (no Job Object; hard daemon death orphans Moonlight, accepted in ARCHITECTURE).
- Monitor enumeration via Xrandr 1.5 P/Invoke — works under XWayland.
- `lance config` opens URLs with `xdg-open`.

## Gaps

| # | Gap | Where | Known? | Severity |
|---|---|---|---|---|
| C1 | ✅ *Fixed (2026-11-22).* **SIGHUP was not handled** — closing the terminal running `lance connect` killed it (exit 129) with no `session_ended` hooks and no disconnect ping, leaving the Windows agent to notice via probe-watch (~6–7 s). `SessionDaemon` now registers SIGHUP (Unix only), cancels the default kill, and ends the session like Ctrl-C (`source=explicit`). Verified end-to-end (JIT and AOT) by running the real client on a pty against a fake HTTPS agent and closing the pty: hooks ran, `DELETE /sessions/{id}` arrived, exit 0. Writes to the dead terminal throw `IOException`; Serilog swallows them, but any future *unguarded* console write in the teardown path would crash it. | `SessionDaemon.RunAsync` | **New** | High |
| C2 | ✅ *Avoided.* Flatpak Moonlight would break process discovery (`FindMoonlightProcesses` derives the name `com.moonlight_stream` from the wrapper path and misses the real `moonlight`). Owner chose the Fedora RPM, whose `/usr/bin/moonlight` matches Lance's Linux default executable. Revisit only if Flatpak/AppImage support is wanted. | `ProcessCommandLine.FindMoonlightProcesses` | **New** | — |
| C3 | ✅ *Done (2026-11-22).* Samples were Windows-only (`C:\…\moonlight.exe`, a `hooks\\…` backslash path Linux cannot resolve, `cmd.exe` / `%TEMP%` in `smoke.json`). Added `samples/linux/lance.json` and `samples/linux/hooks/smoke.json` (`sh -c`, `/tmp/lance-hook-smoke.log`, token as a `<agent-token>` placeholder); the Linux publish now ships that config. No Linux `vox.client.json`: building `vox` for Linux is a separate project, out of scope here (owner, 2026-11-22). | `samples/linux/`, `scripts/publish.cs` | **New** | — |
| C11 | ✅ *Fixed (2026-11-22).* **Ctrl-C teardown was cut off after ~2 s.** System.CommandLine's `ProcessTerminationTimeout` defaults to 2 s and is not configured, so after Ctrl-C (or SIGTERM) the process is force-exited (130) if `session_ended` hooks + the ping take longer. Measured with a probe: a 5 s teardown is killed at the default, completes with a 30 s timeout. Affects the Windows client too. Fixed with approach (b): the daemon owns SIGINT/SIGTERM, the library handler is off for `connect`. Verified: a 6 s `session_ended` hook completes and the ping arrives, for both Ctrl-C and SIGTERM. | `Program.cs` `InvokeAsync` | **New** | Medium (only bites slow hooks / slow agent) |
| C4 | **Every slot must be re-paired from the new client.** A fresh Moonlight install has a new client certificate, so Slot 0 and each clone (each has its own server UUID) need a one-time pairing from Fedora before `lance connect` works. Operational, not a code gap. | — | Follows from `[DEFER-PAIR-AUTO]` | Setup step |
| C5 | Window placement — Windows-only, early return. | `SessionDaemon.PositionWindowsAsync` | `[DEFER-LINUX-WINPOS]` | Low with one monitor |
| C6 | Window-title detection (method 2) — always `null` on Linux. | `ProcessCommandLine` | `[DEFER-LINUX-WINDETECT]` | Low (only hand-launched Moonlights) |
| C7 | `lance.json` resolves only beside the binary (or via `--config`). | `ClientConfigLoader` | `[DEFER-PATHS]`, Slice 7 | Medium once `lance` lives on PATH |
| C8 | Refresh rate unknown → no generated `--fps`. Now **has hardware to verify against** (kscreen reports 143.99 Hz); the original deferral reason was "no Linux hardware". | `MonitorEnumerator.EnumerateLinux` | `[DEFER-LINUX-REFRESH]` | Low (cap is 60; Moonlight's default applies) |
| C9 | `DllImport("libX11")` / `("libXrandr")` resolve the unversioned `.so`, which on Fedora ships in `libX11-devel` / `libXrandr-devel`. Present here; on a clean install `lance monitors` would throw `DllNotFoundException`. | `MonitorEnumerator` | **New** | Low (portability) |
| C10 | Pure Wayland (no XWayland) → empty monitor list. | `MonitorEnumerator` | PLAN Slice 5 | Low on KDE (XWayland on by default) |

**The Wayland wrinkle in C5/C6.** PLAN Slice 5 assumes `wmctrl` (X11). On KDE
Wayland, a native-Wayland Moonlight window is invisible to `wmctrl`/`XMoveWindow`,
and Wayland clients cannot position themselves. Options, none chosen:

1. Force Moonlight onto XWayland (`QT_QPA_PLATFORM=xcb`, `SDL_VIDEODRIVER=x11` in
   its environment) so the planned `wmctrl`/X11 approach applies. Cost: loses
   native-Wayland presentation; latency impact unmeasured.
2. KWin scripting over D-Bus (move window by PID to an output). KDE-only.
3. Leave placement to the user / compositor window rules.

With a single monitor on this machine, C5/C6/C8 barely matter today — they matter
once a second monitor is attached.

## Security note (not Linux-specific)

`samples/lance.json` and `samples/lance-agent.json` both carry the same concrete
bearer token, committed since `3df26be` (2026-05-31) to a **public** GitHub repo.
If the Windows agent uses that token, it is effectively public — rotate it on the
agent and replace the samples with a placeholder.

## Decisions

Made (2026-11-22): client-only scope; Moonlight from the Fedora RPM; SIGHUP → graceful
teardown; per-OS samples under `samples/linux/`; C1/C3 filed under Slice 2 (daily-use
fixes), the rest stays in Slice 5.

Made (2026-11-22, later): **C11 → approach (b)** — the connect daemon owns
SIGINT/SIGTERM the way it owns SIGHUP, and System.CommandLine's termination handler is
disabled for `connect`, so teardown is bounded only by the existing hook
`timeoutSeconds` and agent `timeoutSeconds`. **`vox` on Linux** is a separate project,
out of scope here.

Open:

1. **Wayland placement (C5/C6):** option 1, 2 or 3 above — or defer until a second
   monitor is in play.

## Suggested order

1. ~~Fix `DOTNET_ROOT`~~ ✅. Update the SDK to clear `NU1903`; rotate the agent
   token if it is the committed one.
2. Install Moonlight (Fedora RPM), write a real `lance.json` from
   `samples/linux/lance.json` (via `--config` until C7), pair each slot from Fedora (C4).
3. Run one real `lance connect` → `disconnect` against the Windows agent — the
   outstanding Slice 1.8 end-to-end run, now from Linux.
4. ~~C1 SIGHUP~~ ✅, ~~C3 samples~~ ✅, ~~C11 Ctrl-C cutoff~~ ✅.
5. C7 XDG config path (Slice 7).
6. C8 refresh rate, then C5/C6 placement when a second monitor matters.
