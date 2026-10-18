using PersonalAgent.Core.Computer;

namespace PersonalAgent.TestDoubles;

public enum ComputerOperation { Screenshot, Click, Scroll, Type, PressKey }
public sealed record ComputerCall(ComputerOperation Operation, ScreenshotOptions? Options = null,
    ScreenPoint? Point = null, MouseButton? Button = null, ScrollDirection? Direction = null,
    int? Amount = null, string? Text = null, KeyCombination? Keys = null);
public sealed record ComputerStep(ComputerCall Expected, ScreenCapture? Capture = null, Exception? Error = null);

// Test-only deterministic script. Never registered in production DI; does not emulate authorization or native input.
// Every call consumes exactly one matching step. There is no implicit success, screenshot reuse or retry.
public sealed class FakeComputerController(IEnumerable<ComputerStep> script) : IComputerController
{
    private readonly Queue<ComputerStep> steps = new(script.Select(step => step with { Capture = Copy(step.Capture) }));
    private readonly List<ComputerCall> calls = [];
    private readonly object sync = new();
    public IReadOnlyList<ComputerCall> Calls { get { lock (sync) return calls.ToArray(); } }
    public int RemainingSteps { get { lock (sync) return steps.Count; } }

    public Task<ScreenCapture> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct = default)
    {
        options.Validate();
        var capture = Execute(new(ComputerOperation.Screenshot, Options: options), ct)
            ?? throw new InvalidOperationException("Scripted screenshot requires a frame.");
        capture.Validate();
        return Task.FromResult(capture);
    }
    public Task ClickAsync(ScreenPoint point, MouseButton button, CancellationToken ct = default)
    { Execute(new(ComputerOperation.Click, Point: point, Button: button), ct); return Task.CompletedTask; }
    public Task ScrollAsync(ScreenPoint point, ScrollDirection direction, int amount, CancellationToken ct = default)
    { Execute(new(ComputerOperation.Scroll, Point: point, Direction: direction, Amount: amount), ct); return Task.CompletedTask; }
    public Task TypeAsync(string text, CancellationToken ct = default)
    { Execute(new(ComputerOperation.Type, Text: text), ct); return Task.CompletedTask; }
    public Task PressKeyAsync(KeyCombination keys, CancellationToken ct = default)
    { Execute(new(ComputerOperation.PressKey, Keys: keys), ct); return Task.CompletedTask; }

    private ScreenCapture? Execute(ComputerCall call, CancellationToken ct)
    {
        lock (sync)
        {
            ct.ThrowIfCancellationRequested();
            calls.Add(call);
            if (steps.Count == 0 || steps.Peek().Expected != call)
                throw new InvalidOperationException("Unexpected computer call; no matching script step.");
            var step = steps.Dequeue();
            if (step.Error is not null) throw step.Error;
            return Copy(step.Capture);
        }
    }
    private static ScreenCapture? Copy(ScreenCapture? frame) => frame is null ? null : frame with
    { ImageBytes = frame.ImageBytes.ToArray(), Monitors = frame.Monitors.ToArray() };
}
