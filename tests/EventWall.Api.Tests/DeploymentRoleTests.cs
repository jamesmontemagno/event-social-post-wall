using EventWall.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventWall.Api.Tests;

public class DeploymentRoleTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("Public", false)]
    [InlineData("Admin", true)]
    [InlineData("admin", true)]
    [InlineData("Submission", false)]
    public void RolesAreExplicitAndDefaultToPublic(string? role, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Deployment:Role"] = role }).Build();
        Assert.Equal(expected, DeploymentRole.IsAdmin(configuration));
    }

    [Theory]
    [InlineData("Submission", SiteRole.Submission)]
    [InlineData("submission", SiteRole.Submission)]
    [InlineData("Public", SiteRole.Public)]
    [InlineData("Admin", SiteRole.Admin)]
    public void Three_roles_are_resolved_without_boolean_fallback(string role, SiteRole expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Deployment:Role"] = role }).Build();
        Assert.Equal(expected, DeploymentRole.Resolve(configuration));
    }

    [Theory]
    [InlineData("https://questions.example.test", false, true)]
    [InlineData("http://127.0.0.1:5175", true, true)]
    [InlineData("http://questions.example.test", true, false)]
    [InlineData("http://localhost:5175", false, false)]
    [InlineData("https://user:pass@questions.example.test", false, false)]
    [InlineData("/questions", false, false)]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    public void Submission_destination_is_validated(string? url, bool development, bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Public:SubmissionSiteUrl"] = url }).Build();
        if (valid) Assert.NotEmpty(DeploymentRole.SubmissionSiteUrl(configuration, development));
        else Assert.Throws<InvalidOperationException>(() => DeploymentRole.SubmissionSiteUrl(configuration, development));
    }

    [Theory]
    [InlineData("https://wall.example.test/event/", false, "https://wall.example.test/event/")]
    [InlineData("http://127.0.0.1:5173", true, "http://127.0.0.1:5173/")]
    [InlineData("http://127.0.0.1:5173", false, null)]
    [InlineData("http://wall.example.test", true, null)]
    [InlineData("https://user:pass@wall.example.test", false, null)]
    [InlineData("javascript:alert(1)", true, null)]
    [InlineData(null, false, null)]
    [InlineData("", true, null)]
    public void Admin_destination_is_disabled_when_missing_or_unsafe(string? value, bool development, string? expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Public:WallSiteUrl"] = value }).Build();
        Assert.Equal(expected, DeploymentRole.AdminSiteUrl(configuration, "Public:WallSiteUrl", development, NullLogger.Instance));
    }

    [Fact]
    public void Missing_hosted_question_link_never_sends_attendees_to_another_event()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => DeploymentRole.SubmissionSiteUrl(configuration, false));
    }

    [Fact]
    public void Every_asset_pair_is_validated()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"eventwall-roles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var assetRole in Enum.GetValues<SiteRole>())
            {
                File.WriteAllText(Path.Combine(directory, "index.html"),
                    $"<meta name=\"eventwall-role\" content=\"{assetRole.ToString().ToLowerInvariant()}\">");
                foreach (var serverRole in Enum.GetValues<SiteRole>())
                {
                    if (serverRole == assetRole && assetRole != SiteRole.Submission) DeploymentRole.ValidateAssets(directory, serverRole);
                    else Assert.Throws<InvalidOperationException>(() => DeploymentRole.ValidateAssets(directory, serverRole));
                }
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Submission_requires_Blazor_assets_and_rejects_React_packages()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"eventwall-blazor-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "questions"));
        try
        {
            var assets = new[] { "questions.js", "questions.css", "theme.css", "skin.css", "footer.css", "theme-init.js", "reconnect.js" };
            Assert.Throws<InvalidOperationException>(() => DeploymentRole.ValidateAssets(directory, SiteRole.Submission));
            foreach (var asset in assets) File.WriteAllText(Path.Combine(directory, "questions", asset), "fixture");
            File.WriteAllText(Path.Combine(directory, "THIRD-PARTY-NOTICES.txt"), "fixture");
            DeploymentRole.ValidateAssets(directory, SiteRole.Submission);
            foreach (var asset in assets)
            {
                File.Delete(Path.Combine(directory, "questions", asset));
                Assert.Throws<InvalidOperationException>(() => DeploymentRole.ValidateAssets(directory, SiteRole.Submission));
                File.WriteAllText(Path.Combine(directory, "questions", asset), "fixture");
            }
            File.WriteAllText(Path.Combine(directory, "index.html"), "<meta name=\"eventwall-role\" content=\"submission\">");
            Assert.Throws<InvalidOperationException>(() => DeploymentRole.ValidateAssets(directory, SiteRole.Submission));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("Combined")]
    [InlineData("")]
    [InlineData("Publci")]
    public void InvalidRolesFailStartup(string role)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Deployment:Role"] = role }).Build();
        Assert.Throws<InvalidOperationException>(() => DeploymentRole.IsAdmin(configuration));
    }

    [Fact]
    public void WrongOrMissingAssetsFailStartup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"eventwall-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Throws<InvalidOperationException>(() => DeploymentRole.ValidateAssets(directory, false));
            File.WriteAllText(Path.Combine(directory, "index.html"), "<meta name=\"eventwall-role\" content=\"admin\">");
            Assert.Throws<InvalidOperationException>(() => DeploymentRole.ValidateAssets(directory, false));
            DeploymentRole.ValidateAssets(directory, true);
            File.WriteAllText(Path.Combine(directory, "index.html"), "<meta name=\"eventwall-role\" content=\"public\">");
            DeploymentRole.ValidateAssets(directory, false);
            Assert.Throws<InvalidOperationException>(() => DeploymentRole.ValidateAssets(directory, true));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
