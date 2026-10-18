# FDD-002 — GUI access, pause and human takeover

Status: Draft

## Trigger / Outcome
- Initiator: an agent requests a Computer Use operation; user takes over by hotkey, tray, or mini-window; or user activity interrupts a scheduled run.
- Outcome: GUI access is exclusive while granted and immediately yields to human control at safe operation boundaries; resume uses a fresh screen observation.

## Inputs
- Run ID, requested action, current desktop-lease generation, user activity/idle status, pause/resume request, Windows session state.

## Outputs
- Valid operation result; denied, paused, blocked or cancelled state; fresh screenshot when access resumes.

## Flow
1. Check that Windows session is unlocked and current run can obtain the desktop lease.
2. Before **each** GUI action, validate authorization and current lease generation; reject stale queued actions.
3. Execute the atomic input safely, always releasing held modifiers in cleanup.
4. Upon local takeover request, close the GUI gate immediately without waiting for model response; allow current atomic input to finish safely and release modifiers.
5. Persist `PausedForUser` / corresponding run state; keep user in control until explicit approved resume.
6. At resume, reacquire lease, take a **fresh** screenshot, invalidate stale coordinates and ask the model to re-evaluate the UI before any next GUI action.

## Validations
- Lease ownership and generation; valid physical screen coordinates / DPI; session not locked or at UAC elevation mismatch.

## Invariants
- Human takeover always takes precedence over the agent.
- No **new** GUI action starts after the pause gate closes, including while an LLM call is still pending.
- GUI cannot be concurrently controlled by two runs; pause never promises to undo already completed clicks.
- Stale screenshots/coordinates are never blindly replayed after resume.

## Failures / Side Effects
- On lock/disconnect/UAC, return a blocked result without bypassing Windows protections.
- Pauses are distinct from cancel, skipped schedule, or stopping an MCP-only task.

## Dependencies
- None.

## Scenarios
- S1: Given a running GUI action loop, when the user invokes Take Control, then no subsequent clicks/keys begin and modifiers are released. [untested]
- S2: Given a paused GUI run and changed display, when the user resumes, then the agent receives a new capture and does not execute stale coordinates. [untested]
- S3: Given a locked desktop, when the model requests a click, then it is blocked and no unlock/UAC bypass is attempted. [untested]
- S4: Given concurrent runs, when both request GUI, then only one owns the desktop at a time. [untested]

## Implementation References
- Planned: `docs/DEVELOPMENT_PLAN_v1.1.md` §§6, 9–10; backlog PR-04, PR-05, PR-06, PR-14.

- Capture contract foundation (PA-024): `src/PersonalAgent.Core/Computer/ScreenContracts.cs` defines physical points/bounds, source geometry independent of resized image dimensions, per-monitor DPI and capture time/generation. `tests/PersonalAgent.UnitTests/ScreenContractTests.cs` checks negative origins, mixed-DPI metadata, overflow-safe half-open bounds, selection validation and neutral serialization. It does not decode image bytes, capture a desktop or implement lease/pause/resume; complete S1-S4 remain `[untested]`.

- Primary capture adapter (PA-025): `src/PersonalAgent.Computer/WindowsScreenCapture.cs` and `WindowsCaptureNative.cs` default to denied authorization, require active/default desktop, recheck generation/geometry, and return an in-memory PNG. The promoted GDI helper is shared with the attended WindowsSmoke fixture. `tests/PersonalAgent.IntegrationTests/WindowsScreenCaptureTests.cs` covers policy/session/context failure paths through a fake native boundary and real PNG encode/decode. The attended primary preview check returned PRIMARY OK on 2026-10-18 (1920x1080, DPI120, one monitor; process exit 0), recorded in `progress/LOG.md`. Real lock/UAC and two-monitor capture remain unverified. The adapter is not an agent tool; complete S1-S4 remain `[untested]`.

- Virtual/selected-monitor capture (PA-026, two-display hardware verification pending): the existing capture adapter enumerates full physical topology, preserves signed bounds and each display's DPI, and rejects changed topology before returning a frame. The GDI copy fills display gaps black and copies monitor rectangles at offsets from the virtual origin. `tests/PersonalAgent.IntegrationTests/WindowsScreenCaptureTests.cs` checks negative X/Y, mixed DPI, selection, unknown monitor rejection, reordered enumeration, topology changes and overflow using fake native geometry plus real PNG encoding. The attended WindowsSmoke fixture has explicit VIRTUAL and SECONDARY buttons and prints source/topology/system bounds for comparison; no automatic capture/input is added. This is partial S2 validation only; full S1-S4 remain `[untested]`.

- Coordinate foundation (PA-027): `src/PersonalAgent.Core/Computer/ScreenshotCoordinates.cs` maps encoded pixel centers to original physical bounds and back using integer arithmetic; physical geometry is not scaled by DPI a second time. It rejects out-of-frame points, monitor gaps and stale generations. `tests/PersonalAgent.UnitTests/CoordinatesTests.cs` covers 100/125/150% DPI, negative origins, resize/upscale, half-open bounds, extreme integers and stale context. This is partial S2 validation; no native click or fresh-resume orchestration is implemented, so full S1-S4 remain `[untested]`.

