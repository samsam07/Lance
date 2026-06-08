# Lance — Plan

Development proceeds in phases. These descriptions serve as **task-attribution guidelines** — assign new work to the earliest phase whose scope it fits; defer only when a genuine prerequisite is missing.

- **Phase 1 — MVP:** Proves the concept with the smallest working slice. Slot lifecycle (allocate / start / stop / deallocate) over plain HTTP; no auth, no sessions, no service install. A task belongs here if it is required to get `lance connect` working end-to-end on one machine pair.
- **Phase 2 — Alpha:** Makes the tool fully functional for personal use. Auth/TLS, slot Connected state, client-driven connect/disconnect, and platform completions deferred from Phase 1. A task belongs here if it builds on the Phase-1 slot layer and does not require a session abstraction.
- **Phase 3 — Beta:** Makes the tool shareable. Feature-complete but not yet public-ready — integration tests, session layer (monitor↔slot mapping, state files), polish, and anything that requires Phase-2 features to be stable first. A task belongs here if it adds the session abstraction or is about hardening rather than new capability.
- **Phase 4 — Release:** Hardens and packages for distribution. Windows service / daemon install, auto-managing the Apollo service/watchdog (`[DEFER-SVC]`), installer, and public-facing hardening. A task belongs here if it changes the deployment/install model rather than application logic.

Phases 1 and 2 are detailed below with full slice breakdowns. Phase 3 is
fleshed out as Phase 2 shipped; Phase 4 remains a one-liner.

---

## Phase 1 — MVP *(✓ Complete)*

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

## Phase 2 — Alpha *(✓ Complete)*

**Goal:** a fully functional personal tool. Auth/TLS secures the API, slot
Connected state enables free-slot detection, and `connect`/`disconnect` use the
full client-driven flow with `--monitors` and `--slots`.

**In scope**
- Auth + TLS on the agent API.
- Agent: slot `Connected` state — TCP probe on slot base port at query time;
  `SlotDto.Status` gains `"Connected"`.
- Client: full `connect` (client-driven, `--monitors`, free-slot check),
  `disconnect` (`--slots`, `--keep-running`, `--purge`), enhanced `status`.
- Platform completions deferred from Phase 1: Windows process adoption
  (`[DEFER-WIN-ADOPT]`), Linux graceful SIGTERM stop (`[DEFER-LINUX-SIGTERM]`).
- Resolution of `[INVESTIGATE-STOP]` (Apollo graceful stop — fix the stop path).
- Unit and integration tests (first test code written this phase).

**Out of scope (deferred)**
- Windows service / daemon install (Phase 4).
- Auto-managing the Apollo service/watchdog `[DEFER-SVC]` (Phase 4).
- Session layer: `POST /sessions`, state files, monitor↔slot mapping (later phase).
- `[VERIFY-APOLLO]` Linux agent privilege model (verify before Linux deployment).


### Phase 2 — slice breakdown (review gates)

Same rules as Phase 1: one slice at a time, review gate after each.

1. **Platform completions + stop fix.**
   - `[DEFER-WIN-ADOPT]` — Windows process adoption: enumerate running
     `sunshine.exe` via `Process.GetProcessesByName`, attribute each to a slot
     by config name (standard) or observed port (fallback), register as adopted
     (id ≥1000 if non-standard). Mirrors the Linux path already implemented.
   - `[DEFER-LINUX-SIGTERM]` — Send SIGTERM before falling back to Kill on Linux
     stop. Requires P/Invoke (`kill(pid, SIGTERM)`).
   - `[INVESTIGATE-STOP]` — `CloseMainWindow` is likely a no-op on Apollo's
     tray/headless process; the 10 s graceful wait is wasted every stop. Fix:
     check `CloseMainWindow()` return value — if `false`, skip the wait and
     proceed directly to `Kill()`.

2. **Auth + TLS (agent + client).**
   - Agent: HTTPS via self-signed cert generated on first run (`tls.certPath`,
     defaults to `lance-agent.pfx` beside binary). `auth.token` config field —
     if set, all non-`/health` endpoints require `Authorization: Bearer <token>`;
     if absent, API is open. Auth enforced by a lightweight middleware (not the
     ASP.NET auth stack). `GET /health` always unauthenticated.
   - Client: TLS cert validation unconditionally disabled in Phase 2 (self-signed
     cert; validation configurable when PEM support is added). Token sent via
     `agent.token` in `lance.json` or `--token`/`-k` CLI flag (flag wins).
     `agent.url` must use `https://`. Command builders unified via `GlobalOptions`
     record to avoid per-command option threading.

