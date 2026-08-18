using Microsoft.Extensions.Logging.Abstractions;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.Storage;
using SkiaSharp;

namespace Msm.Portfolio.Tests;

/// <summary>
/// Covers measuring a photograph and ranking it.
/// </summary>
/// <remarks>
/// Every one of these is about the picture as a picture. There is deliberately nothing
/// here about who is in the frame, because there is deliberately nothing about that in
/// the thing being tested.
/// </remarks>
public class PhotographRankingTests
{
    private static readonly ImageProcessor Processor = new(NullLogger<ImageProcessor>.Instance);

    /// <summary>A real encoded JPEG, so the codec path is genuinely exercised.</summary>
    private static MemoryStream Jpeg(Action<SKCanvas, int, int> draw, int width = 800, int height = 600)
    {
        using var bitmap = new SKBitmap(width, height);

        using (var canvas = new SKCanvas(bitmap))
        {
            draw(canvas, width, height);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);

        var stream = new MemoryStream();
        data.SaveTo(stream);
        stream.Position = 0;

        return stream;
    }

    private static void Detailed(SKCanvas canvas, int width, int height)
    {
        // Hard-edged stripes: plenty of detail for the edge measurement to find.
        canvas.Clear(SKColors.Gray);

        using var paint = new SKPaint { Color = SKColors.White };

        for (var x = 0; x < width; x += 8)
        {
            canvas.DrawRect(x, 0, 4, height, paint);
        }
    }

    [Fact]
    public void A_detailed_photograph_measures_sharper_than_a_flat_one()
    {
        using var detailed = Jpeg(Detailed);
        using var flat = Jpeg((canvas, _, _) => canvas.Clear(SKColors.Gray));

        var withDetail = Processor.Measure(detailed);
        var withoutDetail = Processor.Measure(flat);

        Assert.NotNull(withDetail);
        Assert.NotNull(withoutDetail);
        Assert.True(withDetail.Sharpness > withoutDetail.Sharpness);
    }

    [Fact]
    public void Exposure_follows_how_bright_the_photograph_is()
    {
        using var dark = Jpeg((canvas, _, _) => canvas.Clear(new SKColor(20, 20, 20)));
        using var bright = Jpeg((canvas, _, _) => canvas.Clear(new SKColor(230, 230, 230)));

        var measuredDark = Processor.Measure(dark);
        var measuredBright = Processor.Measure(bright);

        Assert.True(measuredDark!.Exposure < 20);
        Assert.True(measuredBright!.Exposure > 80);
    }

    [Fact]
    public void Detail_lost_to_pure_black_and_pure_white_is_counted()
    {
        using var clipped = Jpeg((canvas, width, height) =>
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(0, 0, width, height / 2f, paint);
        });

        var measured = Processor.Measure(clipped);

