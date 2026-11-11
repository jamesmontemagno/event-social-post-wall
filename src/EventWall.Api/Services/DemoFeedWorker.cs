using EventWall.Api.Data;
using EventWall.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EventWall.Api.Services;

public sealed record DemoMessage(PostSource Source, string Author, string Text);

public static class DemoContent
{
    public const string SeedPrefix = "demo-seed-";
    public const string StreamPrefix = "demo-stream-";
    public static readonly string[] SocialProviders = ["Twitch", "YouTube", "X", "Bluesky", "Mastodon"];
    public static IReadOnlyList<PostImage> ImagesFor(string author) => author switch
    {
        "@sam.codes.bsky.social" => [new("/demo-images/code.svg", "Fictional demo: a minimal API code sample.")],
        "@devjules" => [
            new("/demo-images/community.svg", "Fictional demo: a community watch party illustration."),
            new("/demo-images/stage.svg", "Fictional demo: the community stage."),
            new("/demo-images/code.svg", "Fictional demo: a C# code sample."),
            new("/demo-images/questions.svg", "Fictional demo: community questions for the hosts.")
        ],
        "@carla.dev" => [new("/demo-images/stage.svg", "Fictional demo: a stage illustration awaiting moderator review.")],
        _ => []
    };

    public static readonly IReadOnlyList<DemoMessage> Live =
    [
        new(PostSource.YouTube, "Marina Costa", "Watching from São Paulo with our whole user group! Hello from the community in Brazil 👋 #question"),
        new(PostSource.Twitch, "pixelpirate_42", "Question for the panel: what's the best way for a newcomer to get involved? #question"),
        new(PostSource.Bluesky, "@sam.codes.bsky.social", "That hot reload demo was so smooth. Loving every session at Community Event so far #communityevent"),
        new(PostSource.X, "@devjules", "The hands-on workshop is my favorite part of #communityevent every year. Learning by building together."),
        new(PostSource.Portal, "Priya in Seattle", "How do you decide which features ship in a release versus staying in preview? Would love to hear about the process."),
        new(PostSource.Twitch, "null_ref_ninja", "first time catching it live and the chat here is so wholesome 💜 #question"),
        new(PostSource.YouTube, "Kenji Watanabe", "Any guidance for introducing a new process to a long-running project? Asking for an experienced team 😅 #question"),
        new(PostSource.Bluesky, "@buildwithben.bsky.social", "The live demo at Community Event just sold my team on a weekend hackathon #communityevent"),
        new(PostSource.X, "@maui_maven", "Cross-platform demo running on four devices at once 🤯 #communityevent"),
        new(PostSource.Portal, "Attendee", "What's one lesson you wish you had learned earlier in your career?")
    ];

    public static readonly IReadOnlyList<DemoMessage> Rejected =
    [
        new(PostSource.X, "@free_gains_4u", "🚀🚀 Giveaway for #communityevent viewers — click the link in my profile!!"),
        new(PostSource.Twitch, "follow4follow_bot", "FOLLOW ME FOR A FOLLOW BACK #question")
    ];

    public static readonly IReadOnlyList<DemoMessage> Pending =
    [
        new(PostSource.YouTube, "Aisha Bello", "Can you share the resources from the workshop? I'd love to try it with my students. #question"),
        new(PostSource.Twitch, "rustacean_curious", "coming from a different background — which beginner sessions would you recommend? #question"),
        new(PostSource.Bluesky, "@carla.dev", "Taking notes for our local meetup recap. Great energy at Community Event today! #communityevent"),
        new(PostSource.X, "@growth_hacks_9000", "Get 10k followers fast!!! DM me #communityevent"),
        new(PostSource.Mastodon, "@nora@community.example", "Our study group is comparing notes from the community sessions. #communityevent")
    ];

