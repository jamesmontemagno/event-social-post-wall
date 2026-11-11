using EventWall.Api.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace EventWall.Api.Tests;

public sealed class DatabasePurposeTests
{
    [Theory]
    [InlineData("Staging", null, "EventWallStaging")]
    [InlineData("Staging", "Demo", "EventWallStaging")]
    [InlineData("Production", null, "EventWallProd")]
    [InlineData("Production", "Production", "EventWallProd")]
    public void Hosted_environment_accepts_its_separate_database(
        string environment,
        string? purpose,
        string databaseName)
    {
        var configuration = Configuration(purpose, databaseName);

        DatabasePurpose.Validate(configuration, environment);
    }

    [Theory]
    [InlineData("Staging", "EventWallProd")]
    [InlineData("Production", "EventWallStaging")]
    public void Hosted_environment_rejects_the_other_environment_database(string environment, string databaseName)
    {
        var configuration = Configuration(null, databaseName);

        Assert.Throws<InvalidOperationException>(() => DatabasePurpose.Validate(configuration, environment));
    }

    [Fact]
    public void Explicit_purpose_must_match_the_hosting_environment()
    {
        var configuration = Configuration("Production", "EventWallProd");

        Assert.Throws<InvalidOperationException>(() =>
            DatabasePurpose.Validate(configuration, Environments.Staging));
    }

    [Fact]
    public void Demo_reset_accepts_only_the_demo_database()
    {
        DatabasePurpose.EnsureDemoConnectionString("Server=localhost;Initial Catalog=EventWallStaging;User ID=test;");

        Assert.Throws<InvalidOperationException>(() =>
            DatabasePurpose.EnsureDemoConnectionString("Server=localhost;Initial Catalog=EventWallProd;User ID=test;"));
    }

    private static IConfiguration Configuration(string? purpose, string databaseName)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:EventWall"] = $"Server=localhost;Initial Catalog={databaseName};User ID=test;"
        };
        if (purpose is not null)
        {
            settings[DatabasePurpose.ConfigurationKey] = purpose;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
