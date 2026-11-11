# Bluesky

Bluesky uses the public **Jetstream** live post stream. No Bluesky account, app
password, API key, or OAuth client is required for this connector.

## Enable

On Admin:

```text
Social__Bluesky__Enabled=true
Social__Bluesky__PolicyApproved=true
```

Set `Event__Name` and `Event__QuestionHashtag` consistently across roles, then
restart Admin. Confirm demo mode is off if testing real collection.

The connector opens
`wss://jetstream.us-east.bsky.network/xrpc/network.bsky.jetstream.subscribeEvents`
with post-commit filtering and filters event terms locally. Outbound HTTPS and
WebSockets must be allowed. Matching authors may also trigger a cached public
profile lookup; local platform icons remain the fallback.

## Rehearse

1. Publish a new public post containing your event hashtag.
2. Verify it appears in the queue, not immediately on the wall.
3. Approve it and refresh the wall.
4. Edit the source content and confirm it returns to moderation.
5. Delete it and confirm withdrawal reaches the wall on its next successful check.

The connector persists a sequence cursor and resumes after reconnects. Delivery
is at-least-once, with AT URI deduplication. An event-term-removing edit withdraws
the previously collected post rather than leaving stale content live.

## Resource and policy considerations

The public stream covers the network, not just your hashtag. Local filtering can
consume meaningful bandwidth/CPU during busy periods. Load-test with the actual
stream in staging before choosing an Azure plan.

A saved cursor is not a promise of unlimited historical replay or complete
delivery after long downtime. The app does not implement a separate archive
replay workflow. Keep one active collector per event; checkpoints must never
rewind when a stale worker resumes.

Review current Bluesky/AT Protocol terms and public-post handling policies before
enabling display, retention, analytics, or export.

Official reference: [Jetstream](https://bsky.network/docs/jetstream/).
