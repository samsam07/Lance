# Lance — Architectural Decisions

This file captures the *why* behind each significant choice made during planning. Read this before second-guessing any decision below. If you disagree with one of these, raise it with the user explicitly — do not silently deviate.

## D1: Two components — agent (server) and client (CLI)

**Decision:** Two binaries, talking over HTTPS/JSON. `lance-agent` runs on the Apollo machine, `lance` runs on the Moonlight machine.

**Rationale:** Each side has fundamentally different concerns. The agent manages Apollo processes and config files (a long-running stateful service). The client manages Moonlight processes locally and presents a CLI (short-lived invocations). Putting them in one binary would force a worse design on one side or the other.

**Alternative considered:** A single binary that runs in two modes. Rejected because the dependency surface is different (the agent needs ASP.NET Core, the client needs System.CommandLine + Spectre.Console rendering) and combining them inflates the deployable.

## D2: .NET 10 with AOT compilation

**Decision:** .NET 10 (LTS released Nov 2025). AOT from Phase 1.

**Rationale:**
- The user is a .NET expert. Using their strongest language minimizes review burden and bug surface.
- AOT gives fast startup (matters for CLI tool that's invoked frequently) and small binaries (~10-20MB self-contained vs 60-80MB JITted).
- .NET 10 is LTS, supported for years.
- Starting AOT from Phase 1 enforces discipline (no reflection, no `dynamic`) rather than discovering AOT incompatibility late.

**Alternative considered:** Go. Rejected because user's .NET fluency matters more than Go's ecosystem fit for this shape of tool. Also considered Python — rejected because the deployment story is messier and CLI tools should not require a runtime install on the target machine.

## D3: HTTPS/REST + JSON for transport

**Decision:** HTTPS with self-signed cert, no client-side cert verification in Phase 1. JSON request/response bodies. No gRPC, no WebSocket initially.

**Rationale:**
- Debuggable with curl (`curl -k` skips cert verification).
- Easy to extend.
- ASP.NET Core Minimal APIs make 5 endpoints into ~100 lines.
- Sub-second LAN latency for personal-use traffic doesn't need gRPC's efficiency.
- WebSocket would be nice for live status push but is a Phase 2 concern.
- HTTPS even in Phase 1 means the bearer token isn't in cleartext over the wire. Cert warnings are acceptable for personal use.
- Phase 3 adds proper cert management (Let's Encrypt for VPN scenarios, optional cert pinning).

**Client TLS behavior in Phase 1:** the `lance` client's HttpClient is configured to ignore certificate errors. No `verify_tls` flag in config — Phase 1 always ignores. Phase 3 introduces verification config.

## D4: Bearer token auth from day one

**Decision:** Shared secret in `Authorization: Bearer <token>` header. Required on all endpoints except `/health`.

**Rationale:**
- The agent runs on Windows on the local network. Even if the user trusts their home LAN today, this might run on a laptop that travels.
- A shared token is 5 minutes of code and prevents drive-by misuse.
- mTLS is the "correct" answer but overkill for personal use.

**Token storage:** Plain text in the agent config file and client config file. Both files should have restrictive permissions. We do not encrypt at rest — that's security theater without an HSM. Phase 3 may add proper credential management.

**Token rotation:** Phase 1 = manual (edit both config files, restart agent, restart any active `lance` invocations). Phase 3 may add a rotation flow.

## D5: Slot model — clone from template (Option X)

**Decision:** Lance reads the user's pre-paired `sunshine.conf` as the template (which IS slot 0), clones it (including state files) into `sunshine_N.conf` for each new slot, mutating only the must-differ fields.

**Rationale:**
- The user verified experimentally that Apollo's pairing survives cloning the state file. Cloned slots are automatically paired with Moonlight — no per-slot pairing dance.
- Inheriting the template means all the user's careful Apollo config (Headless Mode on, Adapter Name set to Intel Iris Xe, codec preferences, etc.) carries over to every slot without Lance needing to understand those settings.
- Lance never modifies the template file — the template is sacred. The user can break it themselves, but Lance won't.

**Fields Lance mutates per cloned slot (slots 1..N):**
- `sunshine_name` (display name shown in Moonlight, e.g. "Lance-2")
- `port` (decremented by 1000 from template port — see D10)
- `log_path` (unique per slot)
- `file_state` (unique per slot; state file content is cloned from template's state file)

**Fields Lance does NOT touch:** everything else, verbatim — Adapter Name, encoder settings, Headless Mode flag, audio config, codecs, etc.

**State file copying:** During clone, the template's `file_state` JSON (containing pairing keys) is copied to the slot's `file_state` path. This is what makes pairing inherit. Each slot then has its own independent state file going forward (Apollo writes to it during operation).

## D6: Lifecycle vocabulary

**Decision:**

- `allocate` — create config slot (clone template). Idempotent. Cannot allocate slot 0 (already exists by definition).
- `deallocate` — remove config slot. Requires slot to be stopped. Slot 0 cannot be deallocated.
- `start` — launch Apollo process for this slot. Works for slot 0.
- `stop` — terminate Apollo process for this slot. Config remains. Works for slot 0.
- `list` — show all slots and their state, including slot 0.
- `config` — return URL of slot's Apollo web UI. Requires slot to be running.

`connect` and `disconnect` are client-side conveniences that compose these.

**Rationale:** Clear separation of concerns. The user explicitly wanted independent control over config existence and process running state. The four primitives (allocate/deallocate/start/stop) cover the matrix. Slot 0 is just a normal slot with one safety rail (can't be deallocated).

## D7: Disconnect default — stops Apollo but keeps slots allocated

**Decision:** `lance disconnect` (no flags) stops Apollo processes but leaves the cloned config files on disk.

**Rationale:**
- The expensive resource is the running process (CPU, memory, encoder block). The cheap resource is a 5KB config file on disk.
- Defaults should release expensive resources, keep cheap ones for fast reconnect.
- The principle of least surprise: "disconnect" intuitively means "end the current session" — stopping Apollo matches that. Leaving Apollo running would be unexpected and could waste resources if the user forgets.

**Flags:**
- `--keep-running` → don't stop Apollo. Useful for stepping away briefly.
- `--purge` → also deallocate slots. Full cleanup. Does NOT deallocate slot 0 (impossible).

No aliases (`close`, `terminate`, etc.). Verbose-but-clear over short-but-ambiguous.

## D8: Connect is fire-and-forget (detached)

**Decision:** `lance connect` spawns Moonlight processes as detached children, records PIDs in a state file, returns immediately.

**Rationale:**
- Matches user expectation from tools like `mstsc`, `tmux`, `docker run -d` — launch and walk away.
- Keeping `lance connect` in foreground would force the user to keep a terminal open all day.

**State file location:** `$XDG_RUNTIME_DIR/lance/state.json` (Linux), `%LOCALAPPDATA%\Lance\state.json` (Windows).

**State file validation on connect:** before refusing a new `connect` due to "session active," validate the state file. Check each recorded Moonlight PID is alive. If all are dead, treat as no active session — clean up the stale state file and proceed. This handles the "client crashed mid-session" case.

**State file locking:** the state file is opened with an exclusive lock during read+write operations to prevent races between concurrent `lance` invocations on the same machine. If the lock is held, the second invocation fails fast with a clear error.

**Edge case:** Running `lance connect` while a session is genuinely active (PIDs alive) → refuse with a clear error. Phase 1 simplicity. Phase 2 may add `--add` to extend an existing session.

## D9: --monitors flag is a list of physical monitor IDs (1-based)

**Decision:**

- `lance connect` (no flag) → one Moonlight instance per physical monitor on the client machine.
- `lance connect --monitors 1,2` → Moonlight instances for monitors 1 and 2.
- Monitor IDs are 1-indexed, matching KDE's display arrangement UI.

The list LENGTH determines how many slots are requested. The monitor IDs themselves are about which physical client monitors to use (Phase 2 uses them for window placement; Phase 1 just uses the count).

**Rationale:** The user is thinking about "which monitors do I want to use today," not "how many." Specifying a count without IDs is ambiguous (which monitors do they go on?). A list is unambiguous and forward-compatible.

**Phase 2 future:** `--monitors 1,2*,3` where `*` denotes the primary monitor. Reserve `*` syntax in the parser so we don't have to change the flag semantics later.

**Slot mapping:** monitor list of length N → request slots 0..(N-1) from the agent. Sequential. The first monitor in the `--monitors` list is served by slot 0, the second by slot 1, etc. This is fixed in Phase 1 — no way to request specific slot IDs via `lance connect` (would need direct slot API calls, deferred to Phase 2).

## D10: Slot ports start at template port and decrement by 1000

**Decision:** Slot 0 (template) keeps its original port. Slot 1 port = template port - 1000. Slot 2 = template - 2000. Etc.

**Rationale:** User's existing Apollo install uses 49xxx range as template. Decrementing keeps slots clear of well-known ports and Apollo's own port range usage (Apollo claims several ports starting from its configured port). 1000 gap is paranoid-safe.

**Maximum slots:** Configurable, default 8 (slots 0..7). So template at 49989 supports slots 0..7 with ports 49989, 48989, 47989, ..., 42989.

## D11: Phase 1 is clean-slate on agent startup

**Decision:** When `lance-agent` starts, it kills any Apollo processes it finds running. This includes slot 0 — Lance is the Apollo instance manager, period.

**Rationale:** Process adoption (recognizing existing Apollo processes and inheriting them) is more robust but more code. Phase 1 prioritizes simplicity. The downside (the user must reconnect after agent restart) is acceptable at this stage.

**Phase 2 will add adoption.** On startup, the agent will scan for running Apollo processes, identify them by config file path, and adopt them into its state — preserving the user's active session across agent restarts.

**Implication for the user:** in Phase 1, don't restart `lance-agent` during an active session unless you're ready to reconnect everything.

## D12: Template detection — hardcoded to `sunshine.conf` in Phase 1

**Decision:** Template is `<apollo_config_dir>/sunshine.conf`. Not configurable in Phase 1.

**Rationale:** Simplicity. The user only has one Apollo install. Phase 2/3 can add `template_config_path` to agent config for users who want a dedicated lance template.

## D13: Error handling — all-or-nothing in Phase 1

**Decision:** If any step in `connect` fails, roll back fully and return an error. No partial state.

**Rationale:** Best-effort with interactive prompts requires the prompting code, the rollback-or-continue logic, and careful state tracking. Phase 1 doesn't have time for it. Aborting on first failure is the safest behavior and the simplest to implement.

**Phase 2 will add:** Interactive prompts on partial failure with `[c]ontinue / [r]etry / [a]bort` options. `--no-prompt` flag for scripting. `--continue-on-error` flag for headless best-effort.

## D14: CLI parsing library — System.CommandLine

**Decision:** System.CommandLine (GA Feb 2025) for the `lance` client.

**Rationale:**
- Officially supported by Microsoft.
- Fully AOT-compatible.
- Source generators handle the binding without reflection.

**Alternative considered:** Spectre.Console.Cli — better aesthetics, but has had AOT issues. We use Spectre.Console for *rendering* (tables, progress, colors) but not for CLI *parsing*. This is a common stack: System.CommandLine for parse, Spectre.Console for present.

## D15: Logging — Serilog with source-gen-friendly config

**Decision:** Serilog with `Serilog.Sinks.Console` and `Serilog.Sinks.File` (rolling).

**Rationale:**
- Structured logging from day one is valuable for debugging.
- Overhead is negligible for our request volume.
- AOT-compatible with the chosen sinks.

**Log levels:**
- Default: Information
- Verbose mode: Debug (with `--verbose` flag on client, env var on agent)
- File logs always at Debug.

**Log locations:**
- Agent: `%ProgramData%\Lance\logs\agent-.log` (rolling daily)
- Client: stderr by default. `--log-file <path>` to enable file logging. Phase 2 may default-enable to `$XDG_STATE_HOME/lance/` or `%LOCALAPPDATA%\Lance\logs\`.

## D16: Naming

**Decision:** Project = `lance`. Server binary = `lance-agent` (`lance-agent.exe` on Windows). Client binary = `lance` (`lance.exe` on Windows).

**Rationale:**
- Lance pairs thematically with Apollo (sun) and Moonlight — a lance pierces/connects.
- "Agent" is more accurate than "server" because Apollo is the actual remote-desktop server. Lance's agent is an orchestrator/manager. Calling it "server" would create two "servers" in the same diagram.
- Industry precedent: monitoring agents, Docker daemon, k8s node-agent.

## D17: Config file format — JSON

**Decision:** JSON for both agent and client config files.

**Rationale:**
- First-class .NET support via `appsettings.json` pattern.
- Source-gen friendly for AOT.
- Editable in any text editor.
- The user accepted this; YAML's prettiness wasn't worth the extra dependency.

## D18: Default agent port — 9876

**Decision:** Agent HTTPS listens on `0.0.0.0:9876` by default. Configurable.

**Rationale:** High port, easy to remember, no well-known service conflict.

## D19: Repository layout — one solution, three projects

**Decision:** `Lance.sln` containing `Lance.Agent`, `Lance.Client`, `Lance.Shared` projects plus test projects.

**Rationale:**
- Shared DTOs and JSON contexts go in `Lance.Shared` so request/response types are defined once.
- Separate Agent and Client projects so each can have its own dependency set (and the client doesn't drag in ASP.NET Core).
- Tests in `tests/` parallel structure.

## D20: Newtonsoft.Json is forbidden

**Decision:** Use System.Text.Json with source generators. Do not add Newtonsoft.Json to any project.

**Rationale:**
- Newtonsoft.Json is NOT AOT-compatible — it relies on runtime reflection and dynamic code generation that breaks under AOT.
- System.Text.Json with source generators (`[JsonSerializable]` context classes) is the AOT-safe path.
- Slight boilerplate cost but no runtime cost, no reflection.

## D21: Connect target can be specified inline (mstsc-style) or via config

**Decision:** `lance connect` accepts a positional target argument and inline flags that override config:

```
lance connect [host[:port]] [--token <token>] [--config <path>] [options]
```

**Resolution precedence (highest to lowest):**
1. Inline CLI flags (e.g., `--token`, `--port`)
2. Positional `host` argument
3. `--config <path>` file
4. Config file beside the executable (`lance.json`)
5. Platform default location (`~/.config/lance/config.json` on Linux, `%APPDATA%\Lance\config.json` on Windows)
6. Error with helpful message if nothing resolved

**Rationale:**
- Matches `mstsc` and `ssh` mental model — drop a binary anywhere and use it without config.
- Supports portable distribution.
- Config file is convenience, not a requirement.

## D22: Slot 0 is just a normal slot — used in every `lance connect`

**Decision:**

- Slot 0 exists as soon as `sunshine.conf` exists (template is the slot).
- Slot 0 can be started, stopped, queried like any other slot.
- Slot 0 CANNOT be deallocated (would delete the template).
- `lance connect` numbers slots starting from 0. With N monitors, slots 0..(N-1) are used.
- One monitor → slot 0. Two monitors → slots 0, 1. Three monitors → slots 0, 1, 2.
- Slot 0 is part of EVERY `lance connect` session. No special-casing.

**Rationale:**
- The user explicitly wanted: 3 monitors = 3 Apollo instances (not 4). Starting slot numbering at 0 makes this work without "reserving" any slot.
- The user's pre-paired `sunshine.conf` (template) becomes the streaming instance for the first monitor. This is intentional and unavoidable.
- Simpler model: no "reserved" anything, slot 0 is just the lowest slot ID.

## D23: `lance config` requires the slot to be running

**Decision:** `GET /slots/{id}/config-url` returns `409 Conflict` if the slot is allocated but not running. The CLI translates this to a clear error and exits non-zero.

**Rationale:**
- The web UI Lance tries to open is served BY the Apollo process. If Apollo isn't running, there's nothing to open.
- Cleaner to fail with an actionable error than to open a browser to a dead URL.
- No prompt to start the slot — Phase 1 is no-prompts.

**The query parameter `?redirect=1`** on this endpoint returns a 302 redirect to the URL instead of JSON, for programmatic browser-bookmark scenarios. Same auth required.

## D24: Apollo web UI URL is always HTTPS

**Decision:** The URL returned by `/slots/{id}/config-url` always uses `https://` because Apollo serves its web UI over HTTPS (self-signed). Browsers will warn; that's expected.

**Rationale:** Apollo's behavior, not Lance's. Lance is just relaying.

## D25: Agent owns Apollo lifecycle — graceful shutdown stops all managed processes

**Decision:** The agent registers a graceful-shutdown handler (`IHostApplicationLifetime.ApplicationStopping` in ASP.NET Core). On Ctrl+C / SIGTERM / console close, it iterates running slots and gracefully stops each Apollo process (with a kill timeout fallback).

**Rationale:**
- Leaves the system clean when the user stops the agent.
- If the agent is force-killed (taskkill /F), Apollo processes are orphaned. Next agent startup's clean-slate kill (D11) handles them.

## D26: Browser-open fallback

**Decision:** When `lance config <id>` tries to open the URL but the platform's open command fails (e.g., `xdg-open` not installed), print the URL to stdout with a "please open manually" message and exit successfully.

**Rationale:** A failed browser-open shouldn't fail the command — the user got the URL, they can open it. Robust default.

## Things deliberately NOT decided yet

These are deferred to later phases or future discussion. If you encounter them during Phase 1, raise with the user rather than assuming:

- Best-effort error handling behavior
- Window placement strategy on X11/Wayland/KDE/Windows specifically (KDE window rules via WM_CLASS is the leading candidate for KDE)
- Audio routing intelligence (audio inherited from template — same as everything else — is the Phase 1 behavior)
- Idle timeouts
- Bandwidth/codec/fps defaults — should come from client config with sensible starter values
- Whether `lance status` should also query the agent or just show local state (Phase 1: queries the agent for completeness)
- Token rotation flow (Phase 1 = manual edit + restart)
- Auto-detection of Apollo install path (Phase 2)
- Env var override for token (`LANCE_TOKEN`) (Phase 2)
- `lance slot <subcommand>` direct slot control commands (Phase 2)
- Version mismatch warning between client and agent (Phase 2)
