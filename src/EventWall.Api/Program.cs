using System.Security.Claims;
using System.Security.Cryptography;
using System.Data;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Components;
using EventWall.Api.Models;
using EventWall.Api.Security;
using EventWall.Api.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using System.Threading.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
var deploymentRole = DeploymentRole.Resolve(builder.Configuration);
var isAdminDeployment = deploymentRole == SiteRole.Admin;
var isSubmissionDeployment = deploymentRole == SiteRole.Submission;
var submissionSiteUrl = deploymentRole == SiteRole.Public
    ? DeploymentRole.SubmissionSiteUrl(builder.Configuration, builder.Environment.IsDevelopment())
    : null;
var eventConfiguration = new EventConfiguration(builder.Configuration);
var submissionSlug = eventConfiguration.Slug;
var submissionName = eventConfiguration.Name;
var questionHashtag = eventConfiguration.QuestionHashtag;
var webRoot = builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
if (!isSubmissionDeployment)
{
    // Keep Vite deployments isolated; Blazor needs its framework and linked-asset manifest.
    builder.Environment.WebRootFileProvider = Directory.Exists(webRoot)
        ? new PhysicalFileProvider(webRoot)
        : new NullFileProvider();
}
if (!builder.Environment.IsDevelopment() || File.Exists(Path.Combine(webRoot, "index.html")))
{
    DeploymentRole.ValidateAssets(webRoot, deploymentRole);
}
var connectionString = builder.Configuration.GetConnectionString("EventWall");
var databaseProvider = builder.Configuration["Database:Provider"];
var demoMode = DemoMode.Resolve(builder.Configuration, builder.Environment.EnvironmentName);
DatabasePurpose.Validate(builder.Configuration, builder.Environment.EnvironmentName);
var localDatabasePath = Path.Combine(
    builder.Environment.ContentRootPath,
    "App_Data",
    demoMode ? "eventwall-demo.db" : "eventwall.db");
var cookieSecurePolicy = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;

if ((builder.Environment.IsProduction() || builder.Environment.IsStaging()) &&
    string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("A production SQL Server connection string is required.");
}

static StaffMemberDto StaffDto(StaffMember member, int actions) => new(member.Id, member.Username, member.Enabled,
    member.Version, DateTime.SpecifyKind(member.CreatedAtUtc, DateTimeKind.Utc), DateTime.SpecifyKind(member.CodeRotatedAtUtc, DateTimeKind.Utc),
    member.LastSignedInAtUtc.HasValue ? DateTime.SpecifyKind(member.LastSignedInAtUtc.Value, DateTimeKind.Utc) : null, actions);

if (!string.IsNullOrWhiteSpace(databaseProvider) &&
    !string.Equals(databaseProvider, "Sqlite", StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(databaseProvider, "SqlServer", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Database:Provider must be either Sqlite or SqlServer.");
}

if (string.Equals(databaseProvider, "SqlServer", StringComparison.OrdinalIgnoreCase) &&
    string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Database:Provider is SqlServer, but the EventWall connection string is missing.");
}

builder.Services.AddDbContextFactory<EventWallDbContext>(options =>
{
    if (string.Equals(databaseProvider, "Sqlite", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(connectionString))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localDatabasePath)!);
        options.UseSqlite(connectionString ?? $"Data Source={localDatabasePath}")
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
    }
    else
    {
        options.UseSqlServer(connectionString);
    }
});
if (isAdminDeployment)
{
    builder.Services.AddSingleton<StaffPasscodeService>();
    builder.Services.AddSingleton<TeamAccessService>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<StaffPresence>();
    builder.Services.AddSingleton<ProviderTokenService>();
    builder.Services.AddSingleton<AuthorBlocking>();
}
if (isSubmissionDeployment)
{
    builder.Services.AddSingleton<SubmissionProtection>();
    builder.Services.AddSingleton<TurnstileVerifier>();
    builder.Services.AddHttpClient(TurnstileVerifier.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EventWall/1.0");
    });
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
}
builder.Services.AddSingleton<SourceHealthRegistry>();
builder.Services.AddSingleton<EventPostIngestor>();
builder.Services.AddSingleton<XReadBudget>();
builder.Services.AddHttpClient("social", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("EventWall/1.0");
});
if (isAdminDeployment && demoMode)
{
    // Demo mode never calls social provider APIs; it streams synthetic posts instead.
    builder.Services.AddHostedService<DemoFeedWorker>();
}
else if (isAdminDeployment)
{
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<XAuthorProfileResolver>();
    builder.Services.AddHostedService<SocialPollingWorker>();
    builder.Services.AddSingleton<IBlueskyJetstreamClient, BlueskyJetstreamClient>();
    builder.Services.AddSingleton<BlueskyCursorStore>();
    builder.Services.AddSingleton<BlueskyProfileResolver>();
    builder.Services.AddHostedService<BlueskyJetstreamWorker>();
    builder.Services.AddHostedService<TwitchEventSubWorker>();
}
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "EventWall.Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = cookieSecurePolicy;
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

var keyDirectory = builder.Configuration["DATA_PROTECTION_KEY_PATH"];
var protection = builder.Services.AddDataProtection().SetApplicationName($"EventWall.{deploymentRole}");
if (!string.IsNullOrWhiteSpace(keyDirectory))
{
    Directory.CreateDirectory(keyDirectory);
    protection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
}

