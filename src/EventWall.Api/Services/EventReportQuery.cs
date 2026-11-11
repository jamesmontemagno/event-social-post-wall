using System.Globalization;
using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public static class EventReportQuery
{
    public static ReportFilters ParseFilters(string? from, string? through, string? source, string? timeZone)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone ?? "UTC"); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("Choose a supported time zone."); }
        catch (InvalidTimeZoneException) { throw new ArgumentException("Choose a supported time zone."); }
        DateTime? Boundary(string? value, bool end)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                date == DateOnly.MaxValue)
                throw new ArgumentException("Dates must use yyyy-MM-dd and be before 9999-12-31.");
            var local = (end ? date.AddDays(1) : date).ToDateTime(TimeOnly.MinValue);
            if (zone.IsInvalidTime(local))
                throw new ArgumentException("This date starts at a nonexistent local time. Choose another date.");
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }
        var start = Boundary(from, false);
        var until = Boundary(through, true);
        if (start >= until) throw new ArgumentException("The end date must not precede the start date.");
        PostSource? platform = null;
        if (!string.IsNullOrEmpty(source))
        {
            if (!Enum.TryParse<PostSource>(source, out var parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != source)
                throw new ArgumentException("Choose a known platform.");
            platform = parsed;
        }
        return new(start, until, platform, zone.Id);
    }

    public static async Task<EventReportDto> BuildAsync(EventWallDbContext database, IConfiguration configuration,
        SourceHealthRegistry health, ReportFilters filters, CancellationToken cancellationToken,
        DateTime? generatedAtUtc = null)
    {
        var now = generatedAtUtc ?? DateTime.UtcNow;
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var eligible = Enum.GetValues<PostSource>().Where(source => source is PostSource.Portal or PostSource.Manual ||
            DemoMode.IsEnabled(configuration) || configuration.GetValue<bool>($"Social:{source}:AnalyticsApproved")).ToArray();
        if (filters.Source is { } requested && !eligible.Contains(requested))
            throw new ArgumentException("Analytics are not approved for this platform.");
        var selected = eligible.Where(source => filters.Source is null || filters.Source == source).ToArray();
        var posts = database.Posts.AsNoTracking().Where(post => post.EventSlug == settings.Slug &&
            selected.Contains(post.Source) && post.ReceivedAtUtc <= now);
        if (filters.FromUtc is { } from) posts = posts.Where(post => post.ReceivedAtUtc >= from);
        if (filters.UntilUtc is { } until) posts = posts.Where(post => post.ReceivedAtUtc < until);

        var counts = new ReportCounts();
        var platforms = selected.ToDictionary(source => source, _ => new ReportCounts());
        var findings = Enum.GetValues<PostFilterReason>().ToDictionary(reason => reason, _ => 0);
        var ageCounts = new int[4];
        var zone = TimeZoneInfo.FindSystemTimeZoneById(filters.TimeZone);
        var hourly = new SortedDictionary<DateTime, ReportTimeline>();
        var daily = new SortedDictionary<DateTime, ReportTimeline>();
        DateTime? first = null, last = null, firstActivity = null, lastActivity = null;
        var decisions = new Dictionary<Guid, (PostSource Source, DateTime Received, DateTime? First, int Staff, bool Approved, bool Rejected, bool Withdrawn)>();
        ReportTimeline Bucket(SortedDictionary<DateTime, ReportTimeline> buckets, DateTime utc, bool day)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(utc), zone);
            var boundary = local.Date;
            var key = day ? boundary : new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new ReportTimeline
                {
                    StartUtc = day ? DayStart(boundary, zone) : key,
                    Label = day ? local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) :
                        TimeZoneInfo.ConvertTime(new DateTimeOffset(key), zone).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)
                };
                buckets.Add(key, bucket);
            }
            return bucket;
        }
        void At(DateTime utc, Action<ReportTimeline> update)
        {
            firstActivity = firstActivity is null || utc < firstActivity ? utc : firstActivity;
            lastActivity = lastActivity is null || utc > lastActivity ? utc : lastActivity;
            update(Bucket(hourly, utc, false));
            update(Bucket(daily, utc, true));
        }
        await foreach (var post in posts.Select(post => new
        {
            post.Id, post.Source, post.Status, post.ReceivedAtUtc, post.ImagesJson, post.FilterReasonsJson
        }).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            first = first is null || post.ReceivedAtUtc < first ? post.ReceivedAtUtc : first;
            last = last is null || post.ReceivedAtUtc > last ? post.ReceivedAtUtc : last;
            using var images = JsonDocument.Parse(post.ImagesJson);
            var imageCount = images.RootElement.GetArrayLength();
            var reasons = JsonSerializer.Deserialize<PostFilterReason[]>(post.FilterReasonsJson)
                ?? throw new InvalidOperationException("Post filter findings are invalid.");
            foreach (var reason in reasons.Distinct()) findings[reason]++;
            var age = Math.Max(0, (now - post.ReceivedAtUtc).TotalSeconds);
            if (post.Status == PostStatus.Pending) ageCounts[age < 300 ? 0 : age < 1800 ? 1 : age < 7200 ? 2 : 3]++;
            foreach (var target in new[] { counts, platforms[post.Source] })
            {
                target.Total++;
                switch (post.Status)
                {
                    case PostStatus.Pending:
                        target.Pending++;
                        target.OldestPendingSeconds = Math.Max(target.OldestPendingSeconds ?? 0, age);
                        break;
                    case PostStatus.Live: target.Live++; break;
                    case PostStatus.Rejected: target.Rejected++; break;
                    case PostStatus.Withdrawn: target.Withdrawn++; break;
                }
                target.Images += imageCount;
                if (imageCount > 0) target.ImagePosts++;
                if (reasons.Length > 0) target.FlaggedPosts++;
            }
            decisions.Add(post.Id, (post.Source, post.ReceivedAtUtc, null, 0, false, false, false));
            At(post.ReceivedAtUtc, bucket => bucket.Intake[post.Source] = bucket.Intake.GetValueOrDefault(post.Source) + 1);
        }
        var reviewers = new Dictionary<string, (int Approved, int Rejected, int Withdrawn)>();
        var actions = posts.Join(database.ModerationActions.AsNoTracking().Where(action => action.CreatedAtUtc <= now),
            post => post.Id, action => action.PostId, (post, action) => new
            {
                post.Id, action.CreatedAtUtc, action.ToStatus, action.StaffSessionId, action.StaffUsername
            });
        await foreach (var action in actions.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            // A concurrent ingestion may become visible between the two read-only queries.
            if (!decisions.TryGetValue(action.Id, out var item)) continue;
            var automatic = action.StaffSessionId.StartsWith("source:", StringComparison.Ordinal);
            var filter = action.StaffSessionId == "source:filter";
            foreach (var target in new[] { counts, platforms[item.Source] })
            {
                if (filter) target.FilterActions++;
                else if (automatic) target.SourceActions++;
                else target.StaffActions++;
                if (!item.Approved && action.ToStatus == PostStatus.Live) target.EverApproved++;
                if (!item.Rejected && action.ToStatus == PostStatus.Rejected) target.EverRejected++;
                if (!item.Withdrawn && action.ToStatus == PostStatus.Withdrawn) target.EverWithdrawn++;
                if (!automatic && item.Staff > 0) target.RepeatStaffActions++;
            }
            if (!automatic)
            {
                item.Staff++;
                if (item.First is null || action.CreatedAtUtc < item.First)
                    item.First = action.CreatedAtUtc;
                var name = action.StaffUsername ?? (action.StaffSessionId == "demo-seed" ? "Simulated demo staff" : "Unattributed / legacy staff");
                var reviewer = reviewers.GetValueOrDefault(name);
                if (action.ToStatus == PostStatus.Live) reviewer.Approved++;
                if (action.ToStatus == PostStatus.Rejected) reviewer.Rejected++;
                if (action.ToStatus == PostStatus.Withdrawn) reviewer.Withdrawn++;
                reviewers[name] = reviewer;
            }
            item.Approved |= action.ToStatus == PostStatus.Live;
            item.Rejected |= action.ToStatus == PostStatus.Rejected;
            item.Withdrawn |= action.ToStatus == PostStatus.Withdrawn;
            decisions[action.Id] = item;
            At(action.CreatedAtUtc, bucket =>
            {
                if (filter) bucket.FilterActions++;
                else if (automatic) bucket.SourceActions++;
                else if (action.ToStatus == PostStatus.Live) bucket.Approved++;
                else if (action.ToStatus == PostStatus.Rejected) bucket.Rejected++;
                else if (action.ToStatus == PostStatus.Withdrawn) bucket.Withdrawn++;
            });
        }
        void Latencies(ReportCounts target, PostSource? source)
        {
            var values = decisions.Values.Where(item => item.First >= item.Received && (source is null || item.Source == source))
                .Select(item => (item.First!.Value - item.Received).TotalSeconds).Order().ToArray();
            target.DecisionSamples = values.Length;
            target.MedianDecisionSeconds = Percentile(values, .5);
            target.P95DecisionSeconds = Percentile(values, .95);
        }
        Latencies(counts, null);
        foreach (var source in selected) Latencies(platforms[source], source);
        var blocks = await database.BlockedAuthors.AsNoTracking()
            .Where(item => item.EventSlug == settings.Slug && selected.Contains(item.Source))
            .GroupBy(item => item.Source).Select(group => new { Source = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Source, item => item.Count, cancellationToken);
        var promos = await database.Promos.AsNoTracking().Where(item => item.EventSlug == settings.Slug)
            .GroupBy(item => item.Active).Select(group => new { Active = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var useHours = firstActivity is not null && lastActivity - firstActivity <= TimeSpan.FromDays(3);
        var timeline = useHours ? hourly : daily;
        if (firstActivity is not null && lastActivity is not null)
        {
            if (useHours)
            {
                var end = hourly.Keys.Last();
                for (var point = hourly.Keys.First(); point <= end; point = point.AddHours(1))
                    Bucket(hourly, point, false);
            }
            else
            {
                var end = daily.Keys.Last();
                for (var date = daily.Keys.First(); date <= end; date = date.AddDays(1))
                {
                    if (!daily.ContainsKey(date))
                    {
                        var start = DayStart(date, zone);
                        if (TimeZoneInfo.ConvertTimeFromUtc(start, zone).Date == date)
                            daily.Add(date, new ReportTimeline { StartUtc = start, Label = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
                    }
                }
            }
        }
        return new(now, settings.Name, filters, eligible, Enum.GetValues<PostSource>().Except(eligible).ToArray(),
            counts, selected.Select(source => new ReportPlatform(source, platforms[source], blocks.GetValueOrDefault(source))).ToArray(),
            Utc(first), Utc(last), useHours ? "hour" : "day", timeline.Values.ToArray(),
            findings.Select(item => new ReportFinding(item.Key, item.Value)).ToArray(),
            new[] { "Under 5 minutes", "5-30 minutes", "30 minutes-2 hours", "2 hours or more" }
                .Select((label, index) => new ReportAge(label, ageCounts[index])).ToArray(),
            reviewers.OrderByDescending(item => item.Value.Approved + item.Value.Rejected + item.Value.Withdrawn)
                .ThenBy(item => item.Key).Select(item => new ReportReviewer(item.Key, item.Value.Approved, item.Value.Rejected, item.Value.Withdrawn)).ToArray(),
            new(settings.SubmissionsOpen, promos.Where(item => item.Active).Sum(item => item.Count), promos.Sum(item => item.Count),
                SourceSetupStatus.Build(configuration, health, settings.YouTubeCollectionRunning)));
    }

    private static DateTime DayStart(DateTime date, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(date)) date = date.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(date, zone);
    }
    private static DateTime? Utc(DateTime? date) => date is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
    private static double? Percentile(double[] sorted, double percentile) =>
        sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
}
