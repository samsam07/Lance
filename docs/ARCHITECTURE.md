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
TCP-probing the slot's base port for an ESTABLISHED connection from a remote IP,
at query time. `SlotDto.Status` = `"Allocated"` | `"Running"` | `"Connected"`.
`[RESEARCH-1]` resolved.

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
  - `--keep-running` — stop Apollo slots on the agent but do not kill Moonlight processes.
  - `--purge` — stop then deallocate slots (full teardown). Slot 0 excluded from deallocation.


## Flows

### connect (Phase 2+ shape)

Precondition: Moonlight executable exists; client can reach the agent.

1. **Resolve target monitors → count N.**
   *fails if:* a requested monitor id is invalid → *on failure:* log, drop it
   from N, continue with the rest.
2. **`GET /slots`.** Count free slots: `Allocated` or `Running` (not `Connected`).
   If free < N and the max-slots ceiling prevents allocating more → exit 2
   `no_free_slots` (all usable slots are connected; user must disconnect first).
3. **`POST /slots` (count = existing + shortfall).** Allocate any missing slots so
   the pool reaches N non-connected. Idempotent if the pool is already large enough.
   *fails if:* allocation fails → log, continue with however many free slots remain
   (partial success).
4. **`POST /slots/{id}/start` for each Allocated target slot.** Skip slots already `Running`.
   *fails if:* a slot fails to start → warn, drop from target list, continue
   (partial success).
5. **Launch Moonlight per started slot** (`moonlight stream <host>:<port> Desktop …`).
   *fails if:* a Moonlight process fails to launch → warn, continue with the rest
   (partial success).

Post-state: every slot that came up has a running Moonlight; failed slots are
logged and absent. The setup may be partial.

> **Failure policy — partial success.** Monitors are independent; 2 of 3 beats 0.
> Never roll back working slots.

### disconnect (Phase 2+)

Target: all `Running`/`Connected` slots, or only those in `--slots <list>` if specified.

For each target slot:
1. `POST /slots/{id}/stop` (agent).
2. Find and kill the matching Moonlight process (client): enumerate `moonlight`
   processes, match by `<host>:<port>` in the process command line.

**`--purge`:** after stopping, call `DELETE /slots/{id}` for each stopped slot.
Slot 0 is excluded from deallocation.

**`--keep-running`:** perform step 1 only — stop Apollo slots but do not kill
Moonlight processes.

Best-effort per slot: a failed stop is logged; other slots proceed.

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
- `[INVESTIGATE-STOP]` — Apollo graceful stop consistently times out in Phase 1
  testing (`CloseMainWindow` is likely a no-op on Apollo's tray/headless process).
  Phase 2 Slice 1 fixes this: check `CloseMainWindow()` return value — if `false`,
  skip the graceful wait and proceed directly to `Kill()`.
- Auth: no auth in Phase 1; added in Phase 2.
