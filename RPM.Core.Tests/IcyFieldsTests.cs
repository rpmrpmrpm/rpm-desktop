using RPM.Core.Radio;

namespace RPM.Core.Tests;

public class IcyFieldsTests
{
    // ── real title lines from the 102.7 KIIS FM stream ──

    /// <summary>The artist is before the separator and the title is in the FIELD. Read by the usual
    /// "artist - title" rule instead, the line gives an artist and no title, and the header shows
    /// "Kehlani" in place of the song name.</summary>
    [Fact]
    public void A_record_names_the_artist_in_front_and_the_title_in_the_field()
    {
        var read = IcyFields.Read(
            "Kehlani - text=\"Folded\" song_spot=\"M\" MediaBaseId=\"3133844\" itunesTrackId=\"0\" " +
            "amgTrackId=\"-1\" amgArtistId=\"0\" TAID=\"0\" TPID=\"396193730\" cartcutId=\"0443706001\" " +
            "amgArtworkURL=\"https://i.iheart.com/v3/catalog/track/396193730?ops=fit(200,200)\" " +
            "length=\"00:03:57\" unsID=\"-1\" spotInstanceId=\"-1\"");

        Assert.Equal("Kehlani - Folded", read.Spoken);
        Assert.Equal("Kehlani", read.Named!.Value.Artist);
        Assert.Equal("Folded", read.Named!.Value.Title);
        Assert.True(read.Named!.Value.IsSong);
    }

    /// <summary>Another song from the same stream.</summary>
    [Fact]
    public void The_next_record_reads_the_same_way()
    {
        var read = IcyFields.Read(
            "Olivia Rodrigo - text=\"Drop Dead\" song_spot=\"M\" MediaBaseId=\"3220182\" " +
            "amgTrackId=\"-1\" TPID=\"395031026\" length=\"00:03:42\" spotInstanceId=\"-1\"");

        Assert.Equal("Olivia Rodrigo - Drop Dead", read.Spoken);
        Assert.Equal("Olivia Rodrigo", read.Named!.Value.Artist);
        Assert.Equal("Drop Dead", read.Named!.Value.Title);
    }

    /// <summary>The station's own ident, which uses the same format but is not a song:
    /// <c>song_spot="T"</c>, a length of zero, and a slogan in the artist position. Shown as a pair it
    /// would be a song called "102.7 KIIS FM" by "LA's #1 Hit Music Station".</summary>
    [Fact]
    public void An_ident_is_a_line_and_not_a_record()
    {
        var read = IcyFields.Read(
            "LA's #1 Hit Music Station - text=\"102.7 KIIS FM\" song_spot=\"T\" MediaBaseId=\"0\" " +
            "amgTrackId=\"0\" amgArtworkURL=\"\" length=\"00:00:00\" spotInstanceId=\"-1\"");

        Assert.Equal("102.7 KIIS FM", read.Spoken);
        Assert.False(read.Named!.Value.IsSong);
        Assert.Null(read.Named!.Value.Artist);
    }

    /// <summary>A break marker, which has nothing before the separator.</summary>
    [Fact]
    public void A_break_marker_names_nobody()
    {
        var read = IcyFields.Read("- text=\"Spot Block End\" amgTrackId=\"9876543\" length=\"00:00:00\"");

        Assert.Equal("Spot Block End", read.Spoken);
        Assert.False(read.Named!.Value.IsSong);
        Assert.Null(read.Named!.Value.Artist);
    }

    /// <summary>Only Triton writes <c>song_spot</c>. On another encoder, a title in a field with no
    /// ident marker is still a song.</summary>
    [Fact]
    public void Both_halves_and_no_spot_is_still_a_record()
    {
        var read = IcyFields.Read("Herbie Hancock - text=\"Chameleon\" length=\"00:15:41\"");

        Assert.Equal("Herbie Hancock - Chameleon", read.Spoken);
        Assert.Equal("Herbie Hancock", read.Named!.Value.Artist);
        Assert.Equal("Chameleon", read.Named!.Value.Title);
    }

    /// <summary>The line and the pair agree, so code that reads the line rather than the pair (the
    /// dial, the ad marker, a split) cannot disagree with the header.</summary>
    [Theory]
    [InlineData("Kehlani - text=\"Folded\" song_spot=\"M\" length=\"00:03:57\"")]
    [InlineData("Olivia Rodrigo - text=\"Drop Dead\" song_spot=\"M\" length=\"00:03:42\"")]
    public void The_line_splits_into_the_halves_it_named(string announced)
    {
        var read = IcyFields.Read(announced);
        var (artist, title) = IcyTitle.Split(read.Spoken);

        Assert.Equal(read.Named!.Value.Artist, artist);
        Assert.Equal(read.Named!.Value.Title, title);
    }

