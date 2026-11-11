using System.Text.RegularExpressions;
using System.Data;
using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public sealed class EventPostIngestor(
    IDbContextFactory<EventWallDbContext> databaseFactory,
    IConfiguration? configuration = null)
{
    private readonly IReadOnlyList<string> _mastodonHosts = MastodonStatus.AllowedHosts(configuration);
    private readonly EventConfiguration _event = configuration is null ? EventConfiguration.Default : new(configuration);
    private static readonly Regex QuestionTag = new(
        @"(?<![\p{L}\p{M}\p{N}_])#question(?![\p{L}\p{M}\p{N}_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool MatchesEventTerm(string? text) => EventConfiguration.Default.Matches(text);

    public static bool MatchesQuestionTag(string? text) =>
        !string.IsNullOrWhiteSpace(text) && QuestionTag.IsMatch(text);

    public async Task<bool> IngestAsync(
        PostSource source,
        string externalId,
        string text,
        string authorName,
        string? authorExternalId,
        string? sourceUrl,
        DateTime? sourceCreatedAtUtc,
        CancellationToken cancellationToken,
        string? authorProfileUrl = null,
        string? authorAvatarUrl = null,
        IReadOnlyList<PostImage>? images = null)
    {
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 240 ||
            string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(authorName) ||
            authorExternalId?.Length > 200)
        {
            return false;
        }

        if (source is PostSource.X or PostSource.Bluesky or PostSource.Mastodon && !_event.Matches(text))
        {
            return false;
        }
        if (source is PostSource.Twitch or PostSource.YouTube && !MatchesQuestionTag(text))
        {
            return false;
        }
        text = Truncate(text, 2000);
        authorName = Truncate(authorName, 120);
        var safeImages = (images ?? []).Select(image => new
            { Url = SocialProfile.SafeImageUrl(source, image.Url, _mastodonHosts), image.Alt })
            .Where(image => image.Url is not null)
            .DistinctBy(image => image.Url)
            .Take(4)
            .Select(image => new PostImage(image.Url!, Trim(image.Alt, 1000)))
            .ToList();
        var imagesJson = JsonSerializer.Serialize(safeImages);

        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var settings = await EventSeeder.LockEventForPostIngestionAsync(database, cancellationToken);
        var existing = await database.Posts.SingleOrDefaultAsync(
            post => post.EventSlug == settings.Slug &&
                post.Source == source &&
                post.ExternalId == externalId,
            cancellationToken);
        var blocked = source is not (PostSource.Portal or PostSource.Manual) &&
            !string.IsNullOrWhiteSpace(authorExternalId) &&
            await database.BlockedAuthors.AnyAsync(item =>
                item.EventSlug == settings.Slug && item.Source == source &&
                item.AuthorExternalId == authorExternalId, cancellationToken);

        if (existing is not null)
        {
            if (source == PostSource.X && authorExternalId is not null &&
                existing.AuthorExternalId == authorExternalId && authorName == $"X user {authorExternalId}")
            {
                authorName = existing.AuthorName;
                authorProfileUrl = existing.AuthorProfileUrl ?? authorProfileUrl;
                authorAvatarUrl = existing.AuthorAvatarUrl ?? authorAvatarUrl;
            }
            existing.AuthorProfileUrl = SocialProfile.SafeProfileUrl(source, authorProfileUrl, _mastodonHosts);
            existing.AuthorAvatarUrl = SocialProfile.SafeAvatarUrl(source, authorAvatarUrl, _mastodonHosts);
            if (existing.Text == text && existing.AuthorName == authorName && existing.ImagesJson == imagesJson)
            {
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            var checkDuplicate = existing.Text != text || existing.FilterReasons.Contains(PostFilterReason.DuplicateText);
            var previousState = existing.Status;
            if (existing.Status == PostStatus.Live)
            {
                settings.Revision++;
            }

            existing.Text = text;
            existing.AuthorName = authorName;
            existing.AuthorExternalId = Trim(authorExternalId, 200);
            existing.ImagesJson = imagesJson;
            existing.SourceUrl = SafeHttpsUrl(sourceUrl);
            existing.SourceCreatedAtUtc = sourceCreatedAtUtc;
            var automaticReason = await PostFiltering.ApplyDecisionAsync(
                database, existing, settings, blocked, checkDuplicate, cancellationToken);
            existing.Version++;
            database.ModerationActions.Add(new ModerationAction
            {
                Id = Guid.NewGuid(),
                PostId = existing.Id,
                FromStatus = previousState,
                ToStatus = existing.Status,
                CreatedAtUtc = DateTime.UtcNow,
                StaffSessionId = !blocked && automaticReason is not null ? "source:filter" : $"source:{source}",
                Reason = automaticReason ?? "Source content changed; renewed approval is required."
            });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var post = new EventPost
        {
            Id = Guid.NewGuid(),
            EventSlug = settings.Slug,
            Source = source,
            ExternalId = externalId,
            Text = text,
            AuthorName = authorName,
            AuthorExternalId = Trim(authorExternalId, 200),
            AuthorProfileUrl = SocialProfile.SafeProfileUrl(source, authorProfileUrl, _mastodonHosts),
            AuthorAvatarUrl = SocialProfile.SafeAvatarUrl(source, authorAvatarUrl, _mastodonHosts),
            ImagesJson = imagesJson,
            SourceUrl = SafeHttpsUrl(sourceUrl),
            ReceivedAtUtc = DateTime.UtcNow,
            SourceCreatedAtUtc = sourceCreatedAtUtc,
            Status = PostStatus.Pending
        };
        database.Posts.Add(post);
        await PostFiltering.ApplyToNewPostAsync(database, settings, post, cancellationToken, blocked);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<string>> GetXAuthorIdsAsync(CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var ids = await database.Posts.AsNoTracking()
            .Where(post => post.EventSlug == settings.Slug && post.Source == PostSource.X &&
                post.Status != PostStatus.Withdrawn && post.AuthorExternalId != null)
            .Select(post => post.AuthorExternalId!).Distinct().ToListAsync(cancellationToken);
        return ids.Where(XAuthorProfileResolver.IsValidAuthorId).ToArray();
    }

    public async Task RefreshXAuthorProfilesAsync(
        IReadOnlyCollection<XAuthorProfile> profiles,
        CancellationToken cancellationToken)
    {
        if (profiles.Count == 0) return;
        var byId = profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var ids = byId.Keys.ToArray();
        var posts = await database.Posts.Where(post =>
            post.EventSlug == settings.Slug && post.Source == PostSource.X &&
            post.AuthorExternalId != null && ids.Contains(post.AuthorExternalId)).ToListAsync(cancellationToken);
        foreach (var post in posts)
        {
            var profile = byId[post.AuthorExternalId!];
            post.AuthorName = Truncate(profile.DisplayName, 120);
            post.AuthorProfileUrl = SocialProfile.SafeProfileUrl(PostSource.X, profile.ProfileUrl);
            post.AuthorAvatarUrl = SocialProfile.SafeAvatarUrl(PostSource.X, profile.AvatarUrl);
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RemoveMessageAsync(
        PostSource source,
        string externalId,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var post = await database.Posts.SingleOrDefaultAsync(
            item => item.Source == source && item.ExternalId == externalId,
            cancellationToken);
        if (post is null || post.Status == PostStatus.Withdrawn)
        {
            return false;
        }

        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        ApplySourceRemoval(database, settings, post, reason);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> RemoveAuthorAsync(
        PostSource source,
        string authorExternalId,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var posts = await database.Posts.Where(item =>
                item.Source == source && item.AuthorExternalId == authorExternalId &&
                item.Status != PostStatus.Withdrawn)
            .ToListAsync(cancellationToken);
        if (posts.Count == 0)
        {
            return 0;
        }

        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        foreach (var post in posts)
        {
            ApplySourceRemoval(database, settings, post, "The source cleared this author's chat messages.");
        }

        await database.SaveChangesAsync(cancellationToken);
        return posts.Count;
    }

    public async Task<int> RemoveAllAsync(
        PostSource source,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var posts = await database.Posts.Where(item =>
                item.Source == source && item.Status != PostStatus.Withdrawn)
            .ToListAsync(cancellationToken);
        if (posts.Count == 0)
        {
            return 0;
        }

        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        foreach (var post in posts)
        {
            ApplySourceRemoval(database, settings, post, reason);
        }

        await database.SaveChangesAsync(cancellationToken);
        return posts.Count;
    }

    private static void ApplySourceRemoval(
        EventWallDbContext database,
        EventSettings settings,
        EventPost post,
        string reason)
    {
        var previousStatus = post.Status;
        if (previousStatus == PostStatus.Live)
        {
            settings.Revision++;
        }

        post.Status = PostStatus.Withdrawn;
        post.PublishedRevision = null;
        post.DecidedAtUtc = DateTime.UtcNow;
        post.Version++;
        database.ModerationActions.Add(new ModerationAction
        {
            Id = Guid.NewGuid(),
            PostId = post.Id,
            FromStatus = previousStatus,
            ToStatus = PostStatus.Withdrawn,
            CreatedAtUtc = DateTime.UtcNow,
            StaffSessionId = $"source:{post.Source}",
            Reason = reason
        });
    }

    private static string? Trim(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Truncate(value, maximumLength);

    private static string Truncate(string value, int maximumLength)
    {
        var length = Math.Min(value.Length, maximumLength);
        if (length < value.Length && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
            length--;
        return value[..length];
    }

    private static string? SafeHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? value[..Math.Min(value.Length, 2048)]
            : null;
}
