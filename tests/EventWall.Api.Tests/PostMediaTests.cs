using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EventWall.Api.Tests;

public sealed class PostMediaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_bluesky_photos_in_direct_and_quote_media_embeds(bool quote)
    {
        var images = """{"images":[{"fullsize":"https://cdn.bsky.app/photo.jpg","alt":"A stage"}]}""";
        using var document = JsonDocument.Parse(quote ? "{\"embed\":{\"media\":" + images + "}}" : "{\"embed\":" + images + "}");
        var photo = Assert.Single(PostMedia.BlueskyImages(document.RootElement));
        Assert.Equal("https://cdn.bsky.app/photo.jpg", photo.Url);
        Assert.Equal("A stage", photo.Alt);
    }

    [Fact]
    public void Reads_bluesky_record_blob_photos_as_safe_cdn_urls()
    {
        using var document = JsonDocument.Parse("""
            {"embed":{"$type":"app.bsky.embed.images","images":[
              {"alt":"A stage","image":{"$type":"blob","ref":{"$link":"bafk-image"},"mimeType":"image/jpeg","size":1000}}]}}
            """);

        var photo = Assert.Single(PostMedia.BlueskyRecordImages(document.RootElement, "did:plc:viewer"));

        Assert.Equal(
            "https://cdn.bsky.app/img/feed_fullsize/plain/did%3Aplc%3Aviewer/bafk-image@jpeg",
            photo.Url);
        Assert.Equal("A stage", photo.Alt);
    }

    [Fact]
    public void X_includes_only_attached_photos_not_other_posts_or_videos()
    {
        using var document = JsonDocument.Parse("""
            {"data":[{"attachments":{"media_keys":["one","two"]}}],"includes":{"media":[
              {"media_key":"one","type":"photo","url":"https://pbs.twimg.com/media/photo.jpg","alt_text":"A room"},
              {"media_key":"two","type":"video","preview_image_url":"https://pbs.twimg.com/video.jpg"},
              {"media_key":"unrelated","type":"photo","url":"https://pbs.twimg.com/media/other.jpg"}]}}
            """);
        var photo = Assert.Single(PostMedia.XImages(document.RootElement.GetProperty("data")[0], document.RootElement));
        Assert.Equal("A room", photo.Alt);
        Assert.Equal("https://pbs.twimg.com/media/photo.jpg", photo.Url);
    }

    [Fact]
    public async Task Persists_four_safe_photos_with_bounded_alt_text_and_requires_review_for_image_changes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new DatabaseFactory(connection);
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.EnsureCreatedAsync();
            await EventSeeder.SeedAsync(database, new ConfigurationBuilder().Build());
        }
        var ingestor = new EventPostIngestor(factory);
        var photos = new List<PostImage>
        {
            new("http://cdn.bsky.app/insecure.jpg", "Unsafe"),
            new("https://untrusted.example/tracker.jpg", "Unsafe"),
            new("https://cdn.bsky.app/one.jpg", new string('a', 1500)),
            new("https://cdn.bsky.app/one.jpg", "Duplicate")
        };
        photos.AddRange(Enumerable.Range(2, 6).Select(number => new PostImage($"https://cdn.bsky.app/{number}.jpg", "Photo")));
        await ingestor.IngestAsync(PostSource.Bluesky, "message", "A useful question #communityevent", "Viewer", "id", null, null, default, images: photos);
        await using (var database = factory.CreateDbContext())
        {
            var post = await database.Posts.SingleAsync();
            Assert.Equal(4, post.Images.Count);
            Assert.Equal(1000, post.Images[0].Alt!.Length);
            Assert.All(post.Images, image => Assert.StartsWith("https://cdn.bsky.app/", image.Url));
            Assert.Equal(4, PublicPostDto.From(post).Images!.Count);
            Assert.Equal(4, StaffPostDto.From(post).Images.Count);
            post.Status = PostStatus.Live;
            post.PublishedRevision = 1;
            (await database.Events.SingleAsync()).Revision = 1;
            await database.SaveChangesAsync();
        }
        await ingestor.IngestAsync(PostSource.Bluesky, "message", "A useful question #communityevent", "Viewer", "id", null, null, default,
            images: [new("https://cdn.bsky.app/changed.jpg", "Changed")]);
        await using var verify = factory.CreateDbContext();
        var updated = await verify.Posts.SingleAsync();
        Assert.Equal(PostStatus.Pending, updated.Status);
        Assert.Null(updated.PublishedRevision);
        Assert.Equal(1, updated.Version);
        Assert.Equal(2, (await verify.Events.SingleAsync()).Revision);
        Assert.Equal("Changed", Assert.Single(updated.Images).Alt);
        Assert.Single(await verify.ModerationActions.ToListAsync());
    }

    private sealed class DatabaseFactory(SqliteConnection connection) : IDbContextFactory<EventWallDbContext>
    {
        public EventWallDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection).Options);
    }
}
