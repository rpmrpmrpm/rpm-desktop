using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What a record's name says it is. Both readings shelve records on an artist's page, so what is being
/// tested is mostly the records that must NOT move: a filter's real failure is the one it files wrongly,
/// not the one it misses.
/// </summary>
public class AlbumKindTests
{
    [Theory]
    [InlineData("Live at the Apollo")]
    [InlineData("Live At Wembley Stadium")]
    [InlineData("Unplugged (Live)")]
    [InlineData("MTV Unplugged [Live]")]
    [InlineData("Rattle and Hum - Live")]
    [InlineData("Live 1975")]
    [InlineData("LIVE!")]
    [InlineData("Live")]
    public void A_night_in_front_of_an_audience(string title) => Assert.True(AlbumKind.IsLive(title));

    /// <summary>The show's name is the whole tag, and the show is a set in front of an audience.
    /// Rod Stewart's singular box of studio recordings is not one of them.</summary>
    [Theory]
    [InlineData("VH1 Storytellers", true)]
    [InlineData("Storytellers", true)]
    [InlineData("Storyteller", false)]
    [InlineData("Storyteller: The Complete Anthology", false)]
    public void A_set_with_the_talking_left_in(string title, bool live)
        => Assert.Equal(live, AlbumKind.IsLive(title));

    /// <summary>The format says the same thing whether or not the tag spells it out, so the word is
    /// read on its own.</summary>
    [Theory]
    [InlineData("MTV Unplugged")]
    [InlineData("Unplugged in New York")]
    [InlineData("Unplugged")]
    public void A_night_that_only_says_unplugged(string title) => Assert.True(AlbumKind.IsLive(title));

    /// <summary>A record sold as sessions was played through in a room, so it shelves with the
    /// concerts rather than with the studio albums.</summary>
    [Theory]
    [InlineData("The Abbey Road Sessions")]
    [InlineData("The Complete Peel Sessions")]
    [InlineData("Daytrotter Sessions")]
    [InlineData("Sessions@AOL")]
    public void A_record_played_through_in_a_room(string title) => Assert.True(AlbumKind.IsLive(title));

    /// <summary>The plural is the word a release is named with; the singular is not read, and neither
    /// is a longer word that happens to hold it.</summary>
    [Theory]
    [InlineData("Session")]
    [InlineData("Jam Session")]
    [InlineData("Obsessions")]
    [InlineData("Possessions")]
    public void The_singular_and_the_words_that_hold_it_are_not_read(string title)
        => Assert.False(AlbumKind.IsLive(title));

    /// <summary>The word is inside a great many others, and every one of these is an ordinary record.</summary>
    [Theory]
    [InlineData("Alive")]
    [InlineData("Stayin' Alive")]
    [InlineData("Olive")]
    [InlineData("Living Things")]
    [InlineData("Delivered")]
    [InlineData("Livery Stable Blues")]
    [InlineData("")]
    [InlineData(null)]
    public void A_word_that_merely_holds_the_letters_is_not_one(string? title)
        => Assert.False(AlbumKind.IsLive(title));

    /// <summary>A known cost, tested on purpose: a studio record whose name starts with the word is
    /// filed as a live one. This is the price of the rule.</summary>
    [Theory]
    [InlineData("Live Through This")]
    [InlineData("Live Forever")]
    public void A_studio_record_named_for_the_word_is_caught_too(string title)
        => Assert.True(AlbumKind.IsLive(title));

    [Theory]
    [InlineData("Runaway - Single")]
    [InlineData("Runaway - single")]
    [InlineData("Flashing Lights – Single")]
    [InlineData("Flashing Lights — Single")]
    [InlineData("Stronger -Single")]
    [InlineData("Best Of - Singles")]
    public void One_song_released_on_its_own(string title) => Assert.True(AlbumKind.IsSingle(title));

