# Lance — Phase 2 Handoff

> **First thing:** delete this file (`HANDOFF.md`) once you have read it and
> are ready to begin. It is a one-time session bootstrap, not a project document.

---

## Kickoff prompt (copy-paste this to start the session)

This is the Lance project — continuing into Phase 2. Before doing anything, read
these files in order: CLAUDE.md (root), then docs/ARCHITECTURE.md, docs/SPEC.md,
docs/CONVENTIONS.md, docs/PLAN.md. Also read docs/DEPLOY.md for operational
context. The .editorconfig at root governs mechanical style.

CLAUDE.md defines how we work — the most important rule is that you never make
an architectural or sub-architectural decision silently; you stop and ask. We
build one slice at a time per docs/PLAN.md, with a review gate after each.

Context: Phase 1 is complete and committed. The agent and client are in
end-to-end testing. Phase 2 (Alpha) is now active — sessions, auth/TLS, and
platform completions are in scope. CLAUDE.md and PLAN.md have been updated to
reflect this.

Critical constraint: [RESEARCH-1] (Apollo↔Moonlight connection detection) is
unresolved and is Phase 2 Slice 1. It is a pure research task — no code — and
its findings directly shape the session architecture for Slices 4 and 6. Do not
write any session code until the Slice 1 findings are documented and reviewed.

Don't write any code yet. Start by: (1) confirming you've read all the docs,
(2) listing the Phase-2 slices from PLAN.md as you understand them and noting
which ones depend on the [RESEARCH-1] outcome, and (3) for Slice 1 only (the
research spike), describing what you would investigate, what sources you'd
consult, and what form the findings document should take — then wait for my
go-ahead.
