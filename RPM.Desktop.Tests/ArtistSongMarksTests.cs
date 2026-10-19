using System.ComponentModel;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.Artists.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// The two marks a song shows on an artist's page: the heart at the end of the line and the equaliser
/// in front of the name.
///
/// <para>The listing under All tracks must let a song be marked as a favourite and must show when it is
/// playing, like every other list in the app. If the lines were built once from the mirror and never
/// updated, a mark made in the drawer above them, or a song starting while the page is open, would leave
/// two lists about one collection disagreeing with each other.</para>
///
/// <para>These tests check the line's own contract: it takes what the mirror already knows when it is
/// built, it accepts a later mark, and it raises the change so the row redraws. The rule for showing the
/// heart is tested too (always on a favourite, on hover for the rest), because the hover rule exists to
/// stop a heart being drawn on each of four hundred lines.</para>
/// </summary>
public class ArtistSongMarksTests
{
    private static ArtistSong Song(bool favorite = false) =>
        new(new TrackInfo
            {
                FilePath = "/music/ABBA/Arrival/01 Dancing Queen.flac",
                Extension = ".flac",
                Title = "Dancing Queen",
                Artist = "ABBA",
                IsFavorite = favorite,
            },
            folder: "ABBA/Arrival",
            subtitle: "Arrival, ABBA",
            duration: "3:51");

    /// <summary>The mirror's value is on the line before it is shown: a favourite opens filled in,
    /// rather than filling in a moment later once something else has asked the library.</summary>
    [Fact]
    public void A_line_opens_wearing_what_the_mirror_already_knew()
    {
        Assert.True(Song(favorite: true).IsFavorite);
        Assert.False(Song(favorite: true).IsNotFavorite);
        Assert.False(Song().IsFavorite);
        Assert.True(Song().IsNotFavorite);
    }

    /// <summary>A favourite carries its heart with no pointer anywhere near it; an unmarked song shows one
    /// only while the pointer is on its line.</summary>
    [Fact]
    public void The_heart_is_drawn_on_a_favourite_always_and_on_the_rest_only_under_the_pointer()
    {
        var loved = Song(favorite: true);
        Assert.True(loved.ShowHeart);

        var plain = Song();
        Assert.False(plain.ShowHeart);

        plain.IsHovered = true;
        Assert.True(plain.ShowHeart);

        plain.IsHovered = false;
        Assert.False(plain.ShowHeart);
    }

    /// <summary>Marking it fills the heart in at once and raises the change, or the row would be drawn
    /// from the value it had before the press.</summary>
    [Fact]
    public void A_mark_made_now_is_answered_now_and_announced()
    {
        var song = Song();
        var said = Heard(song);

        song.IsFavorite = true;

        Assert.True(song.IsFavorite);
        Assert.False(song.IsNotFavorite);
        Assert.True(song.ShowHeart);
        Assert.Contains(nameof(ArtistSong.IsFavorite), said);
        Assert.Contains(nameof(ArtistSong.IsNotFavorite), said);
        Assert.Contains(nameof(ArtistSong.ShowHeart), said);
    }

    /// <summary>The same mark twice is not a change: a page told what it already knows must not redraw
    /// the line for it.</summary>
    [Fact]
    public void The_same_mark_again_says_nothing()
    {
        var song = Song(favorite: true);
        var said = Heard(song);

        song.IsFavorite = true;

        Assert.Empty(said);
    }

    /// <summary>The now-playing mark from the deck, on and off the line.</summary>
    [Fact]
    public void The_equaliser_follows_the_deck()
    {
        var song = Song();
        var said = Heard(song);

        song.IsPlaying = true;
        Assert.True(song.IsPlaying);
        Assert.Contains(nameof(ArtistSong.IsPlaying), said);

        song.IsPlaying = false;
        Assert.False(song.IsPlaying);
    }

    /// <summary>
    /// Somebody else's song is offered a heart on the same terms as one of this library's own.
    ///
    /// <para>A borrowed song's favourite is stored as a row in the mirror held for that computer, and
    /// the mark on it is the listener's own: what they think of a record on a friend's disk. The song's
    /// right-click menu offers the same entry, and the two must agree.</para>
    /// </summary>
    [Fact]
    public void A_borrowed_song_is_offered_a_heart_like_any_other()
    {
        var borrowed = Song();
        Assert.False(borrowed.ShowHeart);

        borrowed.IsHovered = true;
        Assert.True(borrowed.ShowHeart);

        var marked = Song(favorite: true);
        Assert.True(marked.ShowHeart);
    }

    /// <summary>The line names the file it stands for, which is how a mark made in any other view finds
    /// it. Without this, the page would have to be rebuilt for every heart.</summary>
    [Fact]
    public void A_line_names_the_file_it_stands_for()
    {
        Assert.Equal("/music/ABBA/Arrival/01 Dancing Queen.flac", Song().FilePath);
    }

    private static List<string> Heard(INotifyPropertyChanged song)
    {
        var said = new List<string>();
        song.PropertyChanged += (_, e) => said.Add(e.PropertyName ?? "");
        return said;
    }
}
