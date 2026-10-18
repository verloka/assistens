# FDD-002 — GUI access, pause and human takeover

Status: Draft

## Trigger / Outcome
- Initiator: an agent requests a Computer Use operation; user takes over by hotkey, tray, or mini-window; or user activity interrupts a scheduled run.
- Outcome: GUI access is exclusive while granted and immediately yields to human control at safe operation boundaries; resume uses a fresh screen observation.

## Inputs
- Run ID, requested action, current desktop-lease generation, user activity/idle status, pause/resume request, Windows session state.

## Outputs
- Valid operation result; denied, paused, blocked or cancelled state; fresh screenshot when access resumes.

## Flow
1. Check that Windows session is unlocked and current run can obtain the desktop lease.
2. Before **each** GUI action, validate authorization and current lease generation; reject stale queued actions.
3. Execute the atomic input safely, always releasing held modifiers in cleanup.
4. Upon local takeover request, close the GUI gate immediately without waiting for model response; allow current atomic input to finish safely and release modifiers.
5. Persist `PausedForUser` / corresponding run state; keep user in control until explicit approved resume.
6. At resume, reacquire lease, take a **fresh** screenshot, invalidate stale coordinates and ask the model to re-evaluate the UI before any next GUI action.

## Validations
- Lease ownership and generation; valid physical screen coordinates / DPI; session not locked or at UAC elevation mismatch.

## Invariants
- Human takeover always takes precedence over the agent.
- No **new** GUI action starts after the pause gate closes, including while an LLM call is still pending.
- GUI cannot be concurrently controlled by two runs; pause never promises to undo already completed clicks.
- Stale screenshots/coordinates are never blindly replayed after resume.

## Failures / Side Effects
- On lock/disconnect/UAC, return a blocked result without bypassing Windows protections.
- Pauses are distinct from cancel, skipped schedule, or stopping an MCP-only task.

## Dependencies
- None.

## Scenarios
- S1: Given a running GUI action loop, when the user invokes Take Control, then no subsequent clicks/keys begin and modifiers are released. [untested]
- S2: Given a paused GUI run and changed display, when the user resumes, then the agent receives a new capture and does not execute stale coordinates. [untested]
- S3: Given a locked desktop, when the model requests a click, then it is blocked and no unlock/UAC bypass is attempted. [untested]
- S4: Given concurrent runs, when both request GUI, then only one owns the desktop at a time. [untested]

## Implementation References
- Planned: `docs/DEVELOPMENT_PLAN_v1.1.md` §§6, 9–10; backlog PR-04, PR-05, PR-06, PR-14.

- Capture contract foundation (PA-024): `src/PersonalAgent.Core/Computer/ScreenContracts.cs` defines physical points/bounds, source geometry independent of resized image dimensions, per-monitor DPI and capture time/generation. `tests/PersonalAgent.UnitTests/ScreenContractTests.cs` checks negative origins, mixed-DPI metadata, overflow-safe half-open bounds, selection validation and neutral serialization. It does not decode image bytes, capture a desktop or implement lease/pause/resume; complete S1-S4 remain `[untested]`.

## Unknowns
- Concrete Windows APIs, pause latency and live race results must be established in the implementation.
