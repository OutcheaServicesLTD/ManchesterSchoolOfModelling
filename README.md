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

Phases 1 to 9 are complete. Later phases follow the implementation order in
specification section 51.

| Phase | Scope | State |
| ----- | ----- | ----- |
| 1 | Project, configuration, Identity, roles, authorization policies, domain entities, database abstraction | Done |
| 2 | Client onboarding, GHL contact ID capture, profile, measurements, guardian workflow | Done |
| 3 | Media storage abstraction, upload pipeline, 60-image pool, 30-image portfolio, featured image, self-tape | Done |
| 4 | Retoucher queue, assignments, upload workspace, submit for review | Done |
| 5 | Admin dashboard, client management, portfolio preview, status management, publish/unpublish | Done |
| 6 | Public portfolio, responsive gallery, contact MSM, Model Board, slugs | Done |
| 7 | Orders, checkout, GoCardless integration, webhooks | Done, except the provider's own HTTP calls — see below |
| 8 | Maintenance subscription, failed payment detection, grace period, automatic unpublish | Done |
| 9 | GoHighLevel synchronisation | Done, except the provider's own HTTP calls — see below |
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

## Onboarding and guardian consent

The client arrives from a GoHighLevel link carrying their contact id:

```
/onboarding?ghlContactId=abc123
```

The contact id is stored against the client and is the permanent CRM link; email and
telephone are never used as the CRM identifier, because either can change without the
contact changing.

Submitting the form creates the account, profile, measurements and a portfolio, and
places that portfolio in the retoucher queue — all in one transaction, so a client can
never exist without a portfolio or vice versa. The account is created **without a
password**: the client receives sign-in access after purchase (specification section 50).

Two behaviours here are worth knowing about:

- **Nothing already stored is read back to an anonymous visitor.** The contact id
  travels in a URL and is not a credential, so anyone holding a link could otherwise
  read the client's date of birth, location and telephone number. Reopening a completed
  link shows a neutral "we already have your details" page instead of the profile.
- **Guardian requirement is derived from the date of birth**, not from the flag the
  browser posts back, so clearing that flag in the page does not bypass the check.

For an under-18 client, guardian details are mandatory and the guardian receives a
tokenised approval link at `/guardian/approve/{token}`. The token is 32 bytes of
cryptographic randomness, time limited, and rotated on use so a forwarded or logged
link cannot be replayed.

Until Phase 9 connects GoHighLevel messaging, `IEmailSender` is a logging
implementation: in development the approval link appears in the application log, and
outside development it logs an error rather than silently dropping a real guardian's
email.

### Where the under-18 block applies

A minor's portfolio still enters the retoucher queue while guardian approval is
outstanding, because the studio workflow is meant to be fast and preparation is not the
thing the specification prohibits. The hard stop is the one section 11 states — purchase
and publication — enforced by `ClientProfile.IsBlockedPendingGuardianConsent`. If MSM
would rather block preparation too, that is a one-line change.

## Media

Each client has one private pool of up to 60 images, of which at most 30 appear on
their portfolio. That gap is the point: staff work from more photographs than the model
ever shows publicly.

**Media is never served from `wwwroot`.** Files live outside the web root and every
request goes through `/media/{assetId}/{variant}`, which decides access:

| Requester | Sees |
| --------- | ---- |
| Staff (Super Admin, Admin, Retoucher) | Any asset |
| The owning client | Their own library |
| Anonymous, including agencies | Only images selected for a **published** portfolio |

Both conditions matter for the public case. A selected image on an unpublished
portfolio stays private, and an unselected image on a published portfolio stays private.
A denied request returns 404 rather than 403, because a 403 would confirm the asset id
exists.

### Image processing

Uploads are decoded rather than trusted by extension or content type, so a renamed
non-image is rejected. Each accepted image is archived exactly as uploaded, and three
web renditions are generated (large 2000px, medium 1200px, thumbnail 400px on the
longest edge). Nothing is cropped and nothing is scaled up: renditions preserve the
original aspect ratio, so portrait and landscape are equally well supported, and a
small original is left at its own size rather than enlarged.

Grids use thumbnails and lazy loading; the archived original is never sent to a browser.

