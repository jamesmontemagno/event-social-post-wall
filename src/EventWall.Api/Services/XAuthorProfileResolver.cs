using System.Net.Http.Headers;
using System.Text.Json;
using EventWall.Api.Models;
using Microsoft.Extensions.Caching.Memory;

namespace EventWall.Api.Services;

public sealed record XAuthorProfile(string Id, string Username, string DisplayName, string? AvatarUrl)
{
    public string ProfileUrl => $"https://x.com/{Uri.EscapeDataString(Username)}";
}

public sealed class XAuthorProfileResolver(
    IConfiguration configuration,
    IHttpClientFactory clients,
    IMemoryCache cache,
    XReadBudget budget,
    ILogger<XAuthorProfileResolver> logger)
{
    private readonly SemaphoreSlim _lookupLock = new(1, 1);

    public static bool IsValidAuthorId(string id) =>
        id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit);

    public async Task<IReadOnlyDictionary<string, XAuthorProfile>> GetAsync(
        IEnumerable<string> authorIds,
        CancellationToken cancellationToken)
    {
        var ids = authorIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Any(id => !IsValidAuthorId(id)))
        {
            throw new ArgumentException("X profile lookups require numeric account IDs of at most 20 digits.", nameof(authorIds));
        }

        var profiles = new Dictionary<string, XAuthorProfile>(StringComparer.Ordinal);
        await _lookupLock.WaitAsync(cancellationToken);
        try
        {
            var missing = new List<string>();
            foreach (var id in ids)
            {
                if (cache.TryGetValue<XAuthorProfile?>(CacheKey(id), out var cached))
                {
                    if (cached is not null) profiles[id] = cached;
                }
                else
                {
                    missing.Add(id);
                }
            }
            if (missing.Count == 0) return profiles;

            if (!configuration.GetValue<bool>("Social:X:Enabled") ||
                !configuration.GetValue<bool>("Social:X:PolicyApproved") ||
                !configuration.GetValue<bool>("Social:X:SpendApproved"))
            {
                logger.LogWarning("X profile reads require collection, policy, and spend approval; uncached authors retain their existing attribution.");
                return profiles;
            }
            var token = configuration["Social:X:BearerToken"];
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("X profile reads require a configured bearer token.");
            }

            foreach (var chunk in missing.Chunk(100))
            {
                var reservation = await budget.TryReserveUserReadsAsync(
                    chunk.Length, configuration.GetValue<decimal>("Social:X:MaximumDailySpendUsd"), cancellationToken);
                if (reservation is null)
                {
                    logger.LogWarning("The daily X spend guard paused profile lookups; uncached authors retain their existing attribution.");
                    break;
                }
                var batch = chunk.Take(reservation.ReservedUserReads).ToArray();
                var lookupFailed = false;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        $"https://api.x.com/2/users?ids={string.Join(",", batch)}&user.fields=id,name,username,profile_image_url");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var response = await clients.CreateClient("social").SendAsync(request, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("The X user response was not an object.");
                    var hasUsers = document.RootElement.TryGetProperty("data", out var users);
                    if (hasUsers && users.ValueKind != JsonValueKind.Array)
                        throw new JsonException("The X user response data was not an array.");
                    if (!hasUsers && (!document.RootElement.TryGetProperty("errors", out var errors) ||
                        errors.ValueKind != JsonValueKind.Array))
                        throw new JsonException("The X user response contained neither profiles nor errors.");

                    await budget.ReconcileUserReadsAsync(reservation, hasUsers ? users.GetArrayLength() : 0, cancellationToken);
                    if (hasUsers)
                    {
                        foreach (var user in users.EnumerateArray())
                        {
                            if (user.ValueKind != JsonValueKind.Object)
                                throw new JsonException("An X user profile was not an object.");
                            var id = ReadString(user, "id");
                            var username = ReadString(user, "username");
                            if (id is null || !batch.Contains(id, StringComparer.Ordinal) ||
                                string.IsNullOrWhiteSpace(username) || username.Length > 15 ||
                                !username.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
                            {
                                logger.LogWarning("X returned an incomplete or unexpected user profile; it will not replace an author's attribution.");
                                continue;
                            }
                            var name = ReadString(user, "name")?.Trim();
                            if (name?.Length > 120) name = name[..120];
                            var profile = new XAuthorProfile(id, username,
                                string.IsNullOrWhiteSpace(name) ? $"@{username}" : name,
                                SocialProfile.SafeAvatarUrl(PostSource.X, ReadString(user, "profile_image_url")));
                            profiles[id] = profile;
                            cache.Set(CacheKey(id), profile, TimeSpan.FromHours(24));
                        }
                    }
                    if (batch.Any(id => !profiles.ContainsKey(id)))
                        logger.LogWarning("Some requested X profiles were unavailable; those authors retain their existing attribution.");
                }
                catch (HttpRequestException exception)
                {
                    lookupFailed = true;
                    logger.LogWarning(exception, "X profile lookup failed; authors retain their existing attribution and the read reservation is retained.");
                }
                catch (JsonException exception)
                {
                    lookupFailed = true;
                    logger.LogWarning(exception, "X returned an invalid profile response; authors retain their existing attribution.");
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    lookupFailed = true;
                    logger.LogWarning(exception, "X profile lookup timed out; authors retain their existing attribution and the read reservation is retained.");
                }
                foreach (var id in batch.Where(id => !profiles.ContainsKey(id)))
                    cache.Set<XAuthorProfile?>(CacheKey(id), null, TimeSpan.FromMinutes(5));
                if (lookupFailed || batch.Length < chunk.Length) break;
            }
            return profiles;
        }
        finally
        {
            _lookupLock.Release();
        }
    }

    private static string CacheKey(string id) => $"x-author:{id}";

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
