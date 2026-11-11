using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventWall.Api.Security;

public enum TurnstileOutcome { Passed, Failed, Unavailable }

public sealed class TurnstileVerifier
{
    public const string HttpClientName = "turnstile";
    public const string HeaderName = "X-Turnstile-Token";
    public const string Action = "submit-question";
    private const int MaxTokenLength = 2048;
    private static readonly Uri SiteVerify = new("https://challenges.cloudflare.com/turnstile/v0/siteverify");
    private readonly IHttpClientFactory clients;
    private readonly ILogger<TurnstileVerifier> logger;
    private readonly string secretKey;
    private readonly HashSet<string> hostnames;

    public TurnstileVerifier(IConfiguration configuration, IHttpClientFactory clients, ILogger<TurnstileVerifier> logger)
    {
        this.clients = clients;
        this.logger = logger;
        var siteKey = configuration["Turnstile:SiteKey"]?.Trim();
        secretKey = configuration["Turnstile:SecretKey"]?.Trim() ?? "";
        if (string.IsNullOrEmpty(siteKey) != string.IsNullOrEmpty(secretKey))
            throw new InvalidOperationException("Turnstile:SiteKey and Turnstile:SecretKey must be configured together.");
        if (string.IsNullOrEmpty(siteKey) && configuration.GetValue<bool>("Turnstile:Required"))
            throw new InvalidOperationException("Turnstile:Required is true, but Turnstile:SiteKey and Turnstile:SecretKey are not configured.");
        SiteKey = string.IsNullOrEmpty(siteKey) ? null : siteKey;
        hostnames = new HashSet<string>(
            configuration.GetSection("Turnstile:Hostnames").Get<string[]>()?.Select(host => host.Trim()).Where(host => host.Length > 0) ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public string? SiteKey { get; }
    public bool Enabled => SiteKey is not null;

    public async Task<TurnstileOutcome> VerifyAsync(string token, string remoteIp, CancellationToken cancellationToken)
    {
        if (token.Length is 0 or > MaxTokenLength) return TurnstileOutcome.Failed;
        var form = new Dictionary<string, string>
        {
            ["secret"] = secretKey,
            ["response"] = token,
            ["idempotency_key"] = Guid.NewGuid().ToString("D")
        };
        if (System.Net.IPAddress.TryParse(remoteIp, out _)) form["remoteip"] = remoteIp;
        SiteVerifyResponse? result;
        try
        {
            using var response = await clients.CreateClient(HttpClientName)
                .PostAsync(SiteVerify, new FormUrlEncodedContent(form), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Turnstile siteverify returned HTTP {Status}.", (int)response.StatusCode);
                return TurnstileOutcome.Unavailable;
            }
            result = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException ||
            exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Turnstile siteverify could not be reached.");
            return TurnstileOutcome.Unavailable;
        }
        if (result is null)
        {
            logger.LogWarning("Turnstile siteverify returned an empty response.");
            return TurnstileOutcome.Unavailable;
        }
        if (result.ErrorCodes?.Any(code => code is "missing-input-secret" or "invalid-input-secret" or "internal-error") == true)
        {
            logger.LogError("Turnstile siteverify rejected the server configuration: {Codes}.", string.Join(", ", result.ErrorCodes));
            return TurnstileOutcome.Unavailable;
        }
        // Cloudflare test secrets omit action and report example.com, so only enforce values that are present/configured.
        var passed = result.Success &&
            (result.Action is null || result.Action == Action) &&
            (hostnames.Count == 0 || result.Hostname is not null && hostnames.Contains(result.Hostname));
        if (!passed && result.Success)
            logger.LogWarning("Turnstile token was issued for an unexpected action or hostname.");
        return passed ? TurnstileOutcome.Passed : TurnstileOutcome.Failed;
    }

    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("hostname")] string? Hostname,
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}
