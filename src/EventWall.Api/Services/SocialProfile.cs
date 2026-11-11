using EventWall.Api.Models;

namespace EventWall.Api.Services;

public static class SocialProfile
{
    public static string? SafeImageUrl(PostSource source, string? value, IReadOnlyList<string>? mastodonHosts = null) =>
        SafeUrl(value, source switch
        {
            PostSource.Bluesky => ["cdn.bsky.app"],
            PostSource.X => ["pbs.twimg.com"],
            PostSource.Mastodon => mastodonHosts ?? [],
            _ => []
        });

    public static string? SafeProfileUrl(PostSource source, string? value, IReadOnlyList<string>? mastodonHosts = null) =>
        SafeUrl(value, source switch
        {
            PostSource.Bluesky => ["bsky.app"],
            PostSource.YouTube => ["www.youtube.com", "youtube.com"],
            PostSource.Twitch => ["www.twitch.tv", "twitch.tv"],
            PostSource.X => ["x.com", "www.x.com"],
            PostSource.Mastodon => mastodonHosts?.Take(1).ToArray() ?? [],
            _ => []
        });

    public static string? SafeAvatarUrl(PostSource source, string? value, IReadOnlyList<string>? mastodonHosts = null) =>
        SafeUrl(value, source switch
        {
            PostSource.Bluesky => ["cdn.bsky.app"],
            PostSource.YouTube => ["yt3.ggpht.com", "yt3.googleusercontent.com"],
            PostSource.Twitch => ["static-cdn.jtvnw.net"],
            PostSource.X => ["pbs.twimg.com"],
            PostSource.Mastodon => mastodonHosts ?? [],
            _ => []
        });

    private static string? SafeUrl(string? value, IReadOnlyCollection<string> hosts) =>
        value?.Length <= 2048 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
}