Batch uploads report each file individually, so one rejected photograph does not force
a retoucher to restart the batch.

### Featured image

Exactly one portfolio image is the featured image, used as the portfolio hero and the
Model Board card. The specification stores this in two places — a flag on the asset and
a foreign key on the portfolio — so both are maintained in one place in `MediaService`
and can never disagree. Selecting the first image promotes it automatically; removing or
deselecting the featured image promotes another, and emptying the portfolio clears it.

Removal is a soft delete. The row is flagged and the file stays in storage, so a
mistaken removal is recoverable; permanent destruction is a Super Admin action.

### Image library choice

SkiaSharp (MIT). ImageSharp is the more common choice but requires a paid commercial
licence above a revenue threshold, which would be a licensing liability for a commercial
product.

## Retoucher workflow

The queue at `/retoucher` has the four tabs from specification section 6 — Waiting,
In progress, Ready for review, Completed — derived from portfolio status rather than
stored separately.

**Work is claimed, not just opened.** Starting work on a waiting client creates a
`RetoucherAssignment` and moves the portfolio to Retouching. After that, only the
assigned retoucher can open that workspace; another retoucher is refused, so two people
cannot unknowingly prepare the same portfolio. Unclaimed work in the Waiting tab is open
to anyone to pick up, and Admins can open any workspace. Every workspace action
re-checks the assignment, so the check cannot be skipped by posting directly.

### Uploading

The workspace sends **one file per request**. That is what makes specification section
42's requirements achievable: a progress bar per file, a failure reported against the
file that caused it, and a retry that re-sends only that file rather than restarting the
batch. Drag-and-drop is progressive enhancement over a plain file input, and the
endpoint answers in JSON — including for authorisation failures, so an expired session
reports itself rather than surfacing as a parse error.

Browser-side size and type checks exist to avoid pointless uploads; the server repeats
every one of them.

### Reordering

Reordering uses move-earlier/move-later buttons rather than drag-only handles.
Specification section 41 requires keyboard navigation, and a drag-only control is
unusable without a mouse.

### Sending for review

Submitting requires at least one selected photograph and a chosen main image, otherwise
the portfolio would reach Admin with nothing to review. It moves the assignment and
portfolio to Ready for review, notifies staff and writes an audit entry, all in one
transaction. Changes save as they are made, so there is no separate "save draft" step —
the draft is simply the portfolio before it is submitted.

## Admin and the portfolio lifecycle

The client table at `/admin` is searchable by name or email and filterable by portfolio
status and retoucher (specification section 5). Each client record shows the full media
library, not only the public selection.

### Permissions, not roles

Admin actions are gated by individual permission rather than by the Admin role, because
the specification requires staff accounts to hold different privileges. A button the
signed-in user cannot use is not rendered, **and** the action itself is gated, so posting
directly achieves nothing. The capabilities reserved to Super Admin in section 4
(permanent deletion, restore, payment override, managing administrators, changing system
configuration) cannot be delegated: an attempt to grant one to another role is stripped
rather than honoured.

### Publishing

Publishing enforces the hard stop from specification section 11: **an under-18 client
cannot be published until their guardian has approved.** It also requires at least one
photograph and a main image. The reason publishing is unavailable is shown on the page
rather than only surfacing when the attempt is refused.

Slugs are assigned once, at first publication, and left alone afterwards — so a link
already shared with an agency keeps working even if the model later changes their
display name. Unpublishing keeps the slug, so the same address returns if the portfolio
goes live again. Reserved slugs are refused: public portfolios are served from the site
root as `/{slug}`, so a model named "Admin" would otherwise shadow the admin area.

### No sale and deletion

A declined sale is archived, not deleted, and stays available to staff (specification
section 48). Only a Super Admin can restore it or destroy it. Permanent deletion removes
the portfolio, its media rows and the stored files, but keeps the client record and
writes an audit entry that deliberately outlives what it describes.

## The public site

No sign-in anywhere. Agencies open a link and read the page.

| Route | Page |
| ----- | ---- |
| `/` | Redirects to the Model Board — the portfolio domain has no marketing homepage |
| `/models` | Model Board |
| `/{slug}` | A model's portfolio, e.g. `/emma-johnson` |

