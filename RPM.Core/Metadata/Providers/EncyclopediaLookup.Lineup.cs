using System.Text.Json;

namespace RPM.Core.Metadata.Providers;

public sealed partial class EncyclopediaLookup
{
    /// <summary>
    /// The band's line-up as it stands, each member with their photograph: who has left is taken off.
    ///
    /// <para><b>"Left" is what Wikidata says, and only that.</b> A membership with an end date, on the
    /// band's own list of members (P527) or on the member's list of bands (P463), is one that ended. Where
    /// the two lists give different years, the later one counts: John Lennon's own entity ends his
    /// Beatles membership in 1969, the band's ends it in 1970. A member nobody wrote an end for stays: no
    /// date is not proof of leaving, and a line-up emptied by a gap in the data is worse than one that is
    /// a little long.</para>
    ///
    /// <para><b>A return needs a start.</b> A member with an ended membership has come back, and stays,
    /// only when an open membership starts in the year the old one ended or later. An open membership with
    /// no start beside an ended one is a gap in the data, not a return: the Beatles' Chas Newby and Tommy
    /// Moore each have one.</para>
    ///
    /// <para><b>A band that has split up keeps its last line-up.</b> Everybody's membership ended when the
    /// band did, and a card for the Beatles with nobody on it says nothing. So a membership that ended in
    /// the year the band did, or later, counts as the band's own ending, not as somebody leaving.</para>
    ///
    /// <para>One request for the band and every member at once. A member the catalogue names by their
    /// MusicBrainz id (the papers' MusicBrainz path does) is found first, all of them in one search of
    /// Wikidata's P434; Simon &amp; Garfunkel's two are Q4028 and Q4039 that way. Where the band has no entity
    /// of its own, the members still get their photographs, and nobody is taken off: there is nothing to
    /// read who left from. Where a request fails, the line-up is left as it came.</para>
    /// </summary>
    private async Task<IReadOnlyList<BandMember>?> LineupAsync(
        string? band, IReadOnlyList<BandMember>? members, CancellationToken ct)
    {
        if (members is not { Count: > 0 })
            return members;

        var bandId = IsEntity(band) ? band : null;
        var ids = members.Select(m => m.Mbid).Where(IsEntity).Select(id => id!).ToList();

        var mbids = members.Select(m => m.Mbid).Where(IsMbid).Select(id => id!).Distinct().Take(25).ToList();
        if (mbids.Count > 0)
        {
            var search = $"{WikidataApi}?action=query&list=search&format=json&srlimit=50&srprop="
                       + $"&srsearch={Uri.EscapeDataString("haswbstatement:" + string.Join('|', mbids.Select(m => "P434=" + m)))}";
            if ((await ReadAsync(search, SearchHits, ct)).Answers(out var hits, out _))
                ids.AddRange(hits);
        }

        ids = [.. ids.Distinct().Take(bandId is null ? 50 : 49)];
        if (ids.Count == 0)
            return members;

        var url = $"{WikidataApi}?action=wbgetentities&format=json&props=claims"
                + $"&ids={Uri.EscapeDataString(string.Join('|', bandId is null ? ids : ids.Prepend(bandId)))}";
        return (await ReadAsync(url, body => CurrentLineup(body, bandId, members), ct))
            .Match(current => current, _ => members);
    }

    private static bool IsEntity(string? id) => id is { Length: > 1 } && id[0] == 'Q' && id[1..].All(char.IsDigit);

    private static bool IsMbid(string? id) => id is { Length: 36 } && Guid.TryParse(id, out _);

    /// <summary>The members still in the band, with their photographs, read from one entity answer. See
    /// <see cref="LineupAsync"/> for the rules. <paramref name="band"/> null keeps everybody.</summary>
    internal static IReadOnlyList<BandMember> CurrentLineup(string body, string? band, IReadOnlyList<BandMember> members)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
            return members;

