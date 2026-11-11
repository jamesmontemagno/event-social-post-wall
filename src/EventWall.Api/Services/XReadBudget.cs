using EventWall.Api.Data;
using EventWall.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public sealed class XReadBudget(IDbContextFactory<EventWallDbContext> databaseFactory)
{
    private const decimal PublishedPostReadPriceUsd = 0.005m;
    private const decimal PublishedUserReadPriceUsd = 0.010m;
    private const int MaximumPostsPerSearch = 100;
    private readonly SemaphoreSlim _reservationLock = new(1, 1);

    public Task<XReadReservation?> TryReserveSearchAsync(
        decimal maximumDailySpendUsd,
        CancellationToken cancellationToken) =>
        ReserveAsync(maximumDailySpendUsd, MaximumPostsPerSearch, 0, cancellationToken);

    public Task<XReadReservation?> TryReserveUserReadsAsync(
        int maximumUsers,
        decimal maximumDailySpendUsd,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUsers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumUsers, 100);
        return ReserveAsync(maximumDailySpendUsd, 0, maximumUsers, cancellationToken);
    }

    private async Task<XReadReservation?> ReserveAsync(
        decimal maximumDailySpendUsd,
        int postReads,
        int maximumUserReads,
        CancellationToken cancellationToken)
    {
        var minimumCost = postReads > 0
            ? postReads * PublishedPostReadPriceUsd
            : PublishedUserReadPriceUsd;
        if (maximumDailySpendUsd < minimumCost)
        {
            return null;
        }

        await _reservationLock.WaitAsync(cancellationToken);
        try
        {
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var key = $"X:{DateTime.UtcNow:yyyyMMdd}";
            var usage = await database.ConnectorDailyUsage.SingleOrDefaultAsync(
                item => item.Id == key, cancellationToken);
            if (usage is null)
            {
                usage = new ConnectorDailyUsage { Id = key, ReservedPostReads = 0 };
                database.ConnectorDailyUsage.Add(usage);
            }

            var remaining = maximumDailySpendUsd -
                usage.ReservedPostReads * PublishedPostReadPriceUsd -
                usage.ReservedUserReads * PublishedUserReadPriceUsd;
            if (remaining < minimumCost)
            {
                return null;
            }

            var userReads = (int)Math.Min(maximumUserReads,
                Math.Max(0m, decimal.Floor(remaining / PublishedUserReadPriceUsd)));
            usage.ReservedPostReads += postReads;
            usage.ReservedUserReads += userReads;
            usage.Version++;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new XReadReservation(key, postReads, userReads);
        }
        finally
        {
            _reservationLock.Release();
        }
    }

    public Task ReconcileSearchAsync(
        XReadReservation reservation,
        int actualPostReads,
        CancellationToken cancellationToken) =>
        ReconcileAsync(reservation, actualPostReads, reservation.ReservedUserReads, cancellationToken);

    public Task ReconcileUserReadsAsync(
        XReadReservation reservation,
        int actualUserReads,
        CancellationToken cancellationToken) =>
        ReconcileAsync(reservation, reservation.ReservedPostReads, actualUserReads, cancellationToken);

    private async Task ReconcileAsync(
        XReadReservation reservation,
        int actualPostReads,
        int actualUserReads,
        CancellationToken cancellationToken)
    {
        var posts = Math.Clamp(actualPostReads, 0, reservation.ReservedPostReads);
        var users = Math.Clamp(actualUserReads, 0, reservation.ReservedUserReads);
        await _reservationLock.WaitAsync(cancellationToken);
        try
        {
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var usage = await database.ConnectorDailyUsage.SingleAsync(
                item => item.Id == reservation.UsageKey,
                cancellationToken);
            if (usage.ReservedPostReads < reservation.ReservedPostReads ||
                usage.ReservedUserReads < reservation.ReservedUserReads)
            {
                throw new InvalidOperationException("The persisted X read reservation is inconsistent.");
            }

            usage.ReservedPostReads += posts - reservation.ReservedPostReads;
            usage.ReservedUserReads += users - reservation.ReservedUserReads;
            usage.Version++;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _reservationLock.Release();
        }
    }
}

public sealed record XReadReservation(string UsageKey, int ReservedPostReads, int ReservedUserReads);
