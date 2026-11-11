# Connect social platforms

Every connector is disabled by default, locally and in Azure. The admin
**Social connections** screen reports readiness and missing settings; it does not
register developer apps or expose stored secrets.

## Before enabling anything

1. Rehearse locally with demo mode first.
2. Obtain authorized API access using the platform's official process.
3. Review its current display, attribution, retention, deletion, analytics, and
   export requirements. Public availability is not blanket permission to reuse content.
4. Configure credentials on **Admin only**, using protected runtime settings or a
   secret manager. For local Aspire, use the AppHost's `Social:*` user-secrets.
5. Set `Enabled=true` and `PolicyApproved=true` only when ready.
6. Send a new test post, verify attribution/images, approve it, then rehearse withdrawal.

Turning a connector off stops future collection; it does not delete already stored
posts. Restart Admin after changing credentials, stream/chat IDs, or event terms.
Keep one active Admin collector instance per event database.

## What each connector actually supports

| Platform | Intake | Authentication | Important limitations |
| --- | --- | --- | --- |
| [Twitch](twitch.md) | EventSub chat containing exact `#question` | User OAuth refresh token | Missed messages during disconnect cannot be replayed; verify all deletion/clear subscriptions |
| [YouTube](youtube.md) | Live chat polling containing exact `#question` | Channel-authorized OAuth refresh token | Starts stopped; quota applies to polls even with no matching questions |
| [X](x.md) | Recent Search, configured hashtag or event name | Bearer token and approved budget | Originals only; up to 100 results/poll; no deletion-event feed |
| [Bluesky](bluesky.md) | Public Jetstream post commits, locally filtered | None | Network-wide stream costs CPU/bandwidth; no replay guarantee beyond available cursor history |
| [Mastodon](mastodon.md) | One instance's configured hashtag timeline | Optional access token | Federation-limited visibility; polling does not detect edits/deletions |

X/Bluesky match `Event__QuestionHashtag` or the literal `Event__Name`.
Mastodon requests the configured hashtag timeline and applies the same local
filter. Twitch/YouTube's `#question` is fixed and case-insensitive; `#questions`
and `#question_extra` do not qualify. Filters reduce stored content, not API charges.

**No connector is a guarantee of complete delivery or platform compliance.** If the
documented deletion behavior is insufficient for your intended use or platform
obligations, keep that source disabled until the gap is addressed.

## Analytics and exports

`Social__<Provider>__AnalyticsApproved=true` and
`Social__<Provider>__ExportContentApproved=true` are independent opt-ins.
Without them, social data is excluded from analytics and its content/identity
fields are omitted from exports. Portal and staff-added posts remain exportable.

## Anonymous questions

The standalone Submission site requires no social account. Open/pause intake in
Admin. Consider [Cloudflare Turnstile](turnstile.md) for a public event, and read
the [abuse and privacy guidance](../operations/security.md).

Provider instructions were checked against linked official documentation in
November 2026. Dashboards, scopes, quotas, pricing, and policies can change; verify
them for your account before enabling collection.
