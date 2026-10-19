using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// A pass over an unchanged shelf changes nothing, and a pass over a changed one changes exactly what
/// changed.
///
/// <para><b>What this really protects.</b> The mirror compares the row it holds against the row the
/// listing has, and it reads the held row through a narrow projection
/// (<c>LibraryDatabase.Compared</c>) instead of fetching every column of every track. That matters a
/// lot: reading the whole row asks the reader for a column BY NAME fifty-five times per row, inside
/// the write gate that every other write in the app waits behind, and can block the UI thread for
/// seconds.</para>
///
/// <para>The risk it adds is silent. A field added to <see cref="Cloud.CloudLibraryMirror.Same"/>
/// whose column is not added to the projection leaves a null in the held row where the listing has a
/// value. The comparison then reports a difference on every pass, five minutes apart, for ever: a
/// borrowed shelf rewriting and redrawing itself when nothing has changed. From the outside this does
/// not look like a bug, which is why it is tested here and not left to review.</para>
///
/// <para>So: every field a peer sends is set to something distinctive, and the second pass must find
/// nothing to do. Then each compared field is changed on its own and must be noticed. A field left at
/// its default reads back the same with or without its column, so the test data is itself checked: a
/// field added to <see cref="PeerTrack"/> fails <see cref="Every_field_a_peer_sends_has_a_value_in_the_listing"/>
/// until it is given a value here.</para>
/// </summary>
public class PeerMirrorProjectionTests : IDisposable
{
    private const string Friend = "FRIEND-PROJECTION";

    private readonly string _root;
    private readonly LibraryDatabase _shelf;

    public PeerMirrorProjectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"peerproj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _shelf = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _shelf.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp directory is not worth failing over */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A track with every compared field set to a value that could not appear by chance.</summary>
    private static PeerTrack Full(string rpmId = "pid-1") => new()
    {
        RpmId = rpmId,
        RelativePath = "Ensemble/The Box/01 Allegro.flac",
        Title = "Allegro con brio",
        Artist = "Ensemble",
        AlbumArtist = "Ensemble Collective",
        Album = "The Box",
        Genre = "Classical",
        TrackNumber = 7,
        DiscNumber = 2,
        DiscCount = 3,
        Work = "Symphony No. 5",
        MovementName = "Allegro con brio",
        MovementNumber = 1,
        Composer = "Beethoven",
        Year = 1808,
        SizeBytes = 41_234_567,
        DurationSeconds = 447.5,
        ContentHash = "hash-of-the-thing",
        DiscSubtitle = "Disc two",
        AddedUtcTicks = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
        Channels = 2,
        SampleRate = 96_000,
        BitsPerSample = 24,
        BitrateKbps = 2_304,
        CodecDescription = "FLAC",
        LoudnessLufs = -14.2,
        TruePeak = -0.8,
        Advisory = (int)ContentAdvisory.Explicit,
        CloudOnly = true,
        InCloud = true,
    };

    // The same arguments SharingService.PullAsync passes for one of the user's own computers, so the
    // cloud state is compared on every pass here as it is there.
    private PeerMirrorResult Pass(params PeerTrack[] listing) =>
        _shelf.MirrorPeerLibrary(
            Friend,
            PeerLibraryMirror.ToCatalogRows(listing),
            prune: true,
            cloudOnly: PeerLibraryMirror.CloudOnlyIds(listing));

    [Fact]
    public void Every_field_a_peer_sends_has_a_value_in_the_listing()
    {
        var full = Full();
        var unset = typeof(PeerTrack).GetProperties()
            .Where(p => Equals(p.GetValue(full), p.PropertyType.IsValueType ? Activator.CreateInstance(p.PropertyType) : null))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            unset.Count == 0,
            $"Full() leaves {string.Join(", ", unset)} at the default, so the passes below cannot tell " +
            "whether LibraryDatabase.Compared reads it. Give it a value.");
    }

    [Fact]
    public void A_second_pass_over_an_unchanged_shelf_finds_nothing_to_do()
    {
        Assert.Equal(1, Pass(Full()).Adopted);

        var again = Pass(Full());

        Assert.Equal(0, again.Adopted);
        Assert.Equal(0, again.Updated);
        Assert.Equal(0, again.Forgotten);
    }

    [Fact]
    public void Ten_passes_over_an_unchanged_shelf_still_find_nothing()
    {
        // The failure here is not a wrong answer but a repeated one: a shelf that reports a hundred
        // rows changed every five minutes for as long as it is borrowed.
        Pass(Full());

        for (var pass = 0; pass < 10; pass++)
            Assert.Equal(0, Pass(Full()).Updated);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("artist")]
    [InlineData("album artist")]
    [InlineData("album")]
    [InlineData("genre")]
    [InlineData("track number")]
    [InlineData("disc number")]
    [InlineData("disc count")]
    [InlineData("work")]
    [InlineData("movement name")]
    [InlineData("movement number")]
    [InlineData("composer")]
    [InlineData("year")]
    [InlineData("size")]
    [InlineData("duration")]
    [InlineData("content hash")]
    [InlineData("relative path")]
    [InlineData("advisory")]
    [InlineData("cloud only")]
    public void A_field_the_comparison_reads_is_noticed_when_it_moves(string field)
    {
        Pass(Full());

        var moved = Move(Full(), field);
        var result = Pass(moved);

        Assert.True(
            result.Updated + result.Adopted > 0,
            $"a change to {field} was not noticed: either the comparison has stopped reading it, " +
            "or its column is missing from LibraryDatabase.Compared and the held row is null there");
    }

    private static PeerTrack Move(PeerTrack track, string field) => field switch
    {
        "title" => track with { Title = "Allegro, but later" },
        "artist" => track with { Artist = "Another Ensemble" },
        "album artist" => track with { AlbumArtist = "Another Collective" },
        "album" => track with { Album = "The Other Box" },
        "genre" => track with { Genre = "Baroque" },
        "track number" => track with { TrackNumber = 8 },
        "disc number" => track with { DiscNumber = 3 },
        "disc count" => track with { DiscCount = 4 },
        "work" => track with { Work = "Symphony No. 6" },
        "movement name" => track with { MovementName = "Andante" },
        "movement number" => track with { MovementNumber = 2 },
        "composer" => track with { Composer = "Brahms" },
        "year" => track with { Year = 1809 },
        "size" => track with { SizeBytes = 41_234_568 },
        "duration" => track with { DurationSeconds = 448.9 },
        "content hash" => track with { ContentHash = "a-different-hash" },
        "relative path" => track with { RelativePath = "Ensemble/The Box/01 Allegro moved.flac" },
        "advisory" => track with { Advisory = (int)ContentAdvisory.Clean },
        "cloud only" => track with { CloudOnly = false },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "not a compared field"),
    };
}