        Assert.NotNull(measured);
        Assert.True(measured.Clipping > 80);
    }

    [Fact]
    public void Something_that_is_not_an_image_measures_as_nothing()
    {
        using var nonsense = new MemoryStream("not a photograph"u8.ToArray());

        Assert.Null(Processor.Measure(nonsense));
    }

    [Fact]
    public void A_sharp_well_exposed_photograph_outranks_a_soft_one()
    {
        var sharp = new MediaAsset { Sharpness = 70, Exposure = 48, Contrast = 55, Clipping = 1 };
        var soft = new MediaAsset { Sharpness = 8, Exposure = 48, Contrast = 55, Clipping = 1 };

        Assert.True(PhotographRanking.Score(sharp) > PhotographRanking.Score(soft));
    }

    [Fact]
    public void A_photograph_can_be_too_bright_as_easily_as_too_dark()
    {
        var wellExposed = new MediaAsset { Sharpness = 50, Exposure = 48, Contrast = 50, Clipping = 0 };
        var tooDark = new MediaAsset { Sharpness = 50, Exposure = 8, Contrast = 50, Clipping = 0 };
        var tooBright = new MediaAsset { Sharpness = 50, Exposure = 92, Contrast = 50, Clipping = 0 };

        Assert.True(PhotographRanking.Score(wellExposed) > PhotographRanking.Score(tooDark));
        Assert.True(PhotographRanking.Score(wellExposed) > PhotographRanking.Score(tooBright));
    }

    [Fact]
    public void Losing_detail_to_pure_black_or_white_takes_marks_away()
    {
        var kept = new MediaAsset { Sharpness = 60, Exposure = 48, Contrast = 50, Clipping = 0 };
        var lost = new MediaAsset { Sharpness = 60, Exposure = 48, Contrast = 50, Clipping = 60 };

        Assert.True(PhotographRanking.Score(kept) > PhotographRanking.Score(lost));
    }

    [Fact]
    public void A_photograph_nobody_measured_is_ranked_as_ordinary()
    {
        // Not buried: a library uploaded before measuring existed must still sort
        // sensibly rather than sinking to the bottom in a heap.
        var unmeasured = new MediaAsset();
        var poor = new MediaAsset { Sharpness = 2, Exposure = 4, Contrast = 3, Clipping = 70 };
        var good = new MediaAsset { Sharpness = 85, Exposure = 48, Contrast = 60, Clipping = 0 };

        var score = PhotographRanking.Score(unmeasured);

        Assert.True(score > PhotographRanking.Score(poor));
        Assert.True(score < PhotographRanking.Score(good));
    }

    private static MediaAsset Asset(int sharpness, int order, bool selected = false) => new()
    {
        Id = Guid.CreateVersion7(),
        MediaType = Msm.Portfolio.Web.Domain.Enums.MediaType.Image,
        Sharpness = sharpness,
        Exposure = 48,
        Contrast = 50,
        Clipping = 0,
        DisplayOrder = order,
        IsSelectedForPortfolio = selected
    };

    [Fact]
    public void A_suggestion_leaves_out_what_does_not_stand_up_next_to_the_best()
    {
        // Ticking everything is not a suggestion. The soft frames are precisely what this
        // is meant to save a person from finding by hand.
        var best = Asset(90, 0);
        var alsoGood = Asset(84, 1);
        var soft = Asset(10, 2);

        var suggested = PhotographRanking.Suggest([best, alsoGood, soft], room: 30);

        Assert.Equal([best.Id, alsoGood.Id], suggested);
    }

    [Fact]
    public void The_best_photograph_is_suggested_even_when_it_scores_nothing()
    {
        // Offering nothing at all would leave a retoucher pressing a button that appears
        // broken, so the best of a bad batch still comes through.
        var poor = Asset(0, 0);
        poor.Clipping = 100;

        Assert.Equal(0, PhotographRanking.Score(poor));
        Assert.Equal([poor.Id], PhotographRanking.Suggest([poor], room: 30));
    }

    [Fact]
    public void A_suggestion_never_offers_more_than_there_is_room_for()
    {
        var pool = Enumerable.Range(0, 20).Select(i => Asset(80, i)).ToList();

        var suggested = PhotographRanking.Suggest(pool, room: 4);

        Assert.Equal(4, suggested.Count);
    }

    [Fact]
    public void A_full_portfolio_is_offered_nothing()
    {
        var suggested = PhotographRanking.Suggest([Asset(90, 0)], room: 0);

        Assert.Empty(suggested);
    }

    [Fact]
    public void Photographs_already_on_the_portfolio_are_not_offered_again()
    {
        var onIt = Asset(95, 0, selected: true);
        var notOnIt = Asset(80, 1);

        var suggested = PhotographRanking.Suggest([onIt, notOnIt], room: 30);

        Assert.Equal([notOnIt.Id], suggested);
    }

    [Fact]
    public void Equal_photographs_are_offered_in_the_order_they_are_already_in()
    {
        // The same library has to produce the same suggestion every time.
        var third = Asset(80, 2);
        var first = Asset(80, 0);
        var second = Asset(80, 1);

        var suggested = PhotographRanking.Suggest([third, first, second], room: 30);

        Assert.Equal([first.Id, second.Id, third.Id], suggested);
    }

    [Fact]
    public void A_score_always_lands_between_nothing_and_full_marks()
    {
        var extremes = new[]
        {
            new MediaAsset { Sharpness = 100, Exposure = 48, Contrast = 100, Clipping = 0 },
            new MediaAsset { Sharpness = 0, Exposure = 100, Contrast = 0, Clipping = 100 },
            new MediaAsset { Sharpness = 100, Exposure = 0, Contrast = 100, Clipping = 100 }
        };

        foreach (var asset in extremes)
        {
            var score = PhotographRanking.Score(asset);

            Assert.InRange(score, 0, 100);
        }
    }
}
