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
Flesh on the skeleton: auth/TLS, sessions, the session layer, `connect`/`disconnect`
proper. *(Detail when Phase 1 ships.)*

## Phase 3 — Beta
Feature-complete but not public-ready. *(TBD.)*

## Phase 4 — Release
Hardening, packaging, install/service, polish. *(TBD.)*
