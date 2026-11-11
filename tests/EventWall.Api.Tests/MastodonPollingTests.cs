using System.Net;
using System.Text;
using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Security;
using EventWall.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventWall.Api.Tests;

public sealed class MastodonPollingTests
{
    private static readonly Uri Instance = new("https://mastodon.social/");

    [Theory]
    [InlineData("""{"reblog":{"id":"1"}}""", MastodonSkipReason.Reblog)]
    [InlineData("""{"in_reply_to_id":"1"}""", MastodonSkipReason.Reply)]
    [InlineData("""{"quote":{"id":"1"}}""", MastodonSkipReason.Quote)]
    [InlineData("""{"visibility":"unlisted"}""", MastodonSkipReason.NotPublic)]
    [InlineData("""{"account":{"id":"1","acct":"a","noindex":true}}""", MastodonSkipReason.None)]
    [InlineData("""{"account":{"id":"1","acct":"a","indexable":false}}""", MastodonSkipReason.None)]
    [InlineData("""{"account":{"id":"1","acct":"a","noindex":false,"indexable":false}}""", MastodonSkipReason.None)]
    [InlineData("""{"account":{"id":"1","acct":"a","noindex":true,"indexable":false,"note":"<p>#nobot</p>"}}""", MastodonSkipReason.AuthorOptedOut)]
    [InlineData("""{"account":{"id":"1","acct":"a","note":"<p>Please no <a>#NoBot</a></p>"}}""", MastodonSkipReason.AuthorOptedOut)]
    [InlineData("""{"account":{"id":"1","acct":"a","bot":true}}""", MastodonSkipReason.Bot)]
    [InlineData("""{"spoiler_text":"Spoilers"}""", MastodonSkipReason.ContentWarning)]
    [InlineData("""{"sensitive":true}""", MastodonSkipReason.ContentWarning)]
    [InlineData("{}", MastodonSkipReason.None)]
    public void Skip_rules_exclude_unsuitable_statuses(string overrides, MastodonSkipReason expected)
    {
        using var document = JsonDocument.Parse(Status("1", overrides: overrides));

        Assert.Equal(expected, MastodonStatus.SkipReason(document.RootElement));
    }

    [Fact]
    public void Reads_public_status_text_identity_links_and_images()
    {
        using var document = JsonDocument.Parse(Status("115", "<p>Hello &amp; welcome<br>to <a href=\"https://x\">#communityevent</a></p><p>Second</p>",
            media: """
                [{"type":"image","url":"https://files.mastodon.social/1.png","description":"Stage photo"},
                 {"type":"video","url":"https://files.mastodon.social/v.mp4"},
                 {"type":"image","url":"https://files.mastodon.social/2.png"},
                 {"type":"image","url":"https://files.mastodon.social/3.png"},
                 {"type":"image","url":"https://files.mastodon.social/4.png"},
                 {"type":"image","url":"https://files.mastodon.social/5.png"}]
                """));

        var post = MastodonStatus.Read(document.RootElement, Instance);

        Assert.NotNull(post);
        Assert.Equal("https://remote.example/statuses/115", post.ExternalId);
        Assert.Equal("Hello & welcome\nto #communityevent\n\nSecond", post.Text);
        Assert.Equal("Ada", post.AuthorName);
        Assert.Equal("https://mastodon.social/@ada@remote.example", post.ProfileUrl);
        Assert.Equal("https://mastodon.social/@ada@remote.example/115", post.PostUrl);
        Assert.Equal(4, post.Images.Count);
        Assert.Equal(new PostImage("https://files.mastodon.social/1.png", "Stage photo"), post.Images[0]);
        Assert.DoesNotContain(post.Images, image => image.Url.EndsWith(".mp4", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://mastodon.social", true)]
    [InlineData("https://mastodon.social/", true)]
    [InlineData("http://mastodon.social", false)]
    [InlineData("https://mastodon.social:8443", false)]
    [InlineData("https://mastodon.social/api", false)]
    [InlineData("https://user@mastodon.social", false)]
    [InlineData("https://127.0.0.1", false)]
    public void Setup_status_requires_a_plain_https_instance(string instance, bool configured)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Social:Mastodon:InstanceUrl"] = instance
        }).Build();

        var source = SourceSetupStatus.Build(configuration).Single(item => item.Provider == "Mastodon");

        Assert.Equal(configured, source.Configured);
    }

