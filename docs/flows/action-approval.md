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
- The local nonmodal approval screen shows the exact action, risk, destination, arguments and expiry to the current Windows owner. Approve requires explicit review acknowledgement; Deny does not. Refresh or a changed selection cannot silently transfer acknowledgement to another request. Opening/closing the screen performs no decision or tool dispatch and does not take focus automatically.
- Before non-idempotent dispatch, durably reserve the exact Run/ToolCallId and single-use approved action. Persist acknowledgement separately from dispatch; exception, lost acknowledgement or restart leaves OutcomeUnknown and requires checking real-world state. Neither the same call nor its approval can dispatch again.

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
- S3: Given a denied action, when run resumes, then no external write occurs. [test: tests/PersonalAgent.IntegrationTests/HumanApprovalTests.cs]

## Implementation References
- Planned: `docs/DEVELOPMENT_PLAN_v1.1.md` §§7, 9, 14; backlog PR-10, PR-12.
- PA-081: ToolRegistry with IToolCallRepository reserves non-idempotent operations before invocation using the exact durable approval grant issued by HumanApprovalAuthorizer. SqliteToolCallRepository checks current Running revision, expiry and canonical argument/tool/source/version identity in one transaction; unique Run/ToolCallId and approval ID prevent duplicate dispatch and consent reuse. Separate completion records Acknowledged or OutcomeUnknown; exceptions/lost ack persistence cannot become success or retry. Known read-only operations retain their existing path. MAF presents a verification instruction for unknown outcomes. [test: tests/PersonalAgent.IntegrationTests/ToolDispatchTests.cs] covers one racing reservation winner, exact identity/TTL/run/rejection checks, no effect on reservation failure, lost ack persistence and actual MCP timeout after one effect. [test: tests/PersonalAgent.IntegrationTests/ToolDispatchRecoveryTests.cs] verifies actual process death and App restart/history without replay. Previous079/080 fixtures now use the durable ledger. All Draft; full real send/recipient S1/S2 and production task tool selection remain separate.
- PA-080: MainWindow Approvals opens an owned nonmodal ApprovalsWindow using Show, no automatic launch/focus takeover. Current Windows SID supplies the trusted desktop ApprovalOwner. The UI shows exact operation/destination/risk/tool/source, untrusted arguments as read-only plain text and expiry; Approve requires deliberate acknowledgement, Deny does not. Same-request polling preserves review, changed selection/revision/expiry/close clears it; stale and other-owner decisions remain refused by repository CAS. Pending UI queries exclude invalidated run revisions, and sanitized errors never expose raw provider/storage messages. [test: tests/PersonalAgent.IntegrationTests/ApprovalsUiTests.cs] loads actual XAML with shared079 fixture/SQLite/local MCP and verifies both decisions, responsive dispatcher, selection isolation, stale/foreign-owner refusal, close without consent, expiry/cancel and actual SID DI. No real window is shown in automation and no corporate/native/paid operations occur. S3 local/UI PASS; complete send/recipient S1/S2 and durable dispatch081 remain separate. All Draft; default production task tools stay closed.
- PA-079: HumanApprovalAuthorizer suspends the exact validated invocation in the existing MAF session, commits WaitingForApproval and waits for the repository decision; common risk/allowlist/schema/pause/cancel checks remain mandatory. Any held desktop lease must be yielded before a request can be presented; ComputerToolSession.YieldDesktopAsync clears frame/coordinates/controller. Approve explicitly continues that exact pending call, Reject/expiry returns controlled denial; ordinary Resume cannot approve. Single-use in-memory receipt rechecks durable decision, arguments, expiry, current policy and the post-decision run revision immediately before dispatch after all boundary waits. Pause/cancel/revocation invalidate continuation and never auto-resume human control. [test: tests/PersonalAgent.IntegrationTests/HumanApprovalTests.cs] verifies actual Core/MAF/SQLite and local/loopback MCP writes, no write before consent, Reject, TTL, cancellation, owner pause and post-approval expiry/pause. [test: tests/PersonalAgent.IntegrationTests/ComputerToolTests.cs] verifies yield releases the lease and requires fresh capture/evaluation before input; stale approved GUI coordinates are never rewritten silently. S3 deterministic acceptance PASS. WPF presentation080, durable dispatch/unknown outcomes081 and complete S1/S2 remain separate; all Draft.
- PA-078: IApprovalRepository/SqliteApprovalRepository persists one bounded exact PendingToolAction per Run/ToolCallId in ApprovalRequests. Canonical hash binds operation, arguments, displayed description/destination, identities, classification/version and run revision; ambiguous duplicate JSON keys are refused. Authenticated owner, expected request revision/hash, unexpired lifetime and current WaitingForApproval run revision are atomically checked on decision. Expiry/rejection/approval is durable, competing/repeated/stale decisions fail closed, and reads reject unknown schema/corrupt action data. [test: tests/PersonalAgent.UnitTests/ApprovalRequestTests.cs] [test: tests/PersonalAgent.IntegrationTests/ApprovalRepositoryTests.cs] verify real SQLite reopen/races/invalidation. No operation is dispatched and approval does not resume an interrupted run; UI/lease/consumption integration follows PA-079..081. Complete S1-S3 remain untested/Draft.
- PA-077: Core ToolPermissionPolicy intersects immutable run tool/source/risk/version rules, ToolAccessContext and existing owner McpToolPermission snapshots. Exact deny takes precedence; no wildcard/category hierarchy or new-tool inheritance. MCP descriptors now carry their existing discovery version (null omitted for legacy/local descriptors), and execution verifies agreement with the reviewed declaration. Policy returns RequiresApproval for sensitive scopes; it supplies no durable consent. [test: tests/PersonalAgent.UnitTests/ToolPermissionPolicyTests.cs] [test: tests/PersonalAgent.IntegrationTests/McpRunPermissionTests.cs] verify scoped rules through actual SDK/common registry, unknown-tool hints, revocation, changed-version re-review, read/write isolation and no write on scope alone. Default App tools remain closed and complete S1-S3 remain untested/Draft.
- PA-076: Core ToolAccessContext snapshots explicit risk categories for one run/conversation; optional trusted ToolRegistry context restricts the existing authorization callback after the normal pause boundary. The callback cannot override the context, and category access alone cannot approve an MCP tool. Unknown tools remain denied. Navigation is appended to the existing enum without renumbering persisted categories; existing generic GUI input remains Write and screenshots remain ExternalDisclosure even though capture is read-only. [test: tests/PersonalAgent.UnitTests/ToolAccessContextTests.cs] [test: tests/PersonalAgent.IntegrationTests/ToolAccessPolicyTests.cs] verify cross-run refusal, immutable/nonhierarchical categories, model-argument privilege spoofing through actual MAF and preserved default connection denial. Per-tool run/connection grants follow PA-077; exact durable approvals remain PA-078..081. Complete S1-S3 remain untested/Draft.
- SDK diagnostic only (PA-006): `tests/PersonalAgent.IntegrationTests/MafToolApprovalSpikeTests.cs` executes the real MAF pipeline with an in-memory tool and scripted model; it checks approval/denial continuation and original-argument binding. This does not cover owner authorization, durable pending actions, expiry or real external writes; S1–S3 above remain `[untested]` as complete business outcomes. Reproduction and limits: `tests/README.md`.

## Unknowns
- Final risk classification for GUI actions in apps without semantic accessibility data remains a design consideration.
