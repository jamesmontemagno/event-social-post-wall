using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace EventWall.Api.Services;

public sealed record BlueskyAuthorProfile(string? Handle, string? DisplayName, string? AvatarUrl);

public sealed class BlueskyProfileResolver(
    IHttpClientFactory clients,
    IMemoryCache cache,
    ILogger<BlueskyProfileResolver> logger)
{
    public async Task<BlueskyAuthorProfile> GetAsync(string did, CancellationToken cancellationToken)
    {
        try
        {
            return await cache.GetOrCreateAsync(did, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                var url = $"https://public.api.bsky.app/xrpc/app.bsky.actor.getProfile?actor={Uri.EscapeDataString(did)}";
                using var response = await clients.CreateClient("social").GetAsync(url, timeout.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("The Bluesky profile response was not an object.");
                }

                return new BlueskyAuthorProfile(
                    ReadString(root, "handle"),
                    ReadString(root, "displayName"),
                    ReadString(root, "avatar"));
            }) ?? Fallback(did);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Could not load a Bluesky profile; the post will use the service icon.");
            return Fallback(did);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Bluesky returned an invalid profile; the post will use the service icon.");
            return Fallback(did);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Loading a Bluesky profile timed out; the post will use the service icon.");
            return Fallback(did);
        }
    }

    private static BlueskyAuthorProfile Fallback(string did) => new(did, null, null);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
