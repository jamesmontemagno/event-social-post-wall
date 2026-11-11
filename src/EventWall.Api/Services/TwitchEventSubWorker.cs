using System.Net.WebSockets;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using EventWall.Api.Models;
using EventWall.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public sealed class TwitchEventSubWorker(
    IConfiguration configuration,
    IHttpClientFactory clients,
    ProviderTokenService tokens,
    EventPostIngestor ingestor,
    SourceHealthRegistry health,
    ILogger<TwitchEventSubWorker> logger) : BackgroundService
{
    private static readonly Uri DefaultEndpoint = new("wss://eventsub.wss.twitch.tv/ws");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = DefaultEndpoint;
        var sessionTransferred = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!IsConfigured())
            {
                endpoint = DefaultEndpoint;
                sessionTransferred = false;
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(endpoint, stoppingToken);
                health.Report("Twitch", "connecting", "Connected to Twitch EventSub; waiting for the session welcome message.");
                var reconnectEndpoint = await ConsumeSessionAsync(socket, sessionTransferred, stoppingToken);
                if (reconnectEndpoint is not null)
                {
                    endpoint = reconnectEndpoint;
                    sessionTransferred = true;
                }
                else
                {
                    endpoint = DefaultEndpoint;
                    sessionTransferred = false;
                    health.Report("Twitch", "error", "EventSub disconnected. Reconnecting; chat messages missed during the gap cannot be replayed.");
                }
            }
            catch (WebSocketException)
            {
                endpoint = DefaultEndpoint;
                sessionTransferred = false;
                health.Report("Twitch", "error", "EventSub disconnected. Reconnecting; chat messages missed during the gap cannot be replayed.");
                logger.LogWarning("Twitch EventSub websocket disconnected.");
            }
            catch (HttpRequestException exception)
            {
                endpoint = DefaultEndpoint;
                sessionTransferred = false;
                health.Report("Twitch", "connected-partial", "Chat subscription is unavailable; check OAuth scopes and channel permissions.");
                logger.LogWarning("Twitch EventSub subscription failed with HTTP status {StatusCode}.", exception.StatusCode);
            }
            catch (JsonException)
            {
                endpoint = DefaultEndpoint;
                sessionTransferred = false;
                health.Report("Twitch", "error", "EventSub returned an unreadable message; reconnecting.");
                logger.LogWarning("Twitch EventSub returned an unreadable message.");
            }
            catch (DbUpdateException)
            {
                endpoint = DefaultEndpoint;
                sessionTransferred = false;
                health.Report("Twitch", "error", "Twitch messages could not be saved. Check database availability.");
                logger.LogError("Twitch EventSub could not persist an incoming message.");
            }
            catch (InvalidOperationException exception)
            {
                endpoint = DefaultEndpoint;
                sessionTransferred = false;
                health.Report("Twitch", "error", "Twitch authorization could not be refreshed. Check the app settings and reconnect if needed.");
                logger.LogWarning("Twitch authorization could not be refreshed: {Message}", exception.Message);
            }
            catch (TaskCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                endpoint = DefaultEndpoint;
                sessionTransferred = false;
                health.Report("Twitch", "error", "EventSub timed out. Reconnecting; missed chat cannot be replayed.");
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private bool IsConfigured()
    {
        var status = SourceSetupStatus.Build(configuration).Single(item => item.Provider == "Twitch");
        if (!status.Enabled)
        {
            health.Report("Twitch", "disabled", "Collection is disabled.");
            return false;
        }

        if (!status.Configured)
        {
            health.Report("Twitch", "needs-configuration", "Required server-side settings are missing.");
            return false;
        }

        if (!configuration.GetValue<bool>("Social:Twitch:PolicyApproved"))
        {
            health.Report("Twitch", "policy-review-required", "Collection remains paused until platform rules are reviewed.");
            return false;
        }

        return true;
    }

    private async Task<Uri?> ConsumeSessionAsync(
        ClientWebSocket socket,
        bool sessionTransferred,
        CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open)
        {
            using var document = await ReceiveJsonAsync(socket, cancellationToken);
            if (document is null)
            {
                return null;
            }

            var root = document.RootElement;
            var metadata = root.GetProperty("metadata");
            var type = metadata.GetProperty("message_type").GetString();
            if (type == "session_welcome")
            {
                if (!sessionTransferred)
                {
                    var sessionId = root.GetProperty("payload").GetProperty("session").GetProperty("id").GetString();
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        throw new JsonException("The EventSub welcome message did not contain a session ID.");
                    }

                    var subscriptionsReady = await SubscribeAsync(sessionId, cancellationToken);
                    health.Report(
                        "Twitch",
                        subscriptionsReady ? "connected" : "connected-partial",
                        subscriptionsReady
                            ? "Chat, message deletion, and chat clear subscriptions are active."
                            : "Chat is connected, but deletion/clear subscriptions are incomplete. Review permissions before using this source live.",
                        DateTime.UtcNow);
                }
                else
                {
                    health.Report(
                        "Twitch",
                        "connected",
                        "EventSub session reconnected; its existing subscriptions were resumed.",
                        DateTime.UtcNow);
                }

                sessionTransferred = false;
            }
            else if (type == "session_reconnect")
            {
                var reconnectUrl = root.GetProperty("payload").GetProperty("session").GetProperty("reconnect_url").GetString();
                return Uri.TryCreate(reconnectUrl, UriKind.Absolute, out var uri) &&
                    uri.Scheme == "wss"
                    ? uri
                    : null;
            }
            else if (type == "notification")
            {
                await HandleNotificationAsync(root.GetProperty("payload"), cancellationToken);
            }
            else if (type == "revocation")
            {
                health.Report("Twitch", "error", "Twitch revoked an EventSub subscription. Check the token, scopes, and channel permissions.");
                return null;
            }
        }

        return null;
    }

    private async Task<bool> SubscribeAsync(string sessionId, CancellationToken cancellationToken)
    {
        var clientId = configuration["Social:Twitch:ClientId"]!;
        var accessToken = await tokens.GetAccessTokenAsync("Twitch", cancellationToken);
        var userId = configuration["Social:Twitch:UserId"]!;
        var broadcasterId = configuration["Social:Twitch:BroadcasterUserId"]!;
        var types = new[]
        {
            "channel.chat.message",
            "channel.chat.message_delete",
            "channel.chat.clear",
            "channel.chat.clear_user_messages"
        };
        var allSubscribed = true;
        foreach (var type in types)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.twitch.tv/helix/eventsub/subscriptions");
            request.Headers.Add("Client-Id", clientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = System.Net.Http.Json.JsonContent.Create(new
            {
                type,
                version = "1",
                condition = new
                {
                    broadcaster_user_id = broadcasterId,
                    user_id = userId
                },
                transport = new
                {
                    method = "websocket",
                    session_id = sessionId
                }
            });

            using var response = await clients.CreateClient("social").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                allSubscribed = false;
                logger.LogWarning(
                    "Twitch EventSub subscription {EventType} returned HTTP {StatusCode}.",
                    type,
                    (int)response.StatusCode);
            }
        }

        return allSubscribed;
    }

    private async Task HandleNotificationAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("subscription", out var subscription) ||
            !subscription.TryGetProperty("type", out var typeProperty) ||
            !payload.TryGetProperty("event", out var eventData))
        {
            return;
        }

        var type = typeProperty.GetString();
        if (type == "channel.chat.message")
        {
            var messageId = eventData.GetProperty("message_id").GetString();
            var message = eventData.GetProperty("message").GetProperty("text").GetString();
            var author = eventData.GetProperty("chatter_user_name").GetString();
            if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(author))
            {
                return;
            }

            var channelName = eventData.TryGetProperty("broadcaster_user_login", out var broadcaster)
                ? broadcaster.GetString()
                : null;
            var link = string.IsNullOrWhiteSpace(channelName)
                ? null
                : $"https://www.twitch.tv/{Uri.EscapeDataString(channelName)}";
            await ingestor.IngestAsync(
                PostSource.Twitch,
                messageId,
                message,
                author,
                eventData.TryGetProperty("chatter_user_id", out var chatterId) ? chatterId.GetString() : null,
                link,
                ReadDate(eventData, "sent_at"),
                cancellationToken,
                eventData.TryGetProperty("chatter_user_login", out var login) && !string.IsNullOrWhiteSpace(login.GetString())
                    ? $"https://www.twitch.tv/{Uri.EscapeDataString(login.GetString()!)}"
                    : null);
        }
        else if (type == "channel.chat.message_delete" &&
            eventData.TryGetProperty("message_id", out var deletedMessageId))
        {
            await ingestor.RemoveMessageAsync(
                PostSource.Twitch,
                deletedMessageId.GetString() ?? string.Empty,
                "Twitch reported that the chat message was deleted.",
                cancellationToken);
        }
        else if (type == "channel.chat.clear_user_messages" &&
            eventData.TryGetProperty("user_id", out var clearedUser))
        {
            await ingestor.RemoveAuthorAsync(
                PostSource.Twitch,
                clearedUser.GetString() ?? string.Empty,
                cancellationToken);
        }
        else if (type == "channel.chat.clear" &&
            !eventData.TryGetProperty("user_id", out _))
        {
            await ingestor.RemoveAllAsync(
                PostSource.Twitch,
                "Twitch reported that the channel chat was cleared.",
                cancellationToken);
        }
    }

    private static async Task<JsonDocument?> ReceiveJsonAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var content = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            content.Write(buffer, 0, result.Count);
            if (content.Length > 1_000_000)
            {
                throw new JsonException("The EventSub message exceeded the allowed size.");
            }
        } while (!result.EndOfMessage);

        return JsonDocument.Parse(content.ToArray());
    }

    private static DateTime? ReadDate(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTime.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var date)
            ? date
            : null;
    }
}
