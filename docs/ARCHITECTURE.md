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

### Session

A session is primarily an **ID** tracking active Moonlight↔Apollo connections,
generated as connections are established. Sessions are per-connection by default,
but can be **grouped** under one ID two ways:
- `POST /sessions` — group all of a multi-monitor request under one id at once.
- `POST /slots/{id}/start` with a session id — group connections **one at a
  time** by tagging each started slot with the same id.

**Audio master:** in a grouped session, exactly one slot carries audio (the
master), so audio isn't duplicated across connections. The master is the slot
serving the session's **primary screen**. By default this is **Slot 0**, and
**for early phases the master is fixed to Slot 0**; making the master
configurable (any primary screen) comes later.

> **INVARIANT:** A session is valid with **zero running master slots**. If the
> master fails, the session runs **without audio** (warn + continue). Audio is
> best-effort, never required. Do not treat a masterless session as broken.

**Open research item (blocks session work):** there is no known direct way to tell
whether an Apollo instance has a live Moonlight client. Candidate fallback:
observe the Moonlight port for activity. **This must be resolved by a research
spike before any session code is written** — the result steers the session
architecture. `[RESEARCH-1]`

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
- `GET /slots/{id}` — slot detail (running/stopped, active client, session id).
- `DELETE /slots/{id}` — deallocate; **refuses if running** (stop first).
- `POST /slots/{id}/force-deallocate` — stop if running, then deallocate.
- `POST /slots/{id}/start` — start an Apollo instance for the slot.
- `POST /slots/{id}/stop` — stop the slot's Apollo instance.
- `GET /slots/{id}/config` — link to the Apollo config page (slot must be
  running; `?redirect=1` supported).

**Sessions** *(Phase 2+)*
- `GET /sessions` — list active sessions (IDs, slots, …).
- `POST /sessions` — create a connection session. Param: monitor count. A
  **fat-agent shorthand** that allocates + starts slots under one session id
  (generated internally or supplied by client, then returned).
- `GET /sessions/{id}` — session detail.
- `DELETE /sessions/{id}` — disconnect (stop) all slots in the session.

> **Architecture decision — fat agent:** the agent owns orchestration. Clients
> request outcomes ("give me a 3-monitor session"); the agent performs the
> allocate+start sequence and handles partial failure server-side. Clients do
> not drive per-slot steps. (See connect flow.)

### State management

Slot state is **not stored** by the agent — it is inferred from Apollo's config
files on disk. Slot 0 is `sunshine.conf`; clones are `sunshine_{id}.conf`. Slots
are a **pool where order is irrelevant except Slot 0**. Slot 0 is special: it
allows audio and acts as the audio master in grouped sessions.

> **INVARIANT:** Slot id drives port math (`template_port − N×portStep`) **only
> for Lance-allocated standard slots**. Adopted/non-standard slots (id ≥1000,
> `IsAdopted`) carry an **observed** port; Lance never derives, recomputes, or
> mutates their port or config, and never starts/allocates/deallocates them
> (list + stop only).

> Known issue: the audio-master model has an edge case across multiple
> simultaneous sessions. Deferred to a later phase. `[DEFER-1]`

Session state *(Phase 2+)* is agent-owned, stored at a configured path, guarded
by locks against races. On agent restart, sessions are reconciled: orphaned
sessions (whose live connections no longer exist) are removed, with clear logs.
If session state is lost, session ids are recalculated individually, logged
clearly.

> Slot 0 is the audio master **by default and for early phases**. Later, the
> master is whichever slot serves the session's primary screen (configurable).

*(Exact state-file paths, log paths, and retention live in SPEC, not here.)*

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
- `lance status [host[:port]]` — unified view: slots + sessions + local Moonlight
  PIDs in one place. (Primary Phase-1 status view, since Phase 1 has no sessions.)

**Sessions** *(Phase 2+)*
- `lance sessions [host[:port]] [--id xxx]`
- `lance connect [host[:port]] [options] [--options "<moonlight-options>"]`
  - `--monitors <list>` — comma-separated 1-indexed monitor IDs. Default: all
    physical monitors.
  - `--session xxx` — connect with a specific session id (error if it already
    exists on the agent).
  - Moonlight passthrough examples: `--bitrate <kbps>`, `--video-codec
    <HEVC|H264|AV1>`, `--fps <n>`, `--resolution <WxH>`, etc.
- `lance disconnect [--session xxx] [--keep-running] [--purge]`

### State management *(Phase 2+)*

Client session state stores active connections with launched Moonlight PIDs,
guarded by locks. Recovery (after a client crash/restart) depends on the
connection-detection research `[RESEARCH-1]` and is **deferred** until that is
resolved. *(Exact paths live in SPEC.)*

## Flows

### connect (fat agent, Phase 2+ shape)

Precondition: Moonlight executable exists; client can reach the agent.

1. **Resolve target monitors → count N.**
   *fails if:* a requested monitor id is invalid → *on failure:* log, drop it
   from N, continue with the rest.
2. **`POST /sessions` (count=N).** Agent allocates + starts N slots internally.
   *fails if:* a slot fails to allocate/start → *on failure:* **partial success** —
   agent keeps the slots that came up, returns which succeeded/failed. No
   rollback. (Master/Slot-0 failure → session runs without audio; warn, continue.)
3. **Agent returns the connection manifest** (per monitor: slot id, Apollo port, …).
   *fails if:* manifest missing entries → *on failure:* log; launch only the
   monitors that have valid details.
4. **Launch Moonlight per returned slot.**
   *fails if:* a Moonlight process fails to launch → *on failure:* warn, continue
   with the rest (partial success).
5. **Record session state** (session id, slot↔PID mapping).
   *fails if:* state write fails → *on failure:* log a **warning**, continue
   (the connection still works; recovery is degraded).

Post-state: every slot that came up has a running Moonlight and is recorded;
failed slots/monitors are logged and simply absent. The session may be partial.

> **Failure policy — partial success (overturns old all-or-nothing / D13).**
> Monitors are independent and individually useful; 2 of 3 beats 0 of 3. Never
> roll back working slots to satisfy an all-or-nothing rule. Master failure
> degrades audio only, never fails the session.

### disconnect (Phase 2+)

`DELETE /sessions/{id}` stops all slots in the session; client stops the matching
Moonlight PIDs. Best-effort per slot (a failed stop is logged, others proceed).

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

## Notes / open items
- `[RESEARCH-1]` Apollo↔Moonlight connection detection — research spike before
  session code.
- `[DEFER-1]` Audio-master edge case across simultaneous sessions — later phase.
- Auth: old SPEC assumed Bearer auth in Phase 1; **current decision is no auth in
  Phase 1** (added in Phase 2). Discrepancy noted for SPEC reconciliation.
