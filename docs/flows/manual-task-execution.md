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
- Domain foundation (PA-009/PA-010): `src/PersonalAgent.Core/Domain/TaskModels.cs` and `TaskRunStateMachine.cs`; `tests/PersonalAgent.UnitTests/DomainSerializationTests.cs` and `TaskRunStateMachineTests.cs` cover neutral JSON contracts, legal/illegal state changes, terminal immutability, pause boundaries and stale versions. Transitions return snapshots; callers must still implement atomic storage and actual cancellation/tool gates. Interrupted ends the attempt and cannot automatically resume. These unit checks do not complete S1-S3; all remain `[untested]`.

- Local fake adapter (PA-011/PA-012): `src/PersonalAgent.Core/Agents/AgentContracts.cs`, `src/PersonalAgent.Agent/FakeAgentRuntime.cs` and `tests/PersonalAgent.IntegrationTests/FakeAgentRuntimeTests.cs` verify ordered Start/progress/Complete, cancellation, controlled step boundaries and limits without credentials. This is not yet a submitted application run, durable outcome or notification, so complete S1-S3 remain `[untested]`.

- Session-local commands (PA-013): `src/PersonalAgent.Core/Application/TaskCommands.cs`, `TaskRunCoordinator.cs` and `tests/PersonalAgent.UnitTests/TaskCommandsTests.cs` verify idempotent submit/cancel/pause/resume, payload-bound command IDs, optimistic versions and cancellable step boundaries. These command-level tests use in-memory state without a runtime consumer, durable store or native GUI driver; they do not complete S1-S3.

- Session-local runner (PA-014): `src/PersonalAgent.Core/Application/TaskRunner.cs` and `tests/PersonalAgent.IntegrationTests/TaskRunnerTests.cs` verify single consumption/execution under duplicate submits, ordered history, cancel vs late success/progress, pause acknowledgement and same-attempt resume, timeouts, sanitized failures and Interrupted on shutdown. No durable storage, external tools, notification delivery or business E2E proof is implied.

- Dashboard (PA-015): `src/PersonalAgent.App/ViewModels/DashboardViewModel.cs`, `Hosting/TaskRunnerService.cs` and `MainWindow.xaml` compose the local fake task path. `tests/PersonalAgent.IntegrationTests/DashboardTests.cs` checks actual XAML commands, ordered progress, pause/resume/cancel, responsive dispatcher and host shutdown without displaying a window. The observable session-local path works; durable storage and notifications required by complete S1-S3 remain `[untested]`.

## Unknowns
- Durable storage, notifications and business E2E acceptance remain pending. Confirm approval and proof as code lands.
