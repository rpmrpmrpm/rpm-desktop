using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// What a card says a record is, before anything has read a tag.
///
/// <para>The library is laid out Artist/Album/track, so the folder itself names the record. That
/// costs nothing to read and lets a wall of two thousand covers fill at once. But it is a poor name
/// that cannot be fixed: the path was sanitised on import, so a band with a slash in its name is
/// filed under a different spelling, and a record retagged in place keeps the folder it was imported
/// into. So a card is renamed from the mirror once the real tags are known, and both names must be
/// right.</para>
/// </summary>
public class AlbumCardNamingTests
{
    private static (string Title, string Artist) Name(
        string folder, string? album = null, string? albumArtist = null, string libraryName = "My Music")
        => AlbumCardName.For(folder, libraryName, album, albumArtist);

    [Fact]
    public void The_folders_shape_names_the_record_and_the_band()
        => Assert.Equal(("Remain in Light", "Talking Heads"), Name("Talking Heads/Remain in Light"));

    /// <summary>Loose tracks at the top of the library have no artist folder above them, and a card
    /// titled "" would be a blank tile.</summary>
    [Fact]
    public void Tracks_loose_in_the_library_root_are_named_after_the_library()
        => Assert.Equal(("My Music", ""), Name("", libraryName: "My Music"));

    /// <summary>A record filed directly under the root (no artist folder) is still a record.</summary>
    [Fact]
    public void A_folder_with_nothing_above_it_names_the_record_and_no_band()
        => Assert.Equal(("Unsorted Rips", ""), Name("Unsorted Rips"));

    /// <summary>Deep trees are possible, for example a library filed by genre or by decade. The last
    /// two segments are the record and its artist, whatever is above them.</summary>
    [Fact]
    public void The_last_two_segments_are_the_record_wherever_it_sits()
        => Assert.Equal(("Kind of Blue", "Miles Davis"), Name("Jazz/1959/Miles Davis/Kind of Blue"));

    /// <summary>A path that arrives with a trailing or doubled separator is the same path.</summary>
    [Theory]
    [InlineData("Talking Heads/Remain in Light/")]
    [InlineData("/Talking Heads/Remain in Light")]
    [InlineData("Talking Heads//Remain in Light")]
    public void Stray_separators_do_not_shift_which_segment_is_which(string folder)
        => Assert.Equal(("Remain in Light", "Talking Heads"), Name(folder));

    /// <summary>The path's spelling is fixed at import and sanitised; the tag is the band's actual
    /// name.</summary>
    [Fact]
    public void Real_tags_replace_the_name_the_path_gave()
        => Assert.Equal(
            ("Highway to Hell", "AC/DC"),
            Name("AC-DC/Highway to Hell", album: "Highway to Hell", albumArtist: "AC/DC"));

    /// <summary>A partial tag is still used: an album with no album artist tag keeps the artist from
    /// the folder instead of losing the line entirely.</summary>
    [Fact]
    public void A_tag_that_says_nothing_leaves_the_path_to_answer()
        => Assert.Equal(
            ("Remain in Light (Deluxe)", "Talking Heads"),
            Name("Talking Heads/Remain in Light", album: "Remain in Light (Deluxe)"));

    /// <summary>A card is reused across rebuilds, so a rebuild that learns nothing new must leave it
    /// reading exactly as it did.</summary>
    [Fact]
    public void Naming_a_card_from_nothing_leaves_it_as_the_path_had_it()
        => Assert.Equal(("Remain in Light", "Talking Heads"), Name("Talking Heads/Remain in Light"));

    /// <summary>An empty tag is still a tag: a record whose album artist was deliberately cleared shows
    /// none, instead of falling back to a folder that may name the wrong band.</summary>
    [Fact]
    public void A_tag_that_is_empty_is_an_answer_rather_than_a_gap()
        => Assert.Equal(("", ""), Name("Talking Heads/Remain in Light", album: "", albumArtist: ""));
}
