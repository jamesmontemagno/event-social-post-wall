using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventWall.Api.Tests;

public sealed class BlueskyJetstreamTests
{
    [Fact]
    public void Parses_post_commits_and_blob_images()
    {
        using var document = JsonDocument.Parse("""
            {
              "$type":"message",
              "payload":{
                "$type":"network.bsky.jetstream.subscribeEvents#commit",
                "seq":1234,
                "did":"did:plc:viewer",
                "operation":"create",
                "collection":"app.bsky.feed.post",
                "rkey":"post-one",
                "record":{
                  "text":"Hello #communityevent",
                  "createdAt":"2026-10-26T23:00:00Z",
                  "embed":{"$type":"app.bsky.embed.images","images":[
                    {"alt":"A stage","image":{"$type":"blob","ref":{"$link":"bafk-image"},"mimeType":"image/jpeg","size":1000}}]
                  }
                }
              }
            }
            """);

        Assert.True(BlueskyJetstreamEventParser.TryParse(document.RootElement, out var item));
        Assert.NotNull(item);
        Assert.Equal(1234, item.Sequence);
        Assert.Equal("did:plc:viewer", item.Did);
        Assert.Equal("create", item.Operation);
        Assert.Equal("Hello #communityevent", item.Text);
        Assert.Equal(DateTime.Parse("2026-10-26T23:00:00Z").ToUniversalTime(), item.CreatedAtUtc);
        var image = Assert.Single(item.Images);
        Assert.Equal("https://cdn.bsky.app/img/feed_fullsize/plain/did%3Aplc%3Aviewer/bafk-image@jpeg", image.Url);
        Assert.Equal("A stage", image.Alt);
    }

    [Fact]
    public void Parses_source_deletions_without_a_record()
    {
        using var document = JsonDocument.Parse("""
            {"$type":"message","payload":{"$type":"network.bsky.jetstream.subscribeEvents#commit",
             "seq":1235,"did":"did:plc:viewer","operation":"delete","collection":"app.bsky.feed.post","rkey":"post-one"}}
            """);

        Assert.True(BlueskyJetstreamEventParser.TryParse(document.RootElement, out var item));
        Assert.NotNull(item);
        Assert.Equal("delete", item.Operation);
        Assert.Null(item.Text);
        Assert.Empty(item.Images);
    }

    [Fact]
    public void Ignores_non_commit_events_and_other_collections()
    {
        using var identity = JsonDocument.Parse("""
            {"$type":"message","payload":{"$type":"network.bsky.jetstream.subscribeEvents#identity","seq":10,"did":"did:plc:viewer"}}
            """);
        using var like = JsonDocument.Parse("""
            {"$type":"message","payload":{"$type":"network.bsky.jetstream.subscribeEvents#commit",
             "seq":11,"did":"did:plc:viewer","operation":"create","collection":"app.bsky.feed.like","rkey":"like-one","record":{}}}
            """);

        Assert.False(BlueskyJetstreamEventParser.TryParse(identity.RootElement, out _));
        Assert.False(BlueskyJetstreamEventParser.TryParse(like.RootElement, out _));
    }

    [Fact]
    public async Task Profile_lookup_failure_falls_back_to_the_service_identity()
    {
        using var handler = new ProfileHandler(HttpStatusCode.Forbidden);
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var profiles = new BlueskyProfileResolver(
            new FixtureHttpClientFactory(client),
            cache,
            NullLogger<BlueskyProfileResolver>.Instance);

        var profile = await profiles.GetAsync("did:plc:viewer", CancellationToken.None);

        Assert.Equal("did:plc:viewer", profile.Handle);
        Assert.Null(profile.DisplayName);
        Assert.Null(profile.AvatarUrl);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Worker_filters_ingests_and_resumes_from_a_persisted_cursor()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        IDbContextFactory<EventWallDbContext> factory = new DatabaseFactory(connection);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            await EventSeeder.SeedAsync(database, new ConfigurationBuilder().Build());
        }

