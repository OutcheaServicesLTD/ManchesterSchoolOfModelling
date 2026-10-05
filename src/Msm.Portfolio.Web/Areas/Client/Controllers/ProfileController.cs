using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Client.Controllers;

/// <summary>
/// The client editing their own profile and measurements (specification section 17).
/// </summary>
/// <remarks>
/// Takes no client id in any route. The profile is resolved from the signed-in user, so
/// there is nothing for a client to change in the URL to reach someone else's record
/// (specification section 35).
/// </remarks>
[Area("Client")]
[Route("client/profile")]
[Authorize(Policy = Policies.ClientArea)]
public class ProfileController(
    IClientProfileAccessor profiles,
    IMeasurementTemplateProvider templates,
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IAuditService audit) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        var model = new ClientProfileViewModel
        {
            FirstName = client.FirstName,
            LastName = client.LastName,
            DisplayName = client.DisplayName,
            DateOfBirth = client.DateOfBirth,
            Location = client.Location,
            ModelProfileType = client.ModelProfileType,
            HairColour = client.HairColour,
            EyeColour = client.EyeColour,
            Biography = client.Biography,
            InstagramUrl = client.InstagramUrl,
            TikTokUrl = client.TikTokUrl
        };

        PrepareTemplate(model, client);

        return View(model);
    }

    [HttpPost("")]
    public async Task<IActionResult> Index(
        ClientProfileViewModel model,
        CancellationToken cancellationToken = default)
    {
        var client = await profiles.GetCurrentAsync(User, cancellationToken);

        if (client is null)
        {
            return View("NoProfile");
        }

        // The profile type governs which measurements are required, so it is read from
        // the stored record rather than the post: a client cannot dodge a required
        // measurement by submitting a different type.
        model.ModelProfileType = client.ModelProfileType;

        PrepareTemplate(model, client, useSubmittedValues: true);
        TryValidateModel(model);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var before = $"{client.FullName}, {client.Location}, DOB {client.DateOfBirth}";

        client.FirstName = model.FirstName.Trim();
        client.LastName = model.LastName.Trim();
        client.DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim();
        client.DateOfBirth = model.DateOfBirth;
        client.Location = model.Location?.Trim();
        client.HairColour = model.HairColour?.Trim();
        client.EyeColour = model.EyeColour?.Trim();
        client.Biography = model.Biography?.Trim();
        client.InstagramUrl = model.InstagramUrl?.Trim();
        client.TikTokUrl = model.TikTokUrl?.Trim();
        client.UpdatedAt = DateTimeOffset.UtcNow;

        UpdateMeasurements(client, model);

        // Keep the sign-in name aligned with the profile so staff screens and the audit
        // log do not show a stale name.
        var user = await userManager.GetUserAsync(User);
        if (user is not null)
        {
            user.FirstName = client.FirstName;
            user.LastName = client.LastName;
        }

        var userId = user?.Id;
        audit.Record(nameof(ClientProfile), client.Id.ToString(), AuditActions.ProfileEdited,
            userId: userId,
            oldValue: before,
            newValue: $"{client.FullName}, {client.Location}, DOB {client.DateOfBirth}");

        await db.SaveChangesAsync(cancellationToken);

        TempData["Saved"] = true;
        return RedirectToAction(nameof(Index));
    }

    private void PrepareTemplate(
        ClientProfileViewModel model,
        ClientProfile client,
        bool useSubmittedValues = false)
    {
        var template = templates.GetTemplate(client.ModelProfileType);
        model.Template = template;

        var source = useSubmittedValues
            ? model.Measurements.ToDictionary(m => m.Key, m => m, StringComparer.Ordinal)
            : client.Measurements.ToDictionary(
                m => m.MeasurementType,
                m => new MeasurementInputModel { Key = m.MeasurementType, Value = m.Value, Unit = m.Unit },
                StringComparer.Ordinal);

        model.Measurements = [.. template.Select(field =>
            source.TryGetValue(field.Key, out var existing)
                ? existing
                : new MeasurementInputModel { Key = field.Key, Unit = field.Unit })];
    }

    private void UpdateMeasurements(ClientProfile client, ClientProfileViewModel model)
    {
        var template = templates.GetTemplate(client.ModelProfileType);
        var existing = client.Measurements.ToDictionary(m => m.MeasurementType, StringComparer.Ordinal);

        foreach (var field in template)
        {
            var entered = model.Measurements.FirstOrDefault(m => m.Key == field.Key);
            var hasValue = entered is not null && !string.IsNullOrWhiteSpace(entered.Value);

            if (!hasValue)
            {
                // Clearing a field removes the measurement rather than storing a blank,
                // so nothing empty reaches the public stats section.
                if (existing.TryGetValue(field.Key, out var toRemove))
                {
                    db.ModelMeasurements.Remove(toRemove);
                }

                continue;
            }

            var unit = field.AllowsUnitChoice && entered!.Unit != MeasurementUnit.None
                ? entered.Unit
                : field.Unit;

            decimal? canonical = decimal.TryParse(entered!.Value, out var numeric)
                ? templates.ToCanonical(numeric, unit)
                : null;

            if (existing.TryGetValue(field.Key, out var measurement))
            {
                measurement.Value = entered.Value!.Trim();
                measurement.CanonicalValue = canonical;
                measurement.Unit = unit;
                measurement.DisplayOrder = field.DisplayOrder;
                measurement.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                db.ModelMeasurements.Add(new ModelMeasurement
                {
                    ClientId = client.Id,
                    MeasurementType = field.Key,
                    Value = entered.Value!.Trim(),
                    CanonicalValue = canonical,
                    Unit = unit,
                    DisplayOrder = field.DisplayOrder
                });
            }
        }
    }
}
