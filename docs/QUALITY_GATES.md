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
- [x] **E2E-03** Human Take Control, GUI actions cease, fresh screenshot on resume (`PR-06`,`PR-14`) — attended native stand; scope below
- [ ] **E2E-04** Scheduled run does not steal keyboard while user active (`PR-11`,`PR-14`)
- [x] **E2E-05** GUI lease gate after MCP-only steps (`PR-09`,`PR-11`) — attended native admission/local MCP; scope below
- [ ] **E2E-06** Telegram follow-up uses prior conversation (`PR-13`)
- [ ] **E2E-07** Send/write approval bound to exact pending action (`PR-10`,`PR-12`)
- [x] **E2E-08** No duplicate write after crash/unknown outcome (`PR-03`,`PR-10`) — actual killed child/local sandbox effect
- [x] **E2E-09** Second channel added via DI without changes to Core (`PR-12`) — explicitly required fake Discord
- [ ] **E2E-10** OpenAI→Claude switch via neutral context, not incompatible session serialization (`PR-08`,`PR-13`)

### PA-123 evidence audit — 2026-10-22

The checkboxes above require the complete original §18 outcome. Keep them open
until that outcome is established; a passing policy/adapter fixture alone does
not certify a live application journey. The table below records **scope** with
every result. No previous task acceptance is revoked and no Draft flow is
promoted. Historical attended evidence is dated; it was not rerun unattended.

Pre-binding automated baseline: commit `36cc359`,
`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1`:
locked restore PASS, full solution build **0 warnings / 0 errors**, **137 unit +
972 integration + 3 local fake E2E = 1112 passed, 0 failed, 0 skipped**.
The integration run took 2m56s. All suites linked below ran in that baseline.
The three tests in the E2ETests project are local fake-flow checks, not the ten
business acceptances in §18. The owner subsequently authorized explicit per-task
desktop GUI/MCP selection, defaulting to no tools. That production change and its
new controlled regressions are described below; live acceptance is still separate.

Post-binding verification on2026-10-22:
`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1` PASS:
locked restore, build **0 warnings / 0 errors**, **137 unit + 996 integration +
3 local fake E2E = 1136 passed, 0 failed, 0 skipped**; integration3m10s.
The24additional cases cover ordinary production tool composition and private
one-call screen-proof isolation. EF pending-model check reports no changes;
no migration or dependency update was needed.

