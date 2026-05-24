# Lance

Lance is a personal tool for seamless multi-monitor remote desktop. It runs one
[Apollo](https://github.com/apolloapp-io/apollo) instance per monitor in
parallel and opens one [Moonlight](https://moonlight-stream.org) window per
monitor — making multi-monitor remote connections open and close in one command.

**Status: Phase 1 MVP** — end-to-end orchestration works. No auth, no sessions,
no service install. See [docs/PLAN.md](docs/PLAN.md) for the roadmap.

---

## How it works

```
lance connect --count 2
```

1. Asks the **agent** (running on the remote machine) to allocate and start 2
   Apollo slots.
2. Launches 2 Moonlight windows on the local machine — one per slot.

The agent manages Apollo instances; the client manages Moonlight instances.
Each slot is an independent Apollo config cloned from the template, with a
unique port.

---

## Components

| Binary | Role | Runs on |
|---|---|---|
| `lance-agent` | Web API that manages Apollo instances | Remote machine (Windows) |
| `lance` | CLI that orchestrates the connection | Local machine (Win/Linux) |

---

## Build

Requires .NET 10 SDK and the MSVC toolchain (for AOT). Run from the repo root
on a **Windows** machine:

```
dotnet run scripts/dist.cs
```

Outputs:
- `dist/lance-agent.zip` — agent binary + sample config
- `dist/client/lance.exe` — client binary

---

## Quick start

### Agent (remote machine)

1. Extract `lance-agent.zip` into a folder, e.g. `C:\Lance\agent\`.
2. Edit `lance-agent.json` — set `remoteServer.installDir` and
   `remoteServer.configDir` to your Apollo installation paths.
3. Stop the Apollo service (the `sunshinesvc.exe` watchdog must not be running).
4. Run as Administrator:
   ```
   lance-agent.exe
   ```

### Client (local machine)

1. Place `lance.exe` somewhere on your PATH.
2. Create `lance.json` beside it (copy from `samples/lance.json`):
   ```json
   {
     "agent": { "url": "http://<remote-ip>:9876" }
   }
   ```

### Commands

```
lance slots                     # list all slots and their status
lance status                    # same as slots (Phase 1)
lance connect --count <N>       # allocate, start N slots, launch N Moonlights
lance config <slot-id>          # open Apollo config page in browser

# Global options (all commands)
lance --agent <url> <command>   # override agent URL for this invocation
lance --config <path> <command> # use a specific lance.json
lance --verbose <command>       # enable debug logging to stderr
lance --no-color <command>      # disable ANSI color output
```

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Generic error |
| 2 | Another instance already running |
| 3 | Agent unreachable |
| 4 | Agent returned an error |
| 5 | Moonlight launch failed |
| 6 | Slot not in required state |
| 7 | Agent URL could not be resolved |

---

## Configuration

### Agent — `lance-agent.json` (beside `lance-agent.exe`)

```json
{
  "listen":       { "host": "0.0.0.0", "port": 9876 },
  "remoteServer": { "installDir": "C:\\Program Files\\Apollo",
                    "configDir":  "C:\\Program Files\\Apollo\\config",
                    "executable": "sunshine.exe",
                    "templateConfigName": "sunshine.conf",
                    "startupTimeoutSeconds": 30 },
  "slots":        { "maxCount": 8, "portStep": 1000, "stopTimeoutSeconds": 10 },
  "logging":      { "level": "Information", "filePath": "lance-agent.log" }
}
```

Missing file → runs with built-in defaults (a warning is logged).

### Client — `lance.json` (beside `lance.exe`, or use `--config <path>`)

```json
{
  "agent":        { "url": "http://<agent-host>:9876", "timeoutSeconds": 30 },
  "remoteClient": { "executable": "moonlight.exe",
                    "defaultFlags": ["--fps", "60", "--video-codec", "HEVC",
                                     "--bitrate", "80000", "--no-vsync"] },
  "logging":      { "level": "Information", "filePath": null }
}
```

Missing file → all defaults apply; `--agent <url>` is required to reach the
agent.

Full samples in [`samples/`](samples/).

---

## Docs

| File | Contents |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | System design and behavioral invariants |
| [docs/SPEC.md](docs/SPEC.md) | Verified facts: ports, DTOs, API contract, config shapes |
| [docs/PLAN.md](docs/PLAN.md) | Phase breakdown and slice history |
| [docs/CONVENTIONS.md](docs/CONVENTIONS.md) | Code style rules |
| [docs/DEPLOY.md](docs/DEPLOY.md) | Step-by-step deployment guide |
