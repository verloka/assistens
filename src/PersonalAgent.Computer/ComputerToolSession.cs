using PersonalAgent.Core.Diagnostics;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Computer;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace PersonalAgent.Computer;

// Explicit run scope, owned/disposed by the composition root. Construction never captures or injects input.
public sealed class ComputerToolSession : IAsyncDisposable
{
    private readonly Guid runId;
    private readonly IDesktopAccessCoordinator access;
    private readonly Func<DesktopLease, IObservedComputerController> createController;
    private readonly SemaphoreSlim serial = new(1, 1);
    private DesktopLease? lease;
    private IObservedComputerController? controller;
    private ScreenCapture? frame;
    private string? observationId;
    private ScreenshotOptions captureOptions = new();
    private bool evaluated, disposed;
    private InputApproval? pendingApproval, approvedInput;
    private sealed record InputApproval(string ToolId, Guid ConversationId, string CallId, string ArgumentsHash,
        string ImageHash, string Geometry, ScreenshotOptions Options, string OldObservationId);
    public IReadOnlyList<IAgentTool> Tools { get; }

    public ComputerToolSession(Guid runId, IDesktopAccessCoordinator access,
        Func<DesktopLease, IObservedComputerController>? createController = null)
    {
        this.runId = runId; this.access = access;
        this.createController = createController ?? (grant => new WindowsComputerController(access, grant));
        Tools = Array.AsReadOnly(new[] { "screenshot", "click", "scroll", "type", "press_key" }
            .Select(operation => (IAgentTool)new Tool(this, operation)).ToArray());
    }

    // Called by trusted runtime only after the model has responded to the delivered frame.
    public void ConfirmModelObservation()
    {
        if (frame is null || evaluated) return;
        if (lease is null || !access.IsCurrent(lease)) throw new DesktopAccessException("StaleDesktopContext");
        controller!.ConfirmObservation(frame); evaluated = true;
    }

