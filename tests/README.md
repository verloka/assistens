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
