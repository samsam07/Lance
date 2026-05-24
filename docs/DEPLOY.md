# Lance — Deployment (Phase 1)

Phase 1 is a personal-use MVP. No installer, no service — just two binaries
dropped into folders and run manually.

## Prerequisites

**Remote machine (agent side)**
- Windows, running as Administrator
- Apollo (Sunshine fork) installed; the service **stopped** before running Lance
  (`sunshinesvc.exe` / `apollo.exe` watchdog must not be running — see
  `[DEFER-SVC]` in ARCHITECTURE.md)
- The `sunshine.conf` template config already set up (paired at least once with
  Moonlight so `file_state` carries the credentials)

**Local machine (client side)**
- Moonlight installed; `moonlight.exe` (Windows) or `moonlight` (Linux) on PATH
  or specified via `remoteClient.executable` in `lance.json`
- Network line-of-sight to the remote machine on port 9876 (or whichever port
  `lance-agent.json` configures)

## Build

Run from the repo root on a **Windows** machine (AOT requires the MSVC toolchain):

```
dotnet run scripts/dist.cs
```

Optional flag: `--keep-iis-artifacts` — retains `web.config` and
`staticwebassets` files in the agent dist (rarely needed).

Outputs:
| Path | Contents |
|---|---|
| `dist/lance-agent.zip` | Agent binary + sample config (deploy to remote) |
| `dist/client/` | Client binary + sample config (deploy to local machine) |

A Linux client build (`dist/client-linux/`) is produced when the script is run
on Linux.

## Agent deployment (remote machine)

1. Extract `lance-agent.zip` beside each other into a folder, e.g.
   `C:\Lance\agent\`.
2. Rename `lance-agent.json` (the sample) or edit it in place:
   - Set `remoteServer.installDir` and `remoteServer.configDir` to match your
     Apollo installation.
   - Set `listen.host` to `0.0.0.0` (listen on all interfaces) or a specific IP.
   - Adjust `logging.filePath` if you want logs elsewhere.
3. Stop the Apollo service (shortcut / `sunshinesvc.exe` watchdog) if running.
4. Run as Administrator:
   ```
   lance-agent.exe
   ```
   The agent logs to console and to `lance-agent.log` (rolling daily). On first
   run with no `lance-agent.json`, it starts with built-in defaults and warns.

## Client deployment (local machine)

1. Place `lance.exe` (or `lance` on Linux) in a convenient folder on PATH.
2. Copy `lance.json` from `dist/client/` beside the binary (or anywhere; use
   `--config <path>` to point to it explicitly).
3. Edit `lance.json`:
   - Set `agent.url` to `http://<remote-machine-ip>:9876`.
   - Adjust `remoteClient.executable` if Moonlight is not on PATH.
   - Tune `remoteClient.defaultFlags` (fps, codec, bitrate) for your setup.

## First run

```
# Check agent is reachable and slots are visible
lance slots

# Connect 2 monitors (allocates slots 0 and 1, starts them, launches 2 Moonlights)
lance connect --count 2

# Open Apollo config page for slot 1 in the browser
lance config 1
```

## Config file lookup order

The client finds the agent URL via (first match wins):
1. `--agent <url>` CLI flag
2. `--config <path>` → reads `agent.url` from that file
3. `lance.json` beside the `lance` binary → reads `agent.url`
4. Exit 7 if none of the above yield a URL

The agent always reads `lance-agent.json` beside the `lance-agent` binary; if
absent it runs with built-in defaults (logs a warning).
