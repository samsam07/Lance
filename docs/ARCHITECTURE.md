# Lance — Architecture

Lance is a cross-platform, two-component orchestration tool for easy, low-latency
multi-monitor remote desktop. It builds on **Apollo** (a Sunshine fork) and
**Moonlight**, managing their lifecycle so multi-monitor remote connections open
and close seamlessly.

Components:
- **Lance Agent** (`lance-agent`) — runs on the remote server, manages Apollo instances.
- **Lance Client** (`lance`) — runs on the host machine, manages Moonlight instances.

## Core concept

Multi-monitor remote connection works by running one Apollo instance per monitor
in parallel, each with a slightly different configuration. On the host, one
Moonlight instance is launched per monitor with matching config (resolution, etc.).

### Slot

A slot is a logical handle to a configured Apollo instance — concretely, the set
of Apollo config files needed to launch one instance.

- **Template slot (Slot 0):** the config installed by Apollo (`sunshine.conf`).
  Serves as the template for cloning. Always exists.
- **Slot cloning / allocation:** create a slot by cloning the template's config
  files into `sunshine_{id}.conf`. The template slot is never "allocated" (it
  always exists).
- **Slot deallocation:** remove a slot's config files. The template slot can
  never be deallocated.
- **Slot start:** launch an Apollo instance from the slot's config (→ "running slot").
- **Slot stop:** stop that slot's Apollo instance.

**Audio:** Slot 0 is the only slot with audio enabled. All clone slots have
`stream_audio = disabled` (cloning mutation rule). This is fixed for Phase 1 and 2.

**Slot connected state:** A running slot is either **open** (awaiting a Moonlight
client) or **connected** (has an active client). The agent derives this by
TCP-probing the slot's base port for any ESTABLISHED connection at query time.
No IP filtering is applied — a Moonlight client on the same machine connects via
loopback and is correctly detected. `SlotDto.Status` = `"Allocated"` | `"Running"`
| `"Connected"`. `[RESEARCH-1]` resolved.

## Lance Agent

The agent orchestrates the otherwise-manual work of running parallel Apollo
instances. It is:
- A Web API server exposing endpoints to Lance clients.
- Installed as a daemon / Windows service on the remote machine. *(Phase 2+;
  Phase 1 runs it as a plain process.)*

### Endpoints

**Slots** *(full request/response bodies and error codes live in SPEC — the
canonical contract; this list is the behavioral overview)*
- `GET /health` — liveness + agent info (version, uptime, max slots, template status).
- `GET /slots` — list all slots and their status.
- `POST /slots` — allocate slots to reach a target count. **Idempotent**
  (count=3 ensures 3 exist).
- `GET /slots/{id}` — slot detail (status, active client).
- `DELETE /slots/{id}` — deallocate; **refuses if running** (stop first).
- `POST /slots/{id}/force-deallocate` — stop if running, then deallocate.
- `POST /slots/{id}/start` — start an Apollo instance for the slot.
- `POST /slots/{id}/stop` — stop the slot's Apollo instance.
- `GET /slots/{id}/config` — link to the Apollo config page (slot must be
  running; `?redirect=1` supported).


### State management

Slot state is **not stored** by the agent — it is inferred from Apollo's config
files on disk. Slot 0 is `sunshine.conf`; clones are `sunshine_{id}.conf`. Slots
are a **pool where order is irrelevant except Slot 0**.

> **INVARIANT:** Slot id drives port math (`template_port − N×portStep`) **only
> for Lance-allocated standard slots**. Adopted/non-standard slots (id ≥1000,
> `IsAdopted`) carry an **observed** port; Lance never derives, recomputes, or
> mutates their port or config, and never starts/allocates/deallocates them
> (list + stop only).

*(Log paths and retention live in SPEC.)*

## Lance Client

