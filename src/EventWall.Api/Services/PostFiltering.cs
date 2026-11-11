using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventWall.Api.Data;
using EventWall.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public static class PostFiltering
{
    public const int MaximumEntries = 25;
    public const int MaximumEntryLength = 40;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static Regex Pattern(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex Links = Pattern(@"(?:https?://|www\.)[^\s]+", RegexOptions.IgnoreCase);
    private static readonly Regex Tags = Pattern(@"(?<![\p{L}\p{N}_])[@#][\p{L}\p{N}_]+(?:[.-][\p{L}\p{N}_]+)*");
    private static readonly Regex Words = Pattern(@"[\p{L}\p{N}][\p{L}\p{M}\p{N}]*(?:['\u2019-][\p{L}\p{M}\p{N}]+)*(?:\+\+|#)?");
    private static readonly Regex Keycaps = Pattern(@"[0-9#*]\uFE0F?\u20E3");
    private static readonly Regex CheckIn = Pattern(
        @"^(?:(?:hi|hello|hey|greetings|cheers|hej|howdy)(?:\s+(?:all|everyone))?\s+from|(?:i(?:'m| am)\s+)?(?:watching|joining|tuning in|checking in|listening)(?:\s+live)?\s+from)\s+(?<place>[^!?.,:;\r\n]{1,80}?)[.!]*$",
        RegexOptions.IgnoreCase);
    private static readonly HashSet<string> TechnicalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NET", "ASP", "ASPNET", "C", "C#", "C++", "F#", "EF", "SQL", "API", "APIS", "AI", "UI",
        "SDK", "CLI", "HTTP", "HTTPS", "JSON", "XML", "HTML", "CSS", "AOT", "MAUI", "CORE"
    };
    private static readonly HashSet<string> PlaceConnectors = new(StringComparer.Ordinal)
        { "de", "da", "do", "of", "the", "and", "del", "la", "van" };
    private static readonly string[] StarterTerms =
        ["fuck", "fucking", "motherfucker", "shit", "bullshit", "bitch", "asshole", "bastard", "dick", "cunt"];
    private static readonly (string Term, Regex Pattern)[] StarterPatterns =
        StarterTerms.Select(term => (term, ProfanityPattern(term))).ToArray();

    public static string Label(PostFilterReason reason) => reason switch
    {
        PostFilterReason.Profanity => "Profanity",
        PostFilterReason.AllCaps => "All caps",
        PostFilterReason.SingleWord => "Single word",
        PostFilterReason.EmojiOnly => "Emoji only",
        PostFilterReason.LocationOnly => "Location/check-in only",
        PostFilterReason.NoSubstantiveText => "No substantive text",
        PostFilterReason.RepeatedCharacters => "Repeated-character spam",
        PostFilterReason.DuplicateText => "Exact duplicate text",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown post filter reason.")
    };

    public static Dictionary<string, string[]> ValidateSettings(PostFilterSettings? settings)
    {
        var errors = new Dictionary<string, string[]>();
        if (settings is null)
        {
            errors["postFilters"] = ["Post filter settings are required."];
            return errors;
        }

        ValidateList("blockedTerms", settings.BlockedTerms);
        ValidateList("allowedTerms", settings.AllowedTerms);
        ValidateList("locationPhrases", settings.LocationPhrases);
        if (errors.Count == 0 && settings.BlockedTerms.Select(NormalizeEntry)
            .Intersect(settings.AllowedTerms.Select(NormalizeEntry), StringComparer.Ordinal).Any())
        {
            errors["allowedTerms"] = ["A term cannot be both blocked and allowed."];
        }
        return errors;

        void ValidateList(string name, string[]? entries)
        {
            if (entries is null || entries.Length > MaximumEntries ||
                entries.Any(entry => string.IsNullOrWhiteSpace(entry) || entry.Length > MaximumEntryLength ||
                    entry.Any(char.IsControl) || NormalizeEntry(entry).Length > MaximumEntryLength ||
                    !entry.EnumerateRunes().Any(Rune.IsLetterOrDigit)))
            {
                errors[name] = [$"Use up to {MaximumEntries} entries, each 1-{MaximumEntryLength} characters with letters or numbers and no control characters."];
            }
        }
    }

    public static PostFilterSettings NormalizeSettings(PostFilterSettings settings) => settings with
    {
        BlockedTerms = NormalizeList(settings.BlockedTerms),
        AllowedTerms = NormalizeList(settings.AllowedTerms),
        LocationPhrases = NormalizeList(settings.LocationPhrases)
    };

    public static IReadOnlyList<PostFilterReason> Evaluate(string text, PostFilterSettings settings)
    {
        var normalized = NormalizeText(text);
        var substance = Tags.Replace(Links.Replace(normalized, " "), " ").Trim();
        var withoutEmoji = RemoveEmoji(substance);
        var words = Words.Matches(withoutEmoji).Select(match => match.Value).ToArray();
        var emojiOnly = ContainsEmoji(substance) && withoutEmoji.EnumerateRunes()
            .All(rune => Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune));
        var reasons = new List<PostFilterReason>();

        if (settings.Profanity && HasProfanity(normalized, settings))
            reasons.Add(PostFilterReason.Profanity);
        var casedLetters = words.SelectMany(word => word.EnumerateRunes())
            .Where(rune => Rune.IsUpper(rune) || Rune.IsLower(rune)).ToArray();
        if (settings.AllCaps && words.Length >= 3 && casedLetters.Length >= 10 &&
            casedLetters.All(Rune.IsUpper) && words.Any(word => !TechnicalWords.Contains(word)))
            reasons.Add(PostFilterReason.AllCaps);
        if (settings.SingleWord && words.Length == 1 && !emojiOnly && !UsesUnspacedScript(words[0]))
            reasons.Add(PostFilterReason.SingleWord);
        if (settings.EmojiOnly && emojiOnly)
            reasons.Add(PostFilterReason.EmojiOnly);
        if (settings.LocationOnly && IsLocationOnly(withoutEmoji, settings.LocationPhrases))
            reasons.Add(PostFilterReason.LocationOnly);
        if (settings.NoSubstantiveText && words.Length == 0 && !emojiOnly)
            reasons.Add(PostFilterReason.NoSubstantiveText);
        if (settings.RepeatedCharacters && IsRepeatedSpam(withoutEmoji))
            reasons.Add(PostFilterReason.RepeatedCharacters);
        return reasons;
    }

    public static async Task<string?> ApplyDecisionAsync(
        EventWallDbContext database,
        EventPost post,
        EventSettings settings,
        bool blocked,
        bool checkDuplicate,
        CancellationToken cancellationToken)
    {
        var filters = settings.PostFilters;
        var reasons = Evaluate(post.Text, filters).ToList();
        if (checkDuplicate && filters.DuplicateText && await HasDuplicateTextAsync(database, post, cancellationToken))
            reasons.Add(PostFilterReason.DuplicateText);
        post.FilterReasonsJson = JsonSerializer.Serialize(reasons);
        var rejected = blocked || reasons.Count > 0;
        post.Status = rejected ? PostStatus.Rejected : PostStatus.Pending;
        post.DecidedAtUtc = rejected ? DateTime.UtcNow : null;
        post.PublishedRevision = null;
        return blocked ? "Automatically rejected: account blocked for this event." :
            reasons.Count == 0 ? null : $"Automatically rejected: {string.Join(", ", reasons.Select(Label))}.";
    }

    public static async Task ApplyToNewPostAsync(
        EventWallDbContext database,
        EventSettings settings,
        EventPost post,
        CancellationToken cancellationToken,
        bool blocked = false)
    {
        var reason = await ApplyDecisionAsync(database, post, settings, blocked, true, cancellationToken);
        if (reason is null) return;
        database.ModerationActions.Add(new ModerationAction
        {
            Id = Guid.NewGuid(),
            PostId = post.Id,
            FromStatus = PostStatus.Pending,
            ToStatus = PostStatus.Rejected,
            CreatedAtUtc = post.DecidedAtUtc!.Value,
            StaffSessionId = blocked ? $"source:{post.Source}" : "source:filter",
            Reason = reason
        });
    }

    private static Task<bool> HasDuplicateTextAsync(
        EventWallDbContext database,
        EventPost post,
        CancellationToken cancellationToken)
    {
        var candidates = database.Posts.AsNoTracking()
            .Where(item => item.EventSlug == post.EventSlug && item.Id != post.Id);
        if (database.Database.IsSqlServer())
        {
            // SQL Server pads strings for equality even with a binary collation.
            var byteLength = post.Text.Length * 2;
            return candidates.AnyAsync(item =>
                EF.Functions.Collate(item.Text, "Latin1_General_100_BIN2") == post.Text &&
                EF.Functions.DataLength(item.Text) == byteLength, cancellationToken);
        }

        return candidates.AnyAsync(item => EF.Functions.Collate(item.Text, "BINARY") == post.Text, cancellationToken);
    }

    private static string[] NormalizeList(string[] entries) => entries.Select(NormalizeEntry).Distinct().ToArray();
    private static string NormalizeEntry(string text) => string.Join(' ',
        NormalizeText(text).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string NormalizeText(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC)
        .EnumerateRunes().Where(rune => Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format ||
            rune.Value == 0x200D).Select(rune => rune.ToString()));

    private static bool HasProfanity(string text, PostFilterSettings settings)
    {
        text = text.Replace("\u200D", string.Empty);
        var allowed = settings.AllowedTerms.Select(NormalizeEntry).ToHashSet(StringComparer.Ordinal);
        return StarterPatterns.Any(item => !allowed.Contains(item.Term) && item.Pattern.IsMatch(text)) ||
            settings.BlockedTerms.Any(term => !allowed.Contains(NormalizeEntry(term)) &&
                ProfanityPattern(NormalizeEntry(term)).IsMatch(text));
    }

    private static bool UsesUnspacedScript(string word) => word.EnumerateRunes().Any(rune => rune.Value is
        >= 0x0E00 and <= 0x0EFF or >= 0x1000 and <= 0x109F or >= 0x1780 and <= 0x17FF or
        >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x9FFF or >= 0x20000 and <= 0x323AF);

    private static Regex ProfanityPattern(string term)
    {
        var pattern = new StringBuilder(@"(?<![\p{L}\p{N}])");
        for (var index = 0; index < term.Length; index++)
        {
            if (index > 0 && char.IsLetter(term[index - 1]) && char.IsLetter(term[index]))
                pattern.Append(@"[\s._-]?");
            pattern.Append(term[index] switch
            {
                'a' => "[a4@*]", 'e' => "[e3*]", 'i' => "[i1!*]", 'o' => "[o0*]",
                's' => "[s5$]", 't' => "[t7]", 'u' => "[u*]",
                ' ' => @"\s+", _ => Regex.Escape(term[index].ToString())
            });
        }
        pattern.Append(@"(?![\p{L}\p{N}])");
        return Pattern(pattern.ToString(), RegexOptions.IgnoreCase);
    }

    private static bool IsLocationOnly(string text, string[] phrases)
    {
        var trimmed = text.Trim().TrimEnd('.', '!');
        if (phrases.Any(phrase => NormalizeEntry(phrase) == NormalizeEntry(trimmed))) return true;
        var match = CheckIn.Match(text.Trim());
        if (!match.Success) return false;
        var place = match.Groups["place"].Value.Trim();
        if (phrases.Any(phrase => NormalizeEntry(phrase) == NormalizeEntry(place))) return true;
        var words = place.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 0 or > 5 || words.Any(word =>
            !word.EnumerateRunes().All(rune => Rune.IsLetter(rune) ||
                Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark || rune.Value is '-' or '\'' or 0x2019)))
            return false;
        return words.Length == 1 || words.All(word =>
            Rune.IsUpper(Rune.GetRuneAt(word, 0)) || PlaceConnectors.Contains(word));
    }

    private static bool IsRepeatedSpam(string text)
    {
        var characters = text.ToLowerInvariant().EnumerateRunes().Where(rune => !Rune.IsWhiteSpace(rune)).ToArray();
        var longest = 0;
        var run = 0;
        Rune previous = default;
        foreach (var character in characters)
        {
            run = character == previous ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = character;
        }
        return longest >= 5 && longest * 100 >= characters.Length * 80;
    }

    private static bool ContainsEmoji(string text) =>
        Keycaps.IsMatch(text) || text.EnumerateRunes().Any(IsEmojiRune);
    private static string RemoveEmoji(string text) => string.Concat(Keycaps.Replace(text, " ")
        .EnumerateRunes().Where(rune => !IsEmojiRune(rune) &&
            rune.Value is not (0x200D or 0xFE0E or 0xFE0F or 0x20E3) &&
            rune.Value is not (>= 0xE0020 and <= 0xE007F)).Select(rune => rune.ToString()));
    private static bool IsEmojiRune(Rune rune) => rune.Value is
        >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or
        0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or 0x231A or 0x231B or
        >= 0x23E9 and <= 0x23F3 or >= 0x23F8 and <= 0x23FA or 0x24C2 or 0x25AA or 0x25AB or
        0x25B6 or 0x25C0 or >= 0x25FB and <= 0x25FE or 0x2934 or 0x2935 or
        >= 0x2B05 and <= 0x2B07 or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55 or 0x3030 or 0x303D or 0x3297 or 0x3299;
}
