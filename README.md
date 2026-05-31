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

`lance connect` asks the agent to start one Apollo instance per monitor, then
launches one Moonlight window per instance. Each instance uses an independent
configuration cloned from your existing Apollo setup, inheriting your pairing
credentials automatically.

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
dotnet run scripts/dist.cs
```

Optional flag: `--keep-iis-artifacts` — keeps `web.config` and static web asset
files in the agent output (rarely needed).

**Outputs:**

| Path | Contents |
|---|---|
| `dist/lance-agent.zip` | Agent binary + sample config — deploy to the remote machine |
| `dist/client/` | Client binary + sample config — deploy to the local machine |
| `dist/client-linux/` | Linux client binary — produced when the script runs on Linux |

---

## Deployment

### Agent (remote machine)

1. Extract `lance-agent.zip` into a folder, e.g. `C:\Lance\agent\`.
2. Edit `lance-agent.json`:
   - Set `remoteServer.installDir` and `remoteServer.configDir` to your Apollo
     installation paths.
   - Set `auth.token` to a secret string to protect the API (recommended). Set it
     to `""` to run the API open with no authentication.
   - `tls.certPath` can be left as `"lance-agent.pfx"` — the agent generates a
     self-signed certificate on first run if the file does not exist.
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

# Connect to all physical monitors
lance connect

# Connect to specific monitors only (1-indexed, comma-separated)
lance connect --monitors 1,3

# Connect with custom Moonlight flags (appended after the defaults; later flags win)
lance connect --monitors 1,2 --options "--fps 120 --bitrate 100000"

# Disconnect all active sessions (kills Moonlight, stops Apollo on the remote)
lance disconnect

# Disconnect specific slots only
lance disconnect --slots 1,2

# Disconnect but leave Apollo running on the remote (for quick reconnect)
lance disconnect --keep-running

# Stop Apollo and remove slot configs on the remote
lance disconnect --purge

# Low-level slot management
lance slots                    # list all slots
lance allocate <count>         # ensure the pool has exactly <count> slots
lance start <id>               # start a slot's Apollo instance
lance stop <id>                # stop a slot's Apollo instance
lance deallocate <id>          # remove a slot config (slot must be stopped)
lance force-deallocate <id>    # stop if running, then remove config

# Open Apollo's web config page for a slot in the browser
lance config <id>
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
  "logging": {
    "level": "Information",
    "filePath": "lance-agent.log",
    "retainDays": 7
  }
}
```

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

- **Monitor placement:** Moonlight has no CLI flag to open on a specific physical
  monitor. `--monitors` selects which streams to open and what resolution each
  requests; window placement is done manually.
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
