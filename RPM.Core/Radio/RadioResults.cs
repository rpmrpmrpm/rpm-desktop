using RPM.Fingerprint.Catalog;
namespace RPM.Core.Radio;

/// <summary>
/// One row per station, taken from a directory answer that lists some stations more than once.
///
/// <para><b>What the directory returns.</b> Radio Browser lists a STREAM, not a station, and anybody
/// may add one. So a station arrives as rows in two shapes. One name can have several addresses: NPO
/// Radio 1 at MP3 192k and again at AAC 32k. One address can have several names: "NPO 2" and "NPO Radio
/// 2" are the same icecast mount, added twice. The list is a way of choosing what to listen to, and the
/// same station twice is not a useful choice. So both shapes are merged. See
/// <see cref="OnePerStation"/>.</para>
///
/// <para><b>Which row is kept.</b> The one with the best sound: a known bitrate beats an unknown one,
/// and a higher bitrate beats a lower one. <b>The logo is taken from whichever row has one.</b> This
/// matters because the entries for one station often disagree about artwork, as often as they disagree
/// about bitrate. Sometimes the worst stream is the one that carries the picture. Choosing one whole
/// row would mean choosing between a station that sounds right and one that looks right. Choosing the
/// stream, then giving it the logo, means neither is lost.</para>
///
/// <para><b>No station is dropped for having no logo.</b> A station the directory has no picture for is
/// still a station, and many stations in a country are in that state.</para>
/// </summary>
public static class RadioResults
{
    /// <summary>
    /// The answer to a genre search, with the stations the admin filed under that genre added to it.
    ///
    /// <para>The directory is searched for a genre by its own tags. So a station that the admin tagged
    /// with a genre the directory does not know is never in the answer. <paramref name="retagged"/> are
    /// those stations, fetched by uuid (<see cref="RadioBrowserClient.ByUuidAsync"/>). Each one is
    /// added only if it passes the rest of the query: the same country, and a name that contains the
    /// searched text (the directory's name or the admin's title). A station already in the answer is
    /// not added twice.</para>
    ///
    /// <para>The merged list is ordered by play count, most played first. That is the order the
    /// directory answers in, so the fold that follows (<see cref="OnePerStation"/>) keeps its usual
    /// first-row positions.</para>
    /// </summary>
    public static IReadOnlyList<RadioStation> WithRetagged(
        IReadOnlyList<RadioStation> answer, IReadOnlyList<RadioStation> retagged, RadioQuery query)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(retagged);
        ArgumentNullException.ThrowIfNull(query);

