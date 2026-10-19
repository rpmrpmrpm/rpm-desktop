using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Sharing;

/// <summary>
/// Turns this machine's library rows into the listing a peer is given.
///
/// <para>Some fields are left out on purpose. A row carries a person's play counts, their favourites
/// and the dates they listened to things. The listing carries only what is in the collection. Only
/// what a peer needs to draw a shelf, sort it, search it and ask for a track is sent.</para>
/// </summary>
public static class PeerCatalogBuilder
{
    /// <summary>
    /// The listing for a set of library rows.
    ///
    /// <para>A track with no RPM id is skipped, not sent. The id is how a peer asks for the track. A
    /// row without one would be a line on a shelf that can never play. This happens for a file whose
    /// container refuses a stamp (<see cref="RpmTag.NoCarrier"/>), or for a file the library has not
    /// read yet. Such a track comes back on its own once the library has stamped it.</para>
    ///
    /// <para>Rows that this machine borrowed from a peer are never shared again. If machine A shares
    /// music that it borrowed from B with machine C, nobody agreed to that chain. It would loop as soon
    /// as two machines pointed at each other.</para>
    /// </summary>
    public static IReadOnlyList<PeerTrack> From(IEnumerable<LibraryTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var listing = new List<PeerTrack>();

        foreach (var track in tracks)
        {
            if (!RpmTag.IsId(track.RpmId) || track.PeerAdopted)
                continue;

            listing.Add(new PeerTrack
            {
                RpmId = track.RpmId!,
                RelativePath = track.RelativePath,
                Title = track.Title,
                Artist = track.Artist,
                AlbumArtist = track.AlbumArtist,
                Album = track.Album,
                Genre = track.Genre,
                Composer = track.Composer,
                Work = track.Work,
                MovementName = track.MovementName,
                MovementNumber = track.MovementNumber,
                DiscSubtitle = track.DiscSubtitle,
                TrackNumber = track.TrackNumber,
                DiscNumber = track.DiscNumber,
                DiscCount = track.DiscCount,
                Year = track.Year,
                DurationSeconds = Finite(track.DurationSeconds) ?? 0,
                SizeBytes = track.SizeBytes,
                AddedUtcTicks = track.AddedUtcTicks,
                Channels = track.Channels,
                SampleRate = track.SampleRate,
                BitsPerSample = track.BitsPerSample,
                BitrateKbps = track.BitrateKbps,
                CodecDescription = track.CodecDescription,
                LoudnessLufs = Finite(track.LoudnessLufs),
                TruePeak = Finite(track.TruePeak),
                ContentHash = track.ContentHash,

                // The file's own explicit (E) mark. A borrowed record, song or list then shows the same
                // mark as on the owner's shelf. The cloud listing already carries this flag. Without it
                // here, every borrowed row would read as unknown.
                Advisory = track.Advisory is { } advisory ? (int)advisory : 0,

                // A track the owner has offloaded has no bytes on their disk. It is still the owner's and
                // still on the shelf. The borrower is sent to the cloud copy to play it.
                CloudOnly = track.CloudState == CloudState.CloudOnly,

                // Separately: whether the cloud store has the track at all. A track that was uploaded and
                // is still there is the normal state of a cloud record. A borrower keeps this when the
                // owner's computer is switched off.
                InCloud = track.CloudState is CloudState.Synced or CloudState.CloudOnly,
            });
        }

        return listing;
    }

