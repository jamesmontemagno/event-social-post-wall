using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Models;
using EventWall.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace EventWall.Api.Tests;

public sealed class SubmissionApiTests
{
    [Theory]
    [InlineData("  COMMUNITYEVENT  ", "communityevent")]
    [InlineData("My-Event-2026", "my-event-2026")]
    public void Configuration_branding_uses_the_same_slug_normalization_as_seeding(string value, string expected) =>
        Assert.Equal(expected, EventSeeder.NormalizeSlug(value));

    [Fact]
    public async Task Bootstrap_and_health_work_without_a_database_and_issue_zero_SQL()
    {
        await using var fixture = new Fixture(initialize: false);
        var responses = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => fixture.Submission.GetAsync("/api/public/submission-config")));
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var config = await response.Content.ReadFromJsonAsync<SubmissionConfigDto>();
            Assert.Equal("Community Event", config!.Name);
            Assert.NotEmpty(config.SubmissionToken);
            Assert.Contains("httponly", string.Join("", response.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(HttpStatusCode.OK, (await fixture.Submission.GetAsync("/api/health")).StatusCode);
        Assert.Equal(0, fixture.Counter.Commands);
        Assert.False(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public async Task Blazor_prerender_and_connection_bootstrap_do_not_read_SQL()
    {
        await using var fixture = new Fixture(initialize: false);
        var response = await fixture.Submission.GetAsync("/?view=staff");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<meta name=\"eventwall-role\" content=\"submission\"", html);
        Assert.Contains("Getting ready for your question...", html);
        Assert.Contains("_framework/blazor.web", html);
        Assert.Contains("Blazor Server", html);
        Assert.Contains("react.dev", html);
        Assert.Contains("THIRD-PARTY-NOTICES.txt", html);
        Assert.DoesNotContain("Shared staff passcode", html);
        Assert.DoesNotContain("/api/public/wall", html);
        foreach (var asset in new[] { "/_framework/blazor.web.js", "/questions/theme.css" })
        {
            var assetResponse = await fixture.Submission.GetAsync(asset);
            Assert.Equal(HttpStatusCode.OK, assetResponse.StatusCode);
            Assert.True((await assetResponse.Content.ReadAsByteArrayAsync()).Length > 0,
                $"{asset} returned an empty response ({assetResponse.Content.Headers}).");
        }
        var negotiation = await fixture.Submission.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, negotiation.StatusCode);
        Assert.Equal(0, fixture.Counter.Commands);
        Assert.False(File.Exists(fixture.DatabasePath));
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("Admin")]
    public async Task Other_roles_do_not_expose_the_Blazor_app_or_transport(string role)
    {
        await using var fixture = new Fixture();
        using var factory = new Factory(role, fixture.ConnectionString, fixture.Directory);
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/questions/questions.js", "/_framework/blazor.web.js", "/_blazor/negotiate" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(path, null)).StatusCode);
        }
    }

    [Theory]
    [InlineData("/api/public/wall")]
    [InlineData("/api/public/wall/changes?sinceRevision=0")]
    [InlineData("/api/public/wall/items/00000000-0000-0000-0000-000000000001")]
    [InlineData("/api/public/event")]
    [InlineData("/api/admin/queue")]
    [InlineData("/api/admin/login")]
    [InlineData("/api/admin/analytics")]
    public async Task Submission_does_not_expose_other_roles(string path)
    {
        await using var fixture = new Fixture(initialize: false);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Submission.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Submission.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(0, fixture.Counter.Commands);
    }

    [Fact]
    public async Task Wall_never_accepts_submissions_or_issues_tokens()
    {
        await using var fixture = new Fixture();
        using var wallFactory = new Factory("Public", fixture.ConnectionString, fixture.Directory);
        using var wall = wallFactory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await wall.PostAsJsonAsync("/api/public/submissions", new { text = "A question" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await wall.GetAsync("/api/public/submission-config")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await wall.GetAsync("/api/admin/analytics")).StatusCode);
    }

    [Fact]
    public async Task Admin_event_exposes_the_configured_site_destinations_without_changing_the_wall_response()
    {
        await using var fixture = new Fixture();
        const string wallUrl = "https://wall.example.test/event/";
        const string questionUrl = "https://questions.example.test/ask/";
        using var adminFactory = new Factory("Admin", fixture.ConnectionString, fixture.Directory,
            wallSiteUrl: wallUrl, submissionSiteUrl: questionUrl);
        using var admin = adminFactory.CreateClient();
        var adminEvent = await admin.GetFromJsonAsync<JsonElement>("/api/public/event");
        Assert.Equal(wallUrl, adminEvent.GetProperty("wallSiteUrl").GetString());
        Assert.Equal(questionUrl, adminEvent.GetProperty("submissionSiteUrl").GetString());

        using var wallFactory = new Factory("Public", fixture.ConnectionString, fixture.Directory,
            wallSiteUrl: wallUrl, submissionSiteUrl: questionUrl);
        using var wall = wallFactory.CreateClient();
        var wallEvent = await wall.GetFromJsonAsync<JsonElement>("/api/public/event");
        Assert.False(wallEvent.TryGetProperty("wallSiteUrl", out _));
        Assert.Equal(questionUrl, wallEvent.GetProperty("submissionSiteUrl").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Submission.GetAsync("/api/public/event")).StatusCode);
    }

    [Fact]
    public async Task Invalid_or_missing_admin_destinations_are_disabled()
    {
        await using var fixture = new Fixture();
        using var adminFactory = new Factory("Admin", fixture.ConnectionString, fixture.Directory,
            wallSiteUrl: "javascript:alert(1)");
        using var admin = adminFactory.CreateClient();
        var adminEvent = await admin.GetFromJsonAsync<PublicEventDto>("/api/public/event");
        Assert.Null(adminEvent!.WallSiteUrl);
        Assert.Null(adminEvent.SubmissionSiteUrl);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/session-status")).StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("forged")]
    [InlineData("expired")]
    [InlineData("wrong-event")]
    [InlineData("wrong-cookie")]
    [InlineData("tampered-cookie")]
    [InlineData("origin")]
    public async Task Invalid_tokens_are_rejected_before_SQL(string kind)
    {
        await using var fixture = new Fixture(initialize: false);
        await fixture.Bootstrap();
        var token = fixture.Token;
        if (kind == "missing") token = null;
        if (kind == "forged") token = "invalid";
        if (kind is "expired" or "wrong-event")
        {
            var protector = fixture.SubmissionFactory.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(SubmissionProtection.TokenPurpose).ToTimeLimitedDataProtector();
            token = protector.Protect($"{(kind == "wrong-event" ? "another-event" : "communityevent")}:{fixture.Browser}",
                DateTimeOffset.UtcNow.AddMinutes(kind == "expired" ? -1 : 30));
        }
        using var request = fixture.Request(Guid.NewGuid(), token: token);
        if (kind == "origin") request.Headers.Add("Origin", "https://another.example.test");
        using var wrongBrowser = fixture.SubmissionFactory.CreateClient();
        if (kind == "wrong-cookie") await wrongBrowser.GetAsync("/api/public/submission-config");
        using var noCookies = fixture.SubmissionFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        if (kind == "tampered-cookie") request.Headers.Add("Cookie", $"{SubmissionProtection.CookieName}=tampered");
        var client = kind == "wrong-cookie" ? wrongBrowser : kind == "tampered-cookie" ? noCookies : fixture.Submission;
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("submission-token-invalid", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, fixture.Counter.Commands);
    }

    [Fact]
    public async Task Per_IP_limit_cannot_be_bypassed_by_forwarded_headers()
    {
        await using var fixture = new Fixture(initialize: false);
        for (var index = 0; index < 2; index++)
        {
            using var request = fixture.Request(Guid.NewGuid());
            request.Headers.Add("X-Forwarded-For", $"192.0.2.{index + 1}");
            var response = await fixture.Submission.SendAsync(request);
            Assert.Equal(index == 0 ? HttpStatusCode.Forbidden : HttpStatusCode.TooManyRequests, response.StatusCode);
            if (index == 1)
            {
                Assert.NotNull(response.Headers.RetryAfter);
                var retry = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("retryAfterSeconds").GetInt32();
                Assert.InRange(retry, 120, 180);
                Assert.Equal(TimeSpan.FromSeconds(retry), response.Headers.RetryAfter.Delta);
            }
        }
        Assert.Equal(0, fixture.Counter.Commands);
    }

    [Fact]
    public async Task A_new_browser_on_the_same_IP_cannot_bypass_a_received_submission_limit()
    {
        await using var fixture = new Fixture();
        await fixture.Bootstrap();
        using var first = fixture.Request(Guid.NewGuid(), token: fixture.Token);
        Assert.Equal(HttpStatusCode.Accepted, (await fixture.Submission.SendAsync(first)).StatusCode);
        var commands = fixture.Counter.Commands;

        using var another = fixture.SubmissionFactory.CreateClient();
        var config = await another.GetFromJsonAsync<SubmissionConfigDto>("/api/public/submission-config");
        using var next = fixture.Request(Guid.NewGuid(), token: config!.SubmissionToken);
        var limited = await another.SendAsync(next);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(commands, fixture.Counter.Commands);
        await using var database = fixture.CreateDb();
        Assert.Equal(1, await database.Posts.CountAsync());
    }

    [Fact]
    public async Task Hourly_browser_budget_survives_token_refresh_and_is_separate_per_browser()
    {
        await using var fixture = new Fixture(initialize: false);
        await fixture.Bootstrap();
        var browser = fixture.Browser;
        var guard = fixture.SubmissionFactory.Services.GetRequiredService<SubmissionProtection>();
        for (var index = 0; index < 4; index++) { using var lease = guard.Acquire(browser); Assert.True(lease.IsAcquired); }
        await fixture.Bootstrap();
        Assert.Equal(browser, fixture.Browser);
        for (var index = 4; index < 8; index++) { using var lease = guard.Acquire(browser); Assert.True(lease.IsAcquired); }
        using var denied = guard.Acquire(browser);
        Assert.False(denied.IsAcquired);
        using var another = guard.Acquire(Guid.NewGuid().ToString("N"));
        Assert.True(another.IsAcquired);
        using var request = fixture.Request(Guid.NewGuid(), token: fixture.Token);
        var response = await fixture.Submission.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(0, fixture.Counter.Commands);
    }

    [Fact]
    public async Task Honeypot_and_invalid_input_do_not_touch_SQL()
    {
        await using var fixture = new Fixture(initialize: false, separateClientIps: true);
        await fixture.Bootstrap();
        foreach (var (text, name, website, status) in new[]
        {
            ("An automated question", "", "spam.example", HttpStatusCode.Accepted),
            ("", "", "", HttpStatusCode.BadRequest),
            (new string('x', 2001), "", "", HttpStatusCode.BadRequest),
            ("A question", new string('n', 81), "", HttpStatusCode.BadRequest),
            (new string('x', 17000), "", "", HttpStatusCode.RequestEntityTooLarge)
        })
        {
            using var request = fixture.Request(Guid.NewGuid(), text, fixture.Token, name, website);
            Assert.Equal(status, (await fixture.Submission.SendAsync(request)).StatusCode);
        }
        Assert.Equal(0, fixture.Counter.Commands);
    }

    [Fact]
    public async Task Same_key_retries_are_safe_and_changed_payload_conflicts()
    {
        await using var fixture = new Fixture(separateClientIps: true);
        await fixture.Bootstrap();
        var key = Guid.NewGuid();
        for (var index = 0; index < 2; index++)
        {
            using var request = fixture.Request(key, token: fixture.Token);
            Assert.Equal(HttpStatusCode.Accepted, (await fixture.Submission.SendAsync(request)).StatusCode);
        }
        using var changed = fixture.Request(key, "A different question for the hosts?", fixture.Token);
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Submission.SendAsync(changed)).StatusCode);
        var differentKey = Guid.NewGuid();
        using var differentAttendee = fixture.Request(differentKey, token: fixture.Token);
        Assert.Equal(HttpStatusCode.Accepted, (await fixture.Submission.SendAsync(differentAttendee)).StatusCode);
        await using var database = fixture.CreateDb();
        Assert.Equal(2, await database.Posts.CountAsync());
        Assert.True(fixture.Counter.Commands > 0, "The interceptor must observe real submission SQL, not just report zero for every request.");
        var original = await database.Posts.SingleAsync(post => post.ExternalId == $"portal:{key:D}");
        var copy = await database.Posts.SingleAsync(post => post.ExternalId == $"portal:{differentKey:D}");
        Assert.Equal(PostStatus.Pending, original.Status);
        Assert.Equal(PostStatus.Rejected, copy.Status);
        Assert.Equal([PostFilterReason.DuplicateText], copy.FilterReasons);
        Assert.Single(await database.ModerationActions.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_filtered_retries_write_one_post_and_one_audit()
    {
        await using var fixture = new Fixture(separateClientIps: true);
        await fixture.Bootstrap();
        var key = Guid.NewGuid();
        using var one = fixture.Request(key, "Hello", fixture.Token);
        using var two = fixture.Request(key, "Hello", fixture.Token);
        var responses = await Task.WhenAll(fixture.Submission.SendAsync(one), fixture.Submission.SendAsync(two));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        await using var database = fixture.CreateDb();
        Assert.Equal(1, await database.Posts.CountAsync());
        Assert.Equal(1, await database.ModerationActions.CountAsync());
    }

    [Fact]
    public async Task Staff_pause_is_authoritative_after_bootstrap()
    {
        await using var fixture = new Fixture();
        await fixture.Bootstrap();
        await using (var database = fixture.CreateDb())
        {
            (await database.Events.SingleAsync()).SubmissionsOpen = false;
            await database.SaveChangesAsync();
        }
        using var request = fixture.Request(Guid.NewGuid(), token: fixture.Token);
        var response = await fixture.Submission.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("submissions-paused", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using var check = fixture.CreateDb();
        Assert.Empty(await check.Posts.ToListAsync());
    }

    [Fact]
    public async Task Turnstile_rejects_unverified_sends_before_SQL_and_accepts_a_verified_token()
    {
        var siteverify = new FakeTurnstile(form => form["response"] switch
        {
            "valid-token" => new { success = true, action = (string?)TurnstileVerifier.Action, hostname = (string?)"questions.test", error_codes = Array.Empty<string>() },
            "other-action" => new { success = true, action = (string?)"login", hostname = (string?)"questions.test", error_codes = Array.Empty<string>() },
            _ => new { success = false, action = (string?)null, hostname = (string?)null, error_codes = new[] { "invalid-input-response" } }
        });
        await using var fixture = new Fixture(separateClientIps: true, turnstile: siteverify);
        var config = await fixture.Submission.GetFromJsonAsync<SubmissionConfigDto>("/api/public/submission-config");
        Assert.Equal(TestSiteKey, config!.TurnstileSiteKey);
        await fixture.Bootstrap();
        foreach (var challenge in new string?[] { null, "invalid-token", "other-action", new string('x', 2049) })
        {
            using var request = fixture.Request(Guid.NewGuid(), token: fixture.Token, challenge: challenge);
            var response = await fixture.Submission.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("challenge-failed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Equal(0, fixture.Counter.Commands);
        Assert.Equal(2, siteverify.Requests.Count);

        using var verified = fixture.Request(Guid.NewGuid(), token: fixture.Token, challenge: "valid-token");
        Assert.Equal(HttpStatusCode.Accepted, (await fixture.Submission.SendAsync(verified)).StatusCode);
        var sent = siteverify.Requests.Last();
        Assert.Equal(TestSecretKey, sent["secret"]);
        Assert.True(IPAddress.TryParse(sent["remoteip"], out _));
        Assert.True(Guid.TryParse(sent["idempotency_key"], out _));
        await using var database = fixture.CreateDb();
        Assert.Equal(1, await database.Posts.CountAsync());
    }

    [Fact]
    public async Task Turnstile_outage_fails_closed_without_SQL()
    {
        await using var fixture = new Fixture(initialize: false, turnstile: new FakeTurnstile(_ => throw new HttpRequestException("offline")));
        await fixture.Bootstrap();
        using var request = fixture.Request(Guid.NewGuid(), token: fixture.Token, challenge: "valid-token");
        var response = await fixture.Submission.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("challenge-unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, fixture.Counter.Commands);
    }

    [Theory]
    [InlineData("Turnstile:SiteKey")]
    [InlineData("Turnstile:SecretKey")]
    [InlineData("Turnstile:Required")]
    public async Task Incomplete_Turnstile_configuration_fails_at_startup(string setting)
    {
        await using var fixture = new Fixture(initialize: false);
        using var factory = new Factory("Submission", fixture.ConnectionString, fixture.Directory,
            settings: new Dictionary<string, string> { [setting] = setting == "Turnstile:Required" ? "true" : "configured" });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    private const string TestSiteKey = "1x00000000000000000000AA";
    private const string TestSecretKey = "test-secret";

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"eventwall-submission-{Guid.NewGuid():N}");
        public string DatabasePath => Path.Combine(Directory, "test.db");
        public string ConnectionString => $"Data Source={DatabasePath};Pooling=False";
        public CommandCounter Counter { get; } = new();
        public Factory SubmissionFactory { get; }
        private readonly Factory? adminFactory;
        public HttpClient Submission { get; }
        public string? Token { get; private set; }
        public string Browser { get; private set; } = "";

        public Fixture(bool initialize = true, bool separateClientIps = false, FakeTurnstile? turnstile = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (initialize)
            {
                adminFactory = new("Admin", ConnectionString, Directory);
                using var admin = adminFactory.CreateClient();
            }
            SubmissionFactory = new("Submission", ConnectionString, Directory, Counter, separateClientIps: separateClientIps,
                settings: turnstile is null ? null : new Dictionary<string, string>
                {
                    ["Turnstile:SiteKey"] = TestSiteKey,
                    ["Turnstile:SecretKey"] = TestSecretKey,
                    ["Turnstile:Hostnames:0"] = "questions.test"
                },
                turnstile: turnstile);
            Submission = SubmissionFactory.CreateClient();
        }

        public async Task Bootstrap()
        {
            var response = await Submission.GetAsync("/api/public/submission-config");
            response.EnsureSuccessStatusCode();
            Token = (await response.Content.ReadFromJsonAsync<SubmissionConfigDto>())!.SubmissionToken;
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                var cookie = cookies.Single().Split(';')[0].Split('=', 2)[1];
                Browser = SubmissionFactory.Services.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("EventWall.Submission.Browser.v1").Unprotect(cookie);
            }
        }

        public HttpRequestMessage Request(Guid key, string text = "How do I build a great .NET app?", string? token = null, string name = "", string website = "", string? challenge = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/submissions")
            { Content = JsonContent.Create(new { text, displayName = name, website }) };
            request.Headers.Add("Idempotency-Key", key.ToString());
            if (token is not null) request.Headers.Add("X-Submission-Token", token);
            if (challenge is not null) request.Headers.Add(TurnstileVerifier.HeaderName, challenge);
            return request;
        }

        public EventWallDbContext CreateDb() =>
            adminFactory!.Services.GetRequiredService<IDbContextFactory<EventWallDbContext>>().CreateDbContext();

        public async ValueTask DisposeAsync()
        {
            Submission.Dispose();
            await SubmissionFactory.DisposeAsync();
            if (adminFactory is not null) await adminFactory.DisposeAsync();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class Factory(string role, string connection, string root, CommandCounter? counter = null,
        string? wallSiteUrl = null, string? submissionSiteUrl = null, bool separateClientIps = false,
        IReadOnlyDictionary<string, string>? settings = null, FakeTurnstile? turnstile = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").UseWebRoot(root)
                .UseSetting("Deployment:Role", role)
                .UseSetting("ConnectionStrings:EventWall", connection)
                .UseSetting("Database:Provider", "Sqlite")
                .UseSetting("Event:SubmissionsInitiallyOpen", "true")
                .UseSetting("Demo:Enabled", "false")
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting("STAFF_PASSCODE", "test-only-staff-passcode");
            foreach (var (key, value) in settings ?? new Dictionary<string, string>()) builder.UseSetting(key, value);
            if (turnstile is not null) builder.ConfigureServices(services =>
                services.AddHttpClient(TurnstileVerifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => turnstile));
            if (wallSiteUrl is not null) builder.UseSetting("Public:WallSiteUrl", wallSiteUrl);
            if (submissionSiteUrl is not null) builder.UseSetting("Public:SubmissionSiteUrl", submissionSiteUrl);
            if (counter is not null) builder.ConfigureServices(services =>
                services.AddDbContextFactory<EventWallDbContext>(options => options.AddInterceptors(counter)));
            if (separateClientIps) builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter, SeparateClientIps>());
        }
    }

    private sealed class FakeTurnstile(Func<IReadOnlyDictionary<string, string>, object> respond) : HttpMessageHandler
    {
        private readonly List<IReadOnlyDictionary<string, string>> requests = [];
        public IReadOnlyList<IReadOnlyDictionary<string, string>> Requests { get { lock (requests) return [.. requests]; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://challenges.cloudflare.com/turnstile/v0/siteverify", request.RequestUri!.ToString());
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
            lock (requests) requests.Add(form);
            var body = JsonSerializer.Serialize(respond(form)).Replace("\"error_codes\"", "\"error-codes\"");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }

        // The factory owns pooled handlers; keep this shared fake usable for the whole test.
        protected override void Dispose(bool disposing) { }
    }

    private sealed class SeparateClientIps : IStartupFilter
    {
        private int clients;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Exercise validation and persistence from distinct IPs without disabling rate guards.
            app.Use(async (context, nextRequest) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse($"2001:db8::{Interlocked.Increment(ref clients):x}");
                await nextRequest();
            });
            next(app);
        };
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int commands;
        public int Commands => commands;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref commands); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref commands); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref commands); return ValueTask.FromResult(result); }
    }
}
