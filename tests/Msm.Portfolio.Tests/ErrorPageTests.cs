using Msm.Portfolio.Web.Models;

namespace Msm.Portfolio.Tests;

/// <summary>
/// The page every wrong address lands on. It used to be the framework's scaffold, which
/// explained ASPNETCORE_ENVIRONMENT to whoever mistyped a portfolio link — and before the
/// status code pages middleware was added, a 404 had no page at all and the browser
/// showed its own.
/// </summary>
public class ErrorPageTests
{
    [Theory]
    [InlineData(404, "We cannot find that page")]
    [InlineData(403, "That page is not yours to see")]
    [InlineData(401, "Please sign in first")]
    [InlineData(500, "Something went wrong at our end")]
    public void Each_kind_of_wrong_address_says_what_happened(int status, string heading)
    {
        var model = new ErrorViewModel { StatusCode = status };

        Assert.Equal(heading, model.Heading);
        Assert.NotEmpty(model.Explanation);
    }

    /// <summary>
    /// A trace identifier is for support to find a fault in the log. Printing one to
    /// somebody who simply typed an address wrongly turns an ordinary mistake into
    /// something that looks like a crash.
    /// </summary>
    [Fact]
    public void A_wrong_address_is_not_given_a_reference_number()
    {
        var model = new ErrorViewModel { StatusCode = 404, RequestId = "trace-1" };

        Assert.False(model.ShowRequestId);
    }

    [Fact]
    public void A_fault_is_given_one()
    {
        Assert.True(new ErrorViewModel { StatusCode = 500, RequestId = "trace-1" }.ShowRequestId);

        // Thrown to by the exception handler, which sets no status on the model.
        Assert.True(new ErrorViewModel { RequestId = "trace-1" }.ShowRequestId);
    }

    /// <summary>
    /// Nothing here may read as the visitor's fault. A portfolio address that has stopped
    /// working is usually a portfolio that came down.
    /// </summary>
    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public void The_wording_never_blames_the_visitor(int status)
    {
        var model = new ErrorViewModel { StatusCode = status };
        var wording = model.Heading + " " + model.Explanation;

        // "Nothing you did caused it" is reassurance, not blame, so the check is for the
        // words that actually accuse: the visitor's fault, or their input called wrong.
        foreach (var blame in new[] { "your fault", "you caused", "invalid", "illegal", "bad request" })
        {
            Assert.DoesNotContain(blame, wording, StringComparison.OrdinalIgnoreCase);
        }
    }
}
