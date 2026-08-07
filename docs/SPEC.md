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
- Apollo stop timeout: 10s graceful wait, then force-kill. `[INVESTIGATE-STOP]` **Resolved (Phase 2).** `CloseMainWindow()` returns `false` on Apollo's headless process; the agent now skips the wait and calls `Kill()` directly when that happens.
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
`Status = "Connected"` is derived from **UDP endpoint presence** at query time — the
slot's Apollo process owns one or more of its streaming UDP ports (base `+9/+10/+11`;
see "Sessions & orchestration → UDP detection port map"). (The former TCP-ESTABLISHED
probe was retired — it never fired during a live stream; `[VALIDATE-UDP]`,
2026-07-11.) Authoritative slot state = on-disk config files (not stored by agent).

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
| `file_state` | `sunshine_{N}_state.json` |
| `credentials_file` | `sunshine_{N}_state.json` |

Both `file_state` and `credentials_file` are set to the **same per-clone path**
(matching Apollo's default where both fields point to one file). Each clone gets
a unique server UUID this way; Moonlight must be paired with each clone separately.
Slot 0's state file is never touched — the template keeps its own pairing state.

**Inherit verbatim — do NOT touch:**
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

**Commands:** `lance slots`, `lance status`, `lance config <ids>`
(opens config URL: `xdg-open` / shell-execute; on browser-open failure print URL).

**Multi-id slot commands:** `start`, `stop`, `deallocate`, and `config` take a
**comma-separated list of slot ids** (`<ids>`, e.g. `1,2,3`; a single id like `1`
still works). Ids are de-duplicated (duplicate → warning, kept once); a non-integer
or empty token (stray comma) is a parse error → exit 1. Each id is processed
independently as its own agent call — **partial success**: a per-slot agent error is
logged and the next id proceeds; the command exits 0 only if every id succeeded,
else exit 1. An **unreachable agent short-circuits** the loop (exit 3) rather than
timing out once per remaining id. `--force` on `deallocate` applies to every id.

`lance monitors` — list local physical monitors: **ID, monitor name, device name,
resolution, refresh rate, position, primary flag**. No agent required. Use it to pick
IDs for `--monitors` and keys for `monitorOptions` / `--monitor-options`.

> **Refresh rate** comes from `DEVMODE.dmDisplayFrequency` (offset 184) on Windows and
> is a whole number — a 59.94 Hz panel reports 59. Windows uses **0 or 1 to mean "the
> hardware default"**, so both are treated as *unknown* and shown as `—`; no `--fps`
> is generated for such a monitor. Unknown is also the state of every monitor on
> Linux — `[DEFER-LINUX-REFRESH]`.
>
> **Monitor name** is the EDID friendly name ("BenQ GW2480", "Optix MAG27CQ"). On
> Windows it comes from the CCD API — `GetDisplayConfigBufferSizes` →
> `QueryDisplayConfig` → `DisplayConfigGetDeviceInfo(GET_TARGET_NAME)` for
> `monitorFriendlyDeviceName`, joined to the GDI enumeration via `GET_SOURCE_NAME`'s
> `viewGdiDeviceName`. WMI is not an option: `System.Management` is not AOT-safe. On
> Linux the Xrandr output name (`HDMI-1`) serves. Any failure degrades to no name,
> and the monitor stays addressable by id.

### Monitor keys

Every user-written reference to a monitor — `--monitors`, `monitorOptions` and
`--monitor-options` — resolves the same way: **all-digits → monitor id; otherwise →
monitor name**.

- Name matching is **exact and case-insensitive**, against the friendly name *or* the
  device name (`\\.\DISPLAY2`). Substring matching is deliberately not supported — it
  is silently ambiguous across similar models.
- A key matching **no** monitor is a **warning** and is skipped, matching the
  `--monitors` precedent for unknown ids.
- A name matching **more than one** monitor (two panels of the same model) **fails
  fast**, listing the ids and telling the user to key by id.
- Two references resolving to the **same** monitor — `"1"` and `"01"`, or an id and
  that monitor's name — **fail fast** in `--monitors` and in `monitorOptions`. The
  check is made **after** resolution, so `--monitors 1,U28E590` naming one screen is
  caught rather than silently claiming two slots. On the CLI, repeated
  `--monitor-options` for one monitor **append** instead, whichever form the key took.

Names may contain spaces (`BenQ GW2480`), so quote the whole value:
`--monitors "BenQ GW2480,U28E590"`. `--monitors` splits on commas only, so a monitor
name containing a comma cannot be used — address it by id.

Ids are stable only within one enumeration: unplugging or reordering displays can
shift them, so a per-monitor option would then land on the wrong screen. Names do not
have that failure mode and are the safer key where a monitor has one.

`lance connect [--monitors <list>]` — Phase 2 client-driven connect. `--monitors`
is a comma-separated list of monitor references, each a 1-indexed id **or a monitor
name** (see "Monitor keys"); default: all physical monitors, in enumeration order
(requires OS display enumeration). Includes free-slot check (exit 2 if no capacity).

`lance disconnect [--session-id <id>] [--keep-running] [--purge]` — kill Moonlight,
stop Apollo (unless `--keep-running`), optionally deallocate. See ARCHITECTURE.md disconnect flow.

> **OS display enumeration:** Windows uses `EnumDisplayDevicesW` + `EnumDisplaySettingsExW`
> (`user32.dll`). Linux uses Xrandr 1.5 via `libX11`/`libXrandr` P/Invoke — requires
> X11 or XWayland. Pure Wayland without XWayland is not supported in Phase 2; native
> Wayland enumeration (`xdg-output` / `wlr-output-management`) is Phase 3 Slice 2.

**Exit codes:** 0 success · 1 generic · 2 no free slots (all running slots are connected) · 3 agent unreachable · 4 agent error · 5 Moonlight launch failed · 6 slot not in
required state · 7 config resolution failed.

## Config files

**Agent — `lance-agent.json`** (beside binary): `listen{host,port}`,
`tls{certPath}` (optional; defaults to `lance-agent.pfx` beside binary),
`auth{token}` (optional; omit to disable auth),
`remoteServer{installDir,configDir,executable,templateConfigName,startupTimeoutSeconds}`,
`slots{maxCount,portStep,stopTimeoutSeconds,namePrefix,templateName,configNamePattern}`,
`logging{level,frameworkLevel,filePath,retainDays}`.
`logging.frameworkLevel` (default `"Warning"`): minimum level for framework
(`Microsoft.*`) log sources — `"Warning"` keeps genuine faults visible while dropping
the Info/Debug request/connection noise; a lower level (`"Debug"`/`"Information"`)
opens the web stack; `"off"`/`"none"` drops framework logs entirely.

**Client — `lance.json`**: `agent{url,token,timeoutSeconds}`,
`remoteClient{executable,defaultOptions,monitorOptions,bitrateMode}`, `ui{color}`,
`logging{level,filePath}`. `remoteClient.bitrateMode` selects bitrate sizing (see
"Bitrate sizing"; unset = `balanced`). `remoteClient.monitorOptions` is an optional map of
**monitor key → option token array**, applied to that monitor only (layer 2 above).
See "Monitor keys" for how a key resolves.
`remoteClient.executable`: `moonlight.exe` (Win) / `moonlight` (Linux). CLI options
append after `defaultOptions` (later args win in Moonlight). TLS cert validation is
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

Agent paths: defer to **Phase 3** (agent service/daemon install slice). Client config
path: defer to **Phase 3** (XDG compliance).

## Moonlight launch

The client launches one Moonlight per slot, using **that slot's Apollo host+port**
returned by the agent (the client does no port math):

```
moonlight stream <slot.Host>:<slot.Port> Desktop [--resolution <WxH>] [defaultOptions…] [monitorOptions[id]…] [--options…] [--monitor-options[id]…]
```
- `slot.Host` / `slot.Port` come from `SlotDto` as returned by the agent — one
  Moonlight per slot. Port is always explicit. The client never derives these values.
- Stream name is `Desktop`.
- **Arg order (later wins in Moonlight)** — config before CLI, general before
  specific:

  | Layer | Source | Scope |
  |---|---|---|
  | 0 | generated `--resolution <WxH>` from the mapped monitor (omitted if display detection failed) | one monitor |
  | 0 | generated `--fps <min(refreshRate, 60)>` (omitted when the rate is unknown) | one monitor |
  | 1 | `remoteClient.defaultOptions` | all monitors |
  | 2 | `remoteClient.monitorOptions[<id>]` | one monitor |
  | 3 | `--options` (whitespace-split) | all monitors |
  | 4 | `--monitor-options "<id>=<options>"` (repeatable) | one monitor |
  | 5 | derived `--bitrate <kbps>` (automatic modes only — see below) | one monitor |

  Layer 0 is **generated, and deliberately emitted first so any later layer overrides
  it** — e.g. `"monitorOptions": { "3": ["--resolution", "2560x1440"] }` streams a 4K
  panel at 1440p, cutting that stream's encoder load and bandwidth by ~55%.
  **Generated `--fps` ceiling: 60.** A 144 Hz panel receives `--fps 60`; a desktop
  gains little from the extra frames while the agent's encoder and uplink pay for all
  of them. Streaming above 60 is done explicitly, via `--fps` in any layer 1–4.

### Bitrate sizing

Each stream's bitrate is sized from the pixels it actually carries, rather than one
value shared by monitors of different resolutions. Selected by
`remoteClient.bitrateMode` or `--bitrate-mode` (**the flag wins**):

| Mode | Behaviour | bits/pixel |
|---|---|---|
| `high` | automatic | 0.16 |
| `balanced` | automatic — **default** | 0.10 |
| `conservative` | automatic | 0.06 |
| *a number* | automatic, caller-supplied | as given; **must be 0.01–1.0**, else exit 1 |
| `manual` | no derivation — the bitrate is whatever the options say | — |

A bare number is **bits per pixel, never kbps**; the 0.01–1.0 guard is what stops
`--bitrate-mode 20000` ("20 Mbps") being read as 20000 bits per pixel.

**Formula:** `kbps = round-to-nearest-1000( width × height × fps × bitsPerPixel / 1000 )`,
floor 1000. `width × height` is the **streamed** resolution (after any layer override,
so lowering a 4K panel to 1440p also lowers its bitrate); `fps` is the effective frame
rate **capped at 60** for the arithmetic only — the stream still runs at whatever
`--fps` says, and exceeding 60 logs a warning that the budget is 60fps-equivalent.

| Tier | 1080p60 | 1440p60 | 4K60 |
|---|---|---|---|
| `high` | 20 Mbps | 35 Mbps | 80 Mbps |
| `balanced` | 12 Mbps | 22 Mbps | 50 Mbps |
| `conservative` | 7 Mbps | 13 Mbps | 30 Mbps |

**Precedence against an explicit `--bitrate`.** The derived value applies *unless* an
explicit `--bitrate` survives the merge at a layer **≥** the layer that set the mode.
Explicit wins ties. Mode source layers: `--bitrate-mode` = **3**, `bitrateMode` config
= **1**, unset = **0**.

| `--bitrate-mode` | `bitrateMode` | explicit `--bitrate` | Result |
|---|---|---|---|
| — | — | — | automatic `balanced` |
| — | — | present | explicit used, nothing derived *(no warning — the ordinary case)* |
| — | set | absent | automatic at the config mode |
| — | set | present | explicit wins + warn |
| set | any | absent | automatic at the flag's mode |
| set | any | present in config (layers 1–2) | derived overrides + warn |
| set | any | present on the CLI (layers 3–4) | explicit wins + warn |

Consequence: a config carrying an explicit `--bitrate` and no mode keeps working
untouched — automatic sizing is opted into by **removing** the flag.
- **`defaultOptions` carries no `--bitrate`, no `--fps` and no `--yuv444`.** 4:4:4
  forces HEVC Range Extensions, which many GPUs cannot decode on their fast path — it
  silently drops the client onto a slower fallback decoder (validated 2026-08-05: an
  RTX 2060 SUPER logged `GPU doesn't support HEVC Main 444 8-bit decoding via
  D3D11VA` and fell back to Vulkan video). `--bitrate` and `--fps` are omitted so
  each stream is sized from the monitor it targets; one value shared across monitors
  of different resolutions mis-allocates bandwidth. With neither set, Moonlight
  applies its own per-resolution defaults until `STREAM_TUNING_SPEC` slice 2.4 lands.
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
> needn't stop it by hand) is a **Phase 3** slice (not yet done). The watchdog
> (`sunshinesvc.exe`) resurrects `apollo.exe`, which would fight Lance owning
> slots (esp. slot 0). Until that slice, the manual prerequisite above stands.

## Error response format
```json
{ "error": "code_string", "message": "Human readable", "details": {} }
```
Error codes: `slot_not_found`, `slot_not_running`, `slot_in_use`,
`cannot_deallocate_template`, `cannot_deallocate_adopted`, `cannot_start_adopted`,
`template_missing`, `apollo_launch_failed`, `invalid_slot_id`,
`max_slots_exceeded`, `io_error`, `internal_error`, `invalid_token`,
`session_id_conflict` (Phase 3 — connect handshake, requested session id already
active; `409`), `invalid_session_id` (Phase 3 — bad id charset/length; `400`),
`no_free_slots` (Phase 3 — not enough free slots for the session; `409`),
`session_not_found` (Phase 3 — `GET /sessions/{id}` on an inactive session; `404`).
*(`slot_in_use` = `DELETE /slots/{id}` on a running slot; use
`POST /slots/{id}/force-deallocate` to stop-then-deallocate instead.)*
*(`invalid_token` = missing or wrong `Authorization: Bearer` header on a
protected endpoint.)*

## Sessions & orchestration (Phase 3)

> Verified/decided values for the session/event/hook subsystem. Behavior lives in
> ARCHITECTURE ("Sessions & tool orchestration"); design rationale in
> `docs/TOOL_ORCHESTRATION_SPEC.md`. Values marked **(proposed)** are new surface
> introduced during Slice 0 doc reconciliation and await owner sign-off.

**Session id.** Client-minted default `Guid.NewGuid().ToString("N")` (32 lowercase
hex); override via `--session-id <string>`. **Charset: 1–64 chars of `[A-Za-z0-9_-]`**
(the id becomes a record file name; this also blocks path traversal). Invalid →
`400 invalid_session_id`. Sent on the connect handshake; the agent enforces **global
uniqueness across active sessions** — collision → `409 session_id_conflict`, client
surfaces and stops (no retry).

**Session states:** `Provisioned` | `Connected` | `Ended` (see ARCHITECTURE for
transitions). **Provision grace window default 30s (proposed configurable).**

**`source` values** (carried in the event payload):
`explicit | pid_watch | probe_watch | ping | reconcile | provision_timeout`.

**Event payload — injected as environment variables** into every spawned hook
process; the same set backs `${VAR}` substitution in hook `args[]`:

| Var | Scope |
|---|---|
| `LANCE_EVENT` | always |
| `LANCE_EVENT_SOURCE` | always |
| `LANCE_SESSION_ID` | always |
| `LANCE_SIDE` | always (`agent` / `client`) |
| `LANCE_AGENT_IP` | always |
| `LANCE_CLIENT_IP` | always |
| `LANCE_SLOT_IDS` | session-tier (e.g. `1,3`) |
| `LANCE_SLOT_ID` | slot-tier only |

At replay, `LANCE_EVENT` / `LANCE_EVENT_SOURCE` are set **fresh**
(`session_ended` / `reconcile`), never restored from the snapshot.

### Hook file format (JSON)

```json
{
  "name": "vox",
  "events": {
    "session_started": {
      "priority": 1000,
      "commands": [
        { "command": "audiohelper.exe", "args": ["backup", "audio-config"], "onError": "terminate" },
        { "command": "audiohelper.exe", "args": ["switch", "audio", "--playback", "VB Cable A", "--capture", "VB Cable B"] },
        { "command": "audiohelper.exe", "args": ["launch-vox", "--peer", "${LANCE_CLIENT_IP}"] }
      ]
    },
    "session_ended": {
      "commands": [
        { "command": "audiohelper.exe", "args": ["kill-vox"] },
        { "command": "audiohelper.exe", "args": ["restore", "audio-config"] }
      ]
    }
  }
}
```

| Field | Level | Default | Meaning |
|---|---|---|---|
| `name` | file | — | Descriptive only, non-unique, for logging. Optional. |
| `priority` | event | 1000 | Orders **files** bound to the same event. Lower runs first. Ties → file load order. Within a file, `commands` run in array order. |
| `command` | command | — | Executable. `command` + `args[]` → `ProcessStartInfo.ArgumentList`. No shell. |
| `args` | command | `[]` | Argument array; supports `${VAR}` substitution (resolved by Lance before spawn). |
| `async` | command | `false` | `false` = wait for exit before next command; `true` = spawn and don't wait. |
| `onError` | command | `terminate` | `terminate` = stop the **rest of this file's** commands on nonzero exit (other files bound to the same event still run — files are independent tools); `continue` = log and proceed. Meaningless for `async: true`. A timed-out `async: false` command counts as a failure and applies `onError` too. |
| `timeoutSeconds` | command | 30 | Applies only to `async: false`. On timeout: log, then apply `onError`. |
| `workingDir` | command | dir containing the hook file | Working directory for the spawn. |

### Session record (crash recovery)

- **Path:** `%ProgramData%\Lance\sessions\<id>.json` (Windows). Linux path
  `[DEFER-PATHS]`. Written **atomically** (temp + rename).
- **Contents:** session id, client IP, slot ids, the **resolved teardown command
  list**, and the **env payload snapshot**.
- **Lifecycle:** persist BEFORE `session_started` hooks; delete AFTER
  `session_ended` hooks complete. Present-at-startup ⇒ teardown never ran.

### UDP detection port map (validated 2026-07-11)

Streaming endpoints are UDP, derived from the slot's base `port` (the `port` config
value). Apollo's fixed offsets, confirmed against a live stream:

