# FDD-005 — Human approval of sensitive actions

Status: Draft

## Trigger / Outcome
- Initiator: a tool invocation is classified as sensitive (write, send, delete, external disclosure).
- Outcome: the **exact pending action** is either approved once and performed or rejected/expired without side effect.

## Inputs
- Run ID, ToolCallId, tool name, validated arguments, recipient/destination, risk classification, owner approval response and expiry.

## Outputs
- `Approved`, `Rejected`, `Expired` or `OutcomeUnknown`; run continuation appropriate to the decision.

## Flow
1. Apply deterministic policy before any side-effecting tool call (MCP or GUI).
2. Persist a request describing the exact operation and target. Present it to the authorized user.
3. Suspend only the dependent action while awaiting approval, without keeping desktop control unnecessarily.
4. Accept an approval or denial only for the pending action with the matching ID, version and authorized owner.
5. On approval, recheck expiry and arguments and execute no more than once; on rejection inform agent without executing.
6. Unknown outcomes require checking real-world state before retrying.

## Validations
- Identity, destination allowlist, action hash/ID, expiration and single-use acknowledgement.

## Invariants
- Approving action A never approves modified action B.
- LLM text alone cannot approve a policy-restricted action.
- No blind retries of external writes when outcome is unknown.

## Failures / Side Effects
- User absent, expired approval, restart, channel failure or changed action returns a blocked/denied status.
- Persisted approval event; external side effect only after accepted approval.

## Dependencies
- None.

## Scenarios
- S1: Given a pending send-message action, when the user approves its exact arguments, then it executes once. [untested]
- S2: Given an approval for one recipient, when model changes recipient, then new approval is required. [untested]
- S3: Given a denied action, when run resumes, then no external write occurs. [untested]

## Implementation References
- Planned: `docs/DEVELOPMENT_PLAN_v1.1.md` §§7, 9, 14; backlog PR-10, PR-12.
- SDK diagnostic only (PA-006): `tests/PersonalAgent.IntegrationTests/MafToolApprovalSpikeTests.cs` executes the real MAF pipeline with an in-memory tool and scripted model; it checks approval/denial continuation and original-argument binding. This does not cover owner authorization, durable pending actions, expiry or real external writes; S1–S3 above remain `[untested]` as complete business outcomes. Reproduction and limits: `tests/README.md`.

## Unknowns
- Final risk classification for GUI actions in apps without semantic accessibility data remains a design consideration.
