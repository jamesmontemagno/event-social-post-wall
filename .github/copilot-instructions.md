# Copilot instructions

## Build, test, and lint

Requirements: .NET 10 SDK, Node.js 22+, and npm.

Run the local stack with Aspire:

```powershell
npm ci --prefix src\EventWall.Web
dotnet run --project src\EventWall.AppHost
```

Build the solution and web app:

```powershell
dotnet build EventWall.slnx
npm run build --prefix src\EventWall.Web
```

Run tests and lint:

```powershell
dotnet test EventWall.slnx
dotnet test tests\EventWall.Api.Tests\EventWall.Api.Tests.csproj --filter "FullyQualifiedName=EventWall.Api.Tests.BlueskyJetstreamTests.Parses_post_commits_and_blob_images"
npm test --prefix src\EventWall.Web
npm test --prefix src\EventWall.Web -- -t "does not expose staff UI through a public query string"
npm run lint --prefix src\EventWall.Web
```

## Architecture

- The backend is an ASP.NET Core minimal API with EF Core. SQLite is used for local runs; Azure uses SQL Server. Routes, role-specific service registration, and startup behavior are composed in [the API Program.cs](../src/EventWall.Api/Program.cs); entities and schema configuration are in [EventWallDbContext.cs](../src/EventWall.Api/Data/EventWallDbContext.cs).
- [The web app](../src/EventWall.Web/src/main.tsx) builds as `public` or `admin` via `VITE_SITE_ROLE`. Submission is a separate Blazor Server site. The API validates matching role-specific assets. The local-only [Aspire AppHost](../src/EventWall.AppHost/Program.cs) runs three APIs and two Vite sites; Azure uses [Bicep](../infra/main.bicep).
- Public, Submission, and Admin are separate deployments. Public serves only the host wall and an external question link; Submission alone accepts attendee questions. Admin serves staff moderation and collectors. Keep staff authentication, CSRF, private configuration, and provider credentials on Admin.
- Data flows into moderation before public display. Hosted migrations use the deployment identity and separate restricted runtime users; only local Development applies migrations at startup. See [architecture](../docs/architecture.md) and [database operations](../docs/operations/database.md).

## Repository-specific conventions

- Route connector posts through [EventPostIngestor](../src/EventWall.Api/Services/EventPostIngestor.cs). It centralizes event-term filtering, deduplication, safe metadata, blocked-author handling, and persistence. New or materially changed source content must remain pending until staff approval; source deletions/withdrawals must stop appearing on the public wall.
- Social collection is off until explicitly configured and policy-approved. Preserve provider-specific spend limits and approval gates; do not make real collection the default.
- Keep demo data isolated from event data. Local demo and non-demo modes use separate SQLite files; hosted staging/demo and production use separate databases. Use the guarded demo reset tooling rather than clearing or reseeding production data.
- Add incremental EF Core migrations. Follow [database operations](../docs/operations/database.md); never use a runtime account to apply hosted schema changes.
- Build and deploy matching frontend/API roles together. Never deploy admin assets to the public app or configure the admin build to use a cross-origin API.
- Use [EventConfiguration](../src/EventWall.Api/Services/EventConfiguration.cs) for event terms. Keep branding, hashtags, and site URLs configurable; never restore personal/live-event defaults.
- Documentation lives in [docs](../docs/index.md); validate it with `npm run build --prefix docs`. Keep Azure and Pages workflows opt-in and production approval-gated.
