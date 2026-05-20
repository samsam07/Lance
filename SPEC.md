# Lance — Technical Specification (Phase 1)

This is the source of truth for Phase 1 interfaces and behaviors. If something is undefined here, raise it with the user.

## 1. Data Models

### 1.1 Slot

Represents a configured Apollo instance.

```csharp
public sealed record Slot(
    int Id,                    // 0-based: 0 = template, 1..N = clones
    string Name,               // friendly name; slot 0 = "Lance-Template", slots 1..N = "Lance-N"
    int Port,                  // Apollo's primary port for this slot
    SlotStatus Status,         // Allocated | Running
    string ConfigPath,         // absolute path to the .conf file
    bool IsTemplate,           // true only for slot 0
    DateTimeOffset AllocatedAt,
    DateTimeOffset? StartedAt, // null if not running
    int? ProcessId             // PID of running Apollo, null if not running
);

public enum SlotStatus
{
    Allocated,    // config exists, Apollo not running
    Running,      // config exists, Apollo running
}
```

**Slot 0 special rules:**
- Always allocated (the template `sunshine.conf` always exists)
- Can be started and stopped like any other slot
- CANNOT be deallocated (would delete the template)
- Lance NEVER modifies its config file
- `IsTemplate = true` to flag it to the client
- **Used by `lance connect` like any other slot** — every session that requests N monitors uses slots 0..(N-1)

The agent's authoritative state is the on-disk config files. `Status = Running` is derived from whether the agent has a known PID for this slot AND that process is alive.

### 1.2 Session (client-side concept)

A session is the client's record of an active `lance connect`. Phase 1 supports one session at a time per client machine.

```csharp
public sealed record Session(
    DateTimeOffset StartedAt,
    string AgentBaseUrl,
    IReadOnlyList<SessionMonitor> Monitors
);

public sealed record SessionMonitor(
    int MonitorId,             // 1-indexed local physical monitor
    int SlotId,                // remote slot serving this monitor (0-based)
    int ApolloPort,            // remote Apollo port
    int MoonlightProcessId     // local Moonlight PID
);
```

Persisted to platform-appropriate state location while active. Deleted on clean disconnect.

## 2. Agent HTTP API

All endpoints (except `/health`) require `Authorization: Bearer <token>` header. Token comes from agent config.

Failed auth: `401 Unauthorized` with body `{"error": "invalid_token"}`.

All endpoints are served over **HTTPS with a self-signed certificate**. The client ignores cert errors in Phase 1.

All request and response bodies are JSON. All timestamps are ISO 8601 UTC. All IDs are integers.

### 2.1 `GET /health`

No auth required. Returns agent liveness and basic info.

**Response 200:**
```json
{
  "status": "ok",
  "version": "0.1.0",
  "uptime_seconds": 1234,
  "max_slots": 8,
  "template_path": "C:\\Program Files\\Apollo\\config\\sunshine.conf",
  "template_exists": true
}
```

### 2.2 `GET /slots`

Returns all slots known to the agent, including slot 0.

**Response 200:**
```json
{
  "slots": [
    {
      "id": 0,
      "name": "Lance-Template",
      "port": 49989,
      "status": "Allocated",
      "config_path": "C:\\Program Files\\Apollo\\config\\sunshine.conf",
      "is_template": true,
      "allocated_at": "2026-05-19T09:00:00Z",
      "started_at": null,
      "process_id": null
    },
    {
      "id": 1,
      "name": "Lance-1",
      "port": 48989,
      "status": "Running",
      "config_path": "C:\\Program Files\\Apollo\\config\\sunshine_1.conf",
      "is_template": false,
      "allocated_at": "2026-05-19T10:00:00Z",
      "started_at": "2026-05-19T10:00:05Z",
      "process_id": 12345
    }
  ]
}
```

### 2.3 `POST /slots/allocate`

Idempotently ensures slots exist for the given IDs.

**Request:**
```json
{ "slot_ids": [1, 2, 3] }
```

**Behavior:**
- For each requested ID, if a slot already exists, leave it alone.
- For each that doesn't exist, clone the template config + state file to `sunshine_N.conf` and copy the state file content to its new location.
- Mutate must-differ fields (see Section 5).
- Slot ID 0 in the request is invalid (already exists by definition) → `400`.
- Return the full updated state of just the requested slots.

**Response 200:**
```json
{
  "slots": [ /* Slot[] for each requested ID */ ]
}
```