| Stream | Offset | Example (base 47989) |
|---|---|---|
| Video | base + 9 | 47998 |
| Control | base + 10 | 47999 |
| Audio | base + 11 | 48000 |

A slot is `Connected` when its Apollo process owns **any** of these. The offsets live
in the host-adapter seam (`IStreamingPortMap` / `ApolloStreamingPortMap`), swappable
per host. Detection is Windows-only for now (`GetExtendedUdpTable`); Linux endpoint
enumeration is deferred (`[VERIFY-APOLLO]`).

### New endpoints

- **`POST /sessions`** (connect handshake) — Request `{ "sessionId": "<id>", "count": N }`.
  The agent vets the id, picks N **free** slots (not held by another active session,
  not `Connected`; allocates/starts as needed — session-aware), persists the record,
  runs agent `session_started` hooks, and responds `{ "sessionId", "slots": [SlotDto…] }`
  with the running slots (partial success: a slot that fails to start is dropped).
  `POST /slots` stays allocation-only and creates no session. Errors:
  `400 invalid_session_id`, `400 invalid_slot_id` (count < 1), `409 session_id_conflict`,
  `409 no_free_slots`, `500 apollo_launch_failed`.
- **`GET /sessions`** → `{ "sessions": [SessionResponse…] }` — active sessions with
  their slots (the no-id disconnect enumerates these).
