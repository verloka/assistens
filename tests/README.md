# Build and test

Requires Windows and the .NET SDK selected by `global.json` (currently 10.0.401).
No commercial API key, tenant credentials or bot token is needed for the current tests.

From the repository root, run the full verification with one command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
```

The script restores in locked mode, builds, and runs all test projects. A failing
command stops the script with an error. Use `-Configuration Release` for Release.

Equivalent individual commands:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build
```

Run a focused group with `dotnet test --filter DesktopHostTests` or
`dotnet test --filter HealthScreenTests`.

PA-109: `--filter 'InputObservationTests|WindowsSystemIdleTests'` exercises the
explicitly started `WindowsInputObserver` prototype with a fake hook registration
on an actual dedicated Windows dispatcher. It reuses the on-demand session idle
probe, installs at most one keyboard/mouse pair, blocks on messages while idle,
keeps no activity queue, and removes hooks on shutdown/partial setup failure.
The idle test measures the observation thread's CPU over250ms (under100ms CPU),
independent of other test threads. A100000-event burst retains counters only.
No actual global hooks, key contents, pointer positions, captures or injected input
are used in these automated cases. The App does not start this prototype.

The native implementation observes WH_KEYBOARD_LL/WH_MOUSE_LL metadata only:
device, injected/lower-integrity flags, event tick and dwExtraInfo. Callbacks always
continue the hook chain and must remain short; consumer exceptions stop observation
with a fixed diagnostic. Start/stop and native cleanup failures do not imply that
observation remains healthy. Native callback delegates are retained if unhook fails.

