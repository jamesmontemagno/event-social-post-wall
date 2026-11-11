# Twitch

The collector uses EventSub **WebSockets** with a user access token obtained from
a refresh token. It reads chat; it does not send chat messages.

## Register an application and get keys

1. Enable two-factor authentication on your Twitch account if required.
2. Open the [Twitch developer console](https://dev.twitch.tv/console/apps) and
   register an application. Follow
   [Twitch's registration guide](https://dev.twitch.tv/docs/authentication/register-app/).
3. Choose an appropriate application category and register the exact redirect
   URL for your OAuth client. For the official Twitch CLI flow below, use
   `http://localhost:3000`.
4. Copy the **Client ID**, generate a **Client Secret**, and keep the secret private.
5. Install the [official Twitch CLI](https://dev.twitch.tv/docs/cli/). Configure
   it for **your** application with `twitch configure`.
6. Authorize the account that will read the channel, using:

   ```text
   twitch token --user-token --scopes "user:read:chat"
   ```

   The command opens a consent browser and prints a user access token and a
   **refresh token**. Run it in a private terminal, not CI or a recording. Confirm
   the signed-in Twitch account is the intended one. Do not request write scopes
   for this read-only collector.
7. Record the reader's numeric user ID and the broadcaster's numeric user ID.
   The [Get Users API](https://dev.twitch.tv/docs/api/reference/#get-users) resolves
   a login to an ID. The token validation endpoint can confirm the authorized
   account/client/scopes. These settings are IDs, not usernames or channel URLs.

For the simplest account setup, authorize the broadcaster and use that account's
ID for both user fields. If using a separate reader/bot account, confirm the
current [chat authentication requirements](https://dev.twitch.tv/docs/chat/authenticating/)
and channel permissions. App-only tokens cannot replace a user token for this
WebSocket transport.

## Configure Admin

| Setting | Value |
| --- | --- |
| `Social__Twitch__ClientId` | Registered application's Client ID |
| `Social__Twitch__ClientSecret` | Client Secret |
| `Social__Twitch__UserId` | Authorized reader's numeric ID |
| `Social__Twitch__BroadcasterUserId` | Channel owner's numeric ID |
| `Social__Twitch__RefreshToken` | Refresh token issued to the same client and account |
| `Social__Twitch__Enabled` | `true` when ready |
| `Social__Twitch__PolicyApproved` | `true` after reviewing platform requirements |

For local Aspire, replace `__` with `:` and use
`dotnet user-secrets set ... --project src/EventWall.AppHost`. For hosted use,
set these on the Admin App Service, not in source files or Vite settings.

## Verify the connection

Restart Admin and inspect **Social connections**. The worker subscribes to:

- `channel.chat.message`
- `channel.chat.message_delete`
- `channel.chat.clear`
- `channel.chat.clear_user_messages`

A partial connection is not a complete safety check. Confirm all four subscriptions
succeed. Send `#question What should newcomers try first?`, approve it in the queue,
then verify the live wall. Delete/clear a test message and verify withdrawal after
the wall's next change check.

## Tokens and failure handling

Access tokens are cached and refreshed. Rotated Twitch refresh tokens are encrypted
with ASP.NET Data Protection and persisted in `ProviderCredentials`; the stored
rotated token takes precedence over the original setting. Back up the Admin key
ring alongside the database.

If reauthorizing after revocation, stop collection, obtain a new authorized token,
and remove only the Twitch `ProviderCredentials` row using a privileged maintenance
session before restarting with the new setting. Otherwise the old stored token
still takes precedence. Do not clear the whole database or token table.

An EventSub disconnect cannot replay missed chat. Check scopes/account/channel IDs
for 401/403 responses, and network WebSocket access for connection failures.

Official references: [token CLI](https://dev.twitch.tv/docs/cli/token-command/#user-access-token),
[OAuth refresh](https://dev.twitch.tv/docs/authentication/refresh-tokens/),
[EventSub types](https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/).