        var present = new HashSet<string>(answer.Select(s => s.Uuid), StringComparer.OrdinalIgnoreCase);
        var added = retagged
            .Where(s => present.Add(s.Uuid))
            .Where(s => query.CountryCode is not { Length: > 0 } code
                        || string.Equals(s.CountryCode, code, StringComparison.OrdinalIgnoreCase))
            .Where(s => query.Name is not { Length: > 0 } text
                        || s.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)
                        || (StationEdits.Of(s)?.Name?.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(s => query.Language is not { Length: > 0 } language
                        || (s.Language?.Contains(language.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (added.Count == 0)
            return answer;

        // A stable merge: the answer's own order is kept, and each added station goes in front of the
        // first answer row that has fewer plays than it.
        var merged = new List<RadioStation>(answer.Count + added.Count);
        var queue = new Queue<RadioStation>(added.OrderByDescending(s => s.ClickCount));
        foreach (var row in answer)
        {
            while (queue.Count > 0 && queue.Peek().ClickCount > row.ClickCount)
                merged.Add(queue.Dequeue());
            merged.Add(row);
        }
        merged.AddRange(queue);
        return merged;
    }

    /// <summary>
    /// The directory's answer with each station once, in the order it first appeared. The directory
    /// sorts by how often a station is played. Sorting again here would throw that order away for one
    /// of our own that nobody asked for.
    /// </summary>
    /// <remarks>
    /// Two rows are one station when they agree on EITHER key. See <see cref="NameKeyOf"/> and
    /// <see cref="StreamKeyOf"/>. Agreement is transitive, which is why both keys are useful. For
    /// example, on the Dutch page: "NPO Radio 2" at MP3 192 and "NPO 2" at MP3 192 are one address
    /// under two names. "NPO Radio 2" also appears at AAC 64 under the same name, and "NPO 2" appears
    /// at AAC 64 as well. Neither key joins all four rows on its own. Used together, they do.
    /// </remarks>
    public static IReadOnlyList<RadioStation> OnePerStation(IReadOnlyList<RadioStation> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);

        if (stations.Count < 2)
            return stations;

        // Work out which rows are the same station before any of them is merged. A merge writes a new
        // name and a new logo onto the survivor, so a key read from a merged row would come from
        // something the directory never said. Reading all the keys first makes the grouping depend only
        // on what the directory returned, in any order.
        var parent = new int[stations.Count];
        for (var i = 0; i < parent.Length; i++)
            parent[i] = i;

        var byName = new Dictionary<string, int>(stations.Count, StringComparer.Ordinal);
        var byStream = new Dictionary<string, int>(stations.Count, StringComparer.Ordinal);

        for (var i = 0; i < stations.Count; i++)
        {
            Join(byName, NameKeyOf(stations[i]), i);
            Join(byStream, StreamKeyOf(stations[i]), i);
        }

        var at = new Dictionary<int, int>(stations.Count);
        var kept = new List<RadioStation>(stations.Count);

        for (var i = 0; i < stations.Count; i++)
        {
            var group = Find(i);
            if (at.TryGetValue(group, out var seen))
            {
                kept[seen] = Better(kept[seen], stations[i]);
                continue;
            }

            at[group] = kept.Count;
            kept.Add(stations[i]);
        }

        return kept;

        // A row with no key of that kind is filed under nothing and stays on its own.
        void Join(Dictionary<string, int> index, string key, int row)
        {
            if (key.Length == 0)
                return;

            if (index.TryGetValue(key, out var first))
                Union(first, row);
            else
                index[key] = row;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb)
                return;

            // The lower row wins, so a group is named by the row the directory listed first. That is
            // the most played of its rows, and its position in the list is the one kept below.
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }

        int Find(int row)
        {
            while (parent[row] != row)
                row = parent[row] = parent[parent[row]];
            return row;
        }
    }

    /// <summary>
    /// The one to keep out of two entries for the same station: the better stream, with whichever logo
    /// exists.
    /// </summary>
    private static RadioStation Better(RadioStation kept, RadioStation other)
    {
        var winner = Rank(other) > Rank(kept) ? other : kept;

        // Every uuid in the group travels with the survivor. Otherwise the losers' ids leave the app
        // entirely. Anything that finds a station BY UUID, such as the hidden list the API serves,
        // would then miss the row for every way the directory files it except one.
        var filed = new List<string>(4);
        foreach (var source in new[] { kept, other })
        {
            foreach (var id in source.AlsoFiledAs.Append(source.Uuid))
            {
                if (!string.Equals(id, winner.Uuid, StringComparison.OrdinalIgnoreCase)
                    && !filed.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    filed.Add(id);
                }
            }
        }

        // Also keep EVERY picture either of them had, not only the winner's. The row with a working
        // picture is often the 32k stream. The choice is not settled here, because only the host can
        // tell which picture a decoder can turn into an image. For example, the best NPO Radio 1 stream
        // points at a Wikimedia file that returns 403 to a request with no browser behind it, and
        // nothing in the URL shows that. So they all travel, best guess first, and the row tries them
        // in order (see RadioStation.OtherLogos).
        var logos = new List<string>(4);
        var votes = new Dictionary<string, int>(4, StringComparer.OrdinalIgnoreCase);

        // The winner is added first for ORDER only, and is not counted. It is one of the two below.
        // Counting it twice would make the vote say only that the better stream won. That is a fact
        // about the audio, not about the picture.
        Gather(winner, counts: false);
        Gather(kept, counts: true);
        Gather(other, counts: true);

        // No logo anywhere is worth trying, so the winner keeps its own tag. The row shows its
        // placeholder icon either way, and the field still holds the directory's answer.
        if (logos.Count == 0)
            return winner with { AlsoFiledAs = filed };

        // Best guess first, so the usual row loads its logo on the first request, instead of after two
        // that were never going to work. This only sets the ORDER. Nothing is dropped, and the row
        // tries the rest if the guess is wrong.
        //
        // First, the ranker judges each address on its own, against the winner's own site. That is what
        // lets a broadcaster's own copy of their logo beat a third party's copy. See RadioLogo.Rank.
        //
        // Then AGREEMENT. This is the one test that is about the fold rather than about a single
        // address, and the ranker cannot see it. Where a station's entries name two pictures the ranker
        // cannot tell apart, the picture named by more entries is the better bet. For example, the
        // entries for 102.7 KIIS FM name the station's wordmark and an unrelated letter. Both are on
        // the same host, both are mirrors, and both score the same. Agreement is applied under the
        // ranker, not over it, because two contributors agreeing is weaker evidence than the
        // broadcaster's own site. They could have copied each other.
        //
        // Votes are counted per PICTURE, not per URL. One asset is often filed at three sizes, and
        // three different assets are often filed. Those are not the same disagreement. See
        // RadioLogo.SamePicture.
        //
        // This is a floor, not a full count, because the merge works in pairs. A picture that two
        // entries agreed on arrives at the next merge as one candidate with one vote. A picture only
        // gains votes when it is named again, so a low count never points the wrong way. It just fails
        // to point.
        //
        // OrderByDescending, not List.Sort: this sort is stable. Candidates that neither test can tell
        // apart keep the order the directory gave them.
        var best = logos.Count == 1
            ? logos
            : logos.OrderByDescending(url => RadioLogo.Rank(url, winner.Homepage))
                   .ThenByDescending(url => votes.GetValueOrDefault(RadioLogo.SamePicture(url)))
                   .ToList();

        return winner with
        {
            Favicon = best[0],
            OtherLogos = best.GetRange(1, best.Count - 1),
            AlsoFiledAs = filed,
        };

        void Gather(RadioStation from, bool counts)
        {
            Take(from.Favicon, counts);
            foreach (var url in from.OtherLogos)
                Take(url, counts);
        }

        void Take(string? url, bool counts)
        {
            if (!RadioLogo.IsDrawable(url))
                return;

            // The vote is cast before the duplicate check, and the check is on the ADDRESS. Two entries
            // that ask one host for one asset at two sizes make one candidate to try, and they count as
            // two entries agreeing.
            if (counts)
            {
                var picture = RadioLogo.SamePicture(url);
                votes[picture] = votes.GetValueOrDefault(picture) + 1;
            }

            if (!logos.Contains(url!, StringComparer.OrdinalIgnoreCase))
                logos.Add(url!);
        }
    }

    /// <summary>How good a stream is, as one number, so two streams can be compared without a chain of
    /// tie-breaks at the call site.</summary>
    /// <remarks>
    /// Reachability counts for more than bitrate. The directory's checker tried the stream, and a 320k
    /// stream it could not open is a worse offer than a 96k stream it could. Bitrate is used after
    /// that, since it is the question being asked. A station that declares no bitrate ranks below every
    /// reachable station that declares one. It is not treated as zero. Otherwise the choice would
    /// silently favour whichever stream with no stated bitrate came back first.
    /// </remarks>
    private static long Rank(RadioStation station)
        => (station.Reachable ? 1_000_000 : 0)
           + (station.BitrateKbps > 0 ? 1_000 + station.BitrateKbps : 0);

    /// <summary>
    /// Which of these rows are the same station, as one number per row.
    ///
    /// <para>This is the grouping that <see cref="OnePerStation"/> merges on. It is offered on its own
    /// so that code outside the app can get the same answer. <c>tools/StationLogos</c> is one caller.
    /// The admin lists the directory's ROWS, but the app shows STATIONS. Hiding one row then hid a
    /// station the app never had: Alswin Ambient Music is filed twice on one stream, and only one of
    /// the two rows is inside the 120 that the app asks for. Stamping the group on each row lets the
    /// admin hide all of them.</para>
    ///
    /// <para>The grouping lives here, and the logo walk shares it. A second copy of "the same station"
    /// would eventually disagree with this one. The disagreement would then show up as a station that
    /// cannot be hidden.</para>
    /// </summary>
    /// <returns>A group number for each station, in the order given. Two rows have the same number when
    /// they are one station.</returns>
    /// <remarks>
    /// <b>Do not use this to decide anything about a station that a person has not looked at.</b> The
    /// grouping is transitive across two keys, so one badly filed row can merge two stations, and every
    /// station either of them touches. For example, <c>icecast.omroep.nl/radio2-bb-aac</c> is NPO Radio
    /// 2's stream, but one row files it under the name "NPO Radio 1". That put all seven NPO Radio 1
    /// rows in NPO 2's group, and hid a station nobody had asked to hide. This is acceptable when the
    /// answer is "draw these rows as one", which is what this method is for. The worst case is one row
    /// too few on a page that someone is reading. It is not acceptable as the basis of a decision that
    /// sticks. Use <see cref="SameStreamGroupsOf"/> for those.
    /// </remarks>
    public static int[] GroupsOf(IReadOnlyList<RadioStation> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);

        var parent = new int[stations.Count];
        for (var i = 0; i < parent.Length; i++)
            parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        void Join(Dictionary<string, int> seen, string key, int i)
        {
            if (key.Length == 0)
                return;

            if (seen.TryGetValue(key, out var first))
            {
                var a = Find(first);
                var b = Find(i);
                if (a != b)
                    parent[a] = b;
            }
            else
            {
                seen[key] = i;
            }
        }

        var byName = new Dictionary<string, int>(stations.Count, StringComparer.Ordinal);
        var byStream = new Dictionary<string, int>(stations.Count, StringComparer.Ordinal);

        for (var i = 0; i < stations.Count; i++)
        {
            Join(byName, NameKeyOf(stations[i]), i);
            Join(byStream, StreamKeyOf(stations[i]), i);
        }

        var groups = new int[stations.Count];
        for (var i = 0; i < groups.Length; i++)
            groups[i] = Find(i);

        return groups;
    }

