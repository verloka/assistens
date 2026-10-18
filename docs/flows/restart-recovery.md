# FDD-007 — Safe recovery after app restart or interrupted action

Status: Draft

## Trigger / Outcome
- Initiator: startup after process crash, shutdown, or interrupted active run.
- Outcome: durable state and pending work are recovered or safely blocked, without repeating an uncertain write or GUI action.

## Inputs
- Persisted run states, last tool IDs/results, schedule occurrence IDs, approval state, notification outbox and previous agent session metadata.

## Outputs
- Recovered pending/queued tasks, interrupted runs with explainable states, replay-safe notification delivery and correct next scheduler occurrence.

## Flow
1. On startup, check durable store and find incomplete runs/occurrences/approvals.
2. Distinguish queued work (safe to start) from operations whose result is unknown.
3. Mark interrupted GUI runs blocked/interrupted; never replay stale coordinates or old screenshots.
4. Preserve pending approvals and expiry rules; require valid owner decision for a continued sensitive action.
5. Restore schedule based on occurrence deduplication and configured misfire policy.
6. Resume eligible outbound delivery independently of run execution.

## Validations
- Consistent persisted state, unique operation IDs, session-provider compatibility, expiry and ownership.

## Invariants
- Unknown external outcomes never trigger blind automatic retries.
- Notification retry cannot create a duplicate task run.
- Restart does not silently elevate permissions or revive expired approvals.

## Failures / Side Effects
- Corrupt state is reported; backup/recovery follows documented policy, not guessing.
- Schedule/run/approval/notification state may transition; already-effective external actions must not be duplicated.

## Dependencies
- Flow: FDD-001 — run lifecycle.
- Flow: FDD-003 — scheduled occurrences.
- Flow: FDD-005 — approvals.
- Flow: FDD-006 — notification outbox.

## Scenarios
- S1: Given an interruption after an external write whose result is unknown, when app restarts, then the write is not repeated automatically. [untested]
- S2: Given a persisted queued read-only run, when app restarts, then recovery safely requeues it at most once. [untested]
- S3: Given a pending notification and completed run, when app restarts, then only delivery is retried. [untested]

## Implementation References
- Planned: `docs/DEVELOPMENT_PLAN_v1.1.md` §§9, 13; backlog PR-03, PR-10, PR-11, PR-12.
- Schema foundation (PA-017): `src/PersonalAgent.Persistence/AgentDbContext.cs`, `Entities/RunEntities.cs` and `Migrations/` define versioned run payloads, conversation/task references, UTC ticks, application-managed RunVersion and append-only events. `tests/PersonalAgent.IntegrationTests/PersistenceSchemaTests.cs` applies/reapplies migrations to real temporary SQLite files and verifies reopen/data/constraints. There is no startup recovery or durable runtime integration yet; complete S1-S3 remain `[untested]`.

## Unknowns
- Exact recovery database migrations and idempotency key shape will be validated during implementation.