- **`GET /sessions/{id}`** → `SessionResponse` — one session's slots (the disconnect
  fast-path). Not found → `404 session_not_found`.
- **`DELETE /sessions/{id}`** — clean-disconnect ping → `session_ended(ping)`. Fast-path
  only (probe-watch backstops it). Idempotent; unknown/ended id → `200`.

### New CLI surface

- `lance connect [--monitors <list>] [--options "<flags>"] [--monitor-options "<id>=<options>" …] [--bitrate-mode <mode>] [--session-id <id>] [--hook <path> …]`
  — now a **foreground daemon** (blocks until the session ends). `--hook` is
  repeatable and additive over the client config `hooks` list.
  `--monitor-options` is repeatable, one monitor per occurrence, and **appends** when
  given more than once for the same monitor. A malformed entry (no `=`, or an empty
  monitor) fails fast (exit 1). The key is an id or a monitor name — see "Monitor
  keys". A monitor that is valid but not part of this connect is a **warning**, and its
  options are ignored.
  `--bitrate-mode` is global (it applies to every stream in the connect) and overrides
  `remoteClient.bitrateMode`. An unrecognised mode, or a number outside 0.01–1.0, fails
  fast (exit 1). See "Bitrate sizing".
- `lance disconnect [--session-id <id>] [--keep-running] [--purge] [<host:port> …]`
  — session-based; kill Moonlights (agent fast-path to resolve the session's slots,
  or explicit `host:port` fallback when the agent is unreachable). Ending a session
  **stops its slots' Apollo by default** (via the ping — tears down the virtual displays);
  `--keep-running` (ping carries `keepRunning=true`) leaves Apollo up for a fast reconnect;
  `--purge` stops+deallocates the session's slots (Slot 0 excluded, wins over
  `--keep-running` with a warning). No id → all sessions.

