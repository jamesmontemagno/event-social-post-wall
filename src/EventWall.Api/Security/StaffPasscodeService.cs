using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace EventWall.Api.Security;

public sealed class StaffPasscodeService
{
    public const string FingerprintClaim = "credential_fingerprint";
    private readonly string? _hash;
    private readonly PasswordHasher<StaffPasscodeService> _hasher = new();

    public StaffPasscodeService(IConfiguration configuration)
    {
        var passcode = configuration["STAFF_PASSCODE"];
        IsConfigured = !string.IsNullOrWhiteSpace(passcode) && passcode.Length >= 12;
        if (IsConfigured)
        {
            _hash = _hasher.HashPassword(this, passcode!);
            Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passcode!)));
        }
        else
        {
            Fingerprint = string.Empty;
        }
    }

    public bool IsConfigured { get; }
    public string Fingerprint { get; }

    public bool Verify(string? passcode) =>
        IsConfigured &&
        !string.IsNullOrEmpty(passcode) &&
        _hasher.VerifyHashedPassword(this, _hash!, passcode) != PasswordVerificationResult.Failed;
}
