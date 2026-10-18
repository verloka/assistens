using PersonalAgent.Core.Computer;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalAgent.Computer;

// In-memory WPF codecs only: no desktop access, files, metadata forwarding or network.
public sealed class WindowsScreenshotSerializer : IScreenshotSerializer
{
    public Task<ScreenCapture> SerializeAsync(ScreenCapture capture, ScreenshotImageOptions options, CancellationToken cancellationToken = default)
    {
        capture.Validate();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (capture.ImageBytes.Length > options.MaxInputBytes || (long)capture.ImageWidth * capture.ImageHeight > options.MaxInputPixels)
            throw TooLarge();
        // Take ownership of a snapshot before dispatching codec work off the caller/UI thread.
        var source = capture with { ImageBytes = capture.ImageBytes.ToArray(), Monitors = capture.Monitors.ToArray() };
        return Task.Run(() => Encode(source, options, cancellationToken), cancellationToken);
    }

    private static ScreenCapture Encode(ScreenCapture capture, ScreenshotImageOptions options, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var input = new MemoryStream(capture.ImageBytes, writable: false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            if (decoder.Frames.Count != 1 || (capture.ContentType == "image/png" ? decoder is not PngBitmapDecoder : decoder is not JpegBitmapDecoder))
                throw new ScreenshotSerializationException("InvalidImage", "Screenshot bytes must match the declared PNG/JPEG format and contain one frame.");
            BitmapSource image = decoder.Frames[0];
            if (image.PixelWidth != capture.ImageWidth || image.PixelHeight != capture.ImageHeight)
                throw new ScreenshotSerializationException("ImageGeometryMismatch", "Encoded dimensions do not match screenshot metadata.");
            if ((long)image.PixelWidth * image.PixelHeight > options.MaxInputPixels) throw TooLarge();
            if (image.PixelWidth > options.MaxWidth || image.PixelHeight > options.MaxHeight)
            {
                if (!options.ResizeToFit) throw TooLarge();
                var scale = Math.Min((double)options.MaxWidth / image.PixelWidth, (double)options.MaxHeight / image.PixelHeight);
                var width = Math.Max(1, (int)Math.Floor(image.PixelWidth * scale));
                var height = Math.Max(1, (int)Math.Floor(image.PixelHeight * scale));
                image = new TransformedBitmap(image, new ScaleTransform((double)width / image.PixelWidth, (double)height / image.PixelHeight));
            }
            ct.ThrowIfCancellationRequested();
            if (image.PixelWidth > options.MaxWidth || image.PixelHeight > options.MaxHeight) throw TooLarge();
            // Copy pixels into a new bitmap so input EXIF/text/location metadata cannot leak into the payload.
            var format = options.Format == ScreenshotImageFormat.Jpeg ? PixelFormats.Bgr24 : PixelFormats.Bgra32;
            var converted = new FormatConvertedBitmap(image, format, null, 0);
            var stride = checked((converted.PixelWidth * format.BitsPerPixel + 7) / 8);
            var pixels = new byte[checked(stride * converted.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);
            var clean = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, format, null, pixels, stride);
            BitmapEncoder encoder = options.Format == ScreenshotImageFormat.Png ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = options.JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(clean));
            using var output = new MemoryStream();
            encoder.Save(output);
            ct.ThrowIfCancellationRequested();
            if (output.Length > options.MaxBytes) throw TooLarge();
            var result = capture with
            {
                ImageBytes = output.ToArray(), ContentType = options.Format == ScreenshotImageFormat.Png ? "image/png" : "image/jpeg",
                ImageWidth = clean.PixelWidth, ImageHeight = clean.PixelHeight
            };
            result.Validate();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (ScreenshotSerializationException) { throw; }
        catch (Exception exception) { throw new ScreenshotSerializationException("InvalidImage", "Screenshot encoding failed.", exception); }
    }

    private static ScreenshotSerializationException TooLarge() => new("ImageLimitExceeded", "Screenshot exceeds configured image limits; explicitly adjust limits or resize settings.");
}
