# Azure scaling and costs

## Start with measurements, not audience guesses

The Bicep baseline uses one B1 Linux App Service plan and Basic Azure SQL databases.
All sites in that plan share its CPU/memory, including staging. This is a low-cost
starting configuration, **not a promise to handle a specific attendee count**.

Use the [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/)
for your region, plan, database tier, backups, logs, bandwidth, and any provider
charges. Avoid treating a historical price estimate as a quote.

| Component | Main load/cost driver | Watch |
| --- | --- | --- |
| Public wall | Snapshot reads, change checks, remote images | Response latency, SQL reads, HTTP errors |
| Submission | Connected Blazor circuits plus submitted writes | Memory, connections, reconnects, SQL writes |
| Admin | Staff operations and enabled collectors | CPU, queue lag, provider health, database contention |
| SQL | Retained posts/audits, reports, indexes, backups | DTU/vCore usage, storage, throttling, deadlocks |
| X | Paid post/profile reads | Persisted local reservation budget and provider billing |
| YouTube | Live chat polling | API quota and start/stop status |
| Bluesky | Network-wide live stream | Admin CPU, bandwidth, reconnects |

Azure SQL Basic has a small storage/throughput ceiling; confirm current service
limits before choosing it. Reports and ingestion compete with wall/submission
queries. Storing only image URLs avoids hosting media binaries in SQL, but retained
text, audit history, and indexes still grow.

## Known application limits

- Global app limiter: **5,000 HTTP requests per one-minute fixed window per
  instance**, about 83.3 requests/second averaged across a minute.
- Submission IP limiter: **one attempt per three-minute fixed window**.
- Submission browser limiter: **eight attempts per sliding hour**.
- Submission body: **16 KiB**; question text: **2,000 characters**; optional name: **80**.
- Wall change checks: **60 seconds**, plus tab-visibility checks.
- YouTube polling: at least **60 seconds**, or longer if requested by the provider.
- X: at most **100 posts per poll**; Mastodon: up to **five 40-post pages**.

The global request budget is an application-defined ceiling, not an Azure quota,
visitor count, or guaranteed throughput. It includes shared traffic and is bursty.
Blazor circuit memory is not represented by requests/second. Many attendees behind
one conference Wi-Fi public IP share the IP submission budget; load-test this
specific behavior rather than bypassing it silently.

## Scale up first

1. Rehearse with synthetic traffic and approved provider streams.
2. Measure steady state, bursts, reconnect storms, reports, and a database outage.
3. Increase the App Service plan's **size**, retaining **one instance**.
   The template's `appServiceSku` supports B/S/P choices.
4. Increase SQL service tier/objective when measurements show database pressure.
   Update the Bicep database SKU declarations too; otherwise a later deployment
   can undo a portal-only change.
5. Consider separate staging/production plans when rehearsals affect live capacity.

Keep the question site's WebSockets, Always On, and affinity enabled. Monitor
reconnects after restarts; tab-local drafts aid recovery but are not server durability.

## Do not enable autoscale without engineering work

Multiple instances require more than a larger instance count:

| Area | Work needed before scale-out |
| --- | --- |
| Collectors | Elect/lease a single active collector or split collection into an explicitly coordinated worker |
| Rate limits | Shared enforcement; current in-process budgets reset and multiply across instances |
| Data Protection | Shared durable key storage per role, correct encryption/access controls |
| Blazor Server | Tested routing/affinity, circuit lifecycle and reconnect strategy; SignalR scale-out alone does not move circuit state |
| Staff presence | Shared storage if cross-instance presence is required |
| Profile caches | Account for duplicated lookups/quota after restarts or across instances |
| Checkpoints | Preserve monotonic updates, deduplication, and safe ownership for every provider |

Do not assume deduplication alone prevents duplicate provider spending or that a
shared SQL database makes in-memory state distributed. Keep one Admin instance
and one Submission instance until those requirements are implemented and tested.

## Monitoring and load tests

App Service/SQL platform metrics are available, but this template does not
provision a complete Application Insights/Log Analytics stack or load-test service.
Configure alerting appropriate to your event:

- sustained CPU/memory pressure and restarts;
- 429/5xx rates and p95 latency;
- SQL utilization, storage, blocking/deadlocks, failed logins;
- connector errors, ingestion gaps, stale checkpoints, X budget, YouTube quota;
- Blazor connection/reconnection failures.

`/api/health` proves the process is responding, not database/provider readiness.
Also perform a safe wall read and a moderated synthetic submission in staging.
Never load-test third-party APIs without permission or bypass real-event moderation.
