# Implementation journal

Append only: date, task ID, changes, tests, evidence, blockers, next action. No secrets/screenshots/message bodies.

## 2026-10-18 10:47:45 +03:00 | PA-001 | done
- Restored state from catalog/checkpoint and repository: no application existed; all 125 tasks were pending. Existing planning/FDD files and README/.gitignore edits were present before this session.
- Fixed taskctl.ps1 PowerShell 5.1 parse failure by replacing the UTF-8 em dash in a source string with ASCII; doctor/next/start now work.
- Added .NET 10 solution, seven production projects, three xUnit projects and SDK pin. Adapters reference Core only; App composes modules.
- Checks: dotnet build --no-restore passed on Windows (0 warnings/errors); dotnet test tests/PersonalAgent.UnitTests --filter ArchitectureTests --no-build passed (1 test); doctor validates 125 tasks.
- FDD: scaffolding only; no business behavior/scenario claimed. Integration/E2E projects are scaffolds, not passing business E2E evidence.
- Unresolved issues: none for PA-001. Next: PA-002 Generic Host lifecycle.

## 2026-10-18 10:53:03 +03:00 | PA-002 | blocked (manual WPF smoke)
- Implemented Generic Host (Microsoft.Extensions.Hosting 10.0.0), DI composition, ILogger lifecycle events, startup failure reporting, cancellation during startup, asynchronous stop/disposal before WPF shutdown, repeated-close protection and host-triggered shutdown dispatch. Main window does not activate itself when shown.
- Added four integration tests against the real Generic Host: worker cancellation/lifecycle logging/disposal, partial-start failure cleanup, cancellation during startup, and disposal after stop failure.
- Checks: dotnet test tests/PersonalAgent.IntegrationTests --filter DesktopHostTests --no-restore passed (4); dotnet build --no-restore passed on Windows (0 warnings/errors); dotnet test --no-build passed (1 architecture + 4 lifecycle tests). E2ETests contains no tests; no business or Windows UI E2E success is claimed. git diff --cached --check passed.
- FDD develop/verify: reviewed index, FDD-001 and FDD-007, and reverse consumers FDD-003/FDD-007 of FDD-001. This change only initializes/stops the desktop host; it adds no task execution, recovery, GUI input, MCP or notification behavior. All Draft scenario links remain untested; no Active contract or flow documentation changed. Actual diff preserves Core isolation (architecture regression passed). WPF event integration remains NOT TESTED manually.
- Blocker: TASKS.md PA-002 explicitly requires manual smoke; no desktop-control tools are available in this session. Requested user check after implementation was ready; no result has been received. This is a verification blocker, not missing Windows/.NET runtime. Code and automated checks are preserved.
- Manual check: from repository root run dotnet run --project src/PersonalAgent.App; confirm the empty Personal Agent window opens, close it with X, verify the command returns without error and no PersonalAgent.App process remains.
- Next: after successful manual evidence, unblock PA-002, start/resume it, then complete with the actual user-observed result. Tracker has no other ready task; no dependency was bypassed. PA-001 committed as c1f75bb.

## 2026-10-18 10:54:59 +03:00 | PA-002 | done (manual blocker resolved)
- User requested launch; ran dotnet run --project src/PersonalAgent.App --no-build. User observed the empty desktop window and then confirmed closing it.
- Checks: actual process session 29485 logged HostStarting 1000, Application started, Application is shutting down and HostStopping 1001; exited 0. Previously verified unchanged code build (0 warnings/errors), 1 architecture test and 4 host integration tests remain the automated evidence. Manual startup/shutdown smoke is now passed; no additional E2E claim.
- Unblocked, resumed and completed PA-002 with user observation and actual process output. No code changes or unresolved PA-002 issues. Next: PA-003 health/status screen.

