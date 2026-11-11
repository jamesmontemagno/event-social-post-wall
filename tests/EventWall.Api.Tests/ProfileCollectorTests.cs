using System.Net;
using System.Text;
using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Security;
using EventWall.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventWall.Api.Tests;

public sealed class ProfileCollectorTests
{
    [Fact]
    public async Task YouTube_filters_chat_and_preserves_profiles_deletions_and_request_count()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new DatabaseFactory(connection);
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.EnsureCreatedAsync();
            await EventSeeder.SeedAsync(database, new ConfigurationBuilder().Build());
            var settings = await database.Events.SingleAsync();
            settings.Revision = 1;
            database.Posts.Add(new()
            {
                EventSlug = settings.Slug, Source = PostSource.YouTube, ExternalId = "legacy",
                Text = "A previously approved untagged chat message", AuthorName = "Viewer",
                Status = PostStatus.Live, PublishedRevision = 1
            });
            await database.SaveChangesAsync();
        }
        const string provider = "YouTube";
        const string avatar = "https://yt3.ggpht.com/avatar.jpg";
        const string profile = "https://www.youtube.com/channel/viewer";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Social:{provider}:Enabled"] = "true",
            [$"Social:{provider}:PolicyApproved"] = "true",
            ["Social:YouTube:LiveChatId"] = "fixture",
            ["Social:YouTube:ClientId"] = "fixture",
            ["Social:YouTube:ClientSecret"] = "fixture",
            ["Social:YouTube:RefreshToken"] = "fixture"
        }).Build();
        using var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        var clients = new ClientFactory(client);
        var tokens = new ProviderTokenService(configuration, clients, factory, new EphemeralDataProtectionProvider());
        var health = new SourceHealthRegistry();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var budget = new XReadBudget(factory);
        var clock = new TestClock();
        using var worker = new SocialPollingWorker(configuration, clients, tokens, new EventPostIngestor(factory),
            budget, new XAuthorProfileResolver(configuration, clients, cache, budget, NullLogger<XAuthorProfileResolver>.Instance),
            health, NullLogger<SocialPollingWorker>.Instance, factory, clock);
        await worker.StartAsync(default);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            Assert.Empty(handler.Requests);
            await using (var database = factory.CreateDbContext())
            {
                (await database.Events.SingleAsync()).YouTubeCollectionRunning = true;
                await database.SaveChangesAsync();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (health.Get(provider) is null || health.Get(provider)?.LastSuccessfulUtc is null)
                await Task.Delay(10, timeout.Token);
            await using (var database = factory.CreateDbContext())
            {
                (await database.Events.SingleAsync()).YouTubeCollectionRunning = false;
                await database.SaveChangesAsync();
            }
            clock.UtcNow = clock.UtcNow.AddMinutes(2);
            await Task.Delay(TimeSpan.FromSeconds(6));
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            await worker.StopAsync(default);
        }
        await using var verify = factory.CreateDbContext();
        Assert.Equal(2, await verify.Posts.CountAsync());
        var post = await verify.Posts.SingleAsync(item => item.ExternalId == "one");
        Assert.Equal("How can Aspire deploy to Kubernetes? #QuEsTiOn", post.Text);
        Assert.Equal(PostStatus.Pending, post.Status);
        Assert.Equal("Viewer", post.AuthorName);
        Assert.Equal("viewer-id", post.AuthorExternalId);
        Assert.Equal(avatar, post.AuthorAvatarUrl);
        Assert.Equal(profile, post.AuthorProfileUrl);
        Assert.Empty(post.Images);
        var deleted = await verify.Posts.SingleAsync(item => item.ExternalId == "legacy");
        Assert.Equal(PostStatus.Withdrawn, deleted.Status);
        Assert.Null(deleted.PublishedRevision);
        Assert.Single(await verify.ModerationActions.ToListAsync());
        Assert.Contains("1 new #question messages found", health.Get(provider)!.Detail);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.True(
            uri.Host == "oauth2.googleapis.com" ||
            uri.AbsolutePath == "/youtube/v3/liveChat/messages"));
    }

    [Theory]
    [InlineData(false, "live chat request", "Enable it in that project", "accessNotConfigured", false)]
    [InlineData(true, "OAuth token refresh", "Google rejected the refresh token", "accessNotConfigured", false)]
    [InlineData(false, "live chat request", "quota is exhausted", "quotaExceeded", true)]
    public async Task YouTube_failures_reach_admin_source_status_with_safe_remediation(
        bool failTokenRefresh, string operation, string expected, string chatErrorReason, bool quotaExhausted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new DatabaseFactory(connection);
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.EnsureCreatedAsync();
            await EventSeeder.SeedAsync(database, new ConfigurationBuilder().Build());
            (await database.Events.SingleAsync()).YouTubeCollectionRunning = true;
            await database.SaveChangesAsync();
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Social:YouTube:Enabled"] = "true",
            ["Social:YouTube:PolicyApproved"] = "true",
            ["Social:YouTube:LiveChatId"] = "fixture",
            ["Social:YouTube:ClientId"] = "fixture",
            ["Social:YouTube:ClientSecret"] = "fixture",
            ["Social:YouTube:RefreshToken"] = "fixture"
        }).Build();
        using var handler = new FixtureHandler(failTokenRefresh, failChatRequest: !failTokenRefresh, chatErrorReason);
        using var client = new HttpClient(handler);
        var clients = new ClientFactory(client);
        var tokens = new ProviderTokenService(configuration, clients, factory, new EphemeralDataProtectionProvider());
        var health = new SourceHealthRegistry();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var budget = new XReadBudget(factory);
        var clock = new TestClock();
        using var worker = new SocialPollingWorker(configuration, clients, tokens, new EventPostIngestor(factory),
            budget, new XAuthorProfileResolver(configuration, clients, cache, budget, NullLogger<XAuthorProfileResolver>.Instance),
            health, NullLogger<SocialPollingWorker>.Instance, factory, clock);
        await worker.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (health.Get("YouTube") is null)
                await Task.Delay(10, timeout.Token);

            var source = Assert.Single(SourceSetupStatus.Build(configuration, health, youtubeCollectionRunning: true), item => item.Provider == "YouTube");
            Assert.Equal(quotaExhausted ? "budget-exhausted" : "error", source.State);
            Assert.False(source.Ready);
            Assert.Null(source.LastSuccessfulUtc);
            Assert.Contains(operation, source.Summary);
            Assert.Contains(expected, source.Summary);
            Assert.DoesNotContain("private-response-value", source.Summary);
            if (quotaExhausted)
            {
                Assert.Contains("paused until the next midnight Pacific", source.Summary);
                var reset = YouTubePollingSchedule.NextQuotaResetUtc(clock.UtcNow.UtcDateTime);
                await using (var database = factory.CreateDbContext())
                {
                    (await database.Events.SingleAsync()).YouTubeCollectionRunning = false;
                    await database.SaveChangesAsync();
                }
                clock.UtcNow = clock.UtcNow.AddMinutes(2);
                await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token);
                await using (var database = factory.CreateDbContext())
                {
                    (await database.Events.SingleAsync()).YouTubeCollectionRunning = true;
                    await database.SaveChangesAsync();
                }
                clock.UtcNow = clock.UtcNow.AddMinutes(2);
                await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token);
                Assert.Equal("budget-exhausted", health.Get("YouTube")!.State);
                Assert.Equal(2, handler.Requests.Count);

                clock.UtcNow = new DateTimeOffset(reset);
                while (handler.Requests.Count < 3)
                    await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(quotaExhausted ? 3 : failTokenRefresh ? 1 : 2, handler.Requests.Count);
        }
        finally
        {
            await worker.StopAsync(default);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _utcTicks = new DateTime(2026, 11, 4, 17, 0, 0, DateTimeKind.Utc).Ticks;
        public DateTimeOffset UtcNow
        {
            get => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
        }
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FixtureHandler(
        bool failTokenRefresh = false,
        bool failChatRequest = false,
        string chatErrorReason = "accessNotConfigured") : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var tokenRequest = request.RequestUri!.Host == "oauth2.googleapis.com";
            if (tokenRequest && failTokenRefresh || !tokenRequest && failChatRequest)
            {
                return Task.FromResult(new HttpResponseMessage(
                    tokenRequest ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(tokenRequest
                        ? """{"error":"invalid_grant","error_description":"private-response-value"}"""
                        : $$$"""{"error":{"message":"private-response-value","errors":[{"reason":"{{{chatErrorReason}}}"}]}}""",
                        Encoding.UTF8, "application/json")
                });
            }
            var json = request.RequestUri!.Host == "oauth2.googleapis.com"
                ? """{"access_token":"fixture-token","expires_in":3600}"""
                : """
                  {"pollingIntervalMillis":60000,"items":[{"id":"one","snippet":{"type":"textMessageEvent",
                    "textMessageDetails":{"messageText":"How can Aspire deploy to Kubernetes? #QuEsTiOn"},"publishedAt":"2026-10-26T23:00:00Z"},
                    "authorDetails":{"channelId":"viewer-id","displayName":"Viewer","channelUrl":"https://www.youtube.com/channel/viewer",
                      "profileImageUrl":"https://yt3.ggpht.com/avatar.jpg"}},
                    {"id":"untagged","snippet":{"type":"textMessageEvent","textMessageDetails":{"messageText":"Hello Community Event #communityevent"}},
                      "authorDetails":{"channelId":"other-id","displayName":"Other viewer"}},
                    {"id":"near-match","snippet":{"type":"textMessageEvent","textMessageDetails":{"messageText":"How can Aspire deploy? #questions"}},
                      "authorDetails":{"channelId":"other-id","displayName":"Other viewer"}},
                    {"id":"deletion","snippet":{"type":"messageDeletedEvent","messageDeletedEventDetails":{"deletedMessageId":"legacy"}}}]}
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class DatabaseFactory(SqliteConnection connection) : IDbContextFactory<EventWallDbContext>
    {
        public EventWallDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection).Options);
    }
}
