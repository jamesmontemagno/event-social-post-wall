using System.Net;
using System.Text;
using System.Text.Json;
using EventWall.Api.Data;
using EventWall.Api.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EventWall.Api.Tests;

public sealed class ProviderTokenServiceTests
{
    [Theory]
    [InlineData("YouTube")]
    [InlineData("Twitch")]
    public async Task Changed_refresh_tokens_are_protected_and_used_after_restart(string provider)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new DatabaseFactory(connection);
        await using (var database = factory.CreateDbContext())
            await database.Database.EnsureCreatedAsync();
        var protection = new EphemeralDataProtectionProvider();
        using var handler = new TokenHandler("rotated-one", "rotated-two", null, "rotated-two");
        using var client = new HttpClient(handler);

        var first = CreateService(provider, factory, protection, client);
        Assert.Equal("access", await first.GetAccessTokenAsync(provider, default));
        await using (var database = factory.CreateDbContext())
        {
            var stored = await database.ProviderCredentials.SingleAsync();
            Assert.Equal(provider, stored.Provider);
            Assert.DoesNotContain("rotated-one", stored.ProtectedRefreshToken);
            Assert.Equal("rotated-one", protection.CreateProtector("EventWall.ProviderRefreshToken.v1").Unprotect(stored.ProtectedRefreshToken));
            Assert.Equal(0, stored.Version);
        }

        var restarted = CreateService(provider, factory, protection, client);
        Assert.Equal("access", await restarted.GetAccessTokenAsync(provider, default));
        Assert.Equal("access", await CreateService(provider, factory, protection, client).GetAccessTokenAsync(provider, default));
        Assert.Equal("access", await CreateService(provider, factory, protection, client).GetAccessTokenAsync(provider, default));
        Assert.Contains("refresh_token=configured-token", handler.RequestBodies[0]);
        Assert.Contains("refresh_token=rotated-one", handler.RequestBodies[1]);
        Assert.Contains("refresh_token=rotated-two", handler.RequestBodies[2]);
        Assert.Contains("refresh_token=rotated-two", handler.RequestBodies[3]);
        await using var verify = factory.CreateDbContext();
        var updated = await verify.ProviderCredentials.SingleAsync();
        Assert.Equal("rotated-two", protection.CreateProtector("EventWall.ProviderRefreshToken.v1").Unprotect(updated.ProtectedRefreshToken));
        Assert.Equal(1, updated.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("configured-token")]
    [InlineData("")]
    public async Task YouTube_does_not_store_an_absent_or_unchanged_refresh_token(string? responseToken)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new DatabaseFactory(connection);
        await using (var database = factory.CreateDbContext())
            await database.Database.EnsureCreatedAsync();
        using var handler = new TokenHandler(responseToken);
        using var client = new HttpClient(handler);
        var service = CreateService("YouTube", factory, new EphemeralDataProtectionProvider(), client);

        Assert.Equal("access", await service.GetAccessTokenAsync("YouTube", default));
        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.ProviderCredentials.ToListAsync());
    }

    [Fact]
    public async Task YouTube_reports_an_unreadable_stored_token_instead_of_falling_back_to_configuration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new DatabaseFactory(connection);
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.EnsureCreatedAsync();
            database.ProviderCredentials.Add(new()
            {
                Provider = "YouTube",
                ProtectedRefreshToken = new EphemeralDataProtectionProvider()
                    .CreateProtector("EventWall.ProviderRefreshToken.v1").Protect("old-token")
            });
            await database.SaveChangesAsync();
        }
        using var handler = new TokenHandler();
        using var client = new HttpClient(handler);
        var service = CreateService("YouTube", factory, new EphemeralDataProtectionProvider(), client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAccessTokenAsync("YouTube", default));
        Assert.Contains("stored YouTube refresh token could not be decrypted", error.Message);
        Assert.Empty(handler.RequestBodies);
    }

    private static ProviderTokenService CreateService(
        string provider, DatabaseFactory factory, IDataProtectionProvider protection, HttpClient client)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Social:{provider}:ClientId"] = "client",
            [$"Social:{provider}:ClientSecret"] = "secret",
            [$"Social:{provider}:RefreshToken"] = "configured-token"
        }).Build();
        return new ProviderTokenService(configuration, new ClientFactory(client), factory, protection);
    }

    private sealed class TokenHandler(params string?[] refreshTokens) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var token = refreshTokens[RequestBodies.Count - 1];
            var content = new Dictionary<string, object> { ["access_token"] = "access", ["expires_in"] = 3600 };
            if (token is not null) content["refresh_token"] = token;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DatabaseFactory(SqliteConnection connection) : IDbContextFactory<EventWallDbContext>
    {
        public EventWallDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection).Options);
    }
}
