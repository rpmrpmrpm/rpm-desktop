using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Sharing;

/// <summary>
/// A peer's listing, in the shape the mirror already knows how to adopt.
///
/// <para>
/// The cloud already teaches this library that a track can be a row without being a file
/// (<see cref="CloudLibraryMirror"/>). Everything that follows from that is decided there. That covers
/// one row per library path, never two rows for one record, and a record already on the shelf under
/// another name being the same record. Another of the user's computers is the same situation with a
/// different address: music they own, held somewhere that is not this disk.
/// </para>
///
/// <para>
/// So a peer row is turned into a catalogue row and handed to the rules that already exist. A second set
/// of rules would have to be kept in step with them. Only the location of the bytes differs, and that is
/// carried on the library row, not here.
/// </para>
/// </summary>
public static class PeerLibraryMirror
{
    /// <summary>One peer track, as the cloud mirror's rules read it.</summary>
    public static CloudTrack ToCatalogRow(PeerTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        return new CloudTrack(
            RpmId: track.RpmId,

            // The canonical hash, when the owner knows it. It tells this machine that a peer's track and
            // one of its own are the same audio. Empty means not known. The path and the id still answer
            // the question in that case.
            Sha256: track.ContentHash ?? "",
            RelativePath: track.RelativePath,
            OriginalSize: track.SizeBytes,
            Title: track.Title,
            Artist: track.Artist,
            Album: track.Album,
            AddedAt: track.AddedUtcTicks > 0
                ? new DateTimeOffset(new DateTime(track.AddedUtcTicks, DateTimeKind.Utc)).ToUnixTimeSeconds()
                : 0,
            Tags: new CloudTrackTags(
                DurationSeconds: track.DurationSeconds,
                TrackNumber: (int)track.TrackNumber,
                DiscNumber: (int)track.DiscNumber,
                DiscCount: (int)track.DiscCount,
                Year: (int)track.Year,
                Genre: track.Genre,
                AlbumArtist: track.AlbumArtist,
                Extension: System.IO.Path.GetExtension(track.RelativePath),
                Channels: track.Channels ?? 0,
                SampleRate: track.SampleRate ?? 0,
                BitsPerSample: track.BitsPerSample ?? 0,
                BitrateKbps: track.BitrateKbps ?? 0,
                CodecDescription: track.CodecDescription,
                Work: track.Work,
                MovementName: track.MovementName,
                MovementNumber: track.MovementNumber ?? 0,
                Composer: track.Composer,
                LoudnessLufs: track.LoudnessLufs ?? 0,
                TruePeak: track.TruePeak ?? 0,

                // The explicit flag, read the way the cloud's is. Zero states nothing and keeps whatever
                // the row already has.
                Advisory: track.Advisory,

                // Not carried, on purpose. A favourite is something a person did. The person at this
                // machine is the one whose favourites belong in this library, even when the account is the
                // same and the record is the same record.
                Favorite: false,
                FavoritedAt: 0));
    }

    /// <summary>
    /// The tracks in a listing whose bytes are in the cloud rather than on the owner's disk, by id.
    ///
    /// <para>This is for a listing from one of the user's <em>own</em> computers. The account's cloud
    /// catalogue is already drawn here, and it says everything else that is up there. The offloaded tracks
    /// are the gap that it leaves. For the guest case, which has no such catalogue to rely on, see
    /// <see cref="CloudBackedIds"/>.</para>
    /// </summary>
    public static HashSet<string> CloudOnlyIds(IEnumerable<PeerTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return [.. tracks.Where(t => t.CloudOnly).Select(t => t.RpmId)];
    }

    /// <summary>
    /// The tracks in a listing that the owner's cloud holds a copy of, by id. This includes offloaded
    /// tracks.
    ///
    /// <para>A borrowed shelf marks these as cloud rows. So they are what stays on the shelf once that
    /// computer is off the network. Somebody else's cloud catalogue cannot be read from here. Their
    /// listing is the only thing that says which of their records the store has. Reading only the
    /// offloaded flag would keep the few records the owner has cleared from their disk, and drop every
    /// record that is uploaded but still on their disk.</para>
    /// </summary>
    public static HashSet<string> CloudBackedIds(IEnumerable<PeerTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return [.. tracks.Where(t => t.InCloud || t.CloudOnly).Select(t => t.RpmId)];
    }

    /// <summary>A whole listing.</summary>
    public static IReadOnlyList<CloudTrack> ToCatalogRows(IEnumerable<PeerTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return tracks.Select(ToCatalogRow).ToList();
    }
}
