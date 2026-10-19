using RPM.Core.Radio;

namespace RPM.Core.Tests;

public class RadioNamesTests
{
    /// <summary>Real names, as the directory files them.</summary>
    [Theory]
    [InlineData("PureClassixx #Flac", "PureClassixx")]
    [InlineData("Intense Radio - We love Dance #HQ# FLAC", "Intense Radio")]
    [InlineData("Smooth FM 91.5 - Melbourne - 91.5 FM (AAC+ 320k)", "Smooth FM 91.5")]
    [InlineData("Heart London 106.2 [MP3]", "Heart London 106.2")]
    [InlineData("Radio Paradise (320k FLAC)", "Radio Paradise")]
    [InlineData("Some Station - MP3 128k", "Some Station")]
    [InlineData("Some Station | 320 kbps", "Some Station")]
    [InlineData("Jazz24 #HQ", "Jazz24")]
    public void The_streams_specification_comes_out_of_the_name(string name, string shown)
    {
        Assert.Equal(shown, RadioNames.Clean(name));
    }

    /// <summary>Numbers and punctuation in a station's name are usually part of the NAME. Getting this
    /// wrong in the other direction renames a broadcaster, which is worse than an untidy line.</summary>
    [Theory]
    [InlineData("Radio 538")]
    [InlineData("100% NL")]
    [InlineData("538 TOP 50")]
    [InlineData("Studio Brussel")]
    [InlineData("NPO Radio 1")]
    [InlineData("Radio 10")]
    [InlineData("Sky Radio 101 FM")]
    [InlineData("3FM")]
    [InlineData("Q music Nederland")]
    [InlineData("Arrow Classic Rock")]
    [InlineData("Radio Gelderland")]
    public void A_station_name_is_left_exactly_as_it_was_written(string name)
    {
        Assert.Equal(name, RadioNames.Clean(name));
    }

    /// <summary>A bitrate is a number in the band a stream is served at. Outside it, the number is a
    /// frequency or part of a name.</summary>
    [Fact]
    public void A_number_is_only_a_bitrate_inside_the_band_a_stream_uses()
    {
        Assert.Equal("Radio 4", RadioNames.Clean("Radio 4"));
        Assert.Equal("Kiss 108", RadioNames.Clean("Kiss 108"));
        Assert.Equal("Some Station", RadioNames.Clean("Some Station 128k"));
    }

    /// <summary>A blank row is worse than an untidy one.</summary>
    [Fact]
    public void A_name_that_is_nothing_but_specification_keeps_it()
    {
        Assert.Equal("FLAC", RadioNames.Clean("FLAC"));
        Assert.Equal("#HQ#", RadioNames.Clean("#HQ#"));
    }

    [Fact]
    public void Nothing_to_clean_comes_back_as_it_was()
    {
        Assert.Equal("", RadioNames.Clean(""));
        Assert.Equal("", RadioNames.Clean(null));
    }

    // ── and the tag line ──

    /// <summary>The codec and the bitrate are already printed beside these, taken from the fields that
    /// hold them properly. "##browserfriendly" is a note to the directory, not to a listener.</summary>
    [Fact]
    public void The_tag_line_keeps_only_what_a_station_plays()
    {
        Assert.Equal(
            ["90s", "club", "dance"],
            RadioNames.Genres(["90s", "club", "dance", "flac", "##browserfriendly", "320k"]));
    }

    [Fact]
    public void Qmusics_tag_line_loses_the_directorys_own_note()
    {
        Assert.Equal(["mix", "pop"], RadioNames.Genres(["##browserfriendly", "mix", "pop"]));
    }

    [Fact]
    public void A_station_with_no_genres_left_shows_none()
    {
        Assert.Empty(RadioNames.Genres(["mp3", "128k", "#hq"]));
        Assert.Empty(RadioNames.Genres([]));
        Assert.Empty(RadioNames.Genres(null));
    }

    // ── the strapline after a dash ──

    [Theory]
    [InlineData("Intense Radio - We love Dance", "Intense Radio")]
    [InlineData("Radio 538 - Non-stop", "Radio 538")]
    [InlineData("KINK – Distortion", "KINK")]
    [InlineData("Some Station | The best hits", "Some Station")]
    [InlineData("A - B - C", "A")]
    public void Everything_after_a_spaced_dash_is_a_strapline(string name, string shown)
    {
        Assert.Equal(shown, RadioNames.Clean(name));
    }

