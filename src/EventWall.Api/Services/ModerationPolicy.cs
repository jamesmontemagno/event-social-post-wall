using EventWall.Api.Models;

namespace EventWall.Api.Services;

public static class ModerationPolicy
{
    public static bool CanTransition(PostStatus current, PostStatus target) => target switch
    {
        PostStatus.Live => current is PostStatus.Pending or PostStatus.Rejected or PostStatus.Withdrawn,
        PostStatus.Rejected => current == PostStatus.Pending,
        PostStatus.Withdrawn => current == PostStatus.Live,
        _ => false
    };
}

public static class CsvCellEncoder
{
    public static string Encode(string value)
    {
        var significant = value.FirstOrDefault(character => !char.IsWhiteSpace(character) && !char.IsControl(character));
        if (significant is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
