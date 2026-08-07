# TODO and backlog file

## TODO

- Changelog

## Daily usage observations

- [P1] Performance (stutter and lag) issues - performance tuning required.
  - [ ] End-to-end test of the whole stream-tuning stack
  - [ ] Agent-side apollo optimization, if any
- Session-wide bandwidth budget (`bitrateBudgetKbps` divided across monitors by pixel rate) — nice-to-have, see `STREAM_TUNING_SPEC.md` T6.
- Mouse pointer on mixed screen (some with moonlight others with host pc) - pointer goes behind moonlight
- Slot config sync (triage: spec or drop). Clone configs drift from the template and
  there is no way to push a setting to every slot.
  - Three separate causes: (a) `SlotAllocator.Allocate` skips any id whose
    `sunshine_{id}.conf` already exists, so template edits never reach existing clones;
    (b) changes to Lance's own mutation rules never backfill — the dev box's
    `sunshine_1.conf` predates the `file_state` / `credentials_file` rules and still
    lacks them, which is a *pairing* bug, not a performance one; (c) Apollo rewrites
    the same files from its web UI, so there are two writers that know nothing of
    each other.
  - The trap: the template is the only durable place to put a setting, and it is the
    one place that does not propagate. Per-clone tuning done in Apollo's web UI is
    destroyed by any deallocate/reallocate, because `Deallocate` removes the `.conf`,
    the `.log` **and** the state file (pairing).
  - Safety property that makes a fix cheap: `CloneTemplate` writes **only** the
    `.conf`, so re-cloning is pairing-safe. Only *deallocate* destroys pairing.
    Manual workaround available today: stop the slots, edit `sunshine.conf`, delete
    the `sunshine_{N}.conf` files (leave the state files), then allocate again.
  - To decide if specced: source of truth (template → clones / a declared managed
    field set / desired settings in `lance-agent.json` / no truth, just a `diff`
    report); when it runs (explicit command / on allocate / on start — anything
    automatic fights Apollo's web UI); and whether per-slot divergence has a real use
    now that resolution and fps come from the client.
- Lack of UI to jump back to host PC
  - Make a Lance Client GUI???
- HiDPI monitors
  - [BUG] Client moonlight UI is offcenter on monitors with different scaling (PHY res vs Logical res)
  - [FEAT???] Use logical res instead of physical res when passing resolution to moonlight???
- [BUG] Clipboard contention. Your Moonlight logs show repeated Qt Warning: Unable to obtain clipboard — the clipline hook fighting Moonlight for the clipboard. Clipboard sync is genuinely failing sometimes.

- desktop switching
- absolute mouse position between desktops
- resuming session
	- windows position: https://github.com/kangyu-california/PersistentWindows

## End-to-end Tests

- Test 1:
  - Prep
	- Agent (elevated, Apollo service stopped): set logging.level: Debug, add { "active": true, "path": "hooks\\smoke.json" } to lance-agent.json hooks, copy samples/hooks/smoke.json beside the agent. Start it.
	- Client: add smoke.json to lance.json hooks (or --hook), then lance connect --monitors 1. Confirm the streams open and it blocks.
  - Test
	- [✅] Check %TEMP%\lance-hook-smoke.log on both machines → a session_started line each (LANCE_SIDE=agent / client).
	- [ ] Clean end: from another terminal lance disconnect (or Ctrl-C the daemon) → agent log shows session_ended (ping), and a session_ended smoke line appears on both sides.
	- [ ] Crash recovery: lance connect again; while streaming, kill the agent (Task Manager). Restart it → its startup log should show it recovering the record and, since the stream's gone, replaying session_ended (reconcile) — with a matching smoke line.
	- [ ] Detection timing (optional): hard-cut the client NIC mid-session → agent ends it via probe_watch ~6–7s later.
