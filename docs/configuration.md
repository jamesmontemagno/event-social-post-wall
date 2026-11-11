# Environment settings

ASP.NET Core settings use `:` in JSON/user-secrets and `__` in environment
variables: `Event:Name` becomes `Event__Name`. Environment variables override
checked-in defaults. Restart affected apps after changing settings. Use identical
event values on all three roles.

The application does **not** automatically load `.env` files. Vite's `VITE_*`
variables are compiled into browser code: they must never contain a secret.

## Event and roles

| Environment setting | Default | Apply to / behavior |
| --- | --- | --- |
| `Deployment__Role` | `Public` | All: `Public`, `Submission`, or `Admin` |
| `ASPNETCORE_ENVIRONMENT` | Host default | `Development` locally, `Staging` or `Production` hosted |
| `Event__Slug` | `communityevent` | All: lowercase ASCII letters, digits, hyphens; 1-80 characters; one database per slug |
| `Event__Name` | `Community Event` | All: 1-160 characters, no control characters; display name and literal social search phrase |
| `Event__QuestionHashtag` | `#communityevent` | All: `#` plus 1-80 letters/digits/underscores; display and X/Bluesky/Mastodon collection |
| `Event__SubmissionsInitiallyOpen` | `false` | Admin/setup: initial seed only; subsequent intake changes belong to staff |
| `Public__SubmissionSiteUrl` | Local loopback in Development only | Required on hosted Public; also set on Admin for its shortcut |
| `Public__WallSiteUrl` | None | Admin shortcut; missing/invalid values disable the link with a warning |
| `Public__CorsOrigins__0` (and `__1`, ...) | Empty | Public only, explicit origins for an optional separate static wall host |

Site URLs must be absolute HTTPS without credentials; loopback HTTP is allowed
only in Development. Missing hosted question URLs fail startup rather than send
attendees to somebody else's event. Public links appear after metadata loads.

Changing the event **name** updates the seeded event on Admin startup. Changing
the **slug** on an existing database fails: provision a separate database instead.
Changing search terms does not backfill posts or reset connector checkpoints.
Twitch and YouTube always require exact, case-insensitive `#question`.

## Database and authentication

| Setting | Default / scope |
| --- | --- |
| `ConnectionStrings__EventWall` | Required hosted; use that role's restricted SQL connection, never the SQL administrator |
| `Database__Provider` | SQLite without a connection; otherwise SQL Server. Explicit values: `Sqlite`, `SqlServer` |
| `Database__Purpose` | Inferred `Demo` in Staging, `Production` in Production |
| `Database__ApplyMigrations` | `true` only in Development by default; keep `false` hosted |
| `STAFF_PASSCODE` | **Secret, Admin only.** At least 12 characters; missing/weak configuration closes staff access |
| `DATA_PROTECTION_KEY_PATH` | Admin/Submission: persistent, private directory outside deployed assets |

Hosted database-name guards intentionally require `EventWallStaging` for Demo
purpose and `EventWallProd` for Production. The names protect destructive tooling;
they are not event branding. Staging's Demo purpose does not itself enable simulation.
Hosted SQLite is not a supported baseline; see [self-hosting](deployment/self-hosting.md).

Azure App Service's connection string named `EventWall` with type `SQLAzure`
maps to the same ASP.NET connection setting. Do not set conflicting copies.

## Simulation

| Setting | Default | Meaning |
| --- | --- | --- |
| `Demo__Enabled` | `false` API; `true` local Aspire | Simulate all social sources without provider requests |
| `Demo__AllowInProduction` | `false` | Explicit safety override for an intentional hosted demonstration |
| `Demo__IntervalSeconds` | `7` | Clamped 2-300 seconds between generated posts |
| `Demo__MaxGeneratedPosts` | `120` | Clamped 0-2000 new generated posts per worker run |

Demo flags must agree across roles. Turning simulation off does not erase fixtures.
The Azure template's `demoEnabled` applies only to staging.

## Social providers: Admin only

Every provider supports these independent gates, all `false` by default:

- `Social__<Provider>__Enabled`: permit collection when all other requirements pass.
- `Social__<Provider>__PolicyApproved`: operator has reviewed display, retention,
  deletion, and other platform requirements.
- `Social__<Provider>__AnalyticsApproved`: permit analytics for that provider.
- `Social__<Provider>__ExportContentApproved`: permit its content/identity in exports.

Provider names are `Twitch`, `YouTube`, `X`, `Bluesky`, `Mastodon`.

| Provider | Additional settings |
| --- | --- |
| [Twitch](platforms/twitch.md) | `ClientId`, **`ClientSecret`**, `UserId`, `BroadcasterUserId`, **`RefreshToken`** |
| [YouTube](platforms/youtube.md) | `ClientId`, **`ClientSecret`**, **`RefreshToken`**, `LiveChatId`; also Start/Stop in admin |
| [X](platforms/x.md) | **`BearerToken`**, `SpendApproved`, `MaximumDailySpendUsd`, optional `PollIntervalSeconds` (300; clamped 60-3600) |
| [Bluesky](platforms/bluesky.md) | No credentials; the public Jetstream endpoint is built into the connector |
| [Mastodon](platforms/mastodon.md) | `InstanceUrl`, optional **`AccessToken`**, `MediaHosts__0`, `PollIntervalSeconds` (180; clamped 60-3600) |

Prefix each setting with `Social__<Provider>__`. Never set these on Public or
Submission. Analytics/export permission is not implied by collection permission.

## Cloudflare Turnstile: Submission only

See [Turnstile setup](platforms/turnstile.md).

| Setting | Meaning |
| --- | --- |
| `Turnstile__SiteKey` | Public widget key |
| `Turnstile__SecretKey` | **Secret** server verification key |
| `Turnstile__Required` | Fail startup if keys are absent; recommended hosted |
| `Turnstile__Hostnames__0` (and `__1`, ...) | Accepted verification hostnames, no scheme or path |

Both keys absent disables Turnstile. Only one key configured fails startup.

## Build and tooling settings

| Setting | Purpose |
| --- | --- |
| `VITE_SITE_ROLE` | `public` or `admin`; Submission is not a Vite build |
| `VITE_API_BASE_URL` | Empty means same-origin. Only Public can use an explicitly configured separate API |
| `VITE_BASE_PATH` | Static asset base for an optional subpath-hosted Public build |
| `API_PROXY_TARGET` | Local Vite API proxy; set automatically by Aspire |
| MSBuild `EventWallSiteRole` | `public`, `admin`, or `submission`; must match the package's assets and runtime role |
| `DOCS_BASE` | VitePress base path; `/` locally, `/repository-name/` for project Pages |

Database setup consumes **process-only secrets**:
`EVENTWALL_DEPLOYMENT_SQL`, `EVENTWALL_PUBLIC_SQL_PASSWORD`,
`EVENTWALL_SUBMISSION_SQL_PASSWORD`, and `EVENTWALL_ADMIN_SQL_PASSWORD`.
It also reads `Event__Slug`, `Event__Name`, and `Event__SubmissionsInitiallyOpen`.
It does not load API appsettings or create provider accounts.

The Azure/GitHub variables are listed in
[deployment](deployment/azure.md) and [Actions](deployment/github-actions.md).