if (isAdminDeployment)
{
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "EventWall.Staff";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = cookieSecurePolicy;
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            options.Events.OnValidatePrincipal = context =>
                context.HttpContext.RequestServices.GetRequiredService<TeamAccessService>().ValidatePrincipalAsync(context);
        });
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy(TeamAccessService.StaffPolicy, policy => policy.RequireRole(TeamAccessService.OwnerRole, TeamAccessService.MemberRole));
        options.AddPolicy(TeamAccessService.OwnerPolicy, policy => policy.RequireRole(TeamAccessService.OwnerRole));
    });
}

var allowedOrigins = isSubmissionDeployment ? [] : builder.Configuration.GetSection("Public:CorsOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins).WithMethods("GET", "POST", "OPTIONS")
            .WithHeaders("Content-Type", "X-CSRF-TOKEN", "Idempotency-Key");
    }
}));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("staff-login", context => RateLimitPartition.GetFixedWindowLimiter(
        GetClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 6,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("portal-submit", context => RateLimitPartition.GetFixedWindowLimiter(
        GetClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromMinutes(3),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter("all-requests", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5000,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) : 60;
        context.HttpContext.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SubmissionProtection.Record("rate-limited");
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            title = "Too many requests",
            status = StatusCodes.Status429TooManyRequests,
            detail = "Please wait a moment and try again.",
            code = "rate-limited",
            retryAfterSeconds = retry
        }, cancellationToken);
    };
});

var app = builder.Build();
if (isSubmissionDeployment)
{
    // Fail at startup, not on the first attendee send, when Turnstile settings are incomplete.
    app.Services.GetRequiredService<TurnstileVerifier>();
}

var wallSiteUrl = isAdminDeployment
    ? DeploymentRole.AdminSiteUrl(builder.Configuration, "Public:WallSiteUrl", app.Environment.IsDevelopment(), app.Logger)
    : null;
if (isAdminDeployment)
{
    submissionSiteUrl = DeploymentRole.AdminSiteUrl(builder.Configuration, "Public:SubmissionSiteUrl",
        app.Environment.IsDevelopment(), app.Logger);
}

if (app.Environment.IsProduction() || app.Environment.IsStaging())
{
    app.UseExceptionHandler();
    app.UseHsts();
    if (string.IsNullOrWhiteSpace(builder.Configuration["WEBSITE_SITE_NAME"]))
    {
        app.UseHttpsRedirection();
    }
}

app.Use(async (context, next) =>
{
    if (app.Environment.IsStaging())
    {
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }
    await next();
});
app.UseCors();
if (isAdminDeployment)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (isSubmissionDeployment && path == "/api/public/submissions" &&
        context.Request.ContentLength > 16 * 1024)
    {
        if (context.Request.Protocol.StartsWith("HTTP/1.", StringComparison.Ordinal))
            context.Response.Headers.Connection = "close";
        await Results.Problem(statusCode: 413, title: "Submission body is too large.").ExecuteAsync(context);
        return;
    }
    var isStaffWrite = isAdminDeployment && path.StartsWithSegments("/api/admin") &&
        !HttpMethods.IsGet(context.Request.Method) &&
        !HttpMethods.IsHead(context.Request.Method) &&
        !HttpMethods.IsOptions(context.Request.Method) &&
        !path.StartsWithSegments("/api/admin/login");

    if (isStaffWrite && context.User.Identity?.IsAuthenticated == true)
    {
        try
        {
            await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Invalid anti-forgery token",
                status = StatusCodes.Status400BadRequest
            });
            return;
        }
    }

    await next();
});

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<EventWallDbContext>();
    if (isAdminDeployment)
    {
        if (builder.Configuration.GetValue("Database:ApplyMigrations", app.Environment.IsDevelopment()))
        {
            await database.Database.MigrateAsync();
        }
        else if ((await database.Database.GetPendingMigrationsAsync()).Any())
        {
            throw new InvalidOperationException("Apply pending database migrations with the deployment identity before starting the admin app.");
        }
        await EventSeeder.SeedAsync(database, builder.Configuration);
    }
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow: /\n", "text/plain"));

if (!isSubmissionDeployment)
{
    app.MapGet("/api/public/event", async (IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        return Results.Ok(new PublicEventDto(settings.Slug, settings.Name, settings.SubmissionsOpen,
            demoMode, questionHashtag, submissionSiteUrl, wallSiteUrl));
    });
}

