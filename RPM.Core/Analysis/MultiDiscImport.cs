using RPM.Core.Models;

namespace RPM.Core.Analysis;

/// <summary>
/// How a multi-disc release is filed at import time, per the user's multi-disc setting.
///
/// <para>Merged (default): every disc keeps the one album name and lands in the one album folder, so a
/// disc ripped later joins the discs already imported. The caller forces the shared folder and reads
/// continuing numbers from <see cref="DiscNumbering"/>. Separate: each disc becomes its own album, and
/// its name includes the disc so the discs go into their own folders and do not collide.</para>
/// </summary>
public static class MultiDiscImport
{
    /// <summary>
    /// The album name for one disc in "merged album" mode: the name with a trailing disc marker
    /// removed, if the tagger added one.
    ///
    /// <para><b>Why this is needed.</b> Merging uses the album name as the key. If the tagger wrote
    /// the disc INTO that name, the set is already split: "The Wall (1)" and "The Wall (2)" are two
    /// album names, so nothing merges and a correctly tagged two-disc set ends up in two folders. The
    /// disc numbers on the tracks (disc 1 of 2 and disc 2 of 2) show that it is one set, and only the
    /// name says otherwise.</para>
    ///
    /// <para><b>Two guards: the name is changed only if BOTH hold.</b> The track must be part of a set
    /// of more than one disc, and the number in the marker must be that track's OWN disc number.
    /// Together they stop an album that ends in a number from being renamed. "Chapter 2" and
    /// "Volume 1" are single discs and never reach this code, and a "Volume 1" that is really disc 2
    /// of a set keeps its name because 1 is not 2.</para>
    ///
    /// <para>Case and separator do not matter: "(1)", "[Disc 1]", "- CD1" and ": Disk 1" all mean the
    /// same thing.</para>
    /// </summary>
    /// <param name="album">The album as tagged.</param>
    /// <param name="disc">The track's own disc number. Zero means it is not known, and nothing is
    /// removed.</param>
    /// <param name="discCount">How many discs the set holds. One or zero means there is no set, and
    /// nothing is removed.</param>
    public static string MergedAlbumName(string album, uint disc, uint discCount)
    {
        if (string.IsNullOrWhiteSpace(album) || disc == 0 || discCount <= 1)
            return album;

        var bare = DiscMarker.Replace(album.TrimEnd(), match =>
            uint.TryParse(match.Groups["n"].Value, out var n) && n == disc ? "" : match.Value);

        bare = bare.TrimEnd(' ', '\t', '-', '–', ':', ',');

        // Never return an empty name. An album tagged only "(Disc 1)" does not say which record it
        // is, and an empty album name would file the track loose under the artist, which is worse
        // than keeping the original name.
        return string.IsNullOrWhiteSpace(bare) ? album : bare;
    }

    /// <summary>
    /// A disc marker at the very end of an album name, with the number captured.
    ///
    /// <para>The pattern is anchored to the end, because a marker only counts at the end of the name:
    /// "Disc Two of the Mountain" is a title. The word is optional so a bare "(1)" is matched, but a
    /// bare number with no brackets is NOT matched. "Woodstock 2" is an album, and it cannot be
    /// told apart from a disc marker without the brackets or the word.</para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex DiscMarker = new(
        @"(?:[\s\-–:,]*[\(\[]\s*(?:dis[ck]|cd)?\s*(?<n>\d{1,2})\s*[\)\]]"
        + @"|[\s\-–:,]+(?:dis[ck]|cd)\s*(?<n>\d{1,2}))\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The album name for one disc in "separate albums" mode: the disc's own subtitle when the
    /// release names it (a subtitle reads better than "Disc 2"), otherwise "Album (Disc N)".</summary>
    public static string SeparateAlbumName(string album, uint disc, string? discSubtitle) =>
        !string.IsNullOrWhiteSpace(discSubtitle)
            ? $"{album}: {discSubtitle.Trim()}"
            : $"{album} (Disc {disc})";

    /// <summary>
    /// How a merged disc's file is NAMED, given every track of the record it joins: the queue's own
    /// rows and whatever the album folder already holds.
    ///
    /// <para>Merged filing puts every disc in one folder. A template that does not mention the disc
    /// would name disc 2 track 1 "01 - …" beside disc 1's "01 - …". The folder cannot sort those two
    /// files, and they collide when the two discs share a track title, which gives names such as
    /// "01 - Intro (2).flac". The files are therefore numbered the way the merged album counts, 1…n
    /// across the whole set, using <see cref="DiscNumbering"/>. Only the NAME changes: the tags keep
    /// the standard per-disc numbering, which the library uses to build its disc sections.</para>
    ///
    /// <para>Returns the track unchanged when the template does name the disc, or when there is no
    /// second disc to collide with.</para>
    /// </summary>
    public static TrackInfo NamedInMergedAlbum(
        TrackInfo track, IReadOnlyList<TrackInfo> albumTracks, Import.NamingTemplate template)
    {
        if (template.MentionsDisc || track.DiscNumber <= 1)
            return track;

        var continuing = DiscNumbering.ContinuingNumber(track, albumTracks);
        return continuing == track.TrackNumber ? track : track with { TrackNumber = continuing };
    }
}