A CLI launcher that starts one or more Moonlight instances against Apollo
instances. It asks an agent to prepare the environment for N monitors, **receives
slot info back (each slot's Apollo host + port), and launches one Moonlight per
slot using that slot's details**, then exits. The client is slot-aware — it does
no port math; the agent supplies every Apollo host:port.

> **Project layout:** `Lance.Agent` (ASP.NET Core), `Lance.Client` (console),
> `Lance.Shared` (DTOs + JSON source-gen contexts). `Lance.Shared` exists so the
> client never references ASP.NET Core. See SPEC for build details.

### Commands

**Slots** — mirror the agent slot endpoints.

**Status**
- `lance status` — unified view: slot states (Allocated / Running / Connected) +
  local Moonlight PIDs cross-referenced by slot port.

**Connect / Disconnect** *(Phase 2+)*
- `lance connect [--monitors <list>] [--options "<moonlight-options>"]`
  - `--monitors <list>` — comma-separated 1-indexed monitor IDs. Default: all
    physical monitors.
  - Moonlight passthrough examples: `--bitrate <kbps>`, `--video-codec
    <HEVC|H264|AV1>`, `--fps <n>`, `--resolution <WxH>`, etc.
- `lance disconnect [--slots <list>] [--keep-running] [--purge]`
  - `--slots <list>` — target specific slot IDs. Default: all running/connected slots.
  - `--keep-running` — skip stopping Apollo on the agent; Moonlight is still killed.
    Use case: disconnect the local session but leave remote Apollo running for quick reconnect.
    Mutually exclusive with `--purge`; `--purge` wins if both are given (warns).
  - `--purge` — stop Apollo, kill Moonlight, then deallocate the slot. Slot 0 excluded.
- `lance monitors` — list physical monitors on the local machine (ID, name, resolution,
  position, primary flag). No agent interaction. Used to pick IDs for `--monitors`.


## Flows

### connect (Phase 2+ shape)

Precondition: Moonlight executable exists; client can reach the agent.

1. **Resolve target monitors → ordered list (count N).** An invalid (out-of-range)
   monitor id → log, drop it, continue. A **duplicate** id → fast-fail (user input
   error). Position *i* in the list maps to slot *i* and supplies that slot's
   `--resolution` (see step 5). Default (no `--monitors`): all physical monitors.
2. **`GET /health` + `GET /slots`.** Count free slots (`Allocated` or `Running`,
   not `Connected`) and total slots. Compute available capacity = free +
   (maxSlots − total). If N > capacity → exit 2 `no_free_slots` (pool full,
   not enough free slots; user must disconnect first).
3. **`POST /slots` (count = N).** Allocate any missing slots so the pool reaches N.
   Idempotent if the pool is already large enough.
   *fails if:* allocation fails → log, abort (agent error).
4. **Ensure each target slot is up.** `Allocated` → `POST /slots/{id}/start`;
   already `Running`/`Connected` → reuse as-is. *fails if:* a slot fails to start
   → warn, drop it, continue (partial success).
5. **Launch Moonlight for each up slot that has no live local Moonlight.** Detect
   existing Moonlight instances for the slot using two methods in order (see
   Moonlight detection below); if any match → skip (no duplicate, enables
   reconnect). Otherwise launch
   `moonlight stream <host>:<port> Desktop [defaultFlags…] [--resolution <WxH>] [--options…]`.
   Per-monitor `--resolution` comes from the mapped monitor (the client requests it;
   Apollo's per-slot resolution is only a fallback). `--options` tokens are appended
   last so they win. *fails if:* a launch fails → warn, continue (partial success).
6. **Position each Moonlight window** (Windows; `[DEFER-LINUX-WINPOS]`). After all
   launches, placement tasks run in parallel: poll `Process.MainWindowHandle` for each
   PID (200 ms intervals, 5 s timeout); once non-zero, call `SetWindowPos` with
   `SWP_NOSIZE` to move the window's top-left to the target monitor's `(X, Y)` without
   resizing. SDL uses the window's current display (via `MonitorFromWindow`) when going
   fullscreen, so moving the origin to the target monitor is sufficient — the app
   handles fullscreen geometry itself. Failure to place is a warning, not an error.

Post-state: every up slot has a Moonlight (newly launched or pre-existing) on the
correct physical monitor; failed slots are logged and absent. The setup may be partial.

> **Failure policy — partial success.** Monitors are independent; 2 of 3 beats 0.
> Never roll back working slots.
>
> **Moonlight monitor placement** (Windows; Phase 2). Moonlight has no CLI flag for
> monitor selection. Lance moves each freshly launched window to the target monitor's
> top-left with `SetWindowPos(SWP_NOSIZE)`. SDL picks the display for fullscreen via
> `MonitorFromWindow` at the time the app transitions to fullscreen — the window's new
> position is sufficient for SDL to pick the correct display.
> `[DEFER-LINUX-WINPOS]` — Linux deferred to Phase 3.

### disconnect (Phase 2+)

Target: all `Running`/`Connected` slots, or only those in `--slots <list>` if specified.

For each target slot (best-effort; a failed step is logged, other slots proceed):
1. **Kill all matching Moonlight processes** (client): enumerate `moonlight`
   processes and match using the two-method detection below. Always done,
   regardless of flags. Multiple Moonlight clients per slot are all killed.
2. **`POST /slots/{id}/stop`** (agent). Skipped if `--keep-running`.
3. **`DELETE /slots/{id}`** (agent). Only if `--purge`; Slot 0 excluded.

**`--keep-running`:** skip step 2 (Apollo stays running on the remote). Step 1 still
executes — Moonlight is always killed. Use case: disconnect the session but leave
Apollo running for quick reconnect.

**`--purge`:** executes all three steps. Takes precedence over `--keep-running` if
both are given (client warns that `--keep-running` is ignored).

### agent startup (Phase 1)

On start the agent reconciles itself with reality before serving: validate config
(fail-fast), then **adopt any directly-launched `sunshine.exe` already running** —
attributing each to a slot by its launch command-line config path first (strong
signal), falling back to its bound port. A process matching neither (a
non-standard config) is adopted as a **non-standard slot** (reserved id ≥1000,
observed port, `IsAdopted`) that Lance may list and stop but never
start/allocate/deallocate. It then scans the Apollo config dir for the remaining
standard slots (template + `sunshine_{id}.conf`) and marks each Allocated (with
PID if adopted, else none). Slot state is always derived from disk + live
processes, never persisted.

> **Phase 1 prerequisite:** the user manually stops the Apollo *service*
> (`sunshinesvc.exe` watchdog + `apollo.exe`) before running Lance. Lance manages
> only its own direct `sunshine.exe` launches. Auto-managing the service is
> deferred — `[DEFER-SVC]`. Admin on Linux is untested — `[VERIFY-APOLLO]`.

### Moonlight instance detection

To find Moonlight processes associated with a slot, the client applies two
methods in order. A process matched by either is considered owned by that slot.
Multiple matches are all returned (more than one Moonlight client per slot is
supported).

1. **Command-line `host:port` match** — find all `moonlight` processes whose
   command line contains `<SlotDto.Host>:<SlotDto.Port>`. Covers every instance
   launched by Lance and any manually launched instance that was given the
   explicit host:port argument.
2. **Window title prefix match** — for processes not matched by method 1, check
   whether the process's main window title starts with `"<slot.Name> - "` (e.g.
   `"Lance-1 - "`). Covers manually launched Moonlight instances that connected
   to the slot without an explicit host:port on the command line. Window title
   format confirmed as `"<sunshine_name> - Moonlight"`.

`[DEFER-LINUX-WINDETECT]` — Window title detection (method 2) is **Windows-only
in Phase 2**. On Linux, `Process.MainWindowTitle` is not supported without X11
tooling. Method 1 (command-line match) still works on Linux. Full Linux window
title support deferred to **Phase 3** (options: `wmctrl -lp` or X11 P/Invoke).

### Phase 1 connect (no sessions)

Phase 1 has no session layer, so connect is the simpler client-driven sequence:
ensure N slots allocated, start them, launch N Moonlights. Partial success +
warn on any failed slot. **Fail-fast: no interactive prompts.** Either it works
per-slot or that slot is skipped with a warning.

N is supplied by the user via `--count <N>` (Phase-1 temporary flag). Phase 2
replaces this with `--monitors <list>` — see SPEC for the full note.

## Notes / open items
- `[RESEARCH-1]` **Resolved.** TCP probe on the slot's base port (ESTABLISHED from
  a remote IP) is the detection mechanism. Agent probes at query time.
