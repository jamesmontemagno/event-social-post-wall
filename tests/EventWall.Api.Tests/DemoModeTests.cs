using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace EventWall.Api.Tests;

public sealed class DemoModeTests
{
    [Fact]
    public void Demo_mode_cannot_run_in_production()
    {
        var configuration = DemoConfiguration();

        Assert.Throws<InvalidOperationException>(() => DemoMode.Resolve(configuration, Environments.Production));
        Assert.True(DemoMode.Resolve(configuration, Environments.Development));
        Assert.True(DemoMode.Resolve(configuration, Environments.Staging));
        Assert.False(DemoMode.Resolve(new ConfigurationBuilder().Build(), Environments.Production));
    }

    [Fact]
    public void Hosted_demo_requires_explicit_production_opt_in()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DemoMode.EnabledKey] = "true",
            ["Demo:AllowInProduction"] = "true"
        }).Build();
        Assert.True(DemoMode.Resolve(configuration, Environments.Production));
    }

    [Fact]
    public void Demo_mode_reports_every_source_as_simulated_without_cost_notes()
    {
        var sources = SourceSetupStatus.Build(DemoConfiguration());

        Assert.Equal(new[] { "Twitch", "YouTube", "X", "Bluesky", "Mastodon" }, sources.Select(source => source.Provider));
        Assert.All(sources, source =>
        {
            Assert.True(source.Simulated);
            Assert.True(source.Ready);
            Assert.Equal("demo-simulated", source.State);
            Assert.Null(source.CostNote);
            Assert.Null(source.CollectionRunning);
            Assert.False(source.CollectionAvailable);
        });
    }

    [Fact]
    public void Simulated_x_and_bluesky_messages_satisfy_the_event_filter()
    {
        var social = DemoContent.Live.Concat(DemoContent.Pending).Concat(DemoContent.Rejected).Concat(DemoContent.Stream)
            .Where(message => message.Source is PostSource.X or PostSource.Bluesky);

        Assert.All(social, message => Assert.True(
            message.Text.Contains("#communityevent", StringComparison.OrdinalIgnoreCase) ||
            message.Text.Contains("Community Event", StringComparison.OrdinalIgnoreCase),
            message.Text));
    }

    [Fact]
    public void Simulated_twitch_and_youtube_messages_satisfy_the_question_tag_filter()
    {
        var chat = DemoContent.Live.Concat(DemoContent.Pending).Concat(DemoContent.Rejected).Concat(DemoContent.Stream)
            .Where(message => message.Source is PostSource.Twitch or PostSource.YouTube).ToArray();

        Assert.NotEmpty(chat);
        Assert.All(chat, message => Assert.True(EventPostIngestor.MatchesQuestionTag(message.Text), message.Text));
    }

    [Fact]
    public async Task Demo_seed_creates_live_pending_and_rejected_posts_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        IDbContextFactory<EventWallDbContext> factory = new InMemoryDatabaseFactory(connection);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            await EventSeeder.SeedAsync(database, new ConfigurationBuilder().Build());
        }

        Assert.True(await DemoContent.SeedAsync(factory, DateTime.UtcNow, CancellationToken.None));
        Assert.False(await DemoContent.SeedAsync(factory, DateTime.UtcNow, CancellationToken.None));

        await using var verify = await factory.CreateDbContextAsync();
        var posts = await verify.Posts.ToListAsync();
        var settings = await verify.Events.SingleAsync();
        var live = posts.Where(post => post.Status == PostStatus.Live).ToList();

        Assert.Equal(DemoContent.Live.Count, live.Count);
        Assert.Equal(DemoContent.Pending.Count, posts.Count(post => post.Status == PostStatus.Pending));
        Assert.Equal(DemoContent.Rejected.Count, posts.Count(post => post.Status == PostStatus.Rejected));
        Assert.All(live, post => Assert.InRange(post.PublishedRevision ?? 0, 1, settings.Revision));
        Assert.Equal(live.Count, live.Select(post => post.PublishedRevision).Distinct().Count());
        Assert.All(posts, post => Assert.Null(post.SourceUrl));
        Assert.Contains(live, post => post.Images.Count == 4);
        Assert.Contains(posts, post => post.Status == PostStatus.Pending && post.Images.Count > 0);
        Assert.All(posts.SelectMany(post => post.Images), image =>
        {
            Assert.StartsWith("/demo-images/", image.Url);
            Assert.Contains("Fictional demo", image.Alt);
        });
        Assert.Equal(DemoContent.Live.Count + DemoContent.Rejected.Count, await verify.ModerationActions.CountAsync());
        Assert.False(settings.SubmissionsOpen);
    }

    [Fact]
    public async Task Existing_demo_fixtures_gain_photos_without_resetting_moderation_or_intake()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        IDbContextFactory<EventWallDbContext> factory = new InMemoryDatabaseFactory(connection);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            await EventSeeder.SeedAsync(database, new ConfigurationBuilder().Build());
        }
        await DemoContent.SeedAsync(factory, DateTime.UtcNow, CancellationToken.None);
        await using (var database = await factory.CreateDbContextAsync())
        {
            var seeds = await database.Posts.ToListAsync();
            foreach (var post in seeds) post.ImagesJson = "[]";
            seeds.Single(post => post.AuthorName == "@carla.dev").Status = PostStatus.Rejected;
            (await database.Events.SingleAsync()).SubmissionsOpen = true;
            await database.SaveChangesAsync();
        }
        Assert.False(await DemoContent.SeedAsync(factory, DateTime.UtcNow, CancellationToken.None));
        await using var verify = await factory.CreateDbContextAsync();
        var posts = await verify.Posts.ToListAsync();
        Assert.Equal(DemoContent.Live.Count + DemoContent.Pending.Count + DemoContent.Rejected.Count, posts.Count);
        Assert.Equal(4, posts.Single(post => post.AuthorName == "@devjules").Images.Count);
        var rejected = posts.Single(post => post.AuthorName == "@carla.dev");
        Assert.Equal(PostStatus.Rejected, rejected.Status);
        Assert.Single(rejected.Images);
        Assert.True((await verify.Events.SingleAsync()).SubmissionsOpen);
        var revision = (await verify.Events.SingleAsync()).Revision;
        var versions = posts.ToDictionary(post => post.Id, post => post.Version);
        await DemoContent.SeedAsync(factory, DateTime.UtcNow, CancellationToken.None);
        await using var final = await factory.CreateDbContextAsync();
        Assert.Equal(revision, (await final.Events.SingleAsync()).Revision);
        Assert.All(await final.Posts.ToListAsync(), post => Assert.Equal(versions[post.Id], post.Version));
    }

    [Fact]
    public async Task Demo_reset_reseeds_fixtures_and_preserves_event_settings_and_budgets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        IDbContextFactory<EventWallDbContext> factory = new InMemoryDatabaseFactory(connection);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            await EventSeeder.SeedAsync(database, new ConfigurationBuilder().Build());
        }
        await DemoContent.SeedAsync(factory, DateTime.UtcNow, CancellationToken.None);

        Guid extraPostId;
        long previousRevision;
        await using (var database = await factory.CreateDbContextAsync())
        {
            var settings = await database.Events.SingleAsync();
            settings.SubmissionsOpen = true;
            previousRevision = settings.Revision;
            extraPostId = Guid.NewGuid();
            database.Posts.Add(new EventPost
            {
                Id = extraPostId,
                EventSlug = settings.Slug,
                ExternalId = "portal-before-reset",
                Source = PostSource.Portal,
                Text = "A submitted question",
                AuthorName = "Attendee",
                Status = PostStatus.Pending,
                ReceivedAtUtc = DateTime.UtcNow
            });
            database.ModerationActions.Add(new ModerationAction
            {
                Id = Guid.NewGuid(),
                PostId = extraPostId,
                FromStatus = PostStatus.Pending,
                ToStatus = PostStatus.Rejected,
                CreatedAtUtc = DateTime.UtcNow,
                StaffSessionId = "test-staff"
            });
            database.BlockedAuthors.Add(new BlockedAuthor
            {
                EventSlug = settings.Slug,
                Source = PostSource.X,
                AuthorExternalId = "blocked-demo-author",
                CreatedAtUtc = DateTime.UtcNow,
                StaffSessionId = "test-staff"
            });
            database.ConnectorDailyUsage.Add(new ConnectorDailyUsage
            {
                Id = "X:2026-10-26",
                ReservedPostReads = 25
            });
            database.ProviderCredentials.Add(new ProviderCredential
            {
                Provider = "Twitch",
                ProtectedRefreshToken = "test-protected-token"
            });
            await database.SaveChangesAsync();
        }

        DemoResetResult result;
        await using (var database = await factory.CreateDbContextAsync())
        {
            result = await DemoDatabaseReset.ResetAsync(database, DateTime.UtcNow, CancellationToken.None);
        }

        await using var verify = await factory.CreateDbContextAsync();
        var expectedPosts = DemoContent.Live.Count + DemoContent.Rejected.Count + DemoContent.Pending.Count;
        Assert.Equal(expectedPosts + 1, result.PostsRemoved);
        Assert.Equal(DemoContent.Live.Count + DemoContent.Rejected.Count + 1, result.ModerationActionsRemoved);
        Assert.Equal(1, result.BlockedAuthorsRemoved);
        Assert.Equal(expectedPosts, await verify.Posts.CountAsync());
        Assert.All(await verify.Posts.ToListAsync(), post => Assert.StartsWith(DemoContent.SeedPrefix, post.ExternalId));
        Assert.Equal(DemoContent.Live.Count + DemoContent.Rejected.Count, await verify.ModerationActions.CountAsync());
        Assert.Empty(await verify.BlockedAuthors.ToListAsync());
        Assert.True((await verify.Events.SingleAsync()).SubmissionsOpen);
        Assert.True((await verify.Events.SingleAsync()).Revision > previousRevision);
        Assert.Equal(25, (await verify.ConnectorDailyUsage.SingleAsync()).ReservedPostReads);
        Assert.Equal("test-protected-token", (await verify.ProviderCredentials.SingleAsync()).ProtectedRefreshToken);
    }

    private static IConfiguration DemoConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [DemoMode.EnabledKey] = "true" })
        .Build();

    private sealed class InMemoryDatabaseFactory(SqliteConnection connection) : IDbContextFactory<EventWallDbContext>
    {
        public EventWallDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection).Options);
    }
}
