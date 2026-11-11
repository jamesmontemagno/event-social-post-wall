using EventWall.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public sealed record DemoResetResult(int PostsRemoved, int ModerationActionsRemoved, int BlockedAuthorsRemoved);

public static class DemoDatabaseReset
{
    public static async Task<DemoResetResult> ResetAsync(
        EventWallDbContext database,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var moderationActionsRemoved = await database.ModerationActions.ExecuteDeleteAsync(cancellationToken);
        var postsRemoved = await database.Posts
            .Where(post => post.EventSlug == settings.Slug)
            .ExecuteDeleteAsync(cancellationToken);
        var blockedAuthorsRemoved = await database.BlockedAuthors
            .Where(author => author.EventSlug == settings.Slug)
            .ExecuteDeleteAsync(cancellationToken);

        settings.Revision++;
        await database.SaveChangesAsync(cancellationToken);
        await DemoContent.SeedAsync(database, nowUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new DemoResetResult(postsRemoved, moderationActionsRemoved, blockedAuthorsRemoved);
    }
}