| Scenario | Verified evidence and scope | Complete §18 outcome / remaining gate |
| --- | --- | --- |
| E2E-01 Manual Computer Use | **PASS, live read-only slice.** PA-059 owner report on2026-10-19: `titleMatched=True`, `modelCalls=2`, `captures=2`, `generation=1->2`, `hotkeyPause=True`, `freshAfterResume=True`, `durableHistory=True`, `nativeInput=0`. Reopened SQLite run `E7ADDF79-827F-4C2C-8993-DE169FCFA985` was Completed/version19, reported usage4892+26. [LiveOpenAiWindow](../tests/PersonalAgent.WindowsSmoke/LiveOpenAiWindow.cs); [ComputerToolTests](../tests/PersonalAgent.IntegrationTests/ComputerToolTests.cs) and [ProvidersContractTests](../tests/PersonalAgent.IntegrationTests/ProvidersContractTests.cs) cover controlled input/image contracts. | **PARTIAL.** The fixture reads its already-open authorized window. The exact requested “open the test application/read the tab” live-model journey, with actual model-directed app opening/input, has no report. Do not reinterpret read-only PA-059 as autonomous GUI-input acceptance. |
| E2E-02 Hybrid MCP + GUI | **PASS, controlled composition.** [HybridMcpGuiTests](../tests/PersonalAgent.IntegrationTests/HybridMcpGuiTests.cs) uses actual App/Core/MAF/SQLite and loopback Jira MCP with synthetic Teams/native boundaries; both orders, sources, admission, pause/cancel/timeout and read no-replay are checked. | **BLOCKED — PA-075.** No approved live Jira/ADO endpoint/credentials, authorized ticket/Teams context or organizational/model-data permission is available. Ordinary desktop tasks now bind explicit per-task GUI/MCP selections through the production runtime (ProductionTaskToolsTests); these controlled checks do not establish live corporate integration. |
| E2E-03 User takeover | **PASS, attended native control scope.** PA-115 reports2026-10-22: hotkey clicks/nativeCalls5->5 and Unicode144/2750/nativeCalls9->9; automatic activity clicks4->4 and Unicode80/2750/nativeCalls5->5; modifiers released, Unicode valid, checkpoint/AutoPause confirmed, old/foreign frames denied, fresh captures/clicks with generations1->2 and3->4. Tray pause/capture1->1, Resume stale denial/fresh capture; owner explicitly confirmed free switching/typing in another window. [TakeoverWindow](../tests/PersonalAgent.WindowsSmoke/TakeoverWindow.cs) (`--takeover` / `--activity`), [RegressionWindow](../tests/PersonalAgent.WindowsSmoke/RegressionWindow.cs), [TakeoverStressTests](../tests/PersonalAgent.IntegrationTests/TakeoverStressTests.cs). | **PASS for the local native takeover/resume journey.** Full live-model re-evaluation, mixed-DPI/multiple monitors and deferred power/display environments are separate unaccepted extensions; this row does not close PA-026/030/113. |
| E2E-04 Activity during scheduled GUI | **PASS, attended native scheduler gate.** PA-1152026-10-22 uses actual StartWhenIdle schedule due in15seconds, physical typing244events/64busy samples, one warning, no owner/capture/input, durable explicit Skip. Owner confirmed focus stayed in the input field. [RegressionWindow](../tests/PersonalAgent.WindowsSmoke/RegressionWindow.cs) and [ScheduledDesktopTests](../tests/PersonalAgent.IntegrationTests/ScheduledDesktopTests.cs) cover warnings/idle/skip/defer/dedup/restart. | **PARTIAL for the literal Teams journey.** Real Teams scheduled task/tool composition is not supplied under PA-075. Native keyboard/focus and scheduler safety are accepted; do not claim the corporate check itself ran. |
| E2E-05 GUI after MCP | **PASS, attended native admission scope.** PA-1152026-10-22: one actual loopback MCP read while keyboard busy (98typed/87busy), one warning, zero captures/input/owner before explicit admission; afterward Completed/captures1/MCPcalls1/explicitAdmission/noReadReplay. Owner confirmed focus retained. [RegressionWindow](../tests/PersonalAgent.WindowsSmoke/RegressionWindow.cs), [HybridMcpGuiTests](../tests/PersonalAgent.IntegrationTests/HybridMcpGuiTests.cs) and [RegressionStandTests](../tests/PersonalAgent.IntegrationTests/RegressionStandTests.cs). | **PASS for first-GUI admission/no-read-replay.** Model is scripted and the MCP server/data are local synthetic fixtures; no paid/corporate claim. |
| E2E-06 Telegram follow-up | **PASS, controlled linked-conversation outcome.** [TelegramFollowupTests](../tests/PersonalAgent.IntegrationTests/TelegramFollowupTests.cs) composes actual hosted polling/Core/MAF/SQLite/loopback MCP/outbox with scripted Telegram/model, explicit test-only exact-peer read permission, clarification/fresh-read/denial/no-old-write-replay. [ConversationRegressionTests](../tests/PersonalAgent.IntegrationTests/ConversationRegressionTests.cs) adds restart/provider change/duplicate reply protection. | **BLOCKED — PA-107 and PA-075.** Production Telegram defaults to local/no tools. Real bot/provider, approved source and explicit current peer/tool/data scope plus an owner-observed follow-up are missing. Conversation history grants no tool authority. |
| E2E-07 Action approval | **PASS, controlled exact-action policy.** [CommonToolPolicyTests](../tests/PersonalAgent.IntegrationTests/CommonToolPolicyTests.cs), [HumanApprovalTests](../tests/PersonalAgent.IntegrationTests/HumanApprovalTests.cs), [ToolDispatchTests](../tests/PersonalAgent.IntegrationTests/ToolDispatchTests.cs) and [ChannelsAcceptanceTests](../tests/PersonalAgent.IntegrationTests/ChannelsAcceptanceTests.cs) prove zero effect before exact owner consent, reject/expire/cancel/changed recipient/foreign owner, one dispatch and unknown-outcome no-replay for real loopback MCP and controller-backed synthetic GUI. | **PARTIAL for real Teams send.** No production semantic Teams-send binding or authorized live recipient/organizational write scope has been accepted. Nothing was sent externally. A generic coordinate click is not a substitute for reviewed semantic recipient/payload binding. |
| E2E-08 Recovery/unknown outcome | **PASS, actual killed-process sandbox outcome.** [ToolDispatchRecoveryTests](../tests/PersonalAgent.IntegrationTests/ToolDispatchRecoveryTests.cs) reserves approved dispatch, writes one real sandbox-file effect, kills the child before ack, starts the actual host twice and checks Interrupted/ToolOutcomeUnknown, OutcomeUnknown ledger, review advice in actual unshown Dashboard, rejected old call/approval, zero recovered runtime calls and exactly one file effect. | **PASS for the specified crash/unknown-write contract**, using a real local effect instead of corporate data. No live external-send or universal exactly-once-delivery claim. |
| E2E-09 Second channel | **PASS, required fake Discord DI journey.** [ChannelsAcceptanceTests](../tests/PersonalAgent.IntegrationTests/ChannelsAcceptanceTests.cs) registers a test-only inbound/outbound adapter; actual common task/config/outbox/SQLite routes duplicate input to one run and sends one notification per route through scripted Telegram outage/restart. Same owner pause/resume/cancel and capability fallback work. PA-122 changes no Core/Computer/Agent production code. [ChannelContractTests](../tests/PersonalAgent.IntegrationTests/ChannelContractTests.cs) covers shared cancellation/errors/boundaries. | **PASS for the explicitly fake second-channel acceptance.** This does not claim a real Discord transport. |
| E2E-10 Provider switch | **PASS, controlled SDK/neutral-context outcome.** [ProvidersContractTests](../tests/PersonalAgent.IntegrationTests/ProvidersContractTests.cs) shares image/tool/fresh-observation/cancel/limit assertions across actual OpenAI/Anthropic SDKs with scripted HTTP. [ModelProfilesTests](../tests/PersonalAgent.IntegrationTests/ModelProfilesTests.cs) checks actual UI next-run selection/immutable profiles/current policy and bounded neutral handoff without opaque state, also to arbitrary DI IDs; conversation/session/handoff/restart suites protect no-replay. | **BLOCKED — PA-065.** Live OpenAI read-only evidence exists, but a live Claude profile/API and attended cross-provider report do not. Scripted SDK parity cannot close that gate. |

