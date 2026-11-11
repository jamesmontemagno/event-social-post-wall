using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EventWall.Api.Tests;

public sealed class PostFilteringTests
{
    public static PostFilterSettings Disabled => new()
    {
        Profanity = false, AllCaps = false, SingleWord = false, EmojiOnly = false,
        LocationOnly = false, NoSubstantiveText = false, RepeatedCharacters = false, DuplicateText = false
    };

    [Theory]
    [InlineData("This is shit.", PostFilterReason.Profanity)]
    [InlineData("THIS IS GREAT", PostFilterReason.AllCaps)]
    [InlineData("Hello #communityevent", PostFilterReason.SingleWord)]
    [InlineData("\U0001F525\U0001F525 #communityevent", PostFilterReason.EmojiOnly)]
    [InlineData("Watching from London", PostFilterReason.LocationOnly)]
    [InlineData("https://example.com #communityevent @viewer !!!", PostFilterReason.NoSubstantiveText)]
    [InlineData("aaaaa", PostFilterReason.RepeatedCharacters)]
    public void Every_rule_can_be_disabled_independently(string text, PostFilterReason reason)
    {
        Assert.Contains(reason, PostFiltering.Evaluate(text, new()));
        Assert.Empty(PostFiltering.Evaluate(text, Disabled));
        var settings = reason switch
        {
            PostFilterReason.Profanity => Disabled with { Profanity = true },
            PostFilterReason.AllCaps => Disabled with { AllCaps = true },
            PostFilterReason.SingleWord => Disabled with { SingleWord = true },
            PostFilterReason.EmojiOnly => Disabled with { EmojiOnly = true },
            PostFilterReason.LocationOnly => Disabled with { LocationOnly = true },
            PostFilterReason.NoSubstantiveText => Disabled with { NoSubstantiveText = true },
            PostFilterReason.RepeatedCharacters => Disabled with { RepeatedCharacters = true },
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        Assert.Equal([reason], PostFiltering.Evaluate(text, settings));
    }

    [Theory]
    [InlineData("THIS IS GREAT", true)]
    [InlineData("THIS IS FUN", false)]
    [InlineData("GREAT PRESENTATION", false)]
    [InlineData("THIS IS Great", false)]
    [InlineData("ASP.NET CORE EF", false)]
    [InlineData("WHY IS THIS BROKEN #communityevent", true)]
    [InlineData("API SDK JSON", false)]
    public void All_caps_has_exact_conservative_boundaries(string text, bool rejected) =>
        Assert.Equal(rejected, PostFiltering.Evaluate(text, Disabled with { AllCaps = true }).Count > 0);

    [Theory]
    [InlineData("C#")]
    [InlineData("C++")]
    [InlineData("What's")]
    [InlineData("Hello \U0001F44B")]
    [InlineData("https://example.com hello #communityevent")]
    public void Technical_tokens_and_contractions_count_as_single_words(string text) =>
        Assert.Contains(PostFilterReason.SingleWord, PostFiltering.Evaluate(text, new()));

    [Theory]
    [InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466")]
    [InlineData("\U0001F44D\U0001F3FD")]
    [InlineData("\U0001F1E8\U0001F1E6")]
    [InlineData("1\uFE0F\u20E3 #\uFE0F\u20E3 *\uFE0F\u20E3")]
    [InlineData("\u2764\uFE0F!!!")]
    public void Emoji_sequences_are_not_words_or_empty_punctuation(string text) =>
        Assert.Equal([PostFilterReason.EmojiOnly], PostFiltering.Evaluate(text, new()));

    [Theory]
    [InlineData("Great talk \U0001F525")]
    [InlineData("How does .NET work with C++?")]
    [InlineData("I use EF CORE and SQL every day.")]
    [InlineData("Watching from London. How do I use Aspire?")]
    [InlineData("Hello from Seattle! Great session today.")]
    [InlineData("Watching from London what is next?")]
    [InlineData("Watching from London and learning lots")]
    [InlineData("Visit New York for our next meetup.")]
    [InlineData("Here is the repo https://example.com #communityevent")]
    [InlineData("The class assignment explains this.")]
    [InlineData("That was sooooo good!!!!!")]
    [InlineData("\u8FD9\u4E2A\u6F14\u793A\u975E\u5E38\u7CBE\u5F69")]
    [InlineData("\u0E01\u0E32\u0E23\u0E2A\u0E32\u0E18\u0E34\u0E15\u0E19\u0E35\u0E49\u0E14\u0E35\u0E21\u0E32\u0E01")]
    [InlineData("\u041E\u0442\u043B\u0438\u0447\u043D\u044B\u0439 \u0434\u043E\u043A\u043B\u0430\u0434")]
    public void Useful_content_and_non_space_delimited_scripts_remain_pending(string text) =>
        Assert.Empty(PostFiltering.Evaluate(text, new() { LocationPhrases = ["New York"] }));

    [Theory]
    [InlineData("Watching from London")]
    [InlineData("Hello from S\u00E3o Paulo! \U0001F44B #communityevent")]
    [InlineData("joining from london")]
    [InlineData("New York")]
    public void Short_checkins_and_configured_places_match(string text) =>
        Assert.Contains(PostFilterReason.LocationOnly,
            PostFiltering.Evaluate(text, new() { LocationPhrases = ["New York"] }));

    [Theory]
    [InlineData("SH1T")]
    [InlineData("f.u.c.k")]
    [InlineData("f u c k")]
    [InlineData("f*ck")]
    [InlineData("sh\u200Bit")]
    [InlineData("sh\u200Dit")]
    [InlineData("\uFF53\uFF48\uFF49\uFF54")]
    public void Profanity_handles_bounded_common_obfuscations(string text) =>
        Assert.Contains(PostFilterReason.Profanity, PostFiltering.Evaluate(text, new()));

    [Fact]
    public void Allowed_terms_only_exempt_the_term_not_other_rules_or_other_profanity()
    {
        var settings = new PostFilterSettings { AllowedTerms = ["shit"], BlockedTerms = ["spoiler"] };
        Assert.DoesNotContain(PostFilterReason.Profanity, PostFiltering.Evaluate("shit", settings));
        Assert.Contains(PostFilterReason.SingleWord, PostFiltering.Evaluate("shit", settings));
        Assert.Contains(PostFilterReason.Profanity, PostFiltering.Evaluate("shit and fuck", settings));
        Assert.Contains(PostFilterReason.Profanity, PostFiltering.Evaluate("A SPOILER here", settings));
        Assert.DoesNotContain(PostFilterReason.Profanity, PostFiltering.Evaluate("A spoilerific title", settings));
    }

    [Fact]
    public void Combined_reasons_are_ordered_and_repetition_threshold_is_exact()
    {
        Assert.Equal([PostFilterReason.SingleWord, PostFilterReason.RepeatedCharacters],
            PostFiltering.Evaluate("AAAAA", new()));
        Assert.Contains(PostFilterReason.RepeatedCharacters, PostFiltering.Evaluate("aaaaaaaabb", new()));
        Assert.DoesNotContain(PostFilterReason.RepeatedCharacters, PostFiltering.Evaluate("aaaaaaabbb", new()));
        Assert.DoesNotContain(PostFilterReason.RepeatedCharacters, PostFiltering.Evaluate("aaaa", new()));
        Assert.Equal([PostFilterReason.NoSubstantiveText, PostFilterReason.RepeatedCharacters],
            PostFiltering.Evaluate("!!!!!", new()));
    }

    [Fact]
    public void Settings_validation_is_explicit_and_normalization_deduplicates()
    {
        Assert.NotEmpty(PostFiltering.ValidateSettings(null));
        Assert.NotEmpty(PostFiltering.ValidateSettings(new() { BlockedTerms = ["  SHIT "], AllowedTerms = ["shit"] }));
        Assert.NotEmpty(PostFiltering.ValidateSettings(new() { BlockedTerms = ["\nword"] }));
        Assert.NotEmpty(PostFiltering.ValidateSettings(new() { LocationPhrases = ["!!!"] }));
        Assert.NotEmpty(PostFiltering.ValidateSettings(new() { BlockedTerms = [new string('a', 41)] }));
        Assert.NotEmpty(PostFiltering.ValidateSettings(new() { BlockedTerms = Enumerable.Repeat("term", 26).ToArray() }));
        Assert.NotEmpty(PostFiltering.ValidateSettings(new() { AllowedTerms = null! }));
        Assert.NotEmpty(PostFiltering.ValidateSettings(new() { BlockedTerms = [null!] }));
        var normalized = PostFiltering.NormalizeSettings(new() { BlockedTerms = [" Test  phrase ", "test phrase"] });
        Assert.Equal(["test phrase"], normalized.BlockedTerms);
        Assert.Empty(PostFiltering.ValidateSettings(normalized));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Can Aspire deploy to Kubernetes?", false)]
    [InlineData("#question Can Aspire deploy to Kubernetes?", true)]
    [InlineData("Can #QUESTION Aspire deploy to Kubernetes?", true)]
    [InlineData("Can Aspire deploy to Kubernetes? (#QuEsTiOn)", true)]
    [InlineData("Can Aspire deploy to Kubernetes?\n#question", true)]
    [InlineData("#questions", false)]
    [InlineData("#question123", false)]
    [InlineData("#question_more", false)]
    [InlineData("prefix#question", false)]
    [InlineData("_#question", false)]
    [InlineData("\u00e9#question", false)]
    [InlineData("#question\u00e9", false)]
    [InlineData("#question\u0301", false)]
    [InlineData("\u0301#question", false)]
    public void Question_intake_requires_an_exact_case_insensitive_hashtag(string? text, bool expected) =>
        Assert.Equal(expected, EventPostIngestor.MatchesQuestionTag(text));

    [Theory]
    [InlineData(PostSource.Twitch)]
    [InlineData(PostSource.YouTube)]
    public async Task Untagged_chat_and_near_matches_are_discarded_without_opening_the_database(PostSource source)
    {
        var ingestor = new EventPostIngestor(new UnavailableDatabaseFactory());
        foreach (var text in new[] { "A useful question about Community Event #communityevent", "A useful #questions message", "A useful prefix#question message" })
        {
            Assert.False(await ingestor.IngestAsync(source, "id", text, "Viewer", "account", null, null, default));
        }

    }

    [Theory]
    [InlineData(PostSource.X)]
    [InlineData(PostSource.Bluesky)]
    [InlineData(PostSource.Mastodon)]
    public async Task Custom_event_terms_control_actual_ingestion_and_never_auto_publish(PostSource source)
    {
        await using var fixture = await Fixture.CreateAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Event:Name"] = "C++ Days",
            ["Event:QuestionHashtag"] = "#cppdays"
        }).Build();
        var ingestor = new EventPostIngestor(fixture, configuration);
        Assert.False(await ingestor.IngestAsync(source, "old", "A useful question #communityevent", "Viewer", "account", null, null, default));
        Assert.True(await ingestor.IngestAsync(source, "tag", "How do you test this library? #CPPDAYS", "Viewer", "account", null, null, default));
        Assert.True(await ingestor.IngestAsync(source, "name", "A useful question from C++ Days", "Viewer", "account", null, null, default));
        await using var database = fixture.CreateDbContext();
        var posts = await database.Posts.ToListAsync();
        Assert.Equal(2, posts.Count);
        Assert.All(posts, post => Assert.Equal(PostStatus.Pending, post.Status));
    }

    [Theory]
    [InlineData(PostSource.Twitch)]
    [InlineData(PostSource.YouTube)]
    public async Task Tagged_chat_stays_pending_and_duplicate_delivery_does_not_create_audits(PostSource source)
    {
        await using var fixture = await Fixture.CreateAsync();
        const string text = "Can Aspire deploy to Kubernetes? #QuEsTiOn";
        Assert.True(await fixture.Ingestor.IngestAsync(source, "id", text, "Viewer", "account", null, null, default));
        Assert.False(await fixture.Ingestor.IngestAsync(source, "id", text, "Viewer", "account", null, null, default));
        await using var database = fixture.CreateDbContext();
        var post = await database.Posts.SingleAsync();
        Assert.Equal(text, post.Text);
        Assert.Equal(PostStatus.Pending, post.Status);
        Assert.Equal(0, post.Version);
        Assert.Null(post.PublishedRevision);
        Assert.Empty(post.FilterReasons);
        Assert.Equal(0, await database.ModerationActions.CountAsync());
        Assert.Equal(0, (await database.Events.SingleAsync()).Revision);
    }

    [Theory]
    [InlineData(PostSource.Twitch)]
    [InlineData(PostSource.YouTube)]
    public async Task Legacy_untagged_chat_keeps_moderation_but_source_deletions_still_withdraw_it(PostSource source)
    {
        await using var fixture = await Fixture.CreateAsync();
        const string text = "A previously approved chat message";
        await using (var database = fixture.CreateDbContext())
        {
            database.Posts.Add(new()
            {
                EventSlug = "event", Source = source, ExternalId = "legacy", Text = text,
                AuthorName = "Viewer", AuthorExternalId = "account", Status = PostStatus.Live,
                PublishedRevision = 7, Version = 3
            });
            (await database.Events.SingleAsync()).Revision = 7;
            await database.SaveChangesAsync();
        }
        Assert.False(await fixture.Ingestor.IngestAsync(source, "legacy", text, "Viewer", "account", null, null, default));
        await using (var database = fixture.CreateDbContext())
        {
            var post = await database.Posts.SingleAsync();
            Assert.Equal(text, post.Text);
            Assert.Equal(PostStatus.Live, post.Status);
            Assert.Equal(3, post.Version);
            Assert.Equal(7, post.PublishedRevision);
            Assert.Equal(7, (await database.Events.SingleAsync()).Revision);
            Assert.Equal(0, await database.ModerationActions.CountAsync());
        }
        Assert.True(await fixture.Ingestor.RemoveMessageAsync(source, "legacy", "Deleted at source.", default));
        await using var verify = fixture.CreateDbContext();
        var removed = await verify.Posts.SingleAsync();
        Assert.Equal(PostStatus.Withdrawn, removed.Status);
        Assert.Equal(4, removed.Version);
        Assert.Null(removed.PublishedRevision);
        Assert.Equal(8, (await verify.Events.SingleAsync()).Revision);
        Assert.Single(await verify.ModerationActions.ToListAsync());
    }

    [Theory]
    [InlineData(PostSource.Portal)]
    [InlineData(PostSource.Manual)]
    public async Task Portal_and_manual_questions_do_not_require_a_hashtag(PostSource source)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.True(await fixture.Ingestor.IngestAsync(source, "id", "Can Aspire deploy to Kubernetes?", "Viewer",
            "account", null, null, default));
        await using var database = fixture.CreateDbContext();
        Assert.Equal(PostStatus.Pending, (await database.Posts.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(PostSource.X, "#communityevent")]
    [InlineData(PostSource.X, "Community Event")]
    [InlineData(PostSource.Bluesky, "#communityevent")]
    [InlineData(PostSource.Bluesky, "Community Event")]
    public async Task X_and_bluesky_keep_their_event_terms_instead_of_the_chat_tag(PostSource source, string eventTerm)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.False(await fixture.Ingestor.IngestAsync(source, "ignored", "Can Aspire deploy to Kubernetes? #question",
            "Viewer", "account", null, null, default));
        Assert.True(await fixture.Ingestor.IngestAsync(source, "matching", $"Can Aspire deploy to Kubernetes? {eventTerm}",
            "Viewer", "account", null, null, default));
        await using var database = fixture.CreateDbContext();
        var post = await database.Posts.SingleAsync();
        Assert.Equal("matching", post.ExternalId);
        Assert.Equal(PostStatus.Pending, post.Status);
    }

    [Theory]
    [InlineData(PostSource.Portal)]
    [InlineData(PostSource.Manual)]
    [InlineData(PostSource.Twitch)]
    [InlineData(PostSource.YouTube)]
    [InlineData(PostSource.X)]
    [InlineData(PostSource.Bluesky)]
    public async Task Ingestion_persists_reasons_and_one_automatic_audit_per_post(PostSource source)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Ingestor.IngestAsync(source, "id", "SH1T #communityevent #question", "Viewer", "account",
            null, null, default);
        await fixture.Ingestor.IngestAsync(source, "id", "SH1T #communityevent #question", "Viewer", "account",
            null, null, default);
        await using var database = fixture.CreateDbContext();
        var post = await database.Posts.SingleAsync();
        Assert.Equal(PostStatus.Rejected, post.Status);
        Assert.Equal([PostFilterReason.Profanity, PostFilterReason.SingleWord], post.FilterReasons);
        Assert.NotNull(post.DecidedAtUtc);
        Assert.Null(post.PublishedRevision);
        var action = await database.ModerationActions.SingleAsync();
        Assert.Equal("source:filter", action.StaffSessionId);
        Assert.Contains("Profanity", action.Reason);
        Assert.InRange(action.Reason!.Length, 1, 250);
        Assert.Equal(0, (await database.Events.SingleAsync()).Revision);
    }