Limitations: GetLastInputInfo is session-specific and includes injected events;
its timestamps can wrap or move backwards. A missing injected flag is only a
physical-input candidate, not proof of human origin. Other accessibility software,
remote sessions and drivers can inject input; dwExtraInfo can be forged and grants
no authority. Secure desktops/elevation/session boundaries can prevent observation.
Windows can silently remove a slow low-level hook; Running reports installation
and pump lifetime, not proof of continuous event coverage. Actual native hook,
accessibility/RDP/lock behavior and attended auto-pause acceptance remain separate.
Own-injection attribution and active-run policy are PA-110/111.
PA-110 now stamps normal and cleanup SendInput copies after validation with one
random native-width process marker. Only a matching injected event without
lower-integrity flags is ignored; foreign, inconsistent or unknown metadata is a
takeover candidate. Caller-supplied extra metadata stays invalid. This heuristic
cannot authenticate physical origin or prevent same-session marker forgery.
`--filter InputAttributionTests` covers five controlled driver/gate cases; actual
native observer-to-injection roundtrip and App auto-pause remain separate.
API references: [keyboard callback](https://learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc),
[mouse callback](https://learn.microsoft.com/windows/win32/winmsg/lowlevelmouseproc),
[keyboard flags](https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-kbdllhookstruct),
[mouse flags](https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-msllhookstruct).

PA-111: `--filter ActivityAutoPauseTests` covers actual App/Core/SQLite plus a
dedicated dispatcher and controlled input events. Own injection leaves the lease
current. Human/uncertain events close only an owned GUI gate synchronously while
native input and SQLite are held; later batches cannot enter. Existing handoff
confirmation waits for cleanup and durable storage, preserves AutoPause/UserActivity
and never resumes itself. Failed checkpoints/observation cannot claim control is
safely released; monitor unavailability vetoes later acquisition. Headless hosts
do not install hooks. Running still cannot detect Windows silently removing a hook.

Attended PA-111 acceptance passed on2026-10-21 in both modes; reports are in progress/LOG.md. To rerun, close the main App/other smoke windows that
use Ctrl+Shift+F10, then run:

```powershell
dotnet run --project tests/PersonalAgent.WindowsSmoke --no-build -- --activity
```

Opening this fixture creates controls only. Choose `20 clicks`, deliberately press
START, keep hands off during the3second countdown and until at least one own click,
then move the mouse **inside this window** without a hotkey. After PA111 PAUSE OK,
MOVE TARGET, press RESUME and keep hands off during its3second countdown and
while the fresh-context check finishes. The existing pause stays closed during
the countdown; activity after acquisition can still revoke the new lease.
Repeat in `Unicode text` mode, moving the mouse after some text appears. Provide
the PA111 PAUSE OK and PA111 RESUME OK lines for both modes. PAUSE must report stable
nativeCalls, released modifiers, valid Unicode, checkpoint=True, AutoPause=True and
ownEvents>0; RESUME must reject stale context and use a new generation/fresh capture.
No real native success is recorded until the owner observes and returns these lines.
If PAUSE reports FAILED, send the complete line including observation counters and
handoffError, then close the fixture; it does not continue the resume sequence.
Counters contain no keys, text, coordinates or raw process markers. A separate
read-only hook counter can prove event delivery, but cannot prove own-injection
classification or durable pause/resume acceptance.

PA-113: `--filter DesktopEnvironmentPauseTests` checks session, suspend/resume and
display changes using controlled notifications with actual App/Core/SQLite and
in-memory native geometry. An owned lease closes before atomic drain; duplicate
notifications do not invalidate the pending checkpoint. Recovery stays paused;
explicit Resume requires a new lease and fresh evaluated capture. MCP-only/no-owner
work stays independent. Headless hosts never subscribe. Real WPF startup explicitly
subscribes to SystemEvents on a worker (no captured UI context) and unsubscribes on
shutdown; setup failure blocks GUI. Subscription success cannot establish continuous
OS notification coverage. No forced lock, sleep, resolution or focus change is automated.

Attended Windows Lock/unlock passed. The owner explicitly deferred Sleep/wake and Display change for now; both remain NOT TESTED. Do not request those checks again until the owner resumes. The instructions below are retained for that later verification:

```powershell
dotnet run --project tests/PersonalAgent.WindowsSmoke --no-build -- --environment
```

Opening creates controls only. Select `Lock/unlock`, press ARM, lock Windows yourself
(Win+L), unlock normally, then CHECK. Copy `PA113 ENVIRONMENT OK` or the full failure.
After OK choose `Sleep/wake`, ARM, put Windows to sleep yourself and wake it, then
CHECK. Finally choose `Display change`, ARM, manually change/restore an available
display resolution, then CHECK. Send all three reports. Each ARM creates only a
synthetic lease and temporary SQLite metadata; there is no native input/capture,
clipboard, model call, external service or automatic system-setting change.
CHECK requires observed events, revoked lease, durable environment checkpoint and
no automatic resume. ARM of a new cycle is deliberate; no screenshot is claimed.
Real screenshot/input restoration and multi-monitor coverage remain separate gates.

PA-114 repeated stress:

```powershell
dotnet test --filter Takeover -v minimal
dotnet test --no-build --no-restore --filter Takeover -v minimal
dotnet test --no-build --no-restore --filter Takeover -v minimal
```

TakeoverStressTests adds22 cases, including216 cycles per execution across
click/type/chord, success/partial insertion/unknown native exception and pause/cancel.
The actual drivers, App handoff, Core gate and SQLite must release held inputs,
reject12 queued stale writes per cycle and preserve a waiting run's exclusive fresh
lease. Separate barriers reproduce100 repeated takeovers behind a held writer and
a new generation's takeover before the previous worker publishes completion.
Old consent cannot pause a newer generation; obsolete completion cannot replace
the latest handoff. All input/capture boundaries are controlled; no real desktop
input, screenshots, OS-cycle changes, provider or credentials are involved.
PA-113 Sleep/wake and Display change stay explicitly deferred until final review.

PA-066: `--filter McpConnectionConfig` verifies generic configured connections,
owner-approval/default denial, immutable settings/allowlists, scoped secret
references, connection cancellation/timeouts and sanitized failed-handshake
cleanup. Two IDs use the same factory and actual loopback SDK server through a
test-only transport provider. Production stdio/HTTP providers follow PA-067/068;
the App registers the factory without opening a connection or exposing tools.

PA-067: `--filter McpStdio` runs the actual local MCP child through direct argv
and the pinned SDK stream transport. Tests cover literal shell characters,
explicit executable/working-directory approval, scoped credentials without
ambient-secret inheritance or raw stderr logging, real tool list/call, process
exit, and cancellation/timeout cleanup. The no-protocol child is a test-only
fixture mode. No shell wrapper, model or corporate service is used.

PA-068: `--filter McpHttp` verifies actual loopback Streamable HTTP list/call,
scoped bearer auth, explicit reconnect without tool replay, request cancellation
and timeout, sanitized HTTP status errors and redirect/default-HTTP refusal.
Owned HTTP clients have redirects/cookies/ambient credentials disabled. Use the
connection's bounded ListToolsAsync/CallToolAsync methods for application calls.
SDK2.2 defaults to a stateless server/discover revision; fresh connections may
therefore have no session ID. All credentials and servers in these tests are local
synthetic fixtures; no company/API access is required.

PA-073: run the deterministic MCP suite with:

```powershell
dotnet test --no-restore --filter Mcp
```

Existing Config/Stdio/Http/Discovery/Execution/Lifecycle/ConnectionsUi tests cover
approval/authentication errors, cancellation/timeouts, actual child cleanup,
bounded reconnect, discovery changes and new-tool denial, plus actual XAML and
protected configuration. McpMalformedResponseTests adds actual loopback calls
whose replies are corrupted after the server responds: broken JSON, unknown
content type and invalid image base64. The pinned SDK/common registry/MAF stop
without another model call or replay of the unconfirmed operation; raw bodies
do not enter the public result. No Jira/ADO account, paid model, physical desktop
input or tenant permission is needed. This is a local contract suite, not live
corporate or complete hybrid GUI acceptance.

- `PersonalAgent.UnitTests`: module boundary and reference cycle checks.
- `PersonalAgent.IntegrationTests`: actual Generic Host and SDK loading; host
  probes/fakes (`ProbeService`, `CancellableWorker`, recording logger) cover
  cancellation and failure cleanup. WPF tests load the real XAML on STA threads
  and pump the dispatcher without displaying windows or generating input.
- `PersonalAgent.E2ETests`: deterministic application fake-flow (PA-016), covering
  submit/progress/pause and complete/cancel/failure with one consistent history.
  No live model, desktop, SQLite or notification delivery is tested here.

Add scenario links to existing FDD Drafts only when corresponding tests exist;
never promote them to Active without explicit approval. Keep live/manual provider,
device and corporate checks separate from fake/local evidence. The ten MVP E2E cases
and MVP release gate remain unverified until their own backlog tasks are completed.

## MAF runtime adapter (PA-049)

PA-064: `dotnet test --filter Providers --no-build` runs the shared provider
contract matrix (ten new parameterized cases plus matching existing provider/UI
tests). OpenAI and Anthropic use identical assertions for the screenshot/click/
fresh-PNG/result sequence, correlated events/source metadata/usage, cancellation,
step/token limits and malformed arguments without retry. Only the synthetic HTTP
wire codec differs; both actual SDKs, MAF and real controller/fake native I/O are
used. No live account, desktop input or model-semantic acceptance is claimed.

PA-063: ModelProfilesTests also covers the actual Continue selected conversation
checkbox across an OpenAI-to-Claude scripted-provider switch. A new run/client/MAF
session receives only bounded neutral User/Assistant messages and the last
completed summary, as untrusted user-role data; opaque AgentSessions and tool
bodies are excluded. An explicit new task gets no earlier context. Context is
snapshotted into the durable request, limited to 4000 summary characters plus
eight messages of at most 1000 characters, preserving surrogate pairs.

PA-062: `--filter ModelProfiles` covers actual profile XAML, create/update/select,
explicit Test saved profile, DPAPI credentials and SQLite migration/reopen using
scripted providers. Running and queued runs retain submitted profile snapshots;
changing a provider requires its own key. Profiles store secret references only;
passwords clear after save and do not appear in history/database/files. Startup
selects Local demo, even with saved profiles. Cloud text tasks explicitly selected
in the UI now use the matching provider; desktop tools remain unconfigured and
denied in this path. Full live read-only GUI acceptance remains PA-059/065.

PA-061: `--filter AnthropicCapability` runs eleven actual SDK cases for capability
preflight, tool_use/result correlation, base64 PNGs and fresh post-click geometry,
cache-inclusive usage, incomplete/refused stop reasons, and synthetic capability
probes (including failed vision). WindowsComputerController uses fake native I/O;
no screen or live model is accessed. Existing shared runtime contracts already
support these mappings, so this slice adds evidence without production changes.

PA-060: `--filter AnthropicModelProvider` exercises official `Anthropic` 12.58.0
with the existing pinned MAF/MEAI packages. Eighteen local HTTP/SDK cases cover
fixed endpoint and protected credential, alias resolution, profile validation,
auth/rate/server errors without retry, cancellation, owned-client cleanup, and
streaming through the real host/runner/SQLite with reported usage. The injected
test transport remains caller-owned. No live Claude request or GUI is performed.
SDK reference: https://github.com/anthropics/anthropic-sdk-csharp

PA-058: `--filter AgentContract` runs seven deterministic contracts through the
actual MAF runtime, registry, Task Runner and SQLite: correlated sequential calls,
three malformed second-call variants, cancellation of a timed-out in-flight tool,
and pause followed by explicit resume or cancellation. Completed earlier reads
are not repeated; invalid/paused/cancelled work cannot produce a final success.
Existing ComputerToolTests covers the five GUI-tool sequence with fake native I/O.
No network, paid model or real desktop action is used; full business scenarios
including delivery and live-model takeover remain untested.

PA-057: `--filter "StructuredResult|Dashboard|ToolRegistry"` verifies typed
source/warning aggregation, reported usage even after budget failure, optional
configured cost estimates, invalid-source refusal and retained action evidence
when observation fails. DashboardTests reopens actual SQLite through the real host
and XAML for completed/failed results and unknown usage. Prices in tests are
synthetic; no provider price/billing claim. Model prose does not add source links.

PA-056: `--filter AgentLimits` checks cumulative reported-token and output caps,
step/repetition refusal before another GUI effect, profile generation settings,
missing/invalid usage, duration/cancellation at the classified boundary and legacy
limit deserialization. Repetition signatures ignore changing call/observation IDs.
ScriptedChatClient explicitly supplies synthetic 1+1 token usage by default; a
test supplies an empty UsageDetails for unknown accounting. This is not measured
live usage or a prepaid billing cap. Provider retries remain disabled.

PA-055: `--filter AgentPromptPolicy` checks bundled versioned policy resources,
untrusted task/summary/tool-role boundaries and actual SDK HTTP privileged roles.
A synthetic Teams payload/description asks to change policy and forge write
approval; a subsequent scripted write request remains denied by the allowlist.
These tests prove application boundaries, not live-model injection immunity or
semantic tool selection. Prompt files are embedded, not loaded from tool data.

PA-054: `--filter "ToolImageProtocol|ComputerTool|ToolRegistry"` verifies actual
SDK HTTP image_url content (including decodable PNG and changed synthetic geometry
after an action), fresh observation IDs, permission checks on automatic post-action
capture, no capture after a pre-effect denial/MCP read, and GUI batch rejection.
The pinned bridge does not expose images nested in FunctionResultContent as vision
input; ToolResultMessages sends correlated text results followed by explicitly
untrusted user-role binary observations. The network is scripted and native calls
are fake; no live model/desktop E2E is claimed.

PA-053: `--filter ComputerTool` exercises all five strict computer tool schemas
through real MAF streaming/manual registry dispatch with both a fake controller
and the real WindowsComputerController over fake native interfaces. It checks
encoded-image coordinate mapping, opaque observation IDs, trusted reevaluation,
stale resume refusal, profile capability inference, default deny and cancellation
before a following model/tool request. No actual desktop input/capture occurs.
ComputerToolSession is an explicit disposable run scope; App still defaults fake.

PA-050 adds `dotnet test tests/PersonalAgent.IntegrationTests --filter OpenAiModelProvider --no-restore`.
It uses the official OpenAI 2.13.0 SDK through Microsoft.Extensions.AI.OpenAI
10.10.0, an in-process HTTP handler, real MAF/host/SQLite and actual Windows DPAPI
with synthetic keys. It verifies model/auth test, cancellation, no automatic retry,
sanitized HTTP errors and absence of the synthetic key in SDK events, result/history
and plaintext secret files. No live account check is claimed.
The provider uses the stable chat bridge; see [official SDK](https://developers.openai.com/api/docs/libraries)
and [SDK configuration](https://github.com/openai/openai-dotnet#advanced-usage).

PA-051: `--filter ModelCapability` checks profile-bound runtime preflight and
Test Connection capability probes with scripted HTTP. Function calling is required
for agent use; desktop work additionally requires images, while tools-only work
does not. Explicit Test Connection probes enabled capabilities using a declaration
and synthetic in-memory PNG, without executing tools/capturing the desktop. Live
model capabilities remain unverified; configured flags never come from model names.

`dotnet test tests/PersonalAgent.IntegrationTests --filter MafAgentRuntime --no-restore`
checks the real MAF streaming/session pipeline with a local scripted IChatClient,
including actual AppHost/SQLite DI, cancellation, pause, duration, incomplete
responses, sanitized failures, session isolation and assistant-only output.
No network, paid credentials, native input or tool execution is involved.
The default App runtime remains fake; provider configuration follows in PA-050.

## MAF tool and approval diagnostic (PA-006)

PA-052: `--filter ToolRegistry` tests the neutral IAgentTool to MAF AIFunction
adapter with real SDK dispatch and an in-process fake tool. Coverage includes
explicit allowlists, JSON Schema 2020-12 argument validation, source identity,
text/data/image mapping, default-denied risks/sources, policy after pause, call-ID
replay refusal, cancellation and sequential execution. Schemas use
[JsonSchema.Net](https://docs.json-everything.net/schema/basics/) 9.4.1 with external
references/fetch disabled. Production runtime tools are not enabled by this slice.

```powershell
dotnet test --filter MafToolApprovalSpikeTests
```

The real MAF 1.25.0 pipeline uses `ScriptedChatClient` as its `IChatClient`.
The deterministic fake returns function-call content and a final response; the
SDK performs the actual tool dispatch and approval handshake. No network or key
is used, and the only tool echoes data in memory.

Observed API sequence:

1. Create `ChatClientAgent` with a local `AIFunctionFactory.Create` tool.
2. Wrap protected tools in `ApprovalRequiredAIFunction`.
3. Create an `AgentSession` with `CreateSessionAsync` and call `RunAsync`.
4. Read `ToolApprovalRequestContent` from the response messages; the protected
   function has not run yet.
5. Pass `request.CreateResponse(true/false)` in a user `ChatMessage` to `RunAsync`
   with the same session. Approval invokes the original function once; denial
   continues the conversation without invoking it.

Tests also verify that the default MAF approval-response binding cannot substitute
the original arguments, and that an already-cancelled run invokes neither model
nor tool. Do not disable that binding or use the wrapper alone as a security policy:
`ApprovalRequiredAIFunction` is a marker, and its invoker must enforce approval.

Limits: this fake is non-streaming. Live OpenAI/Anthropic, vision, durable approvals,
identity/expiry checks, restart/session persistence, replay protection and unknown
external outcomes are not covered. The SDK diagnostic is not the application's
complete approval or task flow; those FDD scenarios remain untested.

## MCP transport diagnostic (PA-007)

```powershell
dotnet test tests/PersonalAgent.IntegrationTests --filter McpTransportSpikeTests
```

`PersonalAgent.McpTestServer` is a test-only executable exposing an in-memory
`echo` tool. With MCP SDK 2.2.0 the tests initialize a real client, list the tool,
check its input schema and invoke it over both actual transports:

- `StdioClientTransport` launches the built fixture via `dotnet`. Server logs and
  its diagnostic process ID go to stderr; stdout carries MCP only. Client disposal
  is followed by an assertion that the child exits.
- `HttpClientTransport` connects to an in-process Kestrel server bound to
  `127.0.0.1` on an ephemeral port at `/mcp`. `StreamableHttp` is selected explicitly,
  preventing automatic legacy SSE fallback. The server is stopped/disposed afterward.

The HTTP fixture uses `ModelContextProtocol.AspNetCore` 2.2.0; production modules
remain unchanged. This verifies local transport/discovery/call compatibility, not
remote OAuth, TLS, reconnects, tool policy, run integration or corporate access.
FDD-004 S1-S3 remain untested as complete business outcomes. SDK transport reference:
[official documentation](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html).

## Attended Windows diagnostic (PA-008)

Build-only verification is automated. Run this fixture yourself in a dedicated,
unlocked interactive Windows session; do not run it as a background test:

```powershell
dotnet run --project tests/PersonalAgent.WindowsSmoke
```

1. Activate its window yourself. Keep it unobscured and fully within the desktop.
2. Release all keys, then click its capture/input button with the mouse.
3. Check that the preview depicts this window and the report shows capture OK,
   physical pixel dimensions, DPI, monitor count and virtual origin. No file is saved.
4. Check that exactly one `x` appears in the test field and the report says the
   SendInput result was observed. It never clicks or types in another application.
5. Press Ctrl+Shift+F10 yourself; check for WM_HOTKEY observed OK. Close the window.
   Relaunch if needed to check that the shortcut was released on shutdown.
6. Record actual results and environment (Windows, normal/elevated session, DPI,
   monitor count), including failures. Do not mark PA-008 done from build evidence.

Limitations: foreground/focus checks cannot atomically reserve global Windows input;
use only as an attended one-character diagnostic, without switching apps during
the operation. There is no agent lease/pause implementation here. A key-down/up
pair is submitted together; a partial insertion completes only its key-up and never
retries the character. No modifiers are injected or physical user keys released.
The hotkey registers only after the user presses the button; conflicts are reported.
SendInput is subject to UIPI and does not identify UIPI failures reliably. UAC,
lock, remote/disconnected sessions and elevation mismatch are unsupported, with
no bypass. PerMonitorV2 is requested before window creation, but mixed DPI,
negative origins, multi-monitor layouts and RDP still require hardware checks.
This cropped GDI capture does not validate full-desktop capture, protected windows
or production coordinate mapping. Screenshots and field content are never persisted.

API references: [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput),
[RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey),
[BitBlt](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-bitblt).

## Visual capture smoke matrix (PA-030)

Reuse the attended PRIMARY/VIRTUAL/SECONDARY fixture and checklist above; do not
create a second capture implementation. Capture occurs only on the user's button
press. Before each check keep the blue bar visible and avoid private content.

| Setup/check | Actual result |
| --- | --- |
| One display, 125% scaling, PRIMARY preview | User reported PRIMARY OK on 2026-10-18: PNG/source 1920x1080 at (0,0), DPI120, generation1, 365729 bytes; diagnostic exit0. Recorded under PA-025 in progress/LOG.md. |
| Two displays, negative origin and different DPI | NOT TESTED: user has no second monitor (PA-026). |
| Move window between displays; PRIMARY stays primary, SECONDARY blue bar follows window | NOT TESTED: same hardware blocker. |
| Repeat at 100%/150% scaling; geometry remains physical and preview matches | NOT TESTED: no attended result for changed scaling. |

When hardware is available, run the PA-026 steps for each layout, move the window
between both displays and repeat PRIMARY/VIRTUAL/SECONDARY. At each scale compare
PNG dimensions with SourceBounds and SYSTEM virtual bounds, monitor DPI with
Windows settings, and the visible bar's position. Record actual report lines,
preview match/mismatch, layout/scale and diagnostic exit. Fix and rerun any actual
failure before acceptance. Do not change display settings or capture automatically.
The local fake geometry/codec suites do not fill these manual result cells.

## Atomic SendInput boundary (PA-031)

`dotnet test tests/PersonalAgent.IntegrationTests --filter ComputerInputSender`
uses a fake native boundary: no keystroke/click reaches Windows. It checks INPUT
ABI size/offset, default denial, stale/revoked authorization, locked desktop,
pre-cancellation, malformed/unbalanced batches, cancellation during atomic send,
global serialization across sender instances, partial/unknown outcomes and
key/button release failures. Input arrays are copied before waiting, batches are
limited to 64 events, and the sender faults after any incomplete/unknown send.
No press is retried; a cleanup attempt contains only release events. If the
desktop becomes unavailable, cleanup is reported unsuccessful without input.

The attended PA-008 fixture now reuses this internal sender; its old observed
result predates this change. Build/fake-native tests do not prove the revised
real input path, UIPI/elevation mismatch or live lock/takeover races. Those need
an attended test in the fixture. The driver is not registered as an agent tool
and does not implement the later production DesktopLease or PauseGate.
The native API does not reliably identify UIPI as the reason for zero insertion;
NativeError is diagnostic only, and successful insertion is not UI outcome proof.
Reference: [Microsoft SendInput contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).

## Attended click acceptance (PA-032)

`dotnet test tests/PersonalAgent.IntegrationTests --filter ComputerClick` covers
23 fake-native cases: left/right/middle batches, exact physical pixel mapping
including negative origins/mixed DPI and wide desktop boundaries, off-frame/gap
rejection before native input, stale generation/topology, denied/locked session
and release-only cleanup after partial clicks. No real pointer is moved.

For actual acceptance run `dotnet run --project tests/PersonalAgent.WindowsSmoke`
in an unlocked interactive session. Activate its window and keep TEST TARGET
visible. Release keys/buttons. Select Left, left-click **CLICK**, then
do not move the pointer or switch applications until its
report appears. Repeat for Right and Middle. The gesture captures a fresh
in-memory virtual frame, checks foreground/target location and released user
keys, then moves/clicks once inside this fixture. The report must show exactly
`CLICK Left OK`, `CLICK Right OK`, `CLICK Middle OK` with `observed=2` for each.
Paste those report lines and close the window. If BLOCKED/FAILED occurs, record
it without retrying automatically. No files/uploads, external-app clicks or
automatic diagnostic launches are performed. The pointer is not moved back.

Actual PA-032 click results: user returned **Left/Right/Middle OK** on 2026-10-18,
`observed=2` each at physical (479,474), generations 1-3; additional Left/Middle
checks also OK, and diagnostic exited 0. Launch CLICK with the left mouse button;
the selection chooses the button injected into TEST TARGET. The target records
matching down/up events without opening a menu or performing a middle-click action.
Native checks cannot atomically reserve the foreground desktop; use only this dedicated window and
keep the session stable during the check. Production lease/pause/approval policy
and full business scenarios remain later tasks.
Reference: [Microsoft MOUSEINPUT contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput).

## Scroll acceptance (PA-033)

`WindowsMouseInput.ScrollAsync` shares the click anchor/generation/topology and
authorization boundary. Amount is a positive count of 1-10 wheel ticks, 120 delta
units each; direction controls the sign/vertical or horizontal wheel flag. Each
call sends one absolute move and one bounded wheel event. Partial/unknown native
results fault the sender without replaying movement or wheel input.

`dotnet test tests/PersonalAgent.IntegrationTests --filter ComputerScroll` runs
19 fake-native cases for four directions, min/max and invalid amounts, anchor,
invalid direction, stale/denied/locked context, revocation, pre-cancellation and
partial insertion with no retry. Click and primitive suites remain regressions.

Actual Windows scrolling: user returned **Down/Up/Left/Right OK** on 2026-10-18,
one actual matching native event each at physical (718,629). Down/Up moved
vertical offset 400 to 464/336; Left/Right moved horizontal offset 400 to 336/464.
Received deltas matched -240/+240, and the diagnostic exited 0. This verifies the
dedicated fixture on that setup. To repeat in WindowsSmoke activate the
window, select each of Down/Up/Left/Right in the list above **SCROLL**, and
left-click **SCROLL** once per direction. Keep the pointer/session still until
the report. The dedicated area resets to offset (400,400), takes a fresh
in-memory frame, then receives two wheel ticks. Its native window hook records
actual WM_MOUSEWHEEL/WM_MOUSEHWHEEL, verifies the screen anchor and scrolls only
this test area. No click/focus change in another application or clipboard is used.
The result requires the exact message/delta and actual offset movement in the
selected direction; check the visible content and return all four `SCROLL ...`
lines (expected `OK`, `observed=1`, delta -240 Down/Left or +240 Up/Right), then
close the diagnostic. A BLOCKED/FAILED line is evidence to investigate, not pass.
The pointer is not restored automatically. All production takeover/lease and
live lock/elevation checks remain separate, untested acceptance.

References: [MOUSEINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput),
[WM_MOUSEWHEEL](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mousewheel),
[WM_MOUSEHWHEEL](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mousehwheel).

## Unicode and batched typing (PA-034)

`WindowsKeyboardInput.TypeAsync` accepts at most 4096 UTF-16 units, validates the
entire text before effects, rejects malformed surrogates and unsupported controls,
normalizes CRLF/CR to newline, and sends isolated balanced Enter/Tab pairs.
The trusted target policy must permit these edit keys only in the intended
multiline/tab-accepting field and reject a focus change before subsequent text.
Other text uses Unicode
packets, never clipboard writes. Atomic chunks contain at most 16 code units
(32 native events), keeping surrogate pairs together. A shared input lock covers
all chunks so another driver cannot insert a click/key halfway through text.
Each chunk rechecks trusted authorization, generation, session and cancellation.
Already typed text remains after cancellation; there is no rollback or replay.

`dotnet test tests/PersonalAgent.IntegrationTests --filter ComputerType` runs
25 fake-native cases: text/Unicode, isolated Enter/Tab pairs, line endings, surrogate edges,
limits/empty input, malformed text, default denial, stopping after a full first
chunk, revocation/lock/stale generation, partial cleanup/fault and no interleaving.
These tests do not type into Windows or read/change clipboard contents.
`ComputerTextBoxInputTests` also runs a real, unshown WPF TextBox: ordinary
text composition removes a tab even with AcceptsTab enabled. This caught the
wrong assumption behind the first attended TYPE TEXT failure. Four new control
batch/policy cases failed against the old driver and pass with isolated key pairs.

Attended result before the tab fix: user repeatedly reported TYPE TEXT FAILED,
41/42 characters, while TYPE CANCEL passed (15 characters, one call). The missing
tab is reproduced by the WPF input-pipeline test above. Actual TYPE TEXT with the
corrected driver was subsequently verified by the user: **TYPE TEXT OK**, 42/42
characters, six calls, canceled=False; **TYPE CANCEL OK**, 15/15, one call,
canceled=True. Diagnostic exited 0. The original failure above remains recorded.
To repeat, activate WindowsSmoke and release all keys.
Left-click **TYPE TEXT**. The fixture focuses only its own multiline field and
types a synthetic sample with Cyrillic, CJK, emoji, punctuation, newline and tab.
Do not touch keyboard/mouse or switch applications until the report. Check the
field displays the sample and the report says `TYPE TEXT OK`, `canceled=False`,
with actualChars equal to expectedChars. Then left-click **TYPE CANCEL**: a
fixture-only native decorator cancels after the first successful chunk, and the
real driver must stop subsequent chunks. Expected `TYPE CANCEL OK`, canceled=True,
calls=1, actualChars=15, expectedChars=15, with only fifteen x characters visible
(no half emoji/tail). Return both report lines and close the window. No clipboard,
files/uploads, background typing or app focus switching is used. This verifies
local text/cancellation only, not global hotkey/lease/takeover races or full E2E.

Reference: [Microsoft KEYBDINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput).

## Key chords and modifier lifetime (PA-035)

Internal `WindowsKeyboardInput.PressKeyAsync` parses Ctrl/Alt/Shift plus a known
letter/digit, F1-F12, or named edit/navigation key. Modifiers are distinct and
canonicalized Ctrl+Alt+Shift; navigation keys use the native extended flag.
An explicit trusted canonical-chord allowlist is mandatory and defaults denied,
in addition to desktop authorization/generation. Unknown names/Win chords are
rejected. Existing user-held modifiers/main key block insertion without release.
One balanced atomic batch releases main key then modifiers in reverse order;
partial/unknown insertion uses existing release-only cleanup/fault handling.

`dotnet test tests/PersonalAgent.IntegrationTests --filter ComputerPressKey`
checks 29 fake-native cases, including every partial prefix of Ctrl+Alt+Shift+A,
cancel during complete/partial send, native exception, default/explicit policy,
extended keys, parse errors, existing user keys, pre-cancel and stale generation.
No global chord is injected by this suite. Actual native key effects and live
lock/UIPI/takeover remain for the attended input harness and later lease tasks.

## Atomic input safety audit (PA-036)

`dotnet test tests/PersonalAgent.IntegrationTests --filter InputSafety` runs ten
stateful fake-native cases covering mouse, text and chords, partial versus unknown
outcomes, release-only cleanup, cross-method fault refusal and already-queued work
after a failing send. The same driver instance cannot proceed after native fault.
When cleanup itself is rejected or desktop locks, failure remains explicit and
later input still refuses; the test does not falsely assert all physical keys
were released. Existing cleanup is reused without a second input implementation.
These cases do not establish cross-run lease policy or real global takeover.

## Repeatable four-operation harness (PA-037)

Reuse `dotnet run --project tests/PersonalAgent.WindowsSmoke` in an attended,
unlocked desktop. Every action starts only from a left-click on its launch button;
the diagnostic never launches another application or clicks an external target.
Release keys/buttons and keep the session still during each action:

1. Select Left and launch CLICK: expect matching target down/up and `CLICK Left OK`.
2. Select Down and launch SCROLL: expect the dedicated area's offset increase and `SCROLL Down OK`.
3. Launch TYPE TEXT: verify synthetic Unicode/multiline/tab content and `TYPE TEXT OK` (42/42).
4. Launch KEY Ctrl+A: expect this window's read-only test text selected, `KEY Ctrl+A OK`,
   selectedChars equal totalChars, modifiersReleased=True. Only this canonical chord
   is permitted; it neither copies nor sends text outside this window.

All four operations have actual attended observations in the log. On 2026-10-18
the user returned `KEY Ctrl+A OK; selectedChars=26; totalChars=26;
modifiersReleased=True`; the diagnostic subsequently exited 0.
FAKE/native-boundary safety suites cover
faults/cancellation; no deliberate native modifier failure is injected on the
user's desktop. Global lease/hotkey/resume acceptance remains a separate gate.

## Input regression gate (PA-038)

Existing ComputerClick/Scroll/Type/PressKey/InputSender/TextBoxInput suites cover
bounds, special keys, partial/unknown outcomes, modifier cleanup and cancellation.
ComputerInputSenderTests includes zero insertion with native error 5 and zero
without a useful native error; these are synthetic failure cases, not a live UIPI
test. InputSafety and DesktopAccessDriver additionally cover queued failures and
lease revocation. On 2026-10-18, `dotnet test --filter Computer --no-restore`
passed 4 unit + 116 integration tests; `dotnet test
tests/PersonalAgent.IntegrationTests --no-build --filter "InputSafety|DesktopAccessDriver"`
passed 19. Full `scripts/verify.ps1` passed 76 unit + 254 integration + 3 local
fake-flow tests, build 0 warnings/errors and locked restore. Existing tests were
audited and reused; real elevated-target/locked-desktop behavior remains untested.

## Desktop lease ownership (PA-039)

`dotnet test tests/PersonalAgent.UnitTests --filter DesktopAccess` includes the
Core ownership suite: trusted admission defaults denied, one concurrent owner,
same-owner idempotency, opaque grant identity, stale/foreign release rejection
and monotonically increasing generations over ownership turnover. No screen or
SDK/native API is used. This foundation reports busy rather than queueing and
does not yet implement idle/countdown, input atomic gates or pause/resume policy.
Generations are local to coordinator lifetime; opaque identity prevents foreign
grant reuse, and restart recovery still must discard old GUI work.

## Lease-bound native entry gate (PA-040)

Coordinator RevokeAsync closes access before returning; its completion waits for
already-entered atomic work/cleanup. ReleaseAsync also waits before another owner
can acquire, and cancellation of that wait leaves ownership safely revoked.
Computer's lease-bound capture/input constructors enter this gate immediately
before native effects, after preflight; a revoke in that gap rejects the action.
Each text chunk enters separately so revocation blocks remaining text. Existing
callback-only constructors serve the explicit attended diagnostic, not agent tools.

`dotnet test tests/PersonalAgent.IntegrationTests --filter DesktopAccess` covers
nine fake-native gate cases: all four input paths, preflight/entry races for input
and capture, partial-key cleanup quiescence, inter-chunk revoke and paused frame
discard. Unit DesktopAccess cases cover token/ownership and cancelled release.
No native input is sent. This is not live task pause/hotkey/idle/resume acceptance.

## Pause scopes and separate desktop state (PA-041)

TaskRunCoordinator accepts the shared default-denied desktop coordinator in DI.
GUI-only pause closes that run's resource gate and waits only desktop, write or
desktop-dependent steps. Independent permitted read-only steps may proceed using
trusted RunStepAccess metadata (not arguments granting their own tool permission).
EntireRun waits all boundaries. Resume wakes waiters but discards the old lease;
it refuses while atomic work remains. Cancel wakes with cancellation and closes
ownership; Skip terminates only an unstarted queued occurrence as Skipped, without
pausing unrelated runs. Scheduler-specific occurrence/defer UI remains future work.

PauseScopeTests covers these distinctions. PauseScopePersistenceTests reopens a
real SQLite GUI pause, verifies no new lease/GUI step until explicit resume and
checks the resumed state persisted. Existing command-id/version/persistence/UI
regressions remain applicable. These tests do not invoke real MCP or prove global
hotkey, full safe-pause storage ordering or fresh-screenshot resume orchestration.

## Global Take Control hotkey (PA-042)

Real WPF App startup registers Ctrl+Shift+F10 through a message-only HWND. Gesture
is configurable through TakeControlHotkeyGesture/Start; a settings UI is not added.
Conflicts/errors are reported and GUI remains blocked. Shutdown unregisters and
destroys the HWND. The callback calls Core TakeControl synchronously, globally
blocking both current and future lease entry before asynchronous task persistence.
Even without a current owner, GUI stays closed until an explicit valid resume;
canceling a run does not automatically return control to the agent.

`TakeControlHotkeyTests` covers fake registration/no-repeat, matching/irrelevant
message signatures, conflict errors, idempotent cleanup, callback exception and
unsupported config. `TakeControlPersistenceTests` uses the real app callback and
SQLite with a deliberately held update to show gate closure does not wait for the
pause save. Unit tests cover no-owner takeover, another run's invalid resume and
queued Resume rejection without clearing human control. No global shortcut is
registered by these tests; owned hidden HWNDs are the only native window resource.

The first actual outside-focus report had outsideFocus=True, gateClosed=True and
fakeActions=96->96, but minimized=False. The repeated actual report passed with
outsideFocus=True, minimized=True, gateClosed=True, fakeActions=146->146;
diagnostic exited0. KEY Ctrl+A (PA-037) also passed. To repeat only this gate, left-click
**ARM TAKE CONTROL**. The shortcut is only
registered after this explicit gesture. Minimize the window yourself, activate
another application and press Ctrl+Shift+F10, then restore WindowsSmoke and copy
the `TAKE CONTROL ...` line. Expect OK, outsideFocus=True, minimized=True,
gateClosed=True and unchanged fakeActions count. The loop exercises real lease
atomic entry but never sends desktop clicks/typing/capture. If registration fails,
report BLOCKED without claiming success. Close the window to unregister; no auto
resume or window activation occurs. This proves the shortcut plus local gate on
that setup, not the later real click/type/hotkey/resume E2E-03 acceptance.

References: [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey),
[WM_HOTKEY](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-hotkey).

## Tray and mini-window (PA-043)

Real App startup creates the tray icon; headless AppHost instances do not. Closing
the main window exits by default. Check **Keep running in tray when closing** to
hide it instead; tray **Open** restores it only on user request. **Controls** opens
a non-activating mini-window with Pause & Take Control, Resume and Cancel. Closing
that window hides it. Tray **Exit** stops the host and disposes the tray/hotkey.
Take Control uses the existing immediate local gate and common task pause command;
Resume/Cancel use the same versioned task commands. No native input/capture occurs.
The Paused label means new GUI actions are blocked; the separate human-handoff
message requires completed input drain/release and checkpoint (PA-044). No automatic resume follows hide/open,
cancel or a scheduled tick. New Task/schedule management remain their later UI scope.

`dotnet test tests/PersonalAgent.IntegrationTests --filter DesktopShell` exercises
five STA cases using actual WPF XAML/common commands and a fake tray boundary:
opt-in hide, default exit, disposal/start failure, paused bindings, explicit fresh
lease after Resume, Cancel preserving human pause, and Pausing until atomic drain.
The tests never show windows or register a real tray/global hotkey. Actual tray
visibility/menu acceptance is **NOT TESTED** until an attended report arrives:

1. Run `dotnet run --project src/PersonalAgent.App --no-build`.
2. Check **Keep running in tray when closing**, then close the main window with X.
3. Find the Personal Agent tray icon (possibly under the hidden-icons arrow).
   Right-click it and select **Pause & Take Control**, then **Controls**.
4. Confirm **Paused** is visible with the main window hidden. No task is needed.
5. Use tray **Open** to restore the main window, then tray **Exit** to shut down.

NotifyIcon reference: [Microsoft component overview](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/notifyicon-component-overview-windows-forms).

## Screen-independent computer test doubles (PA-029)

PA-029 shared test doubles live in `tests/TestDoubles/` and are linked into unit
and integration test assemblies only. `dotnet test tests/PersonalAgent.UnitTests
--filter FakeComputer` exercises all five `IComputerController` methods, exact
script ordering, injected failure with no retry, pre-cancellation and separate
frame generations without Windows graphics assemblies or a display. The embedded
4x3 PNG is synthetic and contains no captured screen data. Integration
`FakeComputerPayloadTests` verifies its actual pixels and SDK content with local
codecs, without a desktop or provider call. The fake does not enforce or prove
production authorization, lease, input cleanup or fresh-resume orchestration.

## Bounded model image payloads (PA-028)

`WindowsScreenshotSerializer` implements neutral `IScreenshotSerializer` using
in-memory codecs; no display, file, network or model credentials are needed.
Default options preserve PNG pixels and do not resize: maximum output 4096x4096,
4 MiB encoded, input 32 MiB / 32 million pixels. Oversize images fail with
ImageLimitExceeded. Enable ResizeToFit explicitly to fit width/height with aspect
ratio preserved to pixel rounding, or choose JPEG and its quality explicitly.
There is no automatic quality-reduction retry. SourceBounds, monitor metadata,
time and generation remain intact; ScreenshotCoordinates uses the actual output
dimensions. Input image text/EXIF metadata is not copied to the new bitmap.

`dotnet test tests/PersonalAgent.IntegrationTests --filter ScreenshotSerializationTests`
checks real PNG/JPEG decoding, PNG pixel fidelity, dimension/byte limits, default
resize denial, explicit resize/mapping, malformed/mislabelled bytes, cancellation,
metadata stripping and actual Microsoft.Extensions.AI ChatMessage/DataContent.
These local tests make no paid inference call and prove no live vision behavior.

API references: [DataContent binary constructor](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.datacontent.-ctor),
[WPF JPEG codecs](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-encode-and-decode-a-jpeg-image).

## Local fake execution (PA-011/PA-012)

`dotnet test --filter FakeAgentRuntimeTests` runs deterministic scripts through
the SDK-neutral `IAgentRuntime`, with ordered operation progress, bounded execution,
cancellation and externally controlled async step boundaries. It uses only the
`fake` profile and performs no model/tool/network/desktop calls. It can also return
a scripted failure. `AgentContractTests` covers neutral request/result serialization.
`dotnet test --filter TaskRunnerTests` exercises the session-local command/queue
pipeline with duplicate submits, queued/active cancellation, late results, bounded
runtime, step-boundary pause/resume and safe interruption on shutdown. Only one
runner consumes a coordinator. Runtime adapters must cooperate with cancellation
and call its async step boundary; this is not a native desktop gate. UI, persistence
and business end-to-end acceptance remain separate backlog work.

`dotnet test --filter DashboardTests` loads actual XAML on STA, runs the host/runner,
and checks bound submit/pause/resume/cancel, ordered progress/result, dispatcher
responsiveness and shutdown. It never displays a window or injects desktop input.
For an attended visual check, run `dotnet run --project src/PersonalAgent.App` and
use Start local task; only a local fake profile is configured. The WPF entry point
persists history in `%LocalAppData%/PersonalAgent/history.db`; select a finished task
after restarting to inspect its events/result. This does not validate a paid model
or complete FDD-001. Tests use isolated databases or memory-only host composition.

## SQLite schema (PA-017)

`dotnet test --filter PersistenceSchemaTests` uses real temporary SQLite files,
applies/reapplies migrations, closes/reopens connections and checks UTC storage,
foreign keys, unique run/event sequence, versioned payload constraints and append-only
event triggers. No user database is opened. Pooling is disabled in test fixtures so
temporary files can be removed after all contexts close.

EF Core SQLite/Design 10.0.12 matches the existing 10.0.12 dependency baseline.
Only Persistence references EF. Schema rows map dates to UTC ticks (INTEGER) so
SQLite can compare/order them; domain DTOs remain DateTimeOffset and SDK-free.
RunVersion is application-managed concurrency, not a SQLite rowversion. Event
UPDATE/DELETE triggers are custom migration SQL; future SQLite table rebuilds must
preserve them. PA-019 connects the repository to runtime and finished-task history.

To reproduce migration tooling without changing global tools:

```powershell
dotnet tool install dotnet-ef --tool-path artifacts/ef --version 10.0.12
artifacts/ef/dotnet-ef migrations has-pending-model-changes --project src/PersonalAgent.Persistence
```

The design-time factory targets an in-memory database. Do not replace migration
verification with EnsureCreated. References:
[EF SQLite limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations),
[EF design-time context](https://learn.microsoft.com/en-us/ef/core/cli/dbcontext-creation).

`dotnet test --filter PersistenceRepositoryTests` checks the SDK-neutral
`IRunRepository` through real SQLite: CAS plus appended history commits atomically,
eight competing writers produce one winner, stale/repeated versions fail without
extra events, and a real event insert failure rolls back the run change. Attempt
inputs and terminal outcomes cannot be replaced; unknown payload versions are
rejected. There is no automatic replay or external call.

`dotnet test --filter PersistenceRuntimeTests` verifies committed creation/start
before agent execution, durable progress/final metadata, pause/resume/cancel and
save-failure handling. A failed completion save cannot publish Completed. The
restart Dashboard test closes the host, opens a fresh host against the same real
temporary SQLite file and checks actual XAML history/status/events/result. The
coordinator serializes staged revisions, commits them, then publishes snapshots;
query locks never cover database I/O. Every persisted progress update advances
RunVersion. History loads without replaying started attempts; PA-021's startup
recovery restores only known-safe unstarted local tasks. No provider or native GUI
work is configured.

`dotnet test --filter PersistenceConversationTests` stores/reopens neutral ordered
messages and immutable provider-session checkpoints. Provider/model/schema must
match before reading opaque state; incompatible state returns no checkpoint while
neutral history remains available. Duplicate message sequence/external IDs and
cross-conversation run links are rejected. This is storage scaffolding only:
no real provider session restore, channel follow-up or automatic action replay.
Callers must exclude credentials from session payloads.

`dotnet test --filter PersistenceRecoveryTests` includes a real test-process kill:
the fixture commits a synthetic write request, writes one line in its own temporary
effect file, then waits without acknowledging. After termination, recovery marks
Interrupted, preserves pending evidence and never invokes that attempt again; the
effect remains one line. All started nonterminal states, repeat recovery, safe local
queue restore and unknown-tool blocking are covered. This is not a live MCP or
SendInput unknown-outcome E2E; those drivers/policy are later tasks.

The history startup service owns an exclusive `.lock` file beside the database.
The OS releases its handle on process exit/crash. A second host with the same DB
fails startup before recovery can touch a live run. DashboardTests checks this and
the actual Interrupted status/review warning with Resume disabled. Unstarted fake
tasks with no allowed tools can restart once; other model/tool sets await review.

`dotnet test --filter PersistenceIdempotencyTests` covers durable Submit identity:
an explicit `(ExternalKey.Source, ExternalKey.Value)` is authoritative when supplied;
otherwise CommandId is the key. An execution-intent hash excludes transient IDs and
normalizes tool-ID sets/default limits. A unique source/key index commits with the
new run/conversation/events. Concurrent conflicts re-read the winner after rollback,
never insert a second attempt or replay a completed one. Keys from different
connections/schedules are separate. Control-command receipts are still session-local.
New identity columns are nullable for legacy rows; historical keys are not invented.

Tests also lose the creation acknowledgement after commit: redelivery resolves the
original queued local/no-tool run and permits its first execution once. Started,
uncertain or terminal attempts never requeue through this path. Actual Telegram
polling and scheduler occurrence generation remain later backlog work.

For the full deterministic storage/restart regression group, run
`dotnet test tests/PersonalAgent.IntegrationTests --filter Persistence`.
`PersistenceUpgradeTests` starts at the original schema, writes legacy history,
upgrades/reapplies all migrations and checks data/append-only triggers and nullable
legacy submission keys. It also checks conservative/idempotent Created-state
recovery. Existing schema, repository, runtime, conversation, process-crash and
idempotency tests are reused; all use temporary databases and no external LLM.

## Primary monitor capture (PA-025)

`dotnet test --filter WindowsScreenCaptureTests` uses a fake native boundary and
real PNG encode/decode to check default denial, cancellation, desktop availability,
generation/authorization/geometry changes, metadata and no silent capture retry.
It does not touch the physical screen or prove lock-screen/hardware behavior.

The native adapter checks active WTS session plus accessible Default input desktop,
uses scoped PerMonitorV2 physical geometry and a hidden non-activating HWND for
GetDpiForWindow, then GDI capture with deterministic resource cleanup. App/fixture
manifests establish PerMonitorV2 before visible windows. No screenshot files,
image logs, network or clipboard are used; the driver is not exposed to fake tasks.
Caller authorization defaults to denied and is rechecked with geometry/session
before returning an encoded frame. Production lease integration remains later work.

Attended check: run `dotnet run --project tests/PersonalAgent.WindowsSmoke`, activate
the window on the primary display and click **Capture PRIMARY monitor**. This
separate button types nothing. Verify primary desktop preview (including blue
CAPTURE CHECK bar), PNG dimensions/source bounds/DPI, and paste the PRIMARY report
with your observation. Pixels stay in memory and are not uploaded. Close the window.
Attended result recorded on 2026-10-18: user returned PRIMARY OK, PNG 1920x1080,
source (0,0,1920,1080), DPI 120, one monitor, generation 1, 365729 bytes in response
to this preview check. Diagnostic process subsequently exited 0. This verifies
the primary capture check on that setup; two-monitor and live lock/UAC checks
remain unverified.

API references: [GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow),
[thread DPI context](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddpiawarenesscontext),
[OpenInputDesktop](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop).

## Virtual desktop and selected monitor capture (PA-026)

The same adapter now accepts PrimaryMonitor, VirtualDesktop and Monitor + ID.
It enumerates display rectangles in a scoped PerMonitorV2 context, computes their
bounding rectangle without clamping negative X/Y, and returns each captured
monitor's physical geometry/DPI. Gaps between monitors are black-filled before
copying monitor regions. Missing IDs fail with MonitorUnavailable, without
falling back to a different display. Any topology/DPI/primary change invalidates
the frame; enumeration order alone does not. Existing authorization, desktop,
cancellation and generation checks still apply. No agent tool is registered.

`dotnet test tests/PersonalAgent.IntegrationTests --filter WindowsScreenCaptureTests`
currently runs 28 cases, using fake native displays and the real PNG codec. It
covers primary/virtual/selected bounds, negative origins, mixed DPI, invalid
topology/overflow, monitor removal/addition/move/DPI changes, cancellation and
access denial. It does **not** exercise native two-display pixels or GDI gap fill.

Attended acceptance requires **two actual displays in Extend mode**:

1. Arrange the second display to the left and above the primary in Windows Display
   Settings, so its X/Y are negative. Use distinct visible content on each display;
   optionally different scaling. Do not expose private content during the check.
2. Run `dotnet run --project tests/PersonalAgent.WindowsSmoke`, activate its window
   on the primary, and click **Capture VIRTUAL desktop** yourself. No screenshot is
   taken merely by starting the diagnostic.
3. Verify preview contains both displays at the correct relative offsets and the
   blue CAPTURE CHECK bar on the correct display. Compare source bounds to SYSTEM
   virtual=(X,Y,width,height); PNG size must equal width/height. Check the DISPLAY
   lines against configured monitor geometry and scaling (100%=96, 125%=120,
   150%=144 DPI). Empty gaps should be black.
4. Click **Capture SECONDARY monitor**. Verify preview contains only the first
   non-primary display; source must equal its DISPLAY bounds. Move the diagnostic
   window to that display and repeat to verify the blue bar there. Also click
   PRIMARY to check it still selects the primary regardless of window position.
5. Paste the VIRTUAL/MONITOR/PRIMARY, DISPLAY and SYSTEM lines, describe whether the
   preview matched, then close the window. Nothing is saved or uploaded by the app.

Hardware result is **pending**: the latest attended report identifies one display
at DPI120. One-display success or simulated topology cannot satisfy PA-026's
two-display acceptance. Production lease/takeover and live lock/UAC remain later
checks; full FDD-002 S1-S4 are untested.

API references: [EnumDisplayMonitors](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors),
[virtual screen geometry](https://learn.microsoft.com/en-us/windows/win32/gdi/the-virtual-screen),
[BitBlt](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-bitblt).

PA-043 attended result (2026-10-18): user confirmed all requested tray/mini-window
checks succeeded; App exec34756 shut down normally and exited0. The generic icon
was reported as missing and replaced with the generated abstract arrow asset,
embedded in the executable, both windows and tray; see Assets/README.md for source
and prompt. Icon decoding and seven unchanged DesktopShell/HealthScreen tests
passed. This does not complete physical-input safe pause/resume acceptance.

## Safe pause confirmation (PA-044)

`dotnet test tests/PersonalAgent.IntegrationTests --filter SafePause` covers nine
cases with fake native input and real temporary SQLite: Ctrl release held open,
queued input refusal, coalesced takeover, delayed/failing confirmation writes,
failed/locked cleanup, cancel during failed cleanup, restored global uncertainty,
generation-specific checkpoint idempotency and another UI's Resume clearing the
human-handoff message. No real desktop input, capture or hotkey registration.

Take Control closes the local gate immediately, then the task pause and native
atomic cleanup drain. The resource lease is released while logical human pause
ownership remains reserved; a DesktopPauseConfirmed event records generation,
InputReleased and reason only afterwards. The UI displays the human-handoff
message only after the event commits and while the current resource still agrees.
Failed cleanup latches InputCleanupUncertain; Resume/replacement leases cannot
clear it, and history restoration preserves that block. Cancel/Skip wait for
active input and retain uncertain release evidence instead of erasing it.
Checkpoint failure leaves the gate closed without a success message or automatic
retry. Actual physical-input takeover/resume remains the PA-048 attended gate.

## Safe resume and fresh observation (PA-045)

WindowsComputerController is bound permanently to one opaque lease. A trusted
runtime must call ScreenshotAsync, supply that current image for UI re-evaluation,
then ConfirmObservation with the exact returned object before Click/Scroll/Type/
PressKey. This acknowledgment is not a model-callable permission tool. Foreign,
old, unconfirmed or failed-refresh frames cannot authorize input; the controller
owns its geometry copy. Resume creates a new grant/controller, never replaces the
old object's lease or replays pending arguments. No automatic desktop capture or
acquisition occurs when resuming independent non-GUI work. App GUI admission and
production tool registration remain disabled/default denied.

`dotnet test tests/PersonalAgent.IntegrationTests --filter "ComputerResume|SafeResume"`
covers eleven cases: four input paths require fresh/confirmed observation, changed
geometry after pause/resume rejects old coordinates/controllers, failed refresh
invalidates its old frame, callers cannot mutate private geometry, real SQLite
resume write delay/failure keeps admission closed, a new takeover wins a pending
resume without waking old GUI steps, and idempotent Running Resume cannot clear
human control. Native boundaries are fake; actual physical changed-window resume
is still part of attended PA-048.

## Waiting desktop runs (PA-046)

WaitAcquireAsync uses the existing resource coordinator's FIFO queue and async
change signals, not desktop polling or focus acquisition. Trusted admission is
rechecked after wakeup outside the resource lock. Direct Acquire retains its
immediate-busy behavior and cannot bypass a waiting queue; the current owner's
same grant remains idempotent. Token cancellation removes a waiter, and resource
CancelRunAsync cancels that run's pending requests without affecting another owner.
A released/cancelled owner never lifts global human pause automatically; only an
explicit valid resume and allowed admission can grant a waiting run. Uncertain
input fails queued requests closed. Production admission remains default denied.

`dotnet test tests/PersonalAgent.UnitTests --filter DesktopAccessQueue` runs seven
screen-free cases for FIFO, token/run cancellation, changed admission, released
human pause, cancelled owner and input uncertainty. No focus, capture or native
input is performed; runtime/tool wiring and full attended takeover remain later.

## Deterministic desktop races (PA-047)

DesktopAccessRaceTests adds five screen-free cases beyond existing ownership,
queue and native-boundary suites. Explicit synchronization holds admission on
another thread while takeover/cancellation occurs; an outer Task proves that the
async request reached the await/grant boundary before assertions. Other cases
force repeated takeover versus a saved resume version, two old release waiters
versus a replacement owner, and 32 same-run requests sharing one opaque grant.
No timing sleeps, randomized stress or desktop side effects are used.
`dotnet test --filter DesktopAccess --no-restore` includes these unit cases and the
existing fake-native driver gate suite; no matching physical E2E is implied.

## Real Windows takeover regression checklist (PA-115)

This checklist covers Development Plan E2E-03/04/05. Historical attended results
below remain evidence for their original scope; they do not establish a new
physical regression pass after PA-114. Automated cases use controlled native
boundaries unless explicitly stated otherwise.

| Check | Existing evidence | Remaining physical acceptance |
| --- | --- | --- |
| E2E-03: hotkey during clicks and long Unicode input | PA-115 owner-returned PAUSE/RESUME passed in both modes on 2026-10-22; `TakeoverStressTests` and `TakeControlPersistenceTests` | Passed for the guarded own-window native path; full model-driven re-evaluation is separate. |
| E2E-03: automatic pause on human activity | PA-115 owner-returned PA111 PAUSE/RESUME passed in both modes on 2026-10-22; `ActivityAutoPauseTests` | Passed for the guarded own-window native path. |
| E2E-03: tray and window switching | PA-115 native tray PAUSE/RESUME and explicit free other-window observation; `DesktopShellTests` | Passed2026-10-22: checkpoint,stable capture count,stale denial,new generation and fresh capture. |
| E2E-04: scheduled GUI while keyboard is active | PA-115 real scheduled occurrence/Windows idle/typing/nonactivating warning; `ScheduledDesktopTests` | Passed2026-10-22:1warning,0capture/input/owner,durable explicit Skip; owner confirmed focus retained. |
| E2E-05: first GUI after MCP read | PA-115 real loopback MCP/scripted MAF/registry/native own-window capture; `HybridMcpGuiTests` | Passed2026-10-22:1MCP read,0capture/owner before permission,1capture/1read/Completed afterward; owner confirmed focus retained. |

For the existing own-window checks, build once and run one fixture at a time:

```powershell
dotnet build tests/PersonalAgent.WindowsSmoke --no-restore
dotnet run --project tests/PersonalAgent.WindowsSmoke --no-build -- --takeover
dotnet run --project tests/PersonalAgent.WindowsSmoke --no-build -- --activity
```

Close the first fixture before launching the second. Follow the detailed PA-048
and PA-111 instructions in this file; START is the explicit input/capture gesture.
Do not switch away during its active input sequence. Test window switching only
after PAUSE OK; moving the target/window then Resume must reject the old frame.
BLOCKED/FAILED/NO PAUSE is not a passing report.

PA-115 manual-hotkey regression passed on 2026-10-22 with owner-returned reports:
clicks stopped at5/20 with nativeCalls=5->5 and generation1->2; Unicode stopped
at144/2750 with nativeCalls=9->9 and generation3->4. Both confirmed released
modifiers, valid Unicode, durable checkpoint, old/foreign context denial and
fresh capture/click/Ctrl+A on explicit Resume. Exact reports are in progress/LOG.md.
AutoPause=False/ownEvents=0 is expected in this manual-hotkey fixture; automatic
activity, tray/window switching and scheduled/late-MCP were subsequently accepted
within the explicit local native scope described below.

PA-115 automatic-activity regression also passed on2026-10-22: clicks4/20,
nativeCalls4->4, ownEvents12, generation1->2; Unicode80/2750, nativeCalls5->5,
ownEvents179, generation3->4. Both report AutoPause/checkpoint=True, zero marker
width mismatches, released modifiers and fresh Resume with stale/foreign denial.
Exact owner-returned reports are in progress/LOG.md.

Tray pause and explicit fresh Resume subsequently passed with owner-returned
`PA115 TRAY PAUSE OK` (checkpoint/controlConfirmed=True,captures1->1) and
`PA115 TRAY RESUME OK` (staleDenied=True,generation1->2,freshCapture=True,input0).
The owner explicitly confirmed working freely in another window while paused.
The earlier failed Resume record remains retained in LOG. Scheduled/late-MCP rows
are tracked separately below.

Scheduled busy-keyboard native acceptance subsequently passed: one warning,
244typing events,64real busy-idle samples, zero capture/input/desktop owner and
durable Skipped after explicit CHECK/Skip. The owner explicitly confirmed the
warning did not steal focus from the typing field. Exact reports and answers are
in progress/LOG.md.

MCP then GUI native acceptance passed with one warning,98typing events,87busy
samples and one MCP read while capture/input/desktop owner remained zero. After
explicit permission, one own-window screenshot completed the durable run without
repeating the read. The owner confirmed focus stayed in the typing field. Reopened
SQLite run3B796CA8-2DE5-4C3A-BE8E-5DB35DBD6F80 is Completed/version12, with exactly
one ToolRequested/ToolExecuted pair for MCP and one for computer_screenshot.

PA-115 local native regression acceptance is complete on2026-10-22. These are
owner-observed own-window/real-input/real-idle checks with a scripted model and
loopback MCP; corporate Teams/Jira, paid/live models, multi-monitor and deferred
sleep/display acceptance remain separate release gaps. The shipped Dashboard
still exposes no task computer/MCP tool set; this diagnostic grants none in
production. Existing controlled tests alone did not establish physical success;
exact attended reports and human confirmations are retained in progress/LOG.md.

For each attended row retain date/build, entry point, RunId, policy, observed
focus/input, pause/checkpoint and fresh-resume result, with fixed failure codes
when unsuccessful. Do not retain screenshots, credentials or private window text.
PA-113 Sleep/wake and Display change remain separately deferred by the owner and
are not part of this requested regression session. No second monitor is needed
for the own-window checks; PA-026/030 hardware gates remain separate.

### PA-115 native check procedure

```powershell
dotnet run --project tests/PersonalAgent.WindowsSmoke --no-build -- --regression
```

Close other smoke/App windows using the hotkey first. Opening is unarmed; START
creates the isolated synthetic database, production tray/control model and real
Windows idle source. The diagnostic references the actual App assembly (the older
fixtures now use those same production service types instead of compiling copies).
No production tool exposure/settings change, paid model, injected input or
corporate service. Captures copy only this foreground window on the primary
monitor into an in-memory masked canvas, with no upload or image file. Synthetic
database records remain under PersonalAgent.Regression in the temporary directory;
typing contents are not logged. Run one case per launch, then close/relaunch.

1. **Tray + window switching**: START and wait for `PA115 TRAY ARMED`. Use the
   actual Personal Agent tray menu **Pause & Take Control**. Switch to a harmless
   local window and type a little synthetic text, then return to the fixture and
   CHECK. Expect `PA115 TRAY PAUSE OK`; deliberately press this window's RESUME,
   not tray Resume, and keep the fixture foreground. Expect `PA115 TRAY RESUME OK`
   with old-context denial/new generation/fresh capture. Return both lines and
   explicitly confirm whether other-window work remained uninterrupted.
2. **Scheduled busy keyboard**: START schedules a real one-time occurrence15seconds
   ahead with StartWhenIdle. Type synthetic text continuously in the fixture's
   field for about30seconds; the production warning is nonactivating and its
   countdown is15seconds. Do not press its Start now. When CHECK enables, press it;
   it verifies the busy wait and explicitly chooses Skip once. Return
   `PA115 SCHEDULE WAIT` and `PA115 SCHEDULE` lines, and confirm no focus theft.
   Required: one warning, real idle samples, zero captures/input/owner and durable
   Skipped. This exercises explicit skip after deferral, not natural30minute expiry.
3. **MCP then GUI**: START creates an ephemeral loopback MCP read-only server. A
   scripted model through actual MAF/registry reads it once before requesting GUI.
   Type synthetic text until CHECK enables after the warning countdown. CHECK
   must report `PA115 MCP WAIT OK`, one MCP call and zero GUI/owner. Then press
   this fixture's **RESUME / ALLOW own-window capture** and keep it foreground.
   Expect `PA115 MCP OK`, one capture, one MCP call and durable Completed; confirm
   no focus theft before permission. AskEveryTime permits this deliberate action;
   it does not test automatic idle admission or a corporate/live-model workflow.

Failed/blocked or premature focus/capture is not success. Return complete report
lines before repeating. The tray fixture intentionally sends no input; already
accepted click/Unicode fixtures supply the native input/cleanup regression.

## Attended native takeover/resume (PA-048; passed 2026-10-19)

Run `dotnet run --project tests/PersonalAgent.WindowsSmoke --no-build -- --takeover`.
Launch alone performs no capture/input or hotkey registration. START explicitly
registers Ctrl+Shift+F10 and starts an own-target test after a 3-second countdown.
The fixture uses TakeControlHotkeyService from the actual App assembly and the
real Core runner/resource and WindowsComputerController, and an isolated synthetic
SQLite history under the dedicated PersonalAgent.Takeover temp directory, removed
on close. Screenshots stay in memory; no upload, clipboard or LLM. Input guards
require own foreground/focused field or exact own visible target; focus/target
changes reject instead of clicking another application. Unicode native batches
have fixture-only 80ms pacing so a human can interrupt; no production delay added.
Held user modifiers reject Unicode before sending. A rejection may wait up to500ms
for the local hotkey message, with no retry or further input.

For each mode, **20 clicks** and **Unicode text**:
1. Click START, keep this window active, wait for actual clicks/text to begin.
2. Press and release Ctrl+Shift+F10 while the sequence is incomplete.
3. Expect `PA048 PAUSE OK`: native count stable, partial actual effects,
   valid Unicode, released modifiers and a persisted safe-pause checkpoint.
4. Click MOVE TARGET (or move the test window), then RESUME.
5. Expect `PA048 RESUME OK`: old controller/foreign frame denied, fresh generation
   and capture, a deliberate click at the current target and Ctrl+A selection with
   released modifiers. The unfinished sequence is discarded, never continued.
6. Return PAUSE and RESUME lines for both modes, then close the window.

NO PAUSE means the sequence completed before interruption; START can repeat by
explicit gesture. BLOCKED/FAILED is not success and no input is retried.

The user returned actual attended results on 2026-10-19 on one monitor:

| Mode | PAUSE OK | RESUME OK |
| --- | --- | --- |
| 20 clicks | nativeCalls=3->3; clickEvents=3; chars=0/2750; modifiersReleased=True; validUnicode=True; checkpoint=True | oldDenied=True; foreignFrameDenied=True; generation=1->2; freshCapture=True; freshClick=True; CtrlA=True; modifiersReleased=True |
| Unicode text | nativeCalls=12->12; clickEvents=0; chars=191/2750; modifiersReleased=True; validUnicode=True; checkpoint=True | oldDenied=True; foreignFrameDenied=True; generation=1->2; freshCapture=True; freshClick=True; CtrlA=True; modifiersReleased=True |

Both modes discarded the unfinished sequence. The real controller, hotkey, input
and SQLite pause checkpoint satisfy PA-048's attended takeover/resume criterion.
This does not substitute multi-display/locked-session/live-model acceptance or
full model-driven FDD-002 S2. Original WindowsSmoke without --takeover remains
available. Full observed evidence is retained in progress/LOG.md.

### PA-059 attended live OpenAI read-only acceptance

```powershell
dotnet run --project tests/PersonalAgent.WindowsSmoke --no-build -- --live-openai
```

Uses the saved `openai-live` OpenAI profile and DPAPI key from the normal app.
Opening the fixture performs no API/capture/input/hotkey operation. Close another
Personal Agent instance using Ctrl+Shift+F10. Keep the whole fixture visible on one
monitor. START explicitly permits bounded paid requests and native capture of
this own test window; no mouse/keyboard input is available to the model.
The shared production GDI helper copies only the visible own-window rectangle
onto a black monitor canvas. Other desktop pixels are not copied/uploaded.

1. Click START and wait for `PA059 CAPTURED` (a real model requested the tool).
2. Press and release Ctrl+Shift+F10. Wait for `PA059 PAUSE OK`.
3. Click RESUME. The window title changes; the held pre-pause image is discarded
   and a fresh native frame from a new desktop generation is sent to the model.
4. Return the `PA059 LIVE OK` or failure line without credentials. The check
   requires the model's answer to match the title found only in image pixels,
   stable API/capture counts while paused, zero native input and a reopened
   SQLite history record containing both the final result and pause confirmation.

Only the screenshot tool is registered. All production registry/MAF/provider,
Core runner/desktop lease/hotkey and SQLite paths are reused. The fixture adds
an explicit attended hold inside that read-only tool so the hotkey has a
repeatable test point. This is not evidence of arbitrary GUI input automation,
fully automatic model pause recovery or corporate workflows. Synthetic history
is kept in the temporary directory printed by the window; no image files or
keys are written. Cancel/close stops the run without retries.

Attended acceptance passed on 2026-10-19. The owner returned LIVE OK with
modelCalls=2, captures=2, generation=1->2, hotkeyPause=True,
freshAfterResume=True, durableHistory=True, nativeInput=0. The reopened actual
SQLite run E7ADDF79-827F-4C2C-8993-DE169FCFA985 is Completed/version19,
with pause/resume, correlated ToolRequested/ToolExecuted and final result.
Provider-reported usage is4892input+26output; cost remains unknown. The final
synthetic title does not appear in request instructions. This completes PA-059's
live read-only acceptance; full autonomous app opening/input and notification
business scenarios remain separate. AttendedPauseTests verifies the fixture's
Pausing-state handoff and explicit boundary/resume, using no native/API calls.

### PA-074 deterministic hybrid MCP + GUI

```powershell
dotnet test tests/PersonalAgent.IntegrationTests --no-restore --filter FullyQualifiedName~HybridMcpGuiTests
```

The original five account-free cases compose actual MainWindow/XAML commands, Core runner,
MAF, common tool registry, loopback Jira MCP, WindowsComputerController and SQLite.
A scripted model and in-memory Teams/native adapters supply synthetic GUI pixels
and record input; no real screen is read and no native input is sent.
Both GUI → MCP and MCP → GUI retain one run, correlated calls, typed source links
and reopened history. The latter uses a fixture warning/admission/idle policy and
actual pause/Resume commands: MCP holds no desktop lease and GUI waits for explicit
fixture consent. Cancel at that point touches no desktop. Remote error/timeout
after GUI keeps XAML and durable Blocked results consistent, releases the lease
and does not replay the read. This covers deterministic E2E-02/E2E-05 composition;
physical idle detection, corporate accounts and notification delivery remain
separate. Default production model tool exposure remains closed.

PA-090 adds six cases to the same fixture (eleven total). Persisted scheduled
submission now reaches the production warning/idle/lease gate after the actual
local MCP read. All three policies prevent premature GUI, preserve the same
model session and typed Jira result, and complete with one read/one GUI action.
Skip/cancel before GUI has zero native effects; a direct click with an invented
observation token is refused without acquiring desktop. Warning presentation,
clock, idle and native effects are controlled fixtures; E2E-05 local acceptance
does not imply physical focus or corporate/live-provider acceptance.

### PA-078..080 exact human approvals

```powershell
dotnet test tests/PersonalAgent.IntegrationTests --no-restore --filter "FullyQualifiedName~ApprovalRepositoryTests|FullyQualifiedName~HumanApprovalTests|FullyQualifiedName~ApprovalsUiTests|FullyQualifiedName~ApprovalYield"
```

These cases use real SQLite, Core/MAF, local fixture tools or loopback MCP and
unshown WPF/XAML. They verify exact payload/owner/revision/expiry decisions,
one winner under races, no writes before approval, controlled denial, desktop
release, fresh observation after yield, pause/cancel/revocation and revalidation
immediately before dispatch. The App's Approvals header opens an owned nonmodal
screen; current Windows SID supplies owner identity. Full action, destination,
risk, source, arguments and expiry are visible. Approve requires review, Deny
does not, and acknowledgement cannot transfer to another selection. Closing or
refreshing does not make a decision. No real window, corporate account, native
screen/input or paid model is used by these tests. Default production task tool
exposure stays closed pending its explicit policy/tool-selection integration.

### PA-081 durable dispatch and crash ambiguity

```powershell
dotnet test tests/PersonalAgent.IntegrationTests --no-restore --filter FullyQualifiedName~ToolDispatch
```

The approval fixture now uses the durable ToolCalls ledger before writes.
Concurrent dispatch reservations have one winner; exact action, approval, expiry
and run checks fail before effects. Failed ack persistence or actual MCP timeout
after a synthetic effect surfaces OutcomeUnknown with a verification instruction.
The crash case kills a real child after one temporary file write before ack,
restarts the actual App host twice, checks unshown history XAML, refuses reuse of
the call/approval and completes an independent local task. File effect count
remains exactly one. This is controlled E2E-08 evidence, not live corporate or
physical GUI acceptance. Startup recovery requires exclusive history ownership.

### PA-083 shared Computer Use / MCP policy matrix

```powershell
dotnet test --no-restore --filter Policy
```

CommonToolPolicyTests runs the same exact-owner approval, denial, expiry,
cancellation, unknown-write classification and approval/cancel race cases for
loopback MCP and a fixture-only semantic GUI reply tool. The latter uses the real
WindowsComputerController with fake capture/input natives and a fresh lease/frame
after consent. It introduces no production locator/semantic GUI tool and never
reads or controls the real desktop. Existing generic coordinate-tool stale-frame
refusal remains covered separately. This is account-free E2E-07 evidence; live
Teams/corporate acceptance remains a separate release gate.

### PA-084..085 schedule data and occurrence deduplication

```powershell
dotnet test --no-restore --filter "FullyQualifiedName~Schedule"
```

Strict four-mode recurrence data and real SQLite tests cover UTC/timezone,
validation, versioned edits and cadence anchors. SchedulerTests uses actual Core
queue/persistence and hosted App restart with a controlled clock/local runtime:
eight ticks create one Run, missed intervals coalesce, active runs do not overlap,
disable/edit wins before admission, and a committed run survives a lost schedule
advance without another execution on restart. The existing unique external-key
store supplies occurrence identity; no extra occurrence subsystem is added.
No GUI lease/tool grant comes from scheduling. Editor/countdown/idle acceptance
remains separate; explicit DST/misfire checks are described below.

PA-086 adds ScheduleMisfireTests in UnitTests and IntegrationTests: installed
Europe/Kyiv spring gap/fall fold, wall-clock versus elapsed UTC cadence, default
RunOnce, zero-runtime persisted Skip, and a CatchUp budget that survives name edits
and kernel/SQLite reopen. CatchUp permits 1..10 runs per batch (default3), requires
at least hourly intervals or calendar cadence, and cannot reset its budget on
every wake. Skip allows one minute of ordinary tick jitter. The migration keeps
existing schedules at RunOnce and their original UTC occurrence. These checks use
controlled clocks/local runtimes and perform no GUI, external API or notification.

### PA-087..091 schedule editor, admission and deferred checks

```powershell
dotnet test tests/PersonalAgent.IntegrationTests --no-restore --filter "SchedulesUiTests|ScheduledDesktopTests|WindowsSystemIdleTests|SchedulerTests|HybridMcpGuiTests"
```

The editor tests load unshown XAML and real SQLite. Admission tests use the actual
runner/registry/controller with fake native boundaries: no capture/input before
presented countdown plus configured idle/explicit permission, and no stale owner
decision across cancel/configuration/takeover. WindowsSystemIdleTests includes an
actual read-only GetLastInputInfo/GetTickCount64 metadata probe; it sends no input.

PA-091 bounds a pending warning to30monotonic minutes by default, including
unpresented/deferred warnings and configuration refreshes. A shorter existing Run
budget can end earlier. Expiration records Skipped/DeferredExpired; crossing the
deadline during an awaited admission write still cannot grant desktop. A controlled
six-hour window holds thirty missed ten-minute intervals as one run/warning;
after permission it executes one current check and advances overdue RunOnce slots
without an immediate old follow-up. Eight coalescing writers have one version
winner; explicit Skip, unrelated and terminal runs cannot be coalesced by this path.
Physical focus/keyboard, notification delivery and corporate/live-model acceptance
remain separate from these deterministic fixtures.

### PA-092 scheduler lifecycle matrix

```powershell
dotnet test --no-restore --filter Scheduler
```

The mandatory filter includes actual hosted busy-warning restart, expiration
across repeated host reopen, eight concurrent ticks while input is busy and a
Kyiv repeated03:30 calendar occurrence held through the fold/restart. Existing
SchedulerTests supplies occurrence deduplication and lost-advance recovery;
ScheduleMisfireTests and ScheduleCalculatorTests in the full suite supply the
spring gap/fold and policy matrix. E2E-04 local acceptance uses controlled input
idle/native boundaries and no physical focus/input. The optional real ten-minute
smoke was not run; it remains distinct from these passing deterministic tests.

### PA-116 protected credential persistence

```powershell
dotnet test tests/PersonalAgent.IntegrationTests --no-restore --filter ProtectedSecretStoreTests
```

Uses actual Windows CurrentUser DPAPI with random synthetic model/Telegram/MCP
values. Three independent hidden child processes reopen the protected files and
confirm a digest without plaintext in arguments/output; they use the integration
host's runtime configuration because its output prunes shared Windows assemblies.
The existing recovery harness has a separate fixed-output `--secret-roundtrip`
mode; its crash/write modes remain unchanged.

An actual consistent SQLite configuration backup/reopen retains validated opaque
references but contains no plaintext or protected-file blobs. Copying this
database without the separate secret directory cannot resolve its keys. Cancelled
writes and malformed/path references are refused. Existing provider/UI tests
cover secure model/channel/MCP save, missing/tampered values and error handling.
The application storage implementation is reused; general settings export/import
UI is PA-120, and cross-account/machine decryption is not claimed by these tests.

### PA-117 logging privacy and history retention

```powershell
dotnet test tests/PersonalAgent.IntegrationTests --no-restore --filter 'FullyQualifiedName~PrivacyLoggingTests|FullyQualifiedName~HistoryRetentionTests|FullyQualifiedName~PrivacyUiTests'
```

Default AppHost logging uses one metadata-only console formatter. It emits fixed
module/event/type and allowlisted typed IDs/counts/timestamps, with no message
templates, arbitrary strings, scopes, exception messages/inner/stack, image bytes
or SDK payloads. App failure Trace output contains only the exception type.
Tests poison every discarded field and use a formatter that must never execute;
they also check the real host's default provider selection. Explicit test logger
overrides remain available for recording lifecycle assertions.

Screenshots remain RAM-only. Open **Privacy**, Reload settings, choose 1..36500
days, and Save to persist the automatic-expiry choice (off by default). Enabled
expiry runs at startup and hourly. **Clear expired content now** applies the entered
period once, even with automatic expiry off. This is destructive content cleanup;
separate backups, SQLite free pages/WAL and external copies are not secure-erased.

Only whole conversations whose runs all finished before the cutoff are eligible.
Recent messages/sessions, nonterminal/interrupted work, pending approvals,
unconsumed approved writes, dispatched/unknown tools, pending/sending/unknown
deliveries, unsupported schemas and uncertain input release prevent cleanup.
Independent eligible conversations still proceed. Private run/request/result,
event, approval, delivered/failed notification and message contents are redacted;
opaque sessions are removed. IDs/statuses/revisions/timestamps/submission hashes,
ordered events, message identities, action hashes, channel correlations and
delivery/dispatch tombstones remain. Task/schedule/model/channel configuration and
protected credentials remain configuration, outside history cleanup.

HistoryRetentionTests uses real migrated SQLite, restart/dedup/follow-up checks,
an independent-conversation safety matrix, atomic rollback and late-writer denial.
The migration keeps the no-delete event trigger and allows only canonical content
redaction after a terminal run's privacy marker, preserving all event metadata.
PrivacyUiTests loads actual unshown XAML and checks saved policy, invalid input,
manual cleanup and the selected Dashboard history refresh without desktop input.
No paid provider, live transport or physical desktop acceptance is inferred.

### PA-118 run diagnostics and local metrics

```powershell
dotnet test tests/PersonalAgent.IntegrationTests --no-restore --filter 'FullyQualifiedName~RunDiagnosticsTests|FullyQualifiedName~PrivacyLoggingTests'
```

Open **Run diagnostics** for read-only statuses, event kinds/IDs, tool fingerprints,
counts, completed duration, known usage/cost, fixed pause/error classifications and
safe next steps. The current desktop generation is shown only for its bound owner;
historical generations are logged only from persisted pause confirmation. The
delivery tab shows the latest100 outstanding/failed metadata rows; unknown
delivery requires real-world review, never a diagnostic retry. No message JSON,
instructions, summaries, scopes, provider errors, screenshots or raw tool IDs
are displayed. Standard logs use event IDs2100..2111 for RunEventType ordinal,
2200 for NotificationAttempted and2199 for unavailable observation. Known
provider names are fixed labels; arbitrary provider/tool identifiers are hashed.

The built-in **PersonalAgent** Meter exposes `personalagent.operations` and
`personalagent.duration` (seconds), with only fixed `operation`/`outcome` enum
tags: Run, Model, Tool, Mcp, Gui, Screenshot, GuiBusyAdmission, PauseGateClose,
Resume, FailureAfterResume, ScheduleDuplicate, DeliveryAttempt and DeliveryRetry.
Model/tool spans measure the application call, including any boundary wait within
that call; they are not provider-only latency. GuiBusyAdmission counts scheduled
admissions initially blocked by known busy input. PauseGateClose measures local
gate revocation, not drain/checkpoint time; its listeners run off the control
callback. Input/output counters (`personalagent.tokens.input/output`) record
reported result usage, including blocked results; `personalagent.cost.estimate`
emits only a configured known nominal estimate, with fixed/Other currency tags.
Unknown usage/cost remains unknown. These are process-local observations, not
durable billing or exactly-once totals; startup baselines restored history instead
of replaying it. No exporter/network destination is enabled by this task.

RunDiagnosticsTests verifies actual App/Core/MAF/SQLite with a scripted model and
synthetic read tool; poison payloads stay out of the real formatter and diagnostic
DTO/XAML. It also covers known blocked usage, unknown usage, one resumed failure,
persisted unknown-delivery review across two restarts, inert refresh and throwing
metric listeners. Existing ComputerTool/AgentLimits/ScheduledDesktop/Scheduler/
NotificationOutbox/GlobalHotkey/SafePause regressions protect dispatch/control.
No paid provider, live delivery or physical pause-latency benchmark is claimed.

### PA-119 first-run setup

```powershell
dotnet test tests/PersonalAgent.IntegrationTests --no-restore --filter 'FullyQualifiedName~SetupWizardTests|FullyQualifiedName~ModelProfilesTests|FullyQualifiedName~ModelCapabilityTests'
```

**Setup** opens the four-step wizard; it opens automatically on a normal WPF
startup with no model profiles and no run history, or with `--setup` (the existing
`--models` editor shortcut takes precedence). Headless hosts never open it.
Local demo can proceed without credentials/API calls. Cloud requires a saved
profile with image/tool support and an explicit successful current-profile probe;
edits or selection changes invalidate that confirmation. Probe buttons disclose
provider API use, and all requests use the existing bounded synthetic checks.
Optional MCP/Telegram buttons open the existing owned settings windows and grant
nothing to the first task. The first task is text-only, at most2steps/1024tokens/
60seconds, no GUI/MCP/channel tools, one deliberate admission per wizard session.
Blocked/error/unknown outcomes show fixed diagnostics advice and never retry.
Closing a wizard does not cancel an admitted task; normal task controls remain.

Manual acceptance still required for the catalog's **fresh Windows profile**:

1. In a separate new Windows user profile, with this machine's .NET Desktop
   runtime available, launch the built app (not a second instance sharing the
   original user's history): `dotnet PersonalAgent.App.dll --setup`. Copy the
   complete built output directory to a folder readable by that account if its
   mapped drive differs. A clean machine without .NET belongs to PA-121.
2. Confirm the wizard appears with **Local demo (no API)**, and opening it has
   created no run and requested no credentials or connections.
3. Next through the capability page without a probe, skip optional MCP/Telegram,
   and click **Run first read-only task** once. Expect **Local demo completed**
   after about2seconds and one Run ID. No input/screenshot or API is authorized.
4. Close/reopen the app. The same completed task must appear in history with no
   repeat execution. Opening **Setup** again must remain an explicit action.
5. Report the visible result, Run ID, whether it was a separate fresh Windows
   account, and whether any unexpected input/network/credential prompt appeared.

Optional paid cloud acceptance is a separate explicit action: configure a real
vision/tools profile in Models, save, click its disclosed API test, then complete
the wizard. Never paste credentials or screenshots into the progress report.
SetupWizardTests proves the local composed flow with fresh SQLite/actual DPAPI
under the current Windows owner and scripted cloud clients; it does not claim
another Windows-account or live-provider acceptance.

### PA-120 settings transfer and database upgrades

```powershell
dotnet test tests/PersonalAgent.IntegrationTests --no-restore --filter 'FullyQualifiedName~SettingsTransferTests|FullyQualifiedName~DatabaseMaintenanceTests'
```

Open **Settings** deliberately. **Export settings** writes a schema-versioned,
2 MiB maximum JSON template, without credentials, DPAPI references, run history,
agent sessions, approvals, permissions, task instructions, MCP arguments or
secret environment variables. HTTP endpoints transfer their origin only; review
and re-enter paths/query and CLI arguments locally. Names and identifiers reject
or redact recognizable credential patterns. Treat even non-secret configuration
(hosts, model IDs and channel destinations) as local configuration.

On the destination, first configure local model keys with matching profile IDs
and providers. **Open import** validates and previews counts/IDs; **Apply reviewed
import** commits once, atomically. Unknown versions/properties, duplicate JSON
fields, invalid configuration, missing/mismatched local credentials or a failed
write apply nothing. Existing model keys remain local. Imported MCP connections
and schedules receive fresh identities and stay disabled/unapproved, without
tool grants. Schedule instructions are a review placeholder. Channels bind the
current local owner but stay disabled/unverified without receive permission or
pairing consent; configure a local key and explicitly verify the peer before
enabling. Existing active configurations/history/safety ledgers remain. The
retention period transfers with automatic cleanup off. Restart to reload caches,
then review each imported template before enabling it. Reimporting a file is an
explicit new import, not an identity-preserving restore; a conflicting desktop
route rejects the whole import.

Startup owns the existing exclusive database lock before checking migrations.
An existing database with pending migrations is backed up through SQLite's
backup API (including committed WAL), checked with `PRAGMA quick_check`, then
atomically published under `<database>.backups/history-<UTC>-<GUID>.db`. Only then
may migration/recovery run. A backup failure or unknown newer applied migration
stops startup; current schema produces no extra snapshot. **Create private DB
backup** uses the same consistent-copy API on demand.

DB backups contain private history and configuration references, and exclude the
separate protected-secret directory. They are not portable settings, do not
resolve credentials under another Windows owner, and are not erased by history
retention. Keep this directory private; remove obsolete copies deliberately.
Never automatically replace a live database with an old snapshot: later action
receipts/dedup records could be missing. For manual recovery, exit all instances,
preserve the current database and its WAL/SHM, and review external effects and
uncertain operations before choosing a compatible backup. Retain the matching
protected store on the original Windows account; do not export it as plaintext.

SettingsTransferTests covers actual unshown WPF/SQLite/DPAPI and local key binding,
secret canaries, strict parsing, atomic rollback and unchanged unknown-delivery
records. DatabaseMaintenanceTests starts the actual host over historical raw SQL,
checks the pre-upgrade snapshot/current history, committed WAL, failed backup and
newer-schema refusal. No live provider/channel or another-account claim is made.

### PA-121 Windows package and optional login startup

```powershell
dotnet test tests/PersonalAgent.IntegrationTests --no-restore --filter 'FullyQualifiedName~LoginStartupTests|FullyQualifiedName~DesktopShellTests|FullyQualifiedName~DesktopHostTests|FullyQualifiedName~SettingsTransferTests|FullyQualifiedName~SetupWizardTests'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish-windows.ps1
```

See [distribution instructions](../docs/DISTRIBUTION.md) for the ZIP, version,
SHA256, private app data, upgrade and explicit startup/disable/removal procedure.
LoginStartupTests uses real isolated HKCU keys under Software/PersonalAgent/Tests,
never the user's Run registration. It checks default/no-save inertness, actual
Save button, quoted Unicode/space-containing paths, explicit disable/path update,
unrelated values and missing/development/foreign refusal. Existing shell/host
tests preserve exit/disposal and close-to-tray behavior. No autostart consent is
included in Settings export/import.

The publish script's actual executable diagnostic requires locally bundled
CoreCLR/WPF, loads unshown XAML and performs one durable local demo in temporary
SQLite; it opens no windows, registers no hooks/startup and makes no provider/tool
calls. Empty DOTNET_ROOT/DOTNET_ROOT_X64 and a local host trace supplement the
assembly-location checks. This is controlled package evidence on this developer
machine. Clean Windows without .NET, real sign-out/in autostart and disable
observations require the separate acceptance checklist in DISTRIBUTION.md;
do not infer them from successful publishing or synthetic registry keys.
