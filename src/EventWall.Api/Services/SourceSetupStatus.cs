namespace EventWall.Api.Services;

public sealed record SourceStatusDto(
    string Provider,
    bool Enabled,
    bool Configured,
    bool Ready,
    string State,
    string Summary,
    string SetupUrl,
    IReadOnlyList<string> RequiredSettings,
    string? CostNote,
    DateTime? LastSuccessfulUtc,
    bool Simulated = false,
    bool? CollectionRunning = null,
    bool CollectionAvailable = false);

public static class SourceSetupStatus
{
    public static IReadOnlyList<SourceStatusDto> Build(
        IConfiguration configuration,
        SourceHealthRegistry? healthRegistry = null,
        bool youtubeCollectionRunning = false)
    {
        var sources = new[]
        {
            Create(configuration, healthRegistry, "Twitch",
                ["Social:Twitch:ClientId", "Social:Twitch:ClientSecret", "Social:Twitch:UserId", "Social:Twitch:BroadcasterUserId", "Social:Twitch:RefreshToken"],
                "Configure an authorized Twitch app/account and channel chat subscription. Only messages with the exact #question tag (case-insensitive) enter moderation; untagged chat is not stored. Connection and chat-deletion behavior must be rehearsed.",
                "https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/#channelchatmessage",
                null),
            Create(configuration, healthRegistry, "YouTube",
                ["Social:YouTube:LiveChatId", "Social:YouTube:ClientId", "Social:YouTube:ClientSecret", "Social:YouTube:RefreshToken"],
                "Configure the live chat ID and authorized YouTube OAuth app/refresh token. Only messages with the exact #question tag (case-insensitive) enter moderation; untagged chat is not stored. Validate chat availability, quota, and deletion behavior.",
                "https://developers.google.com/youtube/v3/live/docs/liveChatMessages/list",
                "YouTube Data API quota applies: polling costs 5 units per request, at most about 7,200 units/day per collector with the minimum 60-second interval. Other clients share the project quota; local #question filtering does not reduce quota use.",
                youtubeCollectionRunning),
            Create(configuration, healthRegistry, "X",
                ["Social:X:BearerToken", "Social:X:SpendApproved", "Social:X:MaximumDailySpendUsd"],
                "Configure an approved X developer app, bearer token, account entitlement, and explicit spend ceiling before enabling reads.",
                "https://docs.x.com/x-api/posts/search/introduction",
                "Pay-per-use reads; the configured ceiling is a local guard, not a provider billing guarantee."),
            Create(configuration, healthRegistry, "Bluesky",
                [],
                "Bluesky uses the public Jetstream live stream; no account or API credentials are required. Posts are filtered locally.",
                "https://bsky.network/docs/jetstream/",
                null),
            Create(configuration, healthRegistry, "Mastodon",
                ["Social:Mastodon:InstanceUrl"],
                "Mastodon polls one server's public timeline for Event:QuestionHashtag every three minutes by default. An access token is optional unless the server requires one. Boosts, replies, quotes, non-public posts, opted-out authors, bots, and posts with content warnings or sensitive media are skipped. Only posts federated to that server are visible, and edits and deletions are not detected.",
                "https://docs.joinmastodon.org/methods/timelines/#tag",
                null)
        };
        return sources;
    }

    private static SourceStatusDto Create(
        IConfiguration configuration,
        SourceHealthRegistry? healthRegistry,
        string provider,
        IReadOnlyList<string> requiredSettings,
        string summary,
        string setupUrl,
        string? costNote,
        bool? collectionRunning = null)
    {
        var health = healthRegistry?.Get(provider);
        if (DemoMode.IsEnabled(configuration))
        {
            return new SourceStatusDto(
                provider,
                true,
                true,
                true,
                "demo-simulated",
                $"Simulated demo feed: fictional {provider} messages stream into the queue. No provider API calls, credentials, or paid reads are used.",
                setupUrl,
                requiredSettings,
                null,
                health?.LastSuccessfulUtc,
                true);
        }

        var enabled = configuration.GetValue<bool>($"Social:{provider}:Enabled");
        var policyApproved = configuration.GetValue<bool>($"Social:{provider}:PolicyApproved");
        var configured = requiredSettings.All(key =>
        {
            var value = configuration[key];
            return !string.IsNullOrWhiteSpace(value) &&
                (!key.EndsWith("InstanceUrl", StringComparison.Ordinal) || MastodonStatus.TryParseInstance(value, out _)) &&
                (!key.EndsWith("SpendApproved", StringComparison.Ordinal) || bool.TryParse(value, out var approved) && approved) &&
                (!key.EndsWith("MaximumDailySpendUsd", StringComparison.Ordinal) || decimal.TryParse(value, out var limit) && limit > 0);
        });

        var configurationReady = enabled && policyApproved && configured;
        var ready = configurationReady && collectionRunning is not false && health?.State == "connected";
        var state = !enabled ? "disabled"
            : !configured ? "needs-configuration"
            : !policyApproved ? "policy-review-required"
            : collectionRunning is false ? "paused"
            : health?.State ?? "test-required";
        var explanation = state switch
        {
            "disabled" => "Disabled until an operator explicitly enables this source.",
            "needs-configuration" => "Required server-side settings are missing.",
            "policy-review-required" => "Source is configured but must remain paused until display, retention, deletion, and export rules are approved.",
            "paused" => "Collection is stopped. Start YouTube collection when the live stream begins; no YouTube API requests are made while stopped.",
            "connected" => "Connected and collecting.",
            "connected-partial" => "Connected, but one or more required safety subscriptions are unavailable.",
            "error" => "The source reported a connection or collection error.",
            "budget-exhausted" => provider == "YouTube"
                ? "The daily YouTube API quota is exhausted; collection is paused until the next midnight Pacific reset."
                : "The daily spend guard has paused collection.",
            "test-required" => "Credentials and policy gates are configured; waiting for a successful connector check.",
            _ => "The connector has not reported a healthy connection."
        };

        var healthDetail = state == "paused" || string.IsNullOrWhiteSpace(health?.Detail) ? string.Empty : $" {health.Detail}";
        return new SourceStatusDto(
            provider,
            enabled,
            configured,
            ready,
            state,
            healthDetail.Length > 0 ? $"{explanation} {summary}{healthDetail}" : $"{explanation} {summary}",
            setupUrl,
            requiredSettings,
            costNote,
            health?.LastSuccessfulUtc,
            CollectionRunning: collectionRunning,
            CollectionAvailable: collectionRunning is not null && configurationReady);
    }
}
