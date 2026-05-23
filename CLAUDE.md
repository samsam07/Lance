# CLAUDE.md — How to work on Lance

You are pair-programming with the project owner. He stays the architect; you
generate code under his review. The single most important rule:

> **Never make an architectural or sub-architectural decision silently. When you
> hit an undecided point, STOP and ASK — do not invent.**

The last attempt failed because decisions were made during implementation that
he never saw, producing code he didn't recognize. Your job is to make every
non-trivial choice *visible* before it becomes code.

## The documents (read before coding)

- `docs/ARCHITECTURE.md` — what the system does and the invariants. **Source of
  truth for behavior.**
- `docs/SPEC.md` — verified concrete facts (ports, DTOs, mutation rules, configs).
  Source of truth for *values*. If SPEC and ARCHITECTURE disagree on *behavior*,
  ARCHITECTURE wins.
- `docs/CONVENTIONS.md` — how code must *read* (member order, naming, etc.).
- `docs/PLAN.md` — phases and the Phase-1 slice breakdown. Build in slice order.
- `.editorconfig` — mechanically enforced style. Keep the build warning-clean.

## Workflow: slice-by-slice with review gates

Work **one slice at a time**, per `docs/PLAN.md`. For each slice:

1. **Restate** the slice goal and list the decisions it requires. Flag any not
   already settled in the docs → ask before writing code.
2. **Implement** only that slice. Do not build ahead into later slices or phases.
3. **Stop and present** for review. Summarize what you did and call out anything
   you were unsure about. Wait for approval before the next slice.

Never run more than one slice ahead of his understanding. A slice should be
small enough to fully read in ~10–15 minutes.

## Stop-and-ask triggers (ask, don't guess)

- A flow/edge case the docs don't cover.
- Any choice with 2+ reasonable options where the docs are silent.
- Anything marked `???`, `[RESEARCH-1]`, `[DEFER-1]`, `[DEFER-SVC]`,
  `[VERIFY-APOLLO]`, `[VERIFY-MUTEX]`, or `[VERIFY-VERSIONS]` in the docs — these
  are **not yours to silently resolve**. `[RESEARCH-1]` (Apollo↔Moonlight
  connection detection) blocks all session work and is settled by a research
  spike, not code. `[DEFER-SVC]` — auto-managing the Apollo service/watchdog is a
  later phase; Phase 1 assumes the user stops it manually. `[VERIFY-APOLLO]` —
  Apollo's Linux privilege model is untested; verify or ask. `[VERIFY-MUTEX]` —
  named-mutex cross-process behavior on Linux is unverified; if it doesn't hold,
  fall back to a PID-bearing lock file. `[VERIFY-VERSIONS]` — check latest stable
  package versions at first build; don't trust the stale pins.
- A value not in SPEC (port, path, timeout, error code) — ask or cite where you
  got it; never make one up.

## Hard rules

- **Phase 1 only** unless told otherwise. No auth, no TLS enforcement, no
  Windows service, no sessions. (Sessions/auth are Phase 2+.)
- **Connect = partial success**, never all-or-nothing/rollback. 2 of 3 monitors
  beats 0. Master/Slot-0 failure → session runs without audio (warn + continue);
  it does **not** fail the session. (This overturns the old DECISIONS D13 — ignore
  any all-or-nothing connect logic from old docs.)
- **Apollo `file_state` is inherited unchanged** when cloning. Mutating it
  silently breaks pairing. See SPEC mutation table for the exact fields.
- **Slot 0 is never modified or deallocated.**
- Follow `docs/CONVENTIONS.md` exactly — including the deliberate
  fields→properties→constructor order. Do **not** "correct" it.
- Prefer pushing mechanical rules to `.editorconfig` over enforcing them by hand.

## When unsure

Asking is always cheaper than a wrong guess he has to reverse-engineer later.
A short "I see two ways to do X, here's the tradeoff, which do you want?" is
exactly the behavior wanted — not a defect.
