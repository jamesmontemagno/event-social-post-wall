# Deploy to Azure

This is the supported hosted baseline: a Linux App Service plan, three apps per
environment, and Azure SQL. You own the subscription, domains, credentials,
provider approvals, ongoing costs, and operational procedures.

::: warning No automatic provisioning
Cloning or pushing this repository does not deploy anything. Infrastructure,
database initialization, and GitHub deployment identity setup are separate
operator steps. Never point a first-run script at an existing event database.
:::

## 1. Prerequisites and design

- An Azure subscription with permission to create resources in a dedicated resource group.
- Azure CLI with Bicep support, .NET 10 SDK, a [supported Node.js version](../getting-started.md#requirements), and PowerShell 7.
- A GitHub account/repository able to use Actions and environment required reviewers
  for production promotion. Some private-repository plans do not support that gate.
- A globally unique, lowercase app prefix of 3-20 characters (letters, digits,
  internal hyphens). Check App Service/SQL naming and regional availability.

The default template provisions:

| Resource | Name |
| --- | --- |
| One shared Linux B1 plan, one instance | `<prefix>-plan` |
| Production wall / admin / questions | `<prefix>-app`, `<prefix>-admin`, `<prefix>-submit` |
| Staging wall / admin / questions | `<prefix>-stage`, `<prefix>-admin-stage`, `<prefix>-submit-stage` |
| SQL server | `<prefix>-sql` |
| Separate Basic databases | `EventWallProd`, `EventWallStaging` |

Both environments share the plan and SQL server, not a database or credentials.
This keeps costs down but does not isolate compute contention. B1 and Basic SQL
are starting points, not a capacity guarantee. Review [scale/cost guidance](../operations/scaling.md).

## 2. Prepare protected parameters

`infra/main.bicepparam` reads values from your process environment. It contains no
secrets. Set:

```powershell
$subscription = 'your-subscription-id'
$resourceGroup = 'rg-my-event-wall'
$location = 'eastus2'
$env:EVENTWALL_APP_NAME = 'your-unique-event-wall'
$env:SQL_ADMIN_LOGIN = 'eventwall_deployment'
$env:EVENT_SLUG = 'my-event'
$env:EVENT_NAME = 'My Event'
$env:EVENT_HASHTAG = '#myevent'
```

Provide the following values from your password manager or secret store. Use
different random values for **every** entry; staff passcodes need at least 12
characters. SQL passwords must satisfy Azure SQL password requirements.

```text
SQL_ADMIN_PASSWORD
PRODUCTION_STAFF_PASSCODE
PRODUCTION_PUBLIC_SQL_PASSWORD
PRODUCTION_SUBMISSION_SQL_PASSWORD
PRODUCTION_ADMIN_SQL_PASSWORD
STAGING_STAFF_PASSCODE
STAGING_PUBLIC_SQL_PASSWORD
STAGING_SUBMISSION_SQL_PASSWORD
STAGING_ADMIN_SQL_PASSWORD
```

For an interactive PowerShell session, read a value without putting the literal
secret into shell history:

```powershell
$secret = Read-Host 'SQL administrator password' -AsSecureString
$env:SQL_ADMIN_PASSWORD = [System.Net.NetworkCredential]::new('', $secret).Password
```

Repeat for the other named variables or inject them from your secret manager.
Process environment values are still accessible to that process and privileged
local users. Do not print them, commit them, or leave them in shared terminals.
Save them in your secret manager for database initialization and future redeployments.

For custom parameters, copy the parameter file to an ignored
`infra/main.local.bicepparam`. Keep secrets as `readEnvironmentVariable` references.

## 3. Provision resources

```powershell
az login
az group create --subscription $subscription --name $resourceGroup --location $location
az deployment group what-if --subscription $subscription --resource-group $resourceGroup --parameters infra\main.bicepparam
az deployment group create --subscription $subscription --resource-group $resourceGroup --parameters infra\main.bicepparam
```

Review the what-if and estimated charges before creating resources. Templates set
HTTPS-only, disable FTP, enable persistent App Service storage, and enable WebSockets,
Always On, and affinity for the Blazor question site. Staff login remains required
for every privileged operation.

The SQL administrator credential is used only to provision the SQL server. Apps
receive separate contained-user connection strings and
`Database__ApplyMigrations=false`. They will not be ready until those users and
the schema are initialized below.

## 4. Configure SQL networking

The template's firewall initially denies every client. It never uses the broad
Azure-services rule `0.0.0.0`.

1. Get your deployment machine's current public IPv4 address from your network administrator.
2. Read **possible outbound IP addresses** for each App Service:

   ```powershell
   az webapp show --subscription $subscription --resource-group $resourceGroup --name "$($env:EVENTWALL_APP_NAME)-admin-stage" --query possibleOutboundIpAddresses --output tsv
   ```

3. Collect the unique addresses for **all six apps**, plus the temporary deployment
   machine address. Set `EVENTWALL_SQL_IPS` to a JSON array, for example
   `["203.0.113.10","203.0.113.11"]` (documentation addresses; replace them).
4. Rerun the same deployment with that environment value.
5. After initializing both databases, remove your deployment machine address unless
   needed for continuing approved maintenance.

App outbound addresses can change after infrastructure changes; recheck after
changing tiers or moving apps. For a stronger boundary, design VNet integration
and SQL private endpoints separately. They are not provisioned by this baseline.

## 5. Initialize schema and restricted runtime accounts

Build the setup tool, then run it once per database using the SQL deployment
identity. Never use an app's runtime account to apply schema changes.

```powershell
dotnet build tools\EventWall.DatabaseSetup --configuration Release
```

For **staging**, set the process-only values:

| Variable | Value |
| --- | --- |
| `EVENTWALL_DEPLOYMENT_SQL` | Encrypted SQL connection to `<prefix>-sql.database.windows.net`, database `EventWallStaging`, deployment login/password |
| `EVENTWALL_PUBLIC_SQL_PASSWORD` | Same value as `STAGING_PUBLIC_SQL_PASSWORD` |
| `EVENTWALL_SUBMISSION_SQL_PASSWORD` | Same value as `STAGING_SUBMISSION_SQL_PASSWORD` |
| `EVENTWALL_ADMIN_SQL_PASSWORD` | Same value as `STAGING_ADMIN_SQL_PASSWORD` |
| `Event__Slug`, `Event__Name` | Same branding used in Bicep (`EVENT_SLUG` and `EVENT_NAME` are Bicep input names, not app settings) |

Example connection structure (supply a real secret securely; do not commit it):

```text
Server=tcp:<prefix>-sql.database.windows.net,1433;Initial Catalog=EventWallStaging;User ID=<deployment-login>;Password=<secret>;Encrypt=True;TrustServerCertificate=False;
```

Use a SQL connection-string builder or properly quote passwords containing
semicolons/quotes. Assign the already-protected runtime values without printing them:

```powershell
$env:EVENTWALL_PUBLIC_SQL_PASSWORD = $env:STAGING_PUBLIC_SQL_PASSWORD
$env:EVENTWALL_SUBMISSION_SQL_PASSWORD = $env:STAGING_SUBMISSION_SQL_PASSWORD
$env:EVENTWALL_ADMIN_SQL_PASSWORD = $env:STAGING_ADMIN_SQL_PASSWORD
$env:Event__Slug = $env:EVENT_SLUG
$env:Event__Name = $env:EVENT_NAME
dotnet run --project tools\EventWall.DatabaseSetup --configuration Release
```

Repeat against **EventWallProd** with the corresponding **PRODUCTION** runtime
passwords. The tool applies migrations, seeds the event, creates/updates three
contained users, and verifies their permissions:

- `eventwall_public`: read-only wall data.
- `eventwall_submission`: read event/posts and append questions/audit entries.
- `eventwall_admin`: application data management, but no schema administration.

Fresh intake starts closed. Do not use any reset flags for initial provisioning or
normal upgrades. Clear deployment secrets from your process when finished.

## 6. Deploy matching application packages

Configure [GitHub OIDC and environments](github-actions.md), enable the manual
workflow, and run a staging-only release. The release builds three distinct ZIPs,
deploys Admin first, then Public and Submission, and checks anonymous readiness.

Verify staging using the [event-day checklist](../operations/event-day.md).
Before promotion, confirm production migrations/runtime grants are applied. Run
the release with production promotion enabled; it deploys and verifies staging
first, then waits for the protected production environment approval.

Do not grant runtime accounts schema permissions just to bypass a failed startup.
An interrupted deployment can leave roles on different revisions: redeploy the
same three artifacts after fixing the failure.

## 7. Domains, protection, and providers

- Assign separate hostnames, such as `wall.example.com`, `questions.example.com`,
  and `staff.example.com`; bind valid HTTPS certificates in App Service.
- Set `Public__SubmissionSiteUrl` on Public and Admin, and `Public__WallSiteUrl`
  on Admin. Or use Bicep's `productionWallUrl`, `productionSubmissionUrl`,
  `stagingWallUrl`, and `stagingSubmissionUrl` parameters.
- Protect staging using your organization's access gateway/App Service
  authentication or IP restrictions before adding real data. The baseline does
  **not** make staging private. `robots.txt` and Staging's `X-Robots-Tag` only
  discourage indexing.
- If staging authentication blocks the release's anonymous probes, adapt those
  probes to your approved authentication/access mechanism before enabling deployment.
- Configure [Turnstile](../platforms/turnstile.md) on Submission if needed.
- Configure [providers](../platforms/index.md) only on Admin; review policy and
  spending before enabling. Open intake in the staff console when ready.

::: warning Reapplying infrastructure
App settings and connection strings are declared by Bicep. Reapplying the template
can replace out-of-band portal settings, including provider credentials. Keep a
protected source of truth for your additional settings and reapply them after an
infrastructure change. Never export secrets into a tracked parameter file.
:::

## Optional template settings

- `includeStaging=true` is the recommended default.
- `stagingOnly=true` omits production apps; `includeProductionDatabase=false` also
  defers the production database. Pass `{}` for unused production secrets.
- `demoEnabled=true` simulates sources in staging only.
- `appServiceSku` changes the plan size but keeps capacity at one.
- `turnstileSiteKey` and secure `turnstileSecretKey` configure the challenge on
  both question apps. Use a widget allowing both hosts or configure them individually.
- `corsOrigins` is only for a separately hosted public wall; leave empty for the
  default same-origin deployment.
