---
name: ".NET backend and data instructions"
description: "Use when changing ASP.NET Core endpoints, background services, persistence, security, migrations, or xUnit tests in C#."
applyTo: ["src/**/*.cs", "tests/**/*.cs", "tools/**/*.cs"]
---

# .NET backend and data

- Keep the API as minimal APIs composed in [`Program.cs`](../../src/EventWall.Api/Program.cs); put reusable behavior in the existing `Services`, `Data`, `Security`, or `Models` areas rather than introducing a second endpoint architecture.
- Preserve the public/admin boundary enforced by `DeploymentRole`: admin-only routes, authentication, provider credentials, collectors, seeding, and migrations must not become available in the public deployment.
- Route connector posts through [`EventPostIngestor`](../../src/EventWall.Api/Services/EventPostIngestor.cs) and keep moderation state changes consistent with [`ModerationPolicy`](../../src/EventWall.Api/Services/ModerationPolicy.cs). New or materially changed incoming content stays pending until staff approval; public endpoints expose only live posts.
- Use `IDbContextFactory<EventWallDbContext>` to create short-lived contexts per operation, dispose them with `await using`, and use `AsNoTracking` for read-only queries where appropriate. Pass request/worker cancellation tokens through EF Core and provider HTTP calls; see [`EventWallDbContext`](../../src/EventWall.Api/Data/EventWallDbContext.cs).
- Add schema changes as incremental EF Core migrations. Hosted schema changes follow [database operations](../../docs/operations/database.md); do not grant the running app schema-administration privileges.
- Keep provider secrets in admin-only configuration. Use the existing data-protection flow for persisted refresh tokens, and never log credentials or tokens. Preserve connector enablement, policy-approval, and X spend-budget gates.
- Handle expected provider, I/O, and database failures explicitly using the existing source-health and logging patterns; do not hide failures behind broad catches or success-shaped fallbacks.
- Add backend tests to `tests/EventWall.Api.Tests` using its xUnit conventions. See [the shared instructions](../copilot-instructions.md) for verified build and focused-test commands.
