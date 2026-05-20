# Lance

A multi-monitor orchestration tool for [Apollo](https://github.com/ClassicOldSong/Apollo) (Sunshine fork) + [Moonlight](https://moonlight-stream.org/), built to provide RDP-like multi-monitor remote desktop with hardware-accelerated low latency.

**Status:** Phase 1 (MVP). Personal use.

## Why

Linux RDP clients (FreeRDP, Remmina) suffer from jitter and freezes on multi-monitor setups, especially with mixed DPI and 4K displays. The decode path on Linux is fragile (VAAPI/NVIDIA bridge, software fallback, etc.). Apollo + Moonlight has a much cleaner hardware path (vendor encoders on the server, NVDEC/VAAPI on the client), but vanilla Apollo + Moonlight don't handle multi-instance multi-monitor cleanly — every additional monitor requires manually configuring a new Apollo instance, managing ports, and launching matching Moonlight windows.

Lance orchestrates that.

## Architecture

Two components:

- **`lance-agent`** runs on the Windows machine alongside Apollo. HTTPS service that clones Apollo configs, manages Apollo processes, and exposes them via an API. (Phase 1: Windows only. Cross-platform in Phase 3.)
- **`lance`** runs on your client machine (Linux or Windows). CLI tool that talks to the agent and spawns Moonlight instances. (Phase 1: Linux + Windows.)

```
Client machine                          Server machine (Windows)
──────────────                          ────────────────────────
[ lance CLI ]  ─── HTTPS/JSON ───────►  [ lance-agent ]
     │                                       │
     │ spawns                                │ manages
     ▼                                       ▼
[ moonlight ]  ◄─── Apollo stream ───  [ apollo (slot 0) ]
[ moonlight ]  ◄─── Apollo stream ───  [ apollo (slot 1) ]
[ moonlight ]  ◄─── Apollo stream ───  [ apollo (slot 2) ]
```

Lance uses your existing `sunshine.conf` as slot 0 (the template). For N monitors, Lance uses slots 0..(N-1) — three monitors means three Apollo instances total (not four).

## Important: Lance owns Apollo's lifecycle

Once `lance-agent` is running, **it manages Apollo for you**. On startup, the agent kills any existing Apollo processes. Don't start Apollo manually alongside Lance — let Lance do it via `lance connect` or the slot API.

In Phase 1, restarting the agent during an active session will require reconnecting (process adoption comes in Phase 2).

## Prerequisites

**On the server (Windows):**

- Apollo installed and working
- A virtual display driver (Apollo's bundled SudoVDA recommended)
- Apollo's default `sunshine.conf` configured with:
  - Headless Mode enabled
  - Adapter Name set to your encoder GPU (e.g., "Intel(R) Iris(R) Xe Graphics")
  - Audio working
- Apollo paired with your Moonlight client at least once via the web UI

**On the client (Linux or Windows):**

- Moonlight installed:
  - Linux: `moonlight-embedded` (CLI-friendly)
  - Windows: `moonlight-qt` (or moonlight-embedded if available)
- Working GPU decode (NVDEC for NVIDIA, VAAPI for Intel/AMD)

## Setup

### One-time Apollo setup (Windows)

1. Install Apollo on the Windows machine.
2. Open Apollo web UI (https://localhost:47990 by default).
3. Configure:
   - **Audio/Video → Headless Mode:** enabled
   - **Audio/Video → Adapter Name:** your encoder GPU
   - **Audio/Video → Display Device Configuration:** "Verify that display is enabled" or "Activate the display automatically"
4. From your client machine, run Moonlight, add the Windows machine, complete pairing (enter the PIN in Apollo's web UI).
5. Test a connection works end-to-end with the default Apollo instance.

Do not skip this. Lance does not handle initial pairing — it inherits pairing by cloning Apollo's state file.

### Install `lance-agent` (Windows server)

1. Place `lance-agent.exe` in a directory of your choice (e.g., `C:\Program Files\Lance\`).
2. Create `lance-agent.json` beside it with your settings:
   ```json
   {
     "listen": { "host": "0.0.0.0", "port": 9876 },
     "auth": { "token": "GENERATE_A_LONG_RANDOM_STRING_HERE" },
     "apollo": {
       "install_dir": "C:\\Program Files\\Apollo",
       "config_dir": "C:\\Program Files\\Apollo\\config"
     }
   }
   ```
3. Allow inbound TCP 9876 in Windows Firewall.
4. Run `lance-agent.exe` in a console. It will stay in the foreground; close the console (or Ctrl+C) to stop. On graceful shutdown, all managed Apollo processes are stopped.

The agent uses HTTPS with a self-signed certificate. The certificate is auto-generated on first run and cached beside the binary.

### Install `lance` (client — Linux or Windows)

**Linux:**

1. Place `lance` somewhere in your PATH (e.g., `~/.local/bin/lance`).
2. Optionally create a config file. Choices:
   - Beside the binary: `lance.json` (portable mode)
   - Default location: `~/.config/lance/config.json`
   - No config — just use inline args every time
3. Sample config:
   ```json
   {
     "agent": {
       "url": "https://192.168.1.100:9876",
       "token": "SAME_TOKEN_AS_AGENT"
     },
     "moonlight": {
       "executable": "moonlight"
     }
   }
   ```
4. Verify connectivity: `lance status`.

**Windows:**

1. Place `lance.exe` somewhere in your PATH or in a portable folder.
2. Optionally create a config file. Choices:
   - Beside the binary: `lance.json` (portable mode)
   - Default location: `%APPDATA%\Lance\config.json`
   - No config — just use inline args every time
3. Sample config:
   ```json
   {
     "agent": {
       "url": "https://192.168.1.100:9876",
       "token": "SAME_TOKEN_AS_AGENT"
     },
     "moonlight": {
       "executable": "C:\\Program Files\\Moonlight Game Streaming\\moonlight.exe"
     }
   }
   ```
4. Verify connectivity: `lance status`.

**Note on certificate warnings:** In Phase 1, the `lance` client ignores certificate errors when talking to `lance-agent`. The agent uses a self-signed cert. Phase 3 will add proper cert handling.

## Usage

```bash
# Connect on all monitors (one Moonlight window per physical monitor)
# Uses config file for agent URL and token
lance connect

# Connect on specific monitors (1-indexed physical client monitors)
lance connect --monitors 1,2

# Connect with inline target (like mstsc) — no config file needed
lance connect 192.168.1.25 --token your_token_here

# Mix: inline target, config file for token
lance connect 192.168.1.26

# Use a specific config file
lance connect --config ~/work-pc.json

# Override stream settings
lance connect --bitrate 100000 --codec hevc --fps 60

# Check what's running
lance status

# List slots on the agent (slot 0 is the template, used on every connect)
lance slots

# Open a slot's Apollo web UI in browser (requires slot to be running)
lance config 1

# End the session, stop Apollo, keep slot configs for fast reconnect
lance disconnect

# End the session, keep Apollo running (e.g., stepping away briefly)
lance disconnect --keep-running

# End the session and remove slot configs (slot 0 / template is never removed)
lance disconnect --purge
```

## How slots are numbered

- Slot 0 = your existing `sunshine.conf` (the template). Always exists, can't be deleted.
- Slots 1, 2, 3, ... = clones Lance creates when you connect with multiple monitors.
- `lance connect` with N monitors uses slots 0..(N-1). So one monitor uses slot 0, two monitors use slots 0+1, three monitors use slots 0+1+2.

The user's pre-paired `sunshine.conf` becomes the streaming instance for the first monitor on every connect.

## Token rotation

In Phase 1, to change the auth token:

1. Stop `lance` (if running) and `lance-agent`
2. Edit both `lance-agent.json` and the client config to use the new token
3. Restart `lance-agent`
4. Reconnect with `lance`

Phase 3 will add a smoother rotation flow.

## Roadmap

See `PLAN.md`. Currently in Phase 1 (MVP).

## License

TBD.
