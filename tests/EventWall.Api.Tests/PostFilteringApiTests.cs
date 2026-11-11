using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EventWall.Api.Tests;

public sealed class PostFilteringApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Portal_manual_and_social_posts_share_rejection_and_staff_can_override()
    {
        await using var fixture = new Fixture();
        await fixture.Login();
        await fixture.PrepareSubmission();
        var id = Guid.NewGuid();
        using var submission = new HttpRequestMessage(HttpMethod.Post, "/api/public/submissions")
        {
            Content = JsonContent.Create(new { text = "Hello", displayName = "Viewer" })
        };
        submission.Headers.Add("Idempotency-Key", id.ToString());
        var received = await fixture.Submission.SendAsync(submission);
        Assert.Equal(HttpStatusCode.Accepted, received.StatusCode);
        Assert.Equal("{\"received\":true}", await received.Content.ReadAsStringAsync());
        using var duplicate = new HttpRequestMessage(HttpMethod.Post, "/api/public/submissions")
        {
            Content = JsonContent.Create(new { text = "Hello", displayName = "Viewer" })
        };
        duplicate.Headers.Add("Idempotency-Key", id.ToString());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await fixture.Submission.SendAsync(duplicate)).StatusCode);
        var manual = await fixture.Admin.PostAsJsonAsync("/api/admin/manual", new { text = "Hello", authorName = "Staff" });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
        Assert.Equal(PostStatus.Rejected, (await manual.Content.ReadFromJsonAsync<StaffPostDto>(Json))!.Status);
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "social", "Hello #question", "Viewer", "id", null, null, default);
        Assert.Empty((await fixture.Admin.GetFromJsonAsync<StaffPostDto[]>("/api/admin/queue?status=Pending", Json))!);
        var rejected = (await fixture.Admin.GetFromJsonAsync<StaffPostDto[]>("/api/admin/queue?status=Rejected", Json))!;
        Assert.Equal(3, rejected.Length);
        Assert.All(rejected.Where(post => post.Source != PostSource.Manual),
            post => Assert.Equal([PostFilterReason.SingleWord], post.FilterReasons));
        Assert.Equal([PostFilterReason.SingleWord, PostFilterReason.DuplicateText],
            rejected.Single(post => post.Source == PostSource.Manual).FilterReasons);
        var social = rejected.Single(post => post.Source == PostSource.Twitch);
        var wall = (await fixture.Public.GetFromJsonAsync<WallSnapshotDto>("/api/public/wall", Json))!;
        Assert.Empty(wall.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Public.GetAsync($"/api/public/wall/items/{social.Id}")).StatusCode);
        Assert.Empty((await fixture.Public.GetFromJsonAsync<WallChangesDto>(
            $"/api/public/wall/changes?sinceRevision=0&knownIds={social.Id}", Json))!.LiveKnownIds);

        var automaticAnalytics = await fixture.Admin.GetFromJsonAsync<JsonElement>("/api/admin/analytics");
        Assert.Equal(JsonValueKind.Null, automaticAnalytics.GetProperty("counts").GetProperty("medianDecisionSeconds").ValueKind);
        var approved = await fixture.Admin.PostAsJsonAsync($"/api/admin/posts/{social.Id}/approve", new { version = social.Version });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "social", "Hello #question", "Viewer", "id", null, null, default);
        var visible = await fixture.Public.GetStringAsync("/api/public/wall");
        Assert.Contains("Hello", visible);
        Assert.DoesNotContain("filterReasons", visible);
        Assert.DoesNotContain("externalId", visible);
        var humanAnalytics = await fixture.Admin.GetFromJsonAsync<JsonElement>("/api/admin/analytics");
        Assert.Equal(JsonValueKind.Null, humanAnalytics.GetProperty("counts").GetProperty("medianDecisionSeconds").ValueKind);
        // Social analytics stay policy-gated; portal/manual approvals count as human decisions.
        var portal = rejected.Single(post => post.Source == PostSource.Portal);
        (await fixture.Admin.PostAsJsonAsync($"/api/admin/posts/{portal.Id}/approve", new { version = portal.Version })).EnsureSuccessStatusCode();
        humanAnalytics = await fixture.Admin.GetFromJsonAsync<JsonElement>("/api/admin/analytics");
        Assert.Equal(JsonValueKind.Number, humanAnalytics.GetProperty("counts").GetProperty("medianDecisionSeconds").ValueKind);
        await using var database = fixture.CreateDbContext();
        Assert.Equal(3, await database.Posts.CountAsync());
        Assert.Equal(3, await database.ModerationActions.CountAsync(action => action.StaffSessionId.StartsWith("source:")));
        Assert.Equal(2, await database.ModerationActions.CountAsync(action => !action.StaffSessionId.StartsWith("source:")));
    }

    [Fact]
    public async Task Cross_posted_text_is_rejected_with_a_reason_and_staff_can_override_or_disable_the_rule()
    {
        await using var fixture = new Fixture();
        await fixture.Login();
        await fixture.PrepareSubmission();
        const string text = "Can Aspire run on Azure? #question #communityevent";
        await fixture.Ingestor.IngestAsync(PostSource.X, "first", text, "Original", "first", null, null, default);
        using var submission = new HttpRequestMessage(HttpMethod.Post, "/api/public/submissions")
        {
            Content = JsonContent.Create(new { text, displayName = "Attendee" })
        };
        submission.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Accepted, (await fixture.Submission.SendAsync(submission)).StatusCode);
        var manual = await fixture.Admin.PostAsJsonAsync("/api/admin/manual", new { text, authorName = "Staff" });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
        Assert.Equal([PostFilterReason.DuplicateText],
            (await manual.Content.ReadFromJsonAsync<StaffPostDto>(Json))!.FilterReasons);
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "copy", text, "Different author", "second",
            null, null, default);

        var pending = (await fixture.Admin.GetFromJsonAsync<StaffPostDto[]>("/api/admin/queue?status=Pending", Json))!;
        Assert.Equal("first", Assert.Single(pending).ExternalId);
        var rejected = (await fixture.Admin.GetFromJsonAsync<StaffPostDto[]>("/api/admin/queue?status=Rejected", Json))!;
        Assert.Equal(3, rejected.Length);
        Assert.All(rejected, post => Assert.Equal([PostFilterReason.DuplicateText], post.FilterReasons));
        Assert.Empty((await fixture.Public.GetFromJsonAsync<WallSnapshotDto>("/api/public/wall", Json))!.Items);

        var copy = rejected.Single(post => post.Source == PostSource.Twitch);
        (await fixture.Admin.PostAsJsonAsync($"/api/admin/posts/{copy.Id}/approve", new { version = copy.Version }))
            .EnsureSuccessStatusCode();
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "copy", text, "Different author", "second",
            null, null, default);
        var live = (await fixture.Admin.GetFromJsonAsync<StaffPostDto[]>("/api/admin/queue?status=Live", Json))!;
        Assert.Equal(copy.Id, Assert.Single(live).Id);
        Assert.Equal([PostFilterReason.DuplicateText], live[0].FilterReasons);

        var settings = (await fixture.Admin.GetFromJsonAsync<AdminEventDto>("/api/admin/settings", Json))!;
        Assert.True(settings.PostFilters.DuplicateText);
        (await fixture.Admin.PostAsJsonAsync("/api/admin/settings/filters", new
        {
            postFilters = settings.PostFilters with { DuplicateText = false }, version = settings.FilterSettingsVersion
        })).EnsureSuccessStatusCode();
        await fixture.Ingestor.IngestAsync(PostSource.Bluesky, "after-disable", text, "Viewer", "third",
            null, null, default);

        await using var database = fixture.CreateDbContext();
        Assert.Equal(PostStatus.Live, (await database.Posts.SingleAsync(post => post.Id == copy.Id)).Status);
        Assert.Equal(PostStatus.Pending, (await database.Posts.SingleAsync(post => post.ExternalId == "after-disable")).Status);
        Assert.Equal(3, await database.ModerationActions.CountAsync(action => action.StaffSessionId == "source:filter"));
    }

    [Fact]
    public async Task Concurrent_arrivals_from_separate_deployments_leave_only_one_pending()
    {
        await using var fixture = new Fixture();
        await fixture.Login();
        await fixture.PrepareSubmission();
        const string text = "Can Aspire run on Azure? #question #communityevent";
        using var start = new ManualResetEventSlim();
        var social = Task.Run(async () =>
        {
            start.Wait();
            await fixture.Ingestor.IngestAsync(PostSource.X, "social", text, "Social", "one", null, null, default);
        });
        var chat = Task.Run(async () =>
        {
            start.Wait();
            await fixture.Ingestor.IngestAsync(PostSource.Twitch, "chat", text, "Chat", "two", null, null, default);
        });
        var manual = Task.Run(async () =>
        {
            start.Wait();
            (await fixture.Admin.PostAsJsonAsync("/api/admin/manual", new { text, authorName = "Staff" }))
                .EnsureSuccessStatusCode();
        });
        var portal = Task.Run(async () =>
        {
            start.Wait();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/submissions")
            {
                Content = JsonContent.Create(new { text, displayName = "Attendee" })
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            (await fixture.Submission.SendAsync(request)).EnsureSuccessStatusCode();
        });
        start.Set();
        await Task.WhenAll(social, chat, manual, portal);

        await using var database = fixture.CreateDbContext();
        var posts = await database.Posts.ToListAsync();
        Assert.Equal(4, posts.Count);
        Assert.Single(posts, post => post.Status == PostStatus.Pending);
        Assert.Equal(3, posts.Count(post => post.Status == PostStatus.Rejected &&
            post.FilterReasons.SequenceEqual([PostFilterReason.DuplicateText])));
        Assert.Equal(3, await database.ModerationActions.CountAsync());
    }

    [Fact]
    public async Task Settings_are_private_validated_versioned_and_do_not_retroactively_moderate()
    {
        await using var fixture = new Fixture();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Admin.GetAsync("/api/admin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Public.GetAsync("/api/admin/settings")).StatusCode);
        Assert.DoesNotContain("postFilters", await fixture.Public.GetStringAsync("/api/public/event"));
        await fixture.Login();
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "rejected", "Hello #question", "Viewer", "id", null, null, default);
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "pending", "A useful question #question", "Viewer", "id", null, null, default);
        var before = (await fixture.Admin.GetFromJsonAsync<AdminEventDto>("/api/admin/settings", Json))!;
        var settings = PostFilteringTests.Disabled with { BlockedTerms = [" Spoiler ", "spoiler"] };
        var saved = await fixture.Admin.PostAsJsonAsync("/api/admin/settings/filters",
            new { postFilters = settings, version = before.FilterSettingsVersion });
        saved.EnsureSuccessStatusCode();
        var after = (await saved.Content.ReadFromJsonAsync<AdminEventDto>(Json))!;
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.FilterSettingsVersion + 1, after.FilterSettingsVersion);
        Assert.Equal(["spoiler"], after.PostFilters.BlockedTerms);
        var stale = await fixture.Admin.PostAsJsonAsync("/api/admin/settings/filters",
            new { postFilters = settings, version = before.FilterSettingsVersion });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var invalid = await fixture.Admin.PostAsJsonAsync("/api/admin/settings/filters",
            new { postFilters = settings with { AllowedTerms = ["spoiler"] }, version = after.FilterSettingsVersion });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var missing = await fixture.Admin.PostAsJsonAsync("/api/admin/settings/filters",
            new { version = after.FilterSettingsVersion });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        fixture.Admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Admin.PostAsJsonAsync(
            "/api/admin/settings/filters", new { postFilters = settings, version = after.FilterSettingsVersion })).StatusCode);
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "future", "Hello #question", "Viewer", "id", null, null, default);
        await using var database = fixture.CreateDbContext();
        Assert.Equal(PostStatus.Rejected, (await database.Posts.SingleAsync(post => post.ExternalId == "rejected")).Status);
        Assert.Equal(PostStatus.Pending, (await database.Posts.SingleAsync(post => post.ExternalId == "pending")).Status);
        Assert.Equal(PostStatus.Pending, (await database.Posts.SingleAsync(post => post.ExternalId == "future")).Status);
        Assert.Equal(0, (await database.Events.SingleAsync()).Revision);
    }

    [Fact]
    public async Task Staff_exports_include_codes_without_bypassing_source_content_policy()
    {
        await using var fixture = new Fixture();
        await fixture.Login();
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "private-source-id", "Hello #question", "Secret attribution",
            "id", null, null, default);
        var json = await fixture.Admin.GetStringAsync("/api/admin/export.json");
        Assert.Contains("SingleWord", json);
        Assert.DoesNotContain("Hello", json);
        Assert.DoesNotContain("Secret attribution", json);
        Assert.DoesNotContain("private-source-id", json);
        var csv = await fixture.Admin.GetStringAsync("/api/admin/export.csv");
        Assert.Contains("filterReasons", csv);
        Assert.Contains("SingleWord", csv);
        Assert.DoesNotContain("Secret attribution", csv);
    }

    [Fact]
    public async Task Blocked_authors_stay_rejected_even_with_all_rules_disabled()
    {
        await using var fixture = new Fixture();
        await fixture.Login();
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "original", "A useful question #question", "Viewer", "account", null, null, default);
        var original = (await fixture.Admin.GetFromJsonAsync<StaffPostDto[]>("/api/admin/queue?status=Pending", Json))!.Single();
        (await fixture.Admin.PostAsJsonAsync($"/api/admin/posts/{original.Id}/block-author", new { version = 0 })).EnsureSuccessStatusCode();
        (await fixture.Admin.PostAsJsonAsync("/api/admin/settings/filters",
            new { postFilters = PostFilteringTests.Disabled, version = 0 })).EnsureSuccessStatusCode();
        await fixture.Ingestor.IngestAsync(PostSource.Twitch, "future", "Hello #question", "Viewer", "account", null, null, default);
        await using var database = fixture.CreateDbContext();
        var rejected = await database.Posts.SingleAsync(post => post.ExternalId == "future");
        Assert.Equal(PostStatus.Rejected, rejected.Status);
        Assert.Empty(rejected.FilterReasons);
        Assert.Contains("account blocked", (await database.ModerationActions.SingleAsync()).Reason);
    }

    [Fact]
    public async Task Wall_sorts_by_the_selected_date_before_limiting_results()
    {
        await using var fixture = new Fixture();
        var time = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var ids = new List<Guid>();
        var unknown = Guid.NewGuid();
        await using (var database = fixture.CreateDbContext())
        {
            for (var index = 0; index < 102; index++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                database.Posts.Add(new EventPost
                {
                    Id = id, EventSlug = "communityevent", Source = PostSource.X, ExternalId = $"post-{index}",
                    Text = "A useful question", AuthorName = "Viewer", Status = PostStatus.Live,
                    ReceivedAtUtc = time.AddMinutes(-index), SourceCreatedAtUtc = time.AddMinutes(-index),
                    DecidedAtUtc = time.AddMinutes(index), PublishedRevision = 102 - index
                });
            }
            database.Posts.Add(new EventPost
            {
                Id = unknown, EventSlug = "communityevent", Source = PostSource.Manual, ExternalId = "unknown-approval",
                Text = "A useful question", AuthorName = "Viewer", Status = PostStatus.Live,
                ReceivedAtUtc = time.AddDays(1), PublishedRevision = 500
            });
            database.Posts.Add(new EventPost
            {
                Id = Guid.NewGuid(), EventSlug = "communityevent", Source = PostSource.Manual, ExternalId = "pending",
                Text = "A useful question", AuthorName = "Viewer", Status = PostStatus.Pending,
                ReceivedAtUtc = time.AddDays(1)
            });
            (await database.Events.SingleAsync()).Revision = 500;
            await database.SaveChangesAsync();
        }

        var defaultWall = (await fixture.Public.GetFromJsonAsync<WallSnapshotDto>("/api/public/wall", Json))!;
        Assert.Equal(new[] { unknown }.Concat(ids.Take(99)), defaultWall.Items.Select(post => post.Id));
        var approvedWall = (await fixture.Public.GetFromJsonAsync<WallSnapshotDto>("/api/public/wall?sort=approved", Json))!;
        Assert.Equal(ids.Skip(2).Reverse(), approvedWall.Items.Select(post => post.Id));
        var bounded = (await fixture.Public.GetFromJsonAsync<WallSnapshotDto>(
            "/api/public/wall?sort=approved&throughRevision=50", Json))!;
        Assert.Equal(ids.Skip(52).Reverse(), bounded.Items.Select(post => post.Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Public.GetAsync("/api/public/wall?sort=other")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Public.GetAsync(
            "/api/public/wall?afterRevision=1&throughRevision=2")).StatusCode);
    }

    [Fact]
    public async Task Wall_uses_revision_for_approval_ties_and_returns_utc_approval_times()
    {
        await using var fixture = new Fixture();
        var time = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var newest = Create("newest", time.AddTicks(1), 1);
        var tieHigh = Create("tie-high", time, 3);
        var tieLow = Create("tie-low", time, 2);
        var unknown = Create("unknown", null, 100);
        await using (var database = fixture.CreateDbContext())
        {
            database.Posts.AddRange(newest, tieHigh, tieLow, unknown);
            await database.SaveChangesAsync();
        }

        var snapshot = await fixture.Public.GetFromJsonAsync<JsonElement>("/api/public/wall?sort=approved");
        var items = snapshot.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { newest.Id, tieHigh.Id, tieLow.Id, unknown.Id },
            items.Select(item => item.GetProperty("id").GetGuid()));
        var approval = items[0].GetProperty("approvedAtUtc");
        Assert.Equal(time.AddTicks(1), approval.GetDateTime());
        Assert.Equal(DateTimeKind.Utc, approval.GetDateTime().Kind);
        Assert.Equal(DateTimeKind.Utc, items[0].GetProperty("receivedAtUtc").GetDateTime().Kind);
        Assert.Equal(JsonValueKind.Null, items[3].GetProperty("approvedAtUtc").ValueKind);
        var single = await fixture.Public.GetFromJsonAsync<JsonElement>($"/api/public/wall/items/{newest.Id}");
        Assert.Equal(approval.GetString(), single.GetProperty("approvedAtUtc").GetString());

        EventPost Create(string id, DateTime? approved, long revision) => new()
        {
            Id = Guid.NewGuid(), EventSlug = "communityevent", Source = PostSource.Manual, ExternalId = id,
            Text = "A useful question", AuthorName = "Viewer", Status = PostStatus.Live,
            ReceivedAtUtc = time, DecidedAtUtc = approved, PublishedRevision = revision
        };
    }

    [Fact]
    public async Task Wall_moves_reapproved_posts_to_the_front_and_still_reports_changes()
    {
        await using var fixture = new Fixture();
        await fixture.Login();
        var old = new EventPost
        {
            Id = Guid.NewGuid(), EventSlug = "communityevent", Source = PostSource.Manual, ExternalId = "old",
            Text = "An older question", AuthorName = "Viewer", Status = PostStatus.Pending,
            ReceivedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        var recent = new EventPost
        {
            Id = Guid.NewGuid(), EventSlug = "communityevent", Source = PostSource.Manual, ExternalId = "recent",
            Text = "A newer question", AuthorName = "Viewer", Status = PostStatus.Pending,
            ReceivedAtUtc = DateTime.UtcNow
        };
        await using (var database = fixture.CreateDbContext())
        {
            database.Posts.AddRange(old, recent);
            await database.SaveChangesAsync();
        }

        (await fixture.Admin.PostAsJsonAsync($"/api/admin/posts/{old.Id}/approve", new { version = 0 })).EnsureSuccessStatusCode();
        (await fixture.Admin.PostAsJsonAsync($"/api/admin/posts/{recent.Id}/approve", new { version = 0 })).EnsureSuccessStatusCode();
        var initial = (await fixture.Public.GetFromJsonAsync<WallSnapshotDto>("/api/public/wall?sort=approved", Json))!;
        Assert.Equal(new[] { recent.Id, old.Id }, initial.Items.Select(post => post.Id));

        (await fixture.Admin.PostAsJsonAsync($"/api/admin/posts/{old.Id}/withdraw", new { version = 1 })).EnsureSuccessStatusCode();
        var withdrawn = (await fixture.Public.GetFromJsonAsync<WallChangesDto>(
            $"/api/public/wall/changes?sinceRevision={initial.Revision}&knownIds={old.Id},{recent.Id}", Json))!;
        Assert.Equal(new[] { recent.Id }, withdrawn.LiveKnownIds);
        (await fixture.Admin.PostAsJsonAsync($"/api/admin/posts/{old.Id}/approve", new { version = 2 })).EnsureSuccessStatusCode();
        var reapproved = (await fixture.Public.GetFromJsonAsync<WallSnapshotDto>("/api/public/wall?sort=approved", Json))!;
        Assert.Equal(new[] { old.Id, recent.Id }, reapproved.Items.Select(post => post.Id));
        var changes = (await fixture.Public.GetFromJsonAsync<WallChangesDto>(
            $"/api/public/wall/changes?sinceRevision={initial.Revision}&knownIds={recent.Id}", Json))!;
        Assert.Equal(1, changes.NewCount);
        Assert.Equal(new[] { recent.Id }, changes.LiveKnownIds);
    }

    [Fact]
    public async Task Wall_reveals_new_approvals_even_when_their_post_dates_are_outside_the_latest_hundred()
    {
        await using var fixture = new Fixture();
        var time = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var newlyApproved = new EventPost
        {
            Id = Guid.NewGuid(), EventSlug = "communityevent", Source = PostSource.X, ExternalId = "old-new-approval",
            Text = "An older question", AuthorName = "Viewer", Status = PostStatus.Live,
            ReceivedAtUtc = time.AddDays(-1), SourceCreatedAtUtc = time.AddDays(-2),
            DecidedAtUtc = time.AddDays(1), PublishedRevision = 101
        };
        await using (var database = fixture.CreateDbContext())
        {
            for (var index = 0; index < 100; index++)
            {
                database.Posts.Add(new EventPost
                {
                    Id = Guid.NewGuid(), EventSlug = "communityevent", Source = PostSource.Manual, ExternalId = $"existing-{index}",
                    Text = "A useful question", AuthorName = "Viewer", Status = PostStatus.Live,
                    ReceivedAtUtc = time.AddMinutes(index), DecidedAtUtc = time.AddMinutes(index),
                    PublishedRevision = index + 1
                });
            }
            database.Posts.Add(newlyApproved);
            (await database.Events.SingleAsync()).Revision = 101;
            await database.SaveChangesAsync();
        }

        var initial = (await fixture.Public.GetFromJsonAsync<WallSnapshotDto>("/api/public/wall?sort=post", Json))!;
        Assert.DoesNotContain(initial.Items, post => post.Id == newlyApproved.Id);
        var refreshed = (await fixture.Public.GetFromJsonAsync<WallSnapshotDto>(
            "/api/public/wall?sort=post&afterRevision=100", Json))!;
        Assert.Equal(1, refreshed.NewCount);
        Assert.Equal(101, refreshed.Items.Count);
        Assert.Equal(newlyApproved.Id, refreshed.Items[^1].Id);
    }

    [Fact]
    public async Task Wall_refresh_does_not_count_selected_posts_against_the_additional_approval_limit()
    {
        await using var fixture = new Fixture();
        var time = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var older = new EventPost
        {
            Id = Guid.NewGuid(), EventSlug = "communityevent", Source = PostSource.X, ExternalId = "older-new",
            Text = "An older question", AuthorName = "Viewer", Status = PostStatus.Live,
            ReceivedAtUtc = time, SourceCreatedAtUtc = time.AddDays(-1),
            DecidedAtUtc = time.AddDays(1), PublishedRevision = 101
        };
        await using (var database = fixture.CreateDbContext())
        {
            database.Posts.Add(older);
            for (var index = 0; index < 100; index++)
            {
                database.Posts.Add(new EventPost
                {
                    Id = Guid.NewGuid(), EventSlug = "communityevent", Source = PostSource.Manual, ExternalId = $"recent-new-{index}",
                    Text = "A useful question", AuthorName = "Viewer", Status = PostStatus.Live,
                    ReceivedAtUtc = time.AddDays(2).AddMinutes(index), DecidedAtUtc = time.AddDays(3).AddMinutes(index),
                    PublishedRevision = 102 + index
                });
            }
            (await database.Events.SingleAsync()).Revision = 201;
            await database.SaveChangesAsync();
        }

        var refreshed = (await fixture.Public.GetFromJsonAsync<WallSnapshotDto>(
            "/api/public/wall?sort=post&afterRevision=100", Json))!;
        Assert.Equal(101, refreshed.NewCount);
        Assert.Equal(101, refreshed.Items.Count);
        Assert.Equal(older.Id, refreshed.Items[^1].Id);
    }

    [Fact]
    public async Task Wall_changes_validates_all_known_posts_including_large_snapshots()
    {
        await using var fixture = new Fixture();
        var ids = new List<Guid>();
        await using (var database = fixture.CreateDbContext())
        {
            for (var index = 0; index < 150; index++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                database.Posts.Add(new EventPost
                {
                    Id = id, EventSlug = "communityevent", Source = PostSource.Manual, ExternalId = $"known-{index}",
                    Text = "A useful question", AuthorName = "Viewer", ReceivedAtUtc = DateTime.UtcNow,
                    Status = index == 75 ? PostStatus.Withdrawn : PostStatus.Live, PublishedRevision = index + 1
                });
            }
            database.Posts.Add(new EventPost
            {
                Id = Guid.NewGuid(), EventSlug = "communityevent", Source = PostSource.Manual, ExternalId = "new",
                Text = "A useful question", AuthorName = "Viewer", ReceivedAtUtc = DateTime.UtcNow,
                Status = PostStatus.Live, PublishedRevision = 151
            });
            (await database.Events.SingleAsync()).Revision = 151;
            await database.SaveChangesAsync();
        }

        var response = await fixture.Public.PostAsJsonAsync("/api/public/wall/changes", new { sinceRevision = 0, knownIds = ids });
        response.EnsureSuccessStatusCode();
        var changes = (await response.Content.ReadFromJsonAsync<WallChangesDto>(Json))!;
        Assert.Equal(1, changes.NewCount);
        Assert.Equal(149, changes.LiveKnownIds.Count);
        Assert.DoesNotContain(ids[75], changes.LiveKnownIds);
        Assert.Contains(ids[^1], changes.LiveKnownIds);
        var bounded = await fixture.Public.PostAsJsonAsync("/api/public/wall/changes",
            new { sinceRevision = 0, knownIds = ids, knownThroughRevision = 100 });
        bounded.EnsureSuccessStatusCode();
        var boundedChanges = (await bounded.Content.ReadFromJsonAsync<WallChangesDto>(Json))!;
        Assert.Equal(99, boundedChanges.LiveKnownIds.Count);
        Assert.Contains(ids[99], boundedChanges.LiveKnownIds);
        Assert.DoesNotContain(ids[100], boundedChanges.LiveKnownIds);
        var legacy = (await fixture.Public.GetFromJsonAsync<WallChangesDto>(
            $"/api/public/wall/changes?sinceRevision=0&knownIds={string.Join(",", ids)}", Json))!;
        Assert.Equal(1, legacy.NewCount);
        Assert.Equal(149, legacy.LiveKnownIds.Count);
        var maximum = ids.Concat(Enumerable.Range(0, 200).Select(_ => Guid.NewGuid())).ToArray();
        var atLimit = await fixture.Public.PostAsJsonAsync("/api/public/wall/changes",
            new { sinceRevision = 0, knownIds = maximum });
        atLimit.EnsureSuccessStatusCode();
        Assert.Equal(149, (await atLimit.Content.ReadFromJsonAsync<WallChangesDto>(Json))!.LiveKnownIds.Count);
        var tooMany = await fixture.Public.PostAsJsonAsync("/api/public/wall/changes",
            new { sinceRevision = 0, knownIds = Enumerable.Range(0, 351).Select(_ => Guid.NewGuid()) });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Public.PostAsJsonAsync(
            "/api/public/wall/changes", new { sinceRevision = 0, knownIds = ids, knownThroughRevision = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Public.PostAsJsonAsync(
            "/api/public/wall/changes", new { sinceRevision = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Admin.PostAsJsonAsync(
            "/api/public/wall/changes", new { sinceRevision = 0, knownIds = ids })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Submission.PostAsJsonAsync(
            "/api/public/wall/changes", new { sinceRevision = 0, knownIds = ids })).StatusCode);
    }

    [Theory]
    [InlineData(PostStatus.Pending)]
    [InlineData(PostStatus.Live)]
    public async Task Admin_queue_orders_by_newest_source_time_with_received_time_fallback_and_returns_utc_timestamps(PostStatus status)
    {
        await using var fixture = new Fixture();
        await fixture.Login();
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        await using (var database = fixture.CreateDbContext())
        {
            database.Posts.AddRange(
                Create("old-source", PostSource.X, now.AddMinutes(-1), now.AddDays(-2)),
                Create("new-source", PostSource.X, now.AddMinutes(-10), now.AddMinutes(-5)),
                Create("portal", PostSource.Portal, now.AddMinutes(-6), null),
                Create("old-received", PostSource.Manual, now.AddDays(-3), null));
            await database.SaveChangesAsync();
        }
        var posts = (await fixture.Admin.GetFromJsonAsync<StaffPostDto[]>($"/api/admin/queue?status={status}", Json))!;
        Assert.Equal(["new-source", "portal", "old-source", "old-received"], posts.Select(post => post.ExternalId));
        Assert.All(posts, post => Assert.Equal(DateTimeKind.Utc, post.ReceivedAtUtc.Kind));
        Assert.Equal(now.AddMinutes(-5), posts[0].SourceCreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, posts[0].SourceCreatedAtUtc!.Value.Kind);
        Assert.Null(posts[1].SourceCreatedAtUtc);
        Assert.All(posts, post =>
        {
            if (status == PostStatus.Live)
            {
                Assert.Equal(now, post.DecidedAtUtc);
                Assert.Equal(DateTimeKind.Utc, post.DecidedAtUtc!.Value.Kind);
            }
            else
            {
                Assert.Null(post.DecidedAtUtc);
            }
        });

        EventPost Create(string id, PostSource source, DateTime received, DateTime? created) => new()
        {
            Id = Guid.NewGuid(), EventSlug = "communityevent", Source = source, ExternalId = id,
            Text = "A useful question", AuthorName = "Viewer", Status = status,
            ReceivedAtUtc = received, SourceCreatedAtUtc = created,
            DecidedAtUtc = status == PostStatus.Live ? now : null
        };
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"eventwall-post-filters-{Guid.NewGuid():N}");
        private readonly ApiFactory adminFactory;
        private readonly ApiFactory publicFactory;
        private readonly ApiFactory submissionFactory;
        private readonly string connectionString;
        public HttpClient Admin { get; }
        public HttpClient Public { get; }
        public HttpClient Submission { get; }
        public EventPostIngestor Ingestor => new(adminFactory.Services.GetRequiredService<IDbContextFactory<EventWallDbContext>>());
        public EventWallDbContext CreateDbContext() =>
            adminFactory.Services.GetRequiredService<IDbContextFactory<EventWallDbContext>>().CreateDbContext();

        public Fixture()
        {
            Directory.CreateDirectory(directory);
            connectionString = $"Data Source={Path.Combine(directory, "test.db")};Pooling=False";
            adminFactory = new("Admin", connectionString, directory);
            Admin = adminFactory.CreateClient();
            publicFactory = new("Public", connectionString, directory);
            Public = publicFactory.CreateClient();
            submissionFactory = new("Submission", connectionString, directory);
            Submission = submissionFactory.CreateClient();
        }

        public async Task PrepareSubmission()
        {
            var config = await Submission.GetFromJsonAsync<SubmissionConfigDto>("/api/public/submission-config");
            Submission.DefaultRequestHeaders.Add("X-Submission-Token", config!.SubmissionToken);
        }

        public async Task Login()
        {
            (await Admin.PostAsJsonAsync("/api/admin/login", new { passcode = "integration-test-only-passcode" })).EnsureSuccessStatusCode();
            var token = await Admin.GetFromJsonAsync<JsonElement>("/api/admin/csrf");
            Admin.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        }

        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            Public.Dispose();
            Submission.Dispose();
            await submissionFactory.DisposeAsync();
            await publicFactory.DisposeAsync();
            await adminFactory.DisposeAsync();
            File.Delete(Path.Combine(directory, "test.db"));
            Directory.Delete(directory);
        }
    }

    private sealed class ApiFactory(string role, string connectionString, string webRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").UseWebRoot(webRoot)
                .UseSetting("Deployment:Role", role)
                .UseSetting("ConnectionStrings:EventWall", connectionString)
                .UseSetting("Database:Provider", "Sqlite")
                .UseSetting("Demo:Enabled", "false")
                .UseSetting("Database:ApplyMigrations", "true")
                .UseSetting("Event:SubmissionsInitiallyOpen", "true")
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting("STAFF_PASSCODE", "integration-test-only-passcode");
            foreach (var source in new[] { "Twitch", "YouTube", "X", "Bluesky" })
            {
                builder.UseSetting($"Social:{source}:Enabled", "false");
                builder.UseSetting($"Social:{source}:AnalyticsApproved", "false");
                builder.UseSetting($"Social:{source}:ExportContentApproved", "false");
            }
        }
    }
}
