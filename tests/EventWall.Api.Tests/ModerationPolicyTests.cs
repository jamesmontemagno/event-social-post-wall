using EventWall.Api.Models;
using EventWall.Api.Services;

namespace EventWall.Api.Tests;

public sealed class ModerationPolicyTests
{
    [Theory]
    [InlineData(PostStatus.Pending, PostStatus.Live, true)]
    [InlineData(PostStatus.Pending, PostStatus.Rejected, true)]
    [InlineData(PostStatus.Live, PostStatus.Withdrawn, true)]
    [InlineData(PostStatus.Rejected, PostStatus.Live, true)]
    [InlineData(PostStatus.Withdrawn, PostStatus.Live, true)]
    [InlineData(PostStatus.Live, PostStatus.Rejected, false)]
    [InlineData(PostStatus.Rejected, PostStatus.Withdrawn, false)]
    [InlineData(PostStatus.Pending, PostStatus.Withdrawn, false)]
    public void Moderation_transitions_match_the_event_workflow(
        PostStatus current,
        PostStatus target,
        bool expected)
    {
        Assert.Equal(expected, ModerationPolicy.CanTransition(current, target));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("   =HYPERLINK(\"https://example.com\")")]
    [InlineData("\r\n@SUM(A1:A2)")]
    [InlineData("+cmd")]
    [InlineData("-1+1")]
    public void Csv_encoder_neutralizes_spreadsheet_formulas(string value)
    {
        var encoded = CsvCellEncoder.Encode(value);

        Assert.StartsWith("\"'", encoded);
        Assert.EndsWith("\"", encoded);
    }

    [Fact]
    public void Csv_encoder_quotes_delimiters_and_escapes_quotes()
    {
        var encoded = CsvCellEncoder.Encode("A, \"quote\"");

        Assert.Equal("\"A, \"\"quote\"\"\"", encoded);
    }
}
