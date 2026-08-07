# TODO and backlog file

## TODO

- Changelog

## Daily usage observations

- [P1] Performance (stutter and lag) issues - performance tuning required.
  Spec: `docs/STREAM_TUNING_SPEC.md` (slices 2.1-2.5).
  - [✅] Dropped `--yuv444` from the client options — it forced HEVC 4:4:4, which the
    client GPU cannot decode via D3D11VA, silently falling back to Vulkan video.
    Confirmed fixed in the Moonlight logs; user reports the stream feels snappier.
  - [✅] Slice 2.1 — `defaultFlags` renamed to `defaultOptions`; stale
    `--yuv444` / `--no-vsync` / `--bitrate 80000` / `--fps 60` removed everywhere.
  - [✅] Slice 2.2 — per-monitor options: `monitorOptions` config + `--monitor-options`
    CLI, keyed by monitor id.
  - [✅] Slice 2.3 — refresh rate in `lance monitors` + generated `--fps`, capped at 60.
  - [✅] Slice 2.4 — auto-bitrate (`--bitrate-mode`, bits-per-pixel derivation).
    With no `--bitrate` and no mode set, the three monitors now size to ~12 / ~22 /
    ~49 Mbps (~83 Mbps total) instead of a flat 3 × 80 = 240 Mbps.
  - [✅] Slice 2.5 — refer to a monitor by name as well as id.
  - [ ] **End-to-end test of the whole stream-tuning stack** (2.1-2.5) against the live
    3-monitor setup — the layer merge is on the launch path for every stream and has
    only been exercised by unit tests plus `lance monitors`.
  - Remaining known cause: the agent's ~292 Mbps Wi-Fi uplink against a 240 Mbps
    request. Wiring the agent to Ethernet is the largest single fix and needs no code.
- Backlog: session-wide bandwidth budget (`bitrateBudgetKbps` divided across monitors
  by pixel rate) — nice-to-have, see `STREAM_TUNING_SPEC.md` T6.
- Lack of UI to jump back to host PC
- Mouse pointer on mixed screen (some with moonlight others with host pc) - pointer goes behind moonlight
- Make a Lance Client GUI???

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
