using Msm.Portfolio.Web.Domain.Enums;
using SkiaSharp;

namespace Msm.Portfolio.Web.Storage;

/// <summary>What was learned about an image while processing it.</summary>
public record ImageDetails(int Width, int Height, MediaOrientation Orientation);

/// <summary>A generated web rendition, ready to be written to storage.</summary>
public record GeneratedVariant(MediaVariant Variant, byte[] Content, int Width, int Height)
{
    public string ContentType => "image/jpeg";
}

public interface IImageProcessor
{
    /// <summary>Reads dimensions and orientation without altering the image.</summary>
    ImageDetails? Inspect(Stream content);

    /// <summary>
    /// Produces the web renditions from specification section 13. The original is not
    /// among them: it is archived exactly as uploaded.
    /// </summary>
    IReadOnlyList<GeneratedVariant> GenerateVariants(Stream content);
}

/// <summary>
/// Image handling built on SkiaSharp.
/// </summary>
/// <remarks>
/// SkiaSharp is MIT licensed. ImageSharp, the more common choice, requires a paid
/// commercial licence above a revenue threshold, which would be a licensing liability
/// for a commercial product like this one.
/// </remarks>
public class ImageProcessor(ILogger<ImageProcessor> logger) : IImageProcessor
{
    /// <summary>
    /// Longest-edge targets. Applied to the longest edge rather than to width, so a
    /// portrait and a landscape photograph are reduced by a comparable amount
    /// (specification section 13 requires both to be supported).
    /// </summary>
    private static readonly (MediaVariant Variant, int LongestEdge, int Quality)[] Targets =
    [
        (MediaVariant.Large, 2000, 88),
        (MediaVariant.Medium, 1200, 85),
        (MediaVariant.Thumbnail, 400, 80)
    ];

    /// <summary>
    /// Reads the header only. A modern camera file is around 6000 by 4000 pixels, which
    /// is 96MB once decoded — reading dimensions by decoding it would cost that much to
    /// learn two numbers, and this runs before the image is even accepted.
    /// </summary>
    public ImageDetails? Inspect(Stream content)
    {
        try
        {
            using var codec = SKCodec.Create(new SKManagedStream(content));

            if (codec is null)
            {
                return null;
            }

            var (width, height) = Dimensions(codec.Info.Width, codec.Info.Height, codec.EncodedOrigin);

            return new ImageDetails(width, height, Classify(width, height));
        }
        catch (Exception ex)
        {
            // A corrupt or non-image upload must not take the request down; the caller
            // reports it as a rejected file.
            logger.LogWarning(ex, "Could not read an uploaded image.");
            return null;
        }
    }

    public IReadOnlyList<GeneratedVariant> GenerateVariants(Stream content)
    {
        using var source = DecodeForVariants(content, out var originalWidth, out var originalHeight);

        if (source is null)
        {
            return [];
        }

        var generated = new List<GeneratedVariant>(Targets.Length);

        foreach (var (variant, longestEdge, quality) in Targets)
        {
            // Measured against the original, not the decoded copy, so "never scale up"
            // still means what it says when the decode was downsampled.
            var (width, height) = ScaleToFit(originalWidth, originalHeight, longestEdge);

            // Never scale up. Enlarging a small original would add file size without
            // adding detail, and would misrepresent the photograph's real resolution.
            if (width >= originalWidth && height >= originalHeight)
            {
                width = Math.Min(originalWidth, source.Width);
                height = Math.Min(originalHeight, source.Height);
            }

            using var resized = source.Resize(
                new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            if (resized is null)
            {
                logger.LogWarning("Could not resize an image to {Variant}.", variant);
                continue;
            }

            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);

            generated.Add(new GeneratedVariant(variant, data.ToArray(), width, height));
        }

        return generated;
    }

