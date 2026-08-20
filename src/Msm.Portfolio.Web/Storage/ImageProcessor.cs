using Msm.Portfolio.Web.Domain.Enums;
using SkiaSharp;

namespace Msm.Portfolio.Web.Storage;

/// <summary>What was learned about an image while processing it.</summary>
public record ImageDetails(int Width, int Height, MediaOrientation Orientation);

/// <summary>
/// What can be measured about a photograph without judging what is in it.
/// </summary>
/// <remarks>
/// Every figure here describes the picture as a picture: how much fine detail it holds,
/// how bright it is, how much tonal range it uses, and how much of it has been pushed to
/// pure black or pure white. Nothing here is about the person in the frame, and nothing
/// here should be — sorting people by appearance is not a thing this software does.
/// <para>
/// Each is a percentage, so a stored value is readable on its own.
/// </para>
/// </remarks>
public record ImageQuality(int Sharpness, int Exposure, int Contrast, int Clipping);

/// <summary>A generated web rendition, ready to be written to storage.</summary>
public record GeneratedVariant(MediaVariant Variant, byte[] Content, int Width, int Height)
{
    public string ContentType => "image/jpeg";
}

/// <summary>Everything produced from one pass over an uploaded photograph.</summary>
public record ProcessedImage(IReadOnlyList<GeneratedVariant> Variants, ImageQuality? Quality);

public interface IImageProcessor
{
    /// <summary>Reads dimensions and orientation without altering the image.</summary>
    ImageDetails? Inspect(Stream content);

    /// <summary>
    /// Produces the web renditions from specification section 13, and measures the
    /// photograph, from a single decode. The original is not among the renditions: it is
    /// archived exactly as uploaded.
    /// </summary>
    /// <remarks>
    /// Deliberately one call rather than two. Decoding is by far the most expensive thing
    /// this class does, and a second decode purely to measure the image is what exhausted
    /// a small server's memory and dropped uploads partway through a batch. Anything read
    /// from an uploaded photograph belongs here, on the decode that already happened.
    /// </remarks>
    ProcessedImage Process(Stream content);
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
    /// <summary>
    /// 3000, not more.
    /// </summary>
    /// <remarks>
    /// Two things pin this number. Memory: a decode at 3000 costs about 23MB and the
    /// resize another 23MB, so two uploads at once need roughly 92MB — affordable on a
    /// 512MB container. At 4000 the same pair needs about 143MB, which on top of the
    /// application itself and the uploaded files held in memory is enough to have the
    /// process killed, and a killed process reaches the retoucher as half a batch
    /// uploading and the rest reporting a dropped connection.
    /// <para>
    /// And JPEG only decodes at eighths, so 3000 lands exactly on the half-size step for
    /// a 6000px original — no enlargement anywhere. A target of 4000 asks for two thirds,
    /// gets five eighths, and quietly enlarges the difference.
    /// </para>
    /// <para>
    /// 3000 on the longest edge is 2000px wide for a 2:3 portrait, which is the full
    /// width of a 1920px hero with pixels to spare. That was the point of raising it.
    /// </para>
    /// </remarks>
    public const int LargeEdge = 3000;
    public const int MediumEdge = 1200;
    public const int ThumbnailEdge = 400;

    private static readonly (MediaVariant Variant, int LongestEdge, int Quality)[] Targets =
    [
        (MediaVariant.Large, LargeEdge, 90),
        (MediaVariant.Medium, MediumEdge, 85),
        (MediaVariant.Thumbnail, ThumbnailEdge, 80)
    ];

    /// <summary>
    /// How wide a rendition of this photograph comes out, without generating it.
    /// </summary>
    /// <remarks>
    /// The page needs this to tell a browser which rendition to fetch. The targets apply
    /// to the longest edge, so a portrait photograph's width is a fraction of the number
    /// in the target — the thing that made the hero soft in the first place — and a
    /// srcset written with the target itself would be a lie the browser acts on.
    /// </remarks>
    public static int RenditionWidth(int width, int height, int longestEdge)
    {
        if (width <= 0 || height <= 0)
        {
            return longestEdge;
        }

        var longest = Math.Max(width, height);

        // Never scaled up, so a small original stays its own size.
        return longest <= longestEdge
            ? width
            : (int)Math.Round(width * (double)longestEdge / longest);
    }

    /// <summary>
    /// Bumped whenever the targets above change, so photographs rendered under the old
    /// sizes are rebuilt rather than left as they are.
    /// </summary>
    /// <remarks>
    /// Without this, changing a target only affects photographs uploaded afterwards, and
    /// every portfolio already in the system keeps the renditions it was given — which is
    /// exactly the case that matters, because the complaint always comes from looking at
    /// work already done.
    /// <para>
    /// Version 2 raised Large from 2000px to 3000px on the longest edge. A portrait
    /// photograph at 2000px on its longest edge is only 1500px wide, and the hero is a
    /// full-width band: on a 1920px display that was a 1.6× enlargement, and 3.2× on a
    /// high-density screen. Both are visible as softness, and no amount of quality setting
    /// fixes an enlargement.
    /// </para>
    /// </remarks>
    public const int RenditionVersion = 2;

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

