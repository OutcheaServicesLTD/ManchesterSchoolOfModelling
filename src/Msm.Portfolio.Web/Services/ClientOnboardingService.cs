using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Services;

public record OnboardingResult(bool Succeeded, ClientProfile? Client = null, string? Error = null);

public interface IClientOnboardingService
{
    /// <summary>True when a client already exists for this CRM contact.</summary>
    Task<bool> ExistsForContactAsync(string ghlContactId, CancellationToken cancellationToken = default);

    Task<OnboardingResult> SubmitAsync(OnboardingViewModel model, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a completed onboarding form into an account, profile, measurements and a
/// portfolio sitting in the retoucher queue (specification sections 2 and 8).
/// </summary>
public class ClientOnboardingService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IMeasurementTemplateProvider templates,
    IGuardianConsentService guardianConsent,
    IAuditService audit,
    INotificationService notifications,
    ILogger<ClientOnboardingService> logger) : IClientOnboardingService
{
    public Task<bool> ExistsForContactAsync(string ghlContactId, CancellationToken cancellationToken = default) =>
        db.ClientProfiles.AnyAsync(c => c.GhlContactId == ghlContactId, cancellationToken);

    public async Task<OnboardingResult> SubmitAsync(
        OnboardingViewModel model,
        CancellationToken cancellationToken = default)
    {
        var normalisedEmail = model.Email.Trim();

        if (await userManager.FindByEmailAsync(normalisedEmail) is not null)
        {
            return new OnboardingResult(false,
                Error: "An account already exists for that email address. Please contact us so we can help.");
        }

        if (!string.IsNullOrWhiteSpace(model.GhlContactId)
            && await ExistsForContactAsync(model.GhlContactId, cancellationToken))
        {
            return new OnboardingResult(false,
                Error: "We have already received your details. Please contact us if you need to make a change.");
        }

        // Everything below is one transaction: a half-created client with an account but
        // no portfolio would sit invisible to both the retoucher queue and the client.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var user = new ApplicationUser
            {
                UserName = normalisedEmail,
                Email = normalisedEmail,
                PhoneNumber = model.Phone,
                FirstName = model.FirstName.Trim(),
                LastName = model.LastName.Trim(),
                EmailConfirmed = false,
                IsActive = true
            };

            // No password is set here. The client receives account access after purchase
            // (specification section 50), so the account exists but cannot yet be signed in to.
            var created = await userManager.CreateAsync(user);
            if (!created.Succeeded)
            {
                var error = string.Join("; ", created.Errors.Select(e => e.Description));
                logger.LogWarning("Onboarding could not create an account: {Error}", error);
                await transaction.RollbackAsync(cancellationToken);
                return new OnboardingResult(false, Error: "We could not create your account. Please contact us.");
            }

            await userManager.AddToRoleAsync(user, Roles.Client);

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var client = new ClientProfile
            {
                ApplicationUserId = user.Id,
                GhlContactId = string.IsNullOrWhiteSpace(model.GhlContactId) ? null : model.GhlContactId.Trim(),
                FirstName = model.FirstName.Trim(),
                LastName = model.LastName.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim(),
                DateOfBirth = model.DateOfBirth,
                Location = model.Location?.Trim(),
                ModelProfileType = model.ModelProfileType,
                Biography = model.Biography?.Trim(),
                HairColour = model.HairColour?.Trim(),
                EyeColour = model.EyeColour?.Trim(),
                InstagramUrl = model.InstagramUrl?.Trim(),
                TikTokUrl = model.TikTokUrl?.Trim(),
                AccountStatus = ClientAccountStatus.Invited
            };

            db.ClientProfiles.Add(client);

            ApplyMeasurements(client, model);

            var portfolio = new Domain.Entities.Portfolio
            {
                ClientId = client.Id,
                // The portfolio joins the retoucher queue immediately. Where guardian
                // approval is outstanding, preparation still starts; the block applies
                // at purchase and publication (specification section 11).
                Status = PortfolioStatus.ReadyForRetoucher
            };

            db.Portfolios.Add(portfolio);

            GuardianConsent? consent = null;
            if (client.RequiresGuardianConsent(today))
            {
                consent = guardianConsent.RequestConsent(
                    client,
                    model.GuardianName!.Trim(),
                    model.GuardianRelationship!.Trim(),
                    model.GuardianEmail!.Trim(),
                    model.GuardianPhone?.Trim());
            }

            audit.Record(nameof(ClientProfile), client.Id.ToString(), AuditActions.ProfileCreated,
                userId: user.Id,
                newValue: $"Onboarding submitted for {client.FullName}");

            audit.Record(nameof(Domain.Entities.Portfolio), portfolio.Id.ToString(),
                AuditActions.PortfolioStatusChanged,
                userId: user.Id,
                newValue: PortfolioStatus.ReadyForRetoucher.ToString());

            var staffMessage = consent is null
                ? $"{client.FullName} completed onboarding and is ready for a retoucher."
                : $"{client.FullName} completed onboarding. They are under 18 and guardian approval is pending.";

            await notifications.NotifyStaffAsync(
                NotificationTypes.OnboardingCompleted, staffMessage, "/admin", cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Sent only after the commit: a message promising an approval link would be
            // wrong if the transaction were then rolled back.
            if (consent is not null)
            {
                await guardianConsent.SendRequestEmailAsync(consent, client, cancellationToken);
            }

            logger.LogInformation("Onboarding completed for client {ClientId}.", client.Id);
            return new OnboardingResult(true, client);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Onboarding failed and was rolled back.");
            await transaction.RollbackAsync(cancellationToken);
            return new OnboardingResult(false, Error: "Something went wrong saving your details. Please try again.");
        }
    }

    private void ApplyMeasurements(ClientProfile client, OnboardingViewModel model)
    {
        var template = templates.GetTemplate(model.ModelProfileType);

        foreach (var field in template)
        {
            var entered = model.Measurements.FirstOrDefault(m => m.Key == field.Key);

            if (entered is null || string.IsNullOrWhiteSpace(entered.Value))
            {
                continue;
            }

            // Fall back to the field's own unit when the form offered no choice.
            var unit = field.AllowsUnitChoice && entered.Unit != MeasurementUnit.None
                ? entered.Unit
                : field.Unit;

            decimal? canonical = null;
            if (decimal.TryParse(entered.Value, out var numeric))
            {
                canonical = templates.ToCanonical(numeric, unit);
            }

            db.ModelMeasurements.Add(new ModelMeasurement
            {
                ClientId = client.Id,
                MeasurementType = field.Key,
                Value = entered.Value.Trim(),
                CanonicalValue = canonical,
                Unit = unit,
                DisplayOrder = field.DisplayOrder
            });
        }
    }
}
