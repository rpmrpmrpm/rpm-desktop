using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The listing a peer receives, and the one rule the wire puts on it: everything in it must be
/// something JSON can represent.
///
/// <para>The library stores a loudness of negative infinity for a track that cannot be measured. That
/// is deliberate and not rare (a corrupt file, a silent one, or one the decoder refused). It also
/// cannot be written as JSON, so a listing that contains it does not just fail to describe one track:
/// the whole listing fails, and the peer waiting on the other end of the network receives
/// nothing.</para>
/// </summary>
public class PeerCatalogBuilderTests
{
    private static LibraryTrack Track(string rpmId, double? lufs = null, double? peak = null,
        double seconds = 200, CloudState cloud = CloudState.Local) =>
        new()
        {
            CloudState = cloud,
            RelativePath = $"Band/Record/{rpmId}.flac",
            Folder = "Band/Record",
            FileName = $"{rpmId}.flac",
            Extension = ".flac",
            SizeBytes = 10,
            ModifiedUtcTicks = 5,
            RpmId = rpmId,
            Title = "Song",
            Artist = "Band",
            Album = "Record",
            DurationSeconds = seconds,
            LoudnessLufs = lufs,
            TruePeak = peak,
        };

    [Fact]
    public void A_track_nobody_could_measure_still_goes_across()
    {
        var listing = PeerCatalogBuilder.From([
            Track("gid-1", lufs: double.NegativeInfinity),
            Track("gid-2", lufs: -14.2, peak: 0.98),
        ]);

        Assert.Equal(2, listing.Count);

        // Unmeasurable is sent as "unknown", which is what it means. The measured one is unchanged.
        Assert.Null(listing[0].LoudnessLufs);
        Assert.Equal(-14.2, listing[1].LoudnessLufs);
        Assert.Equal(0.98, listing[1].TruePeak);
    }

    [Fact]
    public void One_unmeasurable_track_does_not_take_the_catalog_with_it()
    {
        var catalog = PeerCatalogBuilder.Catalog(
            [Track("gid-1", lufs: double.NegativeInfinity), Track("gid-2", lufs: -14.2)],
            "Desk", [], [], []);

        Assert.False(string.IsNullOrEmpty(catalog.Version));
        Assert.Equal(2, catalog.Tracks.Count);
    }

    [Fact]
    public void A_record_in_the_cloud_says_so_whether_or_not_its_owner_still_holds_the_file()
    {
        var listing = PeerCatalogBuilder.From([
            Track("gid-disk", cloud: CloudState.Local),
            Track("gid-both", cloud: CloudState.Synced),
            Track("gid-offloaded", cloud: CloudState.CloudOnly),
        ]);

        // Uploaded-and-kept is the normal state of a record in the cloud, and by far the most common.
        // A listing that marked only the offloaded ones would leave a borrower with three albums out of
        // a thousand once the owner's computer left the network.
        Assert.False(listing[0].InCloud);
        Assert.True(listing[1].InCloud);
        Assert.True(listing[2].InCloud);

        // Offloaded still means the same thing: the owner's disk no longer has the bytes.
        Assert.False(listing[0].CloudOnly);
        Assert.False(listing[1].CloudOnly);
        Assert.True(listing[2].CloudOnly);
    }

    [Fact]
    public void Nothing_else_a_meter_can_answer_breaks_it_either()
    {
        var catalog = PeerCatalogBuilder.Catalog([
            Track("gid-1", lufs: double.NaN, peak: double.PositiveInfinity, seconds: double.NaN),
        ], "Desk", [], [], []);

        Assert.False(string.IsNullOrEmpty(catalog.Version));
        Assert.Null(catalog.Tracks[0].LoudnessLufs);
        Assert.Null(catalog.Tracks[0].TruePeak);
        Assert.Equal(0, catalog.Tracks[0].DurationSeconds);
    }
}
