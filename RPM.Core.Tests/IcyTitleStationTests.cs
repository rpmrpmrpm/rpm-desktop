using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>
/// Removing a station's own name from the start of its title.
///
/// <para>Used by the home screen's radio card, which shows the station's name in bold and the title
/// under it. NPO Radio 2 sends "NPO Radio 2 - Dit is Jorien! - BNNVARA", so without this the name
/// would appear twice.</para>
/// </summary>
public class IcyTitleStationTests
{
    [Fact]
    public void The_station_naming_itself_first_is_taken_off()
        => Assert.Equal(
            "Dit is Jorien! - BNNVARA",
            IcyTitle.WithoutStation("NPO Radio 2 - Dit is Jorien! - BNNVARA", "NPO Radio 2"));

    /// <summary>The rest is kept whole. It is the station's own arrangement of the programme and
    /// the broadcaster, and this code cannot tell which part matters.</summary>
    [Fact]
    public void Only_the_leading_name_goes()
        => Assert.Equal(
            "Jay-Z - Song Cry",
            IcyTitle.WithoutStation("KINK - Jay-Z - Song Cry", "KINK"));

    [Fact]
    public void The_case_it_was_sent_in_does_not_matter()
        => Assert.Equal("Morning Show", IcyTitle.WithoutStation("npo radio 2 - Morning Show", "NPO Radio 2"));

    /// <summary>A song whose title only contains the station name is unchanged: only a leading name
    /// followed by a separator is removed.</summary>
    [Fact]
    public void A_title_that_mentions_the_station_is_left_alone()
    {
        Assert.Equal(
            "Radio Ga Ga - Queen",
            IcyTitle.WithoutStation("Radio Ga Ga - Queen", "Radio"));

        Assert.Equal(
            "KINKY Boots - The Cast",
            IcyTitle.WithoutStation("KINKY Boots - The Cast", "KINK"));
    }

    /// <summary>Several stations send only their own name between programmes. A blank line is worse
    /// than a repeated name, so the name is kept.</summary>
    [Fact]
    public void A_station_announcing_only_itself_keeps_its_name()
    {
        Assert.Equal("NPO Radio 2", IcyTitle.WithoutStation("NPO Radio 2", "NPO Radio 2"));

        // The title is trimmed first, so a trailing separator loses its space and does not match.
        // The name is returned rather than an empty line, which is the rule in both cases.
        Assert.Equal("NPO Radio 2 -", IcyTitle.WithoutStation("NPO Radio 2 - ", "NPO Radio 2"));
    }

    [Fact]
    public void An_announcement_by_another_name_is_untouched()
        => Assert.Equal(
            "Skrillex - Don't Get Too Close",
            IcyTitle.WithoutStation("Skrillex - Don't Get Too Close", "NPO Radio 2"));

    [Fact]
    public void Nothing_in_nothing_out()
    {
        Assert.Null(IcyTitle.WithoutStation(null, "NPO Radio 2"));
        Assert.Equal("", IcyTitle.WithoutStation("", "NPO Radio 2"));
        Assert.Equal("Something", IcyTitle.WithoutStation("Something", null));
        Assert.Equal("Something", IcyTitle.WithoutStation("Something", "  "));
    }

    /// <summary>The separators are the ones <see cref="IcyTitle.Split"/> already knows, so a station
    /// using an en dash is treated the same as one using a hyphen.</summary>
    [Theory]
    [InlineData("KINK - Jay-Z")]
    [InlineData("KINK – Jay-Z")]
    [InlineData("KINK — Jay-Z")]
    [InlineData("KINK -- Jay-Z")]
    public void Any_separator_the_splitter_knows(string announced)
        => Assert.Equal("Jay-Z", IcyTitle.WithoutStation(announced, "KINK"));
}
