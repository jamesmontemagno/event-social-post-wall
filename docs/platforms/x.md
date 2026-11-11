# X

X collection uses the official Recent Search API and batch user lookup API.
It requires appropriate developer access and an explicit spending approval.

## Get authorized API access

1. Register/sign in at the [X developer console](https://console.x.com/).
2. Create the required developer project/application for your use case.
3. Confirm your account has access to Recent Search and user lookup endpoints.
   Complete any required developer/policy approval.
4. Generate the application's **Bearer Token** and store it privately.
5. Review [current pricing](https://docs.x.com/x-api/getting-started/pricing),
   fund/limit the account as appropriate, and set provider-side spending controls.

The app does not purchase credits, create developer access, or authorize spending
for you. Never enable scraping as a substitute for API access.

## Configure Admin

| Setting | Value |
| --- | --- |
| `Social__X__BearerToken` | Authorized application's bearer token |
| `Social__X__Enabled` | `true` |
| `Social__X__PolicyApproved` | `true` after policy review |
| `Social__X__SpendApproved` | `true` only after budget approval |
| `Social__X__MaximumDailySpendUsd` | Positive approved USD ceiling |
| `Social__X__PollIntervalSeconds` | Optional; default 300, clamped 60-3600 |

Zero/no budget keeps reads off. Set credentials only on Admin and restart.

The query groups the configured hashtag and literal event name before excluding
retweets, replies, and quotes, for example:

```text
(#myevent OR "My Event") -is:retweet -is:reply -is:quote
```

Returned reference metadata is checked again before ingestion. New original
posts still need moderation. Use a distinctive event name/hashtag to avoid
unrelated matches.

## Understand the spend guard

The local accounting model currently uses **$0.005/post** and **$0.010/user**
reads. These matched published pricing when checked in November 2026, but are
**not a billing guarantee**. If X changes prices, review/update `XReadBudget`
before relying on its USD estimate.

- Search reserves a 100-post request against the shared persisted UTC-day budget.
  At those rates it needs at least $0.50 of headroom to attempt a search.
- Profile batches contain at most 100 IDs and shrink to affordable headroom.
- Both post and profile reads share the same ceiling.
- Every returned resource counts, even if filtered locally.
- Definitive search 4xx responses (except timeouts) refund the search reservation.
  Uncertain/server failures and failed profile lookups conservatively retain it.
- Cached profiles last 24 hours (unavailable profiles five minutes). Restarting
  may cause another lookup but does not reset daily spending.
- Provider-side billing deduplication is not assumed by this local estimate.

Set provider-side limits as well. Shared accounts, other applications, changed
pricing, and reservation conservatism can make actual billing differ.

## Delivery limits and rehearsal

The collector persists `since_id` and requests posts newer than the latest seen;
it stops using a checkpoint once it is older than six days. Each poll fetches
at most 100 posts, without a complete backlog-draining loop. A busy event can
lose posts between polls; shorter intervals trade quota/cost for lower gaps.

Recent Search does **not** supply source deletion events. Staff must withdraw
removed/unwanted content, and you must determine whether this satisfies the
platform's obligations for your use. A partial source status is expected, not a
claim of deletion completeness.

Test a new standalone post, profile attribution, an excluded reply/quote, approval,
and manual withdrawal before enabling collection at an event.

Official references: [Recent Search](https://docs.x.com/x-api/posts/search/introduction),
[pricing](https://docs.x.com/x-api/getting-started/pricing).