    /// <summary>A hyphen inside a word is part of the word. Cutting at any dash would rename every one
    /// of these, and a renamed station is worse than a station with a strapline.</summary>
    [Theory]
    [InlineData("Hi-Fi Radio")]
    [InlineData("K-Rock")]
    [InlineData("Radio X-tra")]
    [InlineData("Rock-FM")]
    [InlineData("Non-Stop Pop")]
    [InlineData("1-2-3 Radio")]
    public void A_hyphen_inside_a_word_is_not_a_strapline(string name)
    {
        Assert.Equal(name, RadioNames.Clean(name));
    }

    /// <summary>A name that starts with one has no head to keep, so it keeps all of itself, and the
    /// dash left at the front is tidied away like any other.</summary>
    [Fact]
    public void A_name_that_starts_with_a_dash_keeps_its_words()
    {
        Assert.Equal("The Station", RadioNames.Clean("- The Station"));
        Assert.Equal("Only This", RadioNames.Clean(" - Only This"));
    }

    // ── the strapline kept, where it is the only thing telling two stations apart ──

    private static RadioStation Station(string name)
        => new() { Uuid = name, Name = name, StreamUrl = "http://example.test/" + name };

    private static string[] Shown(params string[] names)
        => RadioNames.Distinguish([.. names.Select(Station)]).Select(s => s.Display).ToArray();

    /// <summary>Concertzender broadcasts these as separate stations. Both reduce to "Concertzender",
    /// and a list that shows that twice looks like a bug.</summary>
    [Fact]
    public void Two_stations_sharing_a_head_keep_their_straplines()
    {
        Assert.Equal(
            ["Concertzender - De Gehoorde Stilte", "Concertzender - Hard Bop"],
            Shown("Concertzender - De Gehoorde Stilte", "Concertzender - Hard Bop"));
    }

    /// <summary>Only the ones that collide. A strapline is noise until it is the difference.</summary>
    [Fact]
    public void The_stations_that_share_nothing_still_lose_theirs()
    {
        Assert.Equal(
            ["Concertzender - De Gehoorde Stilte", "Concertzender - Hard Bop", "Intense Radio"],
            Shown("Concertzender - De Gehoorde Stilte", "Concertzender - Hard Bop",
                  "Intense Radio - We love Dance"));
    }

    /// <summary>One of the pair has no strapline to keep. It prints as it is, and the other's is what
    /// separates them.</summary>
    [Fact]
    public void A_bare_name_beside_a_strapline_is_left_alone()
    {
        Assert.Equal(
            ["Concertzender", "Concertzender - Hard Bop"],
            Shown("Concertzender", "Concertzender - Hard Bop"));
    }

    /// <summary>Two entries of one station, spelt the same. There is no strapline to put back.</summary>
    [Fact]
    public void Two_of_the_same_name_stay_the_same_name()
    {
        Assert.Equal(["Radio 538", "Radio 538"], Shown("Radio 538", "Radio 538"));
    }

    /// <summary>The ordinary list, where nothing shares a head, is returned unchanged.</summary>
    [Fact]
    public void A_list_with_no_collisions_is_not_rewritten()
    {
        IReadOnlyList<RadioStation> stations = [Station("Radio 538"), Station("Sky Radio")];

        Assert.Same(stations, RadioNames.Distinguish(stations));
    }

    /// <summary>The specification still comes off, collision or not: it is never what tells them
    /// apart.</summary>
    [Fact]
    public void A_kept_strapline_still_loses_the_specification()
    {
        Assert.Equal(
            ["Jazz FM - Bebop", "Jazz FM - Hard Bop"],
            Shown("Jazz FM - Bebop #HQ FLAC", "Jazz FM - Hard Bop (MP3 320k)"));
    }

    // ── the folded name: how two ENTRIES for one station are told from two stations ──

    /// <summary>The words that say "this is a radio station" carry no meaning: one station arrives
    /// under every combination of them that a contributor chose to type.</summary>
    [Theory]
    [InlineData("Heart", "GB")]
    [InlineData("Heart FM", "GB")]
    [InlineData("Heart UK", "GB")]
    [InlineData("HEART (UK)", "GB")]
    public void One_station_folds_to_one_key_however_it_was_typed(string name, string country)
        => Assert.Equal("heart", RadioNames.Fold(name, country));

    /// <summary>Four spellings of Qmusic as the Dutch directory lists them. This is the case the
    /// folding exists for.</summary>
    [Theory]
    [InlineData("Qmusic")]
    [InlineData("Q-Music")]
    [InlineData("Q music Nederland")]
    [InlineData("QMUSIC NEDERLAND")]
    [InlineData("QMUSIC NEDERLAND AAC HIGH")]
    public void The_spellings_of_one_dutch_station_fold_together(string name)
        => Assert.Equal("qmusic", RadioNames.Fold(name, "NL"));

