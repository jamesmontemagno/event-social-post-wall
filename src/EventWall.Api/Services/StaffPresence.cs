using System.Security.Claims;
using EventWall.Api.Data;
using EventWall.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public sealed record OnlineStaffDto(string Username, bool IsAdministrator, DateTimeOffset LastSeenAtUtc);

public sealed class StaffPresence(IDbContextFactory<EventWallDbContext> factory, TimeProvider clock)
{
    private sealed record Session(Guid? MemberId, int? Version, OnlineStaffDto Staff);
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public async Task<OnlineStaffDto[]> HeartbeatAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var sessionId = user.FindFirstValue("staff_session")
            ?? throw new InvalidOperationException("An authenticated staff session is required.");
        var memberId = TeamAccessService.MemberId(user);
        var version = memberId.HasValue
            ? int.Parse(user.FindFirstValue(TeamAccessService.VersionClaim)!, System.Globalization.CultureInfo.InvariantCulture)
            : (int?)null;
        var now = clock.GetUtcNow();
        Session[] snapshot;
        lock (gate)
        {
            foreach (var key in sessions.Where(item => now - item.Value.Staff.LastSeenAtUtc >= TimeSpan.FromMinutes(5))
                .Select(item => item.Key).ToArray())
                sessions.Remove(key);
            sessions[sessionId] = new(memberId, version,
                new(TeamAccessService.Username(user), user.IsInRole(TeamAccessService.OwnerRole), now));
            snapshot = sessions.Values.ToArray();
        }

        // Check every listed member, not only the caller, so revoked sessions disappear immediately.
        var memberIds = snapshot.Where(item => item.MemberId.HasValue).Select(item => item.MemberId!.Value).Distinct().ToArray();
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var versions = await database.StaffMembers.AsNoTracking()
            .Where(member => memberIds.Contains(member.Id) && member.Enabled)
            .ToDictionaryAsync(member => member.Id, member => member.Version, cancellationToken);
        return snapshot.Where(item => item.MemberId is not { } id ||
                versions.TryGetValue(id, out var currentVersion) && currentVersion == item.Version)
            .GroupBy(item => item.MemberId)
            .Select(group => group.MaxBy(item => item.Staff.LastSeenAtUtc)!.Staff)
            .OrderByDescending(item => item.IsAdministrator)
            .ThenBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Remove(ClaimsPrincipal user)
    {
        if (user.FindFirstValue("staff_session") is { } sessionId)
            lock (gate) sessions.Remove(sessionId);
    }
}
