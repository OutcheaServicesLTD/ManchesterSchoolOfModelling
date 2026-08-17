# Deployment

What has to be true before this application serves a real client, and what the
application itself will refuse to start without.

## The readiness guard

Outside Development, the application checks its own configuration at startup and
**refuses to start** if a development stand-in is still in place. This is deliberate: a
half-configured deployment of this application takes money it cannot process, or
publishes a minor's portfolio without their guardian ever being asked. Failing loudly at
startup is far cheaper than discovering either later.

| Check | Fatal | Why |
| ----- | ----- | --- |
| GoCardless access token missing | Yes | No payment can be taken |
| GoCardless webhook secret missing | Yes | No webhook can be trusted, so payments never confirm |
| `IEmailSender` is the logging stub | Yes | Guardian approval requests are never delivered, so under-18 clients can never complete |
| Media storage is local disk | Yes | Files do not survive a container rebuild and are not shared between instances |
| GoHighLevel API key missing | No | The application works; MSM's CRM simply falls behind |
| `Database:MigrateOnStartup` is true | No | Two instances starting together race each other |
| `Msm:ContactEmail` missing | No | Public portfolios show the enquiry form but no direct contact details |

Non-fatal problems are logged as warnings on every start.

To deploy knowingly with a fatal problem outstanding — a staging environment with no
payment provider, for example — set:

```
ALLOW_INCOMPLETE_DEPLOYMENT=true
```

The application then logs that checks were skipped and starts. This should never be set
on the environment MSM's clients use.

## Configuration

Everything below is supplied through environment variables in deployment. Nested keys
use a double underscore, so `Database:ConnectionString` becomes
`Database__ConnectionString`. **No credential belongs in `appsettings.json` or in
source control.**

### Required

```
ASPNETCORE_ENVIRONMENT=Production

Database__Provider=PostgreSql            # or SqlServer
Database__ConnectionString=...
Database__MigrateOnStartup=false

Media__StorageProvider=...               # once object storage is chosen
Msm__PublicDomain=https://portfolio.example.com
Msm__ContactEmail=...
Msm__ContactPhone=...

Integrations__GoCardless__AccessToken=...
Integrations__GoCardless__Environment=live
Integrations__GoCardless__WebhookSecret=...
Integrations__HighLevel__ApiKey=...
Integrations__HighLevel__LocationId=...

Seed__SuperAdmin__Email=...              # first start only; see below
Seed__SuperAdmin__Password=...
```

### The first Super Admin

The development Super Admin (`superadmin@msm.local`) is created **only** in Development.
In any other environment, set `Seed__SuperAdmin__Email` and `Seed__SuperAdmin__Password`
before the first start, or no owner account is created and nobody can sign in. Remove
both variables once the account exists and its password has been changed.

## Database

`Database:MigrateOnStartup` should be `false` in production. Migrating from inside the
web process races when more than one instance starts at once. Migrate as a separate
deployment step instead:

```bash
dotnet ef database update --project src/Msm.Portfolio.Web
```

**The committed migrations were generated for SQLite**, the development default. A
migration's SQL is not portable between providers, so regenerate them once the provider
is chosen, with `Database:Provider` already set to the target:

```bash
dotnet ef migrations remove --project src/Msm.Portfolio.Web    # repeat per migration
dotnet ef migrations add InitialSchema --project src/Msm.Portfolio.Web \
    --output-dir Data/Migrations
```

Back the database up before every deployment. It holds the client records, orders,
payment history and audit log; the audit log in particular is intended to outlive the
things it describes.

## Behind a proxy or load balancer

Rate limits are keyed by client IP address. Behind a reverse proxy, every request
arrives from the proxy's address unless forwarded headers are honoured — which turns
each per-address limit into a single global one, so one abusive source would lock out
every genuine visitor.

Configure forwarded headers, and restrict them to the proxy's own address so a client
cannot spoof its way past a limit:

```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    KnownProxies = { IPAddress.Parse("<the proxy's address>") }
});
```

This is left unconfigured on purpose: the correct value depends on hosting that has not
been chosen, and a wrong `KnownProxies` is worse than none.

Also set `AllowedHosts` to the real domain rather than `*`.

## Data protection keys

Sign-in cookies and anti-forgery tokens are protected by a key ring, which is held **in
the database**. The framework default is a folder in the user profile; in a container
that folder is discarded on every rebuild and is not shared between instances, which
would sign every member of staff out on each deployment and fail anti-forgery whenever a
request landed on a different instance from the one that rendered the page.

Nothing needs configuring for this — it follows the database — but two consequences are
worth knowing:

- The application name is fixed to `Msm.Portfolio`. Changing it produces a different key
  ring and invalidates every existing session.
- Restoring an old database restores old keys. That is correct, and is why the key rows
  should be treated as a secret in backups.

## HTTPS

HSTS and HTTPS redirection are enabled outside Development. Terminate TLS at the proxy
or the platform; the application does not manage certificates.

`Content-Security-Policy` includes `upgrade-insecure-requests` outside Development, so a
mixed-content resource is upgraded rather than silently blocked.

## Health checks

```
GET /health
```

Anonymous, exempt from rate limiting, and reports `Healthy` only when the database is
reachable. Point the platform's liveness and readiness probes at it.

It is deliberately unthrottled: a probe that could be rate limited would report a
healthy instance as unhealthy under load and take it out of rotation at exactly the
wrong moment.

## Rate limits

Sized so genuine studio and agency use never reaches them. They exist to stop automated
abuse, not to ration normal work.

| Endpoint | Limit |
| -------- | ----- |
| `POST /account/login` | 10 per 5 minutes per address |
| `POST /onboarding`, `GET`/`POST /guardian/approve/{token}` | 30 per 10 minutes per address |
| `POST /{slug}/enquire` | 5 per 10 minutes per address |
| `POST /webhooks/gocardless` | 300 per minute per address |

Requests over a limit are rejected with `429` and a `Retry-After` header rather than
queued, and each rejection is logged with the path and address. Queuing would hold
connections open under exactly the load the limit exists to shed.

The webhook limit is high because providers retry a backlog in bursts; it bounds a
flood rather than shaping normal delivery.

## Media storage

`LocalDiskMediaStorageService` is registered until object storage is chosen, and the
readiness guard treats it as fatal outside Development. Object storage slots in behind
`IMediaStorageService` by changing one registration in `Program.cs`.

Media is served through `/media/{assetId}/{variant}` and never from `wwwroot`, so
whichever store is chosen must be **private**. A public bucket would bypass the access
rules entirely and expose every client's unpublished photographs.

## Webhook endpoint

Register `https://<domain>/webhooks/gocardless` with GoCardless and set
`Integrations__GoCardless__WebhookSecret` to the secret shown when the endpoint is
created. With no secret configured every webhook is refused, so payments will never
confirm.

## Before go-live

Four things are outstanding and none of them are code:

1. **Object storage** — see above.
2. **An email provider** — guardian approval requests are only logged until one exists.
3. **Malware scanning on uploads** (specification section 38) — uploads are validated by
   decoding, which rejects a renamed non-image but is not a scanner.
4. **Provider verification** — the GoCardless and GoHighLevel HTTP clients were written
   against documented behaviour but never exercised against the providers, whose APIs
   were unreachable from the build environment. Work through
   [`gocardless-verification.md`](gocardless-verification.md) and
   [`gohighlevel-verification.md`](gohighlevel-verification.md) first.
