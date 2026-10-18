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
- `PersonalAgent.E2ETests`: reserved scaffold; no tests yet. A successful command
  or a "no tests available" message is not business E2E acceptance.

Add scenario links to existing FDD Drafts only when corresponding tests exist;
never promote them to Active without explicit approval. Keep live/manual provider,
device and corporate checks separate from fake/local evidence. The ten E2E cases
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
