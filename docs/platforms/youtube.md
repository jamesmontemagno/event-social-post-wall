# YouTube

The collector polls live chat using OAuth and honors the returned polling interval,
with an additional minimum of 60 seconds. It starts **stopped**, even when server
credentials and policy gates are configured.

::: danger YouTube refresh tokens expire after 7 days in Testing mode
If your Google OAuth app has an **External** audience and its publishing status
is **Testing**, Google expires refresh tokens for the YouTube scope after
**seven days**. Access-token refreshes and saving a rotated refresh token do not
remove this limit. Collection will stop until you reauthorize.

Check the app's publishing status before an event. Either reauthorize close
enough to the event or plan for Google's production publishing and verification
requirements in advance. Production tokens can still expire or be revoked for
other reasons. See [Google's refresh token expiration guidance](https://developers.google.com/identity/protocols/oauth2#expiration).
:::

## Create credentials and authorize a channel

1. Create/select a project in the [Google Cloud console](https://console.cloud.google.com/).
2. Enable **YouTube Data API v3** in that project.
3. Configure the OAuth consent screen, audience, required branding/privacy links,
   and test users. Use the channel-owning/authorized Google account.
4. Create an OAuth client of an appropriate type and save its **Client ID** and
   **Client Secret**. Register the exact redirect URI used by your authorization flow.
5. Obtain offline consent for
   `https://www.googleapis.com/auth/youtube.readonly` using
   [Google's authorization-code flow](https://developers.google.com/identity/protocols/oauth2/web-server).
   Request offline access and retain the returned **refresh token**, not only
   the short-lived access token.

For a controlled setup rehearsal, Google's
[OAuth Playground](https://developers.google.com/oauthplayground/) can obtain a
refresh token. Select **Use your own OAuth credentials**, supply your client,
register `https://developers.google.com/oauthplayground` as an allowed redirect,
request the scope above with offline access, authorize the correct channel
account, and exchange the code. Do not use shared playground credentials for a
long-running deployment. Handle tokens privately and remove unnecessary redirect
URIs after setup.

## Find the live chat ID

The setting is **not** a video ID or watch URL. Retrieve either:

- `snippet.liveChatId` from
  [liveBroadcasts.list](https://developers.google.com/youtube/v3/live/docs/liveBroadcasts/list)
  for the authorized broadcast; or
- `liveStreamingDetails.activeLiveChatId` from
  [videos.list](https://developers.google.com/youtube/v3/docs/videos/list)
  for an active livestream.

The broadcast must have chat enabled and the authorized account must be able to
access it. A future, ended, restricted, or chat-disabled stream may not provide
an active live chat ID.

## Configure Admin

```text
Social__YouTube__ClientId
Social__YouTube__ClientSecret
Social__YouTube__RefreshToken
Social__YouTube__LiveChatId
Social__YouTube__Enabled=true
Social__YouTube__PolicyApproved=true
```

In the owner console, open **Social connections** and choose **Start YouTube
collection** when ready. Stop it after the stream. The saved start/stop choice
survives restarts; it cannot bypass missing credentials, policy gates, or quota.
No OAuth or chat requests are made while stopped, although an already in-flight
request may finish.

Send a new exact `#question` message and verify moderation before publication.
Starting collection returns recent messages supplied by YouTube, not guaranteed
history for the period while stopped.

## Token storage and reauthorization

When Google returns a changed refresh token, Admin encrypts it with ASP.NET Data
Protection and saves it in `ProviderCredentials`. The saved token takes precedence
over `Social__YouTube__RefreshToken`; back up the Admin data-protection key ring
alongside the database. If no new refresh token is returned, the existing token
remains in use.

::: warning Reauthorization requires replacing the stored token
Changing the configured token alone does not override a saved rotated token.
Stop YouTube collection and any Admin instances, obtain a new refresh token for
the same client and authorized channel, and update the Admin secret configuration.
If a YouTube row exists in `ProviderCredentials`, remove **only** that row using
a privileged database maintenance session before restarting Admin. Do not clear
other providers' credentials or reset the database. Restart Admin, start YouTube
collection, and verify a new `#question` reaches the review queue. Never expose
tokens in logs or screenshots.
:::

## Quota and errors

[`liveChatMessages.list`](https://developers.google.com/youtube/v3/live/docs/liveChatMessages/list)
currently costs 5 units. At the 60-second minimum, one continuously running
collector uses at most about **300 units/hour or 7,200 units/day** for polling.
Longer provider intervals reduce that. Other clients/instances share your project's
quota. Matching fewer `#question` messages does not reduce polling charges.

The implementation uses polling, not the newer streaming API suggested in
Google's docs. Changing transport requires separate implementation and validation.

| Status | Check |
| --- | --- |
| API disabled | Enable YouTube Data API v3 on the OAuth client's project; allow propagation |
| Missing permissions | Reauthorize with the correct YouTube scope; existing tokens do not gain scopes from a config change |
| Forbidden | Account access to the selected broadcast/chat; not necessarily a bad client secret |
| Chat ended/disabled/not found | Active broadcast and exact live chat ID |
| `invalid_grant` | Expired/revoked refresh token, wrong OAuth client, or Testing-mode lifetime |
| Budget exhausted | Project quota; recognized daily-quota failures pause until midnight Pacific |

The quota pause is in memory; restarting can cause another failed request before
the quota is recognized again. Changing credentials does not restore quota.
Restart Admin after changing credentials or live chat IDs to clear access-token
and pagination caches. Rehearse provider deletion events as well as manual withdrawal.