### New config surface

- **Agent `lance-agent.json`** (implemented): `hooks: [{ "active": true, "path": "…" }]`
  (omitted `active` defaults to true), and a
  `sessions: { "provisionGraceSeconds": 30, "probePollSeconds": 1, "recordDir": "…" }`
  block (defaults shown; `recordDir` defaults to `%ProgramData%\Lance\sessions`).
- **Client `lance.json`** (implemented): `hooks: [{ "active": true, "path": "…" }]`
  (omitted `active` defaults to true), plus repeatable `--hook <path>` additive over the
  config list. `lance connect` is now a foreground daemon (blocks until the session ends)
  and accepts `--session-id`.
- **Hook path resolution:** a relative `path` in a config `hooks` entry resolves against
  that **config file's directory** (so `hooks/foo.json` is found beside the config
  regardless of the working directory). A CLI `--hook <path>` stays relative to the
  current directory. Inactive (`active: false`) entries are skipped and logged at Debug.

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

Both sides log their own domain narrative, not their implementation stack. The agent
quiets framework (`Microsoft.*`) log sources to `Warning` by default (`logging.frameworkLevel`)
— dropping the Info/Debug Kestrel connection/TLS, routing, and hosting-banner noise while
keeping genuine warnings/errors — and states the useful facts itself (`Lance agent <ver>
starting`, adoption, `Listening on <url>`). The client emits no framework logs (bare CLI).
Lower `frameworkLevel` to debug the web/TLS stack; `"off"`/`"none"` drops framework logs
entirely. Client console/status output uses
`Minimal`-bordered tables for genuinely tabular data (slots, status, monitors); single
results are plain messages.
