using RPM.Core.Models;

namespace RPM.Core.Analysis;

/// <summary>
/// The "one album, continuing track numbers" view of a multi-disc album, derived without changing the
/// per-disc numbers in the files' tags. Merged mode stores standard tags: disc 2 track 1 is
/// TRACKNUMBER 1, DISCNUMBER 2. This class turns that into the continuing number (for example 9)
/// that the merged album shows and files by.
/// </summary>
public static class DiscNumbering
{
    /// <summary>
    /// The track's number as the merged album counts it: its own number plus every track on the discs
    /// before it. A single-disc album (DiscNumber 0 or 1) counts as its own track number. A prior
    /// disc's size is its TrackCount tag, or the highest track number actually present when the tag is
    /// missing, so an incomplete rip still gets a sensible offset.
    /// </summary>
    public static uint ContinuingNumber(TrackInfo track, IEnumerable<TrackInfo> albumTracks)
    {
        if (track.DiscNumber <= 1 || track.TrackNumber == 0)
            return track.TrackNumber;

        uint offset = 0;
        foreach (var disc in albumTracks
                     .Where(t => t.DiscNumber > 0 && t.DiscNumber < track.DiscNumber)
                     .GroupBy(t => t.DiscNumber))
        {
            uint byTag = disc.Max(t => t.TrackCount);
            uint byPresent = disc.Max(t => t.TrackNumber);
            offset += Math.Max(byTag, byPresent);
        }

        return offset + track.TrackNumber;
    }
}
