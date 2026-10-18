using PersonalAgent.Core.Computer;

namespace PersonalAgent.TestDoubles;

public static class ScreenshotFixture
{
    // Synthetic 4x3 RGBA PNG; contains no desktop/user data. No OS graphics library is needed to load it.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAYAAAC09K7GAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAtSURBVBhXFchBEQAwDMOwIDGSIimSAgs/76ankuAEL9hgsjiLt9j9UZziFVt85AIVzbvikH8AAAAASUVORK5CYII=";

    public static ScreenCapture Create(long generation = 1) => new()
    {
        ImageBytes = Convert.FromBase64String(Png), ContentType = "image/png", ImageWidth = 4, ImageHeight = 3,
        SourceBounds = new(-4, -3, 4, 3), Monitors = [new("synthetic", new(-4, -3, 4, 3), 144, 144, true)],
        TimestampUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), DesktopGeneration = generation
    };
}
