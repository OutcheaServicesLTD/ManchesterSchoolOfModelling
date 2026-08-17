using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;

namespace Msm.Portfolio.Web.Services;

public record StaffMember(
    Guid UserId,
    string Name,
    string Email,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public record CreateStaffResult(bool Succeeded, string? Error = null, string? GeneratedPassword = null);

/// <summary>
/// Staff account management, reserved to the Super Admin (specification section 4).
/// </summary>
public interface IStaffService
{
    Task<IReadOnlyList<StaffMember>> GetStaffAsync(CancellationToken cancellationToken = default);

    Task<CreateStaffResult> CreateAsync(
        string email, string firstName, string lastName, string role, Guid? actingUserId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> SetActiveAsync(
        Guid userId, bool active, Guid? actingUserId, CancellationToken cancellationToken = default);

    /// <summary>Issues a new one-time password for a staff member who has lost access.</summary>
    Task<(bool Succeeded, string? Password, string? Error)> ResetPasswordAsync(
        Guid userId, Guid? actingUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetPermissionsAsync(string role, CancellationToken cancellationToken = default);

    Task<OperationResult> SetPermissionsAsync(
        string role, IReadOnlyList<string> permissions, Guid? actingUserId,
        CancellationToken cancellationToken = default);
}

public class StaffService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IAuditService audit,
    ILogger<StaffService> logger) : IStaffService
{
    public async Task<IReadOnlyList<StaffMember>> GetStaffAsync(CancellationToken cancellationToken = default)
    {
        var rows = await (
            from user in db.Users
            join userRole in db.UserRoles on user.Id equals userRole.UserId
            join role in db.Roles on userRole.RoleId equals role.Id
            where role.Name != null && Roles.Staff.Contains(role.Name)
            select new { user.Id, user.FirstName, user.LastName, user.Email, user.IsActive, user.CreatedAt, user.LastLoginAt, RoleName = role.Name! })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .Select(r => new StaffMember(
                    r.Id,
                    string.IsNullOrWhiteSpace($"{r.FirstName}{r.LastName}")
                        ? r.Email ?? r.Id.ToString()
                        : $"{r.FirstName} {r.LastName}".Trim(),
                    r.Email ?? string.Empty,
                    r.RoleName,
                    r.IsActive,
                    r.CreatedAt,
                    r.LastLoginAt))
                .OrderBy(s => s.Role)
                .ThenBy(s => s.Name)
        ];
    }

    public async Task<CreateStaffResult> CreateAsync(
        string email,
        string firstName,
        string lastName,
        string role,
        Guid? actingUserId,
        CancellationToken cancellationToken = default)
    {
        // Only staff roles can be created here. Creating a Client this way would produce
        // an account with no profile, and a Super Admin is never created from the UI.
        if (role != Roles.Admin && role != Roles.Retoucher)
        {
            return new CreateStaffResult(false, "Only Admin and Retoucher accounts can be created here.");
        }

        var normalised = email.Trim();

        if (await userManager.FindByEmailAsync(normalised) is not null)
        {
            return new CreateStaffResult(false, "An account already exists with that email address.");
        }

        var user = new ApplicationUser
        {
            UserName = normalised,
            Email = normalised,
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            EmailConfirmed = true,
            IsActive = true
        };

        var password = GeneratePassword();
        var created = await userManager.CreateAsync(user, password);

        if (!created.Succeeded)
        {
            return new CreateStaffResult(false,
                string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        await userManager.AddToRoleAsync(user, role);

        audit.Record(nameof(ApplicationUser), user.Id.ToString(), AuditActions.StaffAccountCreated,
            userId: actingUserId, newValue: $"{normalised} as {role}");

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Staff account {Email} created as {Role}.", normalised, role);

        // Returned once, for the Super Admin to pass on. It is not stored anywhere in
        // readable form; a lost password is replaced rather than recovered.
        return new CreateStaffResult(true, GeneratedPassword: password);
    }

    public async Task<OperationResult> SetActiveAsync(
        Guid userId, bool active, Guid? actingUserId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return OperationResult.Fail("That account could not be found.");
        }

        // Locking yourself out of the only unrestricted account would leave nobody able
        // to manage staff at all.
        if (!active && userId == actingUserId)
        {
            return OperationResult.Fail("You cannot disable your own account.");
        }

        if (!active && await userManager.IsInRoleAsync(user, Roles.SuperAdmin))
        {
            return OperationResult.Fail("A Super Admin account cannot be disabled here.");
        }

        user.IsActive = active;

        audit.Record(nameof(ApplicationUser), userId.ToString(),
            active ? AuditActions.StaffAccountEnabled : AuditActions.StaffAccountDisabled,
            userId: actingUserId, newValue: user.Email);

        await db.SaveChangesAsync(cancellationToken);

        return OperationResult.Ok();
    }

    public async Task<(bool Succeeded, string? Password, string? Error)> ResetPasswordAsync(
        Guid userId, Guid? actingUserId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());

        if (user is null)
        {
            return (false, null, "That account could not be found.");
        }

        var password = GeneratePassword();
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, password);

        if (!result.Succeeded)
        {
            return (false, null, string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        audit.Record(nameof(ApplicationUser), userId.ToString(), AuditActions.StaffPasswordReset,
            userId: actingUserId, newValue: user.Email);

        await db.SaveChangesAsync(cancellationToken);

        return (true, password, null);
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(
        string role, CancellationToken cancellationToken = default)
    {
        var entity = await roleManager.FindByNameAsync(role);

        if (entity is null)
        {
            return [];
        }

        var claims = await roleManager.GetClaimsAsync(entity);

        return [.. claims.Where(c => c.Type == Permissions.ClaimType).Select(c => c.Value)];
    }

    public async Task<OperationResult> SetPermissionsAsync(
        string role,
        IReadOnlyList<string> permissions,
        Guid? actingUserId,
        CancellationToken cancellationToken = default)
    {
        if (role == Roles.SuperAdmin)
        {
            return OperationResult.Fail("Super Admin permissions cannot be changed.");
        }

        var entity = await roleManager.FindByNameAsync(role);

        if (entity is null)
        {
            return OperationResult.Fail("That role could not be found.");
        }

        var known = Permissions.All().ToHashSet(StringComparer.Ordinal);
        var requested = permissions.Where(known.Contains).ToHashSet(StringComparer.Ordinal);

        // Capabilities reserved to the Super Admin are stripped rather than rejected,
        // so a tampered form cannot escalate another role (specification section 4).
        var reserved = requested.Intersect(Permissions.SuperAdminOnly).ToList();
        foreach (var permission in reserved)
        {
            requested.Remove(permission);
        }

        if (reserved.Count > 0)
        {
            logger.LogWarning(
                "Refused to grant Super Admin-only permission(s) to {Role}: {Permissions}",
                role, string.Join(", ", reserved));
        }

        var existing = await roleManager.GetClaimsAsync(entity);
        var current = existing.Where(c => c.Type == Permissions.ClaimType).ToList();

        foreach (var claim in current.Where(c => !requested.Contains(c.Value)))
        {
            await roleManager.RemoveClaimAsync(entity, claim);
        }

        foreach (var permission in requested.Where(p => current.All(c => c.Value != p)))
        {
            await roleManager.AddClaimAsync(entity, new Claim(Permissions.ClaimType, permission));
        }

        audit.Record(nameof(ApplicationRole), entity.Id.ToString(), AuditActions.PermissionsChanged,
            userId: actingUserId,
            oldValue: string.Join(", ", current.Select(c => c.Value).Order()),
            newValue: string.Join(", ", requested.Order()));

        await db.SaveChangesAsync(cancellationToken);

        return OperationResult.Ok();
    }

    /// <summary>
    /// A random password meeting the configured complexity rules, shown once to the
    /// Super Admin. Staff are expected to change it after signing in.
    /// </summary>
    private static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*";

        var characters = new List<char>
        {
            Pick(upper), Pick(lower), Pick(digits), Pick(symbols)
        };

        const string all = upper + lower + digits + symbols;
        while (characters.Count < 14)
        {
            characters.Add(Pick(all));
        }

        // Shuffled so the guaranteed character classes are not always in the same
        // positions, which would narrow the search space.
        return new string([.. characters.OrderBy(_ => System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue))]);

        static char Pick(string source) =>
            source[System.Security.Cryptography.RandomNumberGenerator.GetInt32(source.Length)];
    }
}
