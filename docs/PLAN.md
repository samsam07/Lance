# Lance — Plan

Development proceeds in phases. The body analogy:
- **Phase 1 — Walking skeleton:** bare bones that walk. No auth, no service, no sessions.
- **Phase 2 — Alpha:** flesh on the skeleton — auth + core features.
- **Phase 3 — Beta:** a naked body — functional but not public-ready.
- **Phase 4 — Release:** dressed and ready for the world.

Only Phase 1 is detailed below. Later phases are deliberately left as one-liners
until Phase 1 ships — to avoid planning ahead of what we've learned.

---

## Phase 1 — Walking skeleton (MVP)

**Goal:** the smallest end-to-end tool that proves the concept. Used personally
to validate that orchestrating parallel Apollo + Moonlight actually works.

**In scope**
- Agent: slot allocate / deallocate, start / stop. No sessions.
- Client: `slots` commands (mirror agent), `status`, and a basic Phase-1
  `connect` (allocate → start → launch Moonlight, partial-success, fail-fast).
- HTTP between client and agent.
- Config files (agent + client) as per the sample configs.

**Out of scope (deferred)**
- Auth / TLS (Phase 2).
- Windows service / daemon install — runs as a plain process.
- Sessions and the whole session layer.
- Connection detection `[RESEARCH-1]` and crash recovery.
- Auto-managing the Apollo service/watchdog `[DEFER-SVC]` — Phase 1 assumes the
  user manually stops the Apollo service first; Lance adopts/owns only its own
  direct `sunshine.exe` launches.
- Interactive failure prompts.

**Linux-specific verification (deferred — no Linux hardware available yet):**
- `[VERIFY-APOLLO]` — Apollo's Linux privilege model is untested. Must be verified
  on Linux before Slice 4 (start/stop) is implemented for that platform. Until
  then, Slice 4 targets Windows only.
- `[VERIFY-MUTEX]` — Named-mutex cross-process semantics on Linux/.NET are
  unverified. Must be verified before Slice 6 (client locking) is implemented for
  Linux. Fallback if unreliable: PID-bearing lock file (read PID on acquire; if
  alive → exit 2, if dead → reclaim).

**Platform deferrals (Slice 4):**
- `[DEFER-WIN-ADOPT]` — Windows process adoption is a no-op in Phase 1.
  `ProcessAdopter.Adopt` only runs on Linux (via `/proc/{pid}/cmdline`). On
  Windows, any Apollo instances running before the agent starts will not be
  tracked and cannot be stopped via Lance until restarted through it. Deferred
  to Phase 2 (enumerate via `Process.GetProcessesByName`, register as adopted
  id ≥ 1000).
- `[DEFER-LINUX-SIGTERM]` — Graceful Linux stop (SIGTERM before SIGKILL) requires
  P/Invoke. In Phase 1, Linux stop skips the graceful step and proceeds directly
  to `WaitForExitAsync` + `Kill()` after the configured timeout. Deferred to
  Phase 2.

**Phase 1 connect policy:** partial success, warn on failed slots, no rollback,
no prompts.

**Done when:** from one machine you can run `lance connect` against an agent on
another, get N Moonlight windows on N remote monitors, and `lance status` shows
the truth.

### Phase 1 — slice breakdown (review gates)

Each slice is small enough to fully read in ~10–15 min. **Rule: read and approve
a slice before the next begins. No slice runs ahead of your understanding.**

> Slices below are a proposed ordering for your review — confirm or reorder.

1. **Project skeleton + config loading.** Solution structure, both projects build,
   config files load and validate. (Pure plumbing — delegate freely.)
2. **Agent: slot model + on-disk inference.** Read Apollo config dir, infer slots
   from `sunshine.conf` / `sunshine_{id}.conf`. `GET /slots`. *(Architecture-zone:
   review closely — this is the slot-state source of truth.)*
3. **Agent: allocate / deallocate.** Clone template → `sunshine_{id}.conf`;
   delete on dealloc; Slot 0 protected. `POST /slots`, `DELETE /slots/{id}`.
   *(Architecture-zone: the clone/mutation rules are correctness-critical.)*