    /// <summary>
    /// A number only if JSON can carry it, and null otherwise.
    ///
    /// <para>The library stores a loudness of negative infinity on purpose. It is the meter's answer for
    /// silence. It also stops a file nobody can measure from being decoded again on every pass. JSON has
    /// no way to write that value, and <see cref="System.Text.Json.JsonSerializer"/> throws rather than
    /// invent one. One unmeasurable track would then take the whole catalogue down with it, and the peer
    /// waiting for a shelf would get nothing at all.</para>
    ///
    /// <para>Null is the honest translation. On the wire, null already means "nobody has measured this",
    /// which is exactly what negative infinity means here.</para>
    /// </summary>
    private static double? Finite(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    /// <summary>
    /// What the album rows are for. There is no default. The answer decides whether the owner's own
    /// marks leave this machine at all.
    ///
    /// <para>This is not a privacy switch. Two of one person's computers are one collection in two
    /// rooms. A rating that did not travel between them would be a rating the collection does not have.
    /// A different person's machine is looking at a shelf. What its owner thinks of a record is not on
    /// the shelf.</para>
    /// </summary>
    public enum AlbumAudience
    {
        /// <summary>Another of the owner's own computers. The whole row travels.</summary>
        TheOwner,

        /// <summary>Somebody the owner shares with. What the record IS travels: which pressing it is, and
        /// whether it is published as works and movements. What the owner thinks of it does not travel.</summary>
        AGuest,
    }

    /// <summary>
    /// The album rows for one asker, as much of them as that asker is allowed to see.
    ///
    /// <para>A row is sent even when every field a guest may see is empty. The stamp travels with the
    /// row. The stamp lets the two machines agree which of them holds the later answer.</para>
    /// </summary>
    public static IReadOnlyList<PeerAlbum> From(IEnumerable<AlbumMetaEdit> albums, AlbumAudience audience)
    {
        ArgumentNullException.ThrowIfNull(albums);

        var whole = audience is AlbumAudience.TheOwner;
        var listing = new List<PeerAlbum>();

        foreach (var (meta, edited) in albums)
        {
            listing.Add(new PeerAlbum
            {
                Folder = meta.Folder,

                // The owner's own marks. A guest never receives these.
                Favorite = whole && meta.IsFavorite,
                Rating = whole ? meta.Rating : 0,
                Review = whole ? meta.ReviewMarkdown : null,
                ReviewUpdatedUtcTicks = whole ? meta.ReviewUpdatedUtcTicks : 0,

                // Levelling goes with them too. Whether a record is left alone by the volume pass is a
                // decision about listening, not about the record. The person listening to a borrowed
                // shelf is not the person who made that decision.
                Normalize = whole ? meta.Normalize : null,

                // What the record IS. It is the same wherever it is looked at.
                ReleaseProvider = meta.ReleaseProvider,
                ReleaseId = meta.ReleaseId,
                RpmAlbumId = meta.RpmAlbumId,
                ReleaseGroupId = meta.ReleaseGroupId,
                DiscId = meta.DiscId,
                Barcode = meta.Barcode,
                Catno = meta.Catno,
                Released = meta.Released,
                Edition = meta.Edition,
                Styles = meta.Styles,
                PopularTrack = meta.PopularTrack,
                PopularAskedUtcTicks = meta.PopularAskedUtcTicks,

                // A classical record is published as works with movements under them. A borrowed one has
                // to be drawn that way too, or it becomes a list of twenty untitled tracks.
                Classical = meta.Classical,

                EditedUtcTicks = edited,
            });
        }

        return listing;
    }

    /// <summary>The other direction, for the machine that receives one.</summary>
    public static IReadOnlyList<AlbumMetaEdit> To(IEnumerable<PeerAlbum> albums)
    {
        ArgumentNullException.ThrowIfNull(albums);

        return albums
            .Where(a => !string.IsNullOrWhiteSpace(a.Folder))
            .Select(a => new AlbumMetaEdit(
                new AlbumMeta(
                    a.Folder,
                    IsFavorite: a.Favorite,
                    Rating: a.Rating,
                    ReviewMarkdown: a.Review,
                    ReviewUpdatedUtcTicks: a.ReviewUpdatedUtcTicks,
                    ReleaseId: a.ReleaseId,
                    Catno: a.Catno,
                    Released: a.Released,
                    Classical: a.Classical,
                    Normalize: a.Normalize,
                    ReleaseProvider: a.ReleaseProvider,
                    Barcode: a.Barcode,
                    RpmAlbumId: a.RpmAlbumId,
                    DiscId: a.DiscId,
                    ReleaseGroupId: a.ReleaseGroupId,
                    Edition: a.Edition,
                    Styles: a.Styles,
                    PopularTrack: a.PopularTrack,
                    PopularAskedUtcTicks: a.PopularAskedUtcTicks),
                a.EditedUtcTicks))
            .ToList();
    }

    /// <summary>
    /// The owner's folders and playlists, as a guest is given them.
    ///
    /// <para>This is most of what a collection is once it has more than a few hundred albums: what a
    /// person has made of their records. The names, the order and the contents are sent. Nothing about
    /// where any of it stands with the owner's cloud is sent, because that is not a fact about the
    /// playlist. Whether a borrowed list still plays after the owner's computer is switched off depends
    /// on whether the songs in it are in the cloud. The track listing beside this already says that for
    /// every track.</para>
    /// </summary>
    /// <param name="pictures">The stamp of each picture the owner chose, by node id (see
    /// <see cref="PeerPlaylist.Picture"/>). It is stated at every call. The pictures are files on the
    /// owner's computer, which this builder cannot read. A caller that left them out would send every
    /// list as automatic, and nothing would say so. A node missing from the dictionary is sent as
    /// automatic.
    /// </param>
    public static IReadOnlyList<PeerPlaylist> Playlists(
        IEnumerable<PlaylistTreeRow> tree, IReadOnlyDictionary<long, string> pictures)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(pictures);

        return [.. tree.Select(row => new PeerPlaylist
        {
            Id = row.Id,
            ParentId = row.ParentId,
            IsFolder = row.Kind == PlaylistNodeKind.Folder,
            Name = row.Name,
            Sort = row.Sort,
            Picture = pictures.GetValueOrDefault(row.Id),
            Items = [.. row.Items.Select(item => new PeerPlaylistItem
            {
                RpmId = item.Kind == PlaylistItemKind.Song ? item.RpmId : null,
                RelPath = item.Kind == PlaylistItemKind.Song ? item.RelPath : null,
                Ref = item.Kind == PlaylistItemKind.Playlist ? item.RefPlaylistId : null,
            })],
        })];
    }

