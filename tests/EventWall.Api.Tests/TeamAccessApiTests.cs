using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Security;
using EventWall.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace EventWall.Api.Tests;

public sealed class TeamAccessApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Presence_requires_staff_and_csrf_and_expires_at_5_minutes_without_a_heartbeat()
    {
        await using var fixture = new Fixture();
        const string path = "/api/admin/presence";
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Owner.PostAsync(path, null)).StatusCode);
        await fixture.LoginOwner();
        fixture.Owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.PostAsync(path, null)).StatusCode);
        await fixture.LoginOwner();
        var owner = Assert.Single(await Beat(fixture.Owner));
        Assert.Equal("Admin", owner.Username);
        Assert.True(owner.IsAdministrator);
        var invitation = await fixture.Invite();
        await fixture.LoginMember(invitation.AccessCode);

        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        var online = await Beat(fixture.Member);
        Assert.Equal(["Admin", "TestModerator"], online.Select(item => item.Username));
        Assert.False(online[1].IsAdministrator);
        Assert.Equal(fixture.Clock.GetUtcNow(), online[1].LastSeenAtUtc);
        fixture.Clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(2, (await Beat(fixture.Member)).Length);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("TestModerator", Assert.Single(await Beat(fixture.Member)).Username);
        Assert.Equal(2, (await Beat(fixture.Owner)).Length);
        (await fixture.Owner.PostAsync("/api/admin/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal("TestModerator", Assert.Single(await Beat(fixture.Member)).Username);
    }

    [Fact]
    public async Task Presence_groups_sessions_and_signing_out_one_does_not_remove_another()
    {
        await using var fixture = new Fixture();
        await fixture.LoginOwner();
        await Beat(fixture.Owner);
        using var other = fixture.CreateClient();
        (await other.PostAsJsonAsync("/api/admin/login", new { passcode = "integration-test-only-passcode" })).EnsureSuccessStatusCode();
        var csrf = await other.GetFromJsonAsync<JsonElement>("/api/admin/csrf");
        other.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        Assert.Single(await Beat(other));
        (await fixture.Owner.PostAsync("/api/admin/logout", null)).EnsureSuccessStatusCode();
        var invitationResponse = await other.PostAsJsonAsync("/api/admin/team", new { username = "TestModerator" });
        invitationResponse.EnsureSuccessStatusCode();
        var invitation = (await invitationResponse.Content.ReadFromJsonAsync<StaffInviteDto>())!;
        await fixture.LoginMember(invitation.AccessCode);
        Assert.Equal(["Admin", "TestModerator"], (await Beat(fixture.Member)).Select(item => item.Username));
        (await other.PostAsync("/api/admin/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal("TestModerator", Assert.Single(await Beat(fixture.Member)).Username);
    }

    [Fact]
    public async Task Presence_excludes_rotated_and_disabled_sessions_without_waiting_for_their_next_request()
    {
        await using var fixture = new Fixture();
        await fixture.LoginOwner();
        var invitation = await fixture.Invite();
        await fixture.LoginMember(invitation.AccessCode);
        await Beat(fixture.Member);
        var response = await fixture.Owner.PostAsJsonAsync($"/api/admin/team/{invitation.Member.Id}/code",
            new { version = invitation.Member.Version });
        response.EnsureSuccessStatusCode();
        var rotated = (await response.Content.ReadFromJsonAsync<StaffInviteDto>())!;
        Assert.Equal("Admin", Assert.Single(await Beat(fixture.Owner)).Username);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Member.PostAsync("/api/admin/presence", null)).StatusCode);
        await fixture.LoginMember(rotated.AccessCode);
        Assert.Equal(2, (await Beat(fixture.Member)).Length);
        (await fixture.Owner.PostAsJsonAsync($"/api/admin/team/{rotated.Member.Id}/status",
            new { version = rotated.Member.Version, enabled = false })).EnsureSuccessStatusCode();
        Assert.Equal("Admin", Assert.Single(await Beat(fixture.Owner)).Username);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Member.PostAsync("/api/admin/presence", null)).StatusCode);
    }

    private static async Task<OnlineStaffDto[]> Beat(HttpClient client)
    {
        var response = await client.PostAsync("/api/admin/presence", null);
        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.All(json.EnumerateArray(), item =>
            Assert.Equal(["username", "isAdministrator", "lastSeenAtUtc"], item.EnumerateObject().Select(property => property.Name)));
        return json.Deserialize<OnlineStaffDto[]>(Json)!;
    }

    [Fact]
    public async Task Report_requires_sign_in_valid_filters_and_returns_owner_only_aggregate_data()
    {
        await using var fixture = new Fixture();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Owner.GetAsync("/api/admin/analytics")).StatusCode);
        await fixture.LoginOwner();
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.GetAsync("/api/admin/analytics?from=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.GetAsync("/api/admin/analytics?source=X")).StatusCode);
        var report = await fixture.Owner.GetFromJsonAsync<JsonElement>("/api/admin/analytics?timeZone=America%2FLos_Angeles");
        Assert.Equal(0, report.GetProperty("counts").GetProperty("total").GetInt32());
        Assert.Equal("America/Los_Angeles", report.GetProperty("filters").GetProperty("timeZone").GetString());
        Assert.Contains("Portal", report.GetProperty("eligibleSources").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(JsonValueKind.Array, report.GetProperty("timeline").ValueKind);
        Assert.False(report.TryGetProperty("posts", out _));
    }

    [Fact]
    public async Task Owner_invites_named_moderators_without_exposing_codes_or_hashes_in_lists()
    {
        await using var fixture = new Fixture();
        await fixture.LoginOwner();
        var owner = await fixture.Owner.GetFromJsonAsync<JsonElement>("/api/admin/session-status");
        Assert.Equal("Admin", owner.GetProperty("username").GetString());
        Assert.True(owner.GetProperty("isAdministrator").GetBoolean());
        var invitation = await fixture.Invite();
        Assert.Matches("^[0-9a-f]{32}$", invitation.AccessCode);
        var list = await fixture.Owner.GetStringAsync("/api/admin/team");
        Assert.DoesNotContain(invitation.AccessCode, list);
        Assert.DoesNotContain("hash", list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessCode", list);
        await using var database = fixture.Database();
        var member = await database.StaffMembers.SingleAsync();
        Assert.NotEqual(invitation.AccessCode, member.AccessCodeHash);
        Assert.DoesNotContain(member.AccessCodeHash, list);
        Assert.DoesNotContain(member.AccessCodeHash, JsonSerializer.Serialize(member));
        Assert.Null(member.LastSignedInAtUtc);
        (await fixture.Member.PostAsJsonAsync("/api/admin/login", new { username = "tEsTmOdErAtOr", passcode = invitation.AccessCode })).EnsureSuccessStatusCode();
        var session = await fixture.Member.GetFromJsonAsync<JsonElement>("/api/admin/session-status");
        Assert.Equal("TestModerator", session.GetProperty("username").GetString());
        Assert.False(session.GetProperty("isAdministrator").GetBoolean());
        database.ChangeTracker.Clear();
        Assert.NotNull((await database.StaffMembers.SingleAsync()).LastSignedInAtUtc);
    }

    [Theory]
    [InlineData("Event staff", null, true)]
    [InlineData("TestModerator", "Moderator", false)]
    public async Task Only_known_legacy_owner_tickets_are_upgraded(string username, string? role, bool authenticated)
    {
        await using var fixture = new Fixture();
        fixture.SetSignedCookie(username, role);
        var session = await fixture.Owner.GetFromJsonAsync<JsonElement>("/api/admin/session-status");
        Assert.Equal(authenticated, session.GetProperty("authenticated").GetBoolean());
        Assert.Equal(authenticated, session.GetProperty("isAdministrator").GetBoolean());
        if (authenticated) Assert.Equal("Admin", session.GetProperty("username").GetString());
        else Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Owner.GetAsync("/api/admin/team")).StatusCode);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    [InlineData("ab")]
    [InlineData("name with spaces")]
    [InlineData("<script>")]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNO")]
    public async Task Invalid_or_reserved_usernames_are_rejected(string username)
    {
        await using var fixture = new Fixture();
        await fixture.LoginOwner();
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.PostAsJsonAsync("/api/admin/team", new { username })).StatusCode);
        await using var database = fixture.Database();
        Assert.Empty(await database.StaffMembers.ToListAsync());
    }

    [Fact]
    public async Task Usernames_stay_unique_case_insensitively_and_changes_require_csrf_and_current_version()
    {
        await using var fixture = new Fixture();
        await fixture.LoginOwner();
        var invitation = await fixture.Invite();
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Owner.PostAsJsonAsync("/api/admin/team", new { username = "testmoderator" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Owner.PostAsJsonAsync($"/api/admin/team/{invitation.Member.Id}/code", new { version = 0 })).StatusCode);
        fixture.Owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.PostAsJsonAsync("/api/admin/team", new { username = "AnotherModerator" })).StatusCode);
        await using var database = fixture.Database();
        Assert.Equal(1, await database.StaffMembers.CountAsync());
    }

    [Fact]
    public async Task Members_can_moderate_and_block_with_named_audits_but_cannot_operate_owner_controls()
    {
        await using var fixture = new Fixture();
        await fixture.LoginOwner();
        var invitation = await fixture.Invite();
        await fixture.LoginMember(invitation.AccessCode);
        foreach (var path in new[] { "/team", "/sources", "/settings", "/analytics", "/export.json", "/export.csv" })
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Member.GetAsync("/api/admin" + path)).StatusCode);
        foreach (var path in new[] { "/team", "/settings/submissions", "/settings/filters", "/sources/youtube/collection",
            $"/team/{invitation.Member.Id}/code", $"/team/{invitation.Member.Id}/status" })
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Member.PostAsJsonAsync("/api/admin" + path, new { version = 1, username = "OtherMember", open = false })).StatusCode);

        await using var database = fixture.Database();
        var post = new EventPost { Id = Guid.NewGuid(), EventSlug = (await database.Events.SingleAsync()).Slug, Source = PostSource.Twitch,
            ExternalId = "named-review", Text = "A useful question #question", AuthorName = "Viewer", AuthorExternalId = "viewer-id",
            ReceivedAtUtc = DateTime.UtcNow, Status = PostStatus.Pending };
        database.Posts.Add(post);
        await database.SaveChangesAsync();
        Assert.Single((await fixture.Member.GetFromJsonAsync<StaffPostDto[]>("/api/admin/queue", Json))!);
        (await fixture.Member.PostAsJsonAsync($"/api/admin/posts/{post.Id}/approve", new { version = 0 })).EnsureSuccessStatusCode();
        (await fixture.Member.PostAsJsonAsync($"/api/admin/posts/{post.Id}/withdraw", new { version = 1 })).EnsureSuccessStatusCode();
        (await fixture.Member.PostAsJsonAsync($"/api/admin/posts/{post.Id}/approve", new { version = 2 })).EnsureSuccessStatusCode();
        (await fixture.Member.PostAsJsonAsync($"/api/admin/posts/{post.Id}/block-author", new { version = 3 })).EnsureSuccessStatusCode();
        var queue = (await fixture.Member.GetFromJsonAsync<StaffPostDto[]>("/api/admin/queue", Json))!;
        Assert.Equal("TestModerator", Assert.Single(queue).ModeratedBy);
        var actions = await database.ModerationActions.AsNoTracking().ToListAsync();
        Assert.Equal(3, actions.Count);
        Assert.All(actions, action => { Assert.Equal(invitation.Member.Id, action.StaffMemberId); Assert.Equal("TestModerator", action.StaffUsername); });
        var block = await database.BlockedAuthors.SingleAsync();
        Assert.Equal(invitation.Member.Id, block.StaffMemberId);
        Assert.Equal("TestModerator", block.StaffUsername);
        var team = await fixture.Owner.GetFromJsonAsync<StaffMemberDto[]>("/api/admin/team");
        Assert.Equal(3, Assert.Single(team!).ModerationActions);
        (await fixture.Member.PostAsync("/api/admin/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Member.GetAsync("/api/admin/queue")).StatusCode);
    }

    [Fact]
    public async Task Moderators_can_add_manual_posts_with_csrf_and_normal_filtering_and_approval()
    {
        await using var fixture = new Fixture();
        const string path = "/api/admin/manual";
        var request = new { text = "Can we hear the next speaker?", authorName = "Event staff" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Member.PostAsJsonAsync(path, request)).StatusCode);
        await fixture.LoginOwner();
        var invitation = await fixture.Invite();
        await fixture.LoginMember(invitation.AccessCode);
        fixture.Member.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Member.PostAsJsonAsync(path, request)).StatusCode);
        await fixture.LoginMember(invitation.AccessCode);

        var response = await fixture.Member.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var post = (await response.Content.ReadFromJsonAsync<StaffPostDto>(Json))!;
        Assert.Equal(PostSource.Manual, post.Source);
        Assert.Equal(PostStatus.Pending, post.Status);
        Assert.Equal(request.text, post.Text);
        Assert.Equal(request.authorName, post.AuthorName);
        Assert.Contains((await fixture.Member.GetFromJsonAsync<StaffPostDto[]>("/api/admin/queue?status=Pending", Json))!,
            item => item.Id == post.Id);

        var rejected = await fixture.Member.PostAsJsonAsync(path, new { text = "Hello" });
        Assert.Equal(HttpStatusCode.Created, rejected.StatusCode);
        var filtered = (await rejected.Content.ReadFromJsonAsync<StaffPostDto>(Json))!;
        Assert.Equal(PostStatus.Rejected, filtered.Status);
        Assert.Contains(PostFilterReason.SingleWord, filtered.FilterReasons);

        (await fixture.Member.PostAsJsonAsync($"/api/admin/posts/{post.Id}/approve", new { version = post.Version }))
            .EnsureSuccessStatusCode();
        await using var database = fixture.Database();
        Assert.Equal(PostStatus.Live, (await database.Posts.SingleAsync(item => item.Id == post.Id)).Status);
        var action = await database.ModerationActions.SingleAsync(item => item.PostId == post.Id);
        Assert.Equal(invitation.Member.Id, action.StaffMemberId);
        Assert.Equal("TestModerator", action.StaffUsername);
    }

    [Fact]
    public async Task Rotation_and_disabling_revoke_both_codes_and_existing_sessions()
    {
        await using var fixture = new Fixture();
        await fixture.LoginOwner();
        var invitation = await fixture.Invite();
        await fixture.LoginMember(invitation.AccessCode);
        var response = await fixture.Owner.PostAsJsonAsync($"/api/admin/team/{invitation.Member.Id}/code", new { version = invitation.Member.Version });
        response.EnsureSuccessStatusCode();
        var rotated = (await response.Content.ReadFromJsonAsync<StaffInviteDto>())!;
        Assert.NotEqual(invitation.AccessCode, rotated.AccessCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Member.GetAsync("/api/admin/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Member.PostAsJsonAsync("/api/admin/login", new { username = "TestModerator", passcode = invitation.AccessCode })).StatusCode);
        await fixture.LoginMember(rotated.AccessCode);
        (await fixture.Owner.PostAsJsonAsync($"/api/admin/team/{rotated.Member.Id}/status", new { version = rotated.Member.Version, enabled = false })).EnsureSuccessStatusCode();
        Assert.False((await fixture.Member.GetFromJsonAsync<JsonElement>("/api/admin/session-status")).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Member.PostAsJsonAsync("/api/admin/login", new { username = "TestModerator", passcode = rotated.AccessCode })).StatusCode);
    }

    [Fact]
    public async Task Disabled_members_keep_identity_and_can_only_return_with_a_fresh_invitation()
    {
        await using var fixture = new Fixture();
        await fixture.LoginOwner();
        var invitation = await fixture.Invite();
        (await fixture.Owner.PostAsJsonAsync($"/api/admin/team/{invitation.Member.Id}/status", new { version = invitation.Member.Version, enabled = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Owner.PostAsJsonAsync("/api/admin/team", new { username = "TESTMODERATOR" })).StatusCode);
        var disabled = Assert.Single((await fixture.Owner.GetFromJsonAsync<StaffMemberDto[]>("/api/admin/team"))!);
        Assert.False(disabled.Enabled);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.PostAsJsonAsync($"/api/admin/team/{disabled.Id}/status", new { version = disabled.Version, enabled = true })).StatusCode);
        var response = await fixture.Owner.PostAsJsonAsync($"/api/admin/team/{disabled.Id}/code", new { version = disabled.Version });
        response.EnsureSuccessStatusCode();
        var next = (await response.Content.ReadFromJsonAsync<StaffInviteDto>())!;
        Assert.Equal(disabled.Id, next.Member.Id);
        Assert.True(next.Member.Enabled);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Member.PostAsJsonAsync("/api/admin/login", new { username = "TestModerator", passcode = invitation.AccessCode })).StatusCode);
        await fixture.LoginMember(next.AccessCode);
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("Submission")]
    public async Task Attendee_roles_never_register_team_routes(string role)
    {
        await using var fixture = new Fixture();
        using var factory = new SiteFactory(role, fixture.ConnectionString);
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/presence", "/team", "/team/00000000-0000-0000-0000-000000000001/code", "/team/00000000-0000-0000-0000-000000000001/status", "/sources/youtube/collection",
            "/promos", "/promos/00000000-0000-0000-0000-000000000001", "/promos/00000000-0000-0000-0000-000000000001/active", "/promos/00000000-0000-0000-0000-000000000001/delete" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin" + path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/admin" + path, new { username = "Member", version = 1 })).StatusCode);
        }
    }

    [Fact]
    public async Task Staff_manage_promos_with_csrf_and_versions_and_walls_receive_current_public_content()
    {
        await using var fixture = new Fixture();
        const string path = "/api/admin/promos";
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Owner.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Owner.PostAsJsonAsync(path, new { text = "Survey" })).StatusCode);
        await fixture.LoginOwner();
        var invitation = await fixture.Invite();
        await fixture.LoginMember(invitation.AccessCode);
        var created = await fixture.Member.PostAsJsonAsync(path, new { title = "  Developer survey  ", description = "  Help shape .NET  ", url = "  https://example.test/survey  " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var promo = (await created.Content.ReadFromJsonAsync<PromoDto>())!;
        Assert.Equal("Developer survey", promo.Title);
        Assert.Equal("Help shape .NET", promo.Description);
        Assert.Equal("https://example.test/survey", promo.Url);
        Assert.True(promo.Active);

        var second = await fixture.Owner.PostAsJsonAsync(path, new { title = "Community day", description = "Join the community", url = " " });
        second.EnsureSuccessStatusCode();
        var textOnly = (await second.Content.ReadFromJsonAsync<PromoDto>())!;
        Assert.Null(textOnly.Url);
        var list = (await fixture.Member.GetFromJsonAsync<PromoDto[]>(path))!;
        Assert.Equal([textOnly.Id, promo.Id], list.Select(item => item.Id));

        using var publicFactory = new SiteFactory("Public", fixture.ConnectionString);
        using var wall = publicFactory.CreateClient();
        var snapshot = (await wall.GetFromJsonAsync<WallSnapshotDto>("/api/public/wall", Json))!;
        Assert.Equal(list, snapshot.Promos);
        var deactivated = await fixture.Member.PostAsJsonAsync($"{path}/{textOnly.Id}/active", new { version = textOnly.Version, active = false });
        deactivated.EnsureSuccessStatusCode();
        textOnly = (await deactivated.Content.ReadFromJsonAsync<PromoDto>())!;
        Assert.False(textOnly.Active);
        var allPromos = (await fixture.Member.GetFromJsonAsync<PromoDto[]>(path))!;
        Assert.Equal([textOnly.Id, promo.Id], allPromos.Select(item => item.Id));
        Assert.False(allPromos[0].Active);
        snapshot = (await wall.GetFromJsonAsync<WallSnapshotDto>("/api/public/wall", Json))!;
        Assert.Equal(promo.Id, Assert.Single(snapshot.Promos).Id);
        var hiddenChanges = (await wall.GetFromJsonAsync<WallChangesDto>("/api/public/wall/changes?sinceRevision=0", Json))!;
        Assert.Equal(promo.Id, Assert.Single(hiddenChanges.Promos).Id);
        Assert.Equal(HttpStatusCode.Conflict,
            (await fixture.Owner.PostAsJsonAsync($"{path}/{textOnly.Id}/active", new { version = 1, active = true })).StatusCode);
        var reactivated = await fixture.Owner.PostAsJsonAsync($"{path}/{textOnly.Id}/active", new { version = textOnly.Version, active = true });
        reactivated.EnsureSuccessStatusCode();
        textOnly = (await reactivated.Content.ReadFromJsonAsync<PromoDto>())!;
        Assert.True(textOnly.Active);
        var sorted = (await wall.GetFromJsonAsync<WallSnapshotDto>(
            $"/api/public/wall?sort=approved&throughRevision={snapshot.Revision}", Json))!;
        Assert.Equal([textOnly.Id, promo.Id], sorted.Promos.Select(item => item.Id));
        var updated = await fixture.Owner.PostAsJsonAsync($"{path}/{promo.Id}", new { title = "Updated survey", description = "Updated description", version = promo.Version });
        updated.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Member.PostAsJsonAsync($"{path}/{promo.Id}", new { title = "Stale edit", description = "Stale description", version = promo.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Member.PostAsJsonAsync($"{path}/{promo.Id}/delete", new { version = promo.Version })).StatusCode);
        var changes = (await wall.GetFromJsonAsync<WallChangesDto>("/api/public/wall/changes?sinceRevision=0", Json))!;
        Assert.Equal(snapshot.Revision, changes.Revision);
        Assert.Equal(0, changes.NewCount);
        Assert.Equal([textOnly.Id, promo.Id], changes.Promos.Select(item => item.Id));
        Assert.Equal("Updated survey", changes.Promos[1].Title);
        Assert.Equal("Updated description", changes.Promos[1].Description);
        Assert.Null(changes.Promos[1].Url);
        var postChanges = await wall.PostAsJsonAsync("/api/public/wall/changes",
            new { sinceRevision = snapshot.Revision, knownIds = Array.Empty<Guid>(), knownThroughRevision = snapshot.Revision });
        postChanges.EnsureSuccessStatusCode();
        Assert.Equal(changes.Promos, (await postChanges.Content.ReadFromJsonAsync<WallChangesDto>(Json))!.Promos);

        fixture.Member.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Member.PostAsJsonAsync(path, new { text = "No CSRF" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Member.PostAsJsonAsync($"{path}/{promo.Id}", new { text = "No CSRF edit", version = promo.Version + 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Member.PostAsJsonAsync($"{path}/{textOnly.Id}/active", new { version = textOnly.Version, active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Member.PostAsJsonAsync($"{path}/{promo.Id}/delete", new { version = promo.Version + 1 })).StatusCode);
        await fixture.LoginMember(invitation.AccessCode);
        (await fixture.Member.PostAsJsonAsync($"{path}/{promo.Id}/delete", new { version = promo.Version + 1 })).EnsureSuccessStatusCode();
        changes = (await wall.GetFromJsonAsync<WallChangesDto>("/api/public/wall/changes?sinceRevision=0", Json))!;
        Assert.Equal(textOnly.Id, Assert.Single(changes.Promos).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Owner.PostAsJsonAsync($"{path}/{promo.Id}/delete", new { version = 2 })).StatusCode);
    }

    [Theory]
    [InlineData("", "Description", null)]
    [InlineData("  ", "Description", null)]
    [InlineData("Promo", "", null)]
    [InlineData("Promo", "  ", null)]
    [InlineData("Promo", "Description", "javascript:alert(1)")]
    [InlineData("Promo", "Description", "/relative")]
    [InlineData("Promo", "Description", "https://user:password@example.test")]
    [InlineData("Promo", "Description", "not a url")]
    public async Task Invalid_promo_content_is_rejected(string title, string description, string? url)
    {
        await using var fixture = new Fixture();
        await fixture.LoginOwner();
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.PostAsJsonAsync("/api/admin/promos", new { title, description, url })).StatusCode);
        await using var database = fixture.Database();
        Assert.Empty(await database.Promos.ToListAsync());
    }

    [Fact]
    public async Task Promo_limits_event_isolation_and_restart_persistence_are_enforced()
    {
        await using var fixture = new Fixture();
        await fixture.LoginOwner();
        const string path = "/api/admin/promos";
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.PostAsJsonAsync(path, new { title = new string('a', 81), description = "Description" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.PostAsJsonAsync(path, new { title = "Title", description = new string('a', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.PostAsJsonAsync(path, new { title = "Title", description = "URL", url = "https://example.test/" + new string('a', 2049) })).StatusCode);
        var response = await fixture.Owner.PostAsJsonAsync(path, new { title = new string('a', 80), description = new string('a', 500), url = "https://example.test/" + new string('a', 2027) });
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<PromoDto>())!;
        await using var database = fixture.Database();
        var foreign = new EventPromo { Id = Guid.NewGuid(), EventSlug = "other", Title = "Private event", Description = "Private event promo", CreatedAtUtc = DateTime.UtcNow, Version = 1 };
        database.Promos.Add(foreign);
        await database.SaveChangesAsync();
        Assert.Equal(saved.Id, Assert.Single((await fixture.Owner.GetFromJsonAsync<PromoDto[]>(path))!).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Owner.PostAsJsonAsync($"{path}/{foreign.Id}", new { title = "Wrong event", description = "Wrong event", version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Owner.PostAsJsonAsync($"{path}/{foreign.Id}/delete", new { version = 1 })).StatusCode);
        using var restarted = new SiteFactory("Public", fixture.ConnectionString);
        using var wall = restarted.CreateClient();
        var snapshot = (await wall.GetFromJsonAsync<WallSnapshotDto>("/api/public/wall", Json))!;
        Assert.Equal(saved, Assert.Single(snapshot.Promos));
    }

    [Fact]
    public async Task YouTube_collection_requires_owner_and_csrf_and_preserves_start_stop_choice_across_restarts()
    {
        await using var fixture = new Fixture(youtubeConfigured: true);
        const string path = "/api/admin/sources/youtube/collection";
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Owner.PostAsJsonAsync(path, new { running = true })).StatusCode);
        await fixture.LoginOwner();
        var initial = Assert.Single((await fixture.Owner.GetFromJsonAsync<SourceStatusDto[]>("/api/admin/sources"))!,
            source => source.Provider == "YouTube");
        Assert.Equal("paused", initial.State);
        Assert.False(initial.CollectionRunning);
        Assert.True(initial.CollectionAvailable);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.PostAsJsonAsync(path, new { })).StatusCode);

        fixture.Owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.PostAsJsonAsync(path, new { running = true })).StatusCode);
        var csrf = await fixture.Owner.GetFromJsonAsync<JsonElement>("/api/admin/csrf");
        fixture.Owner.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        (await fixture.Owner.PostAsJsonAsync(path, new { running = true })).EnsureSuccessStatusCode();
        await using (var database = fixture.Database())
            Assert.True((await database.Events.SingleAsync()).YouTubeCollectionRunning);

        using var restarted = new SiteFactory("Admin", fixture.ConnectionString, youtubeConfigured: true);
        using var client = restarted.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/login", new { passcode = "integration-test-only-passcode" })).EnsureSuccessStatusCode();
        var persisted = Assert.Single((await client.GetFromJsonAsync<SourceStatusDto[]>("/api/admin/sources"))!,
            source => source.Provider == "YouTube");
        Assert.True(persisted.CollectionRunning);

        (await fixture.Owner.PostAsJsonAsync(path, new { running = false })).EnsureSuccessStatusCode();
        await using (var database = fixture.Database())
            Assert.False((await database.Events.SingleAsync()).YouTubeCollectionRunning);
        var stopped = Assert.Single((await client.GetFromJsonAsync<SourceStatusDto[]>("/api/admin/sources"))!,
            source => source.Provider == "YouTube");
        Assert.False(stopped.CollectionRunning);
        Assert.Equal("paused", stopped.State);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task YouTube_start_cannot_bypass_configuration_enablement_or_policy_gates(
        bool configured, bool policyApproved, bool enabled)
    {
        await using var fixture = new Fixture(configured, policyApproved, enabled);
        await fixture.LoginOwner();
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Owner.PostAsJsonAsync(
            "/api/admin/sources/youtube/collection", new { running = true })).StatusCode);
        await using var database = fixture.Database();
        Assert.False((await database.Events.SingleAsync()).YouTubeCollectionRunning);

        database.Events.Single().YouTubeCollectionRunning = true;
        await database.SaveChangesAsync();
        (await fixture.Owner.PostAsJsonAsync("/api/admin/sources/youtube/collection", new { running = false })).EnsureSuccessStatusCode();
        database.ChangeTracker.Clear();
        Assert.False((await database.Events.SingleAsync()).YouTubeCollectionRunning);
    }

    [Fact]
    public async Task Starting_YouTube_again_does_not_clear_a_reported_quota_pause()
    {
        await using var fixture = new Fixture(youtubeConfigured: true);
        await fixture.LoginOwner();
        fixture.Health.Report("YouTube", "budget-exhausted", "Quota reset is pending.");
        const string path = "/api/admin/sources/youtube/collection";
        foreach (var running in new[] { true, false, true })
        {
            var response = await fixture.Owner.PostAsJsonAsync(path, new { running });
            response.EnsureSuccessStatusCode();
            var source = Assert.Single((await response.Content.ReadFromJsonAsync<SourceStatusDto[]>())!,
                item => item.Provider == "YouTube");
            Assert.Equal(running, source.CollectionRunning);
            Assert.Equal(running ? "budget-exhausted" : "paused", source.State);
        }
        Assert.Equal("budget-exhausted", fixture.Health.Get("YouTube")!.State);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "eventwall-team-" + Guid.NewGuid().ToString("N"));
        private readonly SiteFactory factory;
        public string ConnectionString { get; }
        public HttpClient Owner { get; }
        public HttpClient Member { get; }
        public ManualTimeProvider Clock { get; } = new();
        public Fixture(bool youtubeConfigured = false, bool youtubePolicyApproved = true, bool youtubeEnabled = true)
        {
            Directory.CreateDirectory(directory);
            ConnectionString = $"Data Source={Path.Combine(directory, "team.db")};Pooling=False";
            factory = new("Admin", ConnectionString, youtubeConfigured, youtubePolicyApproved, youtubeEnabled, Clock);
            Owner = factory.CreateClient();
            Member = factory.CreateClient();
        }
        public EventWallDbContext Database() => factory.Services.GetRequiredService<IDbContextFactory<EventWallDbContext>>().CreateDbContext();
        public SourceHealthRegistry Health => factory.Services.GetRequiredService<SourceHealthRegistry>();
        public HttpClient CreateClient() => factory.CreateClient();
        public void SetSignedCookie(string username, string? role)
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, username), new("staff_session", Guid.NewGuid().ToString("N")),
                new(StaffPasscodeService.FingerprintClaim, factory.Services.GetRequiredService<StaffPasscodeService>().Fingerprint) };
            if (role is not null) claims.Add(new(ClaimTypes.Role, role));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(principal,
                new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
                CookieAuthenticationDefaults.AuthenticationScheme));
            Owner.DefaultRequestHeaders.Add("Cookie", $"EventWall.Staff={ticket}");
        }
        public async Task LoginOwner()
        {
            (await Owner.PostAsJsonAsync("/api/admin/login", new { passcode = "integration-test-only-passcode" })).EnsureSuccessStatusCode();
            await Csrf(Owner);
        }
        public async Task LoginMember(string code)
        {
            (await Member.PostAsJsonAsync("/api/admin/login", new { username = "TestModerator", passcode = code })).EnsureSuccessStatusCode();
            await Csrf(Member);
        }
        private static async Task Csrf(HttpClient client)
        {
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/admin/csrf");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        }
        public async Task<StaffInviteDto> Invite()
        {
            var response = await Owner.PostAsJsonAsync("/api/admin/team", new { username = "TestModerator" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<StaffInviteDto>())!;
        }
        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            Member.Dispose();
            await factory.DisposeAsync();
            File.Delete(Path.Combine(directory, "team.db"));
            Directory.Delete(directory);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class SiteFactory(string role, string connectionString,
        bool youtubeConfigured = false, bool youtubePolicyApproved = true, bool youtubeEnabled = true,
        TimeProvider? clock = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development")
                .UseSetting("Deployment:Role", role)
                .UseSetting("ConnectionStrings:EventWall", connectionString)
                .UseSetting("Database:Provider", "Sqlite")
                .UseSetting("Database:ApplyMigrations", "true")
                .UseSetting("Demo:Enabled", "false")
                .UseSetting("STAFF_PASSCODE", "integration-test-only-passcode")
                .UseSetting("Logging:LogLevel:Default", "Warning");
            foreach (var source in new[] { "Twitch", "YouTube", "X", "Bluesky" })
                builder.UseSetting($"Social:{source}:Enabled", "false");
            builder.UseSetting("Social:YouTube:Enabled", youtubeEnabled.ToString())
                .UseSetting("Social:YouTube:PolicyApproved", youtubePolicyApproved.ToString());
            if (youtubeConfigured)
            {
                builder.UseSetting("Social:YouTube:LiveChatId", "fixture")
                    .UseSetting("Social:YouTube:ClientId", "fixture")
                    .UseSetting("Social:YouTube:ClientSecret", "fixture")
                    .UseSetting("Social:YouTube:RefreshToken", "fixture");
            }
            builder.ConfigureServices(services =>
            {
                if (clock is not null) services.AddSingleton(clock);
                var poller = services.SingleOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(SocialPollingWorker));
                if (poller is not null) services.Remove(poller);
            });
        }
    }
}
