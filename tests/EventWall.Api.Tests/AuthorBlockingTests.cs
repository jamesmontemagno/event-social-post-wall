using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EventWall.Api.Tests;

public sealed class AuthorBlockingTests
{
    [Fact]
    public async Task Blocking_preserves_existing_posts_and_rejects_future_account_posts_only()
    {
        await using var fixture = await Fixture.CreateAsync();
        var old = await fixture.Ingest("old", "account-1");
        await using (var database = fixture.CreateDbContext())
        {
            var post = await database.Posts.SingleAsync(item => item.Id == old.Id);
            post.Status = PostStatus.Live;
            post.PublishedRevision = 1;
            (await database.Events.SingleAsync()).Revision = 1;
            await database.SaveChangesAsync();
        }
        Assert.Equal(BlockAuthorResult.Blocked, await fixture.Blocking.BlockAsync(old.Id, old.Version, "test-staff", default));
        Assert.Equal(BlockAuthorResult.Blocked, await fixture.Blocking.BlockAsync(old.Id, old.Version, "test-staff", default));
        var future = await fixture.Ingest("new", "account-1", authorName: "A renamed account");
        var other = await fixture.Ingest("other", "account-2", text: "A different question");
        var otherSource = await fixture.Ingest("same-id-different-platform", "account-1", PostSource.YouTube,
            text: "Another question");
        await using var verify = fixture.CreateDbContext();
        var original = await verify.Posts.SingleAsync(post => post.Id == old.Id);
        Assert.Equal(PostStatus.Live, original.Status);
        Assert.Equal(1, original.PublishedRevision);
        Assert.Equal(1, (await verify.Events.SingleAsync()).Revision);
        Assert.Equal(old.Version, original.Version);
        Assert.Equal(PostStatus.Rejected, future.Status);
        Assert.NotNull(future.DecidedAtUtc);
        Assert.Null(future.PublishedRevision);
        Assert.Equal(PostStatus.Pending, other.Status);
        Assert.Equal(PostStatus.Pending, otherSource.Status);
        Assert.Equal(1, await verify.BlockedAuthors.CountAsync());
        var decision = await verify.ModerationActions.SingleAsync();
        Assert.Equal(future.Id, decision.PostId);
        Assert.Contains("blocked", decision.Reason);
        Assert.StartsWith("source:", decision.StaffSessionId);
    }

