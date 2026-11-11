using EventWall.Api.Services;
using Microsoft.Extensions.Configuration;

namespace EventWall.Api.Tests;

public sealed class SourceSetupStatusTests
{
    [Fact]
    public void All_target_social_sources_are_present_and_disabled_by_default()
    {
        var configuration = new ConfigurationBuilder().Build();

        var sources = SourceSetupStatus.Build(configuration);

        Assert.Equal(new[] { "Twitch", "YouTube", "X", "Bluesky", "Mastodon" }, sources.Select(source => source.Provider));
        Assert.All(sources, source =>
        {
            Assert.False(source.Ready);
            Assert.False(source.Enabled);
        });
    }

    [Fact]
    public void X_stays_unready_without_explicit_spend_and_policy_approval()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Social:X:Enabled"] = "true",
                ["Social:X:PolicyApproved"] = "true",
                ["Social:X:BearerToken"] = "configured",
                ["Social:X:SpendApproved"] = "false",
                ["Social:X:MaximumDailySpendUsd"] = "5"
            })
            .Build();

        var source = Assert.Single(SourceSetupStatus.Build(configuration), item => item.Provider == "X");

        Assert.False(source.Configured);
        Assert.False(source.Ready);
    }

    [Fact]
    public void Configured_source_stays_unready_until_a_connection_check_succeeds()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Social:Bluesky:Enabled"] = "true",
                ["Social:Bluesky:PolicyApproved"] = "true"
            })
            .Build();

        var source = Assert.Single(SourceSetupStatus.Build(configuration), item => item.Provider == "Bluesky");

        Assert.True(source.Configured);
        Assert.False(source.Ready);
        Assert.Equal("test-required", source.State);

        var health = new SourceHealthRegistry();
        health.Report("Bluesky", "connected", "A successful query completed.", DateTime.UtcNow);
        source = Assert.Single(SourceSetupStatus.Build(configuration, health), item => item.Provider == "Bluesky");

        Assert.True(source.Ready);
    }

    [Fact]
    public void Bluesky_jetstream_needs_no_credentials_and_has_no_polling_interval()
    {
        var configuration = new ConfigurationBuilder().Build();

        var source = Assert.Single(SourceSetupStatus.Build(configuration), item => item.Provider == "Bluesky");

        Assert.True(source.Configured);
        Assert.False(source.Enabled);
        Assert.Contains("Jetstream", source.Summary);
        Assert.Contains("no account or API credentials", source.Summary);
        Assert.Equal("https://bsky.network/docs/jetstream/", source.SetupUrl);
    }

    [Fact]
    public void Simulated_Bluesky_source_does_not_report_a_real_poll_interval()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Enabled"] = "true"
            }).Build();

        var source = Assert.Single(SourceSetupStatus.Build(configuration), item => item.Provider == "Bluesky");

        Assert.True(source.Simulated);
    }

    [Fact]
    public void YouTube_waits_for_explicit_start_even_when_configuration_and_health_are_ready()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Social:YouTube:Enabled"] = "true",
            ["Social:YouTube:PolicyApproved"] = "true",
            ["Social:YouTube:LiveChatId"] = "fixture",
            ["Social:YouTube:ClientId"] = "fixture",
            ["Social:YouTube:ClientSecret"] = "fixture",
            ["Social:YouTube:RefreshToken"] = "fixture"
        }).Build();
        var health = new SourceHealthRegistry();
        health.Report("YouTube", "connected", "Previous collection succeeded.", DateTime.UtcNow);

        var paused = Assert.Single(SourceSetupStatus.Build(configuration, health), item => item.Provider == "YouTube");
        Assert.Equal("paused", paused.State);
        Assert.False(paused.Ready);
        Assert.False(paused.CollectionRunning);
        Assert.True(paused.CollectionAvailable);
        Assert.DoesNotContain("Previous collection succeeded", paused.Summary);

        var active = Assert.Single(SourceSetupStatus.Build(configuration, health, youtubeCollectionRunning: true),
            item => item.Provider == "YouTube");
        Assert.True(active.CollectionRunning);
        Assert.True(active.Ready);
    }
}
