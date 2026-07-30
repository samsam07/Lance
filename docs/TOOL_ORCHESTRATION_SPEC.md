# Tool Orchestration Spec

Status: design locked. One item requires empirical validation before implementation (see §5, probe latency).

> **Integrated 2026-07-11.** This design has been folded into the canonical docs:
> behavior → `ARCHITECTURE.md` ("Sessions & tool orchestration"), values →
> `SPEC.md` ("Sessions & orchestration"), slice plan → `PLAN.md` (Phase 3 Slice 6).
> This file is retained as the original design rationale; when it disagrees with the
> canonical docs, **ARCHITECTURE/SPEC win** (per CLAUDE.md). Open markers carried
> forward: `[VALIDATE-UDP]` (§5 detection, Slice 1.1) and `[SESSION-ENDPOINT]`
> (§8 handshake endpoint shape, Slice 1.4).

## 1. Overview

Sidecar tools (`vox`, future `clipline`, keystroke relay) extend the RDP-like experience Lance targets, filling gaps vanilla Apollo/Moonlight leave: mic backchannel, richer clipboard, client-side event reactions. Running these tools requires coordinated setup on connect and teardown on disconnect, on both machines.

Lance owns this orchestration. Apollo is now a managed backend, not the orchestrator. Lance delivers **events**; **tools own their own process lifecycle**. Lance's sole guarantee is that `session_ended` is eventually dispatched on each side.

**Key model decision — no cross-machine event bus.** Events are raised *locally* on the side that detects them; each side runs its own hooks from its own config. Nothing about events propagates over the wire. The wire carries only coordination (connect handshake, optional clean-disconnect ping). This is a deliberate simplification of the original draft, which modeled one event propagating to both sides.

Terminology: this is a per-side **event dispatcher** (raise event → match hooks → execute), not a shared bus.

## 2. Sessions

A **session** is one `lance connect` invocation, scoped to one client machine, grouping the slots that invocation acquired. Sessions are the unit hooks bind to.

- A session begins only at `lance connect`. `lance allocate` / `lance start` provision slots and Apollo instances but create **no session** and raise **no events**. Slot occupancy and sessions are independent concepts on the agent.
- A second concurrent `lance connect` on the same machine is a **separate session** (new id, own slots, own lifecycle), not a join.
- Multi-monitor: `lance connect --monitors 1,3` acquires one slot per monitor. All belong to the one session. Session-tier events still fire **once**; per-slot events (if any) fire per slot. See §4.

**Session id.** Client-minted, or overridden via `--session-id`. Sent to the agent on the connect handshake. The agent vets it for **global uniqueness across all active sessions**; a collision **refuses the connection** (the client surfaces the error and stops — no silent retry). Free id → agent reserves it, allocates slots, proceeds.

**Agent-side session state machine:**

| State | Meaning | Enters on |
|---|---|---|
| `Provisioned` | slots allocated, no stream yet | successful handshake + allocation |
| `Connected` | ≥1 slot's stream is live | first slot detected connected (§5) |
| `Ended` | teardown ran, record deleted | any `session_ended` source (§4) |

- **Provision grace window (default 30s):** a `Provisioned` session with no slot connected within the window → `session_ended(source=provision_timeout)`. Distinguishes "not yet connected" from "was connected, now gone" — both otherwise look like "all slots idle."
- **Slots are freed only at `session_ended`.** Held through degraded operation (see §3). No mid-session slot release.

## 3. Client daemon

`lance connect` runs in the **foreground, blocking until the session ends**. There is no `--detached` flag.

- **Signal handling:** trap `SIGHUP` / console-close and run graceful teardown (tree-kill children, run `session_ended` hooks) before exit. Load-bearing because foreground-only means terminal close is a normal exit path.
- **Process ownership:** Moonlights are launched as children.
  - **Windows:** placed in a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. Daemon dies for any reason → OS kills all Moonlights. Hook-spawned processes also stay in the job (no breakaway) and die with it; acceptable because client-side tools do no host-state changes to restore.
  - **Linux:** no Job Object equivalent. Graceful exit uses `Process.Kill(entireProcessTree: true)` (also used on Windows for clean exits). Hard daemon death (SIGKILL, power loss) **orphans** Moonlights; this self-heals when the user kills the stray stream (agent then detects teardown, §5). No client-side reaper. No systemd dependency in v1.

