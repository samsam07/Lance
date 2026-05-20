# Lance — Phased Implementation Plan

Three phases. Each phase ends with a usable product at its own level of maturity.

## Phase 1 — MVP

**Goal:** The user can productively use Lance for their daily work. Manual operation acceptable. Rough edges acceptable.

### Phase 1 — In scope

#### Agent (`lance-agent`) — Windows only in Phase 1

- HTTPS server on configurable port (default 9876), self-signed cert
- Bearer token auth on all endpoints except `/health`
- Endpoints (see SPEC.md for full schemas):
  - `GET  /health`
  - `GET  /slots`
  - `POST /slots/allocate` — clone N slots from template
  - `POST /slots/{id}/start`
  - `POST /slots/{id}/stop`
  - `POST /slots/{id}/deallocate` (rejects slot 0)
  - `GET  /slots/{id}/config-url` (requires slot running; supports `?redirect=1`)
  - `POST /sessions/connect` — convenience: allocate (if needed) + start, return connection info
  - `POST /sessions/disconnect` — convenience: stop + optionally deallocate
- Reads template from `<apollo_config_dir>/sunshine.conf` as slot 0 (hardcoded path resolution)
- Slot 0 semantics: addressable, startable, stoppable, NOT deallocatable
- Clones template + state file when allocating a new slot (1..N), omitting `file_apps` so Apollo falls back to default
- Tracks running Apollo processes by PID in memory
- Foreground console application (no Windows service yet)
- On startup: clean-slate (kill any existing Apollo processes — Lance owns the Apollo lifecycle)
- On graceful shutdown (Ctrl+C / SIGTERM / console close): stop all managed Apollo processes
- Structured logging (Serilog) to console and rolling file
- Config file: `lance-agent.json` (location: same dir as binary for Phase 1)

#### Client (`lance`) — Linux AND Windows in Phase 1

- Commands:
  - `lance connect [host[:port]] [options]` — positional target like mstsc, or use config
  - `lance disconnect [--keep-running] [--purge]`
  - `lance status`
  - `lance slots` — list slots on the agent
  - `lance config <slot_id>` — opens the slot's Apollo web UI in default browser (fails if not running)
- Config resolution precedence:
  1. Inline CLI flags
  2. Positional `host` argument
  3. `--config <path>` file
  4. `lance.json` beside the executable
  5. Platform default location
- Talks to agent via HTTPS with bearer token from config (cert verification disabled in Phase 1)
- Spawns Moonlight processes (one per requested monitor)
- Records local Moonlight PIDs and slot mappings in platform-appropriate state file location
- State file validation on connect: stale state (dead PIDs) is cleaned up automatically
- State file locking: prevents concurrent `lance` invocations from racing
- Refuses second `connect` if session is genuinely active (validated PIDs alive)
- Pretty output via Spectre.Console (tables for `status` and `slots`)
- All-or-nothing error handling: any failure aborts and rolls back
- Browser-open is platform-aware (`xdg-open` on Linux, `Process.Start` with shell exec on Windows)
- Browser-open failure → print URL and exit 0
- Config file location is platform-aware

#### Shared

- DTOs in `Lance.Shared` project
- JSON source generators for AOT
- AOT-compatible from the start
- No Newtonsoft.Json

### Phase 1 — Out of scope (don't implement, even if tempting)

