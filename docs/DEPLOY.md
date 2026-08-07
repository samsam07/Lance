# Lance — Deployment (Phase 1)

Phase 1 is a personal-use MVP. No installer, no service — just two binaries
dropped into folders and run manually.

## Prerequisites

**Remote machine (agent side)**
- Windows, running as Administrator
- Apollo (Sunshine fork) installed; the service **stopped** before running Lance
  (`sunshinesvc.exe` / `apollo.exe` watchdog must not be running — see
  `[DEFER-SVC]` in ARCHITECTURE.md)
- The `sunshine.conf` template config already set up and paired at least once
  with Moonlight (Slot 0). Each additional slot must be paired individually
  after first start — see the Pairing section below.

**Local machine (client side)**
- Moonlight installed; `moonlight.exe` (Windows) or `moonlight` (Linux) on PATH
  or specified via `remoteClient.executable` in `lance.json`
- Network line-of-sight to the remote machine on port 9876 (or whichever port
  `lance-agent.json` configures)

## Build

Run from the repo root on a **Windows** machine (AOT requires the MSVC toolchain):

```
make publish
```

Optional: `make publish-keep-iis` — retains `web.config` and `staticwebassets`
files in the agent dist (rarely needed). Or invoke directly:
`dotnet run scripts/publish.cs [--keep-iis-artifacts]`.

Outputs:
| Path | Contents |
|---|---|
| `dist/agent/` | Agent binary + sample config (deploy to remote) |
| `dist/client/` | Client binary + sample config (deploy to local machine) |

A Linux client build (`dist/client-linux/`) is produced when the script is run
on Linux.

## Agent deployment (remote machine)

1. Copy `dist/agent/` to a folder on the remote machine, e.g. `C:\Lance\agent\`.
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
   - Tune `remoteClient.defaultOptions` (codec, capture behaviour) for your setup.

## First run

```
# Check agent is reachable and slots are visible
lance slots

# Connect 2 monitors (allocates slots 0 and 1, starts them, launches 2 Moonlights)
lance connect --count 2

# Open Apollo config page for slot 1 in the browser
lance config 1
```

## Pairing slots

Each clone slot has its own Apollo identity (unique `file_state`) and must be
paired with Moonlight once before it can stream. Slot 0 is already paired from
your initial Apollo setup. For each additional slot (1, 2, …):

1. Start the slot: `lance start <id>`
2. Open Moonlight on the local machine, add a new host: `<remote-ip>:<slot-port>`
   (slot ports: slot 1 = template port − 1000, slot 2 = template port − 2000, …;
   run `lance slots` to see each slot's port).
3. Complete the PIN pairing flow.
4. Stop the slot: `lance stop <id>`

This only needs doing once per slot. After pairing, `lance connect` drives the
session normally.

## Config file lookup order

The client finds the agent URL via (first match wins):
1. `--agent <url>` CLI flag
2. `--config <path>` → reads `agent.url` from that file
3. `lance.json` beside the `lance` binary → reads `agent.url`
4. Exit 7 if none of the above yield a URL

The agent always reads `lance-agent.json` beside the `lance-agent` binary; if
absent it runs with built-in defaults (logs a warning).
