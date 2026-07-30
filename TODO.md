- Changelog

---

# End-to-end Tests

- Test 1:
  - Prep
	- Agent (elevated, Apollo service stopped): set logging.level: Debug, add { "active": true, "path": "hooks\\smoke.json" } to lance-agent.json hooks, copy samples/hooks/smoke.json beside the agent. Start it.
	- Client: add smoke.json to lance.json hooks (or --hook), then lance connect --monitors 1. Confirm the streams open and it blocks.
  - Test
	- [✅] Check %TEMP%\lance-hook-smoke.log on both machines → a session_started line each (LANCE_SIDE=agent / client).
	- [ ] Clean end: from another terminal lance disconnect (or Ctrl-C the daemon) → agent log shows session_ended (ping), and a session_ended smoke line appears on both sides.
	- [ ] Crash recovery: lance connect again; while streaming, kill the agent (Task Manager). Restart it → its startup log should show it recovering the record and, since the stream's gone, replaying session_ended (reconcile) — with a matching smoke line.
	- [ ] Detection timing (optional): hard-cut the client NIC mid-session → agent ends it via probe_watch ~6–7s later.
