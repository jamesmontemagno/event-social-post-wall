using System.Data;
using EventWall.Api.Data;
using EventWall.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public sealed class BlueskyCursorStore(IDbContextFactory<EventWallDbContext> databaseFactory)
{
    private const string Provider = "Bluesky";

    public async Task<long?> GetAsync(CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.ConnectorCheckpoints
            .Where(item => item.Provider == Provider)
            .Select(item => (long?)item.Cursor)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<long> SaveAsync(long cursor, CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var checkpoint = await database.ConnectorCheckpoints
            .SingleOrDefaultAsync(item => item.Provider == Provider, cancellationToken);
        if (checkpoint is null)
        {
            database.ConnectorCheckpoints.Add(new ConnectorCheckpoint
            {
                Provider = Provider,
                Cursor = cursor,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            if (cursor < checkpoint.Cursor)
            {
                await transaction.CommitAsync(cancellationToken);
                return checkpoint.Cursor;
            }

            checkpoint.Cursor = cursor;
            checkpoint.UpdatedAtUtc = DateTime.UtcNow;
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return cursor;
    }
}
