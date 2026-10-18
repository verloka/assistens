# FDD-001 — Manual task execution to final result

Status: Draft

## Trigger / Outcome
- Initiator: user submits a natural-language task through WPF or an authorized inbound channel.
- Outcome: a single task run has a terminal state, persisted summary, progress history and appropriate notification; or an explainable failure/cancellation.

## Inputs
- Task text, sender identity, origin, selected model profile, allowed tool IDs, limits, optional conversation ID/destination.

## Outputs
- Run ID, status, structured result with available sources and usage, history; rejection when unauthorized or prerequisites missing.

## Flow
1. Validate user/channel identity, task payload and requested model/tool availability.
2. Persist run creation, queue the run and select the agent runtime.
3. Provide allowed tools and bounded context to the model, apply the tool permission policy for each call.
4. Obtain desktop access through FDD-002 only when a GUI operation is about to occur; use FDD-004 for MCP operations.
5. Emit progress; honor user cancellation, pause, and FDD-005 approvals at every safe boundary.
6. Persist terminal outcome and request delivery via FDD-006; notification failure does not roll back run completion.

## Validations
- Authorized sender; supported model capabilities (vision and tool calls for GUI); limits; tool allowlist; active session for Windows GUI.

## Invariants
- A task never bypasses tool approval, human desktop takeover or limits.
- Task success is independent of Telegram delivery success.
- A task has one durable terminal state; partial failures do not claim `Completed`.

## Failures / Side Effects
- Unavailable provider/credentials, user cancel, GUI blocked, MCP failure, policy rejection, timeouts and unknown external side effects are recorded.
- Writes: run/history/events; may perform approved external actions; produces notifications.

## Dependencies
- Flow: FDD-002 — desktop access.
- Flow: FDD-004 — MCP tool execution.
- Flow: FDD-005 — sensitive-action approval.
- Flow: FDD-006 — notifications and follow-ups.

## Scenarios
- S1: Given an authorized user and available agent, when a read-only manual task is submitted, then a persisted result and notification are produced. [untested]
- S2: Given an active run, when cancellation is requested, then no additional tool calls start after cancellation is observed and the outcome is `Cancelled`. [untested]
- S3: Given a completed run with a failed delivery attempt, when delivery retries later, then the task remains `Completed`. [untested]

## Implementation References
- Planned: `docs/DEVELOPMENT_PLAN_v1.1.md` §§5–9, 11, 13; backlog PR-02, PR-07, PR-12.

## Unknowns
- Actual types and source/test paths are not yet implemented. Confirm approval and E2E proof as code lands.
