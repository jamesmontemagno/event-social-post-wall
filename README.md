# Event Social Post Wall

A human-moderated social wall for conferences, meetups, livestreams, and community
events. Collect audience questions and social posts, review them privately, and
choose what appears on stage.

**[Get started](https://jamesmontemagno.github.io/event-social-post-wall/getting-started)** ·
**[Deploy to Azure](https://jamesmontemagno.github.io/event-social-post-wall/deployment/azure)** ·
**[Configure platforms](https://jamesmontemagno.github.io/event-social-post-wall/platforms/)** ·
**[Documentation](https://jamesmontemagno.github.io/event-social-post-wall/)**

## What it does

- Collects anonymous questions and posts from Twitch, YouTube, X, Bluesky, and Mastodon.
- Keeps incoming content off the public wall until a moderator approves it.
- Provides a host-controlled wall with manual refresh, pinning, expanded posts,
  images, light/dark themes, and immersive presentation.
- Supports named moderators, filters, blocked accounts, promotional cards with QR
  codes, reports, and policy-gated exports.
- Includes a realistic local demo without cloud resources or social credentials.

Every connector starts disabled. Some platforms require developer accounts, API
approval, or paid access. Review their terms before enabling collection.

## Architecture

Three independently deployed sites share one event database:

```text
Social APIs ----> Admin: collectors + private moderation ----+
                                                           |
Audience ------> Submission: standalone question site ------+--> Database
                                                                  |
Hosts <-------- Public: approved posts only <----------------------+
```

The wall and admin console use React, TypeScript, and Vite. Anonymous questions
use Blazor Server. All three are backed by ASP.NET Core 10 and EF Core:
SQLite locally, Azure SQL when hosted. Aspire starts the local stack; Bicep
provisions Azure App Service and SQL. Staff routes, credentials, and collectors
exist only in the admin deployment.

See [architecture and trust boundaries](docs/architecture.md).

## Run locally

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
[Node.js](https://nodejs.org/) (22.22.2+ on 22.x, 24.15+ on 24.x, or 26+), Git, and the
[Aspire CLI](https://aspire.dev/get-started/install-cli/) (recommended):

```powershell
irm https://aspire.dev/install.ps1 | iex          # Windows (PowerShell)
```

```bash
curl -sSL https://aspire.dev/install.sh | bash    # macOS / Linux
```

Then, from the repository root:

```powershell
npm ci --prefix src\EventWall.Web
aspire run
```

`aspire run` builds the AppHost, starts every site, and prints the dashboard
link. Stop with Ctrl+C or `aspire stop`; use `aspire logs admin-api -f` to stream
a site's logs. Prefer plain .NET? `dotnet run --project src\EventWall.AppHost`
starts the same stack.

| Site | Address |
| --- | --- |
| Host wall | http://127.0.0.1:5173 |
| Staff console | http://127.0.0.1:5174 |
| Questions | http://127.0.0.1:5082 |

Use the tokenized Aspire dashboard link printed in the terminal. Reveal its
generated **staff-passcode** parameter and sign in to the staff console as
**admin**. Demo mode is on by default; no Docker, Azure account, or provider
credentials are needed.

## Make it yours

Configure your event name, slug, hashtag, and question-site URL. No original
event domain or cloud account is required. Start with the
[configuration reference](docs/configuration.md), then follow the
[Azure deployment guide](docs/deployment/azure.md) or
[self-hosting guide](docs/deployment/self-hosting.md).

Azure releases are manual, deploy to staging first, and require a protected
production approval gate. Azure deployment is disabled until explicitly enabled;
documentation updates are published manually. **The included baseline is single-instance; read
[scaling guidance](docs/operations/scaling.md) before increasing capacity.**

## Documentation and contributing

The searchable [documentation website](https://jamesmontemagno.github.io/event-social-post-wall/)
is maintained in [docs](docs/). Preview it locally:

```powershell
npm ci --prefix docs
npm run dev --prefix docs
```

See [CONTRIBUTING](CONTRIBUTING.md) for builds, tests, and contributions;
[SECURITY](SECURITY.md) for private vulnerability reporting; and
[publishing documentation](docs/deployment/documentation.md) for Pages setup.

## License and credits

[MIT](LICENSE), copyright James Montemagno. Third-party dependencies and the
CC0 question avatars retain their own licenses; see
[notices](src/EventWall.Web/public/THIRD-PARTY-NOTICES.txt).
Built with ASP.NET Core, Blazor, React, TypeScript, and Aspire, with AI-assisted
development using GitHub Copilot. Platform names and artwork do not imply endorsement.
