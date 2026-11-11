namespace EventWall.Api.Services;

public enum SiteRole { Public, Admin, Submission }

public static class DeploymentRole
{
    public static SiteRole Resolve(IConfiguration configuration) =>
        (configuration["Deployment:Role"] ?? "Public") switch
        {
            var role when role.Equals("Public", StringComparison.OrdinalIgnoreCase) => SiteRole.Public,
            var role when role.Equals("Admin", StringComparison.OrdinalIgnoreCase) => SiteRole.Admin,
            var role when role.Equals("Submission", StringComparison.OrdinalIgnoreCase) => SiteRole.Submission,
            _ => throw new InvalidOperationException("Deployment:Role must be Public, Admin, or Submission.")
        };

    public static bool IsAdmin(IConfiguration configuration) => Resolve(configuration) == SiteRole.Admin;

    public static void ValidateAssets(string webRoot, bool isAdmin) =>
        ValidateAssets(webRoot, isAdmin ? SiteRole.Admin : SiteRole.Public);

    public static void ValidateAssets(string webRoot, SiteRole siteRole)
    {
        var index = Path.Combine(webRoot, "index.html");
        if (siteRole == SiteRole.Submission)
        {
            if (File.Exists(index))
                throw new InvalidOperationException("Submission uses Blazor Server. Remove legacy React assets and publish the submission package.");
            foreach (var asset in new[] { "questions.js", "questions.css", "theme.css", "skin.css", "footer.css", "theme-init.js", "reconnect.js" })
            {
                if (!File.Exists(Path.Combine(webRoot, "questions", asset)))
                    throw new InvalidOperationException("Publish the Blazor Server submission assets before starting the app.");
            }
            if (!File.Exists(Path.Combine(webRoot, "THIRD-PARTY-NOTICES.txt")))
                throw new InvalidOperationException("Publish the submission third-party notices before starting the app.");
            return;
        }
        if (!File.Exists(index))
        {
            throw new InvalidOperationException("Build and deploy the matching web assets before starting the app.");
        }

        var role = siteRole.ToString().ToLowerInvariant();
        if (!File.ReadAllText(index).Contains($"<meta name=\"eventwall-role\" content=\"{role}\">", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Web assets do not match the {role} deployment role.");
        }

    }

    public static string SubmissionSiteUrl(IConfiguration configuration, bool isDevelopment)
    {
        var value = configuration["Public:SubmissionSiteUrl"];
        if (string.IsNullOrEmpty(value) && isDevelopment) return "http://127.0.0.1:5082/";
        return ValidatedSiteUrl(value, isDevelopment) ??
            throw new InvalidOperationException("Set Public:SubmissionSiteUrl to your question site's absolute HTTPS URL (loopback HTTP is allowed in Development).");
    }

    public static string? AdminSiteUrl(IConfiguration configuration, string key, bool isDevelopment, ILogger logger)
    {
        var value = configuration[key];
        var url = ValidatedSiteUrl(value, isDevelopment);
        if (url is null)
            logger.LogWarning("{ConfigurationKey} is missing or is not a valid external site URL; its admin navigation link is disabled.", key);
        return url;
    }

    private static string? ValidatedSiteUrl(string? value, bool isDevelopment)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            !string.IsNullOrEmpty(url.UserInfo) ||
            (url.Scheme != Uri.UriSchemeHttps && !(isDevelopment && url.IsLoopback && url.Scheme == Uri.UriSchemeHttp)))
        {
            return null;
        }
        return url.AbsoluteUri;
    }
}
