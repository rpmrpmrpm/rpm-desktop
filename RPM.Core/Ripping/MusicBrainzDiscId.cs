using System.Security.Cryptography;
using System.Text;

namespace RPM.Core.Ripping;

/// <summary>
/// Computes a MusicBrainz disc ID from a CD's table of contents. This is our own version of the libdiscid
/// algorithm, so no native library is needed. The disc ID is the key for <c>/ws/2/discid/{id}</c>, which
/// returns the release and its track list.
///
/// The algorithm hashes the ASCII text of the hex offsets, not the raw integers. Every offset includes
/// the 150-frame lead-in, which the drive's LBA leaves out.
/// </summary>
public static class MusicBrainzDiscId
{
    /// <summary>The 2-second lead-in that every CD has before track 1. A drive reports LBA. Every offset that
    /// MusicBrainz hashes is LBA plus this value.</summary>
    public const int LeadIn = 150;

    /// <summary>The gap that a data session leaves before its first track on an enhanced or CD-Extra disc:
    /// 60 s lead-out, 90 s lead-in and 2 s pre-gap, in frames. When a data track follows the audio tracks,
    /// this interval is subtracted from its address. That stands in for the lead-out.</summary>
    public const int XaInterval = (60 + 90 + 2) * DiscToc.FramesPerSecond; // 11400

    /// <summary>The fixed-size offset table the hash runs over: slot 0 is the lead-out, slots 1..99
    /// are tracks 1..99, and absent tracks are zero.</summary>
    public const int OffsetSlots = 100;

    /// <summary>Builds the 100-slot offset table. It applies two libdiscid rules. On an enhanced disc, the
    /// trailing data track (moved back by <see cref="XaInterval"/>) takes the place of the lead-out. A track
    /// that reports address 0 is clamped to the lead-in. Some copy-protected discs do this.</summary>
    public static int[] BuildOffsets(DiscToc toc)
    {
        var lastAudio = LastAudioTrack(toc);
        var offsets = new int[OffsetSlots];

        for (var number = toc.FirstTrack; number <= lastAudio; number++)
        {
            if (number is < 1 or >= OffsetSlots)
                continue;
            var track = toc.Tracks.FirstOrDefault(t => t.Number == number);
            offsets[number] = track is { Lba: > 0 } ? track.Lba + LeadIn : LeadIn;
        }

        // The audio session of an enhanced disc ends where the gap of its data session begins, not at the
        // physical lead-out. If this is wrong, every CD-Extra disc hashes to nothing.
        var trailingData = toc.Tracks.FirstOrDefault(t => t.Number == lastAudio + 1);
        offsets[0] = lastAudio < toc.LastTrack && trailingData is not null
            ? trailingData.Lba - XaInterval + LeadIn
            : toc.LeadoutLba + LeadIn;

        return offsets;
    }

    /// <summary>The disc's last audio track. A trailing data track is left out. The first track number is
    /// used as it is, even when that track is data, because MusicBrainz expects releases to start at track 1.</summary>
    public static int LastAudioTrack(DiscToc toc)
    {
        var lastAudio = toc.Tracks.Where(t => !t.IsData).Select(t => t.Number).DefaultIfEmpty(0).Max();
        return lastAudio == 0 ? toc.LastTrack : lastAudio;
    }

    /// <summary>The 28-character MusicBrainz disc ID.</summary>
    public static string Compute(DiscToc toc) =>
        Compute(toc.FirstTrack, LastAudioTrack(toc), BuildOffsets(toc));

    /// <summary>The disc ID from a prepared offset table. This is the form the algorithm is specified in, and
    /// the form the tests check.</summary>
    public static string Compute(int firstTrack, int lastTrack, int[] offsets)
    {
        if (offsets.Length != OffsetSlots)
            throw new ArgumentException($"offset table must have {OffsetSlots} slots", nameof(offsets));

        // 2 + 2 + 100 * 8 = 804 ASCII characters of uppercase hex, hashed as text.
        var sb = new StringBuilder(804);
        sb.Append(firstTrack.ToString("X2"));
        sb.Append(lastTrack.ToString("X2"));
        foreach (var offset in offsets)
            sb.Append(offset.ToString("X8"));

        var digest = SHA1.HashData(Encoding.ASCII.GetBytes(sb.ToString()));

        // MusicBrainz's URL-safe base64 variant.
        return Convert.ToBase64String(digest)
            .Replace('+', '.')
            .Replace('/', '_')
            .Replace('=', '-');
    }

    /// <summary>The TOC as MusicBrainz's <c>?toc=</c> query takes it: first track, last audio track, lead-out
    /// offset, then one offset per track. It is used for the fuzzy lookup that finds a release when the exact
    /// disc ID has never been submitted.</summary>
    public static string TocString(DiscToc toc, string separator = "+")
    {
        var lastAudio = LastAudioTrack(toc);
        var offsets = BuildOffsets(toc);

        var parts = new List<string>(lastAudio + 3) { toc.FirstTrack.ToString(), lastAudio.ToString(), offsets[0].ToString() };
        for (var number = toc.FirstTrack; number <= lastAudio; number++)
            parts.Add(offsets[number].ToString());

        return string.Join(separator, parts);
    }
}
