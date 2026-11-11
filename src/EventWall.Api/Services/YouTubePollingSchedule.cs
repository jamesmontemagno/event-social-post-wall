namespace EventWall.Api.Services;

public static class YouTubePollingSchedule
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    public static DateTime NextPollUtc(DateTime utcNow, int pollingIntervalMillis = 0) =>
        utcNow.AddMilliseconds(Math.Max(60_000, pollingIntervalMillis));

    public static DateTime NextQuotaResetUtc(DateTime utcNow)
    {
        var pacificNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, Pacific);
        var midnight = DateTime.SpecifyKind(pacificNow.Date.AddDays(1), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, Pacific);
    }
}