if (deploymentRole == SiteRole.Public)
{
    app.MapGet("/api/public/wall", async (
        IDbContextFactory<EventWallDbContext> factory,
        long? afterRevision,
        long? throughRevision,
        string? knownIds,
        string? sort,
        CancellationToken cancellationToken) =>
    {
        if (sort is not (null or "post" or "approved") ||
            afterRevision is not null && throughRevision is not null)
        {
            return Results.BadRequest(new { title = "The wall sort or revision parameters are invalid." });
        }

        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var known = ParseIds(knownIds);
        var livePosts = database.Posts.AsNoTracking()
            .Where(post => post.EventSlug == settings.Slug && post.Status == PostStatus.Live);
        var visiblePosts = throughRevision is null ? livePosts : livePosts.Where(post =>
            post.PublishedRevision == null || post.PublishedRevision <= throughRevision);
        var posts = await OrderWallPosts(visiblePosts, sort)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (afterRevision is not null)
        {
            var selected = posts.Select(post => post.Id).ToHashSet();
            var newPosts = await OrderWallPosts(livePosts.Where(post =>
                    post.PublishedRevision > afterRevision && !known.Contains(post.Id) && !selected.Contains(post.Id)), "approved")
                .Take(100)
                .ToListAsync(cancellationToken);
            posts.AddRange(newPosts);
            posts = OrderWallPosts(posts.AsQueryable(), sort).ToList();
        }
        var newCount = afterRevision is null ? 0 : await database.Posts.AsNoTracking()
            .CountAsync(post => post.EventSlug == settings.Slug &&
                post.Status == PostStatus.Live &&
                post.PublishedRevision > afterRevision &&
                !known.Contains(post.Id), cancellationToken);

        var promos = await Promotions.ListAsync(database, settings.Slug, cancellationToken, activeOnly: true);
        return Results.Ok(new WallSnapshotDto(settings.Revision, newCount, posts.Select(PublicPostDto.From).ToList(), promos));
    });

    app.MapGet("/api/public/wall/items/{id:guid}", async (
        Guid id,
        IDbContextFactory<EventWallDbContext> factory,
        CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var post = await database.Posts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id && item.Status == PostStatus.Live, cancellationToken);
        return post is null ? Results.NotFound() : Results.Ok(PublicPostDto.From(post));
    });

    app.MapGet("/api/public/wall/changes", async (
        long sinceRevision,
        string? knownIds,
        IDbContextFactory<EventWallDbContext> factory,
        CancellationToken cancellationToken) =>
        Results.Ok(await GetWallChangesAsync(factory, sinceRevision, ParseIds(knownIds), null, cancellationToken)));

    app.MapPost("/api/public/wall/changes", async (
        WallChangesRequest request,
        IDbContextFactory<EventWallDbContext> factory,
        CancellationToken cancellationToken) =>
    {
        // A sorted snapshot can include 100 posts, 200 highlighted posts, and 50 pins.
        if (request.SinceRevision < 0 || request.KnownThroughRevision is < 0 ||
            request.KnownIds is null || request.KnownIds.Count > 350)
        {
            return Results.BadRequest(new { title = "The wall revision or known post IDs are invalid." });
        }

        var known = request.KnownIds.Where(id => id != Guid.Empty).Distinct().ToList();
        return Results.Ok(await GetWallChangesAsync(factory, request.SinceRevision, known, request.KnownThroughRevision, cancellationToken));
    });

}

if (isSubmissionDeployment)
{
    app.MapGet("/api/public/submission-config", (HttpContext context, SubmissionProtection guard, TurnstileVerifier challenge) =>
        Results.Ok(new SubmissionConfigDto(submissionSlug, submissionName, demoMode, guard.Issue(context, submissionSlug), challenge.SiteKey)));

    app.MapPost("/api/public/submissions", async Task<IResult> (
        SubmissionRequest request,
        HttpContext httpContext,
        SubmissionProtection guard,
        TurnstileVerifier challenge,
        IDbContextFactory<EventWallDbContext> factory,
        CancellationToken cancellationToken) =>
    {
        if (!guard.TryValidate(httpContext, submissionSlug, out var browser))
        {
            SubmissionProtection.Record("invalid-token");
            return Results.Problem(statusCode: 403, title: "Please refresh your submission token and try again.",
                extensions: new Dictionary<string, object?> { ["code"] = "submission-token-invalid" });
        }
        using var lease = guard.Acquire(browser);
        if (!lease.IsAcquired)
        {
            var retry = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) : 3600;
            httpContext.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SubmissionProtection.Record("rate-limited");
            return Results.Problem(statusCode: 429, title: "Too many submissions. Please wait before trying again.",
                extensions: new Dictionary<string, object?> { ["code"] = "rate-limited", ["retryAfterSeconds"] = retry });
        }
        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
        if (!Guid.TryParse(idempotencyKey, out var idempotencyId))
        {
            return Results.BadRequest(new { title = "A valid Idempotency-Key is required." });
        }

        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            SubmissionProtection.Record("honeypot");
            return Results.Accepted(value: new { received = true });
        }

        var text = request.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["text"] = ["Message text is required and must be 2,000 characters or fewer."]
            });
        }

        var displayName = request.DisplayName?.Trim();
        if (displayName?.Length > 80)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["displayName"] = ["Display name must be 80 characters or fewer."]
            });
        }

        if (challenge.Enabled)
        {
            var outcome = await challenge.VerifyAsync(
                httpContext.Request.Headers[TurnstileVerifier.HeaderName].ToString(), GetClientKey(httpContext), cancellationToken);
            if (outcome == TurnstileOutcome.Unavailable)
            {
                SubmissionProtection.Record("challenge-unavailable");
                return Results.Problem(statusCode: 503, title: "The verification service is unavailable. Please try again shortly.",
                    extensions: new Dictionary<string, object?> { ["code"] = "challenge-unavailable" });
            }
            if (outcome == TurnstileOutcome.Failed)
            {
                SubmissionProtection.Record("challenge-failed");
                return Results.Problem(statusCode: 403, title: "Please complete the verification check and try again.",
                    extensions: new Dictionary<string, object?> { ["code"] = "challenge-failed" });
            }
        }

        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var settings = await EventSeeder.LockEventForPostIngestionAsync(database, cancellationToken);
        var externalId = $"portal:{idempotencyId:D}";
        var authorName = string.IsNullOrWhiteSpace(displayName) ? "Attendee" : displayName;
        var existing = await database.Posts.AsNoTracking()
            .SingleOrDefaultAsync(post => post.EventSlug == settings.Slug &&
                post.Source == PostSource.Portal && post.ExternalId == externalId, cancellationToken);
        if (existing is not null)
        {
            return SubmissionReceipt(existing, text, authorName);
        }
        if (!settings.SubmissionsOpen)
        {
            return Results.Problem(statusCode: 409, title: "Submissions are currently paused.",
                extensions: new Dictionary<string, object?> { ["code"] = "submissions-paused" });
        }

        var post = new EventPost
        {
            Id = Guid.NewGuid(),
            EventSlug = settings.Slug,
            Source = PostSource.Portal,
            ExternalId = externalId,
            Text = text,
            AuthorName = authorName,
            ReceivedAtUtc = DateTime.UtcNow,
            Status = PostStatus.Pending
        };
        database.Posts.Add(post);
        await PostFiltering.ApplyToNewPostAsync(database, settings, post, cancellationToken);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 } or
                SqlException { Number: 2601 or 2627 })
        {
            await transaction.RollbackAsync(cancellationToken);
            await using var retryDatabase = await factory.CreateDbContextAsync(cancellationToken);
            var original = await retryDatabase.Posts.AsNoTracking().SingleOrDefaultAsync(
                item => item.EventSlug == settings.Slug && item.Source == PostSource.Portal && item.ExternalId == externalId,
                cancellationToken);
            if (original is null) throw;
            return SubmissionReceipt(original, text, authorName);
        }
        SubmissionProtection.Record("received");
        return Results.Accepted(value: new { received = true });
    }).RequireRateLimiting("portal-submit");
}

