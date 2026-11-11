using EventWall.Api.Data;
using EventWall.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public static class Promotions
{
    public static Task<List<PromoDto>> ListAsync(EventWallDbContext database, string eventSlug,
        CancellationToken cancellationToken, bool activeOnly = false) =>
        database.Promos.AsNoTracking().Where(promo => promo.EventSlug == eventSlug && (!activeOnly || promo.Active))
            .OrderByDescending(promo => promo.CreatedAtUtc).ThenBy(promo => promo.Id)
            .Select(promo => new PromoDto(promo.Id, promo.Title, promo.Description, promo.Url, promo.CreatedAtUtc, promo.Active, promo.Version))
            .ToListAsync(cancellationToken);

    public static Dictionary<string, string[]> Validate(PromoRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 80)
            errors["title"] = ["Promo title must contain 1-80 characters."];
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 500)
            errors["description"] = ["Promo description must contain 1-500 characters."];
        if (!string.IsNullOrWhiteSpace(request.Url) &&
            (request.Url.Trim().Length > 2048 ||
             !Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var url) ||
             url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host) ||
             !string.IsNullOrEmpty(url.UserInfo)))
            errors["url"] = ["Use an absolute HTTP(S) URL without credentials, up to 2,048 characters."];
        if (request.Version < 0)
            errors["version"] = ["A non-negative promo version is required."];
        return errors;
    }
}
