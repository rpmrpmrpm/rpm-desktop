using RPM.Core.Analysis;
using RPM.Core.Models;
using RPM.Core.Import;

namespace RPM.Core.Tests;

/// <summary>Filing a multi-disc set at import: merged puts the discs in one folder, separate names them apart.</summary>
public class MultiDiscImportTests
{
    private static TrackInfo Track(uint disc, uint track, string album = "The Wall", string? subtitle = null) => new()
    {
        FilePath = $"/staging/{disc}-{track}.flac",
        Extension = ".flac",
        Artist = "Pink Floyd",
        AlbumArtist = "Pink Floyd",
        Album = album,
        Title = $"t{track}",
        TrackNumber = track,
        DiscNumber = disc,
        DiscCount = 2,
        DiscSubtitle = subtitle,
    };

    // ── merged-mode album naming: undoing a tagger that split the set ──

    /// <summary>A set tagged "The Wall (1)" and "The Wall (2)", disc 1 and 2 of 2. Without the fix
    /// these are two album names, so nothing merges and a two-disc record is filed as two
    /// folders.</summary>
    [Theory]
    [InlineData("The Wall (1)", 1u)]
    [InlineData("The Wall (2)", 2u)]
    [InlineData("The Wall (Disc 1)", 1u)]
    [InlineData("The Wall [Disc 2]", 2u)]
    [InlineData("The Wall (CD1)", 1u)]
    [InlineData("The Wall - Disc 2", 2u)]
    [InlineData("The Wall: Disk 1", 1u)]
    [InlineData("The Wall CD 2", 2u)]
    [InlineData("The Wall  (  disc  2  )", 2u)]
    public void A_disc_marker_naming_this_discs_own_number_comes_off(string album, uint disc)
        => Assert.Equal("The Wall", MultiDiscImport.MergedAlbumName(album, disc, discCount: 2));

    [Fact]
    public void A_name_with_no_marker_is_left_exactly_as_it_is()
        => Assert.Equal("The Wall", MultiDiscImport.MergedAlbumName("The Wall", 2, 2));

    /// <summary>Stops an album that simply ends in a number from being renamed. The marker must name
    /// THIS track's disc; anything else is part of the title.</summary>
    [Theory]
    [InlineData("Chapter 2", 1u)]
    [InlineData("Volume 1", 2u)]
    [InlineData("Disintegration (3)", 1u)]
    public void A_number_that_is_not_this_discs_number_stays(string album, uint disc)
        => Assert.Equal(album, MultiDiscImport.MergedAlbumName(album, disc, discCount: 2));

    /// <summary>The other guard. A single disc is not a set, so nothing in its name is a disc
    /// marker: "Volume 1" is the record's name.</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void A_release_that_is_not_a_set_keeps_its_name(uint discCount)
        => Assert.Equal("Volume 1", MultiDiscImport.MergedAlbumName("Volume 1", 1, discCount));

    [Fact]
    public void A_track_on_no_numbered_disc_keeps_its_name()
        => Assert.Equal("The Wall (1)", MultiDiscImport.MergedAlbumName("The Wall (1)", disc: 0, discCount: 2));

    /// <summary>A bare number with no brackets and no word is not a marker. Nothing separates it from
    /// a title, and getting this wrong renames a record instead of merging one.</summary>
    [Fact]
    public void A_bare_trailing_number_is_a_title_and_not_a_marker()
        => Assert.Equal("Woodstock 2", MultiDiscImport.MergedAlbumName("Woodstock 2", 2, 2));

    /// <summary>Only the end of the name. "Disc Two of the Mountain" is a title.</summary>
    [Fact]
    public void A_marker_in_the_middle_of_a_name_is_not_a_marker()
        => Assert.Equal("Disc 1 of the Mountain",
                        MultiDiscImport.MergedAlbumName("Disc 1 of the Mountain", 1, 2));

    /// <summary>Never return an empty name: an empty album files a track loose under the artist,
    /// which is worse than the odd name it came with.</summary>
    [Fact]
    public void A_name_that_is_nothing_but_a_marker_is_kept()
        => Assert.Equal("(Disc 1)", MultiDiscImport.MergedAlbumName("(Disc 1)", 1, 2));

    /// <summary>What makes the merge work: both discs of the set return the same name, which is the
    /// key all later steps group on.</summary>
    [Fact]
    public void Both_discs_of_a_split_set_come_back_with_one_name()
    {
        var one = MultiDiscImport.MergedAlbumName("The Wall (1)", 1, 2);
        var two = MultiDiscImport.MergedAlbumName("The Wall (2)", 2, 2);

        Assert.Equal(one, two);
        Assert.Equal("The Wall", one);
    }

