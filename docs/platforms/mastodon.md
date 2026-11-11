# Mastodon

The connector polls one Mastodon server's public timeline for
`Event__QuestionHashtag`. It is not a global search of the entire Fediverse.

## Choose an instance

Use an instance whose policies permit your event's collection/display. Its base
URL must be HTTPS, use the default port, and have no path, query, credentials, or
fragment, for example `https://mastodon.social`.

Public timelines may allow anonymous requests. If the server requires authorization:

1. Sign in to an account on that instance.
2. Open **Preferences > Development > New application** (labels vary by server).
3. Create an application with the minimum **`read:statuses`** scope needed for
   the hashtag timeline.
4. Store the generated access token privately. Verify the server's own API/access
   rules; do not broaden scopes or scrape pages to bypass restrictions.

## Configure Admin

```text
Social__Mastodon__InstanceUrl=https://your-instance.example
Social__Mastodon__Enabled=true
Social__Mastodon__PolicyApproved=true
```

Optional:

| Setting | Purpose |
| --- | --- |
| `Social__Mastodon__AccessToken` | Read token if required by the chosen server |
| `Social__Mastodon__MediaHosts__0`, `__1`, ... | Exact trusted media CDN hostnames, no scheme/path |
| `Social__Mastodon__PollIntervalSeconds` | Default 180; clamped 60-3600 |

Avatars/media must come from the configured server or the explicit media host
allowlist. For example, an instance may use a separate `files.*` CDN; inspect its
official configuration before allowlisting it. Do not allow arbitrary hosts.

## What is collected

- Up to five 40-post pages per poll, with persisted `min_id` checkpoints.
- The first poll starts with the latest page, which may contain older posts,
  rather than walking backward through all history.
- Only posts federated to the chosen server are visible.
- Boosts, replies, quotes, non-public posts, bot authors, `#nobot` profiles, content
  warnings, and sensitive media are skipped.
- Search-indexing preferences alone do not opt a public hashtag post out of this
  moderated display. Review whether that policy is suitable for your event.

Polling does **not** detect source edits/deletions. Staff must manually withdraw
posts when needed. Keep this connector disabled if that limitation conflicts
with your obligations.

## Verify

Restart Admin, publish a new original public hashtagged post visible to the chosen
server, wait for a poll, then approve it. A new post is preferable to reusing an
old skipped item after a checkpoint advances.

For 401, confirm whether the server requires a token and that it has
`read:statuses`. For 429, reduce request frequency and respect the instance's
policy. Missing images usually indicate an unlisted media host or a skipped
sensitive/content-warning post.

Official reference: [hashtag timeline API](https://docs.joinmastodon.org/methods/timelines/#tag).
