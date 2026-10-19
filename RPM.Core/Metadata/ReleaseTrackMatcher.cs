using RPM.Core.Metadata.Providers;

namespace RPM.Core.Metadata;

/// <summary>The place a row says it has on the record, used to match it against a listing.</summary>
/// <param name="Disc">Its disc number, or 0 when the row does not say.</param>
/// <param name="Track">Its number on that disc, or 0 when the row does not say.</param>
/// <param name="Title">The title the row has now, or null. Used only to confirm a match, never to make
/// one.</param>
public readonly record struct TrackPlace(uint Disc, uint Track, string? Title);

/// <summary>
/// Pairs a record's files with a catalogue listing, so that a chosen release can name them.
///
/// <para>There are two ways to match, in order of how much they can be trusted. <b>By number</b>: a row
/// that says it is disc 2, track 1 is matched to the listing's disc 2, track 1. <b>By order</b>: when no
/// row at all has a track number, the files are taken in the order they were given, disc by disc. This is
/// the case for an untagged rip, where the file order is the running order. It is not used when some rows
/// have numbers, because then the numbers are the evidence and a gap in them means something.</para>
///
/// <para>A title is used to choose between candidates, never to reject the only one. The caller has just
/// picked this release for these files and asked for their names to come from it. Refusing because the name
/// on the file is different would refuse exactly the rename that was asked for. A rip full of "Track 01"
/// would then match nothing at all.</para>
///
/// <para>It never guesses past what it knows. A row it cannot place stays unmatched and keeps what it
/// already says. The caller reports how many rows were matched, because "18 of 20" tells the user that they
/// may have picked the wrong pressing.</para>
/// </summary>
public static class ReleaseTrackMatcher
{
    /// <summary>
    /// The listing row for each place, in the same order, or null where no row could be trusted.
    /// </summary>
    public static IReadOnlyList<ReleaseTrackInfo?> Match(
        IReadOnlyList<TrackPlace> places, IReadOnlyList<ReleaseTrackInfo> listing) =>
        Match(places, listing, out _);

    /// <param name="discNumbersIgnored">
    /// True when the files' own disc numbers were found to be unusable and were thrown away. See <see
    /// cref="ByOrder"/>. The caller has to know this, because those numbers are then also wrong on the
    /// files. Writing the listing's titles to a record while every file still says it is on disc one would
    /// be a half repair.
    /// </param>
    /// <inheritdoc cref="Match(IReadOnlyList{TrackPlace}, IReadOnlyList{ReleaseTrackInfo})"/>
    public static IReadOnlyList<ReleaseTrackInfo?> Match(
        IReadOnlyList<TrackPlace> places, IReadOnlyList<ReleaseTrackInfo> listing,
        out bool discNumbersIgnored)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(listing);

        discNumbersIgnored = false;
        if (places.Count == 0 || listing.Count == 0)
            return [.. places.Select(_ => (ReleaseTrackInfo?)null)];

        return places.All(p => p.Track == 0)
            ? ByOrder(places, listing, out discNumbersIgnored)
            : [.. places.Select(p => ByNumber(p, listing))];
    }

    /// <summary>Both sides know where the track sits. The numbers do the matching and the titles confirm
    /// it.</summary>
    private static ReleaseTrackInfo? ByNumber(TrackPlace place, IReadOnlyList<ReleaseTrackInfo> listing)
    {
        if (place.Track == 0)
            return null;

        // The per-disc number is the one printed on the sleeve. On a single-disc listing the flat position
        // is the same number.
        var candidates = listing
            .Where(t => (t.DiscPosition == 0 ? t.Position : t.DiscPosition) == place.Track)
            .Where(t => place.Disc == 0 || t.Disc == 0 || t.Disc == place.Disc)
            .ToList();

        if (candidates.Count == 1)
            return candidates[0];

        // Several rows have that number. This happens with a set whose listing does not say which disc each
        // row is on. Only a title can choose between them.
        var title = Blank(place.Title);
        return title is null
            ? null
            : candidates.FirstOrDefault(t =>
                string.Equals(Blank(t.Title), title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>No row has a number, so the files are the running order. Matching is done disc by disc. A
    /// two-disc set whose discs are known but whose tracks are not still lands on the right half of the
    /// listing.</summary>
    private static IReadOnlyList<ReleaseTrackInfo?> ByOrder(
        IReadOnlyList<TrackPlace> places, IReadOnlyList<ReleaseTrackInfo> listing,
        out bool discNumbersIgnored)
    {
        discNumbersIgnored = false;
        var matched = new ReleaseTrackInfo?[places.Count];
        var byPlaceDisc = places.Select((p, i) => (p.Disc, Index: i)).GroupBy(x => x.Disc).ToList();
        bool listingNamesDiscs = listing.Any(t => t.Disc > 0);

        // The files give disc numbers, but the listing does not. There is no way to tell which half of the
        // record the second folder is. Taking the listing from the top for each folder would name both
        // discs after the first one.
        if (!listingNamesDiscs && byPlaceDisc.Count > 1)
            return matched;

        // The files' disc numbers are only used if the listing can support them. A disc the listing does
        // not have, or a disc given more files than it has tracks, is a wrong tag. This is the most common
        // wrong tag: a ripper that wrote "disc 1" on all twenty-four files of a double album, or left the
        // field empty when the listing has two discs.
        //
        // Using those numbers anyway gave a bad result on exactly these records. All twenty-four files said
        // disc one, so they were matched against disc one's twelve tracks. The first twelve got their
        // titles, and the other twelve matched nothing and were left alone. All of them still said disc one
        // afterwards.
        //
        // So when the numbers cannot be used, they are ignored. The whole selection is read as the running
        // order of the record, disc by disc. That is what a folder of files in order is. This is done only
        // when the counts match exactly. The files being that release is the evidence for overriding what
        // they say about themselves, and a partial selection is not that evidence.
        if (listingNamesDiscs && places.Count == listing.Count
            && byPlaceDisc.Any(g => listing.Count(t => t.Disc == g.Key) < g.Count()))
        {
            discNumbersIgnored = true;
            var whole = listing
                .OrderBy(t => t.Disc)
                .ThenBy(t => t.DiscPosition == 0 ? t.Position : t.DiscPosition)
                .ToList();
            for (var i = 0; i < places.Count; i++)
                matched[i] = whole[i];
            return matched;
        }

        foreach (var disc in byPlaceDisc)
        {
            var side = (listingNamesDiscs
                    ? listing.Where(t => t.Disc == disc.Key)
                    : listing)
                .OrderBy(t => t.DiscPosition == 0 ? t.Position : t.DiscPosition)
                .ToList();

            var rows = disc.Select(x => x.Index).ToList();
            for (var i = 0; i < rows.Count && i < side.Count; i++)
                matched[rows[i]] = side[i];
        }
        return matched;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
