using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EventWall.Api.Data;
using EventWall.Api.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Security;

public sealed class TeamAccessService(
    IDbContextFactory<EventWallDbContext> factory,
    StaffPasscodeService owner,
    ILogger<TeamAccessService> logger)
{
    public const string OwnerRole = "Administrator";
    public const string MemberRole = "Moderator";
    public const string StaffPolicy = "staff";
    public const string OwnerPolicy = "administrator";
    public const string MemberClaim = "staff_member";
    public const string VersionClaim = "staff_member_version";
    private static readonly Regex UsernamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{2,39}$", RegexOptions.CultureInvariant);
    private readonly PasswordHasher<StaffMember> hasher = new();

    public static bool IsValidUsername(string? username) =>
        username is not null && UsernamePattern.IsMatch(username) &&
        !username.Equals("admin", StringComparison.OrdinalIgnoreCase);

    public static Guid? MemberId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(MemberClaim), out var id) ? id : null;

    public static string Username(ClaimsPrincipal user) =>
        user.Identity?.Name ?? throw new InvalidOperationException("An authenticated staff identity is required.");

    public string RotateCode(StaffMember member)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        member.AccessCodeHash = hasher.HashPassword(member, code);
        member.Enabled = true;
        member.CodeRotatedAtUtc = DateTime.UtcNow;
        member.Version++;
        return code;
    }

    public async Task<ClaimsPrincipal?> AuthenticateAsync(string? username, string? code, CancellationToken cancellationToken)
    {
        var name = username?.Trim() ?? "admin";
        if (name.Equals("admin", StringComparison.OrdinalIgnoreCase))
            return owner.Verify(code) ? Principal(null) : null;
        if (!IsValidUsername(name) || code is not { Length: 32 }) return null;

        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var normalized = name.ToUpperInvariant();
        var member = await database.StaffMembers.SingleOrDefaultAsync(item => item.NormalizedUsername == normalized, cancellationToken);
        if (member is null || !member.Enabled ||
            hasher.VerifyHashedPassword(member, member.AccessCodeHash, code) == PasswordVerificationResult.Failed)
            return null;
        member.LastSignedInAtUtc = DateTime.UtcNow;
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("A team member's credentials changed during sign-in.");
            return null;
        }
        return Principal(member);
    }

    public async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var user = context.Principal;
        if (!owner.IsConfigured || user is null || user.FindFirstValue(StaffPasscodeService.FingerprintClaim) != owner.Fingerprint)
        {
            await Reject();
            return;
        }
        var session = user.FindFirstValue("staff_session");
        if (string.IsNullOrEmpty(session))
        {
            await Reject();
            return;
        }
        var memberClaim = user.FindFirstValue(MemberClaim);
        if (memberClaim is null)
        {
            if (user.IsInRole(OwnerRole) && user.Identity?.Name == "Admin" &&
                user.FindFirstValue(ClaimTypes.NameIdentifier) == "admin")
                return;
            if (user.Identity?.Name == "Event staff" && !user.FindAll(ClaimTypes.Role).Any() &&
                user.FindFirstValue(ClaimTypes.NameIdentifier) is null)
            {
                context.ReplacePrincipal(Principal(null, session));
                context.ShouldRenew = true;
            }
            else await Reject();
            return;
        }
        if (!Guid.TryParse(memberClaim, out var id) ||
            !int.TryParse(user.FindFirstValue(VersionClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            await Reject();
            return;
        }
        await using var database = await factory.CreateDbContextAsync(context.HttpContext.RequestAborted);
        var member = await database.StaffMembers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, context.HttpContext.RequestAborted);
        if (member is null || !member.Enabled || member.Version != version)
        {
            await Reject();
            return;
        }
        if (!user.IsInRole(MemberRole) || user.IsInRole(OwnerRole) || user.Identity?.Name != member.Username ||
            user.FindFirstValue(ClaimTypes.NameIdentifier) != member.Id.ToString("D"))
        {
            context.ReplacePrincipal(Principal(member, session));
            context.ShouldRenew = true;
        }

        async Task Reject()
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    private ClaimsPrincipal Principal(StaffMember? member, string? session = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, member?.Username ?? "Admin"),
            new(ClaimTypes.NameIdentifier, member?.Id.ToString("D") ?? "admin"),
            new(ClaimTypes.Role, member is null ? OwnerRole : MemberRole),
            new("staff_session", session ?? Guid.NewGuid().ToString("N")),
            new(StaffPasscodeService.FingerprintClaim, owner.Fingerprint)
        };
        if (member is not null)
        {
            claims.Add(new(MemberClaim, member.Id.ToString("D")));
            claims.Add(new(VersionClaim, member.Version.ToString(CultureInfo.InvariantCulture)));
        }
        return new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
