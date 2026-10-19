using RPM.Core.Cloud;

namespace RPM.Core.Audio;

/// <summary>Where the cover on the television comes from.
///
/// <para><b>Why this is an argument and not a line of code inside the sink.</b> A sink that reads the art
/// itself, from the file at <see cref="SinkTrack.Path"/>, is right for exactly one kind of record: one
/// whose bytes are on this disk. It is silently wrong for every other kind the app can play. A record
/// borrowed from another computer has a path that belongs to <i>that</i> computer, so the file is not
/// there, and neither is the folder beside it. A record this machine has offloaded has a row and a sleeve
/// but no file at all. Both play correctly, and both cast with a blank square. Nothing reports it, because
/// "no cover" and "this cover could not be found" give the same answer from inside a function that only
/// knows how to look in one place.</para>
///
/// <para>So the sink does not decide this itself. Whoever builds the sink says where covers come from, and
/// the compiler asks every caller. The answer that means "the file and nowhere else" must be
/// <see cref="SinkArt.FromTheFileAlone"/>. That states the choice, rather than arriving at it by leaving
/// something out.</para>
/// </summary>
/// <returns>The cover, or null when this record has none. It never throws. A cover that cannot be read
/// counts as no cover, and playing the music is the main job.</returns>
public delegate Task<AlbumCover?> SinkArtwork(SinkTrack track, CancellationToken cancellation);

/// <summary>The answers to <see cref="SinkArtwork"/> that belong to nobody in particular.</summary>
public static class SinkArt
{
    /// <summary>The file's own picture, and nothing else: the album folder's image, or failing that, the
    /// art in the track's tags.
    ///
    /// <para>The folder comes first. That is not the order <see cref="AlbumArt.ForFile"/> uses. A cover
    /// chosen in the album editor is saved to the folder beside the tracks, and it is the cover this
    /// machine draws the record with. The tags keep whatever the rip put there. If the tags were read
    /// first, the television would show the old picture, not the one the user chose.
    /// <c>SharingService.CoverFor</c> gives a cover to a borrower in the same order, for the same
    /// reason.</para>
    ///
    /// <para>This is right for a record whose files are on this disk, and only for that. Anything else
    /// needs a resolver that can reach where the record actually lives. See the remarks on
    /// <see cref="SinkArtwork"/>.</para>
    /// </summary>
    public static SinkArtwork FromTheFileAlone { get; } = (track, _) =>
    {
        AlbumCover? cover = null;
        try
        {
            var folder = Path.GetDirectoryName(track.Path);
            cover = (folder is { Length: > 0 } ? AlbumArt.ForFolder(folder) : null)
                    ?? AlbumArt.ForFile(track.Path);
        }
        catch
        {
            // A cover that cannot be read is no cover, never an error.
        }

        return Task.FromResult(cover);
    };

    /// <summary>A record with no picture, whatever it is. For a sink that should not draw one: the
    /// recorder's monitor, and a test that is about the audio.</summary>
    public static SinkArtwork None { get; } = (_, _) => Task.FromResult<AlbumCover?>(null);
}
