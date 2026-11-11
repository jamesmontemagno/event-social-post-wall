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
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventWall.Api.Tests;

public sealed class XProfilesTests
{
    [Theory]
    [InlineData("retweeted")]
    [InlineData("replied_to")]
    [InlineData("quoted")]
    [InlineData("future_reference")]
    public async Task Search_groups_both_event_terms_and_imports_only_standalone_originals(string referenceType)
    {
        var search = JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new { id = "101", text = "An original question #communityevent", author_id = "123" },
                new
                {
                    id = "102", text = "A non-original Community Event post #communityevent", author_id = "456",
                    referenced_tweets = new[] { new { type = referenceType, id = "900" } }
                },
                new
                {
                    id = "103", text = "RT is an acronym in my original #communityevent question", author_id = "123",
                    referenced_tweets = Array.Empty<object>()
                }
            }
        });
        await using var fixture = await Fixture.CreateAsync(request =>
            Response(request.RequestUri!.AbsolutePath.EndsWith("/recent") ? search : Profile));

        await fixture.PollAsync();

        var searchRequest = Assert.Single(fixture.Handler.Requests, uri => uri.AbsolutePath == "/2/tweets/search/recent");
        var query = Uri.UnescapeDataString(searchRequest.Query).TrimStart('?').Split('&')
            .Single(part => part.StartsWith("query=", StringComparison.Ordinal));
        Assert.Equal("query=(#communityevent OR \"Community Event\") -is:retweet -is:reply -is:quote", query);
        Assert.Contains("referenced_tweets", searchRequest.Query);
        var profileRequest = Assert.Single(fixture.Handler.Requests, uri => uri.AbsolutePath == "/2/users");
        Assert.Equal("ids=123", profileRequest.Query.TrimStart('?').Split('&')
            .Single(part => part.StartsWith("ids=", StringComparison.Ordinal)));
        await using var database = fixture.CreateDbContext();
        Assert.Equal(["101", "103"], await database.Posts.OrderBy(post => post.ExternalId)
            .Select(post => post.ExternalId).ToArrayAsync());
        var usage = await database.ConnectorDailyUsage.SingleAsync();
        Assert.Equal(3, usage.ReservedPostReads);
        Assert.Equal(1, usage.ReservedUserReads);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"invalid\"")]
    public async Task Malformed_post_references_report_a_source_error_without_importing_the_post(string references)
    {
        var search = """{"data":[{"id":"101","text":"An original question #communityevent","author_id":"123","referenced_tweets":"""
            + references + "}]}";
        await using var fixture = await Fixture.CreateAsync(_ => Response(search));

        await fixture.PollAsync(expectSourceError: true);

        Assert.Single(fixture.Handler.Requests);
        await using var database = fixture.CreateDbContext();
        Assert.Empty(await database.Posts.ToListAsync());
        Assert.Equal(1, (await database.ConnectorDailyUsage.SingleAsync()).ReservedPostReads);
    }

    [Theory]
    [InlineData(400, 0)]
    [InlineData(401, 0)]
    [InlineData(403, 0)]
    [InlineData(429, 0)]
    [InlineData(408, 100)]
    [InlineData(500, 100)]
    public async Task Definitive_client_errors_refund_search_reservations_but_uncertain_failures_keep_them(
        int status, int expectedReservedReads)
    {
        await using var fixture = await Fixture.CreateAsync(_ => Response("{}", (HttpStatusCode)status));

        await fixture.PollAsync(expectSourceError: true);

        await using var database = fixture.CreateDbContext();
        Assert.Equal(expectedReservedReads,
            (await database.ConnectorDailyUsage.SingleAsync()).ReservedPostReads);
    }

    [Fact]
    public async Task Profile_cache_expires_at_24_hours_and_coalesces_concurrent_lookups()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await Task.WhenAll(
            fixture.Profiles.GetAsync(["123", "123"], default),
            fixture.Profiles.GetAsync(["123"], default));
        Assert.All(first, result => Assert.Equal("Fixture Viewer", result["123"].DisplayName));
        Assert.Single(fixture.Handler.Requests);
        fixture.Clock.UtcNow += TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1);
        Assert.Single(await fixture.Profiles.GetAsync(["123"], default));
        Assert.Single(fixture.Handler.Requests);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(1);
        Assert.Single(await fixture.Profiles.GetAsync(["123"], default));
        Assert.Equal(2, fixture.Handler.Requests.Count);
        await using var database = fixture.CreateDbContext();
        Assert.Equal(2, (await database.ConnectorDailyUsage.SingleAsync()).ReservedUserReads);
        Assert.All(fixture.Handler.Requests, uri =>
        {
            Assert.Equal("/2/users", uri.AbsolutePath);
            Assert.Contains("profile_image_url", uri.Query);
        });
    }

    [Theory]
    [InlineData(503, "{}")]
    [InlineData(200, "[]")]
    [InlineData(200, "{bad json")]
    public async Task Failed_profiles_are_negative_cached_for_five_minutes_and_keep_the_reservation(int status, string body)
    {
        await using var fixture = await Fixture.CreateAsync(_ => Response(body, (HttpStatusCode)status));
        Assert.Empty(await fixture.Profiles.GetAsync(["123"], default));
        Assert.Empty(await fixture.Profiles.GetAsync(["123"], default));
        Assert.Single(fixture.Handler.Requests);
        fixture.Clock.UtcNow += TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1);
        Assert.Empty(await fixture.Profiles.GetAsync(["123"], default));
        Assert.Single(fixture.Handler.Requests);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(1);
        Assert.Empty(await fixture.Profiles.GetAsync(["123"], default));
        Assert.Equal(2, fixture.Handler.Requests.Count);
        await using var database = fixture.CreateDbContext();
        Assert.Equal(2, (await database.ConnectorDailyUsage.SingleAsync()).ReservedUserReads);
    }

    [Theory]
    [InlineData("Social:X:Enabled")]
    [InlineData("Social:X:PolicyApproved")]
    [InlineData("Social:X:SpendApproved")]
    public async Task Disabled_or_unapproved_profiles_never_call_X(string key)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Configuration[key] = "false";
        Assert.Empty(await fixture.Profiles.GetAsync(["123"], default));
        Assert.Empty(fixture.Handler.Requests);
        await using var database = fixture.CreateDbContext();
        Assert.Empty(await database.ConnectorDailyUsage.ToListAsync());
    }

    [Fact]
    public async Task User_reads_and_searches_share_the_exact_existing_dollar_ceiling()
    {
        await using var fixture = await Fixture.CreateAsync();
        var search = await fixture.Budget.TryReserveSearchAsync(.53m, default);
        Assert.NotNull(search);
        var users = await fixture.Budget.TryReserveUserReadsAsync(5, .53m, default);
        Assert.NotNull(users);
        Assert.Equal(3, users.ReservedUserReads);
        await fixture.Budget.ReconcileUserReadsAsync(users, 2, default);
        Assert.NotNull(await fixture.Budget.TryReserveUserReadsAsync(1, .53m, default));
        Assert.Null(await fixture.Budget.TryReserveUserReadsAsync(1, .53m, default));
        await fixture.Budget.ReconcileSearchAsync(search, 1, default);
        Assert.Null(await fixture.Budget.TryReserveSearchAsync(.53m, default));
        var remainder = await fixture.Budget.TryReserveUserReadsAsync(100, .53m, default);
        Assert.NotNull(remainder);
        Assert.Equal(49, remainder.ReservedUserReads);
        Assert.Null(await fixture.Budget.TryReserveUserReadsAsync(1, .53m, default));
        await using var database = fixture.CreateDbContext();
        var usage = await database.ConnectorDailyUsage.SingleAsync();
        Assert.Equal(1, usage.ReservedPostReads);
        Assert.Equal(52, usage.ReservedUserReads);
        Assert.Equal(.525m, usage.ReservedPostReads * .005m + usage.ReservedUserReads * .010m);
    }

    [Fact]
    public async Task Profile_batches_shrink_to_remaining_budget_and_do_not_spend_below_one_user_read()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Configuration["Social:X:MaximumDailySpendUsd"] = ".02";
        var profiles = await fixture.Profiles.GetAsync(["123", "456", "789"], default);
        Assert.Single(profiles);
        Assert.Single(fixture.Handler.Requests);
        Assert.Contains("ids=123,456", fixture.Handler.Requests[0].OriginalString);
        fixture.Configuration["Social:X:MaximumDailySpendUsd"] = ".009";
        Assert.Empty(await fixture.Profiles.GetAsync(["789"], default));
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Unavailable_users_refund_known_empty_reads_and_untrusted_profiles_do_not_poison_the_cache()
    {
        await using var fixture = await Fixture.CreateAsync(_ => Response("""{"errors":[{"title":"Not Found Error"}]}"""));
        Assert.Empty(await fixture.Profiles.GetAsync(["123"], default));
        await using (var database = fixture.CreateDbContext())
            Assert.Equal(0, (await database.ConnectorDailyUsage.SingleAsync()).ReservedUserReads);

        await using var unexpected = await Fixture.CreateAsync(_ => Response(
            """{"data":[{"id":"999","username":"other","name":"Other","profile_image_url":"https://attacker.example/avatar"}]}"""));
        Assert.Empty(await unexpected.Profiles.GetAsync(["123"], default));
        Assert.False(unexpected.Cache.TryGetValue("x-author:999", out _));
        var resolved = await unexpected.Profiles.GetAsync(["999"], default);
        Assert.Null(resolved["999"].AvatarUrl);
        Assert.Equal("https://x.com/other", resolved["999"].ProfileUrl);
        Assert.Equal(2, unexpected.Handler.Requests.Count);
    }

    [Fact]
    public async Task Worker_persists_profiles_for_both_sites_and_cached_repeat_polls_preserve_approval()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PollAsync();
        await using (var database = fixture.CreateDbContext())
        {
            var post = await database.Posts.SingleAsync();
            Assert.Equal("Fixture Viewer", post.AuthorName);
            Assert.Equal("https://x.com/fixture_viewer", PublicPostDto.From(post).AuthorProfileUrl);
            Assert.Equal("https://pbs.twimg.com/profile_images/fixture.jpg", StaffPostDto.From(post).AuthorAvatarUrl);
            Assert.Equal(PostStatus.Pending, post.Status);
            post.Status = PostStatus.Live;
            post.PublishedRevision = 7;
            (await database.Events.SingleAsync()).Revision = 7;
            await database.SaveChangesAsync();
        }
        await fixture.PollAsync();
        Assert.Equal(2, fixture.Handler.Requests.Count(uri => uri.AbsolutePath == "/2/tweets/search/recent"));
        Assert.Single(fixture.Handler.Requests, uri => uri.AbsolutePath == "/2/users");
        await using var verify = fixture.CreateDbContext();
        var updated = await verify.Posts.SingleAsync();
        Assert.Equal("Fixture Viewer", updated.AuthorName);
        Assert.Equal("https://x.com/fixture_viewer", updated.AuthorProfileUrl);
        Assert.Equal("https://pbs.twimg.com/profile_images/fixture.jpg", updated.AuthorAvatarUrl);
        Assert.Equal(PostStatus.Live, updated.Status);
        Assert.Equal(7, updated.PublishedRevision);
        Assert.Equal(0, updated.Version);
        Assert.Equal(7, (await verify.Events.SingleAsync()).Revision);
        Assert.Empty(await verify.ModerationActions.ToListAsync());
        var usage = await verify.ConnectorDailyUsage.SingleAsync();
        Assert.Equal(2, usage.ReservedPostReads);
        Assert.Equal(1, usage.ReservedUserReads);
    }

    [Fact]
    public async Task Repeat_polls_request_only_newer_posts_so_unchanged_results_are_not_counted_again()
    {
        var recentId = ((DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds() - 1288834974657) << 22).ToString();
        var search = Search.Replace("\"101\"", $"\"{recentId}\"");
        await using var fixture = await Fixture.CreateAsync(request => request.RequestUri!.AbsolutePath.EndsWith("/recent")
            ? Response(request.RequestUri.Query.Contains("since_id=") ? """{"meta":{"result_count":0}}""" : search)
            : Response(Profile));

        await fixture.PollAsync();
        await fixture.PollAsync();

        var searches = fixture.Handler.Requests.Where(uri => uri.AbsolutePath == "/2/tweets/search/recent").ToArray();
        Assert.Equal(2, searches.Length);
        Assert.DoesNotContain("since_id=", searches[0].Query);
        Assert.Contains($"since_id={recentId}", searches[1].Query);
        await using var database = fixture.CreateDbContext();
        Assert.Equal(recentId, (await database.Posts.SingleAsync()).ExternalId);
        Assert.Equal(1, (await database.ConnectorDailyUsage.SingleAsync()).ReservedPostReads);
    }

    [Theory]
    [InlineData(null, 300)]
    [InlineData("not-a-number", 300)]
    [InlineData("120", 120)]
    [InlineData("10", 60)]
    [InlineData("86400", 3600)]
    public void X_poll_interval_defaults_to_five_minutes_and_is_clamped(string? configured, int expectedSeconds)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Social:X:PollIntervalSeconds"] = configured
        }).Build();
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SocialPollingWorker.XPollInterval(configuration));
    }

    [Fact]
    public async Task Cursors_older_than_the_recent_search_window_are_not_sent_as_since_id()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PollAsync();
        await fixture.PollAsync();

        Assert.All(fixture.Handler.Requests.Where(uri => uri.AbsolutePath == "/2/tweets/search/recent"),
            uri => Assert.DoesNotContain("since_id=", uri.Query));
        await using var database = fixture.CreateDbContext();
        Assert.Equal(101, (await database.ConnectorCheckpoints.SingleAsync(item => item.Provider == "X")).Cursor);
    }

    [Fact]
    public async Task Empty_searches_release_post_reservations_and_backfill_existing_approved_authors()
    {
        await using var fixture = await Fixture.CreateAsync(request => request.RequestUri!.AbsolutePath.EndsWith("/recent")
            ? Response("""{"meta":{"result_count":0}}""") : Response(Profile));
        await fixture.Ingestor.IngestAsync(PostSource.X, "existing", "A useful question #communityevent",
            "X user 123", "123", null, null, default);
        await using (var database = fixture.CreateDbContext())
        {
            var post = await database.Posts.SingleAsync();
            post.Status = PostStatus.Live;
            post.PublishedRevision = 3;
            await database.SaveChangesAsync();
        }
        await fixture.PollAsync();
        await using var verify = fixture.CreateDbContext();
        var updated = await verify.Posts.SingleAsync();
        Assert.Equal("Fixture Viewer", updated.AuthorName);
        Assert.Equal(PostStatus.Live, updated.Status);
        Assert.Equal(3, updated.PublishedRevision);
        Assert.Empty(await verify.ModerationActions.ToListAsync());
        var usage = await verify.ConnectorDailyUsage.SingleAsync();
        Assert.Equal(0, usage.ReservedPostReads);
        Assert.Equal(1, usage.ReservedUserReads);
    }

    [Theory]
    [InlineData(PostStatus.Live)]
    [InlineData(PostStatus.Rejected)]
    public async Task Changed_profiles_after_cache_expiry_preserve_existing_staff_decisions(PostStatus status)
    {
        var profileCalls = 0;
        await using var fixture = await Fixture.CreateAsync(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/recent")
                ? Response(Search)
                : Response(++profileCalls == 1 ? Profile : Profile
                    .Replace("Fixture Viewer", "Updated display name")
                    .Replace("fixture.jpg", "updated.jpg")));
        await fixture.PollAsync();
        await using (var database = fixture.CreateDbContext())
        {
            var post = await database.Posts.SingleAsync();
            post.Status = status;
            post.Version = 3;
            post.PublishedRevision = status == PostStatus.Live ? 7 : null;
            (await database.Events.SingleAsync()).Revision = 7;
            await database.SaveChangesAsync();
        }

        fixture.Clock.UtcNow += TimeSpan.FromHours(24);
        await fixture.PollAsync();

        await using var verify = fixture.CreateDbContext();
        var updated = await verify.Posts.SingleAsync();
        Assert.Equal("Updated display name", updated.AuthorName);
        Assert.Equal("https://pbs.twimg.com/profile_images/updated.jpg", updated.AuthorAvatarUrl);
        Assert.Equal(status, updated.Status);
        Assert.Equal(3, updated.Version);
        Assert.Equal(status == PostStatus.Live ? (long?)7 : null, updated.PublishedRevision);
        Assert.Equal(7, (await verify.Events.SingleAsync()).Revision);
        Assert.Empty(await verify.ModerationActions.ToListAsync());
        Assert.Equal(2, profileCalls);
    }

    [Fact]
    public async Task Changed_source_content_still_requires_review_after_profile_enrichment()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PollAsync();
        await using (var database = fixture.CreateDbContext())
        {
            var post = await database.Posts.SingleAsync();
            post.Status = PostStatus.Live;
            post.PublishedRevision = 1;
            (await database.Events.SingleAsync()).Revision = 1;
            await database.SaveChangesAsync();
        }
        await fixture.Ingestor.IngestAsync(PostSource.X, "101", "A changed question #communityevent",
            "X user 123", "123", null, null, default);
        await fixture.Ingestor.RefreshXAuthorProfilesAsync(
            [new XAuthorProfile("123", "fixture_viewer", "Updated display name", "https://pbs.twimg.com/new.jpg")], default);
        await using var verify = fixture.CreateDbContext();
        var updated = await verify.Posts.SingleAsync();
        Assert.Equal(PostStatus.Pending, updated.Status);
        Assert.Null(updated.PublishedRevision);
        Assert.Equal(1, updated.Version);
        Assert.Equal("Updated display name", updated.AuthorName);
        Assert.Single(await verify.ModerationActions.ToListAsync());
        Assert.Equal(2, (await verify.Events.SingleAsync()).Revision);
    }

    [Fact]
    public async Task Profiles_are_batched_at_100_users_and_display_names_are_bounded()
    {
        await using var fixture = await Fixture.CreateAsync(request =>
        {
            var ids = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Single(part => part.StartsWith("ids=", StringComparison.Ordinal))[4..].Split(',');
            return Response(JsonSerializer.Serialize(new
            {
                data = ids.Select(id => new { id, username = $"user{id}", name = new string('a', 200) })
            }));
        });
        var profiles = await fixture.Profiles.GetAsync(Enumerable.Range(1, 101).Select(id => id.ToString()), default);
        Assert.Equal(101, profiles.Count);
        Assert.All(profiles.Values, profile => Assert.Equal(120, profile.DisplayName.Length));
        Assert.Equal(2, fixture.Handler.Requests.Count);
        await using var database = fixture.CreateDbContext();
        Assert.Equal(101, (await database.ConnectorDailyUsage.SingleAsync()).ReservedUserReads);
    }

    [Fact]
    public async Task Provider_failure_stops_remaining_batches_without_spending_their_budget()
    {
        await using var fixture = await Fixture.CreateAsync(_ => Response("{}", HttpStatusCode.ServiceUnavailable));
        Assert.Empty(await fixture.Profiles.GetAsync(Enumerable.Range(1, 101).Select(id => id.ToString()), default));
        Assert.Single(fixture.Handler.Requests);
        await using var database = fixture.CreateDbContext();
        Assert.Equal(100, (await database.ConnectorDailyUsage.SingleAsync()).ReservedUserReads);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_keeps_the_uncertain_read_reservation()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await Fixture.CreateAsync(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Profiles.GetAsync(["123"], cancellation.Token));
        Assert.False(fixture.Cache.TryGetValue("x-author:123", out _));
        await using var database = fixture.CreateDbContext();
        Assert.Equal(1, (await database.ConnectorDailyUsage.SingleAsync()).ReservedUserReads);
    }

    private const string Profile = """
        {"data":[{"id":"123","username":"fixture_viewer","name":"Fixture Viewer",
          "profile_image_url":"https://pbs.twimg.com/profile_images/fixture.jpg"}]}
        """;
    private const string Search = """
        {"data":[{"id":"101","text":"A useful question #communityevent","author_id":"123",
          "created_at":"2026-11-02T18:00:00Z"}]}
        """;

    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Fixture : IDbContextFactory<EventWallDbContext>, IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly HttpClient client;
        private readonly ClientFactory clients;
        public IConfigurationRoot Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Social:X:Enabled"] = "true", ["Social:X:PolicyApproved"] = "true",
            ["Social:X:SpendApproved"] = "true", ["Social:X:MaximumDailySpendUsd"] = "5",
            ["Social:X:BearerToken"] = "fixture-token"
        }).Build();
        public TestClock Clock { get; } = new();
        public MemoryCache Cache { get; }
        public Handler Handler { get; }
        public XReadBudget Budget { get; }
        public XAuthorProfileResolver Profiles { get; }
        public EventPostIngestor Ingestor => new(this);

        private Fixture(Func<HttpRequestMessage, HttpResponseMessage>? respond)
        {
            Handler = new(respond ?? (request => Response(request.RequestUri!.AbsolutePath == "/2/users" ? Profile : Search)));
            client = new(Handler);
            clients = new(client);
            Cache = new(new MemoryCacheOptions { Clock = Clock });
            Budget = new(this);
            Profiles = new(Configuration, clients, Cache, Budget, NullLogger<XAuthorProfileResolver>.Instance);
        }
        public static async Task<Fixture> CreateAsync(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
        {
            var fixture = new Fixture(respond);
            await fixture.connection.OpenAsync();
            await using var database = fixture.CreateDbContext();
            await database.Database.EnsureCreatedAsync();
            await EventSeeder.SeedAsync(database, fixture.Configuration);
            return fixture;
        }
        public EventWallDbContext CreateDbContext() => new(new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection).Options);
        public async Task PollAsync(bool expectSourceError = false)
        {
            var health = new SourceHealthRegistry();
            using var worker = new SocialPollingWorker(Configuration, clients,
                new ProviderTokenService(Configuration, clients, this, new EphemeralDataProtectionProvider()),
                Ingestor, Budget, Profiles, health, NullLogger<SocialPollingWorker>.Instance, this);
            await worker.StartAsync(default);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (expectSourceError ? health.Get("X")?.State != "error" : health.Get("X")?.LastSuccessfulUtc is null)
                    await Task.Delay(10, timeout.Token);
            }
            finally
            {
                await worker.StopAsync(default);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Cache.Dispose();
            client.Dispose();
            await connection.DisposeAsync();
        }
    }
    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
            await Task.Delay(10, cancellationToken);
            return respond(request);
        }
    }
}