- Cross-platform `lance-agent` (Phase 3 — Linux server, macOS server)
- macOS client (Phase 3)
- Windows service installation for agent
- systemd unit for agent
- Process adoption on agent restart (Phase 2)
- Persistent agent state file
- Window placement on physical monitors on the client
- Audio channel auto-assignment (inherits template's audio config)
- Interactive prompts on partial failure (Phase 1 aborts)
- `--no-prompt` flag (no prompts exist yet)
- `--continue-on-error` flag
- WebSocket / live status push
- Reconnect logic on transient failures
- TLS certificate verification (Phase 1 ignores)
- Multi-client support (one client at a time)
- Idle timeouts
- Installer packages
- Template config path overrides
- Custom Apollo config overrides at allocate time
- Adding more slots to an existing session
- Window placement primary marker (`*` syntax in `--monitors`)
- `lance slot <subcommand>` direct control commands (Phase 2)
- `LANCE_TOKEN` env var (Phase 2)
- Apollo install path auto-detection (Phase 2)
- Version mismatch warnings between client and agent

### Phase 1 — Definition of done

The user can:

1. Manually install + pair Apollo once on Windows (out of scope — that's user setup, documented in README)
2. Run `lance-agent` from a Windows terminal (no service installation required)
3. From their client machine (Fedora or Windows), run `lance connect` and have N Moonlight windows appear streaming virtual desktops
4. Connect with inline target (`lance connect 192.168.1.25 --token xxx`) OR via config file — both work
5. Work productively for at least an hour
6. Run `lance disconnect` and have everything clean up
7. Run `lance status` and see meaningful information
8. Run `lance config 1` (when slot 1 is running) and have the Apollo web UI for slot 1 open in a browser
9. Get a clear error when running `lance config 1` and slot 1 is not running
10. Recover gracefully from a stale state file (client crashed mid-session) on next `lance connect`

## Phase 2 — Daily-driver polish

**Goal:** Lance is genuinely pleasant to use every day. The user wouldn't switch back to FreeRDP for any reason.

### Phase 2 — In scope

- **Agent as a Windows service** (auto-start at boot, graceful shutdown handling)
- **Agent persistent state** (`%ProgramData%\Lance\state.json`) — slot status survives restart
- **Process adoption on startup** — agent recognizes existing Apollo processes whose configs match its naming pattern, takes ownership
- **Window placement on client:**
  - X11 path via `wmctrl` or `xdotool`
  - Wayland/KDE path via KWin window rules using a generated WM_CLASS per Moonlight instance
  - Windows: Win32 SetWindowPos or similar
  - Configurable monitor → window mapping
- **`--monitors` primary marker:** `--monitors 1,2*,3` makes monitor 2 the primary
- **Best-effort error handling:**
  - Interactive prompt on partial failure: `[c]ontinue / [r]etry / [a]bort`
  - `--no-prompt` flag → default to abort
  - `--continue-on-error` flag → default to continue
- **Health checks:** agent periodically checks Apollo processes are alive, optionally auto-restart crashed instances
- **Reconnect logic on client:** brief network blip → retry once before failing
- **Per-instance config overrides at connect time:** `--bitrate-per-monitor 50,80,150` for example
- **Audio routing intelligence:** option to enable audio on a specific slot or override the template's audio config
- **`lance reconnect`** — re-spawn Moonlight for an existing session without going through allocate
- **`lance add`** — add monitors to an active session
- **`lance slot <subcommand>`** — direct slot control (list / start / stop / allocate / deallocate / config)
- **`LANCE_TOKEN` env var** as another source in the config resolution precedence
- **Apollo install path auto-detection** (registry on Windows, common paths)
- **Version mismatch warning** between client and agent on connect (compare against `/health` response, warn but don't fail)
- **Logs default to file on client** (platform-appropriate state dir)
- **Better `lance status`:** includes agent-side status, network latency check, encoder load if obtainable
- **Idle timeout on agent:** configurable, auto-stop Apollo after N minutes with no Moonlight client connected
- **Template config path overrides** in agent config

### Phase 2 — Definition of done

- User installs the agent as a service and never thinks about it again
- Reboots the Windows machine, agent comes back up, slots are re-adopted on next `lance connect`
- `lance connect` places windows on the correct physical monitors automatically
- Network blips are handled gracefully
- Partial failures show useful prompts and recovery options

## Phase 3 — Production-ready for others

**Goal:** A second person who has never seen this could install and use Lance. Both binaries truly cross-platform.

### Phase 3 — In scope

- **Cross-platform `lance-agent`:** Linux and macOS server support (where Apollo runs)
- **macOS client port** for `lance`
- **Installers:**
  - Windows: MSI or single-file exe with embedded service installer
  - Linux: RPM, DEB, and Flatpak/AppImage
  - macOS: pkg or Homebrew formula
  - Portable distribution: zipped binary that runs without install
- **Proper TLS handling:** self-signed cert auto-generation with pinning option, optional Let's Encrypt for VPN scenarios, `verify_tls` config flag
- **Token rotation flow:** API endpoint or admin command to rotate the shared secret
- **Multi-client support:** multiple clients can talk to one agent simultaneously, each tracking its own sessions
- **Configuration UI:** small web UI on the agent for admin operations (no curl needed)
- **Documentation:**
  - User installation guides per platform
  - Troubleshooting playbooks
  - Architecture diagrams
- **Better error messages:** aimed at users not familiar with internals
- **Diagnostic command:** `lance diagnose` runs end-to-end checks and reports problems with specific fixes
- **Optional auto-pairing helper:** drives Moonlight's pairing flow without user intervention (uses Apollo's API)
- **Telemetry (opt-in):** anonymous error reporting to help fix issues for users who can't easily report them
- **Localization framework** (no actual translations yet, just the framework)
- **Stable API versioning:** v1 endpoint paths, backward compatibility commitments

### Phase 3 — Definition of done

- A technically-comfortable user who has never used Apollo can:
  1. Install Apollo (per Apollo's docs)
  2. Install Lance (one command per platform)
  3. Run `lance setup` (interactive guided setup, including initial pairing)
  4. Use `lance connect` daily
- Bug reports include enough information to diagnose without lengthy back-and-forth
- Both `lance` and `lance-agent` run on Linux, Windows, and macOS
- The project is shareable on GitHub with confidence