    // ── the second format: title="…",artist="…",url="…" ──

    /// <summary>A real line from 99.9 Kiss Country, unchanged. The url field holds the first format's
    /// fields with their quotes not escaped, and the song is marked "F".</summary>
    [Fact]
    public void A_record_in_the_listed_shape_names_both_halves()
    {
        var read = IcyFields.Read(
            "title=\"Good Vibes\",artist=\"CHRIS JANSON\",url=\"song_spot=\"F\" MediaBaseId=\"0\" " +
            "itunesTrackId=\"0\" amgTrackId=\"-1\" amgArtistId=\"0\" TAID=\"0\" TPID=\"68828468\" cartcutId=\"0\" " +
            "amgArtworkURL=\"https://i.iheart.com/v3/catalog/track/68828468?ops=fit(200,200),format(%22jpeg%22)\" " +
            "length=\"00:02:42\" unsID=\"-1\" spotInstanceId=\"d27aa236-4e8c-40ed-84ef-c16a35bd8f11\"\"");

        Assert.Equal("CHRIS JANSON - Good Vibes", read.Spoken);
        Assert.Equal("CHRIS JANSON", read.Named!.Value.Artist);
        Assert.Equal("Good Vibes", read.Named!.Value.Title);
        Assert.True(read.Named!.Value.IsSong);
    }

    /// <summary>Real lines from 102.7 KIIS FM: the block is cut before the url field closes.
    /// Apostrophes and a slash inside the artist or title are part of the text, not
    /// punctuation.</summary>
    [Theory]
    [InlineData("title=\"Umbrella\",artist=\"RIHANNA / JAY-Z\",url=\"song_spot=\"F\" MediaBaseId=\"0\" len",
                "RIHANNA / JAY-Z", "Umbrella")]
    [InlineData("title=\"I Ain't Sayin'\",artist=\"JORDAN DAVIS\",url=\"song_spot=\"F\"",
                "JORDAN DAVIS", "I Ain't Sayin'")]
    [InlineData("title=\"If He Did It Before {Same God}\",artist=\"TYE TRIBBETT\",url=\"song_spot=\"F\"",
                "TYE TRIBBETT", "If He Did It Before {Same God}")]
    [InlineData("title=\"Umbrella\",artist=\"RIHANNA / JAY-Z\",url=\"song_spot=\"M\"",
                "RIHANNA / JAY-Z", "Umbrella")]
    public void The_listed_shape_reads_the_same_whatever_the_title_holds(string announced, string artist, string title)
    {
        var read = IcyFields.Read(announced);

        Assert.Equal($"{artist} - {title}", read.Spoken);
        Assert.Equal(artist, read.Named!.Value.Artist);
        Assert.Equal(title, read.Named!.Value.Title);
    }

    /// <summary>A quotation mark or a comma inside the title does not end it: only <c>",</c> followed by
    /// the next field's name does.</summary>
    [Fact]
    public void A_quote_and_a_comma_inside_the_title_stay_in_it()
    {
        var read = IcyFields.Read("title=\"Hello, \"Goodbye\"\",artist=\"THE BEATLES\",url=\"song_spot=\"F\"\"");

        Assert.Equal("Hello, \"Goodbye\"", read.Named!.Value.Title);
        Assert.Equal("THE BEATLES", read.Named!.Value.Artist);
    }

    /// <summary>The station's ident mark, "T", is not a song in this format either.</summary>
    [Fact]
    public void An_ident_in_the_listed_shape_is_a_line()
    {
        var read = IcyFields.Read(
            "title=\"99.9 Kiss Country\",artist=\"Miami's #1 For New Country\",url=\"song_spot=\"T\" length=\"00:00:00\"\"");

        Assert.Equal("99.9 Kiss Country", read.Spoken);
        Assert.False(read.Named!.Value.IsSong);
        Assert.Null(read.Named!.Value.Artist);
    }

    /// <summary>"F" marks songs in the first format too, if a station writes it there.</summary>
    [Fact]
    public void An_F_mark_in_the_first_shape_is_a_record()
    {
        var read = IcyFields.Read("Kim Wilde - text=\"You Keep Me Hangin' On\" song_spot=\"F\" length=\"00:04:13\"");

        Assert.True(read.Named!.Value.IsSong);
        Assert.Equal("Kim Wilde", read.Named!.Value.Artist);
    }

