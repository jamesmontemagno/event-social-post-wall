using System.Net;
using System.Text;
using System.Text.Json;
using EventWall.Api.Security;

namespace EventWall.Api.Tests;

public sealed class YouTubeRequestTests
{
    [Theory]
    [InlineData("accessNotConfigured", "Enable it in that project")]
    [InlineData("SERVICE_DISABLED", "Enable it in that project")]
    [InlineData("insufficientPermissions", "youtube.readonly")]
    [InlineData("ACCESS_TOKEN_SCOPE_INSUFFICIENT", "youtube.readonly")]
    [InlineData("forbidden", "account does not have permission")]
    [InlineData("liveChatDisabled", "Live chat is disabled")]
    [InlineData("liveChatEnded", "This live chat has ended")]
    [InlineData("liveChatNotFound", "not its video ID or watch URL")]
    [InlineData("quotaExceeded", "quota is exhausted")]
    [InlineData("dailyLimitExceeded", "quota is exhausted")]
    [InlineData("QUOTA_EXCEEDED", "quota is exhausted")]
    [InlineData("rateLimitExceeded", "rate-limiting")]
    [InlineData("invalid_grant", "refresh token")]
    [InlineData("invalid_client", "client ID and client secret")]
    [InlineData("unauthorized_client", "client ID and client secret")]
    [InlineData("invalid_scope", "consent screen")]
    [InlineData("access_denied", "consent screen")]
    [InlineData("authError", "rejected the access token")]
    public async Task Recognized_reasons_produce_safe_actionable_errors(string reason, string expected)
    {
        var json = JsonSerializer.Serialize(new
        {
            error = new
            {
                message = "private-response-value",
                errors = new[] { new { reason } }
            }
        });
        var exception = await ReadFailureAsync(json);

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("YouTube live chat request returned HTTP 403.", exception.Message);
        Assert.Contains(expected, exception.Message);
        Assert.Equal(reason is "quotaExceeded" or "dailyLimitExceeded" or "QUOTA_EXCEEDED", exception.QuotaExhausted);
        Assert.DoesNotContain("private-response-value", exception.ToString());
    }

    [Fact]
    public async Task Google_error_info_details_are_supported_after_an_unknown_legacy_reason()
    {
        var exception = await ReadFailureAsync("""
            {"error":{"errors":[{"reason":"unknown"}],
              "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"SERVICE_DISABLED"}]}}
            """);

        Assert.Contains("Enable it in that project", exception.Message);
    }

    [Fact]
    public async Task OAuth_error_strings_are_supported_without_exposing_descriptions()
    {
        var exception = await ReadFailureAsync("""
            {"error":"invalid_grant","error_description":"private-response-value"}
            """, "OAuth token refresh");

        Assert.Contains("OAuth token refresh", exception.Message);
        Assert.Contains("Google rejected the refresh token", exception.Message);
        Assert.DoesNotContain("private-response-value", exception.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>private-response-value</html>")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"error":null}""")]
    [InlineData("""{"error":{"message":"private-response-value"}}""")]
    [InlineData("""{"error":{"errors":[null,{"reason":123},{"reason":"private-response-value"}]}}""")]
    [InlineData("""{"error":{"errors":{},"details":false}}""")]
    public async Task Unrecognized_or_malformed_bodies_preserve_the_http_error_without_raw_content(string json)
    {
        var exception = await ReadFailureAsync(json);

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("Google did not return a recognized error reason", exception.Message);
        Assert.DoesNotContain("private-response-value", exception.ToString());
    }

    [Fact]
    public async Task Successful_responses_are_not_consumed()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("unchanged")
        };

        await YouTubeRequestException.EnsureSuccessAsync(response, "live chat request", default);

        Assert.Equal("unchanged", await response.Content.ReadAsStringAsync());
    }

    private static async Task<YouTubeRequestException> ReadFailureAsync(
        string json, string operation = "live chat request")
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return await Assert.ThrowsAsync<YouTubeRequestException>(() =>
            YouTubeRequestException.EnsureSuccessAsync(response, operation, default));
    }
}