4. **Agent: start / stop + adoption.** Launch/stop `sunshine.exe` per slot, track
   PID, derive Running; on startup adopt already-running instances (command-line
   config path → slot, port fallback). `POST /slots/{id}/start|stop`.
   *(Architecture-zone: adoption mapping is the subtle part — review closely.
   Linux admin model unverified — `[VERIFY-APOLLO]`.)*
5. **Client: HTTP + `slots` + `status`.** Talk to agent, render state. (Mostly
   straightforward — moderate review.)
6. **Client: Phase-1 `connect`.** Allocate → start → launch Moonlight per monitor,
   partial-success, fail-fast. *(Architecture-zone: the failure handling is the
   part that bit you before — review every line.)*

### Review-depth guide (per slice)
- **Plumbing** (1): build-green is enough; skim.
- **Architecture-zone** (2, 3, 4, 6): trace the control flow; confirm it matches
  ARCHITECTURE.md; correct taste against CONVENTIONS.md. These are the moat.
- **Moderate** (5): read, but don't agonize.

### Tests
Unit and integration tests are deferred — no test code is written during Phase 1 slices. The test project skeletons (`Lance.Agent.Tests`, `Lance.Client.Tests`) exist in the solution and will be filled either in a dedicated final slice of Phase 1 or at the start of Phase 2, whichever comes first.

---

## Phase 2 — Alpha

**Goal:** a fully functional personal tool. Sessions track connections, auth/TLS
secures the API, and `connect`/`disconnect` use the proper fat-agent flow.

**In scope**
- Auth + TLS on the agent API.
- Agent sessions layer: `POST /sessions`, `GET /sessions[/{id}]`,
  `DELETE /sessions/{id}`.
- Client: full `connect` (fat-agent, `--monitors`), `disconnect`, `sessions`,
  enhanced `status`.
- Client state file + named mutex (single-instance guard, crash recovery).
- Platform completions deferred from Phase 1: Windows process adoption
  (`[DEFER-WIN-ADOPT]`), Linux graceful SIGTERM stop (`[DEFER-LINUX-SIGTERM]`).
- Resolution of `[VERIFY-MUTEX]` (Linux named-mutex vs PID lock file).
- Unit and integration tests (first test code written this phase).

**Out of scope (deferred)**
- Windows service / daemon install (Phase 4).
- Auto-managing the Apollo service/watchdog `[DEFER-SVC]` (Phase 4).
- Audio-master edge case across simultaneous sessions `[DEFER-1]` (later phase).
- `[VERIFY-APOLLO]` Linux agent privilege model (verify before Linux deployment).

**Hard prerequisite — `[RESEARCH-1]`**
> Apollo↔Moonlight connection detection is unresolved. This research spike
> **must be completed as Slice 1** before any session code is written — the
> result directly shapes the session architecture. If reliable detection is
> impossible, session state becomes best-effort (record slots/PIDs but cannot
> confirm live connections). The spike produces findings documented in
> ARCHITECTURE.md; it produces no code.

### Phase 2 — slice breakdown (review gates)

Same rules as Phase 1: one slice at a time, review gate after each.

1. **Research spike — `[RESEARCH-1]`** *(no code).*
   Investigate whether an Apollo instance with a live Moonlight client is
   detectable (network probe, Apollo API, process signals, etc.). Document
   findings and the chosen detection strategy (or confirmed impossibility) in
   ARCHITECTURE.md. **All session slices depend on this outcome.**

2. **Platform completions.**
   - `[DEFER-WIN-ADOPT]` — Windows process adoption: enumerate running
     `sunshine.exe` via `Process.GetProcessesByName`, attribute each to a slot
     by config name (standard) or observed port (fallback), register as adopted
     (id ≥1000 if non-standard). Mirrors the Linux path already implemented.
   - `[DEFER-LINUX-SIGTERM]` — Send SIGTERM before falling back to Kill on Linux
     stop. Requires P/Invoke (`kill(pid, SIGTERM)`).
   - `[VERIFY-MUTEX]` — Determine whether `System.Threading.Mutex` is reliably
     cross-process on Linux/.NET. If yes, use it for the client mutex. If no,
     fall back to a PID-bearing lock file (read PID; alive → exit 2, dead →
     reclaim). Document decision in SPEC.
   - `[INVESTIGATE-STOP]` — In Phase 1 testing, stopping a running slot
     consistently exceeds the 10 s graceful timeout and falls through to
     force-kill. Investigate why Apollo does not respond to the graceful close
     signal (`CloseMainWindow` on Windows / SIGTERM on Linux once
     `[DEFER-LINUX-SIGTERM]` is resolved). Determine whether the timeout needs
     tuning, whether a different shutdown signal is required, or whether Apollo
     simply does not support graceful termination. Document findings and correct
     the stop path accordingly.