        var events = new[]
        {
            new BlueskyStreamEvent(101, "did:plc:other", "app.bsky.feed.post", "unrelated", "create",
                "A post without the event term", DateTime.UtcNow, []),
            new BlueskyStreamEvent(102, "did:plc:viewer", "app.bsky.feed.post", "post-one", "create",
                "Hello #communityevent", DateTime.UtcNow, [new("https://cdn.bsky.app/image.jpg", "A stage")])
        };
        var client = new FixtureJetstreamClient(events);
        using var profileHandler = new ProfileHandler();
        using var httpClient = new HttpClient(profileHandler);
        var clients = new FixtureHttpClientFactory(httpClient);
        var health = new SourceHealthRegistry();
        var cursorStore = new BlueskyCursorStore(factory);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var profiles = new BlueskyProfileResolver(clients, cache, NullLogger<BlueskyProfileResolver>.Instance);
        var worker = new BlueskyJetstreamWorker(
            BlueskyConfiguration(),
            client,
            cursorStore,
            profiles,
            new EventPostIngestor(factory),
            health,
            NullLogger<BlueskyJetstreamWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.Reconnected.Task.WaitAsync(timeout.Token);
        await worker.StopAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var post = await verify.Posts.SingleAsync();
        Assert.Equal("Hello #communityevent", post.Text);
        Assert.Equal("@viewer.bsky.social", post.AuthorName);
        Assert.Equal("did:plc:viewer", post.AuthorExternalId);
        Assert.Equal("https://bsky.app/profile/viewer.bsky.social", post.AuthorProfileUrl);
        Assert.Equal("https://cdn.bsky.app/avatar.jpg", post.AuthorAvatarUrl);
        Assert.Equal("https://bsky.app/profile/viewer.bsky.social/post/post-one", post.SourceUrl);
        Assert.Equal("A stage", Assert.Single(post.Images).Alt);
        Assert.Equal(102, await cursorStore.GetAsync(CancellationToken.None));
        Assert.Contains((long?)102, client.Cursors);
        Assert.Equal(1, profileHandler.RequestCount);
        Assert.Equal("connected", health.Get("Bluesky")?.State);
    }