    /// <summary>The other direction, for the machine that receives one.</summary>
    /// <remarks>A row whose owner chose its picture comes in as <see cref="PlaylistCover.ChosenByOwner"/>.
    /// The borrower asks for that picture by this value. Every other row comes in as automatic. How the
    /// owner chose a picture is not sent on the wire (see <see cref="PlaylistCover"/>). Only the fact
    /// that they chose one is sent.
    /// </remarks>
    public static IReadOnlyList<PlaylistTreeRow> To(IEnumerable<PeerPlaylist> playlists)
    {
        ArgumentNullException.ThrowIfNull(playlists);

        return [.. playlists.Select(p => new PlaylistTreeRow(
            Id: p.Id,
            ParentId: p.ParentId,
            Kind: p.IsFolder ? PlaylistNodeKind.Folder : PlaylistNodeKind.Playlist,
            Name: p.Name ?? "",
            Sort: p.Sort,
            Items: [.. p.Items.Select(item => item.Ref is { } reference
                ? new PlaylistTreeEntry(PlaylistItemKind.Playlist, null, null, reference)
                : new PlaylistTreeEntry(PlaylistItemKind.Song, item.RpmId, item.RelPath, null))],
            Cover: p.Picture is { Length: > 0 } stamp
                ? new PlaylistCover.ChosenByOwner(p.Id, stamp)
                : new PlaylistCover.Automatic()))];
    }

    /// <summary>
    /// The listing plus its version, ready to answer a catalogue request.
    ///
    /// <para><paramref name="albums"/> and <paramref name="playlists"/> are stated at every call, even
    /// when empty. Who is asking decides whether the owner's marks travel and whether the owner's
    /// playlists do. A parameter that could be forgotten would send them to a guest, or fail to, by
    /// omission.</para>
    /// </summary>
    public static PeerCatalog Catalog(
        IEnumerable<LibraryTrack> tracks, string name, IReadOnlyList<PeerAlbum> albums,
        IReadOnlyList<PeerPlaylist> playlists, IReadOnlyList<PeerMoodList> moodLists)
    {
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(moodLists);

        var listing = From(tracks);
        return new PeerCatalog
        {
            Version = PeerCatalog.VersionOf(listing, albums, playlists, moodLists),
            Name = name ?? "",
            Tracks = listing,
            Albums = albums,
            Playlists = playlists,
            MoodLists = moodLists,
        };
    }

    /// <summary>
    /// The owner's generated lists, in the shape a peer is given.
    ///
    /// <para><b>Sent whole. They are not trimmed to what the borrower can play.</b> Which records a
    /// borrower can reach is not a fact about the list, and the answer is not the same every time. On
    /// the same network the borrower can ask this machine for every one of them. Away from the network,
    /// only the ones in the cloud resolve. Trimming here would fix one of those two answers into a
    /// catalogue that outlives the connection it was fetched over. The borrower drops what it cannot
    /// reach, at the moment it tries. The phone follows the same rule.</para>
    ///
    /// <para>A list whose records are all borrowed from somebody else does not happen. The cut is taken
    /// over this machine's own rows, and <see cref="From"/> never shares a borrowed row again.</para>
    /// </summary>
    public static IReadOnlyList<PeerMoodList> MoodLists(MoodListsPayload cut)
    {
        ArgumentNullException.ThrowIfNull(cut);

        return [.. cut.Lists.Select(l => new PeerMoodList
        {
            Kind = l.Kind,
            Genre = l.Genre,
            Decade = l.Decade,
            Folder = l.Folder,
            RpmIds = l.TrackIds,
        })];
    }
}
