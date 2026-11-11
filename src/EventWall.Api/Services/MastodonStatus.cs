using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventWall.Api.Models;

namespace EventWall.Api.Services;

public enum MastodonSkipReason
{
    None,
    Reblog,
    Reply,
    Quote,
    NotPublic,
    AuthorOptedOut,
    Bot,
    ContentWarning
}

public sealed record MastodonPost(
    string ExternalId,
    long NumericId,
    string Text,
    string AuthorName,
    string AuthorId,
    string PostUrl,
    string ProfileUrl,
    string? AvatarUrl,
    DateTime? CreatedAtUtc,
    IReadOnlyList<PostImage> Images);

public static partial class MastodonStatus
{
    public static Uri? InstanceUri(IConfiguration configuration) =>
        TryParseInstance(configuration["Social:Mastodon:InstanceUrl"], out var instance) ? instance : null;

    public static bool TryParseInstance(string? value, out Uri instance)
    {
        instance = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            Uri.CheckHostName(uri.IdnHost) != UriHostNameType.Dns)
        {
            return false;
        }

        instance = new Uri($"https://{uri.IdnHost}/");
        return true;
    }

    // Mastodon media can be served by the configured server or by its separately configured media CDN.
    public static IReadOnlyList<string> AllowedHosts(IConfiguration? configuration)
    {
        if (configuration is null || InstanceUri(configuration) is not { } instance) return [];
        var hosts = new List<string> { instance.Host };
        foreach (var item in configuration.GetSection("Social:Mastodon:MediaHosts").GetChildren())
        {
            var host = item.Value?.Trim();
            if (Uri.CheckHostName(host) == UriHostNameType.Dns) hosts.Add(host!);
        }
        return hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static MastodonSkipReason SkipReason(JsonElement status)
    {
        if (IsPresent(status, "reblog")) return MastodonSkipReason.Reblog;
        if (IsPresent(status, "in_reply_to_id")) return MastodonSkipReason.Reply;
        if (IsPresent(status, "quote")) return MastodonSkipReason.Quote;
        if (ReadString(status, "visibility") != "public") return MastodonSkipReason.NotPublic;
        if (!status.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object ||
            NoBotTag().IsMatch(ToPlainText(ReadString(account, "note"))))
        {
            return MastodonSkipReason.AuthorOptedOut;
        }
        if (IsTrue(account, "bot")) return MastodonSkipReason.Bot;
        return IsTrue(status, "sensitive") || !string.IsNullOrWhiteSpace(ReadString(status, "spoiler_text"))
            ? MastodonSkipReason.ContentWarning
            : MastodonSkipReason.None;
    }

    public static long? ReadId(JsonElement status) =>
        long.TryParse(ReadString(status, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;

    public static MastodonPost? Read(JsonElement status, Uri instance)
    {
        if (ReadId(status) is not { } numericId ||
            !status.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var acct = ReadString(account, "acct");
        var authorId = ReadString(account, "id");
        var text = ToPlainText(ReadString(status, "content"));
        if (string.IsNullOrWhiteSpace(acct) || acct.Length > 300 ||
            string.IsNullOrWhiteSpace(authorId) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var id = numericId.ToString(CultureInfo.InvariantCulture);
        var uri = ReadString(status, "uri");
        var externalId = !string.IsNullOrWhiteSpace(uri) && uri.Length <= 240 ? uri : $"{instance.Host}:{id}";
        var displayName = ReadString(account, "display_name");
        var profileUrl = new Uri(instance, $"@{Uri.EscapeDataString(acct).Replace("%40", "@", StringComparison.Ordinal)}").AbsoluteUri;
        return new MastodonPost(
            externalId,
            numericId,
            text,
            string.IsNullOrWhiteSpace(displayName) ? $"@{acct}" : displayName,
            authorId,
            $"{profileUrl}/{id}",
            profileUrl,
            ReadString(account, "avatar_static") ?? ReadString(account, "avatar"),
            ReadDate(status, "created_at"),
            Images(status));
    }

    public static IReadOnlyList<PostImage> Images(JsonElement status)
    {
        if (!status.TryGetProperty("media_attachments", out var media) || media.ValueKind != JsonValueKind.Array) return [];
        return media.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && ReadString(item, "type") == "image" &&
                !string.IsNullOrWhiteSpace(ReadString(item, "url")))
            .Select(item => new PostImage(ReadString(item, "url")!, ReadString(item, "description")))
            .Take(4)
            .ToList();
    }

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = LineBreak().Replace(html, "\n");
        text = ParagraphBreak().Replace(text, "\n\n");
        text = Tag().Replace(text, string.Empty);
        return WebUtility.HtmlDecode(text).Trim();
    }

    private static bool IsPresent(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? ReadDate(JsonElement element, string name) =>
        DateTime.TryParse(ReadString(element, name), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"</p>\s*<p[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])#nobot(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoBotTag();
}
