using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Which record an album is, sent to the cloud on its tracks' catalogue rows.
///
/// <para>The phone has no files to read tags from and no way to pick a pressing. If it looked up the
/// shared album entry by artist and title alone, it could get a different entry from the computer
/// that uploaded the music. These few fields make the two agree. They are album-level values,
/// written onto every row of the record, because a client that draws an album reads a catalogue
/// listing and has no album rows of its own.</para>
/// </summary>
public class CloudPressingSyncTests
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    private static LibraryTrack Track() => new()
    {
        Id = 1,
        RelativePath = "Manu Chao/Clandestino/01 Clandestino.flac",
        Folder = "Manu Chao/Clandestino",
        FileName = "01 Clandestino.flac",
        Extension = ".flac",
        RpmId = Id,
        Title = "Clandestino",
        Artist = "Manu Chao",
        Album = "Clandestino",
        DurationSeconds = 154,
        TrackNumber = 1,
        Year = 1998,
        SizeBytes = 5_000_000,
        ModifiedUtcTicks = 100,
        CloudSyncedUtcTicks = 100,
    };

    private static AlbumMeta Linked() => new(
        "Manu Chao/Clandestino", false, 0, null, 0,
        ReleaseId: "66278f7b-bcab-49b5-8c9a-fa76dcf51486",
        Catno: "7243 8457832 9",
        ReleaseProvider: "rpm",
        Barcode: "0724384578329",
        DiscId: "xd_R.uPBg8Y0Cd0KAyaGrsJZ5c-");

    private static CloudTrack Bare() => new(
        Id, "sha", "Manu Chao/Clandestino/01 Clandestino.flac", 5_000_000,
        "Clandestino", "Manu Chao", "Clandestino", 1, CatalogReconciler.TagsFor(Track()));

    [Fact]
    public void A_linked_album_puts_its_ids_on_every_row()
    {
        var tags = CatalogReconciler.TagsFor(Track(), Linked());

        Assert.Equal("7243 8457832 9", tags.Catno);
        Assert.Equal("0724384578329", tags.Barcode);
        Assert.Equal("66278f7b-bcab-49b5-8c9a-fa76dcf51486", tags.MusicBrainzReleaseId);
        Assert.Equal("xd_R.uPBg8Y0Cd0KAyaGrsJZ5c-", tags.MusicBrainzDiscId);
    }

    /// <summary>An album linked to a Discogs release has no MusicBrainz id to send, and must not send
    /// the Discogs number under MusicBrainz's name: they share no namespace.</summary>
    [Fact]
    public void A_discogs_link_does_not_travel_as_a_musicbrainz_one()
    {
        var discogs = Linked() with { ReleaseProvider = "discogs", ReleaseId = "430456" };
        var tags = CatalogReconciler.TagsFor(Track(), discogs);

        Assert.Null(tags.MusicBrainzReleaseId);
        Assert.Equal("7243 8457832 9", tags.Catno);
    }

    /// <summary>A record nobody has linked says nothing about which pressing it is, rather than
    /// sending blanks that would read as an instruction to forget.</summary>
    [Fact]
    public void An_unlinked_album_says_nothing_about_the_pressing()
    {
        var tags = CatalogReconciler.TagsFor(Track());

        Assert.Null(tags.Catno);
        Assert.Null(tags.Barcode);
        Assert.Null(tags.MusicBrainzReleaseId);
        Assert.Null(tags.MusicBrainzDiscId);
    }

    /// <summary>Linking a record on the desktop is a change the cloud must receive, or the phone keeps
    /// looking up the album wall by artist and title alone.</summary>
    [Fact]
    public void Linking_a_record_is_something_to_push()
    {
        var push = CatalogReconciler.Differs(Track(), Bare(), Linked());

        Assert.False(push.IsEmpty);
        Assert.Equal("xd_R.uPBg8Y0Cd0KAyaGrsJZ5c-", push.Tags!.MusicBrainzDiscId);
    }

    /// <summary>Once the cloud has it, there is nothing left to send. A field that pushed on every pass
    /// would push for ever.</summary>
    [Fact]
    public void An_album_the_cloud_already_agrees_about_is_not_pushed_again()
    {
        var agreed = Bare() with { Tags = CatalogReconciler.TagsFor(Track(), Linked()) };

        Assert.True(CatalogReconciler.Differs(Track(), agreed, Linked()).IsEmpty);
    }

    /// <summary>A library that has not linked this record leaves the cloud's value alone. Blank on
    /// this side means "not known", never "erase it", as for every other catalogue field. This stops
    /// one machine that has never opened an album from unlinking it for the others.</summary>
    [Fact]
    public void Knowing_nothing_does_not_unlink_what_the_cloud_knows()
    {
        var cloudKnows = Bare() with { Tags = CatalogReconciler.TagsFor(Track(), Linked()) };

        Assert.True(CatalogReconciler.Differs(Track(), cloudKnows).IsEmpty);
    }

    /// <summary>
    /// Two installs that have linked one record to two different pressings leave each other alone.
    ///
    /// <para>Nothing pulls the pressing fields: CloudSyncPlan neither compares nor applies them,
    /// because they live on the album row. So a disagreement can never settle. If each machine read
    /// the other's value as a difference and pushed its own, it would push one row per track on every
    /// run, for ever.</para>
    /// </summary>
    [Fact]
    public void A_record_the_cloud_has_linked_to_another_pressing_is_left_alone()
    {
        var theirs = Linked() with
        {
            Catno = "506.2123-2",
            Barcode = "0028947653219",
            ReleaseId = "9c8a1f2e-0000-0000-0000-000000000000",
            DiscId = "someOtherDiscId-0000000000",
        };
        var cloud = Bare() with { Tags = CatalogReconciler.TagsFor(Track(), theirs) };

        Assert.True(CatalogReconciler.Differs(Track(), cloud, Linked()).IsEmpty);
    }

    /// <summary>A store that has no pressing at all still gets one. Leaving a different value alone is
    /// not the same as never sending one: these fields exist for a phone that has nothing else to
    /// match the album on.</summary>
    [Fact]
    public void A_record_the_cloud_knows_nothing_about_is_still_linked()
    {
        var push = CatalogReconciler.Differs(Track(), Bare(), Linked());

        Assert.False(push.IsEmpty);
        Assert.Equal("7243 8457832 9", push.Tags!.Catno);
    }

    /// <summary>Field by field, so a store that has the catalogue number but not the barcode is told
    /// the barcode. The pressing is four separate values, not one.</summary>
    [Fact]
    public void A_pressing_the_cloud_half_knows_is_filled_in()
    {
        var half = Linked() with { Barcode = null, DiscId = null };
        var cloud = Bare() with { Tags = CatalogReconciler.TagsFor(Track(), half) };

        var push = CatalogReconciler.Differs(Track(), cloud, Linked());

        Assert.Equal("0724384578329", push.Tags!.Barcode);
    }

    /// <summary>The plan reads each row's album by its folder, which is how the pressing reaches the
    /// rows in the first place.</summary>
    [Fact]
    public void The_plan_finds_each_rows_album_by_its_folder()
    {
        var albums = new Dictionary<string, AlbumMeta>(StringComparer.OrdinalIgnoreCase)
        {
            ["Manu Chao/Clandestino"] = Linked(),
        };

        var decision = CloudSyncPlan.Make([Track()], [Bare()], albums: albums);

        var push = Assert.Single(decision.Push);
        Assert.Equal("0724384578329", push.Tags!.Barcode);
    }
}
