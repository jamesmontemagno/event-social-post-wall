using System.Globalization;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public sealed class SocialPollingWorker(
    IConfiguration configuration,
    IHttpClientFactory clients,
    ProviderTokenService tokens,
    EventPostIngestor ingestor,
    XReadBudget xReadBudget,
    XAuthorProfileResolver xProfiles,
    SourceHealthRegistry health,
    ILogger<SocialPollingWorker> logger,
    IDbContextFactory<EventWallDbContext> databaseFactory,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private const int XDefaultPollIntervalSeconds = 300;
    private const int MastodonDefaultPollIntervalSeconds = 180;
    private const int MastodonPageSize = 40;
    private const int MastodonMaximumPages = 5;
    private static readonly TimeSpan XSinceIdMaximumAge = TimeSpan.FromDays(6);
    private const long XSnowflakeEpochMilliseconds = 1288834974657;
    private const string XCheckpointProvider = "X";
    private const string MastodonCheckpointProvider = "Mastodon";
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly EventConfiguration _event = new(configuration);
    private string? _youtubePageToken;
    private DateTime _youtubeNextPollUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextX = DateTime.MinValue;
        var nextMastodon = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (IsConfigured("X") && now >= nextX)
            {
                nextX = now.Add(XPollInterval(configuration));
                await RunSourceAsync("X", CollectXAsync, stoppingToken);
            }
            else if (!configuration.GetValue<bool>("Social:X:Enabled"))
            {
                health.Report("X", "disabled", "Collection is disabled; no API reads are being made.");
            }

            if (now >= nextMastodon && IsConfigured("Mastodon"))
            {
                nextMastodon = now.Add(MastodonPollInterval(configuration));
                await RunSourceAsync("Mastodon", CollectMastodonAsync, stoppingToken);
            }

            if (IsConfigured("YouTube"))
            {
                await RunSourceAsync("YouTube", async cancellationToken =>
                {
                    if (!await IsYouTubeCollectionRunningAsync(cancellationToken))
                    {
                        _youtubePageToken = null;
                        return;
                    }
                    if (now >= _youtubeNextPollUtc)
                    {
                        _youtubeNextPollUtc = YouTubePollingSchedule.NextPollUtc(now);
                        await CollectYouTubeAsync(cancellationToken);
                    }
                }, stoppingToken);
            }
            else if (!configuration.GetValue<bool>("Social:YouTube:Enabled"))
            {
                health.Report("YouTube", "disabled", "Collection is disabled.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public static TimeSpan XPollInterval(IConfiguration configuration) => TimeSpan.FromSeconds(
        int.TryParse(configuration["Social:X:PollIntervalSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Clamp(seconds, 60, 3600)
            : XDefaultPollIntervalSeconds);

    public static TimeSpan MastodonPollInterval(IConfiguration configuration) => TimeSpan.FromSeconds(
        int.TryParse(configuration["Social:Mastodon:PollIntervalSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Clamp(seconds, 60, 3600)
            : MastodonDefaultPollIntervalSeconds);

    private bool IsConfigured(string provider)
    {
        var status = SourceSetupStatus.Build(configuration).Single(item => item.Provider == provider);
        if (!status.Enabled)
        {
            health.Report(provider, "disabled", "Collection is disabled.");
            return false;
        }

        if (!status.Configured)
        {
            health.Report(provider, "needs-configuration", "Required server-side settings are missing.");
            return false;
        }

        if (!configuration.GetValue<bool>($"Social:{provider}:PolicyApproved"))
        {
            health.Report(provider, "policy-review-required", "Collection remains paused until platform rules are reviewed.");
            return false;
        }

        return true;
    }

    private async Task RunSourceAsync(
        string provider,
        Func<CancellationToken, Task> collect,
        CancellationToken cancellationToken)
    {
        try
        {
            await collect(cancellationToken);
        }
        catch (YouTubeRequestException exception)
        {
            var detail = exception.Message;
            if (exception.QuotaExhausted)
            {
                _youtubeNextPollUtc = YouTubePollingSchedule.NextQuotaResetUtc(_clock.GetUtcNow().UtcDateTime);
                detail += $" Collection is paused until the next midnight Pacific quota reset ({_youtubeNextPollUtc:O} UTC).";
            }
            health.Report(provider, exception.QuotaExhausted ? "budget-exhausted" : "error", detail);
            logger.LogWarning("Social connector {Provider} failed: {Detail}", provider, detail);
        }
        catch (HttpRequestException exception)
        {
            var detail = exception.StatusCode is null
                ? "The provider request failed. Check the connection and account settings."
                : $"The provider returned HTTP {(int)exception.StatusCode}. Check credentials, access, and quota.";
            health.Report(provider, "error", detail);
            logger.LogWarning("Social connector {Provider} failed with HTTP status {StatusCode}.", provider, exception.StatusCode);
        }
        catch (JsonException)
        {
            health.Report(provider, "error", "The provider returned an unexpected response.");
            logger.LogWarning("Social connector {Provider} returned an unexpected response.", provider);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            health.Report(provider, "error", "The provider request timed out.");
            logger.LogWarning("Social connector {Provider} timed out.", provider);
        }
        catch (DbUpdateException)
        {
            health.Report(provider, "error", "The provider response could not be saved. Check database availability.");
            logger.LogError("Social connector {Provider} could not persist an incoming post.", provider);
        }
        catch (DbException)
        {
            health.Report(provider, "error", "Collection settings could not be read. Check database availability; no new provider requests will be made until settings can be read.");
            logger.LogError("Social connector {Provider} could not read collection settings.", provider);
        }
        catch (InvalidOperationException exception)
        {
            health.Report(provider, "error", "The provider response or configuration could not be processed.");
            logger.LogWarning("Social connector {Provider} could not process its response: {Message}", provider, exception.Message);
        }
    }

    private async Task CollectXAsync(CancellationToken cancellationToken)
    {
        var sinceId = await GetXSinceIdAsync(cancellationToken);
        var maximumDailySpend = configuration.GetValue<decimal>("Social:X:MaximumDailySpendUsd");
        var reservation = configuration.GetValue<bool>("Social:X:SpendApproved")
            ? await xReadBudget.TryReserveSearchAsync(maximumDailySpend, cancellationToken)
            : null;
        if (reservation is null)
        {
            var unresolved = await RefreshXProfilesAsync(cancellationToken);
            health.Report("X", "budget-exhausted",
                $"The explicit daily read budget does not allow another 100-post request. {unresolved} author profiles remain unavailable.");
            return;
        }

        var bearerToken = configuration["Social:X:BearerToken"]!;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.x.com/2/tweets/search/recent?query={Uri.EscapeDataString(_event.XQuery)}&max_results=100&tweet.fields=created_at,author_id,edit_history_tweet_ids,attachments,referenced_tweets&expansions=attachments.media_keys&media.fields=type,url,alt_text" +
            (sinceId is null ? "" : $"&since_id={sinceId.Value.ToString(CultureInfo.InvariantCulture)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await clients.CreateClient("social").SendAsync(request, cancellationToken);
        if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)
            await xReadBudget.ReconcileSearchAsync(reservation, 0, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var imported = 0;
        var excludedNonOriginals = 0;
        var hasPosts = document.RootElement.TryGetProperty("data", out var posts);
        if (hasPosts && posts.ValueKind != JsonValueKind.Array)
            throw new JsonException("The X post response data was not an array.");
        await xReadBudget.ReconcileSearchAsync(reservation, hasPosts ? posts.GetArrayLength() : 0, cancellationToken);
        long? newestId = null;
        if (hasPosts)
        {
            foreach (var post in posts.EnumerateArray())
            {
                if (post.TryGetProperty("id", out var postId) &&
                    long.TryParse(postId.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) &&
                    numericId > (newestId ?? 0))
                {
                    newestId = numericId;
                }

                if (post.TryGetProperty("referenced_tweets", out var references))
                {
                    if (references.ValueKind != JsonValueKind.Array)
                        throw new JsonException("The X post references were not an array.");
                    if (references.GetArrayLength() > 0)
                    {
                        excludedNonOriginals++;
                        continue;
                    }
                }
                var id = post.GetProperty("id").GetString();
                var text = post.GetProperty("text").GetString();
                var authorId = post.TryGetProperty("author_id", out var author) ? author.GetString() : null;
                if (id is null || text is null)
                {
                    continue;
                }

                var rootId = id;
                if (post.TryGetProperty("edit_history_tweet_ids", out var history) &&
                    history.ValueKind == JsonValueKind.Array &&
                    history.GetArrayLength() > 0)
                {
                    rootId = history[0].GetString() ?? id;
                }

                var link = $"https://x.com/i/status/{Uri.EscapeDataString(id)}";
                if (await ingestor.IngestAsync(
                    PostSource.X,
                    rootId,
                    text,
                    authorId is null ? "X user" : $"X user {authorId}",
                    authorId,
                    link,
                    ReadDate(post, "created_at"),
                    cancellationToken,
                    authorId is null ? null : $"https://x.com/i/user/{Uri.EscapeDataString(authorId)}",
                    images: PostMedia.XImages(post, document.RootElement)))
                {
                    imported++;
                }
            }
        }

        if (newestId > (sinceId ?? 0))
        {
            await SaveXSinceIdAsync(newestId.Value, cancellationToken);
        }

        var unavailableProfiles = await RefreshXProfilesAsync(cancellationToken);
        health.Report(
            "X",
            "connected-partial",
            $"Recent Search collects standalone original posts only; retweets, replies, and quote posts are excluded. Each poll requests only posts newer than the last one seen. This poller does not receive deletion events. {imported} new matching posts found; {excludedNonOriginals} non-original results ignored. Post and profile reads share the daily spend ceiling. Profiles are cached for 24 hours; {unavailableProfiles} author profiles remain unavailable.",
            DateTime.UtcNow);
    }

    private async Task<int> RefreshXProfilesAsync(CancellationToken cancellationToken)
    {
        var ids = await ingestor.GetXAuthorIdsAsync(cancellationToken);
        var profiles = await xProfiles.GetAsync(ids, cancellationToken);
        await ingestor.RefreshXAuthorProfilesAsync(profiles.Values.ToArray(), cancellationToken);
        return ids.Count - profiles.Count;
    }

    private async Task<long?> GetXSinceIdAsync(CancellationToken cancellationToken)
    {
        var cursor = await GetCheckpointAsync(XCheckpointProvider, cancellationToken);
        if (cursor is null)
        {
            return null;
        }

        // X rejects since_id values older than the Recent Search window; post IDs are snowflakes that encode their creation time.
        var postedAt = DateTimeOffset.FromUnixTimeMilliseconds((cursor.Value >> 22) + XSnowflakeEpochMilliseconds);
        return _clock.GetUtcNow() - postedAt < XSinceIdMaximumAge ? cursor : null;
    }

    private Task SaveXSinceIdAsync(long newestId, CancellationToken cancellationToken) =>
        SaveCheckpointAsync(XCheckpointProvider, newestId, cancellationToken);

    private async Task CollectMastodonAsync(CancellationToken cancellationToken)
    {
        var instance = MastodonStatus.InstanceUri(configuration)
            ?? throw new InvalidOperationException("The Mastodon server URL is invalid.");
        var cursor = await GetCheckpointAsync(MastodonCheckpointProvider, cancellationToken);
        var token = configuration["Social:Mastodon:AccessToken"];
        var imported = 0;
        var skipped = 0;
        var pages = 0;
        for (; pages < MastodonMaximumPages; pages++)
        {
            var path = $"api/v1/timelines/tag/{Uri.EscapeDataString(_event.Hashtag)}?limit={MastodonPageSize}" +
                (cursor is null ? "" : $"&min_id={cursor.Value.ToString(CultureInfo.InvariantCulture)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(instance, path));
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await clients.CreateClient("social").SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                health.Report("Mastodon", "error",
                    "The Mastodon server requires authorization for hashtag timelines. Configure Social:Mastodon:AccessToken with read:statuses access.");
                return;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                health.Report("Mastodon", "error",
                    "The Mastodon server rate-limited the hashtag timeline. Collection will retry at the next poll interval.");
                return;
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("The Mastodon hashtag timeline was not an array.");

            var statuses = document.RootElement.EnumerateArray()
                .Select(status => (Status: status, Id: MastodonStatus.ReadId(status)))
                .Where(item => item.Id is not null)
                .OrderBy(item => item.Id)
                .ToList();
            foreach (var (status, _) in statuses)
            {
                if (MastodonStatus.SkipReason(status) != MastodonSkipReason.None ||
                    MastodonStatus.Read(status, instance) is not { } post)
                {
                    skipped++;
                    continue;
                }

                if (await ingestor.IngestAsync(
                    PostSource.Mastodon,
                    post.ExternalId,
                    post.Text,
                    post.AuthorName,
                    post.AuthorId,
                    post.PostUrl,
                    post.CreatedAtUtc,
                    cancellationToken,
                    post.ProfileUrl,
                    post.AvatarUrl,
                    post.Images))
                {
                    imported++;
                }
            }

            var newest = statuses.Count == 0 ? null : statuses[^1].Id;
            var firstRun = cursor is null;
            if (newest > (cursor ?? 0))
            {
                await SaveCheckpointAsync(MastodonCheckpointProvider, newest.Value, cancellationToken);
                cursor = newest;
            }

            // The first poll establishes a starting point instead of backfilling older hashtag history.
            if (firstRun || document.RootElement.GetArrayLength() < MastodonPageSize)
            {
                pages++;
                break;
            }
        }

        health.Report(
            "Mastodon",
            "connected-partial",
            $"Polling the public {_event.QuestionHashtag} timeline on {instance.Host}. Edits and deletions are not detected, and only posts federated to this server are visible. {imported} new matching posts found; {skipped} posts skipped by Mastodon safety filters across {pages} page{(pages == 1 ? "" : "s")}.",
            DateTime.UtcNow);
    }

    private async Task<long?> GetCheckpointAsync(string provider, CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var cursor = await database.ConnectorCheckpoints.AsNoTracking()
            .Where(item => item.Provider == provider)
            .Select(item => (long?)item.Cursor)
            .SingleOrDefaultAsync(cancellationToken);
        return cursor is > 0 ? cursor : null;
    }

    private async Task SaveCheckpointAsync(string provider, long newestId, CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var checkpoint = await database.ConnectorCheckpoints
            .SingleOrDefaultAsync(item => item.Provider == provider, cancellationToken);
        if (checkpoint is null)
        {
            database.ConnectorCheckpoints.Add(new ConnectorCheckpoint
            {
                Provider = provider,
                Cursor = newestId,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        else if (newestId > checkpoint.Cursor)
        {
            checkpoint.Cursor = newestId;
            checkpoint.UpdatedAtUtc = DateTime.UtcNow;
        }
        else
        {
            return;
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> IsYouTubeCollectionRunningAsync(CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.Events.AsNoTracking().Select(settings => settings.YouTubeCollectionRunning)
            .SingleAsync(cancellationToken);
    }

    private async Task CollectYouTubeAsync(CancellationToken cancellationToken)
    {
        var token = await tokens.GetAccessTokenAsync("YouTube", cancellationToken);
        if (!await IsYouTubeCollectionRunningAsync(cancellationToken))
            return;
        var chatId = configuration["Social:YouTube:LiveChatId"]!;
        var query = $"https://youtube.googleapis.com/youtube/v3/liveChat/messages?liveChatId={Uri.EscapeDataString(chatId)}&part=snippet,authorDetails&maxResults=2000";
        if (!string.IsNullOrWhiteSpace(_youtubePageToken))
        {
            query += $"&pageToken={Uri.EscapeDataString(_youtubePageToken)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await clients.CreateClient("social").SendAsync(request, cancellationToken);
        await YouTubeRequestException.EnsureSuccessAsync(response, "live chat request", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("nextPageToken", out var nextPage))
        {
            _youtubePageToken = nextPage.GetString();
        }

        var interval = root.TryGetProperty("pollingIntervalMillis", out var pollingInterval)
            ? pollingInterval.GetInt32()
            : 10_000;
        _youtubeNextPollUtc = YouTubePollingSchedule.NextPollUtc(_clock.GetUtcNow().UtcDateTime, interval);
        var imported = 0;
        if (root.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var snippet = item.GetProperty("snippet");
                var type = snippet.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                if (type == "messageDeletedEvent" &&
                    snippet.TryGetProperty("messageDeletedEventDetails", out var deleted) &&
                    deleted.TryGetProperty("deletedMessageId", out var deletedId))
                {
                    await ingestor.RemoveMessageAsync(
                        PostSource.YouTube,
                        deletedId.GetString() ?? string.Empty,
                        "YouTube reported that the chat message was deleted.",
                        cancellationToken);
                    continue;
                }

                if (id is null || !snippet.TryGetProperty("textMessageDetails", out var details) ||
                    !details.TryGetProperty("messageText", out var messageText))
                {
                    continue;
                }

                var authorDetails = item.GetProperty("authorDetails");
                var displayName = authorDetails.TryGetProperty("displayName", out var name)
                    ? name.GetString() ?? "YouTube viewer"
                    : "YouTube viewer";
                var authorId = authorDetails.TryGetProperty("channelId", out var channel)
                    ? channel.GetString()
                    : null;
                var profileUrl = authorDetails.TryGetProperty("channelUrl", out var channelUrl) ? channelUrl.GetString() : null;
                var avatarUrl = authorDetails.TryGetProperty("profileImageUrl", out var imageUrl) ? imageUrl.GetString() : null;
                if (await ingestor.IngestAsync(
                    PostSource.YouTube,
                    id,
                    messageText.GetString() ?? string.Empty,
                    displayName,
                    authorId,
                    null,
                    ReadDate(snippet, "publishedAt"),
                    cancellationToken, profileUrl, avatarUrl))
                {
                    imported++;
                }
            }
        }

        health.Report(
            "YouTube",
            "connected-partial",
            $"Live chat polling is active with a minimum 60-second interval; clear/deletion coverage must be validated for this channel. {imported} new #question messages found.",
            DateTime.UtcNow);
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