    // Trusted exact-action authorizer only. The old token is never made generally current again.
    public async Task<bool> PrepareInputApprovalAsync(ToolDescriptor tool, ToolArguments arguments, ToolExecutionContext context, CancellationToken ct)
    {
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (disposed || context.RunId != runId || frame is null || !evaluated || lease is null || !access.IsCurrent(lease)
                || tool.IsReadOnly || !Tools.Any(value => value.Descriptor.Id == tool.Id && value.Descriptor.Source == tool.Source && value.Descriptor.RiskLevel == tool.RiskLevel)
                || arguments.Value.GetProperty("observationId").GetString() != observationId) return false;
            var proof = new InputApproval(tool.Id, context.ConversationId, context.ToolCallId, ArgumentsHash(arguments),
                ImageHash(frame), Geometry(frame), captureOptions, observationId!);
            await ReleaseCoreAsync(ct).ConfigureAwait(false);
            pendingApproval = proof; return true;
        }
        finally { serial.Release(); }
    }

    public async Task<bool> RevalidateInputApprovalAsync(ToolDescriptor tool, ToolArguments arguments, ToolExecutionContext context, CancellationToken ct)
    {
        await serial.WaitAsync(ct).ConfigureAwait(false);
        var valid = false;
        try
        {
            var proof = pendingApproval; pendingApproval = null;
            if (disposed || context.RunId != runId || proof is null || proof.ToolId != tool.Id
                || proof.ConversationId != context.ConversationId || proof.CallId != context.ToolCallId || proof.ArgumentsHash != ArgumentsHash(arguments)) return false;
            lease = await access.WaitAcquireAsync(runId, ct).ConfigureAwait(false); controller = createController(lease);
            frame = await controller.ScreenshotAsync(proof.Options, ct).ConfigureAwait(false); frame.Validate();
            if (!access.IsCurrent(lease) || frame.DesktopGeneration != lease.Generation
                || ImageHash(frame) != proof.ImageHash || Geometry(frame) != proof.Geometry) return false;
            // Identical pixels/topology preserve the evaluated target for this one approved call only.
            observationId = Guid.NewGuid().ToString("N"); captureOptions = proof.Options;
            controller.ConfirmObservation(frame); evaluated = true; approvedInput = proof; valid = true; return true;
        }
        catch (ScreenCaptureException) { return false; }
        catch (DesktopAccessException) { return false; }
        finally
        {
            try { if (!valid) await ReleaseCoreAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { serial.Release(); }
        }
    }

    private static string ArgumentsHash(ToolArguments arguments) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(arguments.Value.GetRawText())));
    private static string ImageHash(ScreenCapture capture) => Convert.ToHexString(SHA256.HashData(capture.ImageBytes));
    private static string Geometry(ScreenCapture capture) => JsonSerializer.Serialize(new
        { capture.ImageWidth, capture.ImageHeight, capture.SourceBounds, capture.Monitors, capture.ContentType });

    private async Task<ToolResult> ExecuteAsync(string operation, ToolArguments arguments, ToolExecutionContext context, CancellationToken ct)
    {
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (disposed || context.RunId != runId) return Blocked("ComputerRunMismatch");
            ct.ThrowIfCancellationRequested();
            var value = arguments.Value;
            if (operation == "screenshot")
            {
                approvedInput = null; pendingApproval = null;
                var area = value.TryGetProperty("area", out var areaValue) ? Enum.Parse<ScreenshotArea>(areaValue.GetString()!, true) : ScreenshotArea.PrimaryMonitor;
                var monitorId = value.TryGetProperty("monitorId", out var monitor) ? monitor.GetString() : null;
                var options = value.TryGetProperty("area", out _) ? new ScreenshotOptions(area, monitorId) : captureOptions;
                options.Validate();
                frame = null; observationId = null; evaluated = false;
                if (lease is null || !access.IsCurrent(lease))
                {
                    lease = await access.WaitAcquireAsync(runId, ct).ConfigureAwait(false);
                    controller = createController(lease);
                }
                using (var measured = AgentTelemetry.Measure(AgentOperation.Screenshot))
                {
                frame = await controller!.ScreenshotAsync(options, ct).ConfigureAwait(false);
                frame.Validate();
                if (!access.IsCurrent(lease) || frame.DesktopGeneration != lease.Generation) { frame = null; return Blocked("StaleDesktopContext"); }
                measured.Outcome = AgentOperationOutcome.Success;
                }
                captureOptions = options;
                observationId = Guid.NewGuid().ToString("N");
                return new()
                {
                    Status = ToolResultStatus.Success,
                    Text = "Fresh screenshot; coordinates are encoded image pixel indices.",
                    StructuredData = JsonSerializer.SerializeToElement(new
                    {
                        observationId,
                        frame.ImageWidth,
                        frame.ImageHeight,
                        frame.SourceBounds,
                        frame.Monitors,
                        frame.TimestampUtc,
                        frame.DesktopGeneration
                    }),
                    Images = [new(frame.ImageBytes.ToArray(), frame.ContentType)]
                };
            }
            if (lease is null || !access.IsCurrent(lease)) return Blocked("StaleDesktopContext");
            var approved = approvedInput; approvedInput = null;
            var exactApproved = approved is not null && approved.ToolId == "computer_" + operation
                && approved.ConversationId == context.ConversationId && approved.CallId == context.ToolCallId
                && approved.ArgumentsHash == ArgumentsHash(arguments) && approved.OldObservationId == value.GetProperty("observationId").GetString();
            if (frame is null || value.GetProperty("observationId").GetString() != observationId && !exactApproved) return Blocked("FreshScreenshotRequired");
            if (!evaluated) return Blocked("ScreenReevaluationRequired");
            if (operation == "click")
                await controller!.ClickAsync(Point(value), Enum.Parse<MouseButton>(value.GetProperty("button").GetString()!, true), ct).ConfigureAwait(false);
            else if (operation == "scroll")
                await controller!.ScrollAsync(Point(value), Enum.Parse<ScrollDirection>(value.GetProperty("direction").GetString()!, true), value.GetProperty("amount").GetInt32(), ct).ConfigureAwait(false);
            else if (operation == "type") await controller!.TypeAsync(value.GetProperty("text").GetString()!, ct).ConfigureAwait(false);
            else await controller!.PressKeyAsync(new(value.GetProperty("chord").GetString()!), ct).ConfigureAwait(false);
            // The registry's separately authorized observation dependency supplies the next frame.
            frame = null; observationId = null; evaluated = false;
            return new() { Status = ToolResultStatus.Success, Text = "GUI action completed; no retry performed." };
        }
        catch (ComputerInputException error)
        {
            var uncertain = !error.CleanupSucceeded || error.Inserted is null or > 0;
            return new()
            {
                Status = uncertain ? ToolResultStatus.OutcomeUnknown : ToolResultStatus.Blocked,
                ErrorCode = error.Code,
                Text = "GUI input stopped; do not repeat the action without checking its actual effect."
            };
        }
        catch (ScreenCaptureException error) { return Blocked(error.Code); }
        catch (DesktopAccessException error) { return Blocked(error.Code); }
        catch (ArgumentException) { return Blocked("InvalidComputerArguments"); }
        finally { serial.Release(); }

        ScreenPoint Point(JsonElement value) => ScreenshotCoordinates.ToDesktop(frame!,
            new(value.GetProperty("imageX").GetInt32(), value.GetProperty("imageY").GetInt32()), lease!.Generation);
    }

    // Approval waits return desktop ownership without retaining a frame or queued coordinates.
    public async Task YieldDesktopAsync(CancellationToken ct = default)
    {
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReleaseCoreAsync(ct).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }

    private async Task ReleaseCoreAsync(CancellationToken ct)
    {
        frame = null; observationId = null; evaluated = false; pendingApproval = null; approvedInput = null;
        if (lease is not null) await access.ReleaseAsync(lease, ct).ConfigureAwait(false);
        lease = null; controller = null;
    }

    public async ValueTask DisposeAsync()
    {
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            disposed = true; frame = null; observationId = null; pendingApproval = null; approvedInput = null;
            if (lease is not null) await access.ReleaseAsync(lease).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }

    private static ToolResult Blocked(string code) => new()
    { Status = ToolResultStatus.Blocked, ErrorCode = code, Text = "Computer operation blocked; obtain a fresh screenshot and re-evaluate the current UI before new input." };

    private sealed class Tool(ComputerToolSession owner, string operation) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("computer_" + operation,
            operation == "screenshot" ? "Capture the authorized desktop in memory. No input is generated." :
                "Perform one authorized " + operation + " using the current evaluated observation. Image coordinates are zero-based pixels. The result includes a fresh post-action screenshot; use its new observationId next. Never reuse an observation after pause/resume.",
            Schema(operation), new(ToolSourceKind.Computer, "windows-computer"),
            operation == "screenshot" ? ToolRiskLevel.ExternalDisclosure : ToolRiskLevel.Write, RequiresDesktop: true, IsReadOnly: operation == "screenshot",
            ObservationToolId: operation == "screenshot" ? null : "computer_screenshot");
        public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
            owner.ExecuteAsync(operation, arguments, context, cancellationToken);
    }

    private static JsonElement Schema(string operation)
    {
        const string observation = "\"observationId\":{\"type\":\"string\",\"pattern\":\"^[a-f0-9]{32}$\"}";
        const string point = ",\"imageX\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647},\"imageY\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}";
        var (properties, required) = operation switch
        {
            "screenshot" => ("\"area\":{\"type\":\"string\",\"enum\":[\"PrimaryMonitor\",\"VirtualDesktop\",\"Monitor\"]},\"monitorId\":{\"type\":\"string\",\"minLength\":1}", ""),
            "click" => (observation + point + ",\"button\":{\"type\":\"string\",\"enum\":[\"left\",\"right\",\"middle\"]}", "\"observationId\",\"imageX\",\"imageY\",\"button\""),
            "scroll" => (observation + point + ",\"direction\":{\"type\":\"string\",\"enum\":[\"up\",\"down\",\"left\",\"right\"]},\"amount\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10}", "\"observationId\",\"imageX\",\"imageY\",\"direction\",\"amount\""),
            "type" => (observation + ",\"text\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":4096}", "\"observationId\",\"text\""),
            _ => (observation + ",\"chord\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":64}", "\"observationId\",\"chord\"")
        };
        var monitorRules = operation == "screenshot" ? ",\"allOf\":[{\"if\":{\"properties\":{\"area\":{\"const\":\"Monitor\"}},\"required\":[\"area\"]},\"then\":{\"required\":[\"monitorId\"]},\"else\":{\"not\":{\"required\":[\"monitorId\"]}}}]" : "";
        return JsonDocument.Parse("{\"type\":\"object\",\"properties\":{" + properties + "},\"required\":[" + required + "],\"additionalProperties\":false" + monitorRules + "}").RootElement.Clone();
    }
}