- Test driver foundation (PA-029): neutral `IComputerController` covers the five planned driver operations. `tests/TestDoubles/FakeComputerController.cs` consumes exact scripted calls/results/errors without OS input or retries; `ScreenshotFixture.cs` contains only a synthetic PNG. `tests/PersonalAgent.UnitTests/FakeComputerTests.cs` executes on net10.0 with no Windows graphics dependency; `tests/PersonalAgent.IntegrationTests/FakeComputerPayloadTests.cs` checks the same fixture through actual codecs and SDK content without capturing a screen. Scripted generation/error cases are foundations, not production lease/pause/recovery proof; full S1-S4 remain `[untested]`.

- Atomic input primitive (PA-031): internal `WindowsInputSender` and `WindowsInputNative` share a process-local input lock, require a trusted authorization/generation callback and active Default desktop, and send only bounded balanced batches. Partial/unknown results never replay presses; cleanup releases only the batch's possible held inputs, and the sender refuses later input after native failure. `tests/PersonalAgent.IntegrationTests/ComputerInputSenderTests.cs` checks native ABI and fake-native denial, cancellation, concurrency, partial insertion and cleanup failures. WindowsSmoke reuses this sender only after its attended gesture. The revised native path has not been manually rerun; production lease/takeover, real UIPI/lock races and full S1-S4 remain `[untested]`.

- Click foundation (PA-032): internal `WindowsMouseInput` rejects points outside observed bounds/monitors and stale generation/DPI/monitor context. Geometry-dependent input is prepared inside the authorized input lock, with session and policy rechecked afterwards; physical points map to virtual absolute input without DPI scaling. `tests/PersonalAgent.IntegrationTests/ComputerClickTests.cs` checks fake-native left/right/middle batches, physical mapping, gaps, stale context, denial and release-only partial-click cleanup. On 2026-10-18 the user returned actual attended CLICK Left/Right/Middle OK, each with exactly two matching target events, physical (479,474), generations 1-3 (additional Left/Middle also OK); diagnostic exited 0. The launch button uses a left click; the selection controls the injected button. This proves the dedicated target on that setup, not full S1-S4, which remain `[untested]`; no agent tool is exposed.

- Scroll foundation (PA-033): `WindowsMouseInput.ScrollAsync` uses the existing authorized physical-anchor preparation for Up/Down/Left/Right and 1-10 ticks per call. `tests/PersonalAgent.IntegrationTests/ComputerScrollTests.cs` checks signed deltas, bounds/limits, stale/revoked/locked context, cancellation and no retry after partial native insertion. The attended WindowsSmoke SCROLL action measures actual native wheel messages, anchor and test-area offset changes; on 2026-10-18 the user returned Down/Up/Left/Right OK, one matching event each, deltas +/-240, offsets 400 to 464/336 on the correct axis at physical (718,629); diagnostic exited 0. Full S1-S4 remain `[untested]`, with no production tool/lease exposure.

- Text-input foundation (PA-034, attended acceptance pending): internal `WindowsKeyboardInput` validates complete UTF-16/control data and the 4096-unit limit before effects, normalizes line endings, and sends balanced Unicode/Enter pairs in at most 16-unit chunks without splitting surrogates. `WindowsInputSender` holds its shared input lock across the text operation and rechecks cancellation/authorization/session/generation before each chunk. `tests/PersonalAgent.IntegrationTests/ComputerTypeTests.cs` verifies Unicode, multiline, surrogate boundaries, limits, no interleaving and stopping further chunks on cancel/revocation/native failure. WindowsSmoke TYPE TEXT/CANCEL compares actual field content, including a deterministic first-chunk cancellation fixture. No real typing observation yet; full S1-S4 and production takeover remain `[untested]`.

## Unknowns
- Concrete Windows APIs, pause latency and live race results must be established in the implementation.

## Image payload foundation
- PA-028: `src/PersonalAgent.Core/Computer/ScreenshotSerialization.cs` defines provider-neutral encoding options and `IScreenshotSerializer`. `src/PersonalAgent.Computer/WindowsScreenshotSerializer.cs` validates PNG/JPEG bytes and dimensions, limits input/output, strips image metadata and resizes only when explicitly enabled. Physical source bounds, monitor DPI, capture time and generation remain unchanged; coordinates use actual encoded dimensions. `src/PersonalAgent.Agent/ScreenshotMessageContent.cs` creates actual SDK text/binary content without making a provider call. `tests/PersonalAgent.IntegrationTests/ScreenshotSerializationTests.cs` checks codecs, PNG pixels, limits, malformed input, metadata stripping, resize mapping, cancellation and SDK content. This is partial S2 foundation only; full S1-S4, native GUI and live vision remain `[untested]`.