    /// <summary>The dash is part of the rule: without it the word is an ordinary one, and two well-known
    /// records are named with it.</summary>
    [Theory]
    [InlineData("Single Ladies")]
    [InlineData("Singles")]
    [InlineData("Singles Soundtrack")]
    [InlineData("Single File")]
    [InlineData("")]
    [InlineData(null)]
    public void The_word_without_the_dash_is_an_ordinary_title(string? title)
        => Assert.False(AlbumKind.IsSingle(title));

    [Theory]
    [InlineData("The Essential Michael Jackson")]
    [InlineData("Greatest Hits")]
    [InlineData("The Very Best of Prince")]
    [InlineData("Best Of Both Worlds")]
    [InlineData("The Collection")]
    [InlineData("Singles Collection")]
    [InlineData("Anthology 1")]
    [InlineData("The Definitive Rod Stewart")]
    [InlineData("Hits")]
    [InlineData("Essentials")]
    public void A_gathering_up_rather_than_a_record(string title)
        => Assert.True(AlbumKind.IsCompilation(title));

    /// <summary>Every entry has to be a whole word or a whole phrase. These hold the letters of one and
    /// are ordinary records.</summary>
    [Theory]
    [InlineData("Recollection")]
    [InlineData("Collective Soul")]
    [InlineData("Best Coast")]
    [InlineData("Graduation")]
    [InlineData("Hitsville")]
    [InlineData("")]
    [InlineData(null)]
    public void A_title_that_merely_holds_the_letters_is_not_one(string? title)
        => Assert.False(AlbumKind.IsCompilation(title));

    /// <summary>A known cost, tested on purpose: a compilation named like an album is filed with the
    /// albums, because no list of phrases can tell them apart.</summary>
    [Theory]
    [InlineData("Legend")]
    [InlineData("Hot Rocks")]
    public void A_compilation_that_does_not_say_so_is_missed(string title)
        => Assert.False(AlbumKind.IsCompilation(title));

    /// <summary>Music written for something being watched, off the name it is sold under.</summary>
    [Theory]
    [InlineData("Purple Rain (Original Soundtrack)")]
    [InlineData("Guardians of the Galaxy: Awesome Mix Vol. 1 - Soundtrack")]
    [InlineData("The Dark Knight - Original Score")]
    [InlineData("Blade Runner (Film Score)")]
    [InlineData("Music from the Motion Picture Pulp Fiction")]
    [InlineData("Music from the HBO Original Series Game of Thrones")]
    [InlineData("Music from Baz Luhrmann's Film The Great Gatsby")]
    [InlineData("Hamilton (Original Broadway Cast Recording)")]
    [InlineData("Final Fantasy VII OST")]
    public void Music_written_for_something_being_watched(string title)
        => Assert.True(AlbumKind.IsSoundtrack(title));

    /// <summary>A known cost, tested on purpose: a studio record named "Music from" somewhere is filed
    /// as a soundtrack. The phrase finds many real soundtracks, so this error is accepted.</summary>
    [Fact]
    public void A_studio_record_named_music_from_somewhere_is_caught_too()
        => Assert.True(AlbumKind.IsSoundtrack("Music from Big Pink"));

    /// <summary>"Score" is an ordinary word until something says which kind, and a soundtrack named
    /// for its film alone says nothing at all. Both stay on the album shelf.</summary>
    [Theory]
    [InlineData("The Score")]
    [InlineData("Settle the Score")]
    [InlineData("Purple Rain")]
    [InlineData("Saturday Night Fever")]
    [InlineData("Ghostwriter")]
    [InlineData("Music for the Masses")]
    [InlineData("")]
    [InlineData(null)]
    public void A_record_that_does_not_say_it_is_one(string? title)
        => Assert.False(AlbumKind.IsSoundtrack(title));

    /// <summary>The two questions are separate, and a title answers each on its own.</summary>
    [Fact]
    public void A_record_can_be_neither_and_can_be_both()
    {
        Assert.False(AlbumKind.IsLive("Graduation"));
        Assert.False(AlbumKind.IsSingle("Graduation"));

        Assert.True(AlbumKind.IsLive("Live at Abbey Road - Single"));
        Assert.True(AlbumKind.IsSingle("Live at Abbey Road - Single"));
    }
}
