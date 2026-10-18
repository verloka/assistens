using Microsoft.Extensions.AI;
using PersonalAgent.Agent;
using PersonalAgent.Computer;
using PersonalAgent.TestDoubles;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalAgent.IntegrationTests;

public sealed class FakeComputerPayloadTests
{
    [Fact]
    public async Task ScriptedScreenshotPassesRealCodecAndSdkContentWithoutDesktopOrProvider()
    {
        var fake = new FakeComputerController([new(new(ComputerOperation.Screenshot, Options: new()), ScreenshotFixture.Create())]);
        var payload = await new WindowsScreenshotSerializer().SerializeAsync(await fake.ScreenshotAsync(new()), new());
        var data = Assert.Single(ScreenshotMessageContent.Create(payload).OfType<DataContent>());
        using var input = new MemoryStream(data.Data.ToArray());
        var bitmap = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(4, bitmap.PixelWidth);
        Assert.Equal(3, bitmap.PixelHeight);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[4 * 3 * 4];
        converted.CopyPixels(pixels, 16, 0);
        Assert.Equal(new byte[] { 30, 0, 0, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 30, 180, 180, 255 }, pixels[^4..]);
        Assert.Single(fake.Calls);
    }
}