## 2026-10-18 10:58:37 +03:00 | PA-003 | done
- Added MVVM health/status view: actual Host lifetime state, registered hosted service names, asynchronous five-second diagnostic with duplicate prevention and shutdown cancellation. No LLM or desktop input.
- Checks: HealthScreenTests 2 passed using actual XAML and STA dispatcher (no visible window); verifies bound status/components, dispatch while long operation is pending, disabled duplicate execution and cancellation/state refresh on stopping. dotnet build --no-restore: 0 warnings/errors; dotnet test --no-build: 1 architecture + 6 integration passed, E2E suite remains empty. Cached diff whitespace check passed.
- FDD develop/verify: diagnostic maintenance adds no business outcome; reviewed lifecycle consumers FDD-001/FDD-007, preserved all unimplemented business scenarios and Draft statuses. Index now distinguishes implemented diagnostics from planned business flows. No false scenario coverage links. Unresolved: none for PA-003. Next: PA-004.

## 2026-10-18 11:01:59 +03:00 | PA-004 | done
- Verified official NuGet metadata and API documentation; pinned stable MAF 1.25.0, Extensions.AI 10.10.0 (MAF resolves AI.Abstractions 10.10.1), MCP 2.2.0, Hosting 10.0.12. Centralized all direct test/app versions and committed lock files for the full transitive graph. Core stays SDK-free.
- Added three scoped ADRs: verified SDK baseline; planned Windows capture/input boundary; planned protected secret storage. The latter two make no implementation/hardware/security acceptance claim.
- Checks: dotnet restore and restore --locked-mode passed; dotnet build --no-restore 0 warnings/errors; dotnet test --no-build 1 architecture + 7 integration passed (SDK type-load check included); E2E suite still empty; cached diff check passed.
- FDD: package/configuration maintenance; no business contracts changed. Provider calls, approval/resume and transport behavior remain unverified until PA-006/PA-007. No external keys used. Unresolved: none for PA-004. Next: PA-005.

## 2026-10-18 11:03:03 +03:00 | PA-005 | done
- Inspected and reused all three existing test projects and host probes/fakes; no duplicate scaffolding or smoke tests. Added fail-fast PowerShell 5.1 verify.ps1 for locked restore, build and tests, plus tests/README.md with focused commands and verification boundaries.
- Checks: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1 passed: restore, build 0 warnings/errors, 1 unit + 7 integration tests; E2ETests remains an empty scaffold. Cached diff whitespace check passed. FDD not applicable to documentation/test-command maintenance.
- Unresolved: none for PA-005. Next: PA-006 MAF local tool calling and approval/resume diagnostic; do not assume live provider credentials.

## 2026-10-18 11:06:21 +03:00 | PA-006 | done; session stopped at user request
- Built deterministic local SDK spike with the real ChatClientAgent and scripted IChatClient, not a fake agent. In-memory echo tool only; no credentials, network, production adapter or external effects.
- Five passing cases: local function dispatch and result round-trip; protected function suspends until approval then executes once; denial resumes without invocation; forged response arguments cannot replace the original call under default MAF binding; pre-cancelled run reaches neither model nor function.
- Documented actual SDK types/methods and limitations in tests/README.md. ApprovalRequiredAIFunction is only a marker; enforcement depends on the invocation pipeline. Non-streaming fake does not validate providers, vision, durable authorization/expiry, restart or replay/unknown-outcome safety.
- Checks: dotnet test tests/PersonalAgent.IntegrationTests --filter MafToolApprovalSpikeTests --no-restore: 5 passed. scripts/verify.ps1 via Windows PowerShell 5.1: locked restore passed; build 0 warnings/errors; 1 architecture + 12 integration passed. E2E suite remains empty. Cached diff whitespace check passed.
- FDD develop/verify: actual diff adds SDK diagnostics and documentation only. Inspected FDD-005 and consumers FDD-001/FDD-004/FDD-007; scoped SDK handshake PASS; complete business S1-S3 remain NOT TESTED, all flows remain Draft. Kept scenario markers untested and added explicitly limited implementation evidence.
- Unresolved: none for PA-006 acceptance (reproducible SDK smoke exists). Live model and application-policy verification remain future tasks.
- User requested finishing the current task and stopping. PA-007 is pending and was not started; no autonomous continuation after this handoff.

