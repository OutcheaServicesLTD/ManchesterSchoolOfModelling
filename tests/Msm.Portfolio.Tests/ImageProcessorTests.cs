using Microsoft.Extensions.Logging.Abstractions;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Storage;
using SkiaSharp;

namespace Msm.Portfolio.Tests;

public class ImageProcessorTests
{
    private static readonly ImageProcessor Processor = new(NullLogger<ImageProcessor>.Instance);

    /// <summary>Builds a real encoded JPEG so the codec path is genuinely exercised.</summary>
    private static MemoryStream MakeJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 4f, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);

        var stream = new MemoryStream();
        data.SaveTo(stream);
        stream.Position = 0;

        return stream;
    }

    [Fact]
    public void Inspect_reads_dimensions_from_a_real_jpeg()
    {
        using var jpeg = MakeJpeg(1600, 1200);

        var details = Processor.Inspect(jpeg);

        Assert.NotNull(details);
        Assert.Equal(1600, details!.Width);
        Assert.Equal(1200, details.Height);
    }

    [Theory]
    [InlineData(1200, 1600, MediaOrientation.Portrait)]
    [InlineData(1600, 1200, MediaOrientation.Landscape)]
    [InlineData(1000, 1000, MediaOrientation.Square)]
    public void Orientation_is_recorded_for_both_portrait_and_landscape(
        int width, int height, MediaOrientation expected)
    {
        using var jpeg = MakeJpeg(width, height);

        Assert.Equal(expected, Processor.Inspect(jpeg)!.Orientation);
    }

    [Fact]
    public void Generates_the_three_web_renditions()
    {
        using var jpeg = MakeJpeg(3000, 2000);

        var variants = Processor.Process(jpeg).Variants;

        Assert.Equal(3, variants.Count);
        Assert.Contains(variants, v => v.Variant == MediaVariant.Large);
        Assert.Contains(variants, v => v.Variant == MediaVariant.Medium);
        Assert.Contains(variants, v => v.Variant == MediaVariant.Thumbnail);
        Assert.All(variants, v => Assert.NotEmpty(v.Content));

        // The original itself is archived untouched and is never a generated rendition.
        Assert.DoesNotContain(variants, v => v.Variant == MediaVariant.Original);
    }

    /// <summary>
    /// Specification section 13 forbids destructive cropping, so every rendition must
    /// keep the original proportions.
    /// </summary>
    [Theory]
    [InlineData(3000, 2000)]
    [InlineData(2000, 3000)]
    [InlineData(2400, 2400)]
    public void Renditions_preserve_the_original_aspect_ratio(int width, int height)
    {
        using var jpeg = MakeJpeg(width, height);
        var sourceRatio = (double)width / height;

        foreach (var variant in Processor.Process(jpeg).Variants)
        {
            var ratio = (double)variant.Width / variant.Height;

            // Tolerance covers rounding to whole pixels only.
            Assert.True(
                Math.Abs(ratio - sourceRatio) < 0.01,
                $"{variant.Variant} changed the aspect ratio from {sourceRatio:F3} to {ratio:F3}.");
        }
    }

    [Fact]
    public void A_tall_portrait_is_bounded_by_its_longest_edge()
    {
        using var jpeg = MakeJpeg(1000, 4000);

        var thumbnail = Processor.Process(jpeg).Variants.Single(v => v.Variant == MediaVariant.Thumbnail);

        Assert.Equal(400, thumbnail.Height);
        Assert.Equal(100, thumbnail.Width);
    }

    /// <summary>
    /// Enlarging a small original would add file size without adding detail, and would
    /// misrepresent the photograph's real resolution.
    /// </summary>
    [Fact]
    public void A_small_original_is_never_scaled_up()
    {
        using var jpeg = MakeJpeg(300, 200);

        foreach (var variant in Processor.Process(jpeg).Variants)
        {
            Assert.True(variant.Width <= 300, $"{variant.Variant} was enlarged to {variant.Width}px wide.");
            Assert.True(variant.Height <= 200, $"{variant.Variant} was enlarged to {variant.Height}px tall.");
        }
    }

    [Fact]
    public void A_file_that_is_not_an_image_is_rejected_rather_than_throwing()
    {
        using var notAnImage = new MemoryStream("this is not an image"u8.ToArray());

        Assert.Null(Processor.Inspect(notAnImage));
    }

    [Fact]
    public void A_truncated_image_is_rejected_rather_than_throwing()
    {
        using var jpeg = MakeJpeg(800, 600);
        var truncated = new MemoryStream(jpeg.ToArray()[..40]);

        Assert.Null(Processor.Inspect(truncated));
    }

    [Theory]
    [InlineData(4000, 3000, 2000, 2000, 1500)]
    [InlineData(3000, 4000, 2000, 1500, 2000)]
    [InlineData(800, 600, 2000, 800, 600)]
    public void ScaleToFit_bounds_the_longest_edge(
        int width, int height, int longestEdge, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), ImageProcessor.ScaleToFit(width, height, longestEdge));
    }

    [Fact]
    public void ScaleToFit_never_rounds_an_edge_to_zero()
    {
        var (width, height) = ImageProcessor.ScaleToFit(10000, 20, 400);

        Assert.Equal(400, width);
        Assert.True(height >= 1);
    }

    // ---------- WebP (specification version 2, item 1) ----------

    [Fact]
    public void A_jpeg_rendition_re_encodes_as_a_smaller_webp()
    {
        using var jpeg = MakeJpeg(1200, 800);

        var webp = Processor.ToWebp(jpeg.ToArray());

        Assert.NotNull(webp);
        // "RIFF....WEBP" — the container format's own signature, not this class's
        // opinion of what a WebP file looks like.
        Assert.Equal("RIFF"u8.ToArray(), webp![..4]);
        Assert.Equal("WEBP"u8.ToArray(), webp[8..12]);
        // A photograph re-encoded at a slightly lower quality into a format built for
        // it should not come out larger than the JPEG it started from.
        Assert.True(webp.Length < jpeg.Length);
    }

    [Fact]
    public void Bytes_that_are_not_an_image_yield_no_webp()
    {
        Assert.Null(Processor.ToWebp("this is not an image"u8.ToArray()));
    }
}
