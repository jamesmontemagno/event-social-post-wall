using System.Net;
using System.Text.Json;

namespace EventWall.Api.Security;

public sealed class YouTubeRequestException : HttpRequestException
{
    public bool QuotaExhausted { get; }

    private YouTubeRequestException(HttpStatusCode statusCode, string operation, string detail, bool quotaExhausted)
        : base($"YouTube {operation} returned HTTP {(int)statusCode}. {detail}", null, statusCode)
    {
        QuotaExhausted = quotaExhausted;
    }

    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? reason = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    reason = error.GetString();
                }
                else if (error.ValueKind == JsonValueKind.Object)
                {
                    reason = ReadReason(error, "errors") ?? ReadReason(error, "details");
                }
            }
        }
        catch (JsonException)
        {
            // Proxies can return HTML or empty bodies; retain the HTTP failure without exposing raw content.
        }

        throw new YouTubeRequestException(response.StatusCode, operation,
            Describe(reason) ?? "Google did not return a recognized error reason. Check YouTube Data API v3 enablement, OAuth permissions, live chat access, and quota in Google Cloud.",
            reason is "quotaExceeded" or "dailyLimitExceeded" or "QUOTA_EXCEEDED");
    }

    private static string? ReadReason(JsonElement error, string property)
    {
        if (error.TryGetProperty(property, out var reasons) && reasons.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reasons.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String &&
                    Describe(reason.GetString()) is not null)
                    return reason.GetString();
            }
        }
        return null;
    }

    private static string? Describe(string? reason) => reason switch
    {
        "accessNotConfigured" or "serviceDisabled" or "SERVICE_DISABLED" =>
            "YouTube Data API v3 is disabled or has not been used in the OAuth client's Google Cloud project. Enable it in that project and allow a few minutes for propagation.",
        "insufficientPermissions" or "ACCESS_TOKEN_SCOPE_INSUFFICIENT" =>
            "The OAuth token lacks YouTube permissions. Authorize the channel account again with the youtube.readonly scope (or another supported YouTube scope), then update the admin server's refresh token.",
        "forbidden" =>
            "The authorized account does not have permission to read this live chat. Verify the OAuth account's channel access and the current broadcast's live chat ID.",
        "liveChatDisabled" =>
            "Live chat is disabled for this broadcast. Enable chat on the broadcast or select a broadcast with chat enabled.",
        "liveChatEnded" =>
            "This live chat has ended. Update Social__YouTube__LiveChatId to the current broadcast's live chat ID and restart the admin app.",
        "liveChatNotFound" =>
            "The live chat was not found. Use the current broadcast's live chat ID, not its video ID or watch URL.",
        "quotaExceeded" or "dailyLimitExceeded" or "QUOTA_EXCEEDED" =>
            "The Google Cloud project's YouTube Data API quota is exhausted. Check its quota usage; wait for quota to reset or request more quota.",
        "rateLimitExceeded" or "userRateLimitExceeded" or "RATE_LIMIT_EXCEEDED" =>
            "YouTube is rate-limiting live chat reads. Respect the returned polling interval and check for other collectors using the same project.",
        "invalid_grant" =>
            "Google rejected the refresh token. It may be expired or revoked. Authorize the channel account again using the configured OAuth client and update the admin server's refresh token.",
        "invalid_client" or "unauthorized_client" =>
            "Google rejected the OAuth client. Verify the admin server's client ID and client secret, and that the refresh token was issued to that client.",
        "invalid_scope" or "access_denied" =>
            "Google rejected OAuth authorization. Check the consent screen, authorized account, and requested YouTube scopes, then authorize again.",
        "authError" =>
            "Google rejected the access token. Verify the OAuth authorization and refresh token for the channel account.",
        _ => null
    };
}