Portfolios are served from the site root. Route matching prefers literal segments over
parameters, so `/admin`, `/client`, `/retoucher`, `/media` and the rest are unaffected,
and slug creation refuses those names as a second line of defence.

### What the public can and cannot see

`PublicPortfolio` is a projection, not the entity. The client record holds an email
address, a telephone number, the CRM contact id and guardian details, and none of them
belong on a public page — building a separate shape means they cannot leak through a
view by accident. A test asserts the serialised projection contains none of them.

Unpublishing removes the portfolio page, the Model Board card **and** public access to
the images in one move, because all three read the same `IsPublished` flag. There is no
separate board record to fall out of step (specification section 47).

### Contact routes to MSM, never to the model

The enquiry form collects the *enquirer's* details. The model's own email and telephone
are never shown and never used. The model enquired about is taken from the portfolio in
the URL rather than the posted form, so an enquiry cannot be redirected at someone else,
and the server re-checks the portfolio is published before storing anything. MSM staff
are notified; the model is not.

Enquiries are **stored**, not only emailed. This adds an `Enquiry` entity beyond the list
in specification section 26 — a deliberate addition, because no email provider is
configured yet and an unstored enquiry would simply be lost.

### Presentation

Mobile-first throughout: models share these links through messaging and social media far
more often than anyone opens them on a desktop. Landscape photographs span two columns
where there is room rather than being squeezed into a portrait-shaped cell, and images
are contained rather than cropped.

Accessibility is treated as a functional requirement (specification section 41): a skip
link, visible focus outlines on every interactive element, labelled form fields, real
heading structure, alt text, and Model Board cards that are a single link rather than
two adjacent ones.

MSM branding in the header and footer is rendered by the platform and is not
client-editable.

## Payments

The £3,499 programme is sold from the studio: Admin opens the checkout with the client
present, the client accepts the terms, and the provider's hosted page collects the
payment details. None are handled by this application.

### What is built and tested, and what is not

Everything up to the provider boundary is real and covered by tests: the order
lifecycle, the payment states from specification section 21, webhook signature
verification, replay-safe idempotency, and the rule that publishes a portfolio on a
successful payment.

**`GoCardlessService` — the HTTP calls to GoCardless — is not verified.** Their API and
their documentation were both unreachable from the environment this was built in, so the
request and response shapes come from documented knowledge rather than an observed
exchange. Before taking real payments, work through
[`docs/gocardless-verification.md`](docs/gocardless-verification.md).

Until then, leave `Integrations:GoCardless:AccessToken` unset. `StubGoCardlessService` is
registered automatically when it is: it takes no money, says so plainly on every page,
and refuses to authorise anything outside Development, so it cannot quietly publish
portfolios nobody paid for.

### The order

The agreed amount is copied onto the order at checkout and never read back from the
product, so changing the advertised price later cannot alter what a client was charged
(specification section 19). Reopening a checkout reuses the unfinished order rather than
creating a second one, and a client who has already paid cannot open another.

Checkout refuses to open for a portfolio the client has not been shown, and for an
under-18 client whose guardian has not approved — both enforced in the service as well
as the page, so opening the URL directly achieves nothing.

### Webhooks

`POST /webhooks/gocardless` is anonymous and exempt from anti-forgery by necessity: the
provider has no session and no token. The payload signature is therefore the only thing
separating a real payment notification from a forged one, and it is verified before
anything is read from the body. With no signing secret configured, everything is
refused — accepting unsigned webhooks would let anyone who found the URL mark an order
as paid and publish a portfolio.

Providers retry until they get a success, so the same event arrives repeatedly. Each is
recorded under a unique provider event id first; a repeat is acknowledged and skipped
rather than applied again. Processing does not depend on a browser, so a client who
closed the tab mid-payment still gets their portfolio published.

An unrecognised provider action is recorded and changes nothing, so a new event type
cannot corrupt an order.

### Two deliberate behaviours

- **A paid order stands even if publication is refused.** If payment succeeds but the
  portfolio cannot go live, the sale is kept and staff are notified — the client has paid
  either way, and a person resolves it.
