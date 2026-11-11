# Architecture

The API build copies shared question-site styles, artwork, and notices from the
web project into ignored generated files under its `wwwroot` directory. Edit the
original web assets and rebuild; do not edit those generated copies.

## Deployment boundaries

| Role | Audience | Responsibilities |
| --- | --- | --- |
| **Public** | Hosts and viewers | Approved wall posts, safe event metadata, external question link |
| **Submission** | Attendees | Blazor Server question page, protected bootstrap and submission API |
| **Admin** | Owner and moderators | Staff authentication, moderation, filters, reports, team, promos, collectors |

All three run the same ASP.NET Core project with `Deployment__Role`, but only
register the routes and services for that role. A URL query string cannot change
roles. Public and Submission have no staff endpoints, collector services, or staff
JavaScript. Submission and Admin require same-origin APIs.

Public/Admin use separate React builds selected by `VITE_SITE_ROLE`. The server
checks the role marker in the deployed assets at startup. Submission is compiled
with Razor components and rejects a React entry point. Unknown API routes return
404 rather than a SPA shell.

```text
Twitch / YouTube / X / Bluesky / Mastodon
                  |
       Admin-only connectors
                  |
          EventPostIngestor --------+
                                    |
Attendee --> Submission guards --> SQL / SQLite --> Staff review
                                    |                   |
                                    +<-- Approve -------+
                                    |
                          Public live-post endpoints
                                    |
                              Host wall
```

## Data and moderation

One database represents one event. Use separate databases for separate events
and for staging/production. EF Core migrations target SQL Server and support local
SQLite. Runtime SQL permissions are role-specific; schema changes use a privileged
deployment identity, not an app account.

Connector ingestion centralizes term filtering, deduplication, safe image/profile
metadata, author blocking, and persistence. Incoming content is Pending or
automatically Rejected. Only Live posts are publicly returned. Material edits
return a decided post to moderation. Provider deletion events withdraw content
where supported; X and Mastodon polling have important
[deletion limitations](platforms/index.md).

Moderation writes use versions to detect concurrent decisions. Publication revisions
support change checks without moving the host's current view.

## Browser and server state

- The wall loads metadata once and posts initially or on explicit refresh/sort.
  It checks for changes every 60 seconds and when the tab becomes visible.
- Withdrawals disappear on the next successful check, not instantaneously.
  Offline clients keep their last snapshot with a warning.
- Pins, theme, style, and promo visibility are browser-local, not shared host state.
- Submission bootstrap uses configuration and protected tokens without SQL.
  Idle question pages do not poll event/status endpoints or renew tokens.
- Blazor Server still maintains a SignalR circuit per connected attendee.
- Admin presence, some rate limits, and profile caches are process-local.

This is a **single-instance baseline**, not a distributed event-processing system.
Read [scaling](operations/scaling.md) before adding instances.

## Repository map

| Path | Purpose |
| --- | --- |
| `src/EventWall.Api` | API, Razor question site, collectors, security, EF data |
| `src/EventWall.Web` | Role-specific React wall/admin builds |
| `src/EventWall.AppHost` | Local-only Aspire orchestration |
| `tests/EventWall.Api.Tests` | Backend tests |
| `tools/EventWall.DatabaseSetup` | Hosted migration/seeding/runtime SQL grants |
| `tools/demo` | Local demo and browser verification scripts |
| `infra/main.bicep` | Azure resources and safe runtime defaults |
| `docs` | This VitePress website |

Internal namespaces retain the `EventWall` prefix for clarity and continuity.
