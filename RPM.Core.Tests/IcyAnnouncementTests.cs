using RPM.Core.Radio;

namespace RPM.Core.Tests;

public class IcyAnnouncementTests
{
    // ── the two failures, using real stream titles ──

    /// <summary>Qmusic's real StreamTitle. Read by the usual "artist - title" rule, the artist would be
    /// "Man! I Feel Like A Woman", and the banner would show the song name as the artist.</summary>
    [Fact]
    public void Qmusic_announces_the_pair_the_other_way_round()
    {
        var heard = IcyAnnouncement.Read("Man! I Feel Like A Woman - SHANIA TWAIN", "Qmusic");

        Assert.True(heard.IsSong);
        Assert.Equal("SHANIA TWAIN", heard.Artist);
        Assert.Equal("Man! I Feel Like A Woman", heard.Title);
    }

    /// <summary>NPO Radio 2's real StreamTitle: the station, the programme, the broadcaster. Read by
    /// the usual rule, the artist would be "NPO Radio 2".</summary>
    [Fact]
    public void A_station_talking_about_itself_is_not_a_record()
    {
        var heard = IcyAnnouncement.Read("NPO Radio 2 - RINKELDEKINKEL - BNNVARA", "NPO Radio 2");

        Assert.False(heard.IsSong);
        Assert.Null(heard.Artist);
        Assert.Equal("NPO Radio 2 - RINKELDEKINKEL - BNNVARA", heard.Title);
    }

    /// <summary>The directory's name for the station may include a slogan that the station's own
    /// titles do not.</summary>
    [Fact]
    public void The_station_is_recognised_by_its_printed_name_too()
    {
        var heard = IcyAnnouncement.Read("Intense Radio - Something", "Intense Radio - We love Dance");

        Assert.False(heard.IsSong);
    }

    // ── normal stations, which must not be affected ──

    [Fact]
    public void The_convention_is_what_an_ordinary_station_is_read_by()
    {
        var heard = IcyAnnouncement.Read("Fleetwood Mac - Dreams", "Sky Radio");

        Assert.True(heard.IsSong);
        Assert.Equal("Fleetwood Mac", heard.Artist);
        Assert.Equal("Dreams", heard.Title);
    }

    /// <summary>When the whole line is in capitals, the capitals carry no meaning, and the usual rule
    /// is used.</summary>
    [Fact]
    public void A_station_that_capitalises_everything_is_left_alone()
    {
        var heard = IcyAnnouncement.Read("FLEETWOOD MAC - DREAMS", "Some Station");

        Assert.Equal("FLEETWOOD MAC", heard.Artist);
        Assert.Equal("DREAMS", heard.Title);
    }

    /// <summary>An initialism is not a signal. Swapping this pair would be worse than leaving it, and
    /// many band names are written this way.</summary>
    [Theory]
    [InlineData("R.E.M. - Losing My Religion", "R.E.M.", "Losing My Religion")]
    [InlineData("U2 - One", "U2", "One")]
    [InlineData("AC/DC - Back In Black", "AC/DC", "Back In Black")]
    [InlineData("ABBA - Waterloo", "ABBA", "Waterloo")]
    public void An_initialism_at_the_head_is_still_the_artist(string announced, string artist, string title)
    {
        var heard = IcyAnnouncement.Read(announced, "Some Station");

        Assert.Equal(artist, heard.Artist);
        Assert.Equal(title, heard.Title);
    }

    /// <summary>A TITLE in capitals next to a normal artist name is the one case the rule gets wrong.
    /// The test records this known limitation of the rule.</summary>
    [Fact]
    public void A_shouted_title_is_the_signals_own_failure()
    {
        var heard = IcyAnnouncement.Read("Bill Withers - AIN'T NO SUNSHINE", "Some Station");

        Assert.Equal("Bill Withers", heard.Title);   // read as the title, wrongly
        Assert.Equal("AIN'T NO SUNSHINE", heard.Artist);
    }

    // ── titles that are not an artist and title pair ──

    [Fact]
    public void An_announcement_with_no_separator_is_a_title_alone()
    {
        var heard = IcyAnnouncement.Read("Nachtdienst", "Sky Radio");

        Assert.True(heard.IsSong);
        Assert.Null(heard.Artist);
        Assert.Equal("Nachtdienst", heard.Title);
    }

    [Fact]
    public void Nothing_announced_is_nothing()
    {
        var heard = IcyAnnouncement.Read(null, "Sky Radio");

        Assert.False(heard.IsSong);
        Assert.Null(heard.Title);

        Assert.False(IcyAnnouncement.Read("   ", "Sky Radio").IsSong);
    }

    /// <summary>A hyphen inside a name is not a separator. This is the splitter's own rule, and this
    /// code must keep it.</summary>
    [Fact]
    public void A_hyphenated_name_survives()
    {
        var heard = IcyAnnouncement.Read("Jay-Z - 99 Problems", "Some Station");

        Assert.Equal("Jay-Z", heard.Artist);
        Assert.Equal("99 Problems", heard.Title);
    }
}
