# Manchester School of Modelling

Digital portfolio and model development platform.

The application manages a client from post-photoshoot onboarding through portfolio
creation, purchase, publication and ongoing maintenance, replacing the manually
created PDF portfolio with a live digital portfolio that can be shown in the studio,
purchased online, shared with agencies through a public URL, and updated by the model
afterwards.

Build against `V1 Functional and Technical Specification`. Section references in the
code point back to it.

## Status

Phase 1 (Foundation) is complete. Later phases follow the implementation order in
specification section 51.

| Phase | Scope | State |
| ----- | ----- | ----- |
| 1 | Project, configuration, Identity, roles, authorization policies, domain entities, database abstraction | Done |
| 2 | Client onboarding, GHL contact ID capture, profile, measurements, guardian workflow | Not started |
| 3 | Media storage abstraction, upload pipeline, 60-image pool, 30-image portfolio, featured image, self-tape | Not started |
| 4 | Retoucher queue, assignments, upload workspace, submit for review | Not started |
| 5 | Admin dashboard, client management, portfolio preview, status management, publish/unpublish | Not started |
| 6 | Public portfolio, responsive gallery, contact MSM, Model Board, slugs | Not started |
| 7 | Orders, checkout, GoCardless integration, webhooks | Not started |
| 8 | Maintenance subscription, failed payment detection, grace period, automatic unpublish | Not started |
| 9 | GoHighLevel synchronisation | Not started |
| 10 | Audit, security, permissions, upload, payment, mobile, accessibility and performance hardening | Not started |

## Requirements

- .NET SDK 10.0 or later
- No database server is needed for local development; SQLite is the default provider

## Running locally

```bash
dotnet restore
dotnet run --project src/Msm.Portfolio.Web
```

The application applies migrations and seeds reference data on first start. In the
Development environment it also creates a Super Admin account:

- `superadmin@msm.local` / `Dev!Passw0rd`

That account is created **only** in Development. In any other environment, set real
credentials before first start, or no owner account is created:

```bash
dotnet user-secrets set "Seed:SuperAdmin:Email" "owner@example.com" --project src/Msm.Portfolio.Web
dotnet user-secrets set "Seed:SuperAdmin:Password" "<a strong password>" --project src/Msm.Portfolio.Web
```

## Tests

```bash
dotnet test
```

## Project layout

Organised per specification section 30.

```
src/Msm.Portfolio.Web/
  Areas/Admin/         Studio and management staff (specification section 5)
  Areas/Retoucher/     Retoucher queue and upload workspace (section 6)
  Areas/Client/        The model's own dashboard (section 17)
  Authorization/       Roles, permissions, policies (sections 3-5, 35)
  Configuration/       Strongly-typed options for configurable values
  Controllers/         Public and account controllers
  Data/                DbContext, provider selection, migrations, seeding
  Domain/Entities/     Core domain entities (section 26)
  Domain/Enums/        Portfolio, account and payment states (sections 27-29)
  ViewModels/
  Views/
tests/Msm.Portfolio.Tests/
```

## Roles and permissions

Four authenticated roles: `SuperAdmin`, `Admin`, `Retoucher`, `Client`. There is no
Agency account — agencies open the public portfolio URL without signing in.

Capabilities are granted as permission claims on a role rather than inferred from the
role name, because the specification requires that several staff accounts can hold
different privileges. `Permissions.DefaultsByRole` holds the defaults applied at
seeding.

Super Admin is granted every permission by `PermissionAuthorizationHandler` rather
than by a seeded claim list, so a permission added later cannot fall outside the
owner's reach. The capabilities reserved to Super Admin in specification section 4 are
listed in `Permissions.SuperAdminOnly`, and a test asserts no other role is granted
them.

## Database

The provider is deliberately configurable, because the production database is still an
open decision (specification section 32). Nothing outside `Data/DataRegistration.cs`
depends on which provider is in use.

```jsonc
"Database": {
  "Provider": "Sqlite",        // Sqlite | PostgreSql | SqlServer
  "ConnectionString": "Data Source=msm-portfolio.db",
  "MigrateOnStartup": true
}
```

Notes for when the provider is chosen:

- The committed migration was generated for **SQLite**, the development default. A
  migration's SQL is not portable between providers, so regenerate it after switching:
  `dotnet ef migrations remove` then `dotnet ef migrations add InitialSchema
  --output-dir Data/Migrations`, with `Database:Provider` already set to the target.
- SQLite stores `decimal` as text, so money columns do not sort or compare correctly in
  raw SQL. This affects development only; PostgreSQL and SQL Server both map `decimal`
  natively.
- `MigrateOnStartup` is convenient in development. Production deployments normally
  migrate as a separate, deliberate step — set it to `false` there.

## Configuration

Values the specification requires to be changeable without a code change live in
configuration and are mirrored into the `SystemSettings` table, where an editor screen
will maintain them in a later phase.

| Section | Covers |
| ------- | ------ |
| `Media` | 60-image pool limit, 30-image portfolio limit, file size and type restrictions, storage provider |
| `Commerce` | Programme price (£3,499), maintenance price (£19.99), 7-day grace period, maintenance start offset |
| `Msm` | Business name, public domain, contact email/phone/WhatsApp, social links |
| `Integrations` | GoCardless and GoHighLevel credentials |

Credentials are never committed. Supply them through user secrets in development and
environment variables in deployment.

## Decisions still open

Carried from specification section 52. These are configuration decisions, not missing
requirements.

- **Database provider** — abstracted; see the Database section above.
- **Media/object storage provider** — `Media:StorageProvider` is `LocalDisk` for now.
  Azure Blob, S3 or R2 slot in behind the same interface in Phase 3.
- **Final maintenance price** — `Commerce:MaintenancePrice`, placeholder £19.99.
  Existing subscriptions keep the price agreed when they started.
- **Maintenance start date** — `Commerce:MaintenanceStartsAfterDays`, currently 0.
- **Production domain** — `Msm:PublicDomain`.
- **MSM contact details** — `Msm:ContactEmail`, `ContactPhone`, `WhatsApp`, to be
  supplied by MSM.
- **Guardian consent wording** — to be supplied or approved by MSM. `GuardianConsent`
  records the version agreed, so changing the wording later cannot retrospectively
  alter what was consented to.
- **Image and video size limits** — `Media:MaxImageBytes`, `Media:MaxVideoBytes`.

### One judgement call worth confirming

For a client born on 29 February, the application treats their birthday in a non-leap
year as 1 March, so they remain 17 for that extra day and still require guardian
consent. .NET's built-in date arithmetic would instead make them an adult on
28 February. Because this gates a safeguarding control, the boundary was set so that an
error requires consent rather than skips it. Worth confirming against MSM's legal
wording.
