using EventWall.Api.Services;

namespace EventWall.Api.Tests;

public sealed class YouTubePollingScheduleTests
{
    [Theory]
    [InlineData(-1, 60_000)]
    [InlineData(0, 60_000)]
    [InlineData(2_000, 60_000)]
    [InlineData(10_000, 60_000)]
    [InlineData(60_000, 60_000)]
    [InlineData(90_000, 90_000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Polling_never_exceeds_one_attempt_per_minute_or_the_provider_limit(int interval, int expected)
    {
        var now = new DateTime(2026, 11, 4, 17, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddMilliseconds(expected), YouTubePollingSchedule.NextPollUtc(now, interval));
        Assert.Equal(now.AddMinutes(1), YouTubePollingSchedule.NextPollUtc(now));
    }

    [Theory]
    [InlineData("2026-01-15T20:00:00Z", "2026-01-16T08:00:00Z")]
    [InlineData("2026-07-15T20:00:00Z", "2026-07-16T07:00:00Z")]
    [InlineData("2026-03-08T07:59:59Z", "2026-03-08T08:00:00Z")]
    [InlineData("2026-03-08T08:00:00Z", "2026-03-09T07:00:00Z")]
    [InlineData("2026-11-01T07:00:00Z", "2026-11-02T08:00:00Z")]
    [InlineData("2026-11-04T17:00:00Z", "2026-11-05T08:00:00Z")]
    public void Quota_reset_is_next_midnight_Pacific_including_daylight_saving_changes(string current, string expected)
    {
        var now = DateTimeOffset.Parse(current).UtcDateTime;
        var reset = YouTubePollingSchedule.NextQuotaResetUtc(now);

        Assert.Equal(DateTimeOffset.Parse(expected).UtcDateTime, reset);
        Assert.Equal(DateTimeKind.Utc, reset.Kind);
        Assert.True(reset > now);
    }
}