Exact historical reports and observations are preserved append-only in
[progress/LOG.md](../progress/LOG.md): PA-0592026-10-19T15:08; PA-0812026-10-19T16:10;
PA-0832026-10-19T16:22; PA-1022026-10-20T18:35; PA-1152026-10-22T12:00/12:03/
12:22/12:26/12:31. Current PA-122 verification is recorded there separately.
Attended procedures remain in [tests/README.md](../tests/README.md); no screenshots,
message bodies, provider keys or protected stores are attached to this audit.

**PA-123 remains blocked:** this table documents all ten scenarios but does not
supply passed evidence for their missing full/live outcomes. PA-124 additionally
depends on PA-065 and PA-075; no bypass is inferred from PA-121's named deferral.
Any owner decision to defer those dependencies must preserve all release gates.

### Completing the six open outcomes

Do not start these checks automatically or reuse synthetic transport credentials.
The ordinary desktop task now binds an explicit one-task selection from the
shipped **Tools… (off by default)** window through ProfileAgentRuntime and the
existing registry/approval/dispatch/desktop gates. ProductionTaskToolsTests uses
that actual composition, scripted models, loopback MCP and controlled native
boundaries. Selection/reset, saved current MCP grants, connection/review changes,
exact mutation targets, approvals, fresh-screen revalidation, denial and restart
are checked. No provider or native desktop acceptance is inferred from these
tests. Telegram follow-ups retain local/no-tool defaults; channels and schedules
do not inherit desktop grants. Their reviewed peer/task scopes remain a real
composition gap. Generic coordinate tools do not establish semantic Teams
recipient/message approval binding.

1. **E2E-01:** use an authorized harmless local test application with a changing
   tab title. Under an explicitly selected live vision/tool profile and reviewed
   input scope from **Tools…**, actually open it through GUI tools, read the title, observe steps,
   and reopen SQLite history to confirm the same result. Keep Take Control ready;
   restrict input/capture to the test surface and do not count an already-open
   screenshot-only fixture as this outcome.
2. **E2E-02:** configure an approved read-only Jira/ADO MCP endpoint and permitted
   ticket plus Teams test context. The organization must allow both access and
   sending that scoped data to the selected model. Verify the production tool
   binding through **Tools…** (only already connected, saved/reviewed current
   tools are offered), GUI admission and real typed source link in the durable result.
