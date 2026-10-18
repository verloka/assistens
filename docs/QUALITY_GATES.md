# Completion and release gates

Task count: **125**. Original source: `DEVELOPMENT_PLAN_v1.1.md` §16, §17, §18, §22.

## Milestones / delivery streams

| Milestone | Scope | Observable result |
|---|---|---|
| PR-01 | WPF/Generic Host/solution/spikes | Runnable app + pinned SDK versions and smoke diagnostics |
| PR-02 | Domain & fake agent | Fake task launched, progress displayed, cancellation safe |
| PR-03 | SQLite & recovery | Runs/history survive restart; unsafe writes are not replayed |
| PR-04 | Capture | Multi-monitor/DPI-safe screenshots and mapping |
| PR-05 | SendInput | Five low-level operations with fail-safe input release |
| PR-06 | Take control | Pause/hotkey/tray; no stale clicks on resume |
| PR-07 | OpenAI agent | Real read-only vision + tools end-to-end |
| PR-08 | Anthropic | Profile/provider switching + contract tests |
| PR-09 | MCP | Stdio/HTTP discovery; Jira/ADO/hybrid demo |
| PR-10 | Approvals | Risk/permission policy; no unsafe side effects without consent |
| PR-11 | Scheduler | Dedup, interval runs, 15s countdown, idle gating |
| PR-12 | Telegram | Authorized two-way channel, outbox/retries, approvals |
| PR-13 | Conversation | Follow-ups across runs, restarts and model switches |
| PR-14 | Activity pause | Takeover on human input, sleep/lock/resume stress |
| PR-15 | Release | Protected secrets, diagnostics, published app, full regression |

## Required end-to-end acceptance matrix

- [ ] **E2E-01** Manual Computer Use using real vision & tools (`PR-07`)
- [ ] **E2E-02** Hybrid MCP+GUI (`PR-09`)
- [ ] **E2E-03** Human Take Control, GUI actions cease, fresh screenshot on resume (`PR-06`,`PR-14`)
- [ ] **E2E-04** Scheduled run does not steal keyboard while user active (`PR-11`,`PR-14`)
- [ ] **E2E-05** GUI lease gate after MCP-only steps (`PR-09`,`PR-11`)
- [ ] **E2E-06** Telegram follow-up uses prior conversation (`PR-13`)
- [ ] **E2E-07** Send/write approval bound to exact pending action (`PR-10`,`PR-12`)
- [ ] **E2E-08** No duplicate write after crash/unknown outcome (`PR-03`,`PR-10`)
- [ ] **E2E-09** Second channel added via DI without changes to Core (`PR-12`)
- [ ] **E2E-10** OpenAI→Claude switch via neutral context, not incompatible session serialization (`PR-08`,`PR-13`)

Checking off this file is **not** task completion: authoritative states and evidence live in `progress/state.json`; final gate `PA-125` requires §22 MVP DoD, all ten E2E cases, and release reproducibility.

## External/manual blockers

A model API credential, corporate tenant permission, real Windows session, screen layout or code-signing environment may be unavailable. Mark the relevant task `blocked` with an exact reason and continue other ready tasks. Never fake E2E, never mark external work as `done` purely from mock tests. No default auto-skip of any required validation.

## FDD regression review gate (Codex)

For every backlog task that may affect end-to-end user/business behavior:

1. Identify impacted flow IDs in `docs/flows/index.md`, including dependencies and reverse consumers.
2. Follow `flow-develop` to preserve unaffected behavior and document only intentional changes before implementation.
3. Implement and execute relevant tests; tie actual test paths to numbered scenarios with `[test: path]`, otherwise keep `[untested]`.
4. Follow `flow-verify` against the actual diff and classify checks as `PASS`, `FAIL`, `UNKNOWN` or `NOT TESTED`.
5. Do not mark a task `done` while its mandatory acceptance criteria are unverified. A `Draft` flow is **not** an approved contract and cannot independently prove acceptance.
6. Only the user can promote `Draft` to `Active`; don't silently change approved contracts or the 125-task tracker to circumvent an issue.

No separate FDD validator script is necessary. Do not create flow documents for low-level implementation details that do not represent a complete outcome.
