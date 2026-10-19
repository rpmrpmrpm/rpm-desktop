using RPM.Core.Analysis;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class DiscInferenceTests
{
    private static TrackInfo Track(string path, string album, uint track,
        string artist = "X", uint disc = 0, uint discCount = 0)
        => new()
        {
            FilePath = path,
            Extension = ".flac",
            Artist = artist,
            AlbumArtist = artist,
            Album = album,
            Title = System.IO.Path.GetFileNameWithoutExtension(path),
            TrackNumber = track,
            DiscNumber = disc,
            DiscCount = discCount,
        };

    private static (uint Disc, uint Count)[] Fill(params TrackInfo[] tracks)
        => DiscInference.Fill(tracks).Select(t => (t.DiscNumber, t.DiscCount)).ToArray();

    [Fact]
    public void Cd_folders_number_the_discs_and_count_them()
    {
        Assert.Equal(
            [(1u, 2u), (2u, 2u)],
            Fill(Track("/m/Set/CD 1/01.flac", "Set", 1),
                 Track("/m/Set/CD 2/01.flac", "Set", 1)));
    }

    [Fact]
    public void The_disc_is_read_from_the_deepest_segment_that_names_one()
    {
        // "Box 2" is the box, not the disc: the folder nearest the file wins.
        Assert.Equal(3u, DiscInference.DiscFromPath("/m/Disc 2 Box/CD 3/01.flac"));
        Assert.Equal(1u, DiscInference.DiscFromPath("/m/Album (CD1)/01.flac"));
        Assert.Equal(0u, DiscInference.DiscFromPath("/m/Album/01.flac"));
    }

    [Fact]
    public void A_tagged_disc_is_never_overwritten_but_the_count_is_completed()
    {
        // Disc 1 is tagged, disc 2 is not; both come out numbered, and both learn the count.
        Assert.Equal(
            [(1u, 2u), (2u, 2u)],
            Fill(Track("/m/Set/CD 1/01.flac", "Set", 1, disc: 1),
                 Track("/m/Set/CD 2/01.flac", "Set", 1)));
    }

    [Fact]
    public void Where_the_tag_and_the_folder_disagree_the_tag_wins()
    {
        // A file in the wrong folder is common; a file with a wrong disc tag is not.
        Assert.Equal(
            [(4u, 4u), (2u, 4u)],
            Fill(Track("/m/Set/CD 1/01.flac", "Set", 1, disc: 4),
                 Track("/m/Set/CD 2/01.flac", "Set", 1)));
    }

    [Fact]
    public void A_bigger_disc_count_already_on_the_file_is_left_alone()
    {
        // Two discs of a three-disc set: the file's tag is more reliable than this import.
        Assert.Equal(
            [(1u, 3u), (2u, 3u)],
            Fill(Track("/m/Set/CD 1/01.flac", "Set", 1, disc: 1, discCount: 3),
                 Track("/m/Set/CD 2/01.flac", "Set", 1, disc: 2, discCount: 3)));
    }

    [Fact]
    public void Two_folders_that_never_say_disc_are_two_rips_not_two_discs()
    {
        // The dangerous case: repeated track numbers alone can also mean a record imported twice.
        Assert.Equal(
            [(0u, 0u), (0u, 0u)],
            Fill(Track("/m/rip-a/01.flac", "Album", 1),
                 Track("/m/rip-b/01.flac", "Album", 1)));
    }

    [Fact]
    public void One_disc_folder_on_its_own_is_an_ordinary_album()
    {
        Assert.Equal(
            [(0u, 0u), (0u, 0u)],
            Fill(Track("/m/Album/CD 1/01.flac", "Album", 1),
                 Track("/m/Album/CD 1/02.flac", "Album", 2)));
    }

    [Fact]
    public void Two_different_records_that_share_a_disc_layout_stay_apart()
    {
        Assert.Equal(
            [(1u, 2u), (2u, 2u), (0u, 0u)],
            Fill(Track("/m/Set/CD 1/01.flac", "Set", 1),
                 Track("/m/Set/CD 2/01.flac", "Set", 1),
                 Track("/m/Other/CD 1/01.flac", "Other Record", 1)));
    }

    [Fact]
    public void An_inferred_disc_is_marked_edited_so_the_import_writes_it()
    {
        // A set whose files have good tags is otherwise moved without a write, and the inferred
        // numbers go into the file names, never into the files.
        var filled = DiscInference.Fill([
            Track("/m/Set/CD 1/01.flac", "Set", 1),
            Track("/m/Set/CD 2/01.flac", "Set", 1),
            Track("/m/Other/CD 1/01.flac", "Other Record", 1),
        ]);

        Assert.True(filled[0].TagsEdited);
        Assert.True(filled[1].TagsEdited);
        Assert.False(filled[2].TagsEdited);   // nothing was inferred for it
    }
}
