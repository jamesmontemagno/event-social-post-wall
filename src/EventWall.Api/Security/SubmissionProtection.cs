using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;

namespace EventWall.Api.Security;

public sealed class SubmissionProtection : IDisposable
{
    public const string CookieName = "EventWall.Submission";
    public const string TokenPurpose = "EventWall.Submission.Token.v1";
    private static readonly Meter Meter = new("EventWall.Submissions");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("eventwall.submission.guards");
    private readonly IDataProtector cookieProtector;
    private readonly ITimeLimitedDataProtector tokenProtector;
    private readonly PartitionedRateLimiter<string> hourly = PartitionedRateLimiter.Create<string, string>(browser =>
        RateLimitPartition.GetSlidingWindowLimiter(browser, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromHours(1),
            SegmentsPerWindow = 12,
            QueueLimit = 0,
            AutoReplenishment = true
        }));

    public SubmissionProtection(IDataProtectionProvider provider)
    {
        cookieProtector = provider.CreateProtector("EventWall.Submission.Browser.v1");
        tokenProtector = provider.CreateProtector(TokenPurpose).ToTimeLimitedDataProtector();
    }

    public string Issue(HttpContext context, string slug)
    {
        var browser = ReadBrowser(context);
        if (browser is null)
        {
            browser = Guid.NewGuid().ToString("N");
            context.Response.Cookies.Append(CookieName, cookieProtector.Protect(browser), new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps || !context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment(),
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true
            });
        }
        return tokenProtector.Protect($"{slug}:{browser}", TimeSpan.FromMinutes(30));
    }

    public bool TryValidate(HttpContext context, string slug, out string browser)
    {
        browser = ReadBrowser(context) ?? "";
        var token = context.Request.Headers["X-Submission-Token"].ToString();
        if (browser.Length == 0 || token.Length is 0 or > 4096) return false;
        if (context.Request.Headers.TryGetValue("Origin", out var origin))
        {
            var hosted = !context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
            if (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var source) ||
                !source.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase) ||
                source.Scheme != (hosted ? "https" : context.Request.Scheme))
                return false;
        }
        try
        {
            return tokenProtector.Unprotect(token) == $"{slug}:{browser}";
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public RateLimitLease Acquire(string browser) => hourly.AttemptAcquire(browser);

    public static void Record(string reason) => Outcomes.Add(1, new KeyValuePair<string, object?>("reason", reason));

    private string? ReadBrowser(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var value) || value.Length > 4096) return null;
        try
        {
            var browser = cookieProtector.Unprotect(value);
            return Guid.TryParseExact(browser, "N", out _) ? browser : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Dispose() => hourly.Dispose();
}
