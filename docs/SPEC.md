# Lance — Spec (verified facts)

> Concrete values and contracts the implementation must match exactly. These are
> **verified facts** (ports, mutation rules, config shapes), not decisions —
> decisions and behavior live in ARCHITECTURE.md. Where this file and
> ARCHITECTURE.md ever disagree on *behavior*, ARCHITECTURE.md wins.
>
> Phase 1 scope. Grows over time. Single spec file — do not split.

## Constants
- Agent default port: **9876**
- Apollo web UI port = streaming port **+ 1**
- Slot port (clone N): `template_port - (N × portStep)`, `portStep = 1000` (**subtracts**)
- Max slots: **8**
- Apollo startup timeout: 30s (poll port via TCP every 500ms)
- Apollo stop timeout: 10s graceful wait, then force-kill. `[INVESTIGATE-STOP]` — Phase 1 testing shows graceful stop consistently times out; see PLAN.md.
- Apollo executable (Lance's direct-launch path): `sunshine.exe` (confirmed).
  Note: the *installed service* path runs `sunshinesvc.exe` + `apollo.exe`, which
  Lance does not use — see `[DEFER-SVC]`. Template config: `sunshine.conf`; clone
  config: `sunshine_{id}.conf`

## Slot model (agent)
```csharp
public sealed record SlotDto
{
    public int Id { get; init; }            // 0 = template, 1..N = clones
    public string Name { get; init; }       // "Lance-Template" (0), "Lance-{N}" (clones)
    public string Host { get; init; } // resolved host the client uses to reach this slot's Apollo instance
    public int Port { get; init; }
    public string Status { get; init; }     // "Allocated" | "Running" | "Connected"
    public string ConfigPath { get; init; }
    public string ConfigName { get; init; } // actual file name; "sunshine_{id}.conf" for standard slots
    public bool IsTemplate { get; init; }   // true only for slot 0
    public bool IsAdopted { get; init; }     // true = discovered via adoption; port is observed, not derived; no deallocate
    public DateTimeOffset AllocatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public int? ProcessId { get; init; }
}
```
`Host` is populated by the agent from its configured `listen.host`; if that value is `0.0.0.0`, `*`, or empty, the agent substitutes the machine's resolved hostname so the client always receives a usable address.
Slot 0: always allocated; can start/stop; **never deallocated**; its config file
is **never modified**. `Status = "Running"` is derived from a live PID;
`Status = "Connected"` is derived from a TCP probe on the slot's base port
(ESTABLISHED connection from a remote IP), performed by the agent at query time.
Authoritative slot state = on-disk config files (not stored by agent).

**Adopted non-standard slots:** a running `sunshine.exe` whose config does **not**
match `sunshine_{id}.conf` is adopted with a **reserved int id starting at 1000**
(incrementing). Its `Port` is the **observed** running-process port, not
`template_port − N×portStep` (port math applies only to Lance-allocated standard
slots). `IsAdopted = true`. Lance may **list and stop** these but must **never
start, allocate, deallocate, or modify** them — they are configs Lance did not
create and does not understand. (Adopted *standard* slots — matching the pattern —
are normal slots, not flagged.)

## Apollo config mutation (cloning template → slot N, N ≥ 1)

**Mutate these fields:**

| Field | Slot N value |
|---|---|
| `sunshine_name` | `Lance-{N}` |
| `port` | `template_port - (N × portStep)` |
| `log_path` | `sunshine_{N}.log` |
| `server_cmd` | `[]` |
| `stream_audio` | `disabled` |

**Inherit verbatim — do NOT touch:**
- `file_state` — **deliberately inherited unchanged.** All slots share the
  template's state file; this is how pairing credentials carry over
  automatically. **Mutating this silently breaks pairing.**
- `headless_mode`, `dd_configuration_option`, all encoder/display settings.

**Format:** INI-like, `key = value` per line, no section headers, no quoting.
`server_cmd` is a JSON array (empty = `server_cmd = []`). If a field is absent in
the template, append it after the last line. Preserve template line ordering.
**Template file is never modified.**

## Agent HTTP API (Phase 2)

> **Phase 2: HTTPS + optional bearer token auth.** JSON bodies, ISO-8601 UTC
> timestamps, integer ids.
>
> **TLS:** agent listens on HTTPS only. Self-signed cert generated on first run
> (`lance-agent.pfx` beside binary, or `tls.certPath`). Client unconditionally
> skips TLS cert validation in Phase 2 — cert validation will be configurable
> when PEM support is added (later phase).
>
> **Auth:** if `auth.token` is set in `lance-agent.json`, all non-`/health`
> requests must carry `Authorization: Bearer <token>` matching that value.
> If `auth.token` is absent the API is open. `GET /health` is always
> unauthenticated. Failed auth → `401 invalid_token`.

- `GET /health` → `{ status, version, uptimeSeconds, maxSlots, templatePath, templateExists }`
- `GET /slots` → `{ slots: [SlotDto, …] }`
- `POST /slots` — **allocate by target count**, idempotent. Body `{ "count": N }`
  ensures the pool has **N total slots, ids `0..(N-1)`**. Slot 0 (the template)
  always already exists and **counts as one** usable pool member; clones are
  created for any missing `1..(N-1)`. E.g. `count: 3` → slots 0, 1, 2 (clones 1
  and 2 created; slot 0 already present). Count below 1 or above max →
  `400 invalid_slot_id`; exceeds max → `400 max_slots_exceeded`. Errors:
  `400 invalid_slot_id`, `400 max_slots_exceeded`, `500 template_missing`,
  `500 io_error`.
- `GET /slots/{id}` → `SlotDto`. Not found → `404 slot_not_found`.
- `POST /slots/{id}/start` — spawn `sunshine.exe "<config>"`, wait for port bind,
  record PID. Already running → `200` (idempotent). Adopted (`IsAdopted`) →
  `409 cannot_start_adopted` (Lance never starts configs it didn't create).
  Errors: `404 slot_not_found`, `500 apollo_launch_failed`.
- `POST /slots/{id}/stop` — graceful close, wait 10s, force kill, clear PID.
  Not running → `200`. Error: `404 slot_not_found`.
- `DELETE /slots/{id}` — deallocate (remove config + log). **Refuses if running**
  → `409 slot_in_use` (stop it first, or use force-deallocate). Slot 0 →
  `409 cannot_deallocate_template`; adopted (`IsAdopted`) →
  `409 cannot_deallocate_adopted`; not found → `200` (idempotent).
- `POST /slots/{id}/force-deallocate` — stop if running, then deallocate. Same
  guards as DELETE except running is allowed (stopped first): Slot 0 →
  `409 cannot_deallocate_template`; adopted → `409 cannot_deallocate_adopted`;
  not found → `200`.
- `GET /slots/{id}/config` — `{ "url": "https://host:<port+1>" }`. Not
  running → `409 slot_not_running`; `?redirect=1` → `302`.


## Client CLI (Phase 1)

**Config resolution:** see "Agent ↔ client target resolution" above.

**Global options:** `--agent <url>|-a` (override agent URL), `--config <path>|-c`, `--token <value>|-k` (bearer token, overrides `agent.token` in config), `--verbose|-v` (debug to stderr), `--no-color`.

Token resolution (first match wins): `--token` CLI flag → `lance.json` `agent.token` → no token sent (works if agent has no token configured).

**Commands:** `lance slots`, `lance status`, `lance config <slot_id>`
(opens config URL: `xdg-open` / shell-execute; on failure print URL, exit 0).

`lance monitors` — list local physical monitors (ID, name, resolution, position,
primary flag). No agent required. Use to pick IDs for `--monitors`.

`lance connect [--monitors <list>]` — Phase 2 client-driven connect. `--monitors`
is comma-separated 1-indexed physical monitor IDs; default: all physical monitors
(requires OS display enumeration). Includes free-slot check (exit 2 if no capacity).

`lance disconnect [--slots <list>] [--keep-running] [--purge]` — kill Moonlight,
optionally stop Apollo, optionally deallocate. See ARCHITECTURE.md disconnect flow.

> **OS display enumeration:** Windows uses `EnumDisplayDevicesW` + `EnumDisplaySettingsExW`
> (`user32.dll`). Linux uses Xrandr 1.5 via `libX11`/`libXrandr` P/Invoke — requires
> X11 or XWayland. Pure Wayland without XWayland is not supported yet. A more robust
> approach covering both X11 and native Wayland natively is planned for a later phase.

**Exit codes:** 0 success · 1 generic · 2 no free slots (all running slots are connected) · 3 agent unreachable · 4 agent error · 5 Moonlight launch failed · 6 slot not in
required state · 7 config resolution failed.

## Config files

**Agent — `lance-agent.json`** (beside binary): `listen{host,port}`,
`tls{certPath}` (optional; defaults to `lance-agent.pfx` beside binary),
`auth{token}` (optional; omit to disable auth),
`remoteServer{installDir,configDir,executable,templateConfigName,startupTimeoutSeconds}`,
`slots{maxCount,portStep,stopTimeoutSeconds,namePrefix,templateName,configNamePattern}`,
`logging{level,filePath,retainDays}`.

**Client — `lance.json`**: `agent{url,token,timeoutSeconds}`,
`remoteClient{executable,defaultFlags}`, `ui{color}`, `logging{level,filePath}`.
`remoteClient.executable`: `moonlight.exe` (Win) / `moonlight` (Linux). CLI flags
append after `defaultFlags` (later args win in Moonlight). TLS cert validation is
unconditionally disabled in Phase 2 (self-signed cert); `agent.url` must use `https://`.

### Linux file-path conventions `[DEFER-PATHS]`

All default file paths follow Windows / "run from a folder" conventions and are
acceptable for Phase 2 (both binaries run manually). They need revisiting when a
proper daemon or service install is added. Deferred items:

| Item | Current path (Phase 2) | Linux standard |
|---|---|---|
| Agent config file | beside binary (`AppContext.BaseDirectory`) | `/etc/lance-agent/` (system) or `~/.config/lance-agent/` (user) |
| TLS certificate (`lance-agent.pfx`) | beside binary | `/etc/lance-agent/` or `/var/lib/lance-agent/` |
| Agent log file (`lance-agent.log`, relative) | cwd / beside binary | `/var/log/lance-agent/` |
| Client config file (`lance.json`) | beside binary | `~/.config/lance/` (XDG) |
| Apollo install / config paths (agent defaults) | `ProgramFiles\Apollo` | empty string; `[VERIFY-APOLLO]` unresolved |

Agent paths: defer to **Phase 4** (daemon/service install). Client config path:
defer to **Phase 3** (XDG compliance).

## Moonlight launch

The client launches one Moonlight per slot, using **that slot's Apollo host+port**
returned by the agent (the client does no port math):

```
moonlight stream <slot.Host>:<slot.Port> Desktop [defaultFlags…] [--resolution <WxH>] [--options tokens…]
```
- `slot.Host` / `slot.Port` come from `SlotDto` as returned by the agent — one
  Moonlight per slot. Port is always explicit. The client never derives these values.
- Stream name is `Desktop`.
- **Arg order (later wins in Moonlight):** `defaultFlags` from config → per-monitor
  `--resolution <WxH>` (from the mapped monitor; omitted if display detection failed)
  → `--options` tokens (whitespace-split). So per-monitor resolution overrides the
  config default, and `--options` overrides everything.
- Spawn as **detached children**; track PID only.
- **Launch gate (connect):** a slot is launched only if no running Moonlight already
  targets its `<host>:<port>` (command-line match) — prevents duplicates, enables reconnect.
- Verified flags: `--fps <n>`, `--video-codec <HEVC|H264|AV1>` (uppercase),
  `--bitrate <kbps>`, `--no-vsync`, `--resolution <WxH>`, `--display-mode <fullscreen|windowed|borderless>`.
- **Moonlight cannot target a specific physical monitor** (no such CLI flag; it picks
  the largest screen). `--monitors` selects stream count + per-stream resolution only.

## Agent ↔ client target resolution

Two distinct host:port pairs — do not conflate:
- **Agent host:port** — how the *client* reaches the *agent*. Resolution
  (first match wins; exit 7 if none yield a URL):
  1. `--agent <url>` / `-a` CLI flag
  2. `--config <path>` / `-c` explicit config file → `agent.url`
  3. `lance.json` beside exe → `agent.url`
- **Apollo host:port** — how each *Moonlight* reaches its *Apollo* instance.
  The client never picks these; the agent returns them per slot. The client
  **is slot-aware**: it consumes the returned slot info to launch the matching
  Moonlight.


## Agent lifecycle (Phase 1)

**Prerequisite (Phase 1, manual):** the user **stops the Apollo service**
(shortcut/installed service = `sunshinesvc.exe` watchdog + `apollo.exe` worker)
before running Lance. Lance only ever manages Apollo instances **it launches
directly** (`sunshine.exe "<config>"`, no watchdog). Auto-managing the service is
deferred — `[DEFER-SVC]`.

**Listen address:** the agent calls `WebHost.UseUrls("http://{host}:{port}")` from
`listen` config immediately after `CreateSlimBuilder`. This explicitly overrides
`ASPNETCORE_URLS`, `launchSettings.json`, and any other environment-injected URL.
Phase 1 is HTTP only — the HTTPS profile in `launchSettings.json` must not be
used and will fail if it reaches Kestrel. Phase 2 replaces `UseUrls` with proper
Kestrel HTTPS/TLS configuration.

**Startup:** read config → (Windows) require admin, fail fast if not elevated →
set up logging → validate config (Apollo exe, config dir, template file) fail-fast
→ bind listener → **adopt: scan for running `sunshine.exe` and attribute each to
a slot** (these are direct-launched instances, e.g. survivors of a prior agent
run — reuse them rather than killing) → scan config dir for the rest (template +
`sunshine_{id}.conf`) → mark slots Allocated (with PID if a live process was
adopted, else no PID) → serve.

**Adopting a running `sunshine.exe` → which slot:**
1. **Command line first (strong signal):** read the process's launch args for the
   config path; the `sunshine_{id}.conf` name pins the slot id directly.
2. **Bound port (fallback):** if the command line isn't readable, match the
   process's bound port against each slot's expected port (`template_port −
   N×portStep`). `[DEFER-WIN-ADOPT]` — **Phase 1 only implements step 1**
   (Linux via `/proc/{pid}/cmdline`; Windows adoption is a full no-op). Step 2
   port-matching and Windows adoption deferred to Phase 2.
3. **Non-standard (neither matches):** the process runs a config that is not a
   standard `sunshine_{id}.conf` and binds no expected port → adopt as a
   **non-standard slot** (reserved id ≥1000, observed port, `IsAdopted = true`,
   record its `ConfigName`). Observe + stop only; never start/allocate/deallocate.

**Graceful shutdown** (`ApplicationStopping`): stop accepting requests → stop each
running slot (graceful, wait 10s, force kill) → flush logs → exit. A hard
kill/power loss leaves Apollo instances running; the next startup **adopts** them
(see startup, above).

> **`[VERIFY-APOLLO]`** — Apollo needs admin on **Windows** (confirmed). **Linux:
> privilege model untested/unknown** — verify or ask before assuming. (Executable
> name for Lance's direct-launch path is `sunshine.exe`; confirmed.)

> **`[DEFER-SVC]`** — auto-managing the Apollo service/watchdog (so the user
> needn't stop it by hand) is deferred, likely Phase 4. The watchdog
> (`sunshinesvc.exe`) resurrects `apollo.exe`, which would fight Lance owning
> slots (esp. slot 0). Phase 1 sidesteps it by the manual prerequisite above.

## Error response format
```json
{ "error": "code_string", "message": "Human readable", "details": {} }
```
Error codes: `slot_not_found`, `slot_not_running`, `slot_in_use`,
`cannot_deallocate_template`, `cannot_deallocate_adopted`, `cannot_start_adopted`,
`template_missing`, `apollo_launch_failed`, `invalid_slot_id`,
`max_slots_exceeded`, `io_error`, `internal_error`, `invalid_token`.
*(`slot_in_use` = `DELETE /slots/{id}` on a running slot; use
`POST /slots/{id}/force-deallocate` to stop-then-deallocate instead.)*
*(`invalid_token` = missing or wrong `Authorization: Bearer` header on a
protected endpoint.)*

## Build / project setup

- **.NET 10**, `PublishAot=true` in every project from day one (enforces
  no-reflection discipline early). `Nullable` + `ImplicitUsings` enabled.
- **Three projects:** `Lance.Agent` (Sdk.Web), `Lance.Client` (Exe), **`Lance.Shared`**
  (DTOs + JSON source-gen contexts). Shared exists so the client never drags in
  ASP.NET Core. Binary names via `AssemblyName`: `lance-agent`, `lance`.
- **JSON:** System.Text.Json with **source generators** only. **Newtonsoft.Json is
  forbidden** (not AOT-safe). camelCase keys.
- **CLI:** `System.CommandLine` for parsing (AOT-safe); **Spectre.Console for
  rendering only** (tables/colors) — not Spectre.Console.Cli (AOT issues).
- Central package management (`Directory.Packages.props`).

> **Package versions:** the old spec pinned specific versions (~3 weeks stale).
> **Do not trust them blindly** — verify latest stable compatible with .NET 10 at
> first build. `[VERIFY-VERSIONS]`

## Logging
Format and per-level detail: **AI to propose, owner approves.** (Baseline: agent
= console + rolling daily file; client = stderr in Phase 1.)
