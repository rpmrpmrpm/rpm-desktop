using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The one rule about a track's RPM id once its bytes are in the cloud: the id is the store's name
/// for it, and nothing on this disk gets to change it.
///
/// <para>
/// If a tool rewrites the files and the ids are lost with the tags, the next sync finds files with
/// no id and mints new ones. Written onto rows that say "in the cloud", those new ids are names the
/// account does not know. The rendition pass then gets 404 for them on every run and reports an
/// account or connection fault, while the store still holds the music under the original ids.
/// </para>
/// </summary>
public class CloudNameTests
{
    /// <summary>Both states mean the store is holding the bytes: one with a copy on this disk, one
    /// without. The name is the store's either way.</summary>
    [Theory]
    [InlineData(CloudState.Synced)]
    [InlineData(CloudState.CloudOnly)]
    public void A_row_whose_bytes_are_up_there_names_its_file(CloudState state)
        => Assert.Equal("gid-a", CloudName.TheStoreCallsIt(state, "gid-a"));

    /// <summary>Nothing has been sent, so the store knows nothing by this id and re-stamping the
    /// file is safe.</summary>
    [Theory]
    [InlineData(CloudState.Local)]
    [InlineData(CloudState.Queued)]
    [InlineData(CloudState.Uploading)]
    [InlineData(CloudState.Error)]
    public void A_row_that_is_only_here_names_nothing(CloudState state)
        => Assert.Null(CloudName.TheStoreCallsIt(state, "gid-a"));

    /// <summary>Null is a row that was never stamped; the empty string is the marker for a file that
    /// cannot carry an id at all (<c>RpmTag.NoCarrier</c>). Neither is a name.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_row_with_no_id_names_nothing(string? rowId)
        => Assert.Null(CloudName.TheStoreCallsIt(CloudState.Synced, rowId));
}