if (isAdminDeployment)
{
    app.MapGet("/api/admin/session-status", (HttpContext context, StaffPasscodeService passcode) =>
        Results.Ok(new
        {
            configured = passcode.IsConfigured,
            authenticated = context.User.Identity?.IsAuthenticated == true,
            username = context.User.Identity?.Name,
            isAdministrator = context.User.IsInRole(TeamAccessService.OwnerRole)
        }));

    app.MapPost("/api/admin/login", async (
        LoginRequest request,
        HttpContext context,
        StaffPasscodeService passcode,
        TeamAccessService team,
        CancellationToken cancellationToken) =>
    {
        if (!passcode.IsConfigured)
        {
            return Results.Problem(
                title: "Staff access is not configured",
                detail: "Set STAFF_PASSCODE to a strong value before enabling staff access.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var principal = await team.AuthenticateAsync(request.Username, request.Passcode, cancellationToken);
        if (principal is null)
        {
            return Results.Unauthorized();
        }

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            });
        return Results.Ok(new { authenticated = true, username = principal.Identity!.Name,
            isAdministrator = principal.IsInRole(TeamAccessService.OwnerRole) });
    }).AllowAnonymous().RequireRateLimiting("staff-login");

    var admin = app.MapGroup("/api/admin").RequireAuthorization(TeamAccessService.StaffPolicy);
    var owner = admin.MapGroup("").RequireAuthorization(TeamAccessService.OwnerPolicy);

    admin.MapPost("/presence", async (HttpContext context, StaffPresence presence, CancellationToken cancellationToken) =>
        Results.Ok(await presence.HeartbeatAsync(context.User, cancellationToken)));

    admin.MapGet("/promos", async (IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        return Results.Ok(await Promotions.ListAsync(database, settings.Slug, cancellationToken));
    });

    admin.MapPost("/promos", async (PromoRequest request,
        IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        var errors = Promotions.Validate(request);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, title: string.Join(" ", errors.Values.SelectMany(messages => messages)));
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var promo = new EventPromo
        {
            Id = Guid.NewGuid(), EventSlug = settings.Slug, Title = request.Title!.Trim(), Description = request.Description!.Trim(),
            Url = string.IsNullOrWhiteSpace(request.Url) ? null : request.Url.Trim(),
            CreatedAtUtc = DateTime.UtcNow, Version = 1
        };
        database.Promos.Add(promo);
        await database.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/admin/promos/{promo.Id}",
            new PromoDto(promo.Id, promo.Title, promo.Description, promo.Url, promo.CreatedAtUtc, promo.Active, promo.Version));
    });

    admin.MapPost("/promos/{id:guid}", async (Guid id, PromoRequest request,
        IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        var errors = Promotions.Validate(request);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, title: string.Join(" ", errors.Values.SelectMany(messages => messages)));
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var promo = await database.Promos.SingleOrDefaultAsync(item => item.Id == id && item.EventSlug == settings.Slug, cancellationToken);
        if (promo is null) return Results.NotFound(new { title = "This promo no longer exists." });
        if (promo.Version != request.Version)
            return Results.Conflict(new { title = "This promo changed in another staff session. Refresh promos before saving again." });
        promo.Title = request.Title!.Trim();
        promo.Description = request.Description!.Trim();
        promo.Url = string.IsNullOrWhiteSpace(request.Url) ? null : request.Url.Trim();
        promo.Version++;
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { title = "This promo changed in another staff session. Refresh promos before saving again." });
        }
        return Results.Ok(new PromoDto(promo.Id, promo.Title, promo.Description, promo.Url, promo.CreatedAtUtc, promo.Active, promo.Version));
    });

    admin.MapPost("/promos/{id:guid}/active", async (Guid id, PromoActiveRequest request,
        IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        if (request.Version < 1 || request.Active is null)
            return Results.BadRequest(new { title = "A promo version and active state are required." });
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var promo = await database.Promos.SingleOrDefaultAsync(item => item.Id == id && item.EventSlug == settings.Slug, cancellationToken);
        if (promo is null) return Results.NotFound(new { title = "This promo no longer exists." });
        if (promo.Version != request.Version)
            return Results.Conflict(new { title = "This promo changed in another staff session. Refresh promos before changing its active state." });
        promo.Active = request.Active.Value;
        promo.Version++;
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { title = "This promo changed in another staff session. Refresh promos before changing its active state." });
        }
        return Results.Ok(new PromoDto(promo.Id, promo.Title, promo.Description, promo.Url, promo.CreatedAtUtc, promo.Active, promo.Version));
    });

    admin.MapPost("/promos/{id:guid}/delete", async (Guid id, PromoVersionRequest request,
        IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var promo = await database.Promos.SingleOrDefaultAsync(item => item.Id == id && item.EventSlug == settings.Slug, cancellationToken);
        if (promo is null) return Results.NotFound(new { title = "This promo no longer exists." });
        if (promo.Version != request.Version)
            return Results.Conflict(new { title = "This promo changed in another staff session. Refresh promos before deleting again." });
        database.Promos.Remove(promo);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { title = "This promo changed in another staff session. Refresh promos before deleting again." });
        }
        return Results.NoContent();
    });

    owner.MapGet("/team", async (IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        return Results.Ok(await database.StaffMembers.AsNoTracking().OrderBy(member => member.NormalizedUsername)
            .Select(member => new StaffMemberDto(member.Id, member.Username, member.Enabled, member.Version,
                DateTime.SpecifyKind(member.CreatedAtUtc, DateTimeKind.Utc), DateTime.SpecifyKind(member.CodeRotatedAtUtc, DateTimeKind.Utc),
                member.LastSignedInAtUtc.HasValue ? DateTime.SpecifyKind(member.LastSignedInAtUtc.Value, DateTimeKind.Utc) : null,
                database.ModerationActions.Count(action => action.StaffMemberId == member.Id)))
            .ToListAsync(cancellationToken));
    });

    owner.MapPost("/team", async (StaffInviteRequest request, TeamAccessService team,
        IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        var username = request.Username?.Trim();
        if (!TeamAccessService.IsValidUsername(username))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] =
                ["Use 3-40 letters, numbers, dots, hyphens or underscores, starting with a letter or number. Admin is reserved."] },
                title: "Use a unique 3-40 character username. Admin is reserved.");
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var member = new StaffMember { Id = Guid.NewGuid(), Username = username!, NormalizedUsername = username!.ToUpperInvariant(),
            AccessCodeHash = "", CreatedAtUtc = DateTime.UtcNow };
        var code = team.RotateCode(member);
        database.StaffMembers.Add(member);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is
            SqliteException { SqliteExtendedErrorCode: 2067 } or SqlException { Number: 2601 or 2627 })
        {
            return Results.Conflict(new { title = "That username is already assigned. Disabled members keep their usernames." });
        }
        return Results.Created($"/api/admin/team/{member.Id}", new StaffInviteDto(StaffDto(member, 0), code));
    });

    owner.MapPost("/team/{id:guid}/code", async (Guid id, StaffMemberVersionRequest request, TeamAccessService team,
        IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var member = await database.StaffMembers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (member is null) return Results.NotFound();
        if (member.Version != request.Version) return Results.Conflict(new { title = "This member changed. Reload the team before rotating their code." });
        var code = team.RotateCode(member);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { title = "This member changed. Reload the team before rotating their code." }); }
        var actions = await database.ModerationActions.CountAsync(action => action.StaffMemberId == id, cancellationToken);
        return Results.Ok(new StaffInviteDto(StaffDto(member, actions), code));
    });

    owner.MapPost("/team/{id:guid}/status", async (Guid id, StaffMemberStatusRequest request,
        IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        if (request.Enabled) return Results.BadRequest(new { title = "Restore access by creating a new invitation code." });
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var member = await database.StaffMembers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (member is null) return Results.NotFound();
        if (member.Version != request.Version) return Results.Conflict(new { title = "This member changed. Reload the team before changing access." });
        member.Enabled = request.Enabled;
        member.Version++;
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { title = "This member changed. Reload the team before changing access." }); }
        var actions = await database.ModerationActions.CountAsync(action => action.StaffMemberId == id, cancellationToken);
        return Results.Ok(StaffDto(member, actions));
    });

    admin.MapPost("/logout", async (HttpContext context, StaffPresence presence) =>
    {
        presence.Remove(context.User);
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    });

    admin.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new { requestToken = tokens.RequestToken });
    });

    admin.MapGet("/queue", async (
        IDbContextFactory<EventWallDbContext> factory,
        PostStatus? status,
        PostSource? source,
        string? search,
        CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var query = database.Posts.AsNoTracking().Where(post => post.EventSlug == settings.Slug);
        if (status is not null)
        {
            query = query.Where(post => post.Status == status);
        }
        if (source is not null)
        {
            query = query.Where(post => post.Source == source);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(post => post.Text.Contains(term) || post.AuthorName.Contains(term));
        }

        var posts = await query.OrderByDescending(post => post.SourceCreatedAtUtc ?? post.ReceivedAtUtc)
            .ThenByDescending(post => post.ReceivedAtUtc).ThenByDescending(post => post.Id)
            .Take(200)
            .ToListAsync(cancellationToken);
        var blocks = await database.BlockedAuthors.AsNoTracking()
            .Where(item => item.EventSlug == settings.Slug).ToListAsync(cancellationToken);
        var blockedAccounts = blocks.Select(item => (item.Source, item.AuthorExternalId)).ToHashSet();
        var postIds = posts.Select(post => post.Id).ToArray();
        var reviewers = await database.ModerationActions.AsNoTracking().Where(action => postIds.Contains(action.PostId))
            .GroupBy(action => action.PostId)
            .Select(group => new { Id = group.Key, Username = group.OrderByDescending(action => action.CreatedAtUtc)
                .Select(action => action.StaffUsername).FirstOrDefault() })
            .ToDictionaryAsync(item => item.Id, item => item.Username, cancellationToken);
        return Results.Ok(posts.Select(post => StaffPostDto.From(post,
            post.AuthorExternalId is not null && blockedAccounts.Contains((post.Source, post.AuthorExternalId)),
            reviewers.GetValueOrDefault(post.Id))));
    });

    admin.MapPost("/posts/{id:guid}/block-author", async (
        Guid id, ModerationRequest request, AuthorBlocking blocking, HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var result = await blocking.BlockAsync(id, request.Version,
            context.User.FindFirstValue("staff_session") ?? "unknown", cancellationToken,
            TeamAccessService.Username(context.User), TeamAccessService.MemberId(context.User));
        return result switch
        {
            BlockAuthorResult.Blocked => Results.Ok(new { blocked = true }),
            BlockAuthorResult.NotFound => Results.NotFound(),
            BlockAuthorResult.VersionConflict => Results.Conflict(new { title = "This item changed. Reload it before blocking its author." }),
            _ => Results.BadRequest(new { title = "Blocking requires a stable social account ID. Anonymous and manual names cannot be blocked." })
        };
    });

    admin.MapPost("/posts/{id:guid}/approve", async (
        Guid id,
        ModerationRequest request,
        IDbContextFactory<EventWallDbContext> factory,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await ChangePostStateAsync(id, PostStatus.Live, request.Version, null, factory, context, cancellationToken));

    admin.MapPost("/posts/{id:guid}/reject", async (
        Guid id,
        ModerationRequest request,
        IDbContextFactory<EventWallDbContext> factory,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await ChangePostStateAsync(id, PostStatus.Rejected, request.Version, request.Reason, factory, context, cancellationToken));

    admin.MapPost("/posts/{id:guid}/withdraw", async (
        Guid id,
        ModerationRequest request,
        IDbContextFactory<EventWallDbContext> factory,
        HttpContext context,
        CancellationToken cancellationToken) =>
        await ChangePostStateAsync(id, PostStatus.Withdrawn, request.Version, request.Reason, factory, context, cancellationToken));

    owner.MapPost("/manual", async (
        ManualPostRequest request,
        IDbContextFactory<EventWallDbContext> factory,
        CancellationToken cancellationToken) =>
    {
        var text = request.Text?.Trim();
        var sourceUrlIsValid = request.SourceUrl is null ||
            Uri.TryCreate(request.SourceUrl, UriKind.Absolute, out var sourceUri) &&
            sourceUri.Scheme == Uri.UriSchemeHttps;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000 ||
            request.AuthorName?.Trim().Length > 80 ||
            !sourceUrlIsValid)
        {
            return Results.BadRequest(new { title = "Text, attribution, or HTTPS source link is invalid." });
        }

        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var settings = await EventSeeder.LockEventForPostIngestionAsync(database, cancellationToken);
        var post = new EventPost
        {
            Id = Guid.NewGuid(),
            EventSlug = settings.Slug,
            Source = PostSource.Manual,
            ExternalId = Guid.NewGuid().ToString("N"),
            Text = text,
            AuthorName = string.IsNullOrWhiteSpace(request.AuthorName) ? "Manually supplied (unverified)" : request.AuthorName.Trim(),
            SourceUrl = request.SourceUrl,
            ReceivedAtUtc = DateTime.UtcNow,
            Status = PostStatus.Pending
        };
        database.Posts.Add(post);
        await PostFiltering.ApplyToNewPostAsync(database, settings, post, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/api/admin/queue", StaffPostDto.From(post));
    });

    owner.MapGet("/settings", async (IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        return Results.Ok(AdminEventDto.From(settings));
    });

    owner.MapPost("/settings/submissions", async (
        SubmissionSettingsRequest request,
        IDbContextFactory<EventWallDbContext> factory,
        CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        settings.SubmissionsOpen = request.Open;
        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(AdminEventDto.From(settings));
    });

    owner.MapPost("/settings/filters", async (
        PostFilterSettingsRequest request,
        IDbContextFactory<EventWallDbContext> factory,
        CancellationToken cancellationToken) =>
    {
        var errors = PostFiltering.ValidateSettings(request.PostFilters);
        if (request.Version < 0)
            errors["version"] = ["A non-negative settings version is required."];
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors,
                title: string.Join(" ", errors.Values.SelectMany(messages => messages)));
        }

        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        if (settings.FilterSettingsVersion != request.Version)
            return Results.Conflict(new { title = "Post filters changed in another staff session. Reload settings before saving again." });
        settings.PostFiltersJson = JsonSerializer.Serialize(PostFiltering.NormalizeSettings(request.PostFilters!));
        settings.FilterSettingsVersion++;
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return Results.Ok(AdminEventDto.From(settings));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { title = "Event settings changed in another staff session. Reload settings before saving again." });
        }
    });

    owner.MapGet("/sources", async (IConfiguration configuration, SourceHealthRegistry health,
        IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var running = await database.Events.AsNoTracking().Select(settings => settings.YouTubeCollectionRunning)
            .SingleAsync(cancellationToken);
        return Results.Ok(SourceSetupStatus.Build(configuration, health, running));
    });

    owner.MapPost("/sources/youtube/collection", async Task<IResult> (
        YouTubeCollectionRequest request,
        IConfiguration configuration,
        SourceHealthRegistry health,
        IDbContextFactory<EventWallDbContext> factory,
        CancellationToken cancellationToken) =>
    {
        if (request.Running is not { } running)
            return Results.BadRequest(new { title = "Specify whether YouTube collection should be running." });
        if (DemoMode.IsEnabled(configuration))
            return Results.BadRequest(new { title = "YouTube collection controls are unavailable for simulated demo feeds." });
        var source = SourceSetupStatus.Build(configuration).Single(item => item.Provider == "YouTube");
        if (running && !source.CollectionAvailable)
            return Results.BadRequest(new { title = "Enable YouTube and configure its credentials and policy approval on the admin server before starting collection." });

        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var changed = settings.YouTubeCollectionRunning != running;
        settings.YouTubeCollectionRunning = running;
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { title = "Collection settings changed in another staff session. Refresh source status and try again." });
        }
        if (changed && running && health.Get("YouTube")?.State != "budget-exhausted")
            health.Report("YouTube", "test-required", "Collection was started; waiting for the next eligible connector check.");
        return Results.Ok(SourceSetupStatus.Build(configuration, health, running));
    });

    owner.MapGet("/analytics", async (string? from, string? through, string? source, string? timeZone,
        IDbContextFactory<EventWallDbContext> factory, SourceHealthRegistry health, CancellationToken cancellationToken) =>
    {
        ReportFilters filters;
        try { filters = EventReportQuery.ParseFilters(from, through, source, timeZone); }
        catch (ArgumentException exception) { return Results.BadRequest(new { title = exception.Message }); }
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        try
        {
            return Results.Ok(await EventReportQuery.BuildAsync(database, builder.Configuration, health, filters, cancellationToken));
        }
        catch (ArgumentException exception) { return Results.BadRequest(new { title = exception.Message }); }
    });

    owner.MapGet("/export.json", async (IDbContextFactory<EventWallDbContext> factory, CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var posts = await database.Posts.AsNoTracking().Where(post => post.EventSlug == settings.Slug)
            .OrderBy(post => post.ReceivedAtUtc)
            .ToListAsync(cancellationToken);
        var exports = posts.Select(post =>
        {
            var contentAllowed = CanExportContent(post.Source, builder.Configuration);
            return new
            {
                post.Id,
                post.Source,
                externalId = contentAllowed ? post.ExternalId : null,
                authorName = contentAllowed ? post.AuthorName : null,
                text = contentAllowed ? post.Text : null,
                sourceUrl = contentAllowed ? post.SourceUrl : null,
                authorProfileUrl = contentAllowed ? post.AuthorProfileUrl : null,
                authorAvatarUrl = contentAllowed ? post.AuthorAvatarUrl : null,
                images = contentAllowed ? post.Images : [],
                post.ReceivedAtUtc,
                post.SourceCreatedAtUtc,
                post.DecidedAtUtc,
                post.Status,
                post.FilterReasons,
                contentOmittedBySourcePolicy = !contentAllowed
            };
        });
        return Results.Json(new
        {
            exportedAtUtc = DateTime.UtcNow,
            eventSlug = settings.Slug,
            policyNote = "Social content fields are included only when the source's ExportContentApproved setting is true.",
            posts = exports
        });
    });

    owner.MapGet("/export.csv", async (
        IDbContextFactory<EventWallDbContext> factory,
        IConfiguration configuration,
        CancellationToken cancellationToken) =>
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var posts = await database.Posts.AsNoTracking().Where(post => post.EventSlug == settings.Slug)
            .OrderBy(post => post.ReceivedAtUtc).ToListAsync(cancellationToken);
        var rows = new List<string>
        {
        "id,source,externalId,authorName,text,sourceUrl,receivedAtUtc,status,decidedAtUtc,authorProfileUrl,authorAvatarUrl,imagesJson,filterReasons"
        };
        rows.AddRange(posts.Select(post => string.Join(',',
        [
            CsvCellEncoder.Encode(post.Id.ToString("D")),
        CsvCellEncoder.Encode(post.Source.ToString()),
        CsvCellEncoder.Encode(CanExportContent(post.Source, configuration) ? post.ExternalId : string.Empty),
        CsvCellEncoder.Encode(CanExportContent(post.Source, configuration) ? post.AuthorName : string.Empty),
        CsvCellEncoder.Encode(CanExportContent(post.Source, configuration) ? post.Text : string.Empty),
        CsvCellEncoder.Encode(CanExportContent(post.Source, configuration) ? post.SourceUrl ?? string.Empty : string.Empty),
        CsvCellEncoder.Encode(post.ReceivedAtUtc.ToString("O")),
        CsvCellEncoder.Encode(post.Status.ToString()),
        CsvCellEncoder.Encode(post.DecidedAtUtc?.ToString("O") ?? string.Empty),
        CsvCellEncoder.Encode(CanExportContent(post.Source, configuration) ? post.AuthorProfileUrl ?? string.Empty : string.Empty),
        CsvCellEncoder.Encode(CanExportContent(post.Source, configuration) ? post.AuthorAvatarUrl ?? string.Empty : string.Empty),
        CsvCellEncoder.Encode(CanExportContent(post.Source, configuration) ? post.ImagesJson : string.Empty),
        CsvCellEncoder.Encode(string.Join(';', post.FilterReasons))
        ])));
        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", rows)),
            "text/csv; charset=utf-8",
            $"eventwall-{settings.Slug}.csv");
    });
}

