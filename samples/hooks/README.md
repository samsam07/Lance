# Sample hooks

Hook files run external commands on session events (see `docs/SPEC.md` →
"Hook file format"). Reference them from `hooks: [{ active, path }]` in
`lance-agent.json` (agent) / `lance.json` (client), or with `--hook <path>` on
`lance connect` (client, repeatable).

Each side runs its **own** hooks from its own config — nothing crosses the wire.
`${VAR}` in `args` is substituted from the event payload (`LANCE_SESSION_ID`,
`LANCE_AGENT_IP`, `LANCE_CLIENT_IP`, `LANCE_SLOT_IDS`, `LANCE_SIDE`, …).

| File | Use |
|---|---|
| `smoke.json` | **Dependency-free.** Appends a line to `%TEMP%\lance-hook-smoke.log` on each event — use it to verify hooks fire (and that crash recovery replays `session_ended`) without any extra tooling. |
| `vox.agent.json` | Agent side of the `vox` mic backchannel: back up + switch host audio, launch `vox`; kill `vox` + restore on end. |
| `vox.client.json` | Client side of `vox`: just launch/kill `vox` (no host-audio switching). |

`audiohelper` / `vox` are **external tools you provide** — Lance only runs the
commands. A tool whose teardown must kill a process has to make it findable
(pidfile or a wrapper that tracks the PID); Lance never supervises hook-spawned
processes.