**Degraded launch policy.** `--monitors 1,3` launches multiple Moonlights; some may fail after others succeed.
- ≥1 launched → **proceed degraded**: log the failure, keep the live streams, raise `session_started`, run hooks.
- 0 launched → **fail**: send the clean-disconnect ping, run **no** client hooks, exit nonzero.
- Slots allocated but never connected are **held until `session_ended`** (freeing mid-session risks another client grabbing them while this one is degraded-but-alive).

**Disconnect.** `lance disconnect` is a separate, process-level invocation. There is **no IPC** to the blocking `lance connect`; disconnect kills the Moonlights, and the daemon reacts to its children dying (raises `session_ended`, runs hooks, exits). Pre-teardown hooks (running while the stream is still up) are therefore unsupported in v1; noted as future.
- `lance disconnect --session-id X`: ask the agent for that session's slots, match Moonlights by those slots' `host:port` on the local process list, kill them.
- **Fallback (agent unreachable):** kill by explicit `host:port` passed as CLI args. Agent is the fast path; CLI args the degraded path.
- `lance disconnect` (no id): kill all sessions' Moonlights.

## 4. Events

Four events, raised **locally per side**, never propagated. Naming: `<subject>_<past-verb>`.

| Event | Tier | Raised by |
|---|---|---|
| `session_started` | session | client (after launching Moonlights); agent (after allocation, before responding go) |
| `session_ended` | session | client (last Moonlight gone / Ctrl-C / SIGHUP); agent (§5: probe-watch, ping, reconcile, or provision_timeout) |
| `slot_connected` | slot | agent only |
| `slot_disconnected` | slot | agent only |

- **Slot-tier is agent-only in v1.** The client knows a Moonlight *process* launched, not that a *stream* established — only the agent's probe knows that. No consumer exists yet; client hooks bind session-tier only. (The client still watches each Moonlight internally to know when the last one dies.)
- Both sides reach `session_ended` **independently** from their own signals. Neither waits for the other. This is what lets the agent restore host state when the client machine is dead and unreachable.

**`source` values** (carried in payload, lets a hook distinguish crash from clean and live from replayed):
`explicit | pid_watch | probe_watch | ping | reconcile | provision_timeout`

**Payload — injected as environment variables** into every spawned hook process:

| Var | Scope |
|---|---|
| `LANCE_EVENT` | always |
| `LANCE_EVENT_SOURCE` | always |
| `LANCE_SESSION_ID` | always |
| `LANCE_SIDE` | always (`agent` / `client`) — lets one hook file serve both sides |
| `LANCE_AGENT_IP` | always |
| `LANCE_CLIENT_IP` | always |
| `LANCE_SLOT_IDS` | session-tier (e.g. `1,3`) |
| `LANCE_SLOT_ID` | slot-tier only |

`${VAR}` substitution in hook `args[]` resolves against this same set (§6).

## 5. Detection (agent-side)

The agent must detect session teardown **independently of the client**, since a crashed client sends nothing. Probe-watch is the authoritative detector; the clean-disconnect ping is a latency optimization on top.

**Liveness signal — UDP endpoint presence, not TCP.** Empirically verified: during a live stream, Apollo's TCP ports stay in `Listen` only (no `ESTABLISHED`); the stream rides UDP, and Apollo binds its UDP streaming endpoints on connect and releases them on disconnect. Therefore:

> A slot is **connected** iff its Apollo process currently owns UDP endpoints at that slot's streaming ports.

**Port resolution per slot:**
1. Ports **explicitly present in the cloned config** win (handles manual edits).
2. Ports absent from the config are computed from the slot's base port via Apollo's fixed base+offset map.
3. The base+offset map lives in the **host-adapter seam** — one table, swappable when targeting Sunshine or another host. The probe logic itself stays host-agnostic.

The probe scopes by **owning PID AND resolved ports**, so multiple slots don't read each other's endpoints and unrelated processes are excluded.