        // Which entity files each MusicBrainz id, so a member named by one is found by it.
        var byMbid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities.EnumerateObject())
            if (entity.Value.TryGetProperty("claims", out var c))
                foreach (var mbid in StringsOf(c, "P434"))
                    byMbid.TryAdd(mbid, entity.Name);

        // Each member's memberships from both sides: the latest year one ended in, and the start year of
        // each open one (null where none is written).
        var ended = new Dictionary<string, int?>(StringComparer.Ordinal);
        var open = new Dictionary<string, List<int?>>(StringComparer.Ordinal);
        int? bandEnded = null;

        if (band is not null && entities.TryGetProperty(band, out var bandEntity)
            && bandEntity.TryGetProperty("claims", out var bandClaims))
        {
            bandEnded = YearNumber(YearOf(bandClaims, "P576"));
            foreach (var (member, end) in Memberships(bandClaims, "P527", target: null))
                Note(member, end);
        }

        var shown = new List<BandMember>(members.Count);
        foreach (var member in members)
        {
            var id = IsEntity(member.Mbid) ? member.Mbid
                   : member.Mbid is { } mbid && byMbid.TryGetValue(mbid, out var found) ? found
                   : null;

            string? photo = null;
            if (id is not null && entities.TryGetProperty(id, out var person) && person.TryGetProperty("claims", out var claims))
            {
                if (band is not null)
                    foreach (var (_, end) in Memberships(claims, "P463", target: band))
                        Note(id, end);
                photo = StringsOf(claims, "P18").FirstOrDefault() is { } file
                    ? $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(file)}?width=240"
                    : null;
            }

            var left = id is not null && ended.TryGetValue(id, out var year) && !CameBack(id, year)
                       && !(bandEnded is { } split && year is { } y && y >= split);
            if (!left)
                shown.Add(member with { Photo = photo ?? member.Photo });
        }
        return shown;

        void Note(string member, (bool Ended, int? Year, int? Start) end)
        {
            if (!end.Ended)
                (open.TryGetValue(member, out var starts) ? starts : open[member] = []).Add(end.Start);
            else if (!ended.TryGetValue(member, out var known) || known is null || end.Year > known)
                ended[member] = end.Year ?? known;
        }

        // An end with no year cannot be placed, so any open membership with a start counts as a return.
        bool CameBack(string member, int? endYear)
            => open.TryGetValue(member, out var starts)
               && starts.Any(start => start is { } s && (endYear is null || s >= endYear));
    }

    /// <summary>
    /// The memberships one entity's claims state, as the entity each one points at, whether it ended (it
    /// has an end time, P582, even one whose date is "unknown"), the year it did, and the year it started
    /// (P580). For a member's list of bands (P463) only the statements about <paramref name="target"/> are
    /// wanted.
    /// </summary>
    private static IEnumerable<(string Value, (bool Ended, int? Year, int? Start) End)> Memberships(
        JsonElement claims, string property, string? target)
    {
        if (!claims.TryGetProperty(property, out var statements) || statements.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var s in statements.EnumerateArray())
        {
            if (!s.TryGetProperty("mainsnak", out var snak) || !snak.TryGetProperty("datavalue", out var dv)
                || !dv.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("id", out var idElement) || idElement.GetString() is not { } id)
                continue;
            if (target is not null && id != target)
                continue;

            var hasQualifiers = s.TryGetProperty("qualifiers", out var qualifiers);
            var start = hasQualifiers && qualifiers.TryGetProperty("P580", out var starts)
                        && starts.ValueKind == JsonValueKind.Array
                ? starts.EnumerateArray().Select(QualifierYear).FirstOrDefault(y => y is not null)
                : null;

            if (hasQualifiers && qualifiers.TryGetProperty("P582", out var ends) && ends.ValueKind == JsonValueKind.Array)
                yield return (id, (true, ends.EnumerateArray().Select(QualifierYear).FirstOrDefault(), start));
            else
                yield return (id, (false, null, start));
        }
    }

    /// <summary>The year of a time qualifier, or null where its date is "unknown".</summary>
    private static int? QualifierYear(JsonElement qualifier)
        => qualifier.TryGetProperty("datavalue", out var dv) && dv.TryGetProperty("value", out var v)
           && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("time", out var t)
            ? YearNumber(t.GetString()?.TrimStart('+').Split('-')[0])
            : null;

    private static int? YearNumber(string? year) => int.TryParse(year, out var y) ? y : null;
}
