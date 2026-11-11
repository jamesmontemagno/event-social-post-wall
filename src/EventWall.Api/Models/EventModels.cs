using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventWall.Api.Models;

public sealed record PostImage(string Url, string? Alt);

public enum PostSource
{
    Portal,
    Manual,
    Twitch,
    YouTube,
    X,
    Bluesky,
    Mastodon
}

public enum PostStatus
{
    Pending,
    Live,
    Rejected,
    Withdrawn
}

public enum PostFilterReason
{
    Profanity,
    AllCaps,
    SingleWord,
    EmojiOnly,
    LocationOnly,
    NoSubstantiveText,
    RepeatedCharacters,
    DuplicateText
}

public sealed record PostFilterSettings
{
    public bool Profanity { get; init; } = true;
    public bool AllCaps { get; init; } = true;
    public bool SingleWord { get; init; } = true;
    public bool EmojiOnly { get; init; } = true;
    public bool LocationOnly { get; init; } = true;
    public bool NoSubstantiveText { get; init; } = true;
    public bool RepeatedCharacters { get; init; } = true;
    public bool DuplicateText { get; init; } = true;
    public string[] BlockedTerms { get; init; } = [];
    public string[] AllowedTerms { get; init; } = [];
    public string[] LocationPhrases { get; init; } = [];
}

public sealed class EventSettings
{
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public bool SubmissionsOpen { get; set; } = true;
    public bool YouTubeCollectionRunning { get; set; }
    public long Revision { get; set; }
    public string PostFiltersJson { get; set; } = "{}";
    public int FilterSettingsVersion { get; set; }
    [NotMapped]
    public PostFilterSettings PostFilters => JsonSerializer.Deserialize<PostFilterSettings>(PostFiltersJson)
        ?? throw new InvalidOperationException("Post filter settings are invalid.");
}

