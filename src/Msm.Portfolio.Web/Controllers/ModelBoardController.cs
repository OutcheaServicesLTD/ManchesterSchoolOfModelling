using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Controllers;

/// <summary>
/// The public Model Board (specification sections 18, 34 and 47).
/// </summary>
[AllowAnonymous]
public class ModelBoardController(
    IPublicPortfolioService portfolios,
    IOptions<MsmBrandOptions> brandOptions) : Controller
{
    [HttpGet("/models")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        return View(new ModelBoardViewModel
        {
            Cards = [.. await portfolios.GetModelBoardAsync(cancellationToken)],
            Brand = brandOptions.Value
        });
    }
}
