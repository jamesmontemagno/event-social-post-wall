# Local setup

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), not just the runtime.
- [Node.js](https://nodejs.org/) 22.22.2+ on 22.x, 24.15+ on 24.x, or 26+, and npm.
- Git. PowerShell 7 is needed for the optional scripts; Aspire itself also runs on macOS and Linux.
- The [Aspire CLI](https://aspire.dev/get-started/install-cli/) (recommended).

No Azure account, Docker, external database, or social keys are required for demo mode.

## Install the Aspire CLI

The Aspire CLI is the easiest way to run, inspect, and stop the local stack:

::: code-group

```powershell [Windows]
irm https://aspire.dev/install.ps1 | iex
```

```bash [macOS / Linux]
curl -sSL https://aspire.dev/install.sh | bash
```

:::

Homebrew, npm, NuGet, and WinGet packages, plus the Aspire VS Code extension,
are listed in the [install guide](https://aspire.dev/get-started/install-cli/).
Open a new terminal, then confirm with `aspire --version`.

## Run the stack

```powershell
git clone https://github.com/jamesmontemagno/event-social-post-wall.git
cd event-social-post-wall
npm ci --prefix src\EventWall.Web
aspire run
```

`aspire run` finds the repository's single AppHost, builds it, starts all
resources, and prints the tokenized dashboard link. Without the CLI, the
equivalent is `dotnet run --project src\EventWall.AppHost`.

While the repository is private, cloning requires an invited GitHub account.
On macOS/Linux, use `/` in shell paths, for example
`npm ci --prefix src/EventWall.Web`.

## Open the three sites

| Resource | URL |
| --- | --- |
| Wall | `http://127.0.0.1:5173` |
| Staff console | `http://127.0.0.1:5174` |
| Questions | `http://127.0.0.1:5082` |
| Public API | `http://127.0.0.1:5080` |
| Admin API | `http://127.0.0.1:5081` |
| Aspire dashboard | Tokenized `http://localhost:15888` URL printed at startup |

Aspire starts Admin first so local migrations finish before the other APIs start.
The question site is Blazor Server: there is no third Vite server.

In the dashboard, reveal **staff-passcode**, then sign in as **admin**. Aspire
generates and persists that secret in its own local user-secrets store. To choose
your own, use at least 12 characters:

```powershell
dotnet user-secrets set "Parameters:staff-passcode" "replace-with-a-long-local-passcode" --project src\EventWall.AppHost
```

The example is not a production credential. Never reuse it in a hosted deployment.
Local user-secrets are outside the repository but are **not an encrypted vault**.

Submit a question, approve it in the console, then refresh the wall. Newly seeded
demo events start with intake open; an existing staff decision to pause intake is
preserved. Demo posts are fictional, and no provider API calls are made.

## Your event

Pass common branding to the AppHost after `--`:

```powershell
aspire run -- --Event:Slug=my-meetup --Event:Name="My Meetup" --Event:QuestionHashtag="#mymeetup"
```

The same arguments work with `dotnet run --project src\EventWall.AppHost -- ...`.

Use a new database for a different event slug. The app deliberately refuses to
silently add another event to the same database. See [configuration](configuration.md).

To leave demo mode:

```powershell
aspire run -- --Demo:Enabled=false
```

This switches from `src/EventWall.Api/App_Data/eventwall-demo.db` to
`eventwall.db`; it does not delete or merge anything. All real connectors remain
disabled until configured. New non-demo events start with intake closed.

Set local provider settings in the AppHost's user-secrets store using the
`Social:<Provider>:<Setting>` names in the provider guides. Aspire forwards that
section only to Admin. Restart after changing settings.

## Stop, reset, and troubleshoot

Ctrl+C stops the stack. Other handy Aspire CLI commands, run from the repository root:

| Command | Purpose |
| --- | --- |
| `aspire stop` | Stop a running AppHost from another terminal |
| `aspire ps` | List running AppHosts |
| `aspire logs admin-api -f` | Stream one resource's logs (`public-api`, `submission-api`, `public-web`, `admin-web`) |
| `aspire doctor` | Diagnose local Aspire setup problems |

This AppHost is local-only: it refuses `aspire publish` and `aspire deploy`. Use
the [Azure deployment guide](deployment/azure.md) for hosted releases.

Do not run Aspire and the standalone demo launcher at the
same time: they use the same local databases and overlapping ports. The standalone
demo and its guarded reset are described in [demo and branding](customization.md).

Local listeners bind to loopback. Do not expose Vite, the dashboard, or these
Development servers directly to the internet. Use the
[deployment guide](deployment/azure.md) for hosted packages.
