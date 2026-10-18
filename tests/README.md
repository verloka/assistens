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

## MAF tool and approval diagnostic (PA-006)

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
The Paused label means new GUI actions are blocked; PA-044 still owns the complete
physical-release/persistence confirmation. No automatic resume follows hide/open,
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
