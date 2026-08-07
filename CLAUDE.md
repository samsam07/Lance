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
- `docs/design/` — per-subsystem design rationale (`stream-tuning.md`,
  `tool-orchestration.md`). These record *why* a decision was made and are
  **subordinate**: once a design ships, its behavior lives in ARCHITECTURE and its
  values in SPEC, and those win on any disagreement. Read for background; never
  cite one as the source of truth. New design docs go here.
- `CHANGELOG.md` — the user-facing record of what Lance does. Update it as part of
  any change that a user could notice — see "Changelog" below.
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

## Changelog — keep it current with every change

`CHANGELOG.md` follows [Keep a Changelog](https://keepachangelog.com/).
**After implementing anything, check whether it belongs there and add it before
presenting the slice for review.** Don't wait to be asked.

**Lance is pre-release.** There is exactly one version section, and everything
accumulates into it:

```
## [0.1.0] — Unreleased
```

**Never bump the version, never add a new version heading, never add a release
date.** `0.1.0` is what both `.csproj` files stamp and what the agent/client
compatibility check compares.

**Goes in:** anything a *user* can observe — a new command or flag, changed
behavior, a new config field, a new error code or exit code, an API endpoint, a
security-relevant change.
**Stays out:** docs, tests, refactors, build scripts, logging internals, and
anything that changes no observable behavior. When genuinely unsure, ask rather
than pad the file.

**How to write the entry — the part that is easy to get wrong.** The `0.1.0`
section describes the **net state of Lance at its first release**, not the
development history. Nothing has ever shipped, so there is no released behavior for
a change to be "Changed" or "Fixed" *against*. Therefore:

- **New user-facing capability** → add a bullet under `Added`.
- **A change to something already described** → **edit that existing bullet** so it
  describes the new reality. Do *not* append a `Changed` entry.
- **A bug fix to behavior that never shipped** → usually **no entry at all**. If an
  existing bullet overstated what Lance does, correct that bullet instead.
- **Removing something described** → delete or amend its bullet.

That keeps `0.1.0` a readable feature list instead of a replayed commit log. Match
the surrounding style: bold lead-in, one or two sentences, filed under the existing
`####` grouping.

**After the first real release** this flips to standard Keep a Changelog: date the
`0.1.0` heading, open a fresh `## [Unreleased]` above it, and from then on `Added` /
`Changed` / `Deprecated` / `Removed` / `Fixed` / `Security` carry their normal
meanings against the last shipped version.

## Stop-and-ask triggers (ask, don't guess)

- A flow/edge case the docs don't cover.
- Any choice with 2+ reasonable options where the docs are silent.
- Anything marked `???` or carrying an **open** `[DEFER-…]` / `[VERIFY-…]` marker in
  the docs — these are **not yours to silently resolve**. ARCHITECTURE's
  "Notes / open items" is the authority on which are open; the live ones are:
  - `[DEFER-SVC]` — auto-managing the Apollo service/watchdog. Phase 3 Slice 4, not
    built; until then the user stops Apollo by hand.
  - `[VERIFY-APOLLO]` — Apollo's Linux privilege model is untested. Verify or ask
    before any Linux agent work. Also gates Linux UDP endpoint enumeration.
  - `[VERIFY-VERSIONS]` — don't trust stale package pins; check latest stable
    compatible with .NET 10 before changing dependencies.
  - `[DEFER-PATHS]` — Linux/XDG file-path conventions (Phase 3 Slices 3 and 7).
  - `[DEFER-TLS-PINNING]` — client cert pinning / PEM support (Phase 3 Slice 8).
  - `[DEFER-LINUX-WINDETECT]`, `[DEFER-LINUX-WINPOS]`, `[DEFER-LINUX-REFRESH]` —
    Linux client completions (Phase 3 Slice 5).
  - `[DEFER-PAIR-AUTO]` — automated slot pairing (Phase 4).

  Already resolved — **do not treat these as open**: `[RESEARCH-1]` (superseded by
  `[VALIDATE-UDP]`), `[VALIDATE-UDP]`, `[SESSION-ENDPOINT]`, `[INVESTIGATE-STOP]`,
  `[DEFER-WIN-ADOPT]`, `[DEFER-LINUX-SIGTERM]`, `[DEFER-1]` (closed as moot),
  `[VERIFY-MUTEX]` (resolved by design — uniqueness is agent-side; see ARCHITECTURE).
- A value not in SPEC (port, path, timeout, error code) — ask or cite where you
  got it; never make one up.

## Hard rules

- **Phase 3 is active.** The sessions & tool-orchestration subsystem shipped (Slice 1);
  daily-use fixes and polish are the current work (Slice 2). Sessions, auth, and TLS are
  all in scope and built. No Windows service install yet (Phase 3 Slice 3, not yet
  reached). Auto-managing the Apollo service `[DEFER-SVC]` is a later Phase 3 slice
  (Slice 4), still deferred — the user still stops Apollo manually.
- **Connect = partial success**, never all-or-nothing/rollback. 2 of 3 monitors
  beats 0. A slot that fails is logged and skipped; the rest proceed. (This
  overturns the old DECISIONS D13 — ignore any all-or-nothing connect logic from
  old docs.)
- **Apollo `file_state` and `credentials_file` are rewritten per clone**, both to the
  same `sunshine_{N}_state.json` (matching Apollo's default, where both point at one
  file). Each clone therefore carries its own server UUID and must be paired with
  Moonlight separately — see `[DEFER-PAIR-AUTO]`. **Slot 0's state file is never
  touched**, and no two slots may share one. See the SPEC mutation table for the full
  field list; SPEC is authoritative here.
- **Slot 0 is never modified or deallocated.**
- Follow `docs/CONVENTIONS.md` exactly — including the deliberate
  fields→properties→constructor order. Do **not** "correct" it.
- Prefer pushing mechanical rules to `.editorconfig` over enforcing them by hand.

## When unsure

Asking is always cheaper than a wrong guess he has to reverse-engineer later.
A short "I see two ways to do X, here's the tradeoff, which do you want?" is
exactly the behavior wanted — not a defect.