3. **Agent: slot `Connected` state.** *(Architecture-zone.)*
   When serving `GET /slots` or `GET /slots/{id}`, probe the slot's base port for
   an ESTABLISHED TCP connection from a remote IP. If found →
   `Status = "Connected"`; running but no connection → `Status = "Running"`.
   `SlotDto.Status` gains the `"Connected"` value.

4. **Client: monitors command, connect + disconnect + enhanced status.** *(Architecture-zone.)*
   - `lance monitors` — new standalone command listing physical monitors (ID, name,
     resolution, position, primary). No agent required. Windows: `EnumDisplayDevicesW` +
     `EnumDisplaySettingsExW`. Linux: Xrandr 1.5 via `libX11`/`libXrandr`. Pure Wayland
     without XWayland not yet supported.
   - `lance connect [--monitors <list>] [--options "<flags>"]` — replaces `--count`.
     Free-slot check via `GET /health` + `GET /slots` (capacity = free + allocatable;
     exit 2 if N exceeds capacity). Duplicate monitor id → fast-fail. Phase A ensures
     each target slot is up (start if Allocated, reuse if Running/Connected); Phase B
     launches Moonlight for each up slot lacking a live local Moonlight (two-method
     detection — enables reconnect, prevents duplicates). Per-monitor `--resolution WxH`
     injected; `--options` tokens appended last. After launches, `WindowPlacer` moves
     each window's origin to the target monitor with `SetWindowPos(SWP_NOSIZE)`
     (Windows; `[DEFER-LINUX-WINPOS]`); SDL fullscreens on the correct display.
   - `lance disconnect [--slots <list>] [--keep-running] [--purge]` — per-slot:
     (1) kill Moonlight by `<host>:<port>` command-line match (always); (2) stop
     Apollo on agent (unless `--keep-running`); (3) deallocate (if `--purge`, Slot 0
     excluded). `--purge` wins over `--keep-running` with a warning.
     `ProcessCommandLine` helper reads Moonlight process command lines without admin
     (Windows: PEB inspection; Linux: `/proc/{pid}/cmdline`).
   - `lance status` (enhanced) — slots table + Moonlight PID column cross-referenced
     by `SlotDto.Host:Port` via `ProcessCommandLine`.
   - `ExitCodes.SessionActive` renamed to `NoFreeSlots` (exit 2).

### Review-depth guide (Phase 2)
- **Platform completions + stop fix** (1): moderate — adoption logic is subtle; review closely.
- **Auth/TLS** (2): moderate — correctness matters; no crypto invention.
- **Slot Connected state** (3): **architecture-zone** — TCP probe logic and the new Status value; review closely.
- **Connect + disconnect** (4): **architecture-zone** — free-slot logic, partial-success, and Moonlight process matching are the correctness-critical parts.

### Tests
Phase 2 is when test code is first written. Aim: unit tests for the slot
Connected-state TCP probe logic (Slice 3) and the connect free-slot check +
partial-success logic (Slice 4) at minimum. Integration tests deferred to Phase 3.

**Open deferred items carried forward:**
- `[DEFER-LINUX-WINDETECT]` → Phase 3
- `[DEFER-LINUX-WINPOS]` → Phase 3
- `[DEFER-PATHS]` client XDG compliance → Phase 3
- `[VERIFY-APOLLO]` Linux agent privilege model → verify before Linux deployment

---

## Phase 3 — Beta

**Goal:** takes a working Alpha and makes it solid. Fixes and polish found
during daily use come first; then Linux client completions; then integration
tests and hardening. A task belongs here if it fixes something discovered in
use, closes a Linux gap, adds integration coverage, or improves resilience —
without changing the deployment model.

**In scope**
- Daily-use fixes and polish (audio routing, anything surfaced during use).
- Linux client completions deferred from Phase 2.
- Integration tests (agent + client, real HTTP, no mocks).
- Client config XDG compliance (`[DEFER-PATHS]`).
- `[VERIFY-APOLLO]` — Apollo Linux privilege model; gate Linux agent work on this.
- TLS cert pinning / PEM support on the client (`[DEFER-TLS-PINNING]`).
- Session layer — `POST /sessions`, monitor↔slot mapping, state persistence
  (`[SESSION-TBD]`). Retained as a slice; drop it if daily use proves the
  client-driven model sufficient.
- Pure Wayland/XWayland support for `lance monitors`.

**Out of scope (deferred)**
- Windows service / daemon install (Phase 4).
- Auto-managing the Apollo service/watchdog `[DEFER-SVC]` (Phase 4).
- `[DEFER-PAIR-AUTO]` automated slot pairing (Phase 4).

