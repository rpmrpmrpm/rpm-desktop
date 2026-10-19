using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>
/// What the radio pane's filter keeps. Most of what is tested is the station that must NOT be kept: a
/// filter fails by hiding a station, and a listener who has narrowed the dial cannot tell a station
/// that was filtered out from one the directory never had.
/// </summary>
public class StationSieveTests
{
    private static RadioStation Station(
        string name = "Test FM",
        string? codec = "MP3",
        string[]? tags = null,
        string? homepage = null)
        => new()
        {
            Uuid = name,
            Name = name,
            StreamUrl = "http://example.test/stream",
            Codec = codec,
            Tags = tags ?? [],
            Homepage = homepage,
        };

    [Fact]
    public void A_sieve_that_asks_nothing_keeps_everybody()
    {
        var all = new[] { Station("A"), Station("B", codec: null), Station("C", codec: "FLAC") };

        Assert.False(StationSieve.Off.IsOn);
        Assert.Same(all, StationSieve.Off.Through(all));
    }

    [Theory]
    [InlineData("MP3", "MP3", true)]
    [InlineData("mp3", "MP3", true)]
    [InlineData("AAC+", "AAC+", true)]
    [InlineData("AACP", "AAC+", true)]
    [InlineData("VORBIS", "OGG", true)]
    // The two AACs are two entries on the menu, and picking one is not picking the other.
    [InlineData("AAC+", "AAC", false)]
    [InlineData("AAC", "AAC+", false)]
    [InlineData("MP3", "OGG", false)]
    public void A_format_is_the_codec_the_station_filed(string codec, string wanted, bool kept)
        => Assert.Equal(kept, new StationSieve { Format = wanted }.Keeps(Station(codec: codec)));

    /// <summary>A station serves more than one thing under one field, and each part answers for
    /// itself.</summary>
    [Theory]
    [InlineData("AAC+,H.264", "AAC+")]
    [InlineData("MP3, OGG", "OGG")]
    public void A_station_that_files_two_codecs_answers_for_both(string codec, string wanted)
        => Assert.True(new StationSieve { Format = wanted }.Keeps(Station(codec: codec)));

    /// <summary>Silence is not a format. A station that filed no codec is not claimed for one.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_station_that_filed_no_codec_is_not_kept_by_a_format(string? codec)
        => Assert.False(new StationSieve { Format = "MP3" }.Keeps(Station(codec: codec)));

    /// <summary>The directory's tags are crowd-entered and spelt every way there is. They are read
    /// through radio's own list, so all of these are the one kind of station.</summary>
    [Theory]
    [InlineData("hip hop")]
    [InlineData("hip-hop")]
    [InlineData("HipHop")]
    [InlineData("rap")]
    [InlineData("urban")]
    public void A_genre_is_read_through_radios_own_list(string tag)
        => Assert.True(new StationSieve { Genre = "Hip-Hop" }.Keeps(Station(tags: [tag])));

    /// <summary>A dial is not a record collection: the spoken formats are stations too, and a listener
    /// reaches for them as readily as for Jazz.</summary>
    [Theory]
    [InlineData("talk", "Talk")]
    [InlineData("talk show", "Talk")]
    [InlineData("spoken word", "Talk")]
    [InlineData("news", "News")]
    [InlineData("newstalk", "News")]
    [InlineData("sport", "Sports")]
    [InlineData("sports talk", "Sports")]
    public void The_spoken_formats_are_on_the_list_too(string tag, string genre)
        => Assert.True(new StationSieve { Genre = genre }.Keeps(Station(tags: [tag])));

    /// <summary>Whole tags. "Classic rock" is its own format and holds the word "rock", and a rule that
    /// looked for a genre inside a tag would file every one of those under Rock.</summary>
    [Fact]
    public void A_tag_that_merely_holds_a_genre_is_not_that_genre()
    {
        Assert.False(new StationSieve { Genre = "Rock" }.Keeps(Station(tags: ["classic rock"])));
        Assert.True(new StationSieve { Genre = "Classic Rock" }.Keeps(Station(tags: ["classic rock"])));

        Assert.False(new StationSieve { Genre = "Dance" }.Keeps(Station(tags: ["dancehall"])));
        Assert.True(new StationSieve { Genre = "Reggae" }.Keeps(Station(tags: ["dancehall"])));
    }

    /// <summary>A tagger writing several genres into one field is the second most common case in the
    /// directory, after a single word. A rule that read only whole tags would leave every one of those
    /// stations under no genre at all.</summary>
    [Theory]
    [InlineData("club dance electronic house trance", "Dance")]
    [InlineData("club dance electronic house trance", "Electronic")]
    [InlineData("rap hiphop rnb", "Hip-Hop")]
    [InlineData("rap hiphop rnb", "R&B / Soul")]
    [InlineData("local news", "News")]
    [InlineData("chillout+lounge", "Electronic")]
    [InlineData("drum & bass", "Electronic")]
    [InlineData("alternative / indie", "Rock")]
    [InlineData("noticias y deportes", "Sports")]
    public void A_tag_holding_several_genres_is_all_of_them(string tag, string genre)
        => Assert.True(new StationSieve { Genre = genre }.Keeps(Station(tags: [tag])));