    /// <summary>
    /// Which of these rows are the same stream, as one number per row.
    ///
    /// <para><b>The conservative grouping. Decisions that last are made on this one.</b> Two rows with
    /// the same address are the same station, and there is nothing to argue about. There is no name
    /// matching, so nothing chains. A station filed under two different streams comes out as two
    /// groups. That errs toward doing too little.</para>
    ///
    /// <para>This is the right direction for hiding. <see cref="GroupsOf"/> reaches further, which
    /// suits drawing a list, where reaching too far costs one row. Here the cost is a station nobody
    /// can hear. Hiding by name can hide the wrong station, which is why the difference
    /// matters.</para>
    /// </summary>
    public static int[] SameStreamGroupsOf(IReadOnlyList<RadioStation> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);

        var groups = new int[stations.Count];
        var first = new Dictionary<string, int>(stations.Count, StringComparer.Ordinal);

        for (var i = 0; i < stations.Count; i++)
        {
            var key = StreamKeyOf(stations[i]);

            // No address to compare: its own group, never anybody else's.
            groups[i] = key.Length == 0
                ? i
                : first.TryGetValue(key, out var seen) ? seen : first[key] = i;
        }

        return groups;
    }

    /// <summary>
    /// The first of the two keys: the folded name, within one country. See
    /// <see cref="RadioNames.Fold"/>, which removes the specification, the punctuation, the accents and
    /// the words that say "this is a radio station".
    /// </summary>
    /// <remarks>
    /// <para>This is the key for one station listed at several bitrates. That is the usual form of a
    /// duplicate. "NPO Radio 2" at MP3 192, at AAC 64 and at AAC 32 is three rows and three addresses
    /// under one name. The name is folded rather than only squashed, because one name is rarely spelled
    /// one way. "Qmusic", "Q-Music", "Q music Nederland" and "QMUSIC NEDERLAND" are one station, and so
    /// are "Heart", "Heart FM" and "Heart UK".</para>
    /// <para>The country is part of the key, because a name is only unique within one country. A search
    /// with no country filter finds a Radio 1 in a dozen countries, and those are a dozen stations.
    /// Merging them would delete eleven broadcasters from the list to tidy up a duplicate that is not
    /// there.</para>
    /// </remarks>
    private static string NameKeyOf(RadioStation station)
    {
        var name = RadioNames.Fold(station.Name, station.CountryCode);
        return name.Length == 0 ? "" : $"{name}|{Country(station)}";
    }

    /// <summary>
    /// The second key: the stream's address, within one country. The parts that are not the address are
    /// removed: the scheme, a leading "www.", a default port and a trailing slash.
    /// </summary>
    /// <remarks>
    /// <para><b>What it finds that the name does not.</b> The same stream can be added twice under two
    /// names. Measured against the live directory, the Dutch page's "NPO 2" and "NPO Radio 2" are both
    /// <c>icecast.omroep.nl/radio2-bb-mp3</c>. "3FM", "NPO 3FM" and "NPO 3FM Serious Radio" are all
    /// <c>icecast.omroep.nl/3fm-bb-mp3</c>. "NPO Klassiek" and "NPO Radio 4" are both
    /// <c>icecast.omroep.nl/radio4-bb-mp3</c>. In a sample of 2,020 rows from five searches, the folded
    /// name alone leaves 1,799 stations, and the address alone leaves 1,926. Together they leave 1,751.
    /// Each key reaches rows that the other cannot: the address reaches 48 more, and the name reaches
    /// 175 more.</para>
    ///
    /// <para><b>Why it is safe to merge on.</b> Two rows with one address are one stream. Whichever one
    /// survives plays the audio that both rows describe. That is a stronger claim than the name key can
    /// make.</para>
    ///
    /// <para><b>What it gets wrong.</b> A row whose address the directory has filed against the wrong
    /// station. In the same sample, one case in 2,020: an entry for Hot 99.5 Washington names the
    /// address of Z100 New York, so the two merge. The cost is one station missing from a page. The row
    /// that stays plays what its address says.</para>
    ///
    /// <para><b>The query string is kept.</b> Some directories put the station's identity in the query.
    /// <c>playerservices.streamtheworld.com</c> is one host for hundreds of stations. Dropping the
    /// query would merge broadcasters that share nothing but a CDN.</para>
    ///
    /// <para><b>The country is part of the key</b>, for the same reason the name key has it. CNN and
    /// CNN UK share one address in two countries, and the app treats them as two stations everywhere
    /// else.</para>
    /// </remarks>
    private static string StreamKeyOf(RadioStation station)
    {
        if (!Uri.TryCreate(station.StreamUrl, UriKind.Absolute, out var url) || url.Host.Length == 0)
            return "";

        var host = url.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        // The scheme is dropped. http and https for one address are one stream, and the directory's
        // entries for a station often differ on which scheme they use.
        var port = url.IsDefaultPort ? "" : $":{url.Port}";
        return $"{host}{port}{url.AbsolutePath.TrimEnd('/')}{url.Query}|{Country(station)}";
    }

    private static string Country(RadioStation station)
        => station.CountryCode?.Trim().ToLowerInvariant() ?? "";
}