    [Fact]
    public async Task Cursor_store_keeps_a_checkpoint_advanced_by_another_collector()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        IDbContextFactory<EventWallDbContext> factory = new DatabaseFactory(connection);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
        }

        var store = new BlueskyCursorStore(factory);
        Assert.Equal(300, await store.SaveAsync(300, CancellationToken.None));
        Assert.Equal(300, await store.SaveAsync(200, CancellationToken.None));
        Assert.Equal(300, await store.GetAsync(CancellationToken.None));

        Assert.Equal(400, await store.SaveAsync(400, CancellationToken.None));
        Assert.Equal(400, await store.GetAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task Worker_resumes_when_another_collector_advances_the_checkpoint(int eventCount)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        IDbContextFactory<EventWallDbContext> factory = new DatabaseFactory(connection);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
        }

        var cursorStore = new BlueskyCursorStore(factory);
        await cursorStore.SaveAsync(100, CancellationToken.None);
        var events = Enumerable.Range(101, eventCount)
            .Select(sequence => new BlueskyStreamEvent(sequence, "did:plc:viewer", "app.bsky.feed.post",
                $"post-{sequence}", "ignored", null, null, []))
            .ToArray();
        var client = new FixtureJetstreamClient(events, async cancellationToken =>
        {
            await cursorStore.SaveAsync(300, cancellationToken);
        });
        using var profileHandler = new ProfileHandler();
        using var httpClient = new HttpClient(profileHandler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var worker = new BlueskyJetstreamWorker(
            BlueskyConfiguration(),
            client,
            cursorStore,
            new BlueskyProfileResolver(new FixtureHttpClientFactory(httpClient), cache, NullLogger<BlueskyProfileResolver>.Instance),
            new EventPostIngestor(factory),
            new SourceHealthRegistry(),
            NullLogger<BlueskyJetstreamWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(client.Reconnected.Task, worker.ExecuteTask!, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(client.Reconnected.Task, completed);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(new long?[] { 100, 300 }, client.Cursors);
        Assert.Equal(300, await cursorStore.GetAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("delete", null)]
    [InlineData("update", "The post no longer mentions the event.")]
    public async Task Worker_withdraws_deleted_or_no_longer_matching_posts(string operation, string? text)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        IDbContextFactory<EventWallDbContext> factory = new DatabaseFactory(connection);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            await EventSeeder.SeedAsync(database, new ConfigurationBuilder().Build());
            database.Posts.Add(new EventPost
            {
                Id = Guid.NewGuid(),
                EventSlug = "communityevent",
                Source = PostSource.Bluesky,
                ExternalId = "at://did:plc:viewer/app.bsky.feed.post/post-one",
                Text = "Welcome to #communityevent",
                AuthorName = "Viewer",
                AuthorExternalId = "did:plc:viewer",
                Status = PostStatus.Live,
                PublishedRevision = 1,
                ReceivedAtUtc = DateTime.UtcNow
            });
            (await database.Events.SingleAsync()).Revision = 1;
            await database.SaveChangesAsync();
        }

        var client = new FixtureJetstreamClient([
            new BlueskyStreamEvent(201, "did:plc:viewer", "app.bsky.feed.post", "post-one", operation, text, null, [])
        ]);
        using var profileHandler = new ProfileHandler();
        using var httpClient = new HttpClient(profileHandler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var worker = new BlueskyJetstreamWorker(
            BlueskyConfiguration(),
            client,
            new BlueskyCursorStore(factory),
            new BlueskyProfileResolver(new FixtureHttpClientFactory(httpClient), cache, NullLogger<BlueskyProfileResolver>.Instance),
            new EventPostIngestor(factory),
            new SourceHealthRegistry(),
            NullLogger<BlueskyJetstreamWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.Reconnected.Task.WaitAsync(timeout.Token);
        await worker.StopAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var post = await verify.Posts.SingleAsync();
        Assert.Equal(PostStatus.Withdrawn, post.Status);
        Assert.Equal(2, (await verify.Events.SingleAsync()).Revision);
        Assert.Equal(PostStatus.Withdrawn, Assert.Single(await verify.ModerationActions.ToListAsync()).ToStatus);
        Assert.Equal(0, profileHandler.RequestCount);
    }

    private static IConfiguration BlueskyConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Social:Bluesky:Enabled"] = "true",
            ["Social:Bluesky:PolicyApproved"] = "true"
        }).Build();

    private sealed class DatabaseFactory(SqliteConnection connection) : IDbContextFactory<EventWallDbContext>
    {
        public EventWallDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection).Options);
    }

    private sealed class FixtureJetstreamClient(
        IReadOnlyList<BlueskyStreamEvent> events,
        Func<CancellationToken, Task>? onFirstConnect = null) : IBlueskyJetstreamClient
    {
        private int _readCount;
        public List<long?> Cursors { get; } = [];
        public TaskCompletionSource Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ConnectAsync(long? cursor, CancellationToken cancellationToken)
        {
            Cursors.Add(cursor);
            if (Cursors.Count == 1 && onFirstConnect is not null)
            {
                await onFirstConnect(cancellationToken);
            }
        }

        public async IAsyncEnumerable<BlueskyStreamEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                foreach (var item in events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                }

                yield break;
            }

            Reconnected.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProfileHandler(HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Contains("app.bsky.actor.getProfile", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    """{"handle":"viewer.bsky.social","displayName":"","avatar":"https://cdn.bsky.app/avatar.jpg"}""",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class FixtureHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
