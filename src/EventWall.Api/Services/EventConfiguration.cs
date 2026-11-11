using System.Text.RegularExpressions;
using EventWall.Api.Data;

namespace EventWall.Api.Services;

public sealed class EventConfiguration
{
    public const string DefaultSlug = "communityevent";
    public const string DefaultName = "Community Event";
    public const string DefaultQuestionHashtag = "#communityevent";
    public static EventConfiguration Default { get; } = new(null);

    private readonly Regex _eventTerm;
    public string Slug { get; }
    public string Name { get; }
    public string QuestionHashtag { get; }
    public string Hashtag => QuestionHashtag[1..];
    public string XQuery => $"({QuestionHashtag} OR \"{Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\") -is:retweet -is:reply -is:quote";

    public EventConfiguration(IConfiguration? configuration)
    {
        Slug = EventSeeder.NormalizeSlug(configuration?["Event:Slug"] ?? DefaultSlug);
        Name = (configuration?["Event:Name"] ?? DefaultName).Trim();
        if (Name.Length is 0 or > 160 || Name.Any(char.IsControl))
            throw new InvalidOperationException("Event:Name must be nonempty, at most 160 characters, and contain no control characters.");
        QuestionHashtag = configuration?["Event:QuestionHashtag"] ?? DefaultQuestionHashtag;
        if (QuestionHashtag.Length is < 2 or > 81 || QuestionHashtag[0] != '#' ||
            QuestionHashtag.Skip(1).Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            throw new InvalidOperationException("Event:QuestionHashtag must be # followed by 1-80 letters, digits, or underscores.");

        var namePattern = string.Join(@"\s+", Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        _eventTerm = new Regex(
            $@"(?<![\p{{L}}\p{{M}}\p{{N}}_])(?:{Regex.Escape(QuestionHashtag)}|{namePattern})(?![\p{{L}}\p{{M}}\p{{N}}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));
    }

    public bool Matches(string? text) => !string.IsNullOrWhiteSpace(text) && _eventTerm.IsMatch(text);
}
