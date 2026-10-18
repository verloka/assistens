using Microsoft.Extensions.AI;
using PersonalAgent.Agent;
using PersonalAgent.Computer;
using PersonalAgent.Core.Computer;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalAgent.IntegrationTests;

public sealed class ScreenshotSerializationTests
{
    [Theory]
    [InlineData(ScreenshotImageFormat.Png, "image/png")]
    [InlineData(ScreenshotImageFormat.Jpeg, "image/jpeg")]
    public async Task EncodedPayloadIsBoundedDecodableAndAcceptedByActualModelMessageTypes(ScreenshotImageFormat format, string mediaType)
    {
        var input = Frame();
        var options = new ScreenshotImageOptions { Format = format, MaxBytes = 10_000 };
        var result = await new WindowsScreenshotSerializer().SerializeAsync(input, options);
        Assert.Equal(mediaType, result.ContentType);
        Assert.InRange(result.ImageBytes.Length, 1, options.MaxBytes);
        AssertGeometry(input, result);
        using var stream = new MemoryStream(result.ImageBytes);
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(13, decoded.Frames[0].PixelWidth);
        Assert.Equal(7, decoded.Frames[0].PixelHeight);
        Assert.Equal(format == ScreenshotImageFormat.Png, decoded is PngBitmapDecoder);
        if (format == ScreenshotImageFormat.Png)
        {
            var rgba = new FormatConvertedBitmap(decoded.Frames[0], PixelFormats.Bgra32, null, 0);
            var pixels = new byte[13 * 7 * 4];
            rgba.CopyPixels(pixels, 13 * 4, 0);
            Assert.Equal(Enumerable.Range(0, 13 * 7).SelectMany(index => new byte[] { (byte)(index * 2), 60, 120, 255 }), pixels);
        }
        var message = new ChatMessage(ChatRole.User, ScreenshotMessageContent.Create(result).ToList());
        var data = Assert.Single(message.Contents.OfType<DataContent>());
        Assert.Equal(mediaType, data.MediaType);
        Assert.Equal(result.ImageBytes, data.Data.ToArray());
        Assert.Contains("generation=7", Assert.Single(message.Contents.OfType<TextContent>()).Text);
        result.ImageBytes[0] ^= 0xff;
        Assert.NotEqual(result.ImageBytes[0], data.Data.Span[0]);
    }

    [Fact]
    public async Task ExplicitResizePreservesOriginalGeometryAndUsesActualEncodedDimensionsForMapping()
    {
        var input = Frame();
        var result = await new WindowsScreenshotSerializer().SerializeAsync(input,
            new() { ResizeToFit = true, MaxWidth = 5, MaxHeight = 5 });
        AssertGeometry(input, result);
        Assert.Equal(5, result.ImageWidth);
        Assert.Equal(2, result.ImageHeight);
        using var stream = new MemoryStream(result.ImageBytes);
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(result.ImageWidth, decoded.PixelWidth);
        Assert.Equal(result.ImageHeight, decoded.PixelHeight);
        Assert.Equal(new ScreenPoint(-12, -8), ScreenshotCoordinates.ToDesktop(result, new(0, 0), 7));
        Assert.Equal(13, input.ImageWidth);
        Assert.Equal(7, input.ImageHeight);
    }

    [Theory]
    [InlineData("output-bytes")]
    [InlineData("width")]
    [InlineData("input-bytes")]
    [InlineData("input-pixels")]
    public async Task LimitsRejectPayloadRatherThanSilentlyReducingQualityOrTextSize(string limit)
    {
        var options = limit switch
        {
            "output-bytes" => new ScreenshotImageOptions { MaxBytes = 1 },
            "width" => new ScreenshotImageOptions { MaxWidth = 1 },
            "input-bytes" => new ScreenshotImageOptions { MaxInputBytes = 1 },
            "input-pixels" => new ScreenshotImageOptions { MaxInputPixels = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(limit))
        };
        var error = await Assert.ThrowsAsync<ScreenshotSerializationException>(() => new WindowsScreenshotSerializer().SerializeAsync(Frame(), options));
        Assert.Equal("ImageLimitExceeded", error.Code);
    }

    [Theory]
    [InlineData("invalid", "InvalidImage")]
    [InlineData("mime", "InvalidImage")]
    [InlineData("dimensions", "ImageGeometryMismatch")]
    public async Task InvalidOrMislabelledImageCannotBecomeModelContent(string issue, string expected)
    {
        var input = Frame();
        input = issue switch
        {
            "invalid" => input with { ImageBytes = [1, 2, 3] },
            "mime" => input with { ContentType = "image/jpeg" },
            "dimensions" => input with { ImageWidth = 12 },
            _ => input
        };
        var error = await Assert.ThrowsAsync<ScreenshotSerializationException>(() => new WindowsScreenshotSerializer().SerializeAsync(input, new()));
        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public async Task CancellationAndInvalidConfigurationDoNotProducePayload()
    {
        var serializer = new WindowsScreenshotSerializer();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serializer.SerializeAsync(Frame(), new(), cancel.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => serializer.SerializeAsync(Frame(), new() { MaxBytes = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => serializer.SerializeAsync(Frame(), new() { JpegQuality = 101 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => serializer.SerializeAsync(Frame(), new() { Format = (ScreenshotImageFormat)999 }));
    }

    [Fact]
    public async Task EncodingStripsInputTextMetadata()
    {
        var result = await new WindowsScreenshotSerializer().SerializeAsync(Frame(), new());
        using var stream = new MemoryStream(result.ImageBytes);
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var metadata = decoded.Metadata as BitmapMetadata;
        Assert.False(metadata?.ContainsQuery("/tEXt/{str=Comment}") ?? false);
    }

    private static void AssertGeometry(ScreenCapture expected, ScreenCapture actual)
    {
        Assert.Equal(expected.SourceBounds, actual.SourceBounds);
        Assert.Equal(expected.Monitors, actual.Monitors);
        Assert.Equal(expected.DesktopGeneration, actual.DesktopGeneration);
        Assert.Equal(expected.TimestampUtc, actual.TimestampUtc);
        Assert.NotSame(expected.ImageBytes, actual.ImageBytes);
    }

    private static ScreenCapture Frame()
    {
        var pixels = Enumerable.Range(0, 13 * 7).SelectMany(index => new byte[] { (byte)(index * 2), 60, 120, 255 }).ToArray();
        var image = BitmapSource.Create(13, 7, 96, 96, PixelFormats.Bgra32, null, pixels, 13 * 4);
        var metadata = new BitmapMetadata("png");
        metadata.SetQuery("/tEXt/{str=Comment}", "synthetic-private-marker");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
        using var stream = new MemoryStream(); encoder.Save(stream);
        return new()
        {
            ImageBytes = stream.ToArray(), ContentType = "image/png", ImageWidth = 13, ImageHeight = 7,
            SourceBounds = new(-13, -9, 13, 7), Monitors = [new("synthetic", new(-13, -9, 13, 7), 144, 144, true)],
            TimestampUtc = DateTimeOffset.UtcNow, DesktopGeneration = 7
        };
    }
}
