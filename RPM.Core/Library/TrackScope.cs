namespace RPM.Core.Library;

/// <summary>
/// Which tracks a count is about: every track the collection lists, or only the tracks that have a
/// file on this computer.
///
/// <para><b>Always pass a value. Do not rely on a default.</b> The mirror lists three kinds of track:
/// files on this disk, records from the account's cloud catalogue that have never been on this
/// computer, and tracks streamed over the network from another machine. The last two have no file.
/// A library can be made only of them. Sign in on a new machine, point rpm at an empty folder, and the
/// catalogue fills the mirror before any track is imported.</para>
///
/// <para>So "how many tracks are there" has two correct answers, and they are different numbers. The
/// caller must say which one it wants.</para>
/// </summary>
public enum TrackScope
{
    /// <summary>Every track the collection lists, wherever its audio is. Used by the album wall, by
    /// playback, and by the home screen's gate. A cloud record counts as a record, because it can be
    /// streamed.</summary>
    Anywhere,

    /// <summary>Only tracks with a file on this disk. Used for import results, disk-space figures, and
    /// the duplicate check for a file being imported.</summary>
    OnThisComputer,
}
