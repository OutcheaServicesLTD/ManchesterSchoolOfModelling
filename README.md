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

All ten phases from specification section 51 are complete. Four deployment decisions
remain open — see [Decisions still open](#decisions-still-open) and
[`docs/deployment.md`](docs/deployment.md).

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
| 10 | Audit, security, permissions, upload, payment, mobile, accessibility and performance hardening | Done |

### Version 2

Four items requested after the platform went into real use:

| Item | Scope | State |
| ---- | ----- | ----- |
| 1 | Image optimisation | Was already mostly built (multi-size JPEG renditions, lazy loading, responsive `srcset`). Added: a WebP rendition, offered automatically to any browser that says it can use one via content negotiation on the existing `/media` URLs — no template changes needed, roughly 60% smaller than the JPEG for the same photograph in testing |
| 2 | Auto-preview after retoucher upload | Submitting now carries a portfolio straight to viewable, reusing the same checks the old manual "Mark in viewing" click applied. That click still exists for a legacy portfolio stuck mid-review, but nothing new ever needs it |
| 3 | Stripe subscriptions | A second, optional recurring layer alongside the existing £99 one-off purchase — GoCardless is untouched. A client starts and manages it themselves from the client portal; Stripe Checkout and the Stripe Customer Portal do almost all of the work. See below |
| 4 | Portfolio URL collisions | Was already collision-safe (`SlugService` auto-suffixes a taken slug). Changing a slug by hand is now Super Admin only, so an ordinary Admin cannot break a link an agency already has |

## Just want to look at the site?

No terminal needed.

1. Install the **.NET 10 SDK** from
   [dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0).
   Choose the button marked **SDK**, not the one marked *Runtime* — only the SDK can
   build the site, and picking the wrong one is the usual reason "I installed .NET" still
   does not work.
2. Restart the computer.
3. Double-click **`Start Website.bat`** in this folder.

A black window opens and stays open — that is the site running, so leave it there. After
about a minute your browser opens at the Model Board. Close the black window to stop.

On macOS or Linux, run `./start-website.sh` instead.

Sign in at `/account/login` with `superadmin@msm.local` and `Dev!Passw0rd`. That account
exists only in local development.

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

### Which address

`dotnet run` uses the `http` profile, so the application is at:

```
http://localhost:5213
```

`dotnet run --launch-profile https` adds `https://localhost:7165`, which needs a trusted
development certificate (`dotnet dev-certs https --trust`).

In VS Code, **F5** runs "Run MSM Portfolio (http)" and opens the Model Board once Kestrel
reports it is listening. That configuration deliberately uses plain HTTP: it is the one
that works everywhere, including a Codespace or a container.

### If the browser says the page isn't working

| What you see | What it means |
| ------------ | ------------- |
| `ERR_CONNECTION_REFUSED` | Nothing is listening on that port. Check the terminal for `Now listening on:` and use the address it prints. |
| `ERR_EMPTY_RESPONSE` | Something accepted the connection and closed it without replying — almost always `http://` sent to an HTTPS port. Try `https://` on the same port, or use the HTTP one. |
| A certificate warning | Expected on `https://localhost:7165` until you run `dotnet dev-certs https --trust`. |

The port is whatever the terminal prints, not a fixed number — if an editor or debug
configuration opens a different one (8080 is a common template default), it will not
find the application no matter how long you wait.

Running inside a container, bind to all interfaces rather than loopback so the port can
be forwarded out:

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://0.0.0.0:8080 \
    dotnet run --project src/Msm.Portfolio.Web --no-launch-profile
```

## Tests

```bash
dotnet test
```

## Deploying

- [`docs/preview-deployment.md`](docs/preview-deployment.md) — putting a **preview**
  online for MSM to click through, without a terminal. A demonstration site: payments
  take no money, emails are not delivered, and no real client's details belong in it.
- [`docs/deployment.md`](docs/deployment.md) — the **live** system: required
  configuration, the readiness guard, migrations, proxies and rate limits, and the four
  things outstanding before go-live.

The `Dockerfile` is host-agnostic and runs the same on Render, Fly, Azure Container Apps
or a plain Linux server, so choosing a host now does not lock the project in.

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

**No email provider is configured yet**, so `IEmailSender` is a logging implementation:
in development the approval link appears in the application log, and outside development
it logs an error rather than silently dropping a real guardian's email. The readiness
guard treats this as fatal outside Development, so it cannot reach production unnoticed.

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

Served from **`model-portfolio.manchesterschoolofmodelling.co.uk`**, a subdomain of MSM's
existing site, so portfolios can be hosted and moved without touching the main website.

| Route | Page |
| ----- | ---- |
| `/` | Redirects to the Model Board — the portfolio domain has no marketing homepage |
| `/models` | Model Board |
| `/{slug}` | A model's portfolio, e.g. `/emma-johnson` |

`Msm:PublicDomain` carries this value, and it is not cosmetic: every outbound link is
built from it — the address shared with an agency, the social preview tags, the CRM
contact's portfolio URL and **the guardian's approval link**. Set wrongly, the site still
looks fine and only the recipients of its links ever discover otherwise, so the readiness
guard refuses to start a deployment where it is still a local address. Locally it is
overridden to `http://localhost:5213` in `appsettings.Development.json`, so the links on
the dashboards are ones that actually open on the machine you are working on.

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

### Contact routes through the page, never through the model's own details

The enquiry form collects the *enquirer's* details. The model's own email and telephone
are never shown on the page and are never posted back through it. The model enquired
about is taken from the portfolio in the URL rather than the posted form, so an enquiry
cannot be redirected at someone else, and the server re-checks the portfolio is published
before storing anything.

The enquiry goes to the model and nowhere else, at MSM's instruction: an agency that
contacts a model is dealing with that model. **MSM keeps no copy and no member of staff
is notified.** The model receives it by email and sees it on their own dashboard.

**A model under eighteen is the exception.** Their enquiries go to the guardian whose
consent already governs the portfolio, never to the child. With no guardian address on
file there is nowhere safe to send it, and the enquiry is not delivered at all.

Because nothing is stored, **delivery is not optional**. `IEmailSender.SendAsync` reports
whether the message was delivered, and an enquiry that cannot be sent — no provider, a
provider that refuses, no address to send to — comes back to the agency as "we could not
deliver your enquiry just now", with what they typed still in the form. Thanking them for
a message that reached nobody, with no record of it anywhere, would lose the enquiry and
leave them waiting on a reply that is never coming.

The `Enquiry` entity and its table are left in place but **dormant** — nothing writes to
them. Dropping the table would destroy the enquiries taken before this decision, and
keeping it means the decision can be reversed.

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

## What the website sells

**£99, once, and the portfolio is public for a year.** That is the only payment. The
price and the length of the term are both configuration — `Commerce:PortfolioPrice` and
`Commerce:PortfolioTermDays` — because both are commercial decisions rather than facts
about the software.

Buying sets `Portfolio.ExpiresAt` a year out. Buying again **adds** a year to whatever is
left rather than replacing it: somebody who renews two months early has paid for a year
and should get a year. When the date passes, `PortfolioTermWorker` takes the portfolio
down — hourly, because a year runs out by the passage of time and nothing in a request
can be relied on to notice. A model with a month left is told on their dashboard.

A portfolio with **no** expiry never expires. Those are the ones sold under the old
£3,499 programme price, which carried no term; reading a null as "expired long ago" would
take down every one of them on the first run.

The programme product is retired rather than rewritten, and orders still reference it.
Restating a past £3,499 sale as a sale of something else would falsify the record.

## Maintenance and the grace period

**Off.** `Commerce:MaintenanceEnabled` is false, so no subscription is opened on purchase
and none of what follows can fire. It is a switch rather than a deletion, so MSM can go
back to charging maintenance without the work being rebuilt.

When it is on: the monthly maintenance charge is a separate product. A subscription
record is created when the portfolio is purchased, fixing the price agreed that day so a
later change cannot alter it, and starting at the offset in
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

## The interface

Built to the approved *Luxury Modelling Portfolio Platform* design: warm ivory rather
than generic white, near-black with a warm undertone rather than pure black, Bodoni Moda
for display and DM Sans for everything else, square corners throughout, and wide
letter-spacing on small uppercase labels.

One stylesheet, `wwwroot/css/msm.css`, covers the whole application — the public
portfolios agencies see and the staff portal the studio works in. They share a design
language; the portal is simply denser, because staff are in it all day.

### The wordmark

MSM's registered mark appears in the public header and footer, in the staff portal
header, and on the sign-in page. It is rendered by the platform and is never
client-editable (specification section 16).

Two files, not one recoloured with a CSS filter: this is a fine Bodoni wordmark built
from hairline strokes, and a filter muddies exactly the detail that makes it read as
expensive. `wwwroot/img/msom-logo.png` is used on light backgrounds and
`msom-logo-white.png` on dark, with the stylesheet showing whichever suits the current
appearance and the alt text sitting on the pair so a screen reader hears the name once.
Both were trimmed from the supplied artwork and given a real alpha channel, so they sit
on any ground without a white box around them.

The browser-tab icon is the initial **M** on a square. The full wordmark is five times
wider than it is tall and would be an illegible smudge at 32 pixels. Light and dark PNGs
are declared with a media query, an `.ico` covers browsers that ask for `/favicon.ico`
regardless, and the Apple touch icon carries the ivory ground itself because iOS
composites it onto a solid colour.

### Bootstrap is not loaded

Its rounded corners, blue accents and system font stack fight this design at every turn.
The class names it would have provided — `btn`, `card`, `row`, `form-control` and the
rest — are implemented in `msm.css` instead, in the editorial language above. Views keep
familiar, readable markup and look nothing like a default scaffold, and the application
ships one 44KB stylesheet rather than a framework it uses a tenth of. Bootstrap's
vendored files, the scaffold's `site.css` and `site.js` are deleted rather than left
sitting unreferenced.

### Light and dark

Three states, not two. An explicit choice is remembered; its absence means follow the
operating system, which the stylesheet handles through `prefers-color-scheme` alone. The
toggle sets `data-theme` on the root element, and the script that applies it is loaded
synchronously in `<head>` — deferred, the browser would paint an ivory page and then
repaint it black.

### Fonts are self-hosted

Both families are served from `wwwroot/fonts` rather than from Google. The Content
Security Policy allows fonts from this origin only, and a portfolio that renders in
Times because a third party is slow or blocked is not the product MSM is selling. Both
are variable fonts, so one file covers each family's whole weight range — three files,
144KB, no external request anywhere on the site.

### No inline script or handlers

`script-src 'self'` with no inline exception is what makes the policy worth having, so
the confirmation dialogs on destructive actions, the onboarding form's profile-type
refresh and the client's copy-link button are declared with data attributes and bound in
`wwwroot/js/msm.js`. This matters more than it sounds: an inline `onsubmit` handler is
silently refused by the browser and **the form submits anyway**, so a confirmation
written that way disappears from exactly the destructive action it was added to guard.

### Verified

Every page was driven in a real browser at 390px, 768px and 1400px, in both appearances:
no external requests, no console errors, no sideways scroll, and the chosen appearance
surviving a reload.

## Hardening

Specification section 43, plus the parts of sections 35 to 42 that are not visible in a
feature.

### The application refuses to start half-configured

Several parts of this application deliberately ship with stand-ins: payments take no
money, the CRM logs instead of sending, guardian emails only reach the log, media sits
on local disk. Each is the right default while the corresponding account or decision is
outstanding, and each fails quietly and expensively if it reaches production unnoticed.

So outside Development the application checks its own configuration at startup and
**refuses to start** when a stand-in that matters is still in place — missing payment
credentials, no webhook secret, stubbed email, local disk media. Lesser problems are
logged as warnings instead. `ALLOW_INCOMPLETE_DEPLOYMENT=true` overrides it for a
staging environment, which should only ever be done knowingly.

The full list, and what each deployment has to set, is in
[`docs/deployment.md`](docs/deployment.md).

### Rate limits

| Endpoint | Limit per address |
| -------- | ----------------- |
| `POST /account/login` | 10 per 5 minutes |
| `POST /onboarding`, `GET`/`POST /guardian/approve/{token}` | 30 per 10 minutes |
| `POST /{slug}/enquire` | 5 per 10 minutes |
| `POST /webhooks/gocardless` | 300 per minute |

Sized against how the application is genuinely used rather than as low as possible: a
studio onboarding a queue of clients after a shoot must never reach one.

Sign-in is the tightest because Identity's account lockout is **per account** and does
nothing about one source working through a list of addresses. The guardian approval link
is limited on `GET` as well as `POST`, unlike the other anonymous forms, because the
token in that URL is the only thing standing between a stranger and a minor's consent
record — guessing at it must not be free. The webhook limit is high on purpose:
providers retry a backlog in bursts, so it bounds a flood rather than shaping normal
delivery.

Over-limit requests are rejected with `429` and a `Retry-After` header rather than
queued — queuing would hold connections open under exactly the load the limit exists to
shed — and each rejection is logged.

Limits are keyed by client IP, so a deployment behind a proxy must honour forwarded
headers or every limit becomes global. That is called out in the deployment
documentation.

### Response headers

`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy` and
a Content Security Policy, on every response rather than selected pages: a header that
is only sometimes present protects only sometimes, and the pages most worth protecting
are the ones carrying a client's private media.

The CSP is restrictive because it can be — everything is same-origin, including media,
so no external origin needs allowing. `'unsafe-inline'` is permitted for **styles only**,
because the gallery sets each image's aspect ratio inline to stop the page jumping as
thumbnails load. Scripts carry no such exception, and a test asserts they never acquire
one.

`Referrer-Policy` matters more here than it looks: a portfolio slug identifies a real
person, and trimming the referrer to the origin stops it being handed to every site a
visitor clicks through to.

### Data protection keys live in the database

Sign-in cookies and anti-forgery tokens are protected by a key ring. The framework
default is a folder in the user profile, which a container discards on every rebuild and
does not share between instances — so every deployment would sign all staff out, and
anti-forgery would fail whenever a request landed on a different instance from the one
that rendered the page. The keys are therefore persisted through the same `DbContext` as
everything else. Verified by signing in, restarting the process, and finding the session
still valid.

### Health checks

`GET /health` is anonymous, exempt from rate limiting, and reports healthy only when the
database is reachable. It is unthrottled deliberately: a probe that could be rate limited
would report a healthy instance as unhealthy under load and take it out of rotation at
the worst possible moment.

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
| `Commerce` | Portfolio price (£99) and term (365 days), whether maintenance is charged at all (off), maintenance price (£19.99), 7-day grace period, maintenance start offset |
| `Msm` | Business name, public domain, contact email/phone/WhatsApp, social links |
| `Integrations` | GoCardless, GoHighLevel and Stripe credentials |

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
- **Maintenance** — not charged. `Commerce:MaintenanceEnabled` is false and the £99 is
  the only payment. The price, grace period and start offset settings are all still there
  and take effect the moment it is switched back on.
- **MSM contact details** — `Msm:ContactEmail`, `ContactPhone`, `WhatsApp`, to be
  supplied by MSM. Until they are set, the public portfolio shows the enquiry form but
  no direct contact options, and the footer omits them.
- **Guardian consent wording** — `GuardianConsent:ConsentText`, to be supplied or
  approved by MSM. A clearly-labelled placeholder is shown until then. Each approval
  records the version agreed (`GuardianConsent:CurrentVersion`), so changing the wording
  later cannot retrospectively alter what was consented to.
- **Email delivery** — no provider is configured. Guardian approval emails are only
  logged. Either route guardian messaging through GoHighLevel or register a real
  `IEmailSender` before go-live. The readiness guard blocks a production start until
  one exists.
- **GoCardless** — `Integrations:GoCardless:AccessToken` and `WebhookSecret`. The HTTP
  client is written but unverified; see `docs/gocardless-verification.md`. Leave the
  token unset until it is checked, and the stub takes over.
- **GoHighLevel** — `Integrations:HighLevel:ApiKey` and `LocationId`, plus the six
  custom fields listed in `docs/gohighlevel-verification.md`. Same position as
  GoCardless: written, unverified, stub by default.
- **Stripe** — `Integrations:Stripe:SecretKey`, `WebhookSecret` and `PriceId` (the
  recurring Price created for the Portfolio Maintenance product in the Stripe
  Dashboard). Optional: the client portal simply does not offer a subscription until
  all three are set, and the stub takes over in the meantime — same position as
  GoCardless and GoHighLevel, and equally unverified against a real Stripe account.
  `webhooks/stripe` needs a webhook endpoint configured in the Stripe Dashboard for
  `checkout.session.completed`, `invoice.paid`, `invoice.payment_failed` and
  `customer.subscription.deleted`.
- **Image and video size limits** — `Media:MaxImageBytes`, `Media:MaxVideoBytes`.

### One judgement call worth confirming

For a client born on 29 February, the application treats their birthday in a non-leap
year as 1 March, so they remain 17 for that extra day and still require guardian
consent. .NET's built-in date arithmetic would instead make them an adult on
28 February. Because this gates a safeguarding control, the boundary was set so that an
error requires consent rather than skips it. Worth confirming against MSM's legal
wording.