- `[DEFER-1]` **Closed.** Moot without sessions — slot 0 is the audio slot; no
  multi-session conflict is possible.
- `[INVESTIGATE-STOP]` **Resolved (Phase 2 Slice 1).** `CloseMainWindow()` returns
  `false` on Apollo's tray/headless process — the graceful wait was always wasted.
  Fix: check the return value; if `false`, skip the wait and call `Kill()` directly.
- `[DEFER-PATHS]` — All default file paths (agent config, TLS cert, log file; client
  config) follow Windows / "run from folder" conventions and are non-standard on
  Linux. `lance-agent.pfx`, `lance-agent.json`, and `lance-agent.log` resolve beside
  the binary rather than under `/etc/`, `/var/lib/`, `/var/log/`, or `~/.config/`.
  Agent paths: revisit when the daemon/service install is added (Phase 4). Client
  config: XDG compliance (Phase 3). Full table in SPEC.md `[DEFER-PATHS]`.
- `[DEFER-LINUX-WINDETECT]` **Phase 3** — Linux window title detection for
  Moonlight instance matching (method 2). Windows uses `Process.MainWindowTitle`;
  Linux needs `wmctrl -lp` or X11 P/Invoke. Method 1 (command-line host:port)
  still covers Lance-launched instances on Linux. See "Moonlight instance
  detection" above.
