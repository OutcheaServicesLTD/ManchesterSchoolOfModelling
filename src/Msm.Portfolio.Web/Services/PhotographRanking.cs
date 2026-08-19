using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Services;

/// <summary>
/// Puts a client's photographs in an order worth starting from.
/// </summary>
/// <remarks>
/// <para>
/// A retoucher works from up to sixty photographs and publishes at most thirty. Reading
/// every one at full size to find the soft frames and the blown ones is the dullest part
/// of the job, and it is the part a computer can genuinely help with, because "in focus"
/// and "not blown out" are measurable.
/// </para>
/// <para>
/// What is deliberately not measured: anything about the person in the photograph. No
/// face detection, no expression, no pose, no judgement of who looks better in which
/// frame. Those are the retoucher's decisions and a model's livelihood, and a score out
/// of a hundred is no basis for either. This ranks pictures on whether they are
/// technically sound, and it is offered as a starting point that a person then changes.
/// </para>
/// <para>
/// A photograph that was never measured is not penalised into last place for it: it is
/// ranked as ordinary, so a library uploaded before measuring existed still sorts
/// sensibly by the order it was put in.
/// </para>
/// <para>
/// The whole rule lives here, and the page only ticks what this chose. Splitting it — a
/// score here, a threshold in a script — would be two rules that drift apart, and the
/// one nobody could test would be the one making the decision.
/// </para>
/// </remarks>
public static class PhotographRanking
{
    /// <summary>What an unmeasured photograph scores: neither promoted nor buried.</summary>
    internal const int Unmeasured = 50;

    /// <summary>
    /// How technically sound a photograph is, out of 100.
    /// </summary>
    public static int Score(MediaAsset asset)
    {
        if (!asset.HasBeenMeasured)
        {
            return Unmeasured;
        }

        // Sharpness carries the most weight, because a soft frame cannot be rescued and
        // an underexposed one usually can.
        var sharpness = Math.Clamp(asset.Sharpness ?? 0, 0, 100) * 0.5;

        // Exposure is judged by distance from a well-exposed middle rather than by "more
        // is better": a photograph can be too bright just as easily as too dark.
        var exposure = (100 - (Math.Abs((asset.Exposure ?? 50) - 48) * 2.5)) * 0.25;

        // Some tonal range, but a high-key studio portrait against white is a legitimate
        // photograph with low contrast, so this is worth little.
        var contrast = Math.Clamp(asset.Contrast ?? 0, 0, 100) * 0.15;

        // Detail pushed to pure black or pure white is gone for good, so this only ever
        // takes marks away.
        var clipping = Math.Clamp(asset.Clipping ?? 0, 0, 100) * 0.6;

        return (int)Math.Round(Math.Clamp(sharpness + Math.Max(0, exposure) + contrast - clipping, 0, 100));
    }

    /// <summary>
    /// The photographs to offer as a starting selection, best first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fills the portfolio: the best of what is not already on it, up to the number of
    /// places left. A retoucher with fifty photographs and thirty places wants those
    /// thirty chosen and then to prune, not a shortlist of five — the ranking decides the
    /// order to work through, and the person decides what actually goes.
    /// </para>
    /// <para>
    /// No quality threshold. One was tried, and on a real shoot — fifty frames of the same
    /// set-up, all properly lit — it cut a portfolio of thirty down to five, because the
    /// photographs genuinely were of a piece and the ones just behind the best were fine.
    /// Sorting by quality is worth doing; refusing to offer the twenty-ninth best
    /// photograph for a portfolio with thirty places is not.
    /// </para>
    /// <para>
    /// Ties are broken by the order the photographs are already in, so the same library
    /// always produces the same suggestion.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Guid> Suggest(IEnumerable<MediaAsset> pool, int room)
    {
        if (room <= 0)
        {
            return [];
        }

        return
        [
            .. pool
                .Where(a => a.MediaType == MediaType.Image && !a.IsDeleted && !a.IsSelectedForPortfolio)
                .OrderByDescending(Score)
                .ThenBy(a => a.DisplayOrder)
                .Take(room)
                .Select(a => a.Id)
        ];
    }
}