**Measured latency (current Apollo, informative not contractual):**
- Clean connect/disconnect: UDP count changes within ~1s. Poll interval (~1s) is the floor for clean teardown detection.
- Hard client cut (NIC down, no FIN/RST): endpoints released ~6–7s after the client vanishes (Apollo's internal client-timeout).

Caveats to respect: the ~6–7s is Apollo's timeout and may differ across Apollo versions or other hosts — treat as observed, not guaranteed. The clean-disconnect ping remains the fast path; probe-watch is the floor.

**Validation requirement.** Lance must **detect the UDP endpoint presence itself and log probe state transitions** (slot connected / disconnected, with timestamps and source). Validation is done by reading Lance's logs during connect/disconnect and a hard-cut test — **not** by running manual `Get-NetUDPEndpoint`/netstat commands. Confirm before relying on probe-watch in production that logged detection matches the empirical ~1s / ~6–7s behavior above.

**States drive detection:**
- `Provisioned`, no endpoints within grace window → `session_ended(source=provision_timeout)`.
- `Connected`, all slots' endpoints gone → `session_ended(source=probe_watch)`.

## 6. Hooks

**Config discovery.**
- **Client:** `--hook <path>` (repeatable, additive) plus a client config file with a `hooks: [{ active, path }]` array. `--hook` overrides/adds on top of config.
- **Agent:** `lance-agent.json` carries `hooks: [{ active, path }]`.

**File format — JSON** (consistency with `lance-agent.json`; fits the nested array-of-objects shape; System.Text.Json source-gen friendly).

```json
{
  "name": "vox",
  "events": {
    "session_started": {
      "priority": 1000,
      "commands": [
        { "command": "audiohelper.exe", "args": ["backup", "audio-config"], "onError": "terminate" },
        { "command": "audiohelper.exe", "args": ["switch", "audio", "--playback", "VB Cable A", "--capture", "VB Cable B"] },
        { "command": "audiohelper.exe", "args": ["launch-vox", "--peer", "${LANCE_CLIENT_IP}", "--playback", "VB Cable A", "--capture", "VB Cable B"] }
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

**Field semantics and defaults:**

| Field | Level | Default | Meaning |
|---|---|---|---|
| `name` | file | — | Descriptive only, non-unique, for logging. Optional. |
| `priority` | event | 1000 | Orders **files** bound to the same event. Lower runs first. Ties → file load order. Within a file, `commands` run in array order. |
| `command` | command | — | Executable. Structured `command` + `args[]` → passed directly to `ProcessStartInfo.ArgumentList`. **No shell**, no string parsing, no quoting hazards. |
| `args` | command | `[]` | Argument array. Supports `${VAR}` substitution (§4 env set), resolved by Lance before spawn (there is no shell to expand it). |
| `async` | command | `false` | `false` = wait for exit before the next command (ordering dependencies, e.g. backup→switch). `true` = spawn and don't wait (long-lived/blocking commands, e.g. a foreground tool). |
| `onError` | command | `terminate` | `terminate` = stop the hook chain on nonzero exit (fewest surprises for sequential setup). `continue` = log and proceed. Meaningless for `async: true`. |
| `timeoutSeconds` | command | 30 | Applies only to `async: false`. On timeout: log, then apply `onError`. Meaningless for `async: true`. |
| `workingDir` | command | dir containing the hook file | Working directory for the spawn. |

**Process lifecycle is the tool's, not Lance's** (non-goal, stated explicitly): Lance never supervises hook-spawned processes. Its only relationship to a spawned process is optionally waiting for its exit to sequence subsequent commands. Consequences:
- A tool whose teardown must kill a process must make that process **findable** — write a **pidfile** at launch (or use a wrapper that tracks the PID). Applies to `vox` (via `audiohelper`) and to `clipline` (teardown is "kill it").
- A **wrapper** (e.g. `audiohelper`) is a setup/teardown verb-bundle: it performs ordered setup (backup → switch → launch), records the launched tool's PID, and reverses on teardown. It is **not** required to be resident and does **not** watch Lance's liveness (crash teardown is handled by §7).

## 7. Crash recovery (agent-side)

Covers the case where **lance-agent crashes mid-session** while host state is modified (audio switched, `vox` running). Apollo survives an agent crash by design — so the agent, on restart, must finish any teardown that never ran. Client-side crash recovery is **out of scope for v1** (client-side residue is a stray process with no host-state change; severity low).

**Record lifecycle:**
1. **Persist the session record BEFORE running `session_started` hooks.** Path e.g. `%ProgramData%\Lance\sessions\<id>.json`, written atomically (temp + rename). Contains: session id, client IP, slot ids, the **resolved teardown command list**, and the **env payload snapshot**. (Persist-before-setup ensures a crash mid-setup still leaves a record to replay.)
2. **Delete the record AFTER `session_ended` hooks complete.**
3. Invariant: **a record present at agent startup means teardown never ran.**

**On agent startup (reconciliation), before accepting any new connections:**
- For each surviving record, probe its slots (§5 UDP signal):
  - **Any slot connected** → session is alive (Apollo kept streaming). Re-adopt into memory; do **not** replay.
  - **All idle** → orphan. Raise `session_ended(source=reconcile)`, run the **snapshotted** teardown commands, delete the record.
- Reconciliation **must complete before the listener opens**, else a fresh connect could switch audio and then be clobbered by a replayed `restore`.

**Rules:**
- **Snapshot commands + env; never re-read the hook file at replay** (it may have changed; `LANCE_CLIENT_IP` can't be recomputed once the client is gone).
- At replay, `LANCE_EVENT` / `LANCE_EVENT_SOURCE` are set **fresh** (`session_ended` / `reconcile`), not restored from the snapshot — a hook can tell a replayed teardown from a live one.
- **Teardown commands must be idempotent** (author requirement): restore-when-already-restored, kill-when-already-dead → no-ops. This is what makes a mid-chain `terminate` abort safe to clean up later.
- **The agent never job-kills Apollo.** Apollo must survive an agent crash — the premise of this whole section. (Only the client jobs its Moonlights, §3.)
- B replays **`session_ended` only**. Slot-tier hooks are never replayed.

**Accepted gap:** if the agent crashes and **never restarts**, host state stays modified. The Windows service auto-restart makes this rare; no watchdog-of-watchdog in v1.

## 8. Wire protocol

Existing REST/HTTPS (self-signed, bearer token), extended. **No persistent connection anywhere** — the original goal of "maintain an active connection for the session" is dropped; it is unnecessary given local event dispatch and probe-based detection.

- **Connect handshake** (client → agent): existing slot-allocation call, extended with `session_id` and requested monitors/slots. Agent vets the id (global uniqueness; collision → refuse), allocates slots, **persists the record**, runs agent `session_started` hooks, responds go with the allocated slot set.
- **Clean-disconnect ping** (client → agent): `DELETE /sessions/{id}` shape. Fast-path only — not required for correctness (probe-watch backstops it). When unreachable, teardown still happens via probe-watch.

Sequencing note: the agent's `session_started` hooks complete before the client's; the agent's `session_ended` hooks may run after the client's. This ordering is inherent to who detects what; no cross-machine barrier exists or is needed.

## 9. Reference example — `vox`

`vox` is a symmetric UDP audio pipe; `audiohelper` is its wrapper (verb-bundle + pidfile owner).

**Agent** `hooks/vox.json` — backup and switch host audio, launch `vox`; reverse on end. `backup` sets `onError: terminate` so a failed backup aborts before `switch` destroys the only copy.

**Client** `vox.json` — just launch/kill `vox` (no host-audio switching on the client side):
```json
{
  "name": "vox",
  "events": {
    "session_started": { "commands": [ { "command": "audiohelper", "args": ["launch-vox", "--peer", "${LANCE_AGENT_IP}"] } ] },
    "session_ended":   { "commands": [ { "command": "audiohelper", "args": ["kill-vox"] } ] }
  }
}
```

**Flow:**
1. Client: `lance connect --hook ~/vox.json`.
2. Agent: vet id → allocate → persist record → run agent `session_started` (backup, switch, launch-vox) → respond go.
3. Client: launch Moonlight per slot (in job object on Windows). 0 launched → ping + abort; ≥1 → proceed.
4. Client: raise `session_started` → run client hooks (launch-vox).
5. Client blocks, watching Moonlights.
6. Last Moonlight exits / Ctrl-C / SIGHUP → client raises `session_ended` → run client hooks (kill-vox) → kill remaining Moonlights → send ping → exit.
7. Agent: on a live end — ping, probe-watch all-idle, or provision timeout → raise `session_ended` → run agent hooks (kill-vox, restore) → delete record → **stop the session's own slots** (tears down its virtual displays; slot 0 included, standalone adopted instances untouched) → free them. Leaving Apollo running would reconfigure the agent's desktop (a virtual display becomes primary); the slower reconnect is the accepted cost. A `disconnect --keep-running` ping carries `keepRunning=true`, which skips the stop (Apollo left running for a fast reconnect). **Startup reconcile** of an orphaned session runs the same teardown but **leaves Apollo running** (it never stops slots).

## 10. Future / nice-to-have

- `slot_paused` / `slot_resumed` events (Apollo emits pause/resume natively; add when a consumer exists).
- Client-published slot-tier events (needs a per-monitor consumer).
- **Client-side crash recovery** (client equivalent of §7; needs client-side pending-teardown persistence).
- Template substitution beyond the fixed env set.
- Pre-teardown control channel (IPC to the running daemon) for hooks that must run while the stream is still up.
- `hooks.d` directory scan as an alternative to explicit `--hook` / config listing.
- Linux systemd transient scope for hard-death teardown parity with the Windows Job Object.
- Advanced fire-and-wait error handling beyond `onError` (e.g. exit-code-conditional branching).