**Errors:**
- `400 invalid_slot_id` — slot ID 0 or > max_slots, or negative
- `500 template_missing` — template file doesn't exist
- `500 io_error` — file I/O failure

### 2.4 `POST /slots/{id}/start`

Launches the Apollo process for this slot. Works for slot 0.

**Behavior:**
- If slot doesn't exist → `404`
- If already running → `200` with current state (idempotent)
- Spawn Apollo with `sunshine.exe <slot_config_path>`
- Wait briefly (up to 5 seconds) for Apollo to bind its port — confirms healthy startup
- Record PID
- Return slot state

**Response 200:** the Slot object.

**Errors:**
- `404 slot_not_found` — slot doesn't exist
- `500 apollo_launch_failed` — process failed to start or didn't bind port within timeout

### 2.5 `POST /slots/{id}/stop`

Terminates the Apollo process. Config remains. Works for slot 0.

**Behavior:**
- If slot doesn't exist → `404`
- If not running → `200` with current state (idempotent)
- Send graceful close (Windows: WM_CLOSE / Ctrl+C equivalent), wait up to 10s, then force kill
- Clear PID
- Return slot state

**Response 200:** the Slot object.

### 2.6 `POST /slots/{id}/deallocate`

Removes the slot's config and state files.

**Behavior:**
- If slot is 0 → `409 cannot_deallocate_template` (always)
- If slot doesn't exist → `200` (idempotent)
- If running → `409 slot_in_use` with error message
- Delete `sunshine_N.conf` and the slot's state file (and log file if present)
- Slot is gone

**Response 200:**
```json
{ "deallocated": true }
```

**Errors:**
- `409 cannot_deallocate_template` — tried to deallocate slot 0
- `409 slot_in_use` — slot is running, stop it first

### 2.7 `GET /slots/{id}/config-url`

Returns the URL of this slot's Apollo web UI.

**Behavior:**
- If slot doesn't exist → `404 slot_not_found`
- If slot is allocated but not running → `409 slot_not_running` (Apollo must be running to serve its web UI)
- Apollo's web UI is on `port + 1` (Apollo convention) and served over HTTPS by Apollo
- Return URL using the agent's bound hostname/IP

**Query parameters:**
- `?redirect=1` → instead of returning JSON, respond with `302` and `Location: <url>` header. Same auth required. Useful for direct-browser bookmarks in programmatic scenarios.

**Response 200 (default):**
```json
{ "url": "https://192.168.1.100:48990" }
```

**Response 302 (with `?redirect=1`):** empty body, `Location: https://192.168.1.100:48990` header.

**Errors:**
- `404 slot_not_found`
- `409 slot_not_running`

### 2.8 `POST /sessions/connect` (convenience composite)

Allocates slots if needed and starts them. Equivalent to bulk allocate + start.

**Request:**
```json
{ "slot_ids": [0, 1, 2] }
```

**Behavior:**
- Allocate any missing slots (slots 1..N; slot 0 is already allocated)
- Start all requested slots
- All-or-nothing: if any step fails, undo (stop anything started, deallocate anything newly created). Slot 0 is never deallocated during rollback.
- Return slot details

**Response 200:**
```json
{
  "slots": [ /* Slot[] for each requested ID */ ]
}
```

**Errors:**
- `400` — invalid IDs
- `500` — failure with details; rollback was attempted

### 2.9 `POST /sessions/disconnect` (convenience composite)

Stops the given slots, optionally deallocates.

**Request:**
```json
{
  "slot_ids": [0, 1, 2],
  "deallocate": false
}
```

