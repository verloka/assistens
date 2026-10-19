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
- Trusted run access contexts may restrict explicit action categories and are bound to the run and conversation. Categories grant no implicit higher or lower category; unknown classification is denied. Model/tool arguments cannot change that context, and passing this restriction never replaces tool/destination authorization or exact-action approval.
- Per-run tool rules use exact tool/source/risk identities with explicit deny precedence. MCP rules additionally intersect owner connection grants for the same connection, tool, classification and declaration version. Sensitive allowed scope requires exact-action approval; scope alone does not authorize a write. New declarations or versions inherit no permission.
- Durable pending requests bind canonical action payload, exact ToolCallId/tool/source/risk/version, run/conversation revision, authorized owner and expiry. Decisions use an expected request revision and action hash; stale, mismatched, expired or repeated decisions fail without side effects. A persisted Approved state alone never dispatches an operation or replays a run after restart.

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
- PA-078: IApprovalRepository/SqliteApprovalRepository persists one bounded exact PendingToolAction per Run/ToolCallId in ApprovalRequests. Canonical hash binds operation, arguments, displayed description/destination, identities, classification/version and run revision; ambiguous duplicate JSON keys are refused. Authenticated owner, expected request revision/hash, unexpired lifetime and current WaitingForApproval run revision are atomically checked on decision. Expiry/rejection/approval is durable, competing/repeated/stale decisions fail closed, and reads reject unknown schema/corrupt action data. [test: tests/PersonalAgent.UnitTests/ApprovalRequestTests.cs] [test: tests/PersonalAgent.IntegrationTests/ApprovalRepositoryTests.cs] verify real SQLite reopen/races/invalidation. No operation is dispatched and approval does not resume an interrupted run; UI/lease/consumption integration follows PA-079..081. Complete S1-S3 remain untested/Draft.
- PA-077: Core ToolPermissionPolicy intersects immutable run tool/source/risk/version rules, ToolAccessContext and existing owner McpToolPermission snapshots. Exact deny takes precedence; no wildcard/category hierarchy or new-tool inheritance. MCP descriptors now carry their existing discovery version (null omitted for legacy/local descriptors), and execution verifies agreement with the reviewed declaration. Policy returns RequiresApproval for sensitive scopes; it supplies no durable consent. [test: tests/PersonalAgent.UnitTests/ToolPermissionPolicyTests.cs] [test: tests/PersonalAgent.IntegrationTests/McpRunPermissionTests.cs] verify scoped rules through actual SDK/common registry, unknown-tool hints, revocation, changed-version re-review, read/write isolation and no write on scope alone. Default App tools remain closed and complete S1-S3 remain untested/Draft.
- PA-076: Core ToolAccessContext snapshots explicit risk categories for one run/conversation; optional trusted ToolRegistry context restricts the existing authorization callback after the normal pause boundary. The callback cannot override the context, and category access alone cannot approve an MCP tool. Unknown tools remain denied. Navigation is appended to the existing enum without renumbering persisted categories; existing generic GUI input remains Write and screenshots remain ExternalDisclosure even though capture is read-only. [test: tests/PersonalAgent.UnitTests/ToolAccessContextTests.cs] [test: tests/PersonalAgent.IntegrationTests/ToolAccessPolicyTests.cs] verify cross-run refusal, immutable/nonhierarchical categories, model-argument privilege spoofing through actual MAF and preserved default connection denial. Per-tool run/connection grants follow PA-077; exact durable approvals remain PA-078..081. Complete S1-S3 remain untested/Draft.
- SDK diagnostic only (PA-006): `tests/PersonalAgent.IntegrationTests/MafToolApprovalSpikeTests.cs` executes the real MAF pipeline with an in-memory tool and scripted model; it checks approval/denial continuation and original-argument binding. This does not cover owner authorization, durable pending actions, expiry or real external writes; S1–S3 above remain `[untested]` as complete business outcomes. Reproduction and limits: `tests/README.md`.

## Unknowns
- Final risk classification for GUI actions in apps without semantic accessibility data remains a design consideration.
