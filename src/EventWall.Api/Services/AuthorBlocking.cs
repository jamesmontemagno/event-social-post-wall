using System.Data;
using EventWall.Api.Data;
using EventWall.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public enum BlockAuthorResult { Blocked, NotFound, VersionConflict, Unsupported }

public sealed class AuthorBlocking(IDbContextFactory<EventWallDbContext> factory)
{
    public async Task<BlockAuthorResult> BlockAsync(
        Guid postId, int version, string staffSessionId, CancellationToken cancellationToken,
        string? staffUsername = null, Guid? staffMemberId = null)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var settings = await EventSeeder.GetEventAsync(database, cancellationToken);
        var post = await database.Posts.SingleOrDefaultAsync(
            item => item.Id == postId && item.EventSlug == settings.Slug, cancellationToken);
        if (post is null) return BlockAuthorResult.NotFound;
        if (post.Version != version) return BlockAuthorResult.VersionConflict;
        if (post.Source is PostSource.Portal or PostSource.Manual || string.IsNullOrWhiteSpace(post.AuthorExternalId))
            return BlockAuthorResult.Unsupported;

        if (!await database.BlockedAuthors.AnyAsync(item =>
            item.EventSlug == post.EventSlug && item.Source == post.Source &&
            item.AuthorExternalId == post.AuthorExternalId, cancellationToken))
        {
            database.BlockedAuthors.Add(new BlockedAuthor
            {
                EventSlug = post.EventSlug,
                Source = post.Source,
                AuthorExternalId = post.AuthorExternalId,
                CreatedAtUtc = DateTime.UtcNow,
                StaffSessionId = staffSessionId,
                StaffUsername = staffUsername,
                StaffMemberId = staffMemberId
            });
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return BlockAuthorResult.Blocked;
    }
}
