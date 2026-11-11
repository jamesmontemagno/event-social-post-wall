using EventWall.Api.Models;
using EventWall.Api.Services;

namespace EventWall.Api.Tests;

public sealed class SocialProfileTests
{
    [Theory]
    [InlineData(PostSource.Bluesky, "https://cdn.bsky.app/avatar.jpg")]
    [InlineData(PostSource.YouTube, "https://yt3.ggpht.com/avatar.jpg")]
    [InlineData(PostSource.YouTube, "https://yt3.googleusercontent.com/avatar.jpg")]
    [InlineData(PostSource.X, "https://pbs.twimg.com/profile_images/avatar.jpg")]
    public void Allows_known_provider_image_hosts(PostSource source, string url) =>
        Assert.Equal(url, SocialProfile.SafeAvatarUrl(source, url));

    [Theory]
    [InlineData("http://yt3.ggpht.com/avatar.jpg")]
    [InlineData("https://yt3.ggpht.com.attacker.example/avatar")]
    [InlineData("https://127.0.0.1/avatar")]
    [InlineData("https://yt3.ggpht.com:444/avatar")]
    [InlineData("https://user:password@yt3.ggpht.com/avatar")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/svg+xml,test")]
    public void Untrusted_or_insecure_images_fall_back_to_service_icon(string url) =>
        Assert.Null(SocialProfile.SafeAvatarUrl(PostSource.YouTube, url));

    [Fact]
    public void Portal_and_manual_posts_cannot_supply_remote_profiles()
    {
        Assert.Null(SocialProfile.SafeAvatarUrl(PostSource.Portal, "https://cdn.bsky.app/image.jpg"));
        Assert.Null(SocialProfile.SafeProfileUrl(PostSource.Manual, "https://x.com/account"));
        Assert.Null(SocialProfile.SafeProfileUrl(PostSource.Twitch, "https://attacker.example/profile"));
    }
}
