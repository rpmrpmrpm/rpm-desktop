using RPM.Core.Library;

namespace RPM.Core.Cloud;

/// <summary>
/// A correction made somewhere else that this library does not have yet: the catalogue row as it now
/// stands, and this machine's row as it still stands.
/// </summary>
public sealed record CloudPull(CloudTrack Cloud, LibraryTrack Row)
{
    public string RpmId => Cloud.RpmId;
}

/// <summary>What one sync run should do about metadata, in both directions.</summary>
/// <param name="Push">Rows this machine's copy is the newer of, to be sent.</param>
/// <param name="Pull">Rows that another machine has changed since this one last agreed with the store.</param>
/// <param name="Sidecars">RPM ids whose private tag blob is out of date: the file here was
/// written after it was uploaded, and the sidecar is what a restore rebuilds the file from.</param>
/// <param name="CloudOnlySidecars">RPM ids of tracks with no file here whose sidecar is to be patched
/// from the row: every cloud-only row in <paramref name="Push"/>, and every cloud-only row that owes
/// its sidecar and already agrees with the catalogue. Never a row that still differs from the
/// catalogue and did not fit in this push: stamping its sidecar as sent would hide that the
/// catalogue is still behind.</param>
public sealed record CloudSyncDecision(
    IReadOnlyList<CatalogPush> Push,
    IReadOnlyList<CloudPull> Pull,
    IReadOnlyList<string> Sidecars,
    IReadOnlyList<string> CloudOnlySidecars);

/// <summary>
/// Who is right about a track's metadata, this library or the store. It is decided for each row, on
/// each run.
///
/// <para>
/// <b>Why this is not a one-way push.</b> A one-way rule ("the library is the record, the cloud is a
/// copy of it, and where they disagree the copy is wrong") is right with one computer and wrong
/// with two. The second install would read a correction made on the first, see that its own row
/// says something else, and push its stale wording back. Whichever machine synced last would win,
/// and an edit could quietly undo itself hours later.
/// </para>
///
/// <para>
/// <b>Why a clock is never compared with another machine's clock.</b> Two computers do not agree
/// about the time, and a sync that resolved conflicts by comparing their timestamps would let the
/// machine with the fast clock win. So each side is compared only against itself:
/// </para>
///
/// <list type="bullet">
/// <item><b>Changed here?</b> The row's own edit stamp against the moment this machine last agreed
/// with the store, <see cref="LibraryTrack.CloudSyncedUtcTicks"/>. Both are this machine's clock.</item>
/// <item><b>Changed there?</b> The store's stamp on the catalogue row against the stamp this machine
/// last accepted, <see cref="LibraryTrack.CloudUpdatedAt"/>. Both are the server's clock.</item>
/// </list>
///
/// <para>
/// <b>Everything that is not that one case is a push.</b> If both sides changed, that is a genuine
/// race (two people editing one track on two computers inside one sync interval), and the machine
/// in front of the user wins with the whole row, so the two are in step at once and do not trade
/// edits. If neither side changed and they still disagree (a library that drifted under an older
/// build), the library repairs the copy.
/// </para>
///
/// <para>
/// <b>Why this cannot become a tug of war.</b> Every write moves the store's stamp, so the machine
/// that did not push sees "changed there", finds that it has nothing of its own outstanding, and
/// takes the other's version. That is one push and one pull, and then it is done. Two machines do
/// not push at each other for as long as both are switched on.
/// </para>
/// </summary>
public static class CloudSyncPlan
{
    /// <param name="mirror">Every row this library has an opinion about: the tracks it holds and the
    /// tracks it only has in the cloud. Rows with no RPM id are skipped, because the id is what the two
    /// sides are matched on.</param>
    /// <param name="cloud">The catalogue as the store now has it.</param>
    /// <param name="limit">How much one run may send, so that a library that has drifted for a long
    /// time is not sent as one enormous burst. What is left is found again next time.</param>
    /// <param name="albums">The album each folder holds, for the pressing that a row carries up. Absent
    /// for a caller that keeps none, and then a row says nothing about which pressing it is. The other
    /// end reads that as "not known" and not as an instruction to forget.</param>
    public static CloudSyncDecision Make(
        IEnumerable<LibraryTrack> mirror, IReadOnlyList<CloudTrack> cloud, int limit = int.MaxValue,
        IReadOnlyDictionary<string, AlbumMeta>? albums = null)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(cloud);

        var known = new Dictionary<string, CloudTrack>(cloud.Count, StringComparer.Ordinal);
        foreach (var track in cloud)
            known[track.RpmId] = track;

        var push = new List<CatalogPush>();
        var pull = new List<CloudPull>();
        var sidecars = new List<string>();
        var cloudOnlySidecars = new List<string>();

