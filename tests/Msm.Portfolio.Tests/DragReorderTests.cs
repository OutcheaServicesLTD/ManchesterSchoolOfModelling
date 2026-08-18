using Msm.Portfolio.Web.Areas.Retoucher.Controllers;

namespace Msm.Portfolio.Tests;

/// <summary>
/// Covers the order a dragged photograph produces.
/// </summary>
/// <remarks>
/// The page that posts this order is editable by whoever is looking at it and may be
/// several minutes out of date, so the rules here are about what the server refuses to
/// take on trust rather than about dragging itself.
/// </remarks>
public class DragReorderTests
{
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid C = Guid.CreateVersion7();

    [Fact]
    public void A_posted_order_is_used_as_given()
    {
        var result = WorkspaceController.RebuildOrder([C, A, B], [A, B, C]);

        Assert.Equal([C, A, B], result);
    }

    [Fact]
    public void A_photograph_the_browser_left_out_keeps_its_place_at_the_end()
    {
        // A stale page must not be able to drop a photograph from the portfolio simply by
        // failing to mention it.
        var result = WorkspaceController.RebuildOrder([C, A], [A, B, C]);

        Assert.Equal([C, A, B], result);
    }

    [Fact]
    public void Identifiers_that_are_not_on_the_portfolio_are_ignored()
    {
        var stranger = Guid.CreateVersion7();

        var result = WorkspaceController.RebuildOrder([stranger, B, A], [A, B]);

        Assert.Equal([B, A], result);
    }

    [Fact]
    public void A_repeated_identifier_is_counted_once()
    {
        var result = WorkspaceController.RebuildOrder([B, B, A], [A, B]);

        Assert.Equal([B, A], result);
    }

    [Fact]
    public void Nothing_posted_leaves_the_order_alone()
    {
        var result = WorkspaceController.RebuildOrder([], [A, B, C]);

        Assert.Equal([A, B, C], result);
    }

    [Fact]
    public void Every_photograph_survives_whatever_is_posted()
    {
        var result = WorkspaceController.RebuildOrder([C, C, Guid.CreateVersion7()], [A, B, C]);

        Assert.Equal(3, result.Count);
        Assert.Contains(A, result);
        Assert.Contains(B, result);
        Assert.Contains(C, result);
    }
}