    [Fact]
    public async Task Duplicate_and_edited_auto_rejected_posts_cannot_reenter_pending()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initial = await fixture.Ingest("initial", "account");
        await fixture.Blocking.BlockAsync(initial.Id, initial.Version, "staff", default);
        var rejected = await fixture.Ingest("future", "account");
        await fixture.Ingest("future", "account");
        var edited = await fixture.Ingest("future", "account", text: "Edited future question");
        Assert.Equal(rejected.Id, edited.Id);
        Assert.Equal(PostStatus.Rejected, edited.Status);
        Assert.Equal(1, edited.Version);
        await using var database = fixture.CreateDbContext();
        Assert.Equal(2, await database.Posts.CountAsync());
        Assert.Equal(2, await database.ModerationActions.CountAsync());
    }

    [Theory]
    [InlineData(PostSource.Portal, "anonymous")]
    [InlineData(PostSource.Manual, "unverified")]
    [InlineData(PostSource.Twitch, null)]
    public async Task Anonymous_or_unidentified_authors_cannot_be_blocked(PostSource source, string? authorId)
    {
        await using var fixture = await Fixture.CreateAsync();
        var post = await fixture.Ingest("id", authorId, source);
        Assert.Equal(BlockAuthorResult.Unsupported, await fixture.Blocking.BlockAsync(post.Id, 0, "staff", default));
        Assert.False(StaffPostDto.From(post).CanBlockAuthor);
        await using var database = fixture.CreateDbContext();
        Assert.Empty(await database.BlockedAuthors.ToListAsync());
    }

    [Fact]
    public async Task Stale_missing_or_other_event_posts_cannot_create_a_block()
    {
        await using var fixture = await Fixture.CreateAsync();
        var post = await fixture.Ingest("post", "account");
        Assert.Equal(BlockAuthorResult.VersionConflict, await fixture.Blocking.BlockAsync(post.Id, 42, "staff", default));
        Assert.Equal(BlockAuthorResult.NotFound, await fixture.Blocking.BlockAsync(Guid.NewGuid(), 0, "staff", default));
        await using var database = fixture.CreateDbContext();
        var stored = await database.Posts.SingleAsync();
        stored.EventSlug = "different-event";
        await database.SaveChangesAsync();
        Assert.Equal(BlockAuthorResult.NotFound, await fixture.Blocking.BlockAsync(post.Id, 0, "staff", default));
    }

    [Fact]
    public async Task Blocks_are_event_scoped_and_do_not_reject_same_display_name()
    {
        await using var fixture = await Fixture.CreateAsync();
        var post = await fixture.Ingest("old", "id-1", authorName: "Same Name");
        await fixture.Blocking.BlockAsync(post.Id, 0, "staff", default);
        Assert.Equal(PostStatus.Pending, (await fixture.Ingest("other", "id-2",
            text: "A different question", authorName: "Same Name")).Status);
        await using (var database = fixture.CreateDbContext())
        {
            database.Events.Remove(await database.Events.SingleAsync());
            database.Events.Add(new EventSettings { Slug = "other", Name = "Other event" });
            await database.SaveChangesAsync();
        }
        Assert.Equal(PostStatus.Pending, (await fixture.Ingest("next-event", "id-1")).Status);
    }

    [Fact]
    public async Task Profile_metadata_refresh_does_not_change_approval_or_expose_account_ids()
    {
        await using var fixture = await Fixture.CreateAsync();
        var post = await fixture.Ingest("message", "private-account-id");
        await using (var database = fixture.CreateDbContext())
        {
            var stored = await database.Posts.SingleAsync();
            stored.Status = PostStatus.Live;
            stored.PublishedRevision = 7;
            await database.SaveChangesAsync();
        }
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "message", "A useful question #question", "Viewer",
            "private-account-id", null, null, default, "https://www.twitch.tv/viewer",
            "https://static-cdn.jtvnw.net/avatar.png");
        await using var verify = fixture.CreateDbContext();
        var updated = await verify.Posts.SingleAsync(item => item.Id == post.Id);
        Assert.Equal(PostStatus.Live, updated.Status);
        Assert.Equal(7, updated.PublishedRevision);
        Assert.Equal("https://static-cdn.jtvnw.net/avatar.png", PublicPostDto.From(updated).AuthorAvatarUrl);
        Assert.Equal("https://www.twitch.tv/viewer", StaffPostDto.From(updated).AuthorProfileUrl);
        Assert.DoesNotContain("private-account-id", System.Text.Json.JsonSerializer.Serialize(PublicPostDto.From(updated)));
    }

    private sealed class Fixture(SqliteConnection connection) : IDbContextFactory<EventWallDbContext>, IAsyncDisposable
    {
        public EventWallDbContext CreateDbContext() => new(new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection).Options);
        public EventPostIngestor Ingestor => new(this);
        public AuthorBlocking Blocking => new(this);
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var fixture = new Fixture(connection);
            await using var database = fixture.CreateDbContext();
            await database.Database.EnsureCreatedAsync();
            await EventSeeder.SeedAsync(database, new ConfigurationBuilder().Build());
            return fixture;
        }
        public async Task<EventPost> Ingest(string id, string? authorId, PostSource source = PostSource.Twitch,
            string text = "A useful question", string authorName = "Viewer")
        {
            if (source is PostSource.Twitch or PostSource.YouTube) text += " #question";
            await Ingestor.IngestAsync(source, id, text, authorName, authorId, null, null, default);
            await using var database = CreateDbContext();
            return await database.Posts.SingleAsync(post => post.ExternalId == id && post.Source == source &&
                post.EventSlug == database.Events.Single().Slug);
        }
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
