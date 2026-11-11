using EventWall.Api.Data;
using EventWall.Api.Models;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;

namespace EventWall.Api.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public void Baseline_and_additive_migrations_match_current_sql_server_model()
    {
        var options = new DbContextOptionsBuilder<EventWallDbContext>()
            .UseSqlServer("Server=localhost;Database=EventWall;Integrated Security=true;TrustServerCertificate=true;")
            .Options;
        using var database = new EventWallDbContext(options);

        Assert.Collection(database.Database.GetMigrations(),
            migration => Assert.EndsWith("_InitialCreate", migration),
            migration => Assert.EndsWith("_AddXUserReadAccounting", migration),
            migration => Assert.EndsWith("_AddStaffTeamAccess", migration),
            migration => Assert.EndsWith("_AddYouTubeCollectionControl", migration),
            migration => Assert.EndsWith("_AddEventPromotions", migration),
            migration => Assert.EndsWith("_AddPromoActivation", migration));
        Assert.False(database.Database.HasPendingModelChanges());
        var script = database.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [Posts]", script);
        Assert.Contains("[FilterReasonsJson]", script);
        Assert.Contains("[PostFiltersJson]", script);
        Assert.Contains("[FilterSettingsVersion]", script);
        Assert.Contains("ADD [ReservedUserReads] int NOT NULL DEFAULT 0", script);
        Assert.Contains("CREATE TABLE [StaffMembers]", script);
        Assert.Contains("CREATE TABLE [Promos]", script);
        Assert.Contains("ADD [Active] bit NOT NULL DEFAULT CAST(1 AS bit)", script);
        Assert.Contains("[StaffUsername]", script);
        Assert.Contains("ADD [YouTubeCollectionRunning] bit NOT NULL DEFAULT CAST(0 AS bit)", script);
    }

    [Fact]
    public async Task Baseline_creates_current_sqlite_schema_and_is_repeatable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
        await using var database = new EventWallDbContext(options);
        var configuration = new ConfigurationBuilder().Build();

        await database.Database.MigrateAsync();
        await EventSeeder.SeedAsync(database, configuration);
        await database.Database.ExecuteSqlRawAsync("""
            INSERT INTO Posts (Id, EventSlug, Source, ExternalId, Text, AuthorName, ReceivedAtUtc, Status, Version)
            VALUES ('00000000-0000-0000-0000-000000000001', 'communityevent', 'Twitch', 'baseline', 'After baseline', 'Viewer', '2026-10-27', 'Pending', 0)
            """);

        await database.Database.MigrateAsync();
        await EventSeeder.SeedAsync(database, configuration);

        Assert.Equal(6, (await database.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Empty(await database.Database.GetPendingMigrationsAsync());
        Assert.Single(await database.Events.ToListAsync());
        var settings = await database.Events.SingleAsync();
        Assert.False(settings.YouTubeCollectionRunning);
        var filters = settings.PostFilters;
        Assert.All(new[]
        {
            filters.Profanity, filters.AllCaps, filters.SingleWord, filters.EmojiOnly,
            filters.LocationOnly, filters.NoSubstantiveText, filters.RepeatedCharacters, filters.DuplicateText
        }, enabled => Assert.True(enabled));
        Assert.Empty(filters.BlockedTerms);
        Assert.Empty(filters.AllowedTerms);
        Assert.Empty(filters.LocationPhrases);
        Assert.Equal(0, settings.FilterSettingsVersion);
        var post = await database.Posts.SingleAsync();
        Assert.Equal("After baseline", post.Text);
        Assert.Empty(post.Images);
        Assert.Null(post.AuthorProfileUrl);
        Assert.Null(post.AuthorAvatarUrl);
        Assert.Empty(post.FilterReasons);
        Assert.Empty(await database.ModerationActions.ToListAsync());
        Assert.Empty(await database.ConnectorDailyUsage.ToListAsync());
        Assert.Empty(await database.ProviderCredentials.ToListAsync());
        Assert.Empty(await database.BlockedAuthors.ToListAsync());
        Assert.Empty(await database.ConnectorCheckpoints.ToListAsync());
        Assert.Empty(await database.StaffMembers.ToListAsync());
        Assert.Empty(await database.Promos.ToListAsync());

        database.ChangeTracker.Clear();
        await database.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
        await database.Database.MigrateAsync();
        await EventSeeder.SeedAsync(database, configuration);

        Assert.Single(await database.Events.ToListAsync());
        Assert.Empty(await database.Posts.ToListAsync());
    }

    [Fact]
    public async Task Reapplying_baseline_preserves_saved_filters_and_moderation_decisions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
        await using var database = new EventWallDbContext(options);
        var configuration = new ConfigurationBuilder().Build();
        await database.Database.MigrateAsync();
        await EventSeeder.SeedAsync(database, configuration);
        var settings = await database.Events.SingleAsync();
        settings.PostFiltersJson = JsonSerializer.Serialize(new PostFilterSettings { SingleWord = false, BlockedTerms = ["spoiler"] });
        settings.FilterSettingsVersion = 2;
        settings.Revision = 7;
        database.Posts.Add(new EventPost
        {
            Id = Guid.NewGuid(), EventSlug = settings.Slug, Source = PostSource.Portal, ExternalId = "approved",
            Text = "Hello", AuthorName = "Viewer", ReceivedAtUtc = DateTime.UtcNow, Status = PostStatus.Live,
            Version = 2, PublishedRevision = 7, FilterReasonsJson = JsonSerializer.Serialize(new[] { PostFilterReason.SingleWord })
        });
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        await database.Database.MigrateAsync();
        await EventSeeder.SeedAsync(database, configuration);

        settings = await database.Events.SingleAsync();
        Assert.False(settings.PostFilters.SingleWord);
        Assert.Equal(["spoiler"], settings.PostFilters.BlockedTerms);
        Assert.Equal(2, settings.FilterSettingsVersion);
        Assert.Equal(7, settings.Revision);
        var post = await database.Posts.SingleAsync();
        Assert.Equal(PostStatus.Live, post.Status);
        Assert.Equal(7, post.PublishedRevision);
        Assert.Equal(2, post.Version);
        Assert.Equal([PostFilterReason.SingleWord], post.FilterReasons);
        Assert.Equal(6, (await database.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task Additive_user_read_migration_preserves_existing_daily_post_usage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
        await using var database = new EventWallDbContext(options);
        await database.GetService<IMigrator>().MigrateAsync(database.Database.GetMigrations().First());
        await database.Database.ExecuteSqlRawAsync("""
            INSERT INTO ConnectorDailyUsage (Id, ReservedPostReads, Version)
            VALUES ('X:20261102', 42, 3)
            """);

        await database.Database.MigrateAsync();
        var usage = await database.ConnectorDailyUsage.SingleAsync();
        Assert.Equal(42, usage.ReservedPostReads);
        Assert.Equal(3, usage.Version);
        Assert.Equal(0, usage.ReservedUserReads);
        usage.ReservedUserReads = 7;
        await database.SaveChangesAsync();

        await database.Database.MigrateAsync();
        database.ChangeTracker.Clear();
        usage = await database.ConnectorDailyUsage.SingleAsync();
        Assert.Equal(42, usage.ReservedPostReads);
        Assert.Equal(3, usage.Version);
        Assert.Equal(7, usage.ReservedUserReads);
    }

    [Fact]
    public async Task Team_migration_preserves_existing_posts_decisions_blocks_and_settings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
        await using var database = new EventWallDbContext(options);
        await database.GetService<IMigrator>().MigrateAsync(database.Database.GetMigrations().ElementAt(1));
        await database.Database.ExecuteSqlRawAsync("""
            INSERT INTO Events (Slug, Name, SubmissionsOpen, Revision, FilterSettingsVersion)
            VALUES ('communityevent', 'Community Event', 1, 7, 0);
            INSERT INTO Posts (Id, EventSlug, Source, ExternalId, Text, AuthorName, ReceivedAtUtc, Status, Version)
            VALUES ('00000000-0000-0000-0000-000000000001', 'communityevent', 'Twitch', 'prior-team', 'Keep this decision', 'Viewer', '2026-11-02', 'Live', 2);
            INSERT INTO ModerationActions (Id, PostId, FromStatus, ToStatus, CreatedAtUtc, StaffSessionId)
            VALUES ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001', 'Pending', 'Live', '2026-11-02', 'legacy-session');
            INSERT INTO BlockedAuthors (EventSlug, Source, AuthorExternalId, CreatedAtUtc, StaffSessionId)
            VALUES ('communityevent', 'Twitch', 'blocked-id', '2026-11-02', 'legacy-session');
            """);
        await database.Database.MigrateAsync();
        database.ChangeTracker.Clear();
        var post = await database.Posts.SingleAsync();
        Assert.Equal(PostStatus.Live, post.Status);
        Assert.Equal(2, post.Version);
        Assert.Equal("Keep this decision", post.Text);
        var decision = await database.ModerationActions.SingleAsync();
        Assert.Equal("legacy-session", decision.StaffSessionId);
        Assert.Null(decision.StaffUsername);
        Assert.Null(decision.StaffMemberId);
        Assert.Null((await database.BlockedAuthors.SingleAsync()).StaffUsername);
        Assert.Equal(7, (await database.Events.SingleAsync()).Revision);
        Assert.True((await database.Events.SingleAsync()).SubmissionsOpen);
        Assert.Empty(await database.StaffMembers.ToListAsync());
    }

    [Fact]
    public async Task YouTube_control_migration_starts_existing_events_stopped_and_preserves_saved_choice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
        await using var database = new EventWallDbContext(options);
        await database.GetService<IMigrator>().MigrateAsync(database.Database.GetMigrations().ElementAt(2));
        await database.Database.ExecuteSqlRawAsync("""
            INSERT INTO Events (Slug, Name, SubmissionsOpen, Revision, FilterSettingsVersion)
            VALUES ('communityevent', 'Community Event', 1, 7, 3);
            """);

        await database.Database.MigrateAsync();
        var settings = await database.Events.SingleAsync();
        Assert.False(settings.YouTubeCollectionRunning);
        Assert.Equal(7, settings.Revision);
        Assert.Equal(3, settings.FilterSettingsVersion);
        Assert.True(settings.SubmissionsOpen);
        settings.YouTubeCollectionRunning = true;
        await database.SaveChangesAsync();

        await database.Database.MigrateAsync();
        await EventSeeder.SeedAsync(database, new ConfigurationBuilder().Build());
        await using var reopened = new EventWallDbContext(options);
        Assert.True((await reopened.Events.SingleAsync()).YouTubeCollectionRunning);
    }
}