- **A payment failure after confirmation does not unpublish anything.** That concerns the
  money, not the sale; tearing the portfolio down there would bypass the grace period in
  specification section 23.

## Maintenance and the grace period

The monthly portfolio maintenance charge is a separate product from the £3,499
programme. A subscription record is created when the programme is purchased, fixing the
price agreed that day so a later change cannot alter it, and starting at the offset in
`Commerce:MaintenanceStartsAfterDays`.

### What happens when a payment fails

Exactly what specification section 23 describes:

1. The subscription moves to `PaymentIssue` and a grace period opens, its length set by
   `Commerce:MaintenanceGracePeriodDays` (7 by default).
2. Staff and the client are both notified, and both dashboards show a warning counting
   down the days.
3. **The portfolio stays public throughout.** Nothing changes for an agency looking at
   it.
4. If payment is resolved, the warning clears and the portfolio carries on.
5. If it is not, the portfolio is unpublished and the Model Board listing goes with it.

Two details worth knowing:

- **A second failure does not restart the clock.** Otherwise a repeatedly failing
  payment would keep a portfolio live indefinitely without being paid for.
- **Paying after expiry does not republish automatically.** The portfolio has already
  come down; putting it back is a deliberate act, and staff are told it is now possible.

### The warning is never public

The payment problem is between MSM and the client. It appears on the admin and client
dashboards and nowhere else — a test asserts the public portfolio contains no trace of
it.

### Expiry runs on a timer

A grace period elapses by the passage of time, not by anyone doing anything, so
`MaintenanceGracePeriodWorker` checks hourly. A portfolio therefore comes down on the
seventh day even if no staff member signs in and the client never returns. The check is
idempotent — expiring a subscription moves it out of the set the query matches — so a
missed run catches up and a duplicate run does nothing.

### Model Board entitlement

Specification section 18 requires an active entitlement as well as publication, which
Phase 6 left open. It is enforced now: a failed payment keeps entitlement while inside
its grace period, and loses it once the period elapses or the arrangement ends. Because
this is evaluated per request, a model drops off the board the moment their grace period
runs out, rather than waiting for the worker's next hourly pass.

## GoHighLevel synchronisation

The CRM is downstream of this application, never the other way round. The contact id
captured at onboarding is the permanent link, and six fields are mirrored onto the
contact: portfolio URL, portfolio status, purchase status, purchase date, maintenance
status and published date (specification section 25).

### A CRM problem never disturbs a portfolio

This is the rule specification section 45 exists for, and it shapes the design.
Publishing, purchasing or a maintenance change **marks** the portfolio as needing a
sync and then finishes. The push itself happens on a worker, so a CRM that is slow or
down cannot delay the studio and cannot roll back a purchase that already succeeded.

Verified against a genuinely unreachable CRM: the portfolio stayed published, kept its
slug, stayed on the Model Board and served normally, while only the sync state recorded
the failure.

Failures retry with an exponential backoff held on the row, so a restart during an
outage does not reset it and hammer a service that is already struggling. A request the
CRM rejects outright — an unknown contact, a malformed payload — is marked terminal
rather than retried forever, because nothing will change by trying again. Staff are
alerted once, after three consecutive failures, not on every pass.

A client with no CRM contact — one created directly by staff — is recorded as not
synced rather than failed, since retrying would be pointless.

### Only the listed fields leave the application

`CrmContactFields` is a small named record rather than an open dictionary. Nothing else
about a client — their measurements, photographs or guardian's details — can reach an
external system by accident, and a test asserts it.

### Integration status

`/admin/integrations` shows whether each provider is connected, how many portfolios are
up to date, and which are failing with a retry-now action (specification section 4).

### Not verified

**`HighLevelService` — the HTTP calls to GoHighLevel — is unverified.** Their API and
documentation were both unreachable from the environment this was built in. Work through
[`docs/gohighlevel-verification.md`](docs/gohighlevel-verification.md) before relying on
it; the most likely silent failure is a custom-field key that does not exist in MSM's
account, where the call succeeds but writes nothing.

