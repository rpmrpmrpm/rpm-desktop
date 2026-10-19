namespace RPM.Core.Cloud;

/// <summary>
/// Whose name a track goes by. Once its bytes are in the cloud, the RPM id is the store's name for
/// the track: the address under which every rendition, every stream and every catalogue row is
/// filed. Nothing on this disk may replace it.
///
/// <para>
/// The rule is stated once here, because it matters in two places that never see each other: the
/// sync pass, which stamps an id into a file that carries none, and the upsert, which writes the id
/// that a file was read with onto the row. If either of them makes a new id for a track that is
/// already in the cloud, this can happen: an album goes up under the ids its files carry, then
/// something rewrites those files and the ids are lost with the tags. The next sync finds the files
/// unstamped, makes fresh ids and writes them onto rows that still say "in the cloud". The library
/// then shows the album as backed up under names that the account has never had, and the rendition
/// pass gets a 404 for those names on every run. The run blames the account, while the store holds
/// the music all along under the names the files used to carry.
/// </para>
///
/// <para>
/// Renaming a row that is in the cloud is still possible, but only through the path that the cloud
/// owns: <c>LibraryDatabase.SetRpmId</c>. The upload path calls it when the store itself says that
/// this audio is already up there under another id (see <see cref="CloudAdoption"/>).
/// </para>
/// </summary>
public static class CloudName
{
    /// <summary>
    /// The states in which the row's id belongs to the store and not to this machine: the bytes are
    /// up there with a copy here, or up there and nowhere else. It is one list, so a scan and a SQL
    /// statement cannot give different answers about the same row.
    /// </summary>
    public static readonly IReadOnlyList<CloudState> Bound = [CloudState.Synced, CloudState.CloudOnly];

    /// <summary>
    /// The name the store knows this track by, or null when nothing up there answers to it, so a new
    /// id costs the library nothing.
    /// </summary>
    /// <param name="state">What the row says about where its bytes are.</param>
    /// <param name="rowId">The id on the row. Null is a row that was never stamped, and the empty
    /// string is <c>RpmTag.NoCarrier</c>, a file that cannot hold an id at all. Neither is a name.</param>
    public static string? TheStoreCallsIt(CloudState state, string? rowId)
        => rowId is { Length: > 0 } && Bound.Contains(state) ? rowId : null;
}