    public static readonly IReadOnlyList<DemoMessage> Stream =
    [
        new(PostSource.Twitch, "byte_sized_dev", "Will this session be available on demand afterwards? #question"),
        new(PostSource.YouTube, "Tunde Okafor", "Greetings from Lagos! 🇳🇬 Our study group is watching together. #question"),
        new(PostSource.X, "@cloudnative_kim", "Just finished the workshop challenge during the break. #communityevent is my favorite kind of homework."),
        new(PostSource.Bluesky, "@lena.codes.bsky.social", "Coffee ☕ + Community Event + a brand-new side project idea #communityevent"),
        new(PostSource.Twitch, "async_await_anna", "What's the recommended way to turn an idea from today's session into a practical project? #question"),
        new(PostSource.YouTube, "Diego Ramírez", "Will the slides and samples be shared after the event? #question"),
        new(PostSource.X, "@test_first_tom", "Hot take: the community Q&A is the best part of #communityevent"),
        new(PostSource.Bluesky, "@mobile.maya.bsky.social", "Any recommended resources for continuing to learn after Community Event? #communityevent"),
        new(PostSource.Twitch, "gc_whisperer", "the perf charts in that last segment were wild #question"),
        new(PostSource.YouTube, "Sofia Lindqvist", "Hej from Stockholm! What's the best way to contribute to a community project as a first-timer? #question"),
        new(PostSource.X, "@blazor_bree", "Watching the design session and already planning improvements for our project #communityevent"),
        new(PostSource.Bluesky, "@ops.oliver.bsky.social", "Observability dashboards out of the box? Yes please. #communityevent"),
        new(PostSource.Mastodon, "@fediverse_dev@hachyderm.io", "Loving the open source energy at Community Event today. #communityevent"),
        new(PostSource.Mastodon, "@csharp_ari@mastodon.social", "What's the best first task for someone volunteering with the community? #communityevent")
    ];

    public static async Task<bool> SeedAsync(
        IDbContextFactory<EventWallDbContext> factory,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        return await SeedAsync(database, nowUtc, cancellationToken);
    }