    /// <summary>
    /// Fits the image inside a square of the given edge while preserving its aspect
    /// ratio. Nothing is cropped, so a full-length portrait keeps its whole frame
    /// (specification section 13).
    /// </summary>
    internal static (int Width, int Height) ScaleToFit(int width, int height, int longestEdge)
    {
        if (width <= 0 || height <= 0)
        {
            return (1, 1);
        }

        var longest = Math.Max(width, height);

        if (longest <= longestEdge)
        {
            return (width, height);
        }

        var scale = (double)longestEdge / longest;

        // Never round an edge down to zero on an extreme aspect ratio.
        return (Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
    }

    internal static MediaOrientation Classify(int width, int height)
    {
        if (width == height)
        {
            return MediaOrientation.Square;
        }

        return width > height ? MediaOrientation.Landscape : MediaOrientation.Portrait;
    }

    /// <summary>
    /// Decodes only as much of the image as the renditions actually need.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A full-resolution decode of a professional camera file is roughly 96MB of pixels
    /// — for a set of renditions whose largest edge is 2000 pixels. Uploading a handful
    /// at once exhausted a small container's memory, and the process being killed
    /// reached the retoucher as "the connection dropped" partway through a batch.
    /// </para>
    /// <para>
    /// JPEG can be decoded at a fraction of its stored size almost for free, so this
    /// asks the codec for the smallest size that still covers the largest rendition.
    /// The result is visually identical and costs a fraction of the memory.
    /// </para>
    /// </remarks>
    private SKBitmap? DecodeForVariants(Stream content, out int originalWidth, out int originalHeight)
    {
        originalWidth = 0;
        originalHeight = 0;

        try
        {
            using var codec = SKCodec.Create(new SKManagedStream(content));

            if (codec is null)
            {
                return null;
            }

            (originalWidth, originalHeight) =
                Dimensions(codec.Info.Width, codec.Info.Height, codec.EncodedOrigin);

            var largestEdge = Targets.Max(t => t.LongestEdge);
            var storedLongest = Math.Max(codec.Info.Width, codec.Info.Height);

            // Only ever downwards, and never below what the largest rendition needs.
            var desired = storedLongest > largestEdge
                ? (float)largestEdge / storedLongest
                : 1f;

            var scaled = codec.GetScaledDimensions(desired);
            var info = codec.Info.WithSize(scaled.Width, scaled.Height)
                                 .WithColorType(SKColorType.Rgba8888)
                                 .WithAlphaType(SKAlphaType.Premul);

            var bitmap = new SKBitmap(info);

            var result = codec.GetPixels(info, bitmap.GetPixels());

            // IncompleteInput means a truncated file that still decoded usefully, so it
            // is accepted; anything else is a decode failure.
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                bitmap.Dispose();
                logger.LogWarning("Could not decode an uploaded image.");
                return null;
            }

            return ApplyOrientation(bitmap, codec.EncodedOrigin);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not decode an uploaded image.");
            return null;
        }
    }

    /// <summary>
    /// The dimensions as they will be seen, accounting for an EXIF rotation that swaps
    /// width and height. Without it a phone photograph shot in portrait is recorded as
    /// landscape and laid out in the wrong column.
    /// </summary>
    private static (int Width, int Height) Dimensions(int width, int height, SKEncodedOrigin origin)
    {
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        return swaps ? (height, width) : (width, height);
    }

    /// <summary>
    /// Decodes to a bitmap, applying any EXIF rotation. Without this a photograph shot
    /// on a phone in portrait would be stored sideways and its orientation recorded
    /// wrongly.
    /// </summary>
    private SKBitmap? Decode(Stream content)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        try
        {
            using var codec = SKCodec.Create(new SKManagedStream(content));

            if (codec is null)
            {
                return null;
            }

            var bitmap = SKBitmap.Decode(codec);

            return bitmap is null ? null : ApplyOrientation(bitmap, codec.EncodedOrigin);
        }
        catch (Exception ex)
        {
            // A corrupt or non-image upload must not take the request down; the caller
            // reports it as a rejected file.
            logger.LogWarning(ex, "Could not decode an uploaded image.");
            return null;
        }
    }

    private static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft)
        {
            return bitmap;
        }

        var swapsDimensions = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var width = swapsDimensions ? bitmap.Height : bitmap.Width;
        var height = swapsDimensions ? bitmap.Width : bitmap.Height;

        var rotated = new SKBitmap(width, height);
        using var canvas = new SKCanvas(rotated);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Scale(-1, 1, width / 2f, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.RotateDegrees(180, width / 2f, height / 2f);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Scale(1, -1, 1, height / 2f);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1, width / 2f, height / 2f);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(0, height);
                canvas.RotateDegrees(270);
                canvas.Scale(1, -1, width / 2f, height / 2f);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, height);
                canvas.RotateDegrees(270);
                break;
        }

        canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        bitmap.Dispose();

        return rotated;
    }
}
