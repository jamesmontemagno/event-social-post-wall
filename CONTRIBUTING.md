# Contributing

Thank you for helping improve Event Social Post Wall.

1. Read the [architecture](docs/architecture.md) and [local setup](docs/getting-started.md).
2. Open an issue for substantial changes before starting implementation.
3. Use a feature branch, keep changes focused, and include tests for changed behavior.
4. Update the relevant documentation and submit a pull request.

## Checks

Requirements: .NET 10 SDK, Node.js 22+, npm; Azure CLI for Bicep validation.
From the repository root:

```powershell
npm ci --prefix src\EventWall.Web
dotnet build EventWall.slnx
dotnet build tools\EventWall.DatabaseSetup
dotnet test EventWall.slnx
npm test --prefix src\EventWall.Web
npm run lint --prefix src\EventWall.Web
$env:VITE_SITE_ROLE = 'public'
npm run build --prefix src\EventWall.Web
node tools\demo\verify-build.mjs public src\EventWall.Web\dist
$env:VITE_SITE_ROLE = 'admin'
npm run build --prefix src\EventWall.Web
node tools\demo\verify-build.mjs admin src\EventWall.Web\dist
Remove-Item Env:VITE_SITE_ROLE
node --test tools\demo\questions-client.test.mjs tools\azure\release-workflow.test.mjs
npm ci --prefix docs
npm run build --prefix docs
az bicep build --file infra\main.bicep --stdout > $null
```

CI checks affected projects, runs their tests, checks role-isolated builds, and
builds documentation with internal-link validation. Pull requests never receive
deployment credentials. CI does not provision Azure resources or publish docs.

## Invariants

- Keep Public, Submission, and Admin isolated. Never ship admin assets to Public.
- Collectors use `EventPostIngestor`; new or materially edited content needs moderation.
- Real social collection, analytics, export, and X spending require separate opt-ins.
- Never commit tokens, connection strings, databases, passcodes, key rings, or real attendee data.
- Add incremental EF Core migrations. Do not squash published migrations or reset
  someone else's database. See [database operations](docs/operations/database.md).
- Keep event-independent defaults and exercise custom names/hashtags/URLs in tests.
- Preserve keyboard navigation, reduced-motion support, and both color themes.

The existing internal project/namespace prefix `EventWall` is intentional;
the product name is **Event Social Post Wall**.

## Licenses

By submitting a contribution, you agree to license your contribution under MIT.
Only contribute material you have the right to share. Preserve third-party
copyrights and license notices. After dependency changes, restore dependencies,
run `.\tools\licenses\generate-notices.ps1`, and review the generated notices.
Documentation dependencies are development tools, not bundled application code.