- `[DEFER-LINUX-WINPOS]` **Phase 3** — Moonlight monitor placement on Linux.
  Windows uses `SetWindowPos(SWP_NOSIZE)` to move the window origin to the target
  monitor; SDL then fullscreens on the correct display. Linux equivalent: `wmctrl
  -r <title> -e 0,X,Y,-1,-1` or X11 `XMoveWindow` to reposition, same principle.
- `[RESEARCH-VDISPLAY]` **Phase 3 Slice 2** — Remote physical display management.
  **Goal:** when a Lance session connects, the remote machine's physical displays
  should turn off or go dark (RDP/MSTSC-like), and the host drives virtual displays
  only. **Current state:** Slot 0 is manually configured in Apollo to *not* create
  virtual screens — this is intentional: UAC dialogs and other Windows system UI
  render on the physical display stack, so Slot 0 must stay physical. Clone slots
  do create virtual displays. The result is that the remote's physical screens stay
  active and mirror the host session. **Research questions:** Can physical displays
  be disabled programmatically on Windows (e.g. `SetDisplayConfig`, DDC/CI, or an
  Apollo hook) and reliably re-enabled on disconnect? Does disabling them break UAC
  or other system UI? Is this agent-triggered (at `POST /slots/{id}/start`) or
  Apollo-configured? Does behaviour differ across Windows 10 vs 11? What is the
  recovery path if Lance crashes with displays left disabled? **Input blocking
  (vague — needs scoping):** also research whether Lance should disable keyboard,
  mouse, and trackpad on the remote while a session is active (`BlockInput` Win32
  API or filter-driver approach). Open questions: opt-in or always-on? UAC secure
  desktop bypass? Lockout risk if Lance crashes? Research display and input
  management together; spec both before implementing either. Findings → document
  here; implementation phase TBD.
- `[DEFER-PAIR-AUTO]` **Phase 4** — Automated slot pairing. Currently each clone
  slot must be paired with Moonlight manually once before first use (each clone
  has its own `file_state` / unique UUID). Proposed automation: when cloning,
  Lance reads Slot 0's `sunshine_state.json`, copies its `certs` array (the
  paired client certificates) into the clone's state file, and generates a fresh
  `uniqueid`. If Apollo's pairing endpoint skips the PIN when the connecting
  client's cert is already in the `certs` list, no manual pairing step is needed
  at all. **One verification required before implementation:** confirm that
  Apollo's `/pair` handler short-circuits (returns success) when the client cert
  is already present, rather than unconditionally triggering the full PIN flow
  for any new UUID.
- **Auth (Phase 2):** agent optionally enforces a static bearer token. If
  `auth.token` is set in `lance-agent.json`, all non-`/health` requests must
  carry `Authorization: Bearer <token>`. If absent, the API is open. Client
  sends the token via `agent.token` in `lance.json` or `--token` CLI flag
  (flag wins). TLS cert validation is unconditionally disabled on the client
  in Phase 2 (self-signed cert); it will become configurable when PEM support
  is added.
