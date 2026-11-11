using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Security;

public sealed class ProviderTokenService(
    IConfiguration configuration,
    IHttpClientFactory clients,
    IDbContextFactory<EventWallDbContext> databaseFactory,
    IDataProtectionProvider protectionProvider)
{
    private readonly ConcurrentDictionary<string, CachedAccessToken> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("EventWall.ProviderRefreshToken.v1");

    public async Task<string> GetAccessTokenAsync(string provider, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(provider, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2))
        {
            return cached.Value;
        }

        var providerLock = _locks.GetOrAdd(provider, _ => new SemaphoreSlim(1, 1));
        await providerLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(provider, out cached) && cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2))
            {
                return cached.Value;
            }

            var clientId = configuration[$"Social:{provider}:ClientId"];
            var clientSecret = configuration[$"Social:{provider}:ClientSecret"];
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException($"{provider} OAuth client settings are missing.");
            }

            var refreshToken = await GetRefreshTokenAsync(provider, cancellationToken);
            var tokenUrl = provider == "Twitch"
                ? "https://id.twitch.tv/oauth2/token"
                : "https://oauth2.googleapis.com/token";
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = "refresh_token"
                })
            };
            using var response = await clients.CreateClient("social").SendAsync(request, cancellationToken);
            if (provider == "YouTube")
                await YouTubeRequestException.EnsureSuccessAsync(response, "OAuth token refresh", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var accessToken = document.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException($"{provider} token response did not include an access token.");
            }

            var rotatedRefreshToken = document.RootElement.TryGetProperty("refresh_token", out var nextRefreshToken)
                ? nextRefreshToken.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(rotatedRefreshToken) &&
                !string.Equals(refreshToken, rotatedRefreshToken, StringComparison.Ordinal))
            {
                await PersistRefreshTokenAsync(provider, rotatedRefreshToken, cancellationToken);
            }

            var lifetimeSeconds = document.RootElement.TryGetProperty("expires_in", out var lifetime) &&
                lifetime.TryGetInt32(out var parsedLifetime)
                ? parsedLifetime
                : 3600;
            var refreshed = new CachedAccessToken(accessToken, DateTime.UtcNow.AddSeconds(Math.Max(60d, lifetimeSeconds)));
            _cache[provider] = refreshed;
            return refreshed.Value;
        }
        finally
        {
            providerLock.Release();
        }
    }

    private async Task<string> GetRefreshTokenAsync(string provider, CancellationToken cancellationToken)
    {
        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            var stored = await database.ProviderCredentials.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Provider == provider, cancellationToken);
            if (stored is not null)
            {
                try
                {
                    return _protector.Unprotect(stored.ProtectedRefreshToken);
                }
                catch (CryptographicException exception)
                {
                    throw new InvalidOperationException(
                        $"The stored {provider} refresh token could not be decrypted. Restore the original data-protection key ring or reconnect {provider}.",
                        exception);
                }
            }
        }

        var configured = configuration[$"Social:{provider}:RefreshToken"];
        return string.IsNullOrWhiteSpace(configured)
            ? throw new InvalidOperationException($"{provider} OAuth refresh token is missing.")
            : configured;
    }

    private async Task PersistRefreshTokenAsync(string provider, string refreshToken, CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var stored = await database.ProviderCredentials.SingleOrDefaultAsync(
            item => item.Provider == provider, cancellationToken);
        if (stored is null)
        {
            database.ProviderCredentials.Add(new ProviderCredential
            {
                Provider = provider,
                ProtectedRefreshToken = _protector.Protect(refreshToken)
            });
        }
        else
        {
            stored.ProtectedRefreshToken = _protector.Protect(refreshToken);
            stored.Version++;
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    private sealed record CachedAccessToken(string Value, DateTime ExpiresAtUtc);
}