    [Fact]
    public async Task Source_edits_remove_live_posts_and_clear_stale_findings_without_rechecking_duplicates()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Ingest("Hello");
        await using (var database = fixture.CreateDbContext())
        {
            var post = await database.Posts.SingleAsync();
            post.Status = PostStatus.Live;
            post.PublishedRevision = 1;
            var settings = await database.Events.SingleAsync();
            settings.Revision = 1;
            settings.PostFiltersJson = JsonSerializer.Serialize(new PostFilterSettings { BlockedTerms = ["hello"] });
            await database.SaveChangesAsync();
        }
        await fixture.Ingest("Hello");
        await using (var database = fixture.CreateDbContext())
        {
            Assert.Equal(PostStatus.Live, (await database.Posts.SingleAsync()).Status);
            Assert.Single(await database.ModerationActions.ToListAsync());
        }
        await fixture.Ingest("HELLO THERE EVERYONE");
        await using (var database = fixture.CreateDbContext())
        {
            var post = await database.Posts.SingleAsync();
            Assert.Equal(PostStatus.Rejected, post.Status);
            Assert.Contains(PostFilterReason.AllCaps, post.FilterReasons);
            Assert.Equal(1, post.Version);
            Assert.Null(post.PublishedRevision);
            Assert.Equal(2, (await database.Events.SingleAsync()).Revision);
        }
        await fixture.Ingest("A useful new question");
        await using (var database = fixture.CreateDbContext())
        {
            var post = await database.Posts.SingleAsync();
            Assert.Equal(PostStatus.Pending, post.Status);
            Assert.Empty(post.FilterReasons);
            Assert.Null(post.DecidedAtUtc);
            Assert.Equal(2, post.Version);
            Assert.Equal(3, await database.ModerationActions.CountAsync());
        }
    }

    [Fact]
    public async Task Truncation_preserves_unicode_and_duplicate_delivery_does_not_repeat_decisions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var text = new string('a', 1999) + "\U0001F525";
        await fixture.Ingest(text);
        await fixture.Ingest(text);
        await using var database = fixture.CreateDbContext();
        var post = await database.Posts.SingleAsync();
        Assert.Equal(1999, post.Text.Length);
        Assert.Equal(0, post.Version);
        Assert.Single(await database.ModerationActions.ToListAsync());
    }

    [Fact]
    public async Task Duplicate_text_is_exact_across_sources_and_authors()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string text = "A useful caf\u00e9 question #communityevent #question";
        await fixture.Ingestor.IngestAsync(PostSource.X, "first", text, "First author", "first",
            null, null, default);
        await fixture.Ingestor.IngestAsync(PostSource.Bluesky, "copy", text, "Another author", "second",
            null, null, default, images: [new("https://cdn.bsky.app/different.jpg", "A different image")]);
        await fixture.Ingestor.IngestAsync(PostSource.X, "same-source", text, "Another author", "seventh",
            null, null, default);
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "case", text.Replace("A useful", "a useful"), "Viewer", "third",
            null, null, default);
        await fixture.Ingestor.IngestAsync(PostSource.YouTube, "unicode", text.Replace("\u00e9", "e\u0301"),
            "Viewer", "fourth", null, null, default);
        await fixture.Ingestor.IngestAsync(PostSource.Bluesky, "space", text + " ", "Viewer", "fifth",
            null, null, default);
        await fixture.Ingestor.IngestAsync(PostSource.X, "punctuation", text + "!", "Viewer", "sixth",
            null, null, default);

        await using var database = fixture.CreateDbContext();
        var posts = await database.Posts.ToDictionaryAsync(post => post.ExternalId);
        Assert.Equal(7, posts.Count);
        Assert.Equal(PostStatus.Pending, posts["first"].Status);
        Assert.Equal(PostStatus.Rejected, posts["copy"].Status);
        Assert.Equal([PostFilterReason.DuplicateText], posts["copy"].FilterReasons);
        Assert.Single(posts["copy"].Images);
        Assert.Equal(PostStatus.Rejected, posts["same-source"].Status);
        Assert.Equal([PostFilterReason.DuplicateText], posts["same-source"].FilterReasons);
        Assert.NotNull(posts["copy"].DecidedAtUtc);
        Assert.Null(posts["copy"].PublishedRevision);
        Assert.All(new[] { "case", "unicode", "space", "punctuation" },
            id =>
            {
                Assert.Equal(PostStatus.Pending, posts[id].Status);
                Assert.Empty(posts[id].FilterReasons);
            });
        var action = await database.ModerationActions.SingleAsync(action => action.PostId == posts["copy"].Id);
        Assert.Equal("source:filter", action.StaffSessionId);
        Assert.Contains("Exact duplicate text", action.Reason);
    }

    [Theory]
    [InlineData(PostStatus.Pending)]
    [InlineData(PostStatus.Live)]
    [InlineData(PostStatus.Rejected)]
    [InlineData(PostStatus.Withdrawn)]
    public async Task Earlier_posts_in_any_status_count_as_duplicates(PostStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();
        const string text = "A useful question #communityevent #question";
        await fixture.Ingestor.IngestAsync(PostSource.X, "first", text, "First", "first", null, null, default);
        await using (var database = fixture.CreateDbContext())
        {
            (await database.Posts.SingleAsync()).Status = status;
            await database.SaveChangesAsync();
        }

        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "copy", text, "Second", "second", null, null, default);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(status, (await verify.Posts.SingleAsync(post => post.ExternalId == "first")).Status);
        var copy = await verify.Posts.SingleAsync(post => post.ExternalId == "copy");
        Assert.Equal(PostStatus.Rejected, copy.Status);
        Assert.Equal([PostFilterReason.DuplicateText], copy.FilterReasons);
    }

    [Fact]
    public async Task Duplicate_matching_is_limited_to_the_current_event()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string text = "A useful question #communityevent";
        await using (var database = fixture.CreateDbContext())
        {
            database.Posts.Add(new EventPost
            {
                Id = Guid.NewGuid(), EventSlug = "another-event", Source = PostSource.X, ExternalId = "other",
                Text = text, AuthorName = "Viewer", Status = PostStatus.Pending
            });
            await database.SaveChangesAsync();
        }

        await fixture.Ingestor.IngestAsync(PostSource.Bluesky, "current", text, "Viewer", "account", null, null, default);

        await using var verify = fixture.CreateDbContext();
        var current = await verify.Posts.SingleAsync(post => post.ExternalId == "current");
        Assert.Equal(PostStatus.Pending, current.Status);
        Assert.Empty(current.FilterReasons);
    }

    [Fact]
    public async Task Source_edits_check_changed_text_without_reclassifying_the_original()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string original = "A useful question #communityevent";
        await fixture.Ingestor.IngestAsync(PostSource.X, "first", original, "First", "first", null, null, default);
        await fixture.Ingestor.IngestAsync(PostSource.Bluesky, "second", "A different question #communityevent",
            "Second", "second", null, null, default);
        await using (var database = fixture.CreateDbContext())
        {
            var second = await database.Posts.SingleAsync(post => post.ExternalId == "second");
            second.Status = PostStatus.Live;
            second.PublishedRevision = 7;
            (await database.Events.SingleAsync()).Revision = 7;
            await database.SaveChangesAsync();
        }

        await fixture.Ingestor.IngestAsync(PostSource.Bluesky, "second", original, "Second", "second", null, null, default);
        await fixture.Ingestor.IngestAsync(PostSource.X, "first", original, "Updated name", "first", null, null, default);
        await using (var database = fixture.CreateDbContext())
        {
            var first = await database.Posts.SingleAsync(post => post.ExternalId == "first");
            var second = await database.Posts.SingleAsync(post => post.ExternalId == "second");
            Assert.Equal(PostStatus.Pending, first.Status);
            Assert.Empty(first.FilterReasons);
            Assert.Equal(PostStatus.Rejected, second.Status);
            Assert.Equal([PostFilterReason.DuplicateText], second.FilterReasons);
            Assert.Null(second.PublishedRevision);
            Assert.Equal(8, (await database.Events.SingleAsync()).Revision);
            second.Status = PostStatus.Live;
            second.PublishedRevision = 9;
            second.Version++;
            (await database.Events.SingleAsync()).Revision = 9;
            await database.SaveChangesAsync();
        }

        await fixture.Ingestor.IngestAsync(PostSource.Bluesky, "second", original, "Second", "second", null, null, default);
        await using (var database = fixture.CreateDbContext())
        {
            var second = await database.Posts.SingleAsync(post => post.ExternalId == "second");
            Assert.Equal(PostStatus.Live, second.Status);
            Assert.Equal(2, second.Version);
        }

        await fixture.Ingestor.IngestAsync(PostSource.Bluesky, "second", original, "Updated second", "second", null, null, default);
        await using var verify = fixture.CreateDbContext();
        var edited = await verify.Posts.SingleAsync(post => post.ExternalId == "second");
        Assert.Equal(PostStatus.Rejected, edited.Status);
        Assert.Equal([PostFilterReason.DuplicateText], edited.FilterReasons);
        Assert.Null(edited.PublishedRevision);
        Assert.Equal(10, (await verify.Events.SingleAsync()).Revision);
    }

    private sealed class UnavailableDatabaseFactory : IDbContextFactory<EventWallDbContext>
    {
        public EventWallDbContext CreateDbContext() =>
            throw new InvalidOperationException("Ignored chat must not open the database.");
    }

    private sealed class Fixture(SqliteConnection connection) : IDbContextFactory<EventWallDbContext>, IAsyncDisposable
    {
        public EventWallDbContext CreateDbContext() => new(new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection).Options);
        public EventPostIngestor Ingestor => new(this);
        public Task<bool> Ingest(string text) => Ingestor.IngestAsync(PostSource.Twitch, "id", $"{text} #question", "Viewer", "account", null, null, default);
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var fixture = new Fixture(connection);
            await using var database = fixture.CreateDbContext();
            await database.Database.EnsureCreatedAsync();
            database.Events.Add(new() { Slug = "event", Name = "Event" });
            await database.SaveChangesAsync();
            return fixture;
        }
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