3. **E2E-04:** after the corporate read-only task is authorized/bound, schedule it
   with StartWhenIdle due in15seconds. Type synthetic text in a harmless window;
   observe retained focus, one warning and deferral/skip without capture/input.
   PA-115 already established the native gate; this closes the actual task binding.
4. **E2E-06:** configure a real bot through protected local fields and approve its
   exact current owner/peer. Bind only the permitted current read tools, receive
   a real result, reply for details and verify a new run in the same conversation,
   current-source/clarification behavior and no repeated previous write.
5. **E2E-07:** establish a semantic Teams test-recipient/message binding and
   organizational permission before any live send scenario. Verify no send while
   approval is absent/foreign/expired or payload changes; any optional approved
   effect needs its own exact local owner decision. This audit authorizes no send.
6. **E2E-10:** configure/test a real Claude vision/tools profile locally, then
   select it for the next bounded run after OpenAI. Verify fresh client/current
   policy, unchanged tool scope and neutral history without foreign opaque state.
   Prior read-only OpenAI acceptance does not authorize new paid Claude requests.

Record only date/build, environment, run IDs, statuses, counts/typed source
metadata and explicit owner observations. Keep credentials in protected fields;
do not append screenshots, corporate text, provider bodies or auth material.
Reopen the relevant tracker task with this evidence; external acceptance is never
inferred from a dependency deferral. PA-026/030/113/119/121 still require their
separate hardware/power/profile/distribution checks even after these six close.

### Original §22 MVP checklist cross-check

The authoritative wording is [Development Plan §22](DEVELOPMENT_PLAN_v1.1.md).
Its current source contains15checkbox clauses (the catalog's reference to22is
not authority to invent7additional clauses). No original clause is checked off
by this audit; PA-125 requires the original outcomes and every required task done.

| Original §22 clause | Existing evidence / remaining limit |
| --- | --- |
| Runnable Windows app/tray/execution UI | Existing native shell acceptance and published bundled-runtime smoke; PA-119/121 clean-profile/no-.NET/sign-in gates remain. |
| Five own Computer Use tools | ComputerToolTests/ComputerInputSenderTests/InputSafetyTests/capture suites pass; PA-026/030 multi-monitor/mixed-DPI hardware gaps remain. |
| One real vision/tool-use flow | PA-059 live read-only capture/title/SQLite accepted; E2E-01 app-opening/input extension remains unverified. |
| OpenAI and Claude profiles | Actual UI and both scripted SDKs pass; live Claude PA-065 blocked. |
| Configured MCP beside GUI | Local stdio/HTTP/config/registry/hybrid pass; explicit desktop selection is wired and locally verified; live authorized corporate PA-075 remains missing. |
| Schedules/dedup/idle/countdown | Scheduler/recurrence/misfire/restart suites and PA-115 native busy gate pass; full corporate scheduled journey not accepted. |
| Take Control via hotkey/tray/UI | PA-115 native evidence plus control/XAML tests; no new automation permission inferred. |
| Desktop released until Resume | Stable native pause counts/released modifiers and owner-confirmed other-window use; deferred PA-113 sleep/display remains. |
| Fresh observation after Resume | Native stale/foreign denial/new generation/fresh capture plus shared SDK/registry tests; live model-driven re-evaluation is a separate gap. |
| Cancel cannot auto-resume | Task/desktop/hybrid/callback/stress/cancel/restart regressions pass; human pause and unknown records retain veto. |
| Telegram commands/summaries/approvals | Scripted actual adapter/polling/outbox/owner/callback suites pass; live bot/corporate follow-up PA-107 remains. |
| SQLite history/crash no dangerous replay | Actual killed child/reopen/dispatch ledger tests, migration/WAL backup and history-preservation tests pass. |
| Privileged actions use policy/approval | CommonToolPolicy/HumanApproval/ToolDispatch/DataBoundary tests pass; real Teams semantic send/organizational authorization not accepted. |
| State/takeover/permission/channel/scheduler tests | Full1136-test post-binding local baseline passed; this does not certify physical/external environments. |
| Setup/credentials/MCP/Telegram/pause instructions | tests/README.md and DISTRIBUTION.md exist; actual fresh Windows-account wizard PA-119 and clean distribution PA-121 remain unaccepted. |

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