## 2026-10-18 13:30:26 +03:00 | PA-007 | done
- Added test-only MCP server (2.2.0) and two real transport tests: stdio child process and loopback Kestrel Streamable HTTP. Both initialize, discover the echo schema and invoke it; shutdown releases server resources and the child exit is asserted. No commercial credentials or production policy changes.
- Checks: targeted McpTransportSpikeTests 2 passed; scripts/verify.ps1: locked restore passed, build 0 warnings/errors, 1 unit + 14 integration passed; E2E scaffold empty. ASP.NET shared-framework pruning explains the integration lock-file reduction; production locks unchanged.
- FDD develop/verify: reviewed FDD-004, approval dependency FDD-005 and reverse consumers FDD-001/FDD-003/FDD-007. Actual diff only adds diagnostics. SDK transport scope PASS; complete S1-S3 NOT TESTED. All flows remain Draft; limited implementation reference added without changing scenario coverage.
- Unresolved: remote TLS/OAuth, corporate servers, reconnect and production policy are future scope. No blocker for PA-007. Next: PA-008 Windows diagnostic.

## 2026-10-18 13:32:37 +03:00 | PA-008 | blocked (attended Windows smoke)
- Added standalone opt-in WPF diagnostic: user button gates cropped in-memory GDI capture, a single SendInput Unicode pair in its own focused field and Ctrl+Shift+F10 registration. No automatic launch, model, clipboard, foreground activation, external app input or screenshot persistence. Registered hotkey is removed on close; partial input never retries the character.
- Checks: dotnet build tests/PersonalAgent.WindowsSmoke passed with 0 warnings/errors; scripts/verify.ps1 passed locked restore, build 0 warnings/errors, 1 unit + 14 integration tests. E2E empty. No attended GUI or hardware check was run.
- FDD develop/verify: reviewed FDD-002 and consumers FDD-001/FDD-003/FDD-007. Actual diff contains an isolated attended fixture, no runtime tools or production lease behavior. Static safety scope reviewed; FDD-002 S1-S4 NOT TESTED. No Draft promotion or false test links.
- Blocker: requires user-operated foreground test and observation of capture, one x, WM_HOTKEY, shutdown, DPI/monitor environment. Requested result; unavailable unattended desktop verification cannot establish acceptance. UIPI/DPI/monitor/lock limits and exact manual steps documented in tests/README.md.
- Next: independent PA-009 (depends on PA-005); do not bypass PA-008 for tasks that depend on it.

## 2026-10-18 13:34:01 +03:00 | PA-009 | done
- Inspected Core (empty scaffold); added SDK/UI-free domain records for TaskDefinition, TaskRun, Conversation and RunEvent with IDs, origin, UTC timestamp fields, version, summary, sources and structured event payload. Used full status list from plan 9.2, not a new Started alias from the abbreviated task description.
- Checks: DomainSerializationTests 2 passed (JSON round-trip/link/result preservation and Core assembly references); scripts/verify.ps1 locked restore passed, build 0 warnings/errors, 3 unit + 14 integration passed; E2E empty. git diff --check passed.
- FDD develop/verify: reviewed lifecycle FDD-001 and consumers FDD-003/FDD-007, conversation consumer FDD-006. Actual diff adds neutral data contracts only; no execution/storage/delivery or approved contract changes. Complete scenarios remain NOT TESTED, all Drafts preserved. No extra flow documents.
- Unresolved: PA-008 still awaits attended Windows result; unrelated to PA-009 dependency. Next: PA-010 state machine.

## 2026-10-18 13:36:47 +03:00 | PA-010 | done
- Added pure snapshot state machine: explicit legal transitions, stable start timestamp, UTC end timestamp, version increment/conflict, pause scope and required block/failure reasons. Completed/Failed/Cancelled/Skipped/Interrupted cannot transition; Blocked requires explicit requeue. Atomic compare-and-swap remains a store responsibility in PA-018, not claimed from this pure method.
- Checks: dotnet test tests/PersonalAgent.UnitTests --filter TaskRun --no-restore: 21 passed; scripts/verify.ps1 locked restore passed, build 0 warnings/errors, 24 unit + 14 integration passed; E2E empty.
- FDD develop/verify: inspected lifecycle FDD-001 and reverse consumers FDD-003/FDD-007 plus pause/approval/delivery contracts FDD-002/FDD-005/FDD-006. Actual diff preserves terminal outcomes and separates pause from cancel. Domain scope PASS; no actual tool cancellation, durable store or native GUI gate claimed. Business scenarios remain NOT TESTED; all Drafts preserved.
- PA-008 manual result arrived during PA-010: capture 850x700, DPI 120, one monitor, origin 0,0; exactly one x; WM_HOTKEY observed. Process session 41222 exited 0; no WindowsSmoke process remains. Reconcile PA-008 next without a second in_progress task.
- Unresolved: none for PA-010. Next: record PA-008 completion, then PA-011.

