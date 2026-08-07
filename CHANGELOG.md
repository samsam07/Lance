# Changelog

All notable changes to Lance are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**Lance is pre-release.** Nothing has been tagged or published yet. All work
accumulates under `0.1.0` — the version stamped into both binaries and checked by
the client/agent compatibility handshake — and is *not* incremented for features
or fixes until the first official release. The `0.1.0` heading therefore carries
`Unreleased` in place of a release date: a deliberate, small deviation from Keep a
Changelog that keeps this section aligned with the version `lance status` reports.

## [0.1.0] — Unreleased

The first working version. Everything below is new; there is no prior release for
anything to be changed, deprecated, or fixed against.

### Added

#### Core

- **Two cooperating binaries** — `lance-agent`, a web API that manages Apollo
  instances on the remote Windows machine, and `lance`, the CLI that drives a
  session from the local machine (Windows or Linux).
- **Slot model** — a *slot* is one Apollo instance plus its configuration. Slot 0
  is the original Apollo config and acts as the clone template; it is never
  modified or deallocated. Slots 1…N are clones, each with its own port block,
  server identity, and state file.
- **Partial success throughout** — `connect` and `disconnect` are best-effort per
  monitor and per slot: a failure is logged and skipped while the rest proceed.
  Two monitors out of three beats none.
- **Slot discovery and process adoption** — the agent infers slots from the Apollo
  config directory and adopts Apollo instances that were already running when it
  started, attributing each to a slot by config path and falling back to its
  observed port. Instances it cannot attribute are registered as adopted.
- **Version stamp and compatibility check** — both binaries report `0.1.0`, and
  `connect` and `status` compare the client's version against the agent's.

#### Sessions

- **`lance connect` opens a session and blocks** — it performs a handshake with
  the agent, brings up one Apollo slot per target monitor, launches one Moonlight
  window per slot, then stays in the foreground until the session ends. Ends on
  Ctrl-C, when the last Moonlight exits, or when `lance disconnect` runs from
  another terminal.
- **`--session-id`** names a session explicitly; a colliding id is refused.
- **Session state machine and crash-recoverable records** — sessions move
  `Provisioned → Connected → Ended`, and each is persisted atomically to
  `%ProgramData%\Lance\sessions\<id>.json` before setup and deleted after
  teardown.
- **Crash recovery** — on startup the agent probes the slots of every surviving
  session record before opening its listener: any still connected is re-adopted,
  and sessions whose slots have all gone idle have their teardown replayed. Apollo
  is left running on this path.
- **Agent-side teardown backstop** — the agent watches the streams itself, so a
  session is cleaned up even if the client machine disappears mid-stream (a few
  seconds after a hard cut) or never produces a first stream within the
  provisioning grace period.
- **Clean-disconnect ping** — the client notifies the agent on teardown, giving a
  fast, deterministic end instead of waiting for stream-loss detection.
- **Slots are stopped when their session ends**, bringing the remote virtual
  displays back down. `lance disconnect --keep-running` opts out for a fast
  reconnect; `--purge` also deallocates the slots (Slot 0 excluded) and takes
  precedence over `--keep-running` with a warning.
- **`lance disconnect` addresses sessions by id**, or ends all active sessions
  when given none. Passing `host:port` instead kills the matching Moonlight
  processes directly — the fallback for when the agent is unreachable.
- **UDP stream detection** — connection state is derived from UDP endpoint
  presence on the slot's port block, which detects a connect in about a second.

#### Monitors and stream tuning

- **`lance monitors`** lists the local physical monitors — id, friendly name,
  resolution, refresh rate, position, and which is primary. Needs no agent.
  Friendly names come from EDID via the Windows CCD API, or the Xrandr output name
  on Linux.
- **`connect --monitors`** targets specific monitors by id, by name, or a mix.
  Every monitor reference in Lance resolves through one shared resolver, so ids and
  names behave identically wherever they are accepted. An unknown name warns and
  skips; a name matching two identical panels fast-fails.
- **Automatic per-monitor sizing** — each stream is sized from the monitor it
  targets: `--resolution` from its resolution and `--fps` from `min(refresh, 60)`,
  omitted when the refresh rate is unknown.
- **Automatic bitrate** — `bitrateMode` in config and `--bitrate-mode` on the CLI
  accept `high`, `balanced` (default), `conservative`, `manual`, or an explicit
  bits-per-pixel value, derived from each monitor's *streamed* resolution rather
  than one shared figure across mismatched panels.