    public static async Task<bool> SeedAsync(
        EventWallDbContext database,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        if (await database.Posts.AnyAsync(post =>
                post.EventSlug == settings.Slug && post.ExternalId.StartsWith(SeedPrefix), cancellationToken))
        {
            // Upgrade unchanged fictional fixtures without resetting staff decisions or real submissions.
            var seeds = await database.Posts.Where(post =>
                post.EventSlug == settings.Slug &&
                post.ExternalId.StartsWith(SeedPrefix) &&
                post.ImagesJson == "[]").ToListAsync(cancellationToken);
            var fixtures = Live.Concat(Rejected).Concat(Pending).ToList();
            foreach (var post in seeds)
            {
                var fixtureIndex = fixtures.FindIndex(message => message.Source == post.Source &&
                    message.Author == post.AuthorName && message.Text == post.Text);
                if (fixtureIndex < 0 || post.ExternalId != $"{SeedPrefix}{fixtureIndex:D3}") continue;
                var images = ImagesFor(post.AuthorName);
                if (images.Count == 0) continue;
                post.ImagesJson = JsonSerializer.Serialize(images);
                post.Version++;
                if (post.Status == PostStatus.Live) settings.Revision++;
            }
            await database.SaveChangesAsync(cancellationToken);
            return false;
        }

        var index = 0;
        var received = nowUtc.AddMinutes(-24);

        foreach (var message in Live)
        {
            received = received.AddSeconds(95 + index * 11);
            var decided = received.AddSeconds(18 + index % 4 * 13);
            settings.Revision++;
            AddDecided(database, settings, message, index++, received, decided, PostStatus.Live, settings.Revision);
        }

        foreach (var message in Rejected)
        {
            received = received.AddSeconds(70);
            AddDecided(database, settings, message, index++, received, received.AddSeconds(9), PostStatus.Rejected, null);
        }

        for (var pending = 0; pending < Pending.Count; pending++)
        {
            received = nowUtc.AddSeconds(-(Pending.Count - pending) * 25);
            database.Posts.Add(CreatePost(settings, Pending[pending], index++, received, PostStatus.Pending));
        }

        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void AddDecided(
        EventWallDbContext database,
        EventSettings settings,
        DemoMessage message,
        int index,
        DateTime receivedUtc,
        DateTime decidedUtc,
        PostStatus status,
        long? publishedRevision)
    {
        var post = CreatePost(settings, message, index, receivedUtc, status);
        post.DecidedAtUtc = decidedUtc;
        post.PublishedRevision = publishedRevision;
        post.Version = 1;
        database.Posts.Add(post);
        database.ModerationActions.Add(new ModerationAction
        {
            Id = Guid.NewGuid(),
            PostId = post.Id,
            FromStatus = PostStatus.Pending,
            ToStatus = status,
            CreatedAtUtc = decidedUtc,
            StaffSessionId = "demo-seed",
            Reason = status == PostStatus.Rejected ? "Spam (simulated demo decision)." : null
        });
    }

    private static EventPost CreatePost(
        EventSettings settings,
        DemoMessage message,
        int index,
        DateTime receivedUtc,
        PostStatus status) => new()
    {
        Id = Guid.NewGuid(),
        EventSlug = settings.Slug,
        Source = message.Source,
        ExternalId = $"{SeedPrefix}{index:D3}",
        Text = message.Text,
        AuthorName = message.Author,
        AuthorExternalId = $"demo-author:{message.Author}",
        ImagesJson = JsonSerializer.Serialize(ImagesFor(message.Author)),
        ReceivedAtUtc = receivedUtc,
        SourceCreatedAtUtc = receivedUtc.AddSeconds(-4),
        Status = status
    };
}

public sealed class DemoFeedWorker(
    IConfiguration configuration,
    IDbContextFactory<EventWallDbContext> factory,
    EventPostIngestor ingestor,
    SourceHealthRegistry health,
    ILogger<DemoFeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Demo:IntervalSeconds", 7), 2, 300));
        var maximum = Math.Clamp(configuration.GetValue("Demo:MaxGeneratedPosts", 120), 0, 2000);

        try
        {
            if (await DemoContent.SeedAsync(factory, DateTime.UtcNow, stoppingToken))
            {
                logger.LogInformation("Demo mode seeded simulated wall, queue, and report data.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Demo mode could not seed simulated data.");
        }

        foreach (var provider in DemoContent.SocialProviders)
        {
            health.Report(provider, "demo-simulated", "Simulated demo messages; no provider API calls are made.", DateTime.UtcNow);
        }

        int next;
        await using (var database = await factory.CreateDbContextAsync(stoppingToken))
        {
            next = await database.Posts.CountAsync(post => post.ExternalId.StartsWith(DemoContent.StreamPrefix), stoppingToken);
        }

        using var timer = new PeriodicTimer(interval);
        var generated = 0;
        while (generated < maximum && await timer.WaitForNextTickAsync(stoppingToken))
        {
            var message = DemoContent.Stream[next % DemoContent.Stream.Count];
            try
            {
                await ingestor.IngestAsync(
                    message.Source,
                    $"{DemoContent.StreamPrefix}{next:D5}",
                    message.Text.Replace(EventConfiguration.DefaultQuestionHashtag, configuration["Event:QuestionHashtag"] ?? EventConfiguration.DefaultQuestionHashtag)
                        .Replace(EventConfiguration.DefaultName, configuration["Event:Name"] ?? EventConfiguration.DefaultName),
                    message.Author,
                    $"demo-author:{message.Author}",
                    null,
                    DateTime.UtcNow.AddSeconds(-3),
                    stoppingToken);
                health.Report(message.Source.ToString(), "demo-simulated",
                    "Simulated demo messages; no provider API calls are made.", DateTime.UtcNow);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Demo mode could not add a simulated post.");
            }

            next++;
            generated++;
        }
    }
}
