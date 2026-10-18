namespace PersonalAgent.Core.Computer;

// Driver boundary only. Authorization, lease/generation and argument policy are enforced by adapters/tools.
public interface IComputerController
{
    Task<ScreenCapture> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct = default);
    Task ClickAsync(ScreenPoint point, MouseButton button, CancellationToken ct = default);
    Task ScrollAsync(ScreenPoint point, ScrollDirection direction, int amount, CancellationToken ct = default);
    Task TypeAsync(string text, CancellationToken ct = default);
    Task PressKeyAsync(KeyCombination keys, CancellationToken ct = default);
}

// Trusted runtime acknowledges only the current frame after supplying it for UI re-evaluation.
// This acknowledgment is not an LLM-callable permission tool.
public interface IObservedComputerController : IComputerController
{
    void ConfirmObservation(ScreenCapture observation);
}

public enum MouseButton { Left, Right, Middle }
public enum ScrollDirection { Up, Down, Left, Right }
public sealed record KeyCombination(string Chord);
