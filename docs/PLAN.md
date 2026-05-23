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

---

## Phase 2 — Alpha
Flesh on the skeleton: auth/TLS, sessions, the session layer, `connect`/`disconnect`
proper. *(Detail when Phase 1 ships.)*

## Phase 3 — Beta
Feature-complete but not public-ready. *(TBD.)*

## Phase 4 — Release
Hardening, packaging, install/service, polish. *(TBD.)*
