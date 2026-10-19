namespace RPM.Core.Audio;

/// <summary>What a source is playing <i>right now</i>, when that is not the same for the whole stream. A
/// decoded file is one track from start to end and needs none of this. A DJ mix moves from record to record
/// inside one continuous stream, and everything that shows "what is playing" has to follow it.</summary>
/// <param name="Path">The track's identity as the library knows it: an absolute file path. The artwork, the
/// grid's playing row and the album wall's badge all match on this, so it must be the real path and not a
/// label.</param>
/// <param name="IsRecord">Whether <paramref name="Path"/> names a record: something the collection can be
/// asked about, and a sheet can be shown for. True for the DJ's mix, whose records come from the collection
/// whether or not this disk holds the file. False for a radio stream, whose "track" is the station's own
/// address. That identifies the stream, and nothing can be looked up by it. This is stated, not guessed,
/// because everything that opens or shows the playing item decides on it.</param>
/// <param name="PositionSeconds">How far into this track the stream is, in the track's own seconds.</param>
/// <param name="DurationSeconds">How long this track is. Zero when unknown.</param>
/// <param name="ArtUrl">Cover art to show when there is no file to read it from, for example a radio
/// station's logo. Null for anything that plays from disk, where the file's own art is better than any
/// URL.</param>
public readonly record struct NowPlayingTrack(
    string Path,
    bool IsRecord,
    string? Title,
    string? Artist,
    string? Album,
    double PositionSeconds,
    double DurationSeconds,
    string? ArtUrl = null);

/// <summary>A source that plays a series of tracks rather than one, such as a DJ's mix. The engine asks it
/// what is playing instead of reporting the stream itself. So the player's readout, the seek bar and the
/// artwork show the record the room is hearing, not "the set, 41 minutes in".
///
/// <para>It is polled, not pushed. The UI reads the position several times a second anyway. The one thing
/// that must be announced is a change of track, so that the artwork and the playing-row highlight are told
/// to look again. See the engine's Changed event.</para>
/// </summary>
public interface INowPlayingSource
{
    /// <summary>The track currently coming out of this source, or null when it has none yet.</summary>
    NowPlayingTrack? NowPlayingTrack { get; }
}
