using PersonalAgent.Core.Computer;
using PersonalAgent.TestDoubles;

namespace PersonalAgent.UnitTests;

public sealed class FakeComputerTests
{
    [Fact]
    public async Task AllFiveDriverResultsUseOnlyNeutralContractsAndSyntheticFrame()
    {
        ComputerCall[] calls = [new(ComputerOperation.Screenshot, Options: new()),
            new(ComputerOperation.Click, Point: new(-3, -2), Button: MouseButton.Right),
            new(ComputerOperation.Scroll, Point: new(-3, -2), Direction: ScrollDirection.Down, Amount: 2),
            new(ComputerOperation.Type, Text: "Привіт\n🙂"), new(ComputerOperation.PressKey, Keys: new("Ctrl+A"))];
        var fake = new FakeComputerController(calls.Select(call => new ComputerStep(call,
            call.Operation == ComputerOperation.Screenshot ? ScreenshotFixture.Create() : null)));
        IComputerController controller = fake;
        var result = await controller.ScreenshotAsync(new());
        result.Validate();
        Assert.Equal(new ScreenPoint(-3, -2), ScreenshotCoordinates.ToDesktop(result, new(1, 1), 1));
        await controller.ClickAsync(new(-3, -2), MouseButton.Right);
        await controller.ScrollAsync(new(-3, -2), ScrollDirection.Down, 2);
        await controller.TypeAsync("Привіт\n🙂");
        await controller.PressKeyAsync(new("Ctrl+A"));
        Assert.Equal(calls, fake.Calls);
        Assert.Equal(0, fake.RemainingSteps);
        Assert.DoesNotContain(typeof(FakeComputerController).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name is "PresentationCore" or "PresentationFramework" or "PersonalAgent.Computer");
    }

    [Fact]
    public async Task FailedWriteIsRecordedOnceAndNotRetriedOrReplacedWithSuccess()
    {
        var error = new IOException("synthetic unknown outcome");
        var call = new ComputerCall(ComputerOperation.Type, Text: "test");
        var fake = new FakeComputerController([new(call, Error: error)]);
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => fake.TypeAsync("test")));
        Assert.Single(fake.Calls);
        Assert.Equal(0, fake.RemainingSteps);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.TypeAsync("test"));
    }

    [Fact]
    public async Task CancellationDoesNotConsumeStepsAndMismatchCannotFallThrough()
    {
        var fake = new FakeComputerController([new(new(ComputerOperation.Screenshot, Options: new()), ScreenshotFixture.Create())]);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.ScreenshotAsync(new(), cts.Token));
        Assert.Empty(fake.Calls);
        Assert.Equal(1, fake.RemainingSteps);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.ClickAsync(new(), MouseButton.Left));
        Assert.Equal(1, fake.RemainingSteps);
        Assert.Equal(4, (await fake.ScreenshotAsync(new())).ImageWidth);
    }

    [Fact]
    public async Task FramesAreIsolatedAndFreshScriptedGenerationReplacesOldCoordinates()
    {
        var first = ScreenshotFixture.Create(1);
        var fake = new FakeComputerController([
            new(new(ComputerOperation.Screenshot, Options: new()), first),
            new(new(ComputerOperation.Screenshot, Options: new()), ScreenshotFixture.Create(2))]);
        first.ImageBytes[0] = 0;
        var old = await fake.ScreenshotAsync(new());
        Assert.Equal(137, old.ImageBytes[0]);
        old.ImageBytes[0] = 0;
        var fresh = await fake.ScreenshotAsync(new());
        Assert.Equal(137, fresh.ImageBytes[0]);
        Assert.Throws<InvalidOperationException>(() => ScreenshotCoordinates.ToDesktop(old, new(0, 0), fresh.DesktopGeneration));
        Assert.Equal(new ScreenPoint(-4, -3), ScreenshotCoordinates.ToDesktop(fresh, new(0, 0), 2));
    }
}