- **`--options`** appends Moonlight flags to every stream; **`--monitor-options`**
  (repeatable, keyed by monitor id or name) gives one screen its own settings.
  Generated options sit at the base layer so any of them can be overridden.
  Options aimed at a monitor outside the connect, or monitors left unserved by a
  partial launch, produce warnings.
- **Window placement** — on Windows each Moonlight window is moved to its target
  monitor after launch.

#### Hooks

- **`Lance.Hooks`, a shared hook engine** — external commands that fire on
  `session_started` and `session_ended`. Each side runs its own hooks from its own
  config; nothing crosses the wire.
- **Hook files are JSON** with per-command `async`, `onError`
  (`terminate` | `continue`), `timeoutSeconds`, and `workingDir`. Commands run in
  array order; multiple hook files are ordered by `priority`.
- **`${VAR}` substitution in arguments** from the event payload —
  `LANCE_SESSION_ID`, `LANCE_AGENT_IP`, `LANCE_CLIENT_IP`, `LANCE_SLOT_IDS`,
  `LANCE_SIDE`, and `LANCE_EVENT_SOURCE` at teardown. Commands are launched
  directly, with no shell.
- **Hook sources** — the `hooks` array in either config file, plus `--hook <path>`
  on `connect` (repeatable, layered on top).
- **Reference hook samples** in `samples/hooks/`: `vox.agent.json` and
  `vox.client.json` for the microphone-bridge flow, and `smoke.json`, a
  dependency-free hook that logs each event for confirming hooks fire.

#### Slot management

- **`lance status`** shows every slot's state alongside the Moonlight process
  connected to it.
- **`lance slots`, `allocate`, `start`, `stop`, `deallocate`** manage slots
  directly. `deallocate --force` stops a running slot first.
- **`lance config`** opens a slot's Apollo web config page in the browser.
- **Comma-separated id lists** are accepted by `start`, `stop`, `deallocate`, and
  `config`; each id is processed independently under the partial-success rule.
- **Per-slot Apollo identity** — every clone gets its own `file_state` and
  `credentials_file`, so slots never share pairing state and Slot 0's state file is
  never touched. Each clone pairs with Moonlight once, individually.

#### Security

- **HTTPS on the agent API**, using the ASP.NET Core developer certificate.
- **Bearer token authentication** — setting `auth.token` requires
  `Authorization: Bearer <token>` on every endpoint except `GET /health`, which is
  always open. Leaving the token empty runs the API unauthenticated.
- **Client-side token sources** — `agent.token` in `lance.json` or `--token` on the
  command line, with the flag winning.

#### Agent HTTP API

- `GET /health` — status, version, uptime, slot capacity, and template state.
- `GET /slots`, `GET /slots/{id}` — slot state including live connection status.
- `POST /slots` — allocate to a target count, idempotent.
- `POST /slots/{id}/start`, `POST /slots/{id}/stop`.
- `DELETE /slots/{id}` — deallocate; refuses a running slot.
- `POST /slots/{id}/force-deallocate` — stop, then deallocate.
- `GET /slots/{id}/config` — the slot's Apollo web config URL.
- `POST /sessions` — the connect handshake.
- `GET /sessions`, `GET /sessions/{id}`, `DELETE /sessions/{id}` — list, inspect,
  and end a session.

#### Configuration and output

- **`lance-agent.json`** configures the listen address, auth, Apollo install and
  config paths, slot pool limits and port stride, session timing and record
  directory, hooks, and logging. A missing file falls back to built-in defaults
  with a warning.
- **`lance.json`** configures the agent URL, token, request timeout, the Moonlight
  executable, default and per-monitor options, bitrate mode, hooks, colour, and
  logging. Resolved from `--config` first, then next to the binary.
- **Global options on every command** — `--agent`, `--token`, `--config`,
  `--verbose`, and `--no-color`.
- **Distinct exit codes** — `0` success, `1` generic error, `2` no free slots,
  `3` agent unreachable, `4` agent returned an error, `5` Moonlight launch failed,
  `6` slot not in the required state, `7` agent URL could not be resolved.
- **Logging** — the agent writes to the console and a daily-rolling
  `lance-agent.log`; framework and HTTP noise is suppressed so Lance's own events
  read as a narrative. Client output favours plain lines, reserving tables for
  genuinely tabular data.

[0.1.0]: https://github.com/samsam07/Lance/commits/master