### Phase 3 — slice breakdown (review gates)

Same rules as Phase 1 and 2: one slice at a time, review gate after each.

1. **Daily-use fixes and polish.**
   Issues surfaced during personal daily use. Known at Phase 3 open:
   - Audio routing — audio plays on the remote server instead of being sent
     to the host. Likely a config mutation issue in clone slots (wrong value
     cloned or missing override); may be config-only or require code changes.
     Investigate before assuming scope.
   - *(Further items added as discovered.)*

2. **Linux client completions.**
   - `[DEFER-LINUX-WINDETECT]` — Window title detection for Moonlight instance
     matching (method 2). On Linux, `Process.MainWindowTitle` is not supported;
     use `wmctrl -lp` to enumerate window titles and cross-reference by PID.
     Gate on `wmctrl` availability; fall back gracefully to method 1 only.
   - `[DEFER-LINUX-WINPOS]` — Moonlight monitor placement. Windows uses
     `SetWindowPos(SWP_NOSIZE)`; Linux equivalent: `wmctrl -r <title> -e
     0,X,Y,-1,-1` to move by title, or `XMoveWindow` via X11 P/Invoke. Prefer
     `wmctrl` (no P/Invoke); fall back gracefully if unavailable. Requires
     `[DEFER-LINUX-WINDETECT]` to be done first (need the title to target the window).
   - **Pure Wayland support for `lance monitors`.** Current Linux path uses
     Xrandr 1.5 via `libX11`/`libXrandr` — requires X11 or XWayland. Pure
     Wayland (no XWayland) needs the `xdg-output` or `wlr-output-management`
     Wayland protocol. Detect at runtime which path is available; fall back
     gracefully to X11 if Wayland enumeration is unavailable.
   - `[VERIFY-APOLLO]` — Confirm Apollo's Linux privilege model: does
     `sunshine.exe` (or the Linux binary) need `sudo` / `CAP_NET_BIND_SERVICE`?
     Document findings in ARCHITECTURE.md; adjust agent launch path accordingly.

3. **Integration tests.**
   In-process agent host (`WebApplicationFactory` or `TestServer`), real
   slot operations against a temp config dir, and client HTTP calls over
   localhost. Target: allocate/start/stop/deallocate happy path; free-slot
   capacity logic; auth token enforcement; Connected-state TCP probe with a
   real listener. No mocking of HTTP or file I/O at this layer.

4. **Client config XDG compliance (`[DEFER-PATHS]`).**
   On Linux, resolve `lance.json` from `$XDG_CONFIG_HOME/lance/lance.json`
   (default `~/.config/lance/lance.json`) before the binary-adjacent fallback.
   Windows behaviour unchanged. Document the full lookup order in README.

5. **TLS cert pinning / PEM support (`[DEFER-TLS-PINNING]`).**
   Client currently skips TLS validation unconditionally. Add a `tls.certPath`
   field to `lance.json`: when set, load the PEM/DER file and pin the agent's
   cert against it instead of accepting all certs. When absent, behaviour is
   unchanged (accept all — suitable for trusted networks). Agent side: no
   change needed; this is client-only.

6. **Session layer (`[SESSION-TBD]`).** *(Tentative — drop if the
   client-driven model proves sufficient in daily use.)*
   Add a lightweight session concept: the agent tracks which monitor maps to
   which slot for the duration of a connect/disconnect cycle, persisting the
   mapping in a small state file. Enables `lance status` to show monitor IDs
   alongside slot IDs without the client re-deriving the mapping, and enables
   crash recovery (reconnect restores the prior mapping). Scope and shape TBD
   once daily use reveals whether the gap is real.

### Review-depth guide (Phase 3)
- **Daily-use fixes** (1): varies per issue — investigate before coding;
  config-only changes are low risk; code changes follow normal review depth.
- **Linux completions** (2): moderate — wmctrl availability and Wayland
  detection logic are the subtle parts; review all fallback paths.
- **Integration tests** (3): architecture-zone — test boundaries define what
  we trust; review which layers are real vs. faked.
- **XDG paths** (4): plumbing — review the lookup order matches the spec.
- **TLS pinning** (5): moderate — cert loading and validation callback are
  the correctness-critical parts; review closely.
- **Session layer** (6): architecture-zone if it proceeds — new agent state
  and a new endpoint; review every line.

## Phase 4 — Release
Hardening, packaging, install/service, polish. *(TBD.)*

Includes `[DEFER-PAIR-AUTO]` — automated slot pairing. See ARCHITECTURE.md for
the proposed mechanism and the one open verification needed before implementation.
