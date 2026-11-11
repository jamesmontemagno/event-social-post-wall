using EventWall.Api.Models;
using EventWall.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Data;

public static class EventSeeder
{
    public static async Task SeedAsync(EventWallDbContext database, IConfiguration configuration)
    {
        var configuredEvent = new EventConfiguration(configuration);
        var slug = configuredEvent.Slug;
        var existing = await database.Events.SingleOrDefaultAsync();
        if (existing is not null)
        {
            if (existing.Slug != slug)
                throw new InvalidOperationException("This database already belongs to a different Event:Slug. Use a separate database for each event.");
            if (existing.Name != configuredEvent.Name)
            {
                existing.Name = configuredEvent.Name;
                await database.SaveChangesAsync();
            }
            return;
        }

        database.Events.Add(new EventSettings
        {
            Slug = slug,
            Name = configuredEvent.Name,
            SubmissionsOpen = configuration.GetValue<bool>("Event:SubmissionsInitiallyOpen")
        });
        await database.SaveChangesAsync();
    }

    public static async Task<EventSettings> GetEventAsync(
        EventWallDbContext database,
        CancellationToken cancellationToken = default)
    {
        return await database.Events.SingleAsync(cancellationToken);
    }

    public static Task<EventSettings> LockEventForPostIngestionAsync(
        EventWallDbContext database,
        CancellationToken cancellationToken)
    {
        if (database.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Post ingestion requires a transaction.");
        }

        // Serialize duplicate checks and inserts across the admin and submission deployments.
        return database.Database.IsSqlServer()
            ? database.Events.FromSqlRaw("SELECT * FROM [Events] WITH (UPDLOCK, HOLDLOCK)").SingleAsync(cancellationToken)
            : GetEventAsync(database, cancellationToken);
    }

    public static string NormalizeSlug(string value)
    {
        var slug = value.Trim().ToLowerInvariant();
        if (slug.Length is 0 or > 80 ||
            slug.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new InvalidOperationException("Event:Slug must contain only lowercase letters, digits, and hyphens.");
        }

        return slug;
    }
}
