using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;

namespace Msm.Portfolio.Web.Services;

/// <summary>
/// Resolves the signed-in client's own profile.
/// </summary>
/// <remarks>
/// Specification section 35 requires that a client changing an id in the URL can never
/// reach another client's data. Rather than accepting an id and checking ownership
/// afterwards, the client area takes no id at all and looks the profile up from the
/// authenticated principal, so there is no identifier for a client to tamper with.
/// </remarks>
public interface IClientProfileAccessor
{
    Task<ClientProfile?> GetCurrentAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

public class ClientProfileAccessor(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager) : IClientProfileAccessor
{
    public async Task<ClientProfile?> GetCurrentAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = userManager.GetUserId(user);

        if (!Guid.TryParse(userId, out var id))
        {
            return null;
        }

        return await db.ClientProfiles
            .Include(c => c.Portfolio)
            .Include(c => c.GuardianConsent)
            .Include(c => c.Measurements)
            .FirstOrDefaultAsync(c => c.ApplicationUserId == id, cancellationToken);
    }
}
