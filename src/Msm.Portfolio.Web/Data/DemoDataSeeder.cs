using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;
using SkiaSharp;

namespace Msm.Portfolio.Web.Data;

/// <summary>
/// Fills a preview deployment with invented clients so every screen has something in it.
/// </summary>
/// <remarks>
/// <para>
/// A demonstration of this application against an empty database shows an empty Model
/// Board, an empty queue and an empty client list, which tells MSM nothing. This creates
/// clients at each stage of the workflow instead: one published, one waiting for review,
/// one part-way through retouching, one just onboarded, and one under 18 whose guardian
/// has not yet approved.
/// </para>
/// <para>
/// Everything goes through the real services — onboarding, upload, selection, review,
/// publication — rather than being written straight to the database. Rows inserted
/// directly would drift from what the application actually produces, and the demonstration
/// would stop being evidence that any of it works.
/// </para>
/// <para>
/// <b>It refuses to run unless explicitly asked, and never when clients already exist.</b>
/// Invented people appearing in a live studio's client list would be worse than an empty
/// screen.
/// </para>
/// </remarks>
public class DemoDataSeeder(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IClientOnboardingService onboarding,
    IMediaService media,
    IRetoucherService retoucher,
    IPortfolioService portfolios,
    IMeasurementTemplateProvider templates,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<DemoDataSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("Seed:DemoData"))
        {
            return;
        }

        if (!environment.IsDevelopment())
        {
            // The flag alone is not enough. A production environment that somehow
            // acquired it would otherwise fill a real client list with invented people.
            logger.LogWarning(
                "Seed:DemoData is set but the environment is {Environment}. Refusing: demonstration "
                + "clients belong only in a preview.", environment.EnvironmentName);
            return;
        }

        if (await db.ClientProfiles.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Clients already exist, so demonstration data was not added.");
            return;
        }

        var retoucherUserId = await EnsureDemoRetoucherAsync();

        // ── The real models whose photographs MSM will upload ────────────────────────
        // Created empty and waiting in the retoucher queue: a member of staff claims
        // them, drags the photographs in and publishes, which is both how a portfolio
        // gets built and the most convincing thing to show MSM.
        //
        // These are MSM's own clients, not invented people. Where a figure was not
        // supplied the field is left empty rather than guessed: a wrong measurement on a
        // published portfolio is one an agency would book against. Missing fields are
        // named in the log and can be added on the client record.
        logger.LogWarning("Creating the real client records for the preview.");

        await BuildAsync(
            new DemoClient(
                "Elizabeth", "Cousins", "elizabeth.cousins@example.com",
                // An age was supplied rather than a date of birth. Deriving it keeps her
                // shown as 61 whenever this runs, instead of inventing a birthday.
                DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-61)),
                "Sheffield", ModelProfileType.Female,
                Biography: null,
                Photographs: 0,
                HairColour: "Brown",
                EyeColour: "Blue",
                Measurements: new Dictionary<string, string>
                {
                    ["Height"] = "173",     // 5'8"
                    ["Hips"] = "91",        // 36"
                    ["DressSize"] = "10",
                    ["ShoeSize"] = "6"
                }),
            retoucherUserId, Stage.Waiting, cancellationToken);

        await BuildAsync(
            new DemoClient(
                "Joshua", "Dinning", "joshua.dinning@example.com",
                DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-21)),
                "Timperley", ModelProfileType.Male,
                Biography: null,
                Photographs: 0,
                HairColour: "Brown",
                EyeColour: "Blue",
                Measurements: new Dictionary<string, string>
                {
                    ["Height"] = "180",     // 5'11"
                    ["Waist"] = "86",       // 34"
                    ["ShoeSize"] = "10"
                    // Chest was not supplied, and the "S" clothing size is a shirt size
                    // rather than the UK suit or jacket size this field records, so
                    // neither is filled in here.
                }),
            retoucherUserId, Stage.Waiting, cancellationToken);

        // ── Invented clients, off by default ─────────────────────────────────────────
        // Six more at every stage of the workflow, for showing the whole system rather
        // than one portfolio. Separate from the above so a preview built around a real
        // model is not cluttered with fictional ones.
        if (configuration.GetValue<bool>("Seed:SampleClients"))
        {
            await SeedSampleClientsAsync(retoucherUserId, cancellationToken);
        }

        logger.LogWarning("Preview data created.");
    }

    /// <summary>
    /// Invented clients at each stage of the workflow, so every screen has something in
    /// it. Enabled with Seed:SampleClients.
    /// </summary>
    private async Task SeedSampleClientsAsync(Guid retoucherUserId, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Creating sample clients. These are invented people and must never appear in a "
            + "live deployment.");

        await BuildAsync(
            new DemoClient(
                "Amara", "Whitfield", "amara.whitfield@example.com", new DateOnly(1999, 4, 18),
                "Manchester", ModelProfileType.Female,
                "Manchester-based model working across editorial, commercial and campaign "
                + "photography. Trained through the Manchester School of Modelling four week "
                + "development programme and available for bookings nationally.",
                Photographs: 8),
            retoucherUserId, Stage.Published, cancellationToken);

        await BuildAsync(
            new DemoClient(
                "Tobias", "Fenwick", "tobias.fenwick@example.com", new DateOnly(1996, 11, 2),
                "Salford", ModelProfileType.Male,
                "Commercial and lifestyle model with a background in menswear and tailoring.",
                Photographs: 6),
            retoucherUserId, Stage.Published, cancellationToken);

        await BuildAsync(
            new DemoClient(
                "Priya", "Raval", "priya.raval@example.com", new DateOnly(2001, 7, 9),
                "Bolton", ModelProfileType.Female,
                "Editorial and beauty work, with a particular interest in campaign styling.",
                Photographs: 5),
            retoucherUserId, Stage.ReadyForReview, cancellationToken);

        await BuildAsync(
            new DemoClient(
                "Callum", "Reid", "callum.reid@example.com", new DateOnly(1998, 2, 25),
                "Stockport", ModelProfileType.Male, null, Photographs: 4),
            retoucherUserId, Stage.InRetouching, cancellationToken);

        await BuildAsync(
            new DemoClient(
                "Niamh", "O'Connell", "niamh.oconnell@example.com", new DateOnly(2000, 9, 14),
                "Manchester", ModelProfileType.Female, null, Photographs: 0),
            retoucherUserId, Stage.Waiting, cancellationToken);

        // Shows the safeguarding banner, and that publication is blocked until a
        // guardian approves (specification section 11).
        await BuildAsync(
            new DemoClient(
                "Elsie", "Hartley", "elsie.hartley@example.com",
                DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-16)),
                "Rochdale", ModelProfileType.Female, null, Photographs: 3,
                GuardianName: "Rebecca Hartley",
                GuardianEmail: "rebecca.hartley@example.com"),
            retoucherUserId, Stage.InRetouching, cancellationToken);
    }

    private enum Stage { Waiting, InRetouching, ReadyForReview, Published }

    private record DemoClient(
        string FirstName,
        string LastName,
        string Email,
        DateOnly DateOfBirth,
        string Location,
        ModelProfileType ProfileType,
        string? Biography,
        int Photographs,
        string? GuardianName = null,
        string? GuardianEmail = null,
        string? HairColour = null,
        string? EyeColour = null,
        /// <summary>
        /// Real figures, by template key. Any key left out is simply not recorded —
        /// which is the point for a real model whose full card was not supplied.
        /// </summary>
        IReadOnlyDictionary<string, string>? Measurements = null);

    private async Task BuildAsync(
        DemoClient demo, Guid retoucherUserId, Stage stage, CancellationToken cancellationToken)
    {
        var model = new OnboardingViewModel
        {
            GhlContactId = $"demo-{demo.LastName.ToLowerInvariant()}",
            FirstName = demo.FirstName,
            LastName = demo.LastName,
            Email = demo.Email,
            Phone = "07700 900000",
            DateOfBirth = demo.DateOfBirth,
            Location = demo.Location,
            ModelProfileType = demo.ProfileType,
            HairColour = demo.HairColour,
            EyeColour = demo.EyeColour,
            Biography = demo.Biography,
            GuardianRequired = demo.GuardianName is not null,
            GuardianName = demo.GuardianName,
            GuardianRelationship = demo.GuardianName is null ? null : "Parent",
            GuardianEmail = demo.GuardianEmail,
            GuardianPhone = demo.GuardianName is null ? null : "07700 900001",
            Measurements = demo.Measurements is null
                ? InventedMeasurementsFor(demo.ProfileType)
                : SuppliedMeasurements(demo.ProfileType, demo.Measurements)
        };

        var result = await onboarding.SubmitAsync(model, cancellationToken);

        if (!result.Succeeded || result.Client is null)
        {
            logger.LogError(
                "Demonstration client {Name} could not be created: {Error}",
                demo.LastName, result.Error);
            return;
        }

        var clientId = result.Client.Id;

        if (stage == Stage.Waiting)
        {
            return;
        }

        var claimed = await retoucher.StartWorkAsync(clientId, retoucherUserId, cancellationToken);

        if (!claimed.Succeeded)
        {
            logger.LogError("Could not claim {Name}: {Error}", demo.LastName, claimed.Error);
            return;
        }

        if (demo.Photographs > 0)
        {
            var files = Photographs(demo.Photographs, demo.LastName);
            var outcomes = await media.UploadImagesAsync(clientId, files, retoucherUserId, cancellationToken);

            var uploaded = outcomes.Where(o => o.Succeeded && o.AssetId is not null)
                                   .Select(o => o.AssetId!.Value)
                                   .ToList();

            foreach (var assetId in uploaded)
            {
                await media.SetSelectedAsync(clientId, assetId, true, retoucherUserId, cancellationToken);
            }

            if (uploaded.Count > 0)
            {
                await media.SetFeaturedAsync(clientId, uploaded[0], retoucherUserId, cancellationToken);
            }
        }

        if (stage == Stage.InRetouching)
        {
            return;
        }

        var submitted = await retoucher.SubmitForReviewAsync(clientId, retoucherUserId, cancellationToken);

        if (!submitted.Succeeded)
        {
            logger.LogError("Could not submit {Name}: {Error}", demo.LastName, submitted.Error);
            return;
        }

        if (stage != Stage.Published)
        {
            return;
        }

        await portfolios.MarkInViewingAsync(clientId, retoucherUserId, cancellationToken);

        var published = await portfolios.PublishAsync(clientId, retoucherUserId, cancellationToken);

        if (!published.Succeeded)
        {
            // Expected for the under-18 client, whose guardian has not approved — the
            // rule working, not a failure.
            logger.LogInformation(
                "{Name} was not published: {Error}", demo.LastName, published.Error);
        }
    }

    /// <summary>
    /// A retoucher account, so the queue and workspace can be demonstrated from that
    /// side rather than only through an Admin who can open anything.
    /// </summary>
    private async Task<Guid> EnsureDemoRetoucherAsync()
    {
        const string email = "retoucher@msm.local";

        var existing = await userManager.FindByEmailAsync(email);

        if (existing is not null)
        {
            return existing.Id;
        }

        // Shares the owner's password so a preview needs one credential, not two. Only
        // ever reached in Development with demonstration data explicitly requested.
        var password = configuration["Seed:SuperAdmin:Password"] ?? "Dev!Passw0rd";

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FirstName = "Demo",
            LastName = "Retoucher",
            IsActive = true
        };

        var created = await userManager.CreateAsync(user, password);

        if (!created.Succeeded)
        {
            logger.LogError(
                "Could not create the demonstration retoucher: {Errors}",
                string.Join("; ", created.Errors.Select(e => e.Description)));
            throw new InvalidOperationException("Demonstration retoucher could not be created.");
        }

        await userManager.AddToRoleAsync(user, Roles.Retoucher);

        return user.Id;
    }

    /// <summary>
    /// Fills whichever fields this profile type's template asks for.
    /// </summary>
    /// <remarks>
    /// Driven by the configured template rather than a fixed list, so a studio that has
    /// changed which measurements it collects still gets a complete demonstration.
    /// Plausible figures per field, so the specification sheet on the portfolio reads
    /// like a real one instead of the same number repeated down the page.
    /// </remarks>
    /// <summary>
    /// Records only the figures actually supplied, in the template's own order.
    /// </summary>
    /// <remarks>
    /// A field with no figure is left out rather than filled with a placeholder. This is
    /// for a real model: a guessed bust or waist would appear on a published portfolio as
    /// though MSM had measured her, and an agency would book against it.
    /// </remarks>
    private List<MeasurementInputModel> SuppliedMeasurements(
        ModelProfileType profileType, IReadOnlyDictionary<string, string> supplied)
    {
        var missing = templates.GetTemplate(profileType)
            .Where(field => !supplied.ContainsKey(field.Key))
            .Select(field => field.Label)
            .ToList();

        if (missing.Count > 0)
        {
            logger.LogWarning(
                "No figure supplied for {Fields}. Add them on the client record before publishing.",
                string.Join(", ", missing));
        }

        return templates.GetTemplate(profileType)
            .Where(field => supplied.ContainsKey(field.Key))
            .Select(field => new MeasurementInputModel
            {
                Key = field.Key,
                Unit = field.Unit,
                Value = supplied[field.Key]
            })
            .ToList();
    }

    private List<MeasurementInputModel> InventedMeasurementsFor(ModelProfileType profileType)
    {
        var male = profileType == ModelProfileType.Male;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Height"] = male ? "185" : "175",
            ["Bust"] = "86",
            ["Chest"] = "102",
            ["Waist"] = male ? "81" : "64",
            ["Hips"] = "91",
            ["Collar"] = "41",
            ["DressSize"] = "10",
            ["SuitSize"] = "40",
            ["ShoeSize"] = male ? "10" : "6",
            ["Inseam"] = "82"
        };

        return templates.GetTemplate(profileType)
            .Select(field => new MeasurementInputModel
            {
                Key = field.Key,
                Unit = field.Unit,
                Value = values.TryGetValue(field.Key, out var value) ? value : "0"
            })
            .ToList();
    }

    /// <summary>
    /// Placeholder photography, drawn rather than downloaded.
    /// </summary>
    /// <remarks>
    /// Warm, low-key and mostly portrait, so the editorial layout can be judged — and
    /// unmistakably not a photograph of a real person, which matters when the images sit
    /// under MSM's brand on a public preview.
    /// </remarks>
    private static List<IFormFile> Photographs(int count, string seed)
    {
        var random = new Random(seed.GetHashCode(StringComparison.Ordinal));
        var files = new List<IFormFile>(count);

        for (var i = 0; i < count; i++)
        {
            // Every third frame is landscape, so the gallery's two-column spanning rule
            // and the orientation badges both appear in the demonstration.
            var landscape = i % 3 == 2;
            var width = landscape ? 1600 : 1200;
            var height = landscape ? 1200 : 1600;

            var top = new SKColor(
                (byte)random.Next(38, 78), (byte)random.Next(32, 66), (byte)random.Next(26, 56));
            var bottom = new SKColor(
                (byte)random.Next(14, 26), (byte)random.Next(12, 22), (byte)random.Next(10, 18));

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;

            using (var paint = new SKPaint())
            {
                paint.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(width * 0.3f, 0), new SKPoint(width * 0.7f, height),
                    [top, bottom], null, SKShaderTileMode.Clamp);
                canvas.DrawRect(new SKRect(0, 0, width, height), paint);
            }

            using (var glow = new SKPaint())
            {
                glow.Shader = SKShader.CreateRadialGradient(
                    new SKPoint(width * 0.5f, height * 0.34f), height * 0.42f,
                    [new SKColor(0xFF, 0xF3, 0xE0, 0x33), new SKColor(0, 0, 0, 0)],
                    null, SKShaderTileMode.Clamp);
                canvas.DrawRect(new SKRect(0, 0, width, height), glow);
            }

            // Grain, so the JPEG compresses like a photograph rather than a flat wash.
            using (var grain = new SKPaint())
            {
                for (var g = 0; g < width * height / 900; g++)
                {
                    grain.Color = new SKColor(255, 255, 255, (byte)random.Next(3, 12));
                    canvas.DrawCircle(random.Next(width), random.Next(height), random.Next(1, 3), grain);
                }
            }

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 86);

            files.Add(new InMemoryFormFile(
                data.ToArray(), $"{seed.ToLowerInvariant()}-{i + 1:00}.jpg", "image/jpeg"));
        }

        return files;
    }

    /// <summary>
    /// Lets generated images go through the same upload path as a retoucher's file,
    /// so the demonstration exercises the real pipeline rather than a shortcut.
    /// </summary>
    private sealed class InMemoryFormFile(byte[] bytes, string fileName, string contentType) : IFormFile
    {
        public string ContentType => contentType;
        public string ContentDisposition => $"form-data; name=\"files\"; filename=\"{fileName}\"";
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length => bytes.Length;
        public string Name => "files";
        public string FileName => fileName;

        public void CopyTo(Stream target) => target.Write(bytes, 0, bytes.Length);

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            target.WriteAsync(bytes, cancellationToken).AsTask();

        public Stream OpenReadStream() => new MemoryStream(bytes, writable: false);
    }
}