    /// <summary>The words are read as the longest run that is a format, so a run that is its own
    /// format is that format and not its parts.</summary>
    [Fact]
    public void The_longest_run_of_words_that_is_a_format_wins()
    {
        Assert.True(new StationSieve { Genre = "Classic Rock" }.Keeps(Station(tags: ["classic rock cafe"])));
        Assert.False(new StationSieve { Genre = "Rock" }.Keeps(Station(tags: ["classic rock cafe"])));

        Assert.True(new StationSieve { Genre = "Adult Contemporary" }.Keeps(Station(tags: ["80s soft rock"])));
        Assert.False(new StationSieve { Genre = "Rock" }.Keeps(Station(tags: ["80s soft rock"])));
    }

    /// <summary>The same tag is written with accents and without by two different people, and a third
    /// of the directory is not tagged in English at all.</summary>
    [Theory]
    [InlineData("música pop", "Pop")]
    [InlineData("musica pop", "Pop")]
    [InlineData("clásicos en español", "Latin")]
    [InlineData("regional mexicana", "Latin")]
    [InlineData("noticias locales", "News")]
    [InlineData("nachrichten", "News")]
    [InlineData("поп-музыка", "Pop")]
    [InlineData("разговорный", "Talk")]
    public void A_tag_is_read_whatever_it_is_written_in(string tag, string genre)
        => Assert.True(new StationSieve { Genre = genre }.Keeps(Station(tags: [tag])));

    /// <summary>The noun that a station adds to the end of its own genre. A good many write it, and none
    /// of them mean anything different by it.</summary>
    [Theory]
    [InlineData("pop music", "Pop")]
    [InlineData("Jazz Radio", "Jazz")]
    [InlineData("classical music", "Classical")]
    public void A_genre_with_the_obvious_noun_on_the_end_is_still_that_genre(string tag, string genre)
        => Assert.True(new StationSieve { Genre = genre }.Keeps(Station(tags: [tag])));

    [Fact]
    public void A_station_tagged_something_else_is_not_that_genre()
        => Assert.False(new StationSieve { Genre = "Jazz" }.Keeps(Station(tags: ["classical", "opera"])));

    [Fact]
    public void A_station_with_no_tags_at_all_is_not_claimed_for_a_genre()
        => Assert.False(new StationSieve { Genre = "Jazz" }.Keeps(Station(tags: [])));

    /// <summary>Twenty at most, because a menu is read and not searched, and the spoken formats are
    /// among them.</summary>
    [Fact]
    public void The_menu_is_twenty_kinds_of_station_at_most()
    {
        Assert.True(StationSieve.Genres.Count <= 20);
        Assert.Contains("Jazz", StationSieve.Genres);
        Assert.Contains("Talk", StationSieve.Genres);
        Assert.Contains("News", StationSieve.Genres);
        Assert.Contains("Sports", StationSieve.Genres);
        Assert.Equal(StationSieve.Genres.Count, StationSieve.Genres.Distinct().Count());
    }

    /// <summary>Every label on the menu is one a station can actually be filed under: an entry that
    /// nothing can ever match is a filter that looks broken.</summary>
    [Fact]
    public void Every_label_on_the_menu_answers_for_itself()
    {
        foreach (var genre in StationSieve.Genres)
            Assert.True(new StationSieve { Genre = genre }.Keeps(Station(tags: [genre])), genre);

        foreach (var format in StationSieve.Formats)
            Assert.True(new StationSieve { Format = format }.Keeps(Station(codec: format)), format);
    }

    [Fact]
    public void Advertising_is_asked_of_the_stations_own_word_about_itself()
    {
        var commercial = Station("Sells things", tags: ["commercial", "pop"]);
        var quiet = Station("Says nothing", tags: ["pop"]);
        var free = Station("Says otherwise", tags: ["public radio", "non-commercial"]);

        var with = new StationSieve { Ads = AdsWanted.With };
        var without = new StationSieve { Ads = AdsWanted.Without };

        Assert.True(with.Keeps(commercial));
        Assert.False(with.Keeps(free));

        Assert.True(without.Keeps(free));
        Assert.False(without.Keeps(commercial));

        // A station that said nothing is not advertising as far as anybody here knows, so it stays on
        // the ad-free side and is not hidden from both.
        Assert.True(without.Keeps(quiet));
        Assert.False(with.Keeps(quiet));
    }

    /// <summary>Each question is asked only where it was asked, and all of them have to hold.</summary>
    [Fact]
    public void The_three_questions_are_asked_together()
    {
        var sieve = new StationSieve { Format = "MP3", Genre = "Jazz", Ads = AdsWanted.Without };

        Assert.True(sieve.Keeps(Station(codec: "MP3", tags: ["jazz"])));
        Assert.False(sieve.Keeps(Station(codec: "AAC", tags: ["jazz"])));
        Assert.False(sieve.Keeps(Station(codec: "MP3", tags: ["rock"])));
        Assert.False(sieve.Keeps(Station(codec: "MP3", tags: ["jazz", "commercial"])));
    }

    [Fact]
    public void What_it_keeps_stays_in_the_order_it_arrived()
    {
        var all = new[]
        {
            Station("One", codec: "MP3"),
            Station("Two", codec: "AAC"),
            Station("Three", codec: "MP3"),
        };

        var kept = new StationSieve { Format = "MP3" }.Through(all);

        Assert.Equal(["One", "Three"], kept.Select(s => s.Name));
    }
}