3. **Auth + TLS (agent + client).**
   - Agent: `tls{}` config block (cert path, key path); enforce HTTPS on the
     listener. `auth{}` config block (bearer token); validate `Authorization:
     Bearer <token>` on all non-health endpoints.
   - Client: `auth.token` in `lance.json`; send bearer on every request. TLS
     cert validation configurable (strict default; `insecure` flag for dev).
   - `GET /health` remains unauthenticated (liveness probe).

4. **Agent sessions layer.**
   `POST /sessions` — fat-agent shorthand: receives `{ monitorCount }`,
   allocates + starts slots internally, returns a session manifest (session id,
   per-slot `{ slotId, host, port }`). Partial success per ARCHITECTURE:
   returns whatever succeeded; Slot-0 failure → session runs without audio (warn,
   continue). Session state stored in agent memory (Phase 2); disk persistence
   is Phase 3+. `GET /sessions`, `GET /sessions/{id}`,
   `DELETE /sessions/{id}` (stops all slots in the session). *(Architecture-zone
   — review closely: the partial-success invariant and session state model.)*

5. **Client state file + named mutex.**
   - Named mutex `Global\Lance.Client` (Windows) / strategy from Slice 2
     `[VERIFY-MUTEX]` finding (Linux). Acquired on `connect`; held until
     `disconnect` or process death. Already-held → exit 2.
   - `client-state.json` at platform paths from SPEC. Written on `connect` (session
     id, slot↔Moonlight-PID mapping, `startedAt`, `agentUrl`). Read on `disconnect`
     and `status`. Stale check: if all recorded Moonlight PIDs are dead → treat as
     no session, clean up, proceed.
   - Schema: `{ schemaVersion, startedAt, agentUrl, sessionId, monitors: [{ monitorId, slotId, apolloPort, moonlightPid }] }`.

6. **Client: full session commands + enhanced status.** *(Architecture-zone.)*
   - `lance connect [--monitors <list>] [--session <id>]` — calls `POST /sessions`,
     receives manifest, launches Moonlight per slot, writes state file. `--monitors`
     is comma-separated 1-indexed physical monitor IDs; default: all physical
     monitors (requires OS display enumeration). Replaces `--count`.
   - `lance disconnect [--session <id>] [--keep-running] [--purge]` — reads state
     file, calls `DELETE /sessions/{id}`, stops local Moonlight PIDs (unless
     `--keep-running`), removes state file (unless `--purge` skips API call).
   - `lance sessions [--id <id>]` — calls `GET /sessions[/{id}]`, renders table.
   - `lance status` (enhanced) — unified view: slots + sessions + local Moonlight
     PIDs cross-referenced from state file.

### Review-depth guide (Phase 2)
- **Research** (1): findings doc only — review the documented strategy, not code.
- **Platform completions** (2): moderate — adoption logic is subtle; review closely.
- **Auth/TLS** (3): moderate — correctness matters; no crypto invention.
- **Sessions agent** (4): **architecture-zone** — partial-success invariant is
  the correctness-critical part.
- **State file + mutex** (5): moderate — locking model and stale-check logic.
- **Session commands** (6): **architecture-zone** — failure paths, state
  consistency, and the `--monitors` OS enumeration are the tricky parts.

### Tests
Phase 2 is when test code is first written. Aim: unit tests for the session
layer partial-success logic (Slice 4) and client state file read/write (Slice 5)
at minimum. Integration tests deferred to Phase 3.

## Phase 3 — Beta
Feature-complete but not public-ready. *(TBD.)*

## Phase 4 — Release
Hardening, packaging, install/service, polish. *(TBD.)*