    public ProcessedImage Process(Stream content)
    {
        using var source = DecodeForVariants(content, out var originalWidth, out var originalHeight);

        if (source is null)
        {
            return new ProcessedImage([], null);
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

        return new ProcessedImage(generated, Measure(source));
    }

    /// <summary>
    /// Measures how sharp, how bright and how contrasty a photograph is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the bitmap the renditions were made from rather than the uploaded file, so
    /// nothing is decoded twice. That is not a micro-optimisation: JPEG can be decoded
    /// small almost for free, but PNG cannot be decoded small at all, so a second decode
    /// of a twenty-four megapixel PNG costs another ninety megabytes — with two uploads in
    /// flight, enough to have the process killed and the batch reported as a dropped
    /// connection.
    /// </para>
    /// <para>
    /// Measured on a small copy. Blur, exposure and clipping are all properties of the
    /// whole frame and survive being scaled down.
    /// </para>
    /// <para>
    /// Sharpness is the average difference between each pixel and its neighbours: a
    /// photograph in focus has hard edges and a blurred one does not. It is a measure of
    /// detail, not of subject — a sharp photograph of the wrong thing still scores well,
    /// which is exactly why what this produces is a suggestion for a person to check.
    /// </para>
    /// </remarks>
    internal ImageQuality? Measure(SKBitmap source)
    {
        try
        {
            using var grey = ToSmallGrey(source);

            if (grey is null)
            {
                return null;
            }

            var width = grey.Width;
            var height = grey.Height;
            var pixels = grey.GetPixelSpan();

            if (width < 3 || height < 3 || pixels.Length < width * height)
            {
                return null;
            }

            double total = 0;
            double totalSquared = 0;
            var clipped = 0;
            var count = width * height;

            for (var i = 0; i < count; i++)
            {
                double value = pixels[i];

                total += value;
                totalSquared += value * value;

                // Pure black and pure white hold no detail at all: whatever was there has
                // been lost and cannot be brought back in retouching.
                if (value <= 3 || value >= 252)
                {
                    clipped++;
                }
            }

            var mean = total / count;
            var variance = Math.Max(0, (totalSquared / count) - (mean * mean));

            // The Laplacian: how much each pixel differs from the four around it. Edges
            // are large, flat areas are near zero, so a blurred frame averages low.
            double edges = 0;
            var edgeCount = 0;

            for (var y = 1; y < height - 1; y++)
            {
                var row = y * width;

                for (var x = 1; x < width - 1; x++)
                {
                    var here = pixels[row + x] * 4;
                    var around = pixels[row + x - 1] + pixels[row + x + 1]
                        + pixels[row - width + x] + pixels[row + width + x];

                    edges += Math.Abs(here - around);
                    edgeCount++;
                }
            }

            var sharpness = edgeCount == 0 ? 0 : edges / edgeCount;

            return new ImageQuality(
                // Scaled so an ordinary in-focus photograph lands in the middle of the
                // range rather than at one end, where nothing could be told apart.
                Sharpness: Percent(sharpness / 40.0 * 100.0),
                Exposure: Percent(mean / 255.0 * 100.0),
                Contrast: Percent(Math.Sqrt(variance) / 80.0 * 100.0),
                Clipping: Percent((double)clipped / count * 100.0));
        }
        catch (Exception ex)
        {
            // A photograph that cannot be measured is still a perfectly good photograph;
            // it simply goes unranked.
            logger.LogWarning(ex, "Could not measure an uploaded image.");
            return null;
        }
    }

    private static int Percent(double value) => (int)Math.Round(Math.Clamp(value, 0, 100));

    /// <summary>The longest edge the measurements are taken at.</summary>
    private const int MeasureEdge = 320;

    /// <summary>
    /// A small single-channel copy of an already-decoded photograph.
    /// </summary>
    /// <remarks>
    /// One byte per pixel: every measurement here is about light, so the colour channels
    /// have nothing left to say. Rotation is deliberately not applied — these are all
    /// whole-frame figures, and turning the pixels round would not change any of them.
    /// </remarks>
    private static SKBitmap? ToSmallGrey(SKBitmap source)
    {
        var (width, height) = ScaleToFit(source.Width, source.Height, MeasureEdge);

        // Never upwards: a small photograph is measured at the size it actually is.
        if (width >= source.Width || height >= source.Height)
        {
            return source.Copy(SKColorType.Gray8);
        }

        using var small = source.Resize(
            new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

        return small?.Copy(SKColorType.Gray8);
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