    /// <summary>Only the artist, only the title, or neither.</summary>
    [Theory]
    [InlineData("title=\"\",artist=\"\",url=\"song_spot=\"F\"\"", null)]
    [InlineData("title=\"Good Vibes\",artist=\"\",url=\"song_spot=\"F\"\"", "Good Vibes")]
    [InlineData("title=\"\",artist=\"CHRIS JANSON\",url=\"song_spot=\"F\"\"", "CHRIS JANSON")]
    public void The_listed_shape_with_a_half_missing(string announced, string? spoken)
    {
        var read = IcyFields.Read(announced);

        Assert.Equal(spoken, read.Spoken);
        Assert.Null(read.Named?.Artist);
    }

    /// <summary>A plain title that starts with the word "title" is not the second format.</summary>
    [Theory]
    [InlineData("Title Fight - Head In The Ceiling Fan")]
    [InlineData("Artist Vs Poet - Car Crash")]
    public void A_plain_title_starting_with_the_word_is_left_alone(string announced)
    {
        var read = IcyFields.Read(announced);

        Assert.Equal(announced, read.Spoken);
        Assert.Null(read.Named);
    }

    // ── input that must not change ──

    /// <summary>Most stations send a plain title. IcyFields must not change it, and must not decide
    /// which part is the artist: IcyOrders does that.</summary>
    [Theory]
    [InlineData("Herbie Hancock - Chameleon")]
    [InlineData("Simple")]
    [InlineData("Guns N' Roses - Patience")]
    [InlineData("AC/DC - T.N.T.")]
    [InlineData("Blue Öyster Cult - (Don't Fear) The Reaper - Live")]
    [InlineData("Man! I Feel Like A Woman - SHANIA TWAIN")]
    public void An_announcement_with_no_fields_in_it_comes_back_as_it_went_in(string announced)
    {
        var read = IcyFields.Read(announced);

        Assert.Equal(announced, read.Spoken);
        Assert.Null(read.Named);
    }

    /// <summary>A title may contain an equals sign and a quotation mark. A field is recognised by the
    /// space before its name, not by the punctuation after it.</summary>
    [Theory]
    [InlineData("Shostakovich - Symphony No. 5 =\"Fate\"")]
    [InlineData("Prince - Sign O' The Times")]
    public void Punctuation_inside_a_title_is_not_a_field(string announced)
    {
        Assert.Equal(announced, IcyFields.Read(announced).Spoken);
    }

    /// <summary>Reading an already parsed line changes nothing, so it is safe to parse a line
    /// twice.</summary>
    [Theory]
    [InlineData("Kehlani - text=\"Folded\" song_spot=\"M\" length=\"00:03:57\"")]
    [InlineData("- text=\"Spot Block End\" amgTrackId=\"9876543\" length=\"00:00:00\"")]
    [InlineData("title=\"Good Vibes\",artist=\"CHRIS JANSON\",url=\"song_spot=\"F\"\"")]
    [InlineData("Herbie Hancock - Chameleon")]
    public void Reading_twice_says_what_reading_once_said(string announced)
    {
        var once = IcyFields.Read(announced).Spoken;

        Assert.Equal(once, IcyFields.Read(once).Spoken);
    }

    // ── the edges ──

    /// <summary>Only ids: the station has sent no title, so the result is empty.</summary>
    [Theory]
    [InlineData("- amgTrackId=\"9876543\" length=\"00:00:00\"")]
    [InlineData("- text=\"\" length=\"00:00:00\"")]
    [InlineData("-")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_said_is_nothing_held(string? announced)
    {
        var read = IcyFields.Read(announced);

        Assert.Null(read.Spoken);
        Assert.Null(read.Named);
    }

    /// <summary>The block is padded to sixteen bytes and can be cut in the middle of a field. The
    /// text that is there is used.</summary>
    [Fact]
    public void A_field_cut_short_still_gives_up_what_it_has()
    {
        Assert.Equal("Spot Block En", IcyFields.Read("- text=\"Spot Block En").Spoken);
    }

    /// <summary>Some field names end with other field names: "itunesTrackId" ends with "TrackId". A
    /// name matches only at the start of a word.</summary>
    [Fact]
    public void A_longer_field_name_does_not_answer_for_a_shorter_one()
    {
        Assert.Equal(
            "Spot Block End",
            IcyFields.Read("- spotText=\"nonsense\" text=\"Spot Block End\" length=\"00:00:00\"").Spoken);
    }

    /// <summary>Text before the fields and no text field means the text alone is the title: a
    /// station that writes its ids after a plain title has sent only the title.</summary>
    [Fact]
    public void A_head_with_no_text_field_is_the_whole_announcement()
    {
        var read = IcyFields.Read("Herbie Hancock - Chameleon length=\"00:15:41\" spotInstanceId=\"-1\"");

        Assert.Equal("Herbie Hancock - Chameleon", read.Spoken);
        Assert.Null(read.Named);     // the line does not say which part is the artist, so no pair
    }
}