        foreach (var row in mirror)
        {
            if (row.RpmId is not { Length: > 0 } id) continue;
            if (!known.TryGetValue(id, out var theirs)) continue;   // not in the cloud: not this job

            // Somebody else's correction, and nothing of this machine's standing against it. This is the
            // only case that is pulled; everything else is this library's word.
            if (!ChangedHere(row) && ChangedThere(row, theirs) && Differs(row, theirs))
            {
                if (pull.Count < limit)
                    pull.Add(new CloudPull(theirs, row));
                continue;
            }

            AlbumMeta? album = null;
            albums?.TryGetValue(row.Folder ?? "", out album);

            // CorrectsAnything and not !IsEmpty: a push carries this machine's path so that the store knows
            // where the file is now, but a path on its own is never a reason to write. Both ends of that rule
            // are here: Differs above does not read the store's path either.
            //
            // Asked of every row, not only while the push has room: whether a row that did not fit still
            // differs is what decides its cloud-only sidecar below.
            var patch = CatalogReconciler.Differs(row, theirs, album);
            var pushing = patch.CorrectsAnything && push.Count < limit;
            if (pushing)
                push.Add(patch);

            if (row.CloudState == CloudState.CloudOnly
                && (pushing || (!patch.CorrectsAnything && OwesSidecar(row))))
                cloudOnlySidecars.Add(id);

            // The file itself was written after it was uploaded (retagged by anything at all). The sidecar
            // carries the whole tag surface and the embedded cover, none of which the catalogue row holds, so
            // it is sent on the file's own evidence and not on a comparison that cannot be made. Never for a
            // row being pulled: that file is about to be written BY the store, and sending it back would be
            // this machine arguing with itself.
            if (sidecars.Count < limit && row.CloudState == CloudState.Synced && OwesSidecar(row))
                sidecars.Add(id);
        }

