using EventWall.Api.Services;
using EventWall.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EventWall.Api.Tests;

public class EventConfigurationTests
{
    [Fact]
    public async Task Branding_updates_preserve_intake_state_and_reject_a_second_event()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new EventWallDbContext(
            new DbContextOptionsBuilder<EventWallDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Event:Slug"] = "my-event",
            ["Event:Name"] = "Original name"
        }).Build();
        await EventSeeder.SeedAsync(database, configuration);
        var settings = await database.Events.SingleAsync();
        settings.SubmissionsOpen = true;
        await database.SaveChangesAsync();
        configuration["Event:Name"] = "Updated name";
        await EventSeeder.SeedAsync(database, configuration);
        Assert.Equal("Updated name", settings.Name);
        Assert.True(settings.SubmissionsOpen);
        configuration["Event:Slug"] = "another-event";
        await Assert.ThrowsAsync<InvalidOperationException>(() => EventSeeder.SeedAsync(database, configuration));
        Assert.Single(await database.Events.ToListAsync());
    }

    [Theory]
    [InlineData("Hello #My_Event!", true)]
    [InlineData("See you at C++ Days", true)]
    [InlineData("See you at c++   days", true)]
    [InlineData("#My_Event_extra", false)]
    [InlineData("#My_Event\u0301", false)]
    [InlineData("Ccc Days", false)]
    [InlineData("#communityevent", false)]
    public void Matching_uses_configured_literal_terms_and_tag_boundaries(string text, bool matches)
    {
        var configuration = new EventConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Event:Name"] = "C++ Days",
            ["Event:QuestionHashtag"] = "#My_Event"
        }).Build());
        Assert.Equal(matches, configuration.Matches(text));
        Assert.Equal("(#My_Event OR \"C++ Days\") -is:retweet -is:reply -is:quote", configuration.XQuery);
        Assert.Equal("My_Event", configuration.Hashtag);
    }

    [Theory]
    [InlineData("Event:Name", " ")]
    [InlineData("Event:Name", "event\nname")]
    [InlineData("Event:QuestionHashtag", "#")]
    [InlineData("Event:QuestionHashtag", "#event OR #other")]
    [InlineData("Event:Slug", "two events")]
    public void Invalid_event_configuration_fails_explicitly(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => new EventConfiguration(configuration));
    }

    [Fact]
    public void X_query_escapes_quoted_event_names_without_adding_operators()
    {
        var configuration = new EventConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Event:Name"] = "The \"Community\" Event"
        }).Build());
        Assert.Equal("(#communityevent OR \"The \\\"Community\\\" Event\") -is:retweet -is:reply -is:quote", configuration.XQuery);
    }
}
