# Self-hosting

Azure is the documented production baseline. You can also host the three ASP.NET
Core processes behind a trusted HTTPS reverse proxy with SQL Server and persistent
storage. This repository does not ship a supported Docker Compose stack.

## Build separate packages

From a clean checkout, build each role into a **different output directory**.
Do not copy admin assets into a directory later used for Public.

```powershell
npm ci --prefix src\EventWall.Web

$env:VITE_SITE_ROLE = 'public'
npm run build --prefix src\EventWall.Web
dotnet publish src\EventWall.Api -c Release -p:EventWallSiteRole=public -o publish\public
Copy-Item src\EventWall.Web\dist\* publish\public\wwwroot -Recurse -Force
node tools\demo\verify-build.mjs public publish\public\wwwroot

$env:VITE_SITE_ROLE = 'admin'
npm run build --prefix src\EventWall.Web
dotnet publish src\EventWall.Api -c Release -p:EventWallSiteRole=admin -o publish\admin
Copy-Item src\EventWall.Web\dist\* publish\admin\wwwroot -Recurse -Force
node tools\demo\verify-build.mjs admin publish\admin\wwwroot

Remove-Item Env:VITE_SITE_ROLE
dotnet publish src\EventWall.Api -c Release -p:EventWallSiteRole=submission -o publish\submission
node tools\demo\verify-build.mjs submission publish\submission\wwwroot
```

Use fresh, empty publish directories for each release. The example assumes no
previous role's files were staged into the API's source `wwwroot`; a clean checkout
avoids stale assets. Set `VITE_API_BASE_URL` to empty for same-origin builds.
The host needs the matching ASP.NET Core 10 runtime, or a separately tested
self-contained publish for its OS/runtime identifier.

## Runtime requirements

- Three distinct HTTPS origins; server roles match their build roles.
- SQL Server database `EventWallProd` for Production, or `EventWallStaging` for
  Staging. SQLite is for local Development; do not bypass the hosted database guard.
- Initialize schema and the three runtime users with
  `tools/EventWall.DatabaseSetup` and a deployment-only identity.
- `Database__ApplyMigrations=false`; role-specific runtime connection strings.
- Admin-only staff passcode/provider settings.
- Separate persistent, access-controlled Data Protection directories for Admin
  and Submission, outside the application release directories.
- WebSocket proxy support, connection timeouts suitable for Blazor circuits, and
  a supervised process/service for each role.

Run `dotnet EventWall.Api.dll` from each published directory with that role's
environment variables. Bind backend listeners to a private interface/loopback.

## Reverse proxy and abuse controls

TLS and trusted client-IP handling must be correct. The application has a specific
Azure App Service trust path; it does not blindly trust arbitrary
`X-Forwarded-For` headers. For a different reverse proxy, configure and test a
known-proxy forwarding policy before relying on IP-based rate limits or HTTPS
redirects. Never enable broad forwarded-header trust from arbitrary internet clients.

Do not set Azure platform variables to fake this behavior. If all requests are
seen as the proxy's IP, attendees may share one submission limit; if spoofed client
headers are trusted, an attacker can evade limits. This integration needs a
host-specific test, not just a successful `/api/health`.

Use one Admin and one Submission instance until
[distributed-state requirements](../operations/scaling.md) are addressed.