**Behavior:**
- Stop all listed slots
- If `deallocate: true`, also deallocate them — EXCEPT slot 0 is skipped (it can't be deallocated; no error)
- Best-effort: don't fail the whole request if one stop fails; return per-slot result

**Response 200:**
```json
{
  "results": [
    { "id": 0, "stopped": true, "deallocated": false },
    { "id": 1, "stopped": true, "deallocated": false }
  ]
}
```

## 3. Client CLI

Implemented with System.CommandLine. Output uses Spectre.Console for tables and colored status.

### 3.1 Connection target and config resolution

`lance connect` and other agent-talking commands resolve their target via this precedence (highest wins):

1. **Inline flags** — `--token <token>`, `--port <port>`, etc.
2. **Positional `host[:port]`** — `lance connect 192.168.1.25` or `lance connect 192.168.1.25:9876`
3. **`--config <path>`** — load specified config file
4. **`lance.json` beside the executable** — portable mode
5. **Platform default** — `~/.config/lance/config.json` (Linux), `%APPDATA%\Lance\config.json` (Windows)
6. **Error** — nothing resolved; print clear message and exit code 7

Example usages:

```
# Pure config-driven
lance connect

# Like mstsc: target inline (uses config for token if present)
lance connect 192.168.1.25

# Fully inline (no config needed)
lance connect 192.168.1.25:9876 --token abc123xyz

# Override target but use config for token
lance connect 192.168.1.26

# Different config file
lance connect --config ~/work-pc.json
```

### 3.2 Global options

- `--agent <url>` — override agent URL (alternative to positional)
- `--token <token>` — override auth token
- `--config <path>` — override config file location
- `--verbose` / `-v` — debug logging to stderr
- `--no-color` — disable Spectre.Console colors
- `--help` / `-h` — auto-generated help

### 3.3 `lance connect [host[:port]] [options]`

**Positional:**
- `host[:port]` — agent target (optional; see resolution precedence above)

**Options:**
- `--monitors <list>` — comma-separated 1-indexed monitor IDs. Default: all physical monitors. Maps to slot IDs sequentially: first monitor → slot 0, second → slot 1, etc. (slot IDs are 0-based; monitor IDs are 1-based — they are independent concepts).
- `--bitrate <kbps>` — applied to all instances. Overrides config default_flags.
- `--codec <hevc|h264|av1>` — overrides config default_flags.
- `--fps <n>` — default 60.
- `--resolution <WxH>` — overrides per-monitor resolution inference. Phase 1: if specified, applies to all. Otherwise each Moonlight gets its monitor's native resolution.

**Behavior:**
1. Resolve agent target (see 3.1)
2. Acquire exclusive lock on state file (fail fast with exit 2 if another `lance` is running)
3. Read state file. If a session is recorded, validate it:
   - For each `moonlight_pid`, check if process is alive
   - If ALL recorded PIDs are dead → stale state, clean it up and proceed
   - If ANY are alive → genuine active session, refuse with exit 2
4. Enumerate local monitors
5. Determine slot IDs needed: 0..(N-1) where N = length of resolved monitor list
6. Call `POST /sessions/connect` with those slot IDs
7. For each returned slot, spawn Moonlight with appropriate flags
8. Record session state file
9. Release lock
10. Print summary table and return

**Exit codes:**
- 0 — success
- 1 — generic error
- 2 — session already active OR concurrent `lance` invocation
- 3 — agent unreachable
- 4 — agent error (slot allocation failed etc.)
- 5 — moonlight launch failed (rolled back)
- 6 — slot not in required state (e.g., for `lance config` when slot not running)
- 7 — config resolution failed (no config found, no inline target)

### 3.4 `lance disconnect [options]`

**Options:**
- `--keep-running` — don't stop Apollo. Just kill local Moonlight processes.
- `--purge` — also deallocate slots on the agent (slot 0 is never deallocated even with --purge).

`--keep-running` and `--purge` are mutually exclusive.

**Behavior:**
1. Acquire state file lock
2. Read session state file. If absent → "no active session" message, exit 0.
3. Kill local Moonlight PIDs (graceful close, wait 5s, then force kill).
4. Unless `--keep-running`: call `POST /sessions/disconnect` with `deallocate = <--purge>`.
5. Delete session state file.
6. Release lock.
7. Print summary.

### 3.5 `lance status`

Shows local session state and agent reachability. Pretty table via Spectre.

**Sample output:**
```
Lance status
────────────
Agent:       https://192.168.1.100:9876  ✓ reachable (uptime 2h 14m)
Session:     active (started 30m ago)

Active Monitors
───────────────
Monitor  Slot  Port    Moonlight PID  Status
1        0     49989   12345          running
2        1     48989   12346          running
3        2     47989   12347          running
```

If no session: just shows agent reachability and `Session: none`.

### 3.6 `lance slots`

Shows all slots known to the agent, regardless of session. Slot 0 included with a `(template)` marker.

**Sample output:**
```
Slots on agent
──────────────
ID  Name             Port    Status      PID    Allocated     Started
0   Lance-Template   49989   Running     12345  (template)    10:00:05
1   Lance-1          48989   Running     12346  10:00:00      10:00:05
2   Lance-2          47989   Allocated   -      10:00:00      -
```

### 3.7 `lance config <slot_id>`

Calls `GET /slots/{id}/config-url`, opens the URL in the default browser.

- Linux: `xdg-open <url>`
- Windows: `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`

**Browser-open failure handling:** if the open command fails (e.g., `xdg-open` not installed on a minimal install), print the URL with a "please open manually" message and exit 0. The user got the URL — that's enough.

If the slot is not running, the agent returns `409`; the CLI prints a clear error like:

```
Slot 1 is allocated but not running. Start it first.
```

Exit code 6.

## 4. Configuration Files

### 4.1 Agent config — `lance-agent.json`

Located beside the agent binary in Phase 1. Phase 2 moves to `%ProgramData%\Lance\`.

```json
{
  "listen": {
    "host": "0.0.0.0",
    "port": 9876
  },
  "tls": {
    "cert_path": null,
    "cert_password": null
  },
  "auth": {
    "token": "REPLACE_ME_WITH_A_LONG_RANDOM_STRING"
  },
  "apollo": {
    "install_dir": "C:\\Program Files\\Apollo",
    "config_dir": "C:\\Program Files\\Apollo\\config",
    "executable": "sunshine.exe",
    "template_config_name": "sunshine.conf"
  },
  "slots": {
    "max_count": 8,
    "port_step": 1000,
    "name_prefix": "Lance-",
    "template_name": "Lance-Template",
    "config_name_pattern": "sunshine_{id}.conf"
  },
  "logging": {
    "level": "Information",
    "file_path": "C:\\ProgramData\\Lance\\logs\\agent-.log",
    "retain_days": 7
  }
}
```

**TLS notes:**
- `cert_path = null` means auto-generate a self-signed cert on first run, cache it next to the binary.
- `cert_path` can be set to an explicit `.pfx` path with `cert_password` to use a user-supplied cert.

### 4.2 Client config — `lance.json` (beside binary) or platform default

```json
{
  "agent": {
    "url": "https://192.168.1.100:9876",
    "token": "REPLACE_ME_WITH_A_LONG_RANDOM_STRING",
    "timeout_seconds": 10
  },
  "moonlight": {
    "executable": "moonlight",
    "default_flags": [
      "--fps", "60",
      "--codec", "hevc",
      "--bitrate", "80000",
      "--no-vsync"
    ]
  },
  "ui": {
    "color": "auto"
  },
  "logging": {
    "level": "Information",
    "file_path": null
  }
}
```

**Notes:**
- `moonlight.executable` may differ per platform. On Linux it might be `moonlight` or full path. On Windows it might be `moonlight.exe` or full path to Moonlight Qt install.
- `default_flags` is the single source of moonlight-launch flags. CLI overrides (`--bitrate`, `--codec`, etc.) on `lance connect` append to or replace the corresponding entries. Phase 1 simplest: concatenate config flags + CLI overrides (later args win — works because most CLI tools and Moonlight handle dupes that way).
- No `verify_tls` flag in Phase 1 — Lance always ignores cert errors (the agent uses a self-signed cert).

## 5. Apollo Config Mutation Rules

When cloning the template (slot 0) to create slot N (where N >= 1), mutate these fields. All others are inherited verbatim.

| Field | Template (slot 0) | Slot N (N >= 1) |
|---|---|---|
| `sunshine_name` | (whatever) | `Lance-{N}` |
| `port` | template port | `template_port - (N * port_step)` |
| `log_path` | (whatever) | `sunshine_{N}.log` (relative to config dir) |
| `file_state` | `sunshine_state.json` | `sunshine_state_{N}.json` |

Implementation notes:

- Apollo's config format is INI-like: `key = value` per line.
- Lines without `=` are comments or section markers — preserve them verbatim.
- If a field doesn't exist in the template, ADD it to the cloned file.
- Always rewrite the file with the same line ordering as the template plus any new fields appended.
- After cloning the `.conf` file, also copy the template's `file_state` JSON content to the new slot's `file_state` path (this is what makes pairing inherit).

**Verify during implementation:**
- The exact lines/syntax of `sunshine.conf` — does it use sections? Comments with `#` or `;`? Quoted values?

## 6. Moonlight Launch

For each slot, the client spawns Moonlight with flags constructed from:

```
moonlight stream <agent_host>:<slot_port> "Desktop" \
  --resolution <WxH> \
  --fps <n> \
  --bitrate <kbps> \
  --codec <codec> \
  --no-vsync \
  [other flags from config]
```

**Notes:**
- `<agent_host>` is parsed from the resolved agent URL.
- Phase 1: spawn with default Moonlight host display selection. Phase 2 handles window placement.
- Track PID for later cleanup.
- Spawn as detached children (the client process can exit and Moonlight keeps running).
- The exact Moonlight binary and its CLI flags may differ per OS (`moonlight-embedded` on Linux, possibly `moonlight-qt` on Windows). Resolve via `moonlight.executable` config.

**Verify during implementation:**
- Confirm Moonlight CLI flags against the user's actual install. The user runs Moonlight daily and knows the working invocation. Get that exact command line and adapt.

## 7. State File (client-side)

Linux: `$XDG_RUNTIME_DIR/lance/state.json` (or `/tmp/lance-${UID}/state.json` fallback)
Windows: `%LOCALAPPDATA%\Lance\state.json`

```json
{
  "schema_version": 1,
  "started_at": "2026-05-19T10:00:00Z",
  "agent_url": "https://192.168.1.100:9876",
  "monitors": [
    {
      "monitor_id": 1,
      "slot_id": 0,
      "apollo_port": 49989,
      "moonlight_pid": 12345
    }
  ]
}
```

**Locking:** the state file is opened with an exclusive lock (`FileShare.None` or equivalent) during read+write operations. If a second `lance` invocation tries to acquire the lock and finds it held, it fails fast with exit code 2 and a clear message.

**Stale-state validation:** on `lance connect`, before refusing due to "session active", validate that recorded Moonlight PIDs are alive. If ALL are dead, clean up the state file and proceed. If ANY are alive, refuse.

## 8. Agent Lifecycle

### 8.1 Startup

1. Read config
2. Set up logging
3. Bind HTTPS listener
4. Enumerate Apollo processes (any process named `sunshine.exe`)
5. **Kill all of them** (clean-slate — Lance owns Apollo's lifecycle)
6. Scan slot configs in Apollo's config dir matching the slot naming pattern — these are "known" allocated slots from prior runs. The template (slot 0) is always known.
7. Mark all known slots as Allocated, no PIDs
8. Ready to serve

### 8.2 Shutdown (graceful)

Triggered by Ctrl+C, SIGTERM, console close (`IHostApplicationLifetime.ApplicationStopping` in ASP.NET Core):

1. Stop accepting new HTTP connections
2. For each running slot, send graceful stop (timeout 10s, then force kill)
3. Flush logs
4. Exit

### 8.3 Shutdown (forced)

`taskkill /F` or process crash: Apollo processes are orphaned. On next agent startup, clean-slate kill (8.1 step 5) handles them.

## 9. Error Response Format

All errors return JSON with this shape:

```json
{
  "error": "code_string",
  "message": "Human readable",
  "details": { /* optional, structured */ }
}
```

**Standard error codes:**
- `invalid_token`
- `slot_not_found`
- `slot_not_running`
- `slot_already_running`
- `slot_in_use`                  // tried to deallocate while running
- `cannot_deallocate_template`   // tried to deallocate slot 0
- `template_missing`
- `apollo_launch_failed`
- `apollo_already_running`       // PID conflict
- `invalid_slot_id`
- `max_slots_exceeded`
- `io_error`
- `internal_error`

## 10. Logging

- Agent: console + rolling file (daily). Format: `[Timestamp Level] Source: Message`.
- Client: stderr only in Phase 1.
- All HTTP requests logged at Information level.
- All Apollo process events (spawn, exit, signal) logged at Information.
- File operations on config/state files logged at Debug.

## 11. Things to verify or ask about during implementation

These are points where the spec doesn't fully nail down behavior. Ask the user:

1. **Apollo's exact INI format quirks** (does it allow comments? section headers? quoted values?). Verify against a real `sunshine.conf` early.
2. **Apollo's web UI port convention** — is it always `port + 1`, or is it configurable? Verify against the running Apollo.
3. **Moonlight's actual command line** — the user uses it daily; the exact invocation may differ from what's specified in Section 6. Confirm before writing launch code.
4. **Local monitor enumeration on KDE/Wayland** — what's the most reliable way to count physical monitors from a CLI tool? `xrandr` works on X11; for Wayland we may need `wlr-randr` or KDE-specific API. On Windows, `EnumDisplayMonitors` via P/Invoke. Start with what works (xrandr under XWayland is fine for Phase 1 since we only need a count, not positions).
5. **Does Moonlight have a way to specify which client display to render to?** If so, capture the flag now even if we don't use it in Phase 1.
6. **Self-signed cert generation** — should the agent regenerate on every startup, or cache to disk? Caching is friendlier (consistent fingerprint) but adds file management. Recommended: cache to disk beside the binary, regenerate only if missing or expired.

These are all early-implementation questions, not blocking the design.