## 2026-10-18 13:37:12 +03:00 | PA-008 | done (manual blocker resolved)
- Launched existing WindowsSmoke at user's explicit request, dotnet run --project tests/PersonalAgent.WindowsSmoke --no-build (session 41222). User operated button and shortcut, reporting: Capture OK 850x700, DPI=120, monitors=1, virtual origin=(0,0); SendInput observed OK one x; WM_HOTKEY observed OK with no input from handler.
- Checks: session 41222 exited 0 after user closed window; Get-Process PersonalAgent.WindowsSmoke found none. Exact fixture had build 0 warnings/errors; current full suite 24 unit + 14 integration passed. This is a limited attended spike, not business E2E or a full hardware matrix. No second GUI run was injected.
- Single-task tracker policy preserved: finished PA-010 before unblocking/starting/completing PA-008. Updated ADR with actual evidence and unknown OS/elevation; tests/README.md records limitations. FDD-002 S1-S4 still NOT TESTED as complete production flows; all statuses remain Draft.
- Unresolved: none for PA-008 spike. Production lease, input cleanup stress, mixed DPI, multi-monitor, UAC/lock and remote sessions remain future verification. Next: PA-011.

## 2026-10-18 13:38:26 +03:00 | PA-011 | done
- Defined IAgentRuntime and neutral request/context, positive per-run limits, typed public operation events and structured result/usage/artifacts. SDK sessions and hidden model reasoning are excluded from these boundary payloads. Reused existing SourceLink and domain IDs.
- Checks: AgentContractTests 3 passed (JSON/request/result, context/source/usage/progress transfer and pre-cancelled probe); scripts/verify.ps1 locked restore passed, build 0 warnings/errors, 27 unit + 14 integration passed; E2E empty. Probe checks the interface contract, not a production model implementation.
- FDD develop/verify: inspected FDD-001 and dependency/reverse-consumer boundary FDD-002/FDD-003/FDD-004/FDD-005/FDD-006/FDD-007. Actual diff only adds SDK-free types and contract tests; run control, gates, storage and delivery unchanged. Contract scope PASS, complete business scenarios NOT TESTED; all Drafts retained.
- Unresolved: none for PA-011. Next: PA-012 deterministic FakeAgentRuntime; no keys needed.

## 2026-10-18 13:40:10 +03:00 | PA-012 | done
- Added FakeAgentRuntime in Agent module, reusing IAgentRuntime. Copied deterministic steps emit Start/progress/Complete; async injected boundaries support controllable pause/release/cancel tests. Enforced profile=fake, positive budgets, max steps and linked duration cancellation; scripted failures never emit Completed. No credentials, model, tool, network or desktop actions.
- Checks: FakeAgentRuntimeTests 6 passed: ordered result/progress, pause/cancel prevents next step, same-run release, pre-cancel no work, limits/profile refusal and scripted failure. scripts/verify.ps1 locked restore passed, build 0 warnings/errors, 27 unit + 20 integration passed; E2E empty.
- FDD develop/verify: reviewed FDD-001, pause dependency FDD-002 and reverse consumers FDD-003/FDD-007 plus delivery/approval invariants. Actual adapter diff PASS within local fake scope. Application submit/queue, actual desktop gates, persistence and notifications not yet implemented; complete business S1-S3 NOT TESTED. Updated limited implementation reference/README, all Drafts preserved.
- Unresolved: none for PA-012. Next: PA-013 common commands and idempotency.

