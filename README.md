# Lance

Lance is a command-line tool for seamless **multi-monitor remote desktop** using
[Apollo](https://github.com/apolloapp-io/apollo) (a Sunshine fork) and
[Moonlight](https://moonlight-stream.org). It manages the complexity of running
one Apollo instance per monitor in parallel, so a single command opens or closes
a full multi-monitor session.

> **Status: Alpha** — fully functional for personal use. No service installer
> yet; both binaries are run manually.

---

## How it works

Two components cooperate:

| Binary | Role | Runs on |
|---|---|---|
| `lance-agent` | Web API that manages Apollo instances | Remote machine (Windows) |
| `lance` | CLI that drives the session from the local machine | Local machine (Windows or Linux) |

`lance connect` opens a **session**: it asks the agent to start one Apollo
instance per monitor, launches one Moonlight window per instance, then **stays
running in the foreground** until the session ends — you run `lance disconnect`,
close the streams, or press Ctrl-C. Each instance uses an independent
configuration cloned from your existing Apollo setup. Each clone has its own
identity and must be paired with Moonlight once before it can be used.

While a session runs, the agent watches the streams (so it cleans up even if the
client machine vanishes), and both sides can run **hooks** — external commands you
configure to fire on session start/end, e.g. to bridge a microphone back to the
host. See [Sessions & hooks](#sessions--hooks).

A **slot** is Lance's term for one Apollo instance and its configuration. Slot 0
is your original Apollo config (the template); slots 1, 2, … are clones. `lance
status` shows all slots and which ones have an active Moonlight connection.

---

## Requirements

**Remote machine (agent)**
- Windows, run as Administrator
- [Apollo](https://github.com/apolloapp-io/apollo) installed and paired with
  Moonlight at least once
- The Apollo service (`sunshinesvc.exe`) **stopped** before running the agent —
  Lance manages its own Apollo processes directly and the two will conflict

**Local machine (client)**
- [Moonlight](https://moonlight-stream.org) installed (`moonlight.exe` on
  Windows, `moonlight` on Linux)
- Network access to the remote machine on the agent port (default: 9876)

**Build machine**
- .NET 10 SDK
- MSVC toolchain — required for AOT compilation (Windows only, only needed when building)

---

## Building

Run from the repository root on a **Windows** machine:

```
make publish
```

Optional:

```
make publish-keep-iis    # keeps web.config and static web asset files in the agent output (rarely needed)
make test                # run all tests
```

`make` ships with Git for Windows (Git Bash). If you prefer to invoke the script directly:

```
dotnet run scripts/publish.cs [--keep-iis-artifacts]
```

**Outputs:**

| Path | Contents |
|---|---|
| `dist/agent/` | Agent binary + sample config — deploy to the remote machine |
| `dist/client/` | Client binary + sample config — deploy to the local machine |
| `dist/client-linux/` | Linux client binary — produced when the script runs on Linux |

---

## Deployment

### Agent (remote machine)

1. Copy `dist/agent/` to a folder on the remote machine, e.g. `C:\Lance\agent\`.
2. Edit `lance-agent.json`:
   - Set `remoteServer.installDir` and `remoteServer.configDir` to your Apollo
     installation paths.
   - Set `auth.token` to a secret string to protect the API (recommended). Set it
     to `""` to run the API open with no authentication.
   - `tls.certPath` is unused in the current release — HTTPS uses the ASP.NET Core
     developer certificate. Run `dotnet dev-certs https --trust` once on the agent
     machine if you have not already done so.
3. Stop the Apollo service if it is running.
4. Run as Administrator:
   ```
   lance-agent.exe
   ```

Logs are written to the console and to `lance-agent.log` (rolling daily). On
first run without a config file, built-in defaults apply and a warning is logged.

### Client (local machine)

1. Place `lance.exe` (or `lance` on Linux) somewhere on your PATH.
2. Copy `lance.json` from `dist/client/` beside the binary, or point to it with
   `--config <path>`.
3. Edit `lance.json`:
   - Set `agent.url` to `https://<remote-machine-ip>:9876`.
   - Set `agent.token` to match `auth.token` from `lance-agent.json`.
   - Adjust `remoteClient.executable` if Moonlight is not on PATH.
   - Tune `remoteClient.defaultFlags` for your setup (fps, codec, bitrate).

---

## Usage

```
# Check slot states and active Moonlight connections
lance status

# List physical monitors on this machine (use the IDs with --monitors)
lance monitors

# Connect to all physical monitors — BLOCKS until the session ends (Ctrl-C to end)
lance connect

# Connect to specific monitors only (1-indexed, comma-separated)
lance connect --monitors 1,3

# Connect with custom Moonlight flags (appended after the defaults; later flags win)
lance connect --monitors 1,2 --options "--fps 120 --bitrate 100000"

# Give the session an explicit id and run extra hook files (repeatable)
lance connect --session-id office --hook ~/hooks/vox.client.json

# --- Disconnect runs from a SEPARATE terminal (connect is blocking) ---

# End one session by id — kills its Moonlights; the agent tears down its side.
# Apollo is LEFT RUNNING on the remote by default (fast reconnect).
lance disconnect --session-id office

# End all active sessions
lance disconnect

# Also stop and deallocate the session's slots on the remote (Slot 0 excluded)
lance disconnect --session-id office --purge

# Fallback when the agent is unreachable: kill Moonlights by host:port
lance disconnect 192.168.1.50:47989

# Low-level slot management
# <ids> is one id or a comma-separated list (e.g. 1 or 1,2,3); each id is
# processed independently (partial success — a failed id is logged, the rest proceed)
lance slots                     # list all slots
lance allocate <count>          # ensure the pool has exactly <count> slots
lance start <ids>               # start each slot's Apollo instance
lance stop <ids>                # stop each slot's Apollo instance
lance deallocate <ids>          # remove slot configs (slots must be stopped)
lance deallocate <ids> --force  # stop if running, then remove config

# Open Apollo's web config page for one or more slots in the browser
lance config <ids>
```

**Global options** (work with any command):

```
-a, --agent <url>     Override the agent URL for this invocation
-k, --token <value>   Bearer token for the agent API
-c, --config <path>   Use a specific lance.json
-v, --verbose         Enable debug logging to stderr
    --no-color        Disable ANSI colour output
```

---

## Sessions & hooks

A **session** is one `lance connect` invocation and the slots it acquired.
`lance connect` runs in the foreground and blocks until the session ends. Both
sides end independently from their own signals:

- **Client** ends when its last Moonlight exits, on Ctrl-C, or when you run
  `lance disconnect`. It then runs its `session_ended` hooks and pings the agent.
- **Agent** ends when the clean-disconnect ping arrives, or — as a backstop, so it
  cleans up even if the client machine dies — when it detects the streams are gone
  (a few seconds after a hard cut). If the agent itself crashes mid-session, it
  replays the pending teardown on restart.

At session end the agent frees the slots but **leaves Apollo running** (fast
reconnect); use `lance disconnect --purge` to stop and deallocate them too.

**Hooks** are external commands that run on session events. Each side runs its own
hooks from its own config — nothing crosses the wire. A hook file is JSON:

```json
{
  "name": "vox",
  "events": {
    "session_started": {
      "commands": [
        { "command": "audiohelper.exe", "args": ["launch-vox", "--peer", "${LANCE_AGENT_IP}"] }
      ]
    },
    "session_ended": {
      "commands": [
        { "command": "audiohelper.exe", "args": ["kill-vox"] }
      ]
    }
  }
}
```

- Commands run in array order; `async: true` spawns without waiting; `onError`
  (`terminate` | `continue`) and `timeoutSeconds` control a synchronous command's
  failure handling. Multiple hook files are ordered by `priority` (lower first).
- `${VAR}` in `args` is substituted from the event payload — `LANCE_SESSION_ID`,
  `LANCE_AGENT_IP`, `LANCE_CLIENT_IP`, `LANCE_SLOT_IDS`, `LANCE_SIDE`, and (at
  teardown) `LANCE_EVENT_SOURCE`. There is no shell; commands are launched directly.
- Configure hooks with `hooks: [{ active, path }]` in `lance-agent.json` /
  `lance.json`, or `--hook <path>` on `lance connect` (repeatable, added on top).
- Lance never supervises hook-spawned processes — a tool whose teardown must kill a
  process has to make it findable (a pidfile or a wrapper that tracks the PID).

Ready-made examples are in [`samples/hooks/`](samples/hooks/), including
`smoke.json` — a dependency-free hook that logs each event to
`%TEMP%\lance-hook-smoke.log`, handy for confirming hooks fire.

---

## Configuration

### Agent — `lance-agent.json`

Place beside `lance-agent.exe`. Missing file → built-in defaults apply.

```json
{
  "listen": {
    "host": "0.0.0.0",
    "port": 9876
  },
  "tls": {
    "certPath": "lance-agent.pfx"
  },
  "auth": {
    "token": "ODlyrexDUv5jckPb7nUWBK9O"
  },
  "remoteServer": {
    "installDir": "C:\\Program Files\\Apollo",
    "configDir":  "C:\\Program Files\\Apollo\\config",
    "executable": "sunshine.exe",
    "templateConfigName": "sunshine.conf",
    "startupTimeoutSeconds": 30
  },
  "slots": {
    "maxCount": 8,
    "portStep": 1000,
    "stopTimeoutSeconds": 10,
    "namePrefix": "Lance",
    "templateName": "Lance-Template",
    "configNamePattern": "sunshine_{id}.conf"
  },
  "sessions": {
    "provisionGraceSeconds": 30,
    "probePollSeconds": 1,
    "recordDir": "C:\\ProgramData\\Lance\\sessions"
  },
  "hooks": [
    { "active": false, "path": "hooks\\vox.agent.json" }
  ],
  "logging": {
    "level": "Information",
    "filePath": "lance-agent.log",
    "retainDays": 7
  }
}
```

`sessions` tunes teardown detection (`provisionGraceSeconds` = how long a session
may wait for its first stream; `probePollSeconds` = poll interval; `recordDir` =
where crash-recovery records are written). `hooks` lists hook files to run on
session events.

### Client — `lance.json`

Place beside `lance.exe` / `lance`, or specify with `--config <path>`.

```json
{
  "agent": {
    "url": "https://<agent-host>:9876",
    "token": "ODlyrexDUv5jckPb7nUWBK9O",
    "timeoutSeconds": 30
  },
  "remoteClient": {
    "executable": "C:\\Program Files\\Moonlight Game Streaming\\moonlight.exe",
    "defaultFlags": ["--fps", "60", "--video-codec", "HEVC", "--bitrate", "80000", "--no-vsync"]
  },
  "ui": { "color": true },
  "hooks": [
    { "active": false, "path": "hooks\\vox.client.json" }
  ],
  "logging": { "level": "Information", "filePath": null }
}
```

Full sample files are in [`samples/`](samples/).

**Config file lookup** (first match wins):

1. `-c` / `--config <path>` CLI flag
2. `lance.json` beside the `lance` binary
3. Exit 7 if neither yields a URL

---

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Generic error |
| 2 | No free slots — all slots are connected; disconnect first |
| 3 | Agent unreachable |
| 4 | Agent returned an error |
| 5 | Moonlight launch failed |
| 6 | Slot not in required state |
| 7 | Agent URL could not be resolved |

---

## Notes

- **Pairing slots:** each clone slot has its own Apollo identity and must be
  paired with Moonlight individually before first use. Start the slot
  (`lance start <id>`), open Moonlight and add the host on that slot's port
  (`host:<port>`), complete the PIN pairing, then stop the slot
  (`lance stop <id>`). Only needs doing once per slot.
- **Monitor placement:** on Windows, Lance automatically moves each Moonlight
  window to its target monitor after launch. On Linux, windows open on the
  primary monitor; manual placement is needed until Phase 3 adds Linux support.
- **TLS:** The agent always uses HTTPS with a self-signed certificate. The client
  skips certificate validation automatically (configurable cert pinning is planned
  for a later release).
- **Partial success:** `connect` and `disconnect` are best-effort per monitor — a
  failed monitor is logged and skipped; the others proceed.
- **Apollo service:** Lance manages only the Apollo instances it launches directly.
  The installed Apollo service (`sunshinesvc.exe`) must be stopped before running
  the agent, otherwise the two will conflict for the same ports and config files.

---

## Technical reference

| File | Contents |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | System design, flows, and behavioral invariants |
| [docs/SPEC.md](docs/SPEC.md) | API contract, config shapes, ports, and mutation rules |