Until then, leave `Integrations:HighLevel:ApiKey` unset and the stub logs instead of
sending.

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
- SQLite has no native `DateTimeOffset` and refuses to sort one, so on that provider
  only, timestamps are stored as UTC ticks via a value converter. Without it, queries
  such as "the most recent self-tape" throw rather than return rows. Nothing is lost,
  because every timestamp in this application is written as UTC. PostgreSQL and SQL
  Server handle the type natively and are untouched.
- `MigrateOnStartup` is convenient in development. Production deployments normally
  migrate as a separate, deliberate step — set it to `false` there.

## Configuration

Values the specification requires to be changeable without a code change live in
configuration and are mirrored into the `SystemSettings` table, where an editor screen
will maintain them in a later phase.

| Section | Covers |
| ------- | ------ |
| `Media` | 60-image pool limit, 30-image portfolio limit, file size and type restrictions, storage provider |
| `MeasurementTemplates` | Which measurements are collected per profile type. Overrides the section 9 defaults without a schema change |
| `GuardianConsent` | Consent wording version, approval link lifetime, consent text |
| `Commerce` | Programme price (£3,499), maintenance price (£19.99), 7-day grace period, maintenance start offset |
| `Msm` | Business name, public domain, contact email/phone/WhatsApp, social links |
| `Integrations` | GoCardless and GoHighLevel credentials |

Credentials are never committed. Supply them through user secrets in development and
environment variables in deployment.

## Decisions still open

Carried from specification section 52. These are configuration decisions, not missing
requirements.

- **Database provider** — abstracted; see the Database section above.
- **Media/object storage provider** — `LocalDiskMediaStorageService` is registered for
  now. Azure Blob, S3 or R2 slot in behind `IMediaStorageService` by changing that one
  registration. Local disk does not survive a multi-server deployment or a container
  rebuild, so this needs deciding before go-live.
- **Malware scanning** — specification section 38 asks for it where the hosting
  infrastructure supports it. Uploads are currently validated by decoding rather than
  scanned; wire a scanner in once hosting is chosen.
- **Final maintenance price** — `Commerce:MaintenancePrice`, placeholder £19.99.
  Existing subscriptions keep the price agreed when they started.
- **Maintenance start date** — `Commerce:MaintenanceStartsAfterDays`, currently 0.
- **Production domain** — `Msm:PublicDomain`.
- **MSM contact details** — `Msm:ContactEmail`, `ContactPhone`, `WhatsApp`, to be
  supplied by MSM. Until they are set, the public portfolio shows the enquiry form but
  no direct contact options, and the footer omits them.
- **Model Board entitlement** — specification section 18 makes board eligibility depend
  on an active entitlement as well as publication. Publication and the visibility flag
  are enforced now; the entitlement half arrives with the maintenance subscription in
  Phase 8.
- **Guardian consent wording** — `GuardianConsent:ConsentText`, to be supplied or
  approved by MSM. A clearly-labelled placeholder is shown until then. Each approval
  records the version agreed (`GuardianConsent:CurrentVersion`), so changing the wording
  later cannot retrospectively alter what was consented to.
- **Email delivery** — no provider is configured. Guardian approval emails are only
  logged. Either connect GoHighLevel in Phase 9 or register a real `IEmailSender`
  before go-live.
- **GoCardless** — `Integrations:GoCardless:AccessToken` and `WebhookSecret`. The HTTP
  client is written but unverified; see `docs/gocardless-verification.md`. Leave the
  token unset until it is checked, and the stub takes over.
- **GoHighLevel** — `Integrations:HighLevel:ApiKey` and `LocationId`, plus the six
  custom fields listed in `docs/gohighlevel-verification.md`. Same position as
  GoCardless: written, unverified, stub by default.
- **Image and video size limits** — `Media:MaxImageBytes`, `Media:MaxVideoBytes`.

### One judgement call worth confirming

For a client born on 29 February, the application treats their birthday in a non-leap
year as 1 March, so they remain 17 for that extra day and still require guardian
consent. .NET's built-in date arithmetic would instead make them an adult on
28 February. Because this gates a safeguarding control, the boundary was set so that an
error requires consent rather than skips it. Worth confirming against MSM's legal
wording.