## 2026-10-18 13:44:18 +03:00 | PA-013 | done
- Added common Submit/Cancel/Pause/Resume commands and in-memory TaskRunCoordinator as session-local source of snapshots/events. Command receipts bind IDs to type and payload hash; duplicate submit queues one run, duplicate commands return current snapshot, changed payload reuse is rejected. Versions are checked under the state lock; cancellation callbacks execute outside it.
- EntireRun pauses at controllable boundaries; queued tasks pause before execution and resume back to Queued. Already-started paused attempts cannot requeue. GuiOnly is gate metadata and permits independent non-GUI work; no desktop driver or GUI resume authority exists yet. Cancelled status is checked at boundaries even before asynchronous token callbacks complete.
- Checks: Commands filter 6 passed, full unit suite 35 passed (including two new state-machine regressions); final scripts/verify.ps1 locked restore passed, build 0 warnings/errors, 35 unit + 20 integration passed; E2E empty. Actual diff reviewed for cancel/resume race and phase boundaries.
- FDD develop/verify: FDD-001 lifecycle and consumers FDD-003/FDD-007, FDD-002 pause separation and FDD-006 channel-neutrality inspected. Session-local command scope PASS; complete business S1-S3 NOT TESTED. Updated partial references only; all flows remain Draft. Durable dedup/storage and actual GUI gating not claimed.
- Unresolved: none for PA-013. Next: PA-014 queue consumer/runtime lifecycle and timeouts.

## 2026-10-18 13:48:00 +03:00 | PA-014 | done
- Added one queue consumer over coordinator-owned runs/events/results. Execution starts atomically once, uses caller/run/duration cancellation, respects async pause boundaries, records terminal output and sanitizes exception text. Shutdown interrupts an active attempt with no automatic replay; queued not-started work remains queued. No native input/provider calls.
- Extended coordinator with terminal await, typed progress mapping and result snapshots. Cancelled/terminal states ignore late progress/results. Boundary rechecks pause state after each wake; pre-start pause resumes to queue, active pause resumes the same execution. Cooperative adapters remain responsible for observing cancellation/boundaries; this is not a desktop lease.
- Checks: TaskRunnerTests 8 passed (50 duplicate submits execute once, ordered event history, active cancel beats late success/progress, timeout then next task, pause/resume and pause/cancel, cancelled queue item, interruption and second consumer refusal). scripts/verify.ps1 locked restore passed, build 0 warnings/errors, 35 unit + 28 integration passed; E2E empty; staged diff check passed.
- FDD develop/verify: actual diff reviewed against FDD-001 lifecycle, FDD-002 pause/cancel separation and FDD-003/FDD-006/FDD-007 consumers. Session-local fake queue scope PASS; durable storage, actual tools/approval/desktop gates and delivery NOT TESTED. Updated limited references; all flows remain Draft and complete scenarios untested.
- Unresolved: none for PA-014. Next: PA-015 Dashboard progress and common commands.

## 2026-10-18 13:52:00 +03:00 | PA-015 | done
- Added DashboardViewModel using shared application commands and snapshot/event queries, bound local task UI, real TaskRunnerService and DI composition over fake profile. Existing health screen remains functional. UI shows operation progress/status/result and pause/resume/cancel; starting tasks does not activate windows or call models/tools/desktop. Clearly states that history clears on close.
- Coordinator now exposes ordered async RunEvent stream: atomically replay existing events and subscribe to new ones, complete on terminal outcome and detach on cancellation. Streaming event metadata is created before publication; no UI/SDK references added to Core.
- Checks: DashboardTests 2 passed with actual XAML, real host/runner and STA Dispatcher (no visible window): bindings, input-to-command, responsive callback while runtime waits, pause/resume same run, ordered progress/summary, cancel and shutdown interruption. scripts/verify.ps1 locked restore passed, build 0 warnings/errors, 35 unit + 30 integration passed including existing health/host regression; E2E empty; git diff --check passed.
- FDD develop/verify: actual diff reviewed against FDD-001 and consumers FDD-003/FDD-007; pause/delivery separation FDD-002/FDD-006 preserved. Partial local UI flow PASS; no durability, notifications, GUI tools or complete S1-S3 proof. Updated index and real references; all flows remain Draft. No manual visual smoke was run for this task; acceptance uses automated actual-XAML test as allowed.
- Unresolved: none for PA-015. Next: PA-016 targeted concurrent-command/fake-flow regression gaps and deterministic E2E suite.