app.Map("/api/{**path}", () => Results.NotFound());
if (isSubmissionDeployment)
{
    app.UseAntiforgery();
    app.MapStaticAssets();
    app.MapRazorComponents<QuestionsDocument>().AddInteractiveServerRenderMode();
}
else
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.Map("/questions/{**path}", () => Results.NotFound());
    app.Map("/_blazor/{**path}", () => Results.NotFound());
    app.Map("/_framework/{**path}", () => Results.NotFound());
    app.MapFallbackToFile("index.html");
}

await app.RunAsync();

static string GetClientKey(HttpContext context) =>
    GetForwardedClientIp(context) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static string? GetForwardedClientIp(HttpContext context)
{
    if (string.IsNullOrWhiteSpace(context.RequestServices.GetRequiredService<IConfiguration>()["WEBSITE_SITE_NAME"]))
    {
        return null;
    }

    var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
    var candidate = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .LastOrDefault();
    if (System.Net.IPAddress.TryParse(candidate, out var address)) return address.ToString();
    return candidate is not null && System.Net.IPEndPoint.TryParse(candidate, out var endpoint) ? endpoint.Address.ToString() : null;
}

static IResult SubmissionReceipt(EventPost post, string text, string authorName)
{
    if (post.Text == text && post.AuthorName == authorName)
        return Results.Accepted(value: new { received = true });
    return Results.Problem(statusCode: 409, title: "This submission key was already used for a different question.",
        extensions: new Dictionary<string, object?> { ["code"] = "idempotency-conflict" });
}