    // ── separate-mode album naming ──

    [Fact]
    public void Separate_mode_decorates_the_album_with_the_disc_number()
        => Assert.Equal("The Wall (Disc 2)", MultiDiscImport.SeparateAlbumName("The Wall", 2, null));

    [Fact]
    public void Separate_mode_prefers_a_disc_subtitle_when_there_is_one()
        => Assert.Equal("Sign o' the Times: The Hits", MultiDiscImport.SeparateAlbumName("Sign o' the Times", 2, "The Hits"));

    // ── merged-mode folder override (RenamePlanner) ──

    [Fact]
    public void The_folder_override_files_a_disc_into_the_existing_album_folder()
    {
        var template = NamingTemplate.ArtistAlbumNumberedTitle;   // {albumartist}/{album}/{track:00} - {title}
        var track = Track(2, 1);

        // The album is already here (imported earlier); the new disc must go next to it.
        string existing = "Pink Floyd/The Wall";
        var plan = RenamePlanner.Plan([track], template, "/library", _ => existing);

        // The folder is forced to the existing one; the template still names the file.
        Assert.Equal("Pink Floyd/The Wall/01 - t1.flac", Assert.Single(plan).ProposedRelativePath);
    }

    [Fact]
    public void No_override_leaves_the_template_folder_alone()
    {
        var plan = RenamePlanner.Plan([Track(2, 1)], NamingTemplate.ArtistAlbumNumberedTitle, "/library", _ => null);
        Assert.Equal("Pink Floyd/The Wall/01 - t1.flac", Assert.Single(plan).ProposedRelativePath);
    }

    [Fact]
    public void The_override_can_send_two_discs_into_one_folder_without_colliding()
    {
        var template = NamingTemplate.ArtistAlbumNumberedTitle;
        var tracks = new[] { Track(1, 1), Track(2, 1) };   // both are track 1, different discs
        var plan = RenamePlanner.Plan(tracks, template, "/library", _ => "Pink Floyd/The Wall");

        // Same folder, and the collision resolver keeps the second file distinct.
        Assert.All(plan, s => Assert.StartsWith("Pink Floyd/The Wall/", s.ProposedRelativePath));
        Assert.Equal(2, plan.Select(s => s.ProposedRelativePath).Distinct().Count());
    }

    // ── a set whose tagger split it, filed end to end ──

    /// <summary>
    /// The whole case, from the tags as they arrive to the paths that get written.
    ///
    /// <para>A two-disc Pink Floyd tagged "The Wall (1)" and "The Wall (2)", disc 1 and 2 of 2, with
    /// the multi-disc setting on Merged. Every later step uses the album name as its key, so without
    /// the name repair the two discs are two records and are filed as two folders. This runs the
    /// import's own preparation in order (the merged name, the set, the continuing numbers, the
    /// planner) and checks the paths.</para>
    /// </summary>
    [Fact]
    public void A_set_the_tagger_split_by_name_is_filed_as_one_record()
    {
        var template = NamingTemplate.ArtistAlbumNumberedTitle;

        // As tagged: thirteen on each disc, and the disc written into the album name.
        var tagged = new List<TrackInfo>();
        for (uint n = 1; n <= 13; n++) tagged.Add(Track(1, n, album: "The Wall (1)"));
        for (uint n = 1; n <= 13; n++) tagged.Add(Track(2, n, album: "The Wall (2)"));

        // Step one of the import's preparation: the name repair.
        var tracks = tagged
            .Select(t => t with
            {
                Album = MultiDiscImport.MergedAlbumName(t.Album!, t.DiscNumber, t.DiscCount),
            })
            .ToList();

        // Both discs are now one record, so the set groups and disc 2 numbers continue from disc 1.
        var set = tracks.Where(t => t.Album == "The Wall").ToList();
        Assert.Equal(26, set.Count);

        var plan = RenamePlanner.Plan(
            tracks, template, "/library",
            albumFolderOverride: _ => null,
            namingView: t => MultiDiscImport.NamedInMergedAlbum(t, set, template));

        var folders = plan
            .Select(s => s.ProposedRelativePath[..s.ProposedRelativePath.LastIndexOf('/')])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // One folder, not "The Wall (1)" and "The Wall (2)".
        Assert.Equal(["Pink Floyd/The Wall"], folders);

        // And twenty-six distinct files in it: disc 2 runs 14 to 26 instead of starting again at 1.
        Assert.Equal(26, plan.Select(s => s.ProposedRelativePath).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("Pink Floyd/The Wall/14 - t1.flac", plan.Select(s => s.ProposedRelativePath));
    }
}
