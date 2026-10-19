using RPM.Fingerprint;

namespace RPM.Core.Audio;

/// <summary>How many records are put on the phone ahead of the needle.
///
/// <para>If the next record is streamed only when the current one is nearly over, the album stops wherever
/// the connection does. In a lift, in a tunnel, on a platform, or in a dead spot on a motorway, the stream
/// behind the needle dies, and there is nothing on the phone to carry on with. A file that is already on
/// disk does not have this problem, and playback already prefers one when it is there.
/// </para>
///
/// <para>So the question is how many. The answer cannot be a fixed count. A record is four megabytes at
/// 128 kbps and thirty-five megabytes as uploaded. "Three ahead" means twelve megabytes for one listener
/// and a hundred for another, on a phone, on somebody's data plan. So the budget is in bytes, and the count
/// follows from it.
/// </para>
/// </summary>
public static class RecordsAhead
{
    /// <summary>What may be held ahead of the needle on wifi. Three records at any rendition rate, or two
    /// as uploaded. This is enough that the budget decides the count and not the ceiling. It is small
    /// enough that nobody notices it on the storage screen.</summary>
    public const long OnWifi = 75L * 1024 * 1024;

    /// <summary>On a mobile data plan. One record ahead at any rate the listener has chosen. This covers a
    /// tunnel. The aim is not to have the whole afternoon in hand. The aim is to not stop.</summary>
    public const long OnCellular = 40L * 1024 * 1024;

    /// <summary>However large the budget, never more than this. Beyond it, the fetches compete with the
    /// record playing for a connection that somebody is listening to.</summary>
    public const int Most = 3;

    /// <summary>The size of the file as uploaded, in kilobits per second.
    ///
    /// <para>This is an estimate, and it has to be one. The store knows the size, but the phone decides
    /// before it asks. FLAC at CD rate comes out a little over half of 1411. Being out by a third only
    /// changes the count by one record.</para>
    /// </summary>
    public const int OriginalKilobits = 900;

    /// <summary>What one record weighs, in bytes.</summary>
    public static long Weighs(double seconds, int kilobits)
    {
        if (seconds <= 0) return 0;

        var rate = kilobits == AudioLadder.Original ? OriginalKilobits : Math.Max(1, kilobits);
        return (long)(seconds * rate * 1000 / 8);
    }

    /// <summary>How many of the coming records fit the budget, in order.
    /// </summary>
    /// <param name="seconds">The lengths of the records after the one playing, nearest first. A length the
    /// catalogue does not know is zero. It counts as one whole record's worth, not as free. A track of
    /// unknown size is not a track to fetch three of.</param>
    /// <param name="kilobits">What they will be fetched at.</param>
    /// <param name="cellular">Whose bytes these are.</param>
    public static int Count(IEnumerable<double> seconds, int kilobits, bool cellular)
    {
        ArgumentNullException.ThrowIfNull(seconds);

        var budget = cellular ? OnCellular : OnWifi;
        var taken = 0;
        long spent = 0;

        foreach (var length in seconds)
        {
            if (taken >= Most) break;

            // A length nobody knows is charged at four minutes, which is what a record is.
            var weight = Weighs(length > 0 ? length : 240, kilobits);

            // The first one is always allowed, whatever it weighs. Refusing it would mean a phone set to
            // the original file fetches nothing ahead at all. That is the setting that needs it most,
            // because its stream is the hardest to set up again quickly.
            if (taken > 0 && spent + weight > budget) break;

            spent += weight;
            taken++;
        }

        return taken;
    }
}