static List<Guid> ParseIds(string? value) =>
    value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Take(150)
        .Select(text => Guid.TryParse(text, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty)
        .Distinct()
        .ToList() ?? [];

static IOrderedQueryable<EventPost> OrderWallPosts(IQueryable<EventPost> posts, string? sort) =>
    sort == "approved"
        ? posts.OrderByDescending(post => post.DecidedAtUtc)
            .ThenByDescending(post => post.PublishedRevision).ThenByDescending(post => post.Id)
        : posts.OrderByDescending(post => post.SourceCreatedAtUtc ?? post.ReceivedAtUtc)
            .ThenByDescending(post => post.ReceivedAtUtc).ThenByDescending(post => post.Id);

static async Task<WallChangesDto> GetWallChangesAsync(
    IDbContextFactory<EventWallDbContext> factory,
    long sinceRevision,
    List<Guid> known,
    long? knownThroughRevision,
    CancellationToken cancellationToken)
{
    await using var database = await factory.CreateDbContextAsync(cancellationToken);
    var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
    var livePosts = database.Posts.AsNoTracking()
        .Where(post => post.EventSlug == settings.Slug && post.Status == PostStatus.Live);
    var newCount = await livePosts.CountAsync(post =>
        post.PublishedRevision > sinceRevision && !known.Contains(post.Id), cancellationToken);
    var knownPosts = livePosts.Where(post => known.Contains(post.Id));
    if (knownThroughRevision is not null)
    {
        knownPosts = knownPosts.Where(post =>
            post.PublishedRevision == null || post.PublishedRevision <= knownThroughRevision);
    }
    var ids = await knownPosts
        .Select(post => post.Id).ToListAsync(cancellationToken);
    var promos = await Promotions.ListAsync(database, settings.Slug, cancellationToken, activeOnly: true);
    return new WallChangesDto(settings.Revision, newCount, ids, promos);
}

static bool CanExportContent(PostSource source, IConfiguration configuration) =>
    source is PostSource.Portal or PostSource.Manual ||
    DemoMode.IsEnabled(configuration) ||
    configuration.GetValue<bool>($"Social:{source}:ExportContentApproved");

static async Task<IResult> ChangePostStateAsync(
    Guid id,
    PostStatus target,
    int version,
    string? reason,
    IDbContextFactory<EventWallDbContext> factory,
    HttpContext context,
    CancellationToken cancellationToken)
{
    await using var database = await factory.CreateDbContextAsync(cancellationToken);
    await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
    var post = await database.Posts.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    if (post is null)
    {
        return Results.NotFound();
    }

    if (post.Version != version)
    {
        return Results.Conflict(new { title = "This item changed in another staff session. Reload it and try again." });
    }

    if (post.Status == target)
    {
        var reviewer = await database.ModerationActions.AsNoTracking().Where(action => action.PostId == post.Id)
            .OrderByDescending(action => action.CreatedAtUtc).Select(action => action.StaffUsername).FirstOrDefaultAsync(cancellationToken);
        return Results.Ok(StaffPostDto.From(post, moderatedBy: reviewer));
    }

    if (!ModerationPolicy.CanTransition(post.Status, target))
    {
        return Results.Conflict(new { title = "That moderation transition is not allowed." });
    }

    var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
    var previous = post.Status;
    settings.Revision++;
    post.Status = target;
    post.DecidedAtUtc = DateTime.UtcNow;
    post.Version++;
    post.PublishedRevision = target == PostStatus.Live ? settings.Revision : null;
    database.ModerationActions.Add(new ModerationAction
    {
        Id = Guid.NewGuid(),
        PostId = post.Id,
        FromStatus = previous,
        ToStatus = target,
        CreatedAtUtc = DateTime.UtcNow,
        StaffSessionId = context.User.FindFirstValue("staff_session") ?? "unknown",
        StaffUsername = TeamAccessService.Username(context.User),
        StaffMemberId = TeamAccessService.MemberId(context.User),
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 250)]
    });

    try
    {
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(StaffPostDto.From(post, moderatedBy: TeamAccessService.Username(context.User)));
    }
    catch (DbUpdateConcurrencyException)
    {
        await transaction.RollbackAsync(cancellationToken);
        return Results.Conflict(new { title = "This item changed in another staff session. Reload it and try again." });
    }
}

public partial class Program { }