public sealed class EventPost
{
    public Guid Id { get; set; }
    public required string EventSlug { get; set; }
    public PostSource Source { get; set; }
    public required string ExternalId { get; set; }
    public required string Text { get; set; }
    public required string AuthorName { get; set; }
    public string? AuthorExternalId { get; set; }
    public string? AuthorProfileUrl { get; set; }
    public string? AuthorAvatarUrl { get; set; }
    public string ImagesJson { get; set; } = "[]";
    [NotMapped]
    public IReadOnlyList<PostImage> Images => JsonSerializer.Deserialize<List<PostImage>>(ImagesJson)
        ?? throw new InvalidOperationException("Post image metadata is invalid.");
    public string? SourceUrl { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? SourceCreatedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public PostStatus Status { get; set; }
    public long? PublishedRevision { get; set; }
    public int Version { get; set; }
    public string FilterReasonsJson { get; set; } = "[]";
    [NotMapped]
    public IReadOnlyList<PostFilterReason> FilterReasons => JsonSerializer.Deserialize<List<PostFilterReason>>(FilterReasonsJson)
        ?? throw new InvalidOperationException("Post filter findings are invalid.");
}

public sealed class EventPromo
{
    public Guid Id { get; set; }
    public required string EventSlug { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public string? Url { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool Active { get; set; } = true;
    public int Version { get; set; }
}

public sealed record PromoDto(Guid Id, string Title, string Description, string? Url, DateTime CreatedAtUtc, bool Active, int Version);
public sealed record PromoRequest(string? Title, string? Description, string? Url, int Version = 0);
public sealed record PromoVersionRequest(int Version);
public sealed record PromoActiveRequest(int Version, bool? Active);

public sealed class ModerationAction
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public PostStatus FromStatus { get; set; }
    public PostStatus ToStatus { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public required string StaffSessionId { get; set; }
    public string? StaffUsername { get; set; }
    public Guid? StaffMemberId { get; set; }
    public string? Reason { get; set; }
}

public sealed class ConnectorDailyUsage
{
    public required string Id { get; set; }
    public int ReservedPostReads { get; set; }
    public int ReservedUserReads { get; set; }
    public int Version { get; set; }
}

public sealed class ProviderCredential
{
    public required string Provider { get; set; }
    public required string ProtectedRefreshToken { get; set; }
    public int Version { get; set; }
}

public sealed class BlockedAuthor
{
    public required string EventSlug { get; set; }
    public PostSource Source { get; set; }
    public required string AuthorExternalId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public required string StaffSessionId { get; set; }
    public string? StaffUsername { get; set; }
    public Guid? StaffMemberId { get; set; }
}

public sealed class StaffMember
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }
    [JsonIgnore]
    public string AccessCodeHash { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime CodeRotatedAtUtc { get; set; }
    public DateTime? LastSignedInAtUtc { get; set; }
}

public sealed record StaffMemberDto(Guid Id, string Username, bool Enabled, int Version,
    DateTime CreatedAtUtc, DateTime CodeRotatedAtUtc, DateTime? LastSignedInAtUtc, int ModerationActions);
public sealed record StaffInviteDto(StaffMemberDto Member, string AccessCode);
public sealed record StaffInviteRequest(string? Username);
public sealed record StaffMemberStatusRequest(int Version, bool Enabled);
public sealed record StaffMemberVersionRequest(int Version);
public sealed record YouTubeCollectionRequest(bool? Running);

public sealed class ConnectorCheckpoint
{
    public required string Provider { get; set; }
    public long Cursor { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed record PublicEventDto(string Slug, string Name, bool SubmissionsOpen, bool DemoMode = false,
    string QuestionHashtag = "#communityevent", string? SubmissionSiteUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WallSiteUrl = null);

public sealed record SubmissionConfigDto(string Slug, string Name, bool DemoMode, string SubmissionToken, string? TurnstileSiteKey = null);

public sealed record PublicPostDto(
    Guid Id,
    PostSource Source,
    string Text,
    string AuthorName,
    string? SourceUrl,
    DateTime ReceivedAtUtc,
    DateTime? SourceCreatedAtUtc,
    long PublishedRevision,
    string? AuthorProfileUrl = null,
    string? AuthorAvatarUrl = null,
    IReadOnlyList<PostImage>? Images = null,
    DateTime? ApprovedAtUtc = null)
{
    public static PublicPostDto From(EventPost post) => new(
        post.Id,
        post.Source,
        post.Text,
        post.AuthorName,
        post.SourceUrl,
        DateTime.SpecifyKind(post.ReceivedAtUtc, DateTimeKind.Utc),
        post.SourceCreatedAtUtc is { } createdAtUtc ? DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc) : null,
        post.PublishedRevision ?? 0,
        post.AuthorProfileUrl,
        post.AuthorAvatarUrl,
        post.Images,
        post.DecidedAtUtc is { } approvedAtUtc ? DateTime.SpecifyKind(approvedAtUtc, DateTimeKind.Utc) : null);
}

public sealed record StaffPostDto(
    Guid Id,
    PostSource Source,
    string ExternalId,
    string Text,
    string AuthorName,
    string? SourceUrl,
    DateTime ReceivedAtUtc,
    DateTime? SourceCreatedAtUtc,
    DateTime? DecidedAtUtc,
    PostStatus Status,
    int Version,
    bool CanBlockAuthor,
    bool AuthorBlocked,
    string? AuthorProfileUrl,
    string? AuthorAvatarUrl,
    IReadOnlyList<PostImage> Images,
    IReadOnlyList<PostFilterReason> FilterReasons,
    string? ModeratedBy = null)
{
    public static StaffPostDto From(EventPost post, bool authorBlocked = false, string? moderatedBy = null) => new(
        post.Id,
        post.Source,
        post.ExternalId,
        post.Text,
        post.AuthorName,
        post.SourceUrl,
        DateTime.SpecifyKind(post.ReceivedAtUtc, DateTimeKind.Utc),
        post.SourceCreatedAtUtc is { } createdAtUtc ? DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc) : null,
        post.DecidedAtUtc is { } decidedAtUtc ? DateTime.SpecifyKind(decidedAtUtc, DateTimeKind.Utc) : null,
        post.Status,
        post.Version,
        post.Source is not (PostSource.Portal or PostSource.Manual) && !string.IsNullOrWhiteSpace(post.AuthorExternalId),
        authorBlocked,
        post.AuthorProfileUrl,
        post.AuthorAvatarUrl,
        post.Images,
        post.FilterReasons,
        moderatedBy);
}

public sealed record WallSnapshotDto(long Revision, int NewCount, IReadOnlyList<PublicPostDto> Items,
    IReadOnlyList<PromoDto> Promos);
public sealed record WallChangesDto(long Revision, int NewCount, IReadOnlyList<Guid> LiveKnownIds,
    IReadOnlyList<PromoDto> Promos);
public sealed record WallChangesRequest(long SinceRevision, IReadOnlyList<Guid>? KnownIds, long? KnownThroughRevision = null);
public sealed record AdminEventDto(
    string Slug, string Name, bool SubmissionsOpen, long Revision,
    PostFilterSettings PostFilters, int FilterSettingsVersion)
{
    public static AdminEventDto From(EventSettings settings) => new(
        settings.Slug, settings.Name, settings.SubmissionsOpen, settings.Revision,
        settings.PostFilters, settings.FilterSettingsVersion);
}

public sealed record SubmissionRequest(string? Text, string? DisplayName, string? Website);
public sealed record LoginRequest(string? Passcode, string? Username = null);
public sealed record ModerationRequest(int Version, string? Reason);
public sealed record ManualPostRequest(string? Text, string? AuthorName, string? SourceUrl);
public sealed record SubmissionSettingsRequest(bool Open);
public sealed record PostFilterSettingsRequest(PostFilterSettings? PostFilters, int Version);