        return new CloudSyncDecision(push, pull, sidecars, cloudOnlySidecars);
    }

    /// <summary>
    /// Whether the store's sidecar is older than this row: the file was written after the sidecar was
    /// last sent, or, for a track with no file here, the row was edited after it was last sent.
    ///
    /// <para><b>The catalogue push must not clear this.</b> A push sends up to 500 rows per run and the
    /// sidecar passes send 40 and 20. If a push marked every row it sent as in step, it would erase the
    /// only evidence that the other sidecars are still owed, because those were never sent. Only a
    /// sidecar pass, once the sidecar is written or found to agree, may move
    /// <see cref="LibraryTrack.CloudSyncedUtcTicks"/> past this.</para>
    /// </summary>
    public static bool OwesSidecar(LibraryTrack row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.CloudState switch
        {
            CloudState.Synced => row.ModifiedUtcTicks > row.CloudSyncedUtcTicks,
            CloudState.CloudOnly => row.MetadataEditedUtcTicks > row.CloudSyncedUtcTicks,
            _ => false,
        };
    }

    /// <summary>
    /// Whether this library has changed the track since it last agreed with the store.
    ///
    /// <para>There are two ways it can have: the file was written (a retag rewrites it, so its modified
    /// time moves), or the row was edited with no file to write. The second is a track that lives only
    /// in the cloud, whose only evidence is <see cref="LibraryTrack.MetadataEditedUtcTicks"/>.</para>
    /// </summary>
    public static bool ChangedHere(LibraryTrack row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return Math.Max(row.ModifiedUtcTicks, row.MetadataEditedUtcTicks) > row.CloudSyncedUtcTicks;
    }

    /// <summary>
    /// Whether the store's row has been written since this machine last accepted it, which means
    /// that another install wrote it.
    ///
    /// <para>A row whose stamp is zero was written by a build that did not keep one. Nothing can be
    /// concluded from it, so nothing is: it is left to the push side.</para>
    /// </summary>
    public static bool ChangedThere(LibraryTrack row, CloudTrack cloud)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(cloud);

        return cloud.UpdatedAt > 0 && cloud.UpdatedAt > row.CloudUpdatedAt;
    }

    /// <summary>Whether taking the cloud's word would actually change anything here. A blank on the
    /// cloud side is "not known there" and is never a reason to clear a field that this library has.</summary>
    public static bool Differs(LibraryTrack row, CloudTrack cloud)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(cloud);

        return Changed(cloud.Title, row.Title)
            || Changed(cloud.Artist, row.Artist)
            || Changed(cloud.Album, row.Album)
            || Changed(cloud.Tags?.AlbumArtist, row.AlbumArtist)
            || Changed(cloud.Tags?.Genre, row.Genre)
            // Not the path. Where a file sits is this machine's own answer about its own disk. Reading the
            // store's path as a disagreement would drag an album that was re-filed here back to its
            // original filing on every run. See Apply.
            || Number(cloud.Tags?.TrackNumber, row.TrackNumber)
            || Number(cloud.Tags?.DiscNumber, row.DiscNumber)
            || Number(cloud.Tags?.DiscCount, row.DiscCount)
            || Number(cloud.Tags?.Year, row.Year)
            || Changed(cloud.Tags?.Work, row.Work)
            || Changed(cloud.Tags?.MovementName, row.MovementName)
            || Changed(cloud.Tags?.Composer, row.Composer)
            // The advisory flag, where the catalogue states one. Its zero is "not known there", as a
            // blank title is.
            || (cloud.Tags?.StatedAdvisory is { } advisory && advisory != row.Advisory)
            // The listener's mark, on a row that this machine has not touched: another of their computers
            // loved the song, or stopped. It is compared exactly. False is a real answer, so a mark taken
            // off somewhere else comes off here too. But it is compared only on a row that carries the
            // field at all, which is what the null check is for: a catalogue row from an older server says
            // nothing about favourites, and reading its silence as "not a favourite" would clear the marks
            // on a library as soon as it met such a row.
            || (cloud.Tags is { } tags && tags.Favorite != row.IsFavorite);
    }

    /// <summary>
    /// The row as the store's version of it, only where the store has something to say.
    ///
    /// <para>
    /// <b>The path is not taken.</b> Where a file sits is decided by this machine's naming template.
    /// If the store's path were taken, the cloud's memory of where a file used to be would override the
    /// template that has since moved it: the refiler would put the file where the user asked, the pull
    /// would drag it back, one row per track on every run. The store's path is a record of where the
    /// file was when it went up. An adopted row sits at it and a restore writes to it, but it is not an
    /// instruction to a machine that already holds the file.
    /// </para>
    /// </summary>
    public static LibraryTrack Apply(LibraryTrack row, CloudTrack cloud)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(cloud);

        return row with
        {
            Title = Text(cloud.Title, row.Title),
            Artist = Text(cloud.Artist, row.Artist),
            Album = Text(cloud.Album, row.Album),
            AlbumArtist = Text(cloud.Tags?.AlbumArtist, row.AlbumArtist),
            Genre = Text(cloud.Tags?.Genre, row.Genre),
            TrackNumber = Position(cloud.Tags?.TrackNumber, row.TrackNumber),
            DiscNumber = Position(cloud.Tags?.DiscNumber, row.DiscNumber),
            DiscCount = Position(cloud.Tags?.DiscCount, row.DiscCount),
            Year = Position(cloud.Tags?.Year, row.Year),
            Work = Text(cloud.Tags?.Work, row.Work),
            MovementName = Text(cloud.Tags?.MovementName, row.MovementName),
            // Kept as an int? here: the mirror's null still means "this file has never been read", and the
            // cloud saying nothing is not the same as the file saying zero.
            MovementNumber = cloud.Tags?.MovementNumber is > 0
                ? cloud.Tags.MovementNumber
                : row.MovementNumber,
            Composer = Text(cloud.Tags?.Composer, row.Composer),
            // Written into the file with the rest by CloudPullApplier, which writes a flag only when it
            // is not Unknown, so a catalogue that states none leaves the file's flag alone too.
            Advisory = cloud.Tags?.StatedAdvisory ?? row.Advisory,
            // Taken as stated where the row carries it, and left alone where it does not (see the note in
            // Differs). The stamp comes with it, so a list of favourites reads in the order the marks were
            // actually made, whichever machine made them.
            IsFavorite = cloud.Tags?.Favorite ?? row.IsFavorite,
            FavoritedUtcTicks = cloud.Tags is { } marked
                ? marked.Favorite ? marked.FavoritedAt : 0
                : row.FavoritedUtcTicks,
            CloudUpdatedAt = cloud.UpdatedAt,
        };
    }

    private static bool Changed(string? theirs, string? mine)
        => !string.IsNullOrWhiteSpace(theirs) && !string.Equals(theirs, mine, StringComparison.Ordinal);

    private static bool Number(int? theirs, uint mine) => theirs is > 0 && (uint)theirs.Value != mine;

    /// <summary>The store's value where it has one, and what this library holds where it has not. A
    /// blank on that side is "not known there" and never an instruction to erase.</summary>
    private static string? Text(string? theirs, string? mine)
        => string.IsNullOrWhiteSpace(theirs) ? mine : theirs.Trim();

    private static uint Position(int? theirs, uint mine) => theirs is > 0 ? (uint)theirs.Value : mine;
}
