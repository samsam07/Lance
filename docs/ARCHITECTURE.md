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

**Display:** All slots run Apollo in **headless mode** — each running instance
creates and drives its own virtual display. No physical display on the remote is
captured or mirrored. (`headless_mode` is inherited verbatim from the headless
template, so every clone is headless too.)

**Slot connected state:** A running slot is either **open** (awaiting a Moonlight
client) or **connected** (has an active client). The agent derives this at query
time from **UDP endpoint presence**: a slot is `Connected` when its Apollo process
owns any of its streaming UDP ports (base port + fixed offsets — see "Sessions &
tool orchestration → detection"). `SlotDto.Status` = `"Allocated"` | `"Running"` |
`"Connected"`.

> **`[VALIDATE-UDP]` resolved (2026-07-11).** Validated against a live Apollo +
> Moonlight stream by reading Lance's own logs. Confirmed: (1) the stream's UDP
> ports are `base+9 / +10 / +11` (video/control/audio), owned by the Apollo process
> itself (no child process); (2) connect is detected within one ~1s poll of the
> stream actually starting; (3) an ungraceful teardown — which includes Lance's own
> `disconnect` (it kills Moonlight, sending no stream-teardown) *and* a hard NIC-cut
> — is detected ~6–7s later (Apollo's client-timeout). **The earlier TCP-ESTABLISHED
> probe was retired:** during a live stream Apollo's TCP base port stays in `Listen`,
> so that probe reported `Running` the whole time and never once reported
> `Connected`. UDP endpoint presence is now the sole mechanism.

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

> **Phase 3 supersedes the exit-and-done shape below.** With sessions, `lance
> connect` becomes a **foreground daemon** that blocks until the session ends
> (mint/accept `--session-id`, session handshake, launch Moonlights as owned
> children, raise `session_started`, then block watching them). The slot
> resolution and per-monitor launch mechanics here are unchanged and reused; what
> changes is that connect no longer returns after launching. See "Sessions & tool
> orchestration" below for the full daemon behavior.

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

> **Phase 3 reshapes this around sessions.** `lance disconnect [--session-id X]`
> becomes the primary form: kill the session's Moonlights (the blocking `lance
> connect` daemon then reacts to its children dying and runs `session_ended`), with
> the agent as the fast path for resolving the session's slots and a `host:port`
> CLI fallback when the agent is unreachable. **The `--keep-running` / `--purge`
> flags are retained on top of the session model** (owner decision): default leaves
> Apollo running for fast reconnect; `--purge` additionally stops+deallocates the
> session's slots (Slot 0 excluded). The per-slot best-effort mechanics below are
> reused. See "Sessions & tool orchestration" below.

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

## Sessions & tool orchestration (Phase 3)

> Behavior source of truth for the session/event/hook subsystem. Concrete values
> (state names, `source` enum, env-var names, grace window, hook JSON schema,
> record path, endpoints) live in SPEC. The original design rationale lives in
> `docs/TOOL_ORCHESTRATION_SPEC.md`, now integrated here. This subsystem is the
> major body of Phase 3 (see PLAN Slice 6); it supersedes the earlier *tentative*
> session-layer sketch.

**Why.** Sidecar tools (`vox` mic backchannel, future `clipline`, keystroke relay)
fill gaps vanilla Apollo/Moonlight leave. Running them needs coordinated setup on
connect and teardown on disconnect, on **both** machines. Lance owns this
orchestration; Apollo becomes a managed backend, not the orchestrator. Lance
delivers **events**; **tools own their own process lifecycle**. Lance's sole
guarantee is that `session_ended` is eventually dispatched on each side.

**No cross-machine event bus.** Events are raised *locally* on the side that
detects them; each side runs its own hooks from its own config. Nothing about an
event propagates over the wire. The wire carries only coordination (connect
handshake + optional clean-disconnect ping). This is a per-side **event
dispatcher** (raise → match hooks → execute), not a shared bus.

### Session

A **session** is one `lance connect` invocation, scoped to one client machine,
grouping the slots that invocation acquired. Sessions are the unit hooks bind to.

- A session begins **only** at `lance connect`. `lance allocate` / `lance start`
  provision slots and Apollo instances but create **no session** and raise **no
  events**. Slot occupancy and sessions are independent on the agent.
- A second concurrent `lance connect` on the same machine is a **separate
  session** (new id, own slots, own lifecycle), not a join.
- Multi-monitor (`--monitors 1,3`): one slot per monitor, all in the one session.
  Session-tier events fire **once**; per-slot events (if any) fire per slot.

**Session id.** Client-minted, or overridden via `--session-id`; sent on the
connect handshake. The agent vets it for **global uniqueness across all active
sessions** — a collision **refuses the connection** (client surfaces the error and
stops, no silent retry). Free id → agent reserves it, allocates, proceeds.

**Agent-side state machine** (`Provisioned → Connected → Ended`):
- `Provisioned` — slots allocated, no stream yet (enters on successful handshake +
  allocation).
- `Connected` — ≥1 slot's stream is live (enters when the first slot is detected
  connected — see detection).
- `Ended` — teardown ran, record deleted (enters on any `session_ended` source).
- **Provision grace window (default 30s):** a `Provisioned` session with no slot
  connected within the window → `session_ended(source=provision_timeout)`. This
  distinguishes "not yet connected" from "was connected, now gone" — both
  otherwise look like "all slots idle."
- **Slots are freed only at `session_ended`.** Held through degraded operation; no
  mid-session slot release (freeing mid-session risks another client grabbing them
  while this one is degraded-but-alive).

### Events

Four events, raised **locally per side**, never propagated. Naming
`<subject>_<past-verb>`.

| Event | Tier | Raised by |
|---|---|---|
| `session_started` | session | client (after launching Moonlights); agent (after allocation, before responding go) |
| `session_ended` | session | client (last Moonlight gone / Ctrl-C / SIGHUP); agent (probe-watch, ping, reconcile, or provision_timeout) |
| `slot_connected` | slot | agent only |
| `slot_disconnected` | slot | agent only |

- **Slot-tier is agent-only in v1**, and has no consumer yet — client hooks bind
  session-tier only. (The client still watches each Moonlight *process* internally
  to know when the last one dies; it does not know when a *stream* established —
  only the agent's probe does.)
- Both sides reach `session_ended` **independently** from their own signals;
  neither waits for the other. This is what lets the agent restore host state when
  the client machine is dead and unreachable.
- The event payload is injected as environment variables into every spawned hook
  process, and the same set backs `${VAR}` substitution in hook `args[]` (see
  SPEC for the variable list and the `source` enum).

### Client daemon

`lance connect` runs in the **foreground, blocking until the session ends**. There
is no `--detached` flag.

- **Signal handling:** trap `SIGHUP` / console-close and run graceful teardown
  (tree-kill children, run `session_ended` hooks) before exit. Load-bearing —
  foreground-only means terminal close is a normal exit path.
- **Process ownership:** Moonlights are launched as children.
  - **Windows:** placed in a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
    → daemon dies for any reason → OS kills all Moonlights. Hook-spawned processes
    also stay in the job (no breakaway) and die with it; acceptable because
    client-side tools do no host-state changes to restore.
  - **Linux:** no Job Object. Graceful exit uses `Process.Kill(entireProcessTree:
    true)` (also used on Windows for clean exits). Hard daemon death (SIGKILL,
    power loss) **orphans** Moonlights; self-heals when the user kills the stray
    stream (agent then detects teardown). No client-side reaper, no systemd
    dependency in v1.
- **Degraded launch** (`--monitors 1,3` launches multiple Moonlights):
  - ≥1 launched → **proceed degraded**: log the failure, keep live streams, raise
    `session_started`, run hooks.
  - 0 launched → **fail**: send the clean-disconnect ping, run **no** client hooks,
    exit nonzero.
  - Slots allocated but never connected are **held until `session_ended`**.
- **Disconnect** is a separate, process-level invocation. There is **no IPC** to
  the blocking `lance connect`; disconnect kills the Moonlights and the daemon
  reacts to its children dying (raises `session_ended`, runs hooks, exits).
  Pre-teardown hooks (running while the stream is still up) are **unsupported in
  v1** (future). `--keep-running` / `--purge` are honored as above.
  - `--session-id X`: ask the agent for that session's slots, match Moonlights by
    those slots' `host:port` on the local process list, kill them.
  - **Fallback (agent unreachable):** kill by explicit `host:port` CLI args. Agent
    is the fast path; CLI args the degraded path.
  - no id: kill all sessions' Moonlights.

### Detection (agent-side)

The agent must detect teardown **independently of the client** — a crashed client
sends nothing. **Probe-watch is authoritative; the clean-disconnect ping is a
latency optimization on top.**

- **Liveness signal — UDP endpoint presence** (`[VALIDATE-UDP]`, see the slot
  connected-state note above and Slice 1): a slot is **connected iff its Apollo
  process currently owns UDP endpoints at that slot's streaming ports.**
- **Port resolution per slot:** (1) ports explicitly present in the cloned config
  win (handles manual edits); (2) absent ports are computed from the slot's base
  port via Apollo's fixed base+offset map; (3) the map lives in the **host-adapter
  seam** — one table, swappable for Sunshine or another host; the probe logic
  stays host-agnostic. The probe scopes by **owning PID AND resolved ports**, so
  slots don't read each other's endpoints and unrelated processes are excluded.
- **Measured latency** (validated 2026-07-11 against live Apollo; observed, not
  contractual): a client **connect** is detected within one ~1s poll of the stream
  starting. **Disconnect is ~6–7s whenever the teardown is ungraceful** — which
  includes both a hard NIC-cut *and* Lance's own `disconnect`, since it kills
  Moonlight and no stream-teardown reaches Apollo; Apollo waits its client-timeout
  before releasing the UDP ports. A truly graceful Moonlight stream-quit would be
  ~1s. **Consequence: the clean-disconnect ping is the only fast disconnect path for
  Lance** — probe-watch (~6–7s) is the crash backstop, not the common-case fast path.
- **Validation requirement:** Lance must detect UDP endpoint presence itself and
  **log probe state transitions** (connected/disconnected, timestamps, source).
  Validation = reading Lance's logs during connect/disconnect and a hard-cut test,
  **not** manual `Get-NetUDPEndpoint`/netstat. Confirm logged detection matches the
  ~1s / ~6–7s behavior before relying on probe-watch in production.
- **States drive detection:** `Provisioned` with no endpoints within the grace
  window → `session_ended(provision_timeout)`; `Connected` with all slots'
  endpoints gone → `session_ended(probe_watch)`.

### Hooks

- **Discovery.** Client: `--hook <path>` (repeatable, additive) plus a client
  config `hooks: [{ active, path }]` array (`--hook` overrides/adds on top). Agent:
  `lance-agent.json` `hooks: [{ active, path }]`.
- **Format:** JSON (see SPEC for the schema + field defaults). `command` + `args[]`
  are passed directly to `ProcessStartInfo.ArgumentList` — **no shell**, no string
  parsing, no quoting hazards. `${VAR}` in `args[]` is resolved by Lance before
  spawn (there is no shell to expand it).
- **Ordering:** `priority` orders **files** bound to the same event (lower first;
  ties → load order); within a file, `commands` run in array order.
- **Process lifecycle is the tool's, not Lance's** (explicit non-goal). Lance never
  supervises hook-spawned processes; its only relationship to a spawned process is
  optionally waiting for its exit to sequence subsequent commands. Consequences: a
  tool whose teardown must kill a process must make it **findable** (write a
  pidfile at launch, or use a wrapper that tracks the PID). A **wrapper** (e.g.
  `audiohelper`) is a setup/teardown verb-bundle (ordered setup, records the PID,
  reverses on teardown); it is not required to be resident and does not watch
  Lance's liveness (crash teardown is handled by reconciliation).

### Crash recovery (agent-side)

Covers **lance-agent crashing mid-session** while host state is modified (audio
switched, `vox` running). Apollo survives an agent crash by design, so the agent
on restart must finish any teardown that never ran. Client-side crash recovery is
**out of scope for v1** (client residue is a stray process with no host-state
change; severity low).

- **Record lifecycle:** persist the session record **BEFORE** running
  `session_started` hooks; **delete** it **AFTER** `session_ended` hooks complete.
  Invariant: **a record present at agent startup means teardown never ran.** The
  record holds the **resolved teardown command list** and the **env payload
  snapshot** (see SPEC for path + contents; written atomically temp+rename).
- **On startup (reconciliation), before accepting any new connections:** for each
  surviving record, probe its slots — any slot connected → session alive, re-adopt,
  do **not** replay; all idle → orphan, raise `session_ended(reconcile)`, run the
  **snapshotted** teardown commands, delete the record. Reconciliation **must
  complete before the listener opens**, else a fresh connect could switch audio and
  then be clobbered by a replayed `restore`.
- **Rules:** snapshot commands+env, **never re-read the hook file at replay** (it
  may have changed; `LANCE_CLIENT_IP` can't be recomputed once the client is gone).
  At replay, `LANCE_EVENT` / `LANCE_EVENT_SOURCE` are set **fresh**
  (`session_ended` / `reconcile`) so a hook can tell a replayed teardown from a
  live one. **Teardown commands must be idempotent** (author requirement) — this is
  what makes a mid-chain `terminate` abort safe to clean up later. The agent
  **never job-kills Apollo** (Apollo must survive an agent crash — the premise of
  this section; only the client jobs its Moonlights). Only `session_ended` is
  replayed; slot-tier hooks are never replayed.
- **Accepted gap:** if the agent crashes and **never restarts**, host state stays
  modified. The Windows service auto-restart (Phase 4) makes this rare; no
  watchdog-of-watchdog in v1.

### Wire protocol

Existing REST/HTTPS (self-signed, bearer token), extended. **No persistent
connection anywhere** — the "maintain an active connection for the session" goal is
dropped; unnecessary given local event dispatch + probe-based detection.

- **Connect handshake** (client → agent): a **new `POST /sessions` endpoint**
  (decided) — allocate slots, vet the id (global uniqueness; collision → refuse),
  **persist the record**, run agent `session_started` hooks, respond go with the
  allocated slot set. `POST /slots` stays allocation-only and **never creates a
  session**; the two are independent (§2). Request/response body finalized at Slice
  6.4.
- **Clean-disconnect ping** (client → agent): `DELETE /sessions/{id}` shape.
  Fast-path only, not required for correctness (probe-watch backstops it).
- **Sequencing:** the agent's `session_started` hooks complete before the client's;
  the agent's `session_ended` hooks may run after the client's. This ordering is
  inherent to who detects what; no cross-machine barrier exists or is needed.

## Notes / open items
- `[RESEARCH-1]` **Superseded by `[VALIDATE-UDP]` (2026-07-11).** The original TCP
  ESTABLISHED probe never fires during a live stream (TCP base port stays in
  `Listen`); connection detection is now **UDP endpoint presence**, and the TCP probe
  was retired.
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
- `[VALIDATE-UDP]` **Resolved (Slice 6.1, 2026-07-11).** Validated against a live
  stream. Streaming UDP ports = base `+9/+10/+11` (video/control/audio) on the Apollo
  process itself. Connect detected within ~1s of stream start; ungraceful teardown
  (hard cut *and* Lance's kill-based `disconnect`) detected ~6–7s. The TCP
  ESTABLISHED probe never reported `Connected` and was **retired** — UDP endpoint
  presence is the sole detector. Offsets recorded in SPEC.
- `[SESSION-ENDPOINT]` **Resolved (owner, 2026-07-11).** Connect handshake is a
  **new `POST /sessions`** endpoint; `POST /slots` stays allocation-only and never
  creates a session. Request/response body finalized at Slice 6.4.
- `[VERIFY-MUTEX]` — named-mutex cross-process semantics on Linux unverified. May
  intersect the foreground-daemon model (single-instance / session-id uniqueness);
  resolve before the client daemon slice if it does.
- **Auth (Phase 2):** agent optionally enforces a static bearer token. If
  `auth.token` is set in `lance-agent.json`, all non-`/health` requests must
  carry `Authorization: Bearer <token>`. If absent, the API is open. Client
  sends the token via `agent.token` in `lance.json` or `--token` CLI flag
  (flag wins). TLS cert validation is unconditionally disabled on the client
  in Phase 2 (self-signed cert); it will become configurable when PEM support
  is added.
