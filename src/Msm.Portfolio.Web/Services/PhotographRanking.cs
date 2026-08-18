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
    /// How close to the best of the batch a photograph has to be to be suggested.
    /// </summary>
    /// <remarks>
    /// Judged against the rest of the shoot rather than against a number invented here.
    /// An absolute pass mark either lets everything through on a good shoot or nothing
    /// through on a difficult one, and "difficult" includes plenty of deliberate
    /// choices — a low-key editorial set is not a set of bad photographs.
    /// </remarks>
    private const double CloseEnough = 0.7;

    /// <summary>
    /// The photographs to offer as a starting selection, best first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only what is not already on the portfolio, never more than there is room for, and
    /// only what stands up next to the best of what was uploaded — a suggestion that
    /// ticks everything is not a suggestion, and the soft and blown frames are exactly
    /// the ones this is meant to save a person from finding by hand.
    /// </para>
    /// <para>
    /// The best photograph is always included, even in a poor batch. Offering nothing at
    /// all would leave a retoucher pressing a button that does not appear to work.
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

        var candidates = pool
            .Where(a => a.MediaType == MediaType.Image && !a.IsDeleted && !a.IsSelectedForPortfolio)
            .OrderByDescending(Score)
            .ThenBy(a => a.DisplayOrder)
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        var floor = Score(candidates[0]) * CloseEnough;

        return
        [
            .. candidates
                .Where((asset, index) => index == 0 || Score(asset) >= floor)
                .Take(room)
                .Select(a => a.Id)
        ];
    }
}
