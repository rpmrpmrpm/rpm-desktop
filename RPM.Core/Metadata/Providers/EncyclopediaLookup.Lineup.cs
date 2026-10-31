using System.Text.Json;

namespace RPM.Core.Metadata.Providers;

public sealed partial class EncyclopediaLookup
{
    /// <summary>
    /// The band's line-up as it stands, each member with their photograph: who has left is taken off.
    ///
    /// <para><b>"Left" is what Wikidata says, and only that.</b> A membership with an end date, on the
    /// band's own list of members (P527) or on the member's list of bands (P463), is one that ended. A
    /// member with an ended membership and an open one has come back, and stays. A member nobody wrote an
    /// end for stays too: no date is not proof of leaving, and a line-up emptied by a gap in the data is
    /// worse than one that is a little long.</para>
    ///
    /// <para><b>A band that has split up keeps its last line-up.</b> Everybody's membership ended when the
    /// band did, and a card for the Beatles with nobody on it says nothing. So a membership that ended in
    /// the year the band did, or later, counts as the band's own ending, not as somebody leaving.</para>
    ///
    /// <para>One request, for the band and every member at once. A member the catalogue identified only by
    /// a MusicBrainz id has no entity here; they keep their place and go without a photograph. Where the
    /// request fails, the line-up is left exactly as it came.</para>
    /// </summary>
    private async Task<IReadOnlyList<BandMember>?> LineupAsync(
        string? band, IReadOnlyList<BandMember>? members, CancellationToken ct)
    {
        if (band is not { Length: > 1 } || band[0] != 'Q' || members is not { Count: > 0 })
            return members;

        var ids = members.Select(m => m.Mbid).Where(IsEntity).Select(id => id!).Distinct().Take(49).ToList();
        if (ids.Count == 0)
            return members;

        var url = $"{WikidataApi}?action=wbgetentities&format=json&props=claims"
                + $"&ids={Uri.EscapeDataString(string.Join('|', ids.Prepend(band)))}";
        return (await ReadAsync(url, body => CurrentLineup(body, band, members), ct))
            .Match(current => current, _ => members);
    }

    private static bool IsEntity(string? id) => id is { Length: > 1 } && id[0] == 'Q' && id[1..].All(char.IsDigit);

    /// <summary>The members still in the band, with their photographs, read from one entity answer. See
    /// <see cref="LineupAsync"/> for the rules.</summary>
    internal static IReadOnlyList<BandMember> CurrentLineup(string body, string band, IReadOnlyList<BandMember> members)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("entities", out var entities))
            return members;

        // Each member's memberships: ended (with the year) or open, from both sides.
        var ended = new Dictionary<string, int?>(StringComparer.Ordinal);
        var open = new HashSet<string>(StringComparer.Ordinal);
        int? bandEnded = null;

        if (entities.TryGetProperty(band, out var bandEntity) && bandEntity.TryGetProperty("claims", out var bandClaims))
        {
            bandEnded = YearNumber(YearOf(bandClaims, "P576"));
            foreach (var (member, end) in Memberships(bandClaims, "P527", target: null))
                Note(member, end);
        }

        var shown = new List<BandMember>(members.Count);
        foreach (var member in members)
        {
            string? photo = null;
            if (member.Mbid is { } id && IsEntity(id) && entities.TryGetProperty(id, out var person)
                && person.TryGetProperty("claims", out var claims))
            {
                foreach (var (_, end) in Memberships(claims, "P463", target: band))
                    Note(id, end);
                photo = StringsOf(claims, "P18").FirstOrDefault() is { } file
                    ? $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(file)}?width=240"
                    : null;
            }

            var left = member.Mbid is { } key && ended.TryGetValue(key, out var year) && !open.Contains(key)
                       && !(bandEnded is { } split && year is { } y && y >= split);
            if (!left)
                shown.Add(member with { Photo = photo ?? member.Photo });
        }
        return shown;

        void Note(string member, (bool Ended, int? Year) end)
        {
            if (end.Ended)
                ended[member] = end.Year;
            else
                open.Add(member);
        }
    }

    /// <summary>
    /// The memberships one entity's claims state, as the entity each one points at, whether it ended (it
    /// has an end time, P582, even one whose date is "unknown") and the year it did. For a member's list
    /// of bands (P463) only the statements about <paramref name="target"/> are wanted.
    /// </summary>
    private static IEnumerable<(string Value, (bool Ended, int? Year) End)> Memberships(
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

            if (s.TryGetProperty("qualifiers", out var qualifiers) && qualifiers.TryGetProperty("P582", out var ends)
                && ends.ValueKind == JsonValueKind.Array)
                yield return (id, ends.EnumerateArray().Select(EndYear).FirstOrDefault((true, null)));
            else
                yield return (id, (false, null));
        }
    }

    private static (bool Ended, int? Year) EndYear(JsonElement qualifier)
        => qualifier.TryGetProperty("datavalue", out var dv) && dv.TryGetProperty("value", out var v)
           && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("time", out var t)
           && YearNumber(t.GetString()?.TrimStart('+').Split('-')[0]) is { } year
            ? (true, year)
            : (true, null);

    private static int? YearNumber(string? year) => int.TryParse(year, out var y) ? y : null;
}
