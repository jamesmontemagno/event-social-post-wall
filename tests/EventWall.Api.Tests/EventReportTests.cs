using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace EventWall.Api.Tests;

public sealed class EventReportTests(ITestOutputHelper output)
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Separates_current_state_history_automation_and_first_staff_decision()
    {
        await using var fixture = new Fixture();
        await fixture.Initialize();
        fixture.Configuration["Social:Mastodon:AnalyticsApproved"] = "true";
        var live = fixture.Post(PostSource.Portal, PostStatus.Live, Now.AddHours(-2));
        live.ImagesJson = """[{"Url":"https://example.test/a"},{"Url":"https://example.test/b"}]""";
        var rejected = fixture.Post(PostSource.Manual, PostStatus.Rejected, Now.AddHours(-1));
        rejected.FilterReasonsJson = "[0,1]";
        var pending = fixture.Post(PostSource.Portal, PostStatus.Pending, Now.AddMinutes(-10));
        var withdrawn = fixture.Post(PostSource.Manual, PostStatus.Withdrawn, Now.AddHours(-3));
        fixture.Post(PostSource.X, PostStatus.Live, Now.AddHours(-1));
        var otherEvent = fixture.Post(PostSource.Portal, PostStatus.Live, Now.AddHours(-1));
        otherEvent.EventSlug = "other";
        fixture.Action(live, PostStatus.Rejected, live.ReceivedAtUtc, "source:filter");
        fixture.Action(live, PostStatus.Live, live.ReceivedAtUtc.AddSeconds(60));
        fixture.Action(live, PostStatus.Withdrawn, live.ReceivedAtUtc.AddSeconds(90));
        fixture.Action(live, PostStatus.Live, live.ReceivedAtUtc.AddSeconds(120));
        fixture.Action(rejected, PostStatus.Rejected, rejected.ReceivedAtUtc, "source:filter");
        fixture.Action(withdrawn, PostStatus.Withdrawn, Now.AddMinutes(-1), "source:Manual");
        fixture.Action(otherEvent, PostStatus.Live, Now);
        fixture.Database.Promos.Add(new() { Id = Guid.NewGuid(), EventSlug = "event", Title = "Promo", Description = "Details", Active = true, CreatedAtUtc = Now });
        foreach (var (eventSlug, source) in new[] { ("event", PostSource.X), ("event", PostSource.Mastodon), ("other", PostSource.Mastodon) })
            fixture.Database.BlockedAuthors.Add(new() { EventSlug = eventSlug, Source = source, AuthorExternalId = "blocked-account", StaffSessionId = "staff", CreatedAtUtc = Now });
        await fixture.Database.SaveChangesAsync();

        var report = await fixture.Report();
        Assert.Equal(4, report.Counts.Total);
        Assert.Equal((1, 1, 1, 1), (report.Counts.Live, report.Counts.Pending, report.Counts.Rejected, report.Counts.Withdrawn));
        Assert.Equal(.5, report.Counts.ApprovalRate);
        Assert.Equal((1, 2, 2), (report.Counts.EverApproved, report.Counts.EverRejected, report.Counts.EverWithdrawn));
        Assert.Equal((3, 2, 1, 2), (report.Counts.StaffActions, report.Counts.FilterActions, report.Counts.SourceActions, report.Counts.RepeatStaffActions));
        Assert.Equal(60, report.Counts.MedianDecisionSeconds);
        Assert.Equal(1, report.Counts.DecisionSamples);
        Assert.Equal(600, report.Counts.OldestPendingSeconds);
        Assert.Equal(1, report.PendingAges[1].Count);
        Assert.Equal((1, 2, 1), (report.Counts.ImagePosts, report.Counts.Images, report.Counts.FlaggedPosts));
        Assert.Equal(2, report.Findings.Sum(item => item.Count));
        Assert.Equal(4, report.Timeline.Sum(item => item.Intake.Values.Sum()));
        Assert.Equal(2, report.Timeline.Sum(item => item.Approved));
        Assert.Equal(0, report.Timeline.Sum(item => item.Rejected));
        Assert.Equal(2, report.Timeline.Sum(item => item.FilterActions));
        Assert.Equal(1, report.Timeline.Sum(item => item.SourceActions));
        Assert.Equal(1, report.Operations.ActivePromos);
        Assert.Contains(PostSource.X, report.ExcludedSources);
        Assert.DoesNotContain(report.Platforms, item => item.Source == PostSource.X);
        Assert.Equal(1, Assert.Single(report.Platforms, item => item.Source == PostSource.Mastodon).BlockedAccounts);
        Assert.Equal(0, Assert.Single(report.Platforms, item => item.Source == PostSource.Mastodon).Counts.Total);
        Assert.Single(report.Reviewers);
        Assert.Equal(2, report.Reviewers[0].Approved);
        Assert.Equal(pending.ReceivedAtUtc, report.LastReceivedUtc);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Report(EventReportQuery.ParseFilters(null, null, "X", "UTC")));
    }

    [Fact]
    public async Task Filters_received_cohort_but_keeps_later_decisions_and_zero_daily_buckets()
    {
        await using var fixture = new Fixture();
        await fixture.Initialize();
        var included = fixture.Post(PostSource.Portal, PostStatus.Live, new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc));
        fixture.Post(PostSource.Portal, PostStatus.Live, new(2026, 11, 1, 6, 59, 59, DateTimeKind.Utc));
        fixture.Post(PostSource.Portal, PostStatus.Live, new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc));
        fixture.Post(PostSource.Manual, PostStatus.Live, included.ReceivedAtUtc);
        fixture.Action(included, PostStatus.Live, Now.AddDays(-1));
        await fixture.Database.SaveChangesAsync();
        var filters = EventReportQuery.ParseFilters("2026-11-01", "2026-11-01", "Portal", "America/Los_Angeles");
        Assert.Equal(TimeSpan.FromHours(25), filters.UntilUtc - filters.FromUtc);
        var report = await fixture.Report(filters);
        Assert.Equal(1, report.Counts.Total);
        Assert.Equal("day", report.Granularity);
        Assert.Equal(9, report.Timeline.Count);
        Assert.Equal(0, report.Timeline[1].Intake.Values.Sum());
        Assert.Equal(1, report.Timeline[^1].Approved);
        Assert.Equal("2026-11-01", report.Timeline[0].Label);
        Assert.Equal("2026-11-09", report.Timeline[^1].Label);
    }

    [Fact]
    public async Task Hourly_timeline_distinguishes_repeated_DST_hours_and_fills_gaps()
    {
        await using var fixture = new Fixture();
        await fixture.Initialize();
        fixture.Post(PostSource.Portal, PostStatus.Pending, new(2026, 11, 1, 8, 15, 0, DateTimeKind.Utc));
        fixture.Post(PostSource.Portal, PostStatus.Pending, new(2026, 11, 1, 9, 15, 0, DateTimeKind.Utc));
        fixture.Post(PostSource.Portal, PostStatus.Pending, new(2026, 11, 1, 11, 15, 0, DateTimeKind.Utc));
        await fixture.Database.SaveChangesAsync();
        var report = await fixture.Report(EventReportQuery.ParseFilters(null, null, null, "America/Los_Angeles"));
        Assert.Equal("hour", report.Granularity);
        Assert.Equal(4, report.Timeline.Count);
        Assert.Equal("2026-11-01 01:00 -07:00", report.Timeline[0].Label);
        Assert.Equal("2026-11-01 01:00 -08:00", report.Timeline[1].Label);
        Assert.Empty(report.Timeline[2].Intake);
        Assert.Equal(DateTimeKind.Utc, report.Timeline[0].StartUtc.Kind);
    }

    [Theory]
    [InlineData("bad", null, null, "UTC")]
    [InlineData("2026-11-10", "2026-11-01", null, "UTC")]
    [InlineData(null, null, "0", "UTC")]
    [InlineData(null, null, "Unknown", "UTC")]
    [InlineData(null, null, null, "NoSuch/Zone")]
    public void Invalid_filters_fail_explicitly(string? from, string? through, string? source, string zone) =>
        Assert.Throws<ArgumentException>(() => EventReportQuery.ParseFilters(from, through, source, zone));

    [Fact]
    public void Spring_forward_date_covers_one_local_calendar_day_not_twenty_four_hours()
    {
        var filters = EventReportQuery.ParseFilters("2026-03-08", "2026-03-08", null, "America/Los_Angeles");
        Assert.Equal(new DateTime(2026, 3, 8, 8, 0, 0, DateTimeKind.Utc), filters.FromUtc);
        Assert.Equal(new DateTime(2026, 3, 9, 7, 0, 0, DateTimeKind.Utc), filters.UntilUtc);
    }

    [Fact]
    public async Task Empty_reports_are_honest_demo_sources_are_included_and_cancellation_propagates()
    {
        await using var fixture = new Fixture(demo: true);
        await fixture.Initialize();
        var empty = await fixture.Report();
        Assert.Empty(empty.ExcludedSources);
        Assert.Equal(7, empty.Platforms.Count);
        Assert.Null(empty.Counts.MedianDecisionSeconds);
        Assert.Null(empty.Counts.ApprovalRate);
        Assert.Empty(empty.Timeline);
        var post = fixture.Post(PostSource.X, PostStatus.Live, Now.AddSeconds(-10));
        fixture.Action(post, PostStatus.Live, Now.AddSeconds(-11));
        fixture.Action(post, PostStatus.Live, Now.AddSeconds(-1));
        await fixture.Database.SaveChangesAsync();
        var report = await fixture.Report();
        Assert.Equal(1, report.Counts.Total);
        Assert.Equal(1, report.Counts.EverApproved);
        Assert.Null(report.Counts.MedianDecisionSeconds);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EventReportQuery.BuildAsync(fixture.Database, fixture.Configuration, new(), EventReportQuery.ParseFilters(null, null, null, "UTC"), cancellation.Token));
    }

    [Fact]
    public async Task Fractional_offset_zones_show_real_hour_boundaries_without_invented_clock_times()
    {
        await using var fixture = new Fixture();
        await fixture.Initialize();
        fixture.Post(PostSource.Portal, PostStatus.Pending, new(2026, 11, 1, 8, 15, 0, DateTimeKind.Utc));
        fixture.Post(PostSource.Portal, PostStatus.Pending, new(2026, 11, 1, 10, 15, 0, DateTimeKind.Utc));
        await fixture.Database.SaveChangesAsync();
        var report = await fixture.Report(EventReportQuery.ParseFilters(null, null, null, "Asia/Kolkata"));
        Assert.Equal(3, report.Timeline.Count);
        Assert.Equal("2026-11-01 13:30 +05:30", report.Timeline[0].Label);
        Assert.Equal(0, report.Timeline[1].Intake.Values.Sum());
        Assert.Equal(2, report.Timeline.Sum(item => item.Intake.Values.Sum()));
    }

    [Fact]
    public async Task Fifty_thousand_posts_keep_constant_query_count_and_aggregate_payload_under_one_megabyte()
    {
        await using var fixture = new Fixture(demo: true);
        await fixture.Initialize();
        fixture.Counter.Commands = 0;
        await fixture.Report();
        var emptyQueries = fixture.Counter.Commands;
        await fixture.Database.Database.ExecuteSqlRawAsync("""
            WITH RECURSIVE numbers(x) AS (SELECT 0 UNION ALL SELECT x+1 FROM numbers WHERE x<49999)
            INSERT INTO Posts (Id,EventSlug,Source,ExternalId,Text,AuthorName,ReceivedAtUtc,Status,Version,ImagesJson,FilterReasonsJson)
            SELECT printf('%08x-0000-0000-0000-000000000000',x), 'event',
              CASE x%7 WHEN 0 THEN 'Portal' WHEN 1 THEN 'Manual' WHEN 2 THEN 'Twitch' WHEN 3 THEN 'YouTube'
                WHEN 4 THEN 'X' WHEN 5 THEN 'Bluesky' ELSE 'Mastodon' END,
              CAST(x AS TEXT), 'Must not appear in report', 'Private author',
              datetime('2026-10-11',printf('+%d seconds',x*51)),
              CASE x%4 WHEN 0 THEN 'Live' WHEN 1 THEN 'Pending' WHEN 2 THEN 'Rejected' ELSE 'Withdrawn' END, 0,'[]','[]'
            FROM numbers;
            INSERT INTO ModerationActions (Id,PostId,FromStatus,ToStatus,CreatedAtUtc,StaffSessionId,StaffUsername)
            SELECT Id,Id,'Pending','Live',datetime(ReceivedAtUtc,'+60 seconds'),'staff','Admin' FROM Posts WHERE Status='Live';
            """);
        fixture.Counter.Commands = 0;
        var watch = Stopwatch.StartNew();
        var report = await fixture.Report();
        watch.Stop();
        Assert.Equal(emptyQueries, fixture.Counter.Commands);
        Assert.Equal(5, fixture.Counter.Commands);
        Assert.Equal(50000, report.Counts.Total);
        Assert.All(report.Platforms, item => Assert.InRange(item.Counts.Total, 7142, 7143));
        Assert.Equal(12500, report.Counts.Live);
        Assert.Equal(12500, report.Counts.DecisionSamples);
        Assert.Equal(60, report.Counts.P95DecisionSeconds);
        Assert.Equal(50000, report.Timeline.Sum(item => item.Intake.Values.Sum()));
        Assert.Equal(12500, report.Timeline.Sum(item => item.Approved));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report);
        Assert.InRange(bytes.Length, 1, 999999);
        Assert.DoesNotContain("Private author", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("Must not appear", System.Text.Encoding.UTF8.GetString(bytes));
        output.WriteLine($"50,000 posts: {watch.ElapsedMilliseconds}ms, {fixture.Counter.Commands} SQL queries, {bytes.Length} bytes.");
    }

    private sealed class Counter : DbCommandInterceptor
    {
        public int Commands { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public Counter Counter { get; } = new();
        public EventWallDbContext Database { get; }
        public IConfiguration Configuration { get; }
        public Fixture(bool demo = false)
        {
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Enabled"] = demo.ToString() }).Build();
            Database = new(new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection).AddInterceptors(Counter).Options);
        }
        public async Task Initialize()
        {
            await connection.OpenAsync();
            await Database.Database.EnsureCreatedAsync();
            Database.Events.Add(new() { Slug = "event", Name = "Report event", SubmissionsOpen = true });
            await Database.SaveChangesAsync();
        }
        public EventPost Post(PostSource source, PostStatus status, DateTime received)
        {
            var post = new EventPost { Id = Guid.NewGuid(), EventSlug = "event", Source = source, Status = status,
                ExternalId = Guid.NewGuid().ToString(), Text = "Private text", AuthorName = "Author", ReceivedAtUtc = received };
            Database.Posts.Add(post);
            return post;
        }
        public void Action(EventPost post, PostStatus to, DateTime at, string session = "staff") =>
            Database.ModerationActions.Add(new() { Id = Guid.NewGuid(), PostId = post.Id, FromStatus = PostStatus.Pending,
                ToStatus = to, CreatedAtUtc = at, StaffSessionId = session, StaffUsername = session == "staff" ? "Admin" : null });
        public Task<EventReportDto> Report(ReportFilters? filters = null) =>
            EventReportQuery.BuildAsync(Database, Configuration, new(), filters ?? EventReportQuery.ParseFilters(null, null, null, "UTC"), default, Now);
        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
