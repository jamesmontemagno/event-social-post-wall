using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EventWall.Api.Models;

namespace EventWall.Api.Services;

public sealed record BlueskyStreamEvent(
    long Sequence,
    string Did,
    string Collection,
    string RKey,
    string Operation,
    string? Text,
    DateTime? CreatedAtUtc,
    IReadOnlyList<PostImage> Images);

public interface IBlueskyJetstreamClient
{
    Task ConnectAsync(long? cursor, CancellationToken cancellationToken);
    IAsyncEnumerable<BlueskyStreamEvent> ReadEventsAsync(CancellationToken cancellationToken);
}

public sealed class BlueskyJetstreamClient(ILogger<BlueskyJetstreamClient> logger) : IBlueskyJetstreamClient
{
    private const int MaximumMessageBytes = 1_048_576;
    private ClientWebSocket? _socket;

    public async Task ConnectAsync(long? cursor, CancellationToken cancellationToken)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        var uri = BuildEndpoint(cursor);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await _socket.ConnectAsync(uri, timeout.Token);
    }

    public async IAsyncEnumerable<BlueskyStreamEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("The Jetstream connection has not been opened.");
        var buffer = new byte[16 * 1024];

        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    yield break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("Jetstream sent a non-text message.");
                }

                if (message.Length + result.Count > MaximumMessageBytes)
                {
                    throw new InvalidDataException("Jetstream message exceeded the one-megabyte limit.");
                }

                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            BlueskyStreamEvent? item;
            try
            {
                using var document = JsonDocument.Parse(message.ToArray());
                BlueskyJetstreamEventParser.TryParse(document.RootElement, out item);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Ignoring a malformed Bluesky Jetstream event.");
                continue;
            }

            if (item is not null)
            {
                yield return item;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket?.Abort();
        _socket?.Dispose();
        _socket = null;
        return ValueTask.CompletedTask;
    }

    internal static Uri BuildEndpoint(long? cursor)
    {
        var query = "?collections=app.bsky.feed.post&kinds=commit";
        if (cursor is not null)
        {
            query += $"&cursor={cursor.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return new Uri($"wss://jetstream.us-east.bsky.network/xrpc/network.bsky.jetstream.subscribeEvents{query}");
    }
}

public static class BlueskyJetstreamEventParser
{
    public static bool TryParse(JsonElement root, out BlueskyStreamEvent? item)
    {
        item = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("payload", out var commit) ||
            commit.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Jetstream event is missing its payload.");
        }

        if (!commit.TryGetProperty("$type", out var eventType) ||
            eventType.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Jetstream event is missing its type discriminator.");
        }

        if (eventType.GetString() != "network.bsky.jetstream.subscribeEvents#commit")
        {
            return false;
        }

        var sequence = RequiredInt64(commit, "seq");
        var did = RequiredString(commit, "did");
        var collection = RequiredString(commit, "collection");
        var rkey = RequiredString(commit, "rkey");
        var operation = RequiredString(commit, "operation");
        if (!string.Equals(collection, "app.bsky.feed.post", StringComparison.Ordinal))
        {
            return false;
        }

        string? text = null;
        DateTime? createdAtUtc = null;
        IReadOnlyList<PostImage> images = [];
        if (operation is "create" or "update" &&
            commit.TryGetProperty("record", out var record) &&
            record.ValueKind == JsonValueKind.Object)
        {
            if (record.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String)
            {
                text = textValue.GetString();
            }

            if (record.TryGetProperty("createdAt", out var createdAtValue) &&
                createdAtValue.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(
                    createdAtValue.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var createdAt))
            {
                createdAtUtc = createdAt;
            }

            images = PostMedia.BlueskyRecordImages(record, did);
        }

        item = new BlueskyStreamEvent(sequence, did, collection, rkey, operation, text, createdAtUtc, images);
        return true;
    }

    private static long RequiredInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result) ||
            result <= 0)
        {
            throw new JsonException($"Jetstream commit is missing a valid '{propertyName}'.");
        }

        return result;
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"Jetstream commit is missing a valid '{propertyName}'.");
        }

        return value.GetString()!;
    }
}