    [Fact]
    public async Task Ingestor_requires_event_terms_and_keeps_only_configured_mastodon_hosts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = await CreateDatabaseAsync(connection);
        var ingestor = new EventPostIngestor(factory, Configuration());

        Assert.False(await ingestor.IngestAsync(PostSource.Mastodon, "plain", "Just a post", "Ada", "1", null, null, default));
        Assert.True(await ingestor.IngestAsync(PostSource.Mastodon, "tagged", "Hello Community Event", "Ada", "1",
            "https://mastodon.social/@ada/1", null, default,
            "https://mastodon.social/@ada", "https://files.mastodon.social/avatar.png",
            [new("https://files.mastodon.social/1.png", "Kept"), new("https://evil.example/2.png", "Dropped")]));

        await using var database = factory.CreateDbContext();
        var post = await database.Posts.SingleAsync();
        Assert.Equal(PostStatus.Pending, post.Status);
        Assert.Equal("https://mastodon.social/@ada", post.AuthorProfileUrl);
        Assert.Equal("https://files.mastodon.social/avatar.png", post.AuthorAvatarUrl);
        Assert.Contains("files.mastodon.social/1.png", post.ImagesJson);
        Assert.DoesNotContain("evil.example", post.ImagesJson);
        Assert.Null(SocialProfile.SafeProfileUrl(PostSource.Mastodon, "https://files.mastodon.social/@ada", ["mastodon.social", "files.mastodon.social"]));
    }

    [Fact]
    public async Task Collector_starts_at_newest_page_then_requests_newer_statuses_with_token()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = await CreateDatabaseAsync(connection);
        using var handler = new TimelineHandler(
            $"[{Status("103", "No event term")},{Status("102", overrides: """{"reblog":{"id":"9"}}""")},{Status("101", overrides: """{"account":{"id":"42","acct":"ada","indexable":false,"noindex":true}}""")}]",
            "[]");
        var (worker, health, clock) = CreateWorker(factory, handler, Configuration(("Social:Mastodon:AccessToken", "fixture-token")));
        var firstDetail = "";
        using (worker)
        {
            await worker.StartAsync(default);
            try
            {
                await WaitForAsync(() => handler.Requests.Count >= 1 && health.Get("Mastodon")?.State == "connected-partial");
                firstDetail = health.Get("Mastodon")!.Detail;
                clock.UtcNow = clock.UtcNow.AddSeconds(180);
                await WaitForAsync(() => handler.Requests.Count >= 2, TimeSpan.FromSeconds(15));
            }
            finally
            {
                await worker.StopAsync(default);
            }
        }

        Assert.Equal("https://mastodon.social/api/v1/timelines/tag/communityevent?limit=40", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Contains("min_id=103", handler.Requests[1].Uri.Query);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer fixture-token", request.Authorization));
        Assert.Contains("1 new matching posts found; 1 posts skipped", firstDetail);
        await using var database = factory.CreateDbContext();
        var post = await database.Posts.SingleAsync();
        Assert.Equal(PostSource.Mastodon, post.Source);
        Assert.Equal(PostStatus.Pending, post.Status);
        Assert.Equal(103, (await database.ConnectorCheckpoints.SingleAsync(item => item.Provider == "Mastodon")).Cursor);
    }

    [Fact]
    public async Task Collector_reports_authorization_requirement_without_sending_a_token_by_default()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = await CreateDatabaseAsync(connection);
        using var handler = new TimelineHandler(statusCode: HttpStatusCode.Unauthorized);
        var (worker, health, _) = CreateWorker(factory, handler, Configuration());
        using (worker)
        {
            await worker.StartAsync(default);
            try
            {
                await WaitForAsync(() => health.Get("Mastodon")?.State == "error");
            }
            finally
            {
                await worker.StopAsync(default);
            }
        }

        Assert.Null(Assert.Single(handler.Requests).Authorization);
        Assert.Contains("Social:Mastodon:AccessToken", health.Get("Mastodon")!.Detail);
        await using var database = factory.CreateDbContext();
        Assert.Empty(database.ConnectorCheckpoints);
    }

    private static string Status(string id, string content = "<p>Ready for #communityevent</p>", string media = "[]", string overrides = "{}")
    {
        var status = JsonNodeFrom($$$"""
            {"id":"{{{id}}}","uri":"https://remote.example/statuses/{{{id}}}","created_at":"2026-11-10T16:00:00Z",
             "visibility":"public","sensitive":false,"spoiler_text":"","in_reply_to_id":null,"reblog":null,
             "content":{{{JsonSerializer.Serialize(content)}}},"media_attachments":{{{media}}},
             "account":{"id":"42","acct":"ada@remote.example","display_name":"Ada","bot":false,"note":"",
               "avatar_static":"https://files.mastodon.social/avatar.png"}}
            """);
        foreach (var (key, value) in JsonNodeFrom(overrides))
            status[key] = value?.DeepClone();
        return status.ToJsonString();
    }

    private static System.Text.Json.Nodes.JsonObject JsonNodeFrom(string json) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

    private static IConfiguration Configuration(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Social:Mastodon:Enabled"] = "true",
            ["Social:Mastodon:PolicyApproved"] = "true",
            ["Social:Mastodon:InstanceUrl"] = "https://mastodon.social",
            ["Social:Mastodon:MediaHosts:0"] = "files.mastodon.social"
        };
        foreach (var (key, value) in extra) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<DatabaseFactory> CreateDatabaseAsync(SqliteConnection connection)
    {
        var factory = new DatabaseFactory(connection);
        await using var database = factory.CreateDbContext();
        await database.Database.EnsureCreatedAsync();
        await EventSeeder.SeedAsync(database, new ConfigurationBuilder().Build());
        return factory;
    }

    private static (SocialPollingWorker Worker, SourceHealthRegistry Health, TestClock Clock) CreateWorker(
        DatabaseFactory factory, TimelineHandler handler, IConfiguration configuration)
    {
        var client = new HttpClient(handler);
        var clients = new ClientFactory(client);
        var tokens = new ProviderTokenService(configuration, clients, factory, new EphemeralDataProtectionProvider());
        var health = new SourceHealthRegistry();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var budget = new XReadBudget(factory);
        var clock = new TestClock();
        var worker = new SocialPollingWorker(configuration, clients, tokens, new EventPostIngestor(factory, configuration),
            budget, new XAuthorProfileResolver(configuration, clients, cache, budget, NullLogger<XAuthorProfileResolver>.Instance),
            health, NullLogger<SocialPollingWorker>.Instance, factory, clock);
        return (worker, health, clock);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, cancellation.Token);
    }

    private sealed class TestClock : TimeProvider
    {
        private long _utcTicks = new DateTime(2026, 11, 10, 17, 0, 0, DateTimeKind.Utc).Ticks;
        public DateTimeOffset UtcNow
        {
            get => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
        }
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TimelineHandler(string[] pages, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public TimelineHandler(params string[] pages) : this(pages, HttpStatusCode.OK) { }
        public TimelineHandler(HttpStatusCode statusCode) : this([], statusCode) { }

        private readonly List<(Uri Uri, string? Authorization)> _requests = [];
        public IReadOnlyList<(Uri Uri, string? Authorization)> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int index;
            lock (_requests)
            {
                index = _requests.Count;
                _requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            }
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(pages.Length == 0 ? "[]" : pages[Math.Min(index, pages.Length - 1)],
                    Encoding.UTF8, "application/json")
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
