using Microsoft.Extensions.AI;
using PersonalAgent.Core.Computer;

namespace PersonalAgent.Agent;

public static class ScreenshotMessageContent
{
    // The caller first obtains a bounded, validated payload through IScreenshotSerializer.
    // No provider call is made here; the adapter owns the binary content and geometry together.
    public static IReadOnlyList<AIContent> Create(ScreenCapture capture)
    {
        capture.Validate();
        return [new TextContent($"Screenshot: image={capture.ImageWidth}x{capture.ImageHeight}; physical={capture.SourceBounds}; generation={capture.DesktopGeneration}. Image coordinates are pixel indices; use the original geometry for desktop actions."),
            new DataContent(capture.ImageBytes.ToArray(), capture.ContentType)];
    }
}
