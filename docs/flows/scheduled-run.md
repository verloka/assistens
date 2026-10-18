# FDD-003 — Scheduled run with safe desktop preemption

Status: Draft

## Trigger / Outcome
- Initiator: the scheduler reaches a persisted occurrence time.
- Outcome: exactly one corresponding run is created or the occurrence is skipped/deferred, without unannounced GUI interference.

## Inputs
- Task definition, interval/timezone, misfire policy, recurrence/occurrence key, GUI requirements, countdown/idle policy.

## Outputs
- Durable run status, skip/deferral reason, available result and delivery to configured channel.

## Flow
1. Calculate the next due occurrence in configured timezone, recording a unique occurrence key.
2. Avoid duplicate runs and overlapping occurrences of the same schedule.
3. For MCP-only work, proceed independently of the GUI lease; if later GUI is needed, enforce FDD-002 at that first tool call.
4. For GUI work, warn with countdown; apply `AskEveryTime`, `StartWhenIdle` or `StartAfterCountdown` policy as configured.
5. Honor manual skip/delay, user activity, user Take Control, locked Windows session, and misfire policy on restart.
6. Persist outcome, recalculate next occurrence and invoke FDD-006 only for allowed notifications.

## Validations
- Schedule enabled; timezone valid; occurrence unique; active/idle Windows state; user priority; execution and communication permissions.

## Invariants
- A scheduled GUI task cannot silently steal the keyboard during active user work when idle policy is enabled.
- Missed periodic checks are not blindly replayed in a burst; default policy is RunOnce.
- One occurrence cannot result in two independently executing runs.

## Failures / Side Effects
- Offline/locked screen, overlap, unsupported credentials and user skip produce explicit outcomes.
- Persisted schedule and run-event writes; possible external reads and notifications.

## Dependencies
- Flow: FDD-001 — run lifecycle.
- Flow: FDD-002 — desktop control.
- Flow: FDD-006 — notification delivery.

## Scenarios
- S1: Given scheduled GUI work and active keyboard use, when due, then the task defers rather than stealing input. [untested]
- S2: Given restart after missed occurrences, when RunOnce misfire policy is active, then at most one catch-up run starts. [untested]
- S3: Given the same occurrence observed twice, when scheduler tries to enqueue twice, then only one run is created. [untested]

## Implementation References
- Planned: `docs/DEVELOPMENT_PLAN_v1.1.md` §§9–10; backlog PR-11 and PR-14.

## Unknowns
- Actual delay thresholds and default idle detection must be verified on Windows.