    /// <summary>
    /// A broadcaster's other channels are NOT the same station, and they differ from it by exactly one
    /// word. This is the line the fold must not cross: equal, never a prefix.
    /// </summary>
    [Fact]
    public void A_broadcasters_other_channels_keep_their_own_keys()
    {
        var parent = RadioNames.Fold("Radio 10", "NL");

        Assert.NotEqual(parent, RadioNames.Fold("Radio 10 non-stop", "NL"));
        Assert.NotEqual(parent, RadioNames.Fold("Radio 10 60's & 70's Hits", "NL"));
        Assert.NotEqual(RadioNames.Fold("Qmusic", "NL"), RadioNames.Fold("Qmusic Non-Stop", "NL"));
        Assert.NotEqual(RadioNames.Fold("Sky Radio", "NL"), RadioNames.Fold("Sky Radio Christmas", "NL"));
    }

    /// <summary>
    /// A country word at the head of a name is the brand. If it were dropped there, GB News would
    /// fold into a station called News Radio UK.
    /// </summary>
    [Fact]
    public void A_country_word_at_the_head_is_part_of_the_name()
    {
        Assert.Equal("gbnews", RadioNames.Fold("GB News", "GB"));
        Assert.NotEqual(RadioNames.Fold("GB News", "GB"), RadioNames.Fold("News Radio UK", "GB"));
        Assert.Equal("franceinter", RadioNames.Fold("France Inter", "FR"));
    }

    /// <summary>…and after the head it is a qualifier, which one contributor writes and the next does
    /// not.</summary>
    [Fact]
    public void A_country_word_after_the_head_is_a_qualifier()
    {
        Assert.Equal(RadioNames.Fold("Classic FM", "GB"), RadioNames.Fold("Classic FM UK", "GB"));
        Assert.Equal(RadioNames.Fold("Virgin Radio Italia", "IT"), RadioNames.Fold("Virgin Radio Italy", "IT"));
        Assert.Equal(RadioNames.Fold("Nostalgie", "FR"), RadioNames.Fold("Radio Nostalgie France", "FR"));
    }

    /// <summary>Only in the country it belongs to: "Italia" is a qualifier on an Italian station and a
    /// word on anybody else's.</summary>
    [Fact]
    public void A_country_word_is_only_dropped_in_its_own_country()
    {
        Assert.NotEqual(RadioNames.Fold("Virgin Radio", "GB"), RadioNames.Fold("Virgin Radio Italia", "GB"));
        Assert.Equal("italia", RadioNames.Fold("Radio Italia", "GB"));
    }

    /// <summary>The specification comes off first, by the same reader the shown name uses.</summary>
    [Fact]
    public void The_specification_is_not_part_of_the_key()
    {
        Assert.Equal(RadioNames.Fold("BBC Radio 4", "GB"), RadioNames.Fold("BBC Radio 4 (128k)", "GB"));
        Assert.Equal(RadioNames.Fold("KINK", "NL"), RadioNames.Fold("Kink 192k mp3", "NL"));
        Assert.Equal(RadioNames.Fold("Radio Caroline", "GB"), RadioNames.Fold("Radio Caroline AAC 96 kbps", "GB"));
    }

    /// <summary>
    /// Read as Unicode, not as ASCII. A reader that kept only ASCII letters would turn "Вести FM" and
    /// "Ретро FM" into the one word "fm" and make two Russian stations one.
    /// </summary>
    [Fact]
    public void A_name_in_another_script_keeps_its_letters()
    {
        Assert.NotEqual(RadioNames.Fold("Вести FM", "RU"), RadioNames.Fold("Ретро FM", "RU"));
        Assert.NotEmpty(RadioNames.Fold("美国之音", "CN"));
    }

    /// <summary>Accents are spelling, not identity.</summary>
    [Fact]
    public void Accents_fold_away()
        => Assert.Equal(RadioNames.Fold("Radio Comercial", "PT"), RadioNames.Fold("Rádio Comercial", "PT"));

    /// <summary>
    /// A name made of nothing but noise keeps it. A station filed under "" would be folded into every
    /// other station filed under "".
    /// </summary>
    [Fact]
    public void A_name_that_is_all_noise_still_files_under_something()
    {
        Assert.Equal("radio", RadioNames.Fold("Radio", "NL"));
        Assert.Equal("thestation", RadioNames.Fold("The Station", "NL"));
        Assert.Equal("nederland", RadioNames.Fold("Radio Nederland", "NL"));
        Assert.Equal("", RadioNames.Fold("", "NL"));
        Assert.Equal("", RadioNames.Fold(null, null));
    }

    /// <summary>Without a country code nothing is a country word.</summary>
    [Fact]
    public void No_country_means_no_qualifier_is_dropped()
        => Assert.Equal("classicuk", RadioNames.Fold("Classic FM UK", null));
}
