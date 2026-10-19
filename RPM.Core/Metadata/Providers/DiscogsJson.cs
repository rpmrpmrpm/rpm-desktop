using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// Reads what Discogs returns.
///
/// <para>This is pure, and separate from the provider that fetches the data. Fetching needs a signed-in
/// account and a Lambda function behind it. Reading needs only a string. This is the half that gets things
/// wrong. Discogs' two endpoints disagree about almost every field. The year is a number on a release and a
/// string in a search. The artist appears only at the front of the title. A tracklist can hold entries that
/// are not tracks. A track is credited only when its artist differs from the record's artist.</para>
/// </summary>
public static class DiscogsJson
{
    public const string ProviderId = "discogs";

    /// <summary>Reads a <c>/database/search</c> answer.</summary>
    public static IReadOnlyList<ReleaseSearchResult> ParseSearch(string json, int limit = 25)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<ReleaseSearchResult>();
        foreach (var r in results.EnumerateArray())
        {
            // A master is the abstract album, such as "Rumours" as a whole. The results wanted are
            // pressings, which are releases.
            if (Text(r, "type") is { } type && !type.Equals("release", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!r.TryGetProperty("id", out var idEl) || IdOf(idEl) is not { } id)
                continue;

            var (artist, title) = SplitTitle(Text(r, "title") ?? "");

            found.Add(new ReleaseSearchResult(
                Ref: new ReleaseRef(ProviderId, id),
                Title: title,
                Artist: artist,
                Year: Year(r),
                Date: Text(r, "year"),
                Country: Text(r, "country"),
                Format: Join(r, "format"),
                TrackCount: 0,   // the search does not say; the listing does
                Label: First(r, "label"),
                CatalogNumber: Text(r, "catno"),
                Note: null,
                // Discogs orders by relevance but publishes no score, so position is the only signal.
                Relevance: Relevance(found.Count, limit),
                // Each result comes with a cover, so no second request is needed.
                CoverUrl: Text(r, "thumb") ?? Text(r, "cover_image")));
        }
        return found;
    }

    /// <summary>Reads a <c>/releases/{id}</c> answer: the tracklist and the track times.</summary>
    public static ReleaseDetail ParseRelease(string json, string releaseId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Discogs credits a TRACK only when its artist differs from the release's. That happens on a
        // compilation, a split or a guest. On an ordinary album the artist is stated once, on the release,
        // and every track's own credit is absent. Reading only the track would leave every row without a
        // name, on exactly the records most people own.
        var releaseArtist = Artists(root);

        var tracks = new List<ReleaseTrackInfo>();
        uint discCount = 0;
        if (root.TryGetProperty("tracklist", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            uint overall = 0;
            var perDisc = new Dictionary<uint, uint>();
            var discSubtitles = new Dictionary<uint, string>();
            var positions = new List<string>();
            var ownCredits = new List<DiscogsCredits.Roles>();

            foreach (var row in Flatten(list))
            {
                // Discogs positions read "1-1", "2-3" or "CD2-05" on a multi-disc set, and a bare "1" or
                // "A1" on a single one. Only the compound form names a disc.
                var position = Text(row.Element, "position");
                uint disc = ParseDiscNumber(position);
                uint discPos = perDisc.TryGetValue(disc, out var c) ? c + 1 : 1;
                perDisc[disc] = discPos;
                if (disc > discCount)
                    discCount = disc;
                if (disc > 0 && row.Heading is { } ph)
                    discSubtitles.TryAdd(disc, ph);

                var title = Text(row.Element, "title") ?? "";
                positions.Add(position ?? "");

                // A credit written on the track, or on the work it belongs to, is about this track and
                // nothing else. It needs no range to resolve, and it takes priority over what the release
                // says.
                ownCredits.Add(DiscogsCredits.Of(row.Element)
                    .Over(row.Parent is { } parent ? DiscogsCredits.Of(parent) : default));

                tracks.Add(new ReleaseTrackInfo(
                    ++overall,
                    ReleaseTrackInfo.TitleOf(row.Work, title),
                    Artists(row.Element) ?? releaseArtist,
                    Duration(Text(row.Element, "duration")),
                    Disc: disc,
                    DiscPosition: discPos,
                    Work: row.Work,
                    MovementName: row.Work is null ? null : Blank(title),
                    MovementNumber: row.Movement,
                    MovementCount: row.MovementCount));
            }

            // The credits the release states for itself. These are resolved last. A release-level credit
            // names the tracks it covers by position, such as "12-1 to 12-7". No position is known until
            // the whole listing has been read.
            var shared = DiscogsCredits.Sheet(root);
            for (var i = 0; i < tracks.Count; i++)
            {
                var roles = ownCredits[i].Over(shared.For(i, positions));
                if (!roles.IsEmpty)
                    tracks[i] = roles.Applied(tracks[i]);
            }

            // A single disc (no compound positions, or all "1-x") carries no disc number. This matches
            // MusicBrainz, where a one-disc release reports Disc 0. A real multi-disc set gets its
            // subtitles.
            if (discCount > 1)
            {
                tracks = tracks
                    .Select(t => t.Disc > 0 && discSubtitles.TryGetValue(t.Disc, out var s)
                        ? t with { DiscSubtitle = s }
                        : t)
                    .ToList();
            }
            else
            {
                tracks = tracks.Select(t => t with { Disc = 0 }).ToList();
            }
        }

        var (label, catalogNumber) = LabelInfo(root);
        var kind = Choice(root);

        return new ReleaseDetail(
            new ReleaseRef(ProviderId, releaseId),
            Text(root, "title") ?? "",
            releaseArtist,
            Year(root),
            Text(root, "country"),
            Formats(root),
            tracks,
            DiscCount: discCount > 1 ? discCount : (tracks.Count > 0 ? 1u : 0u),
            Barcode: Barcode(root),
            Label: label,
            CatalogNumber: catalogNumber,
            Genre: kind.IsKnown ? kind.Genre : null,
            Subgenre: kind.Subgenre,
            Photos: PhotoCount(root));

        // Nothing here sets the advisory flag or the sort names, because Discogs has no field for either. A
        // release adopted from here keeps those values as the file already has them. An empty value is not
        // the same as a wrong one.
    }

    // ── the tracklist, which is not a list of tracks ────────────────────────────────────────
    //
    // A Discogs tracklist holds three kinds of entry. A TRACK is what it says. A HEADING names what follows
    // it, such as a vinyl side or a disc of a box set. An INDEX with sub-tracks is a multi-part piece. Its
    // own title is the work ("Sonata In B-Flat Major, KV. 333"), and the movements are inside it with their
    // own positions ("1-1", "1-2", "1-3"). Every classical release is written this way. Reading only the
    // top level of the array loses every movement on the record. For example, the first disc of a 14-CD
    // Mozart box has eight tracks, and six of them are one level down.

    /// <summary>One row of a listing once the shape above is flattened: the entry itself, and the work it
    /// is a movement of when it came out of one.</summary>
    /// <param name="Parent">The index entry the movement came from. Its credits apply to this track. Null
    /// for an ordinary track.</param>
    /// <param name="Heading">The heading last seen above this row, when it has not been claimed
    /// yet.</param>
    private readonly record struct TracklistRow(
        JsonElement Element,
        string? Work,
        uint Movement,
        uint MovementCount,
        string? Heading,
        JsonElement? Parent);

    /// <summary>The listing's tracks in playing order, with movements included and headings carried
    /// along.</summary>
    private static List<TracklistRow> Flatten(JsonElement list)
    {
        var rows = new List<TracklistRow>();
        string? heading = null;

        foreach (var entry in list.EnumerateArray())
        {
            if (SubTracks(entry) is { Count: > 0 } parts)
            {
                var work = Text(entry, "title");
                for (var i = 0; i < parts.Count; i++)
                {
                    rows.Add(new TracklistRow(parts[i], work, (uint)i + 1, (uint)parts.Count, heading, entry));
                    heading = null;
                }
                continue;
            }

            // A heading, or an index with no tracks inside. This is not a track. Counting it would throw
            // off the listing's fit against what was detected in the drive. The heading name is kept. On a
            // real multi-disc set, the heading above a disc's first track is that disc's own title.
            if (!IsTrack(entry))
            {
                heading = Text(entry, "title") ?? heading;
                continue;
            }

            rows.Add(new TracklistRow(entry, null, 0, 0, heading, null));
            heading = null;
        }

        return rows;
    }

    /// <summary>The movements inside an entry, or empty when it holds none.</summary>
    private static List<JsonElement> SubTracks(JsonElement entry)
        => entry.TryGetProperty("sub_tracks", out var parts) && parts.ValueKind == JsonValueKind.Array
            ? [.. parts.EnumerateArray().Where(IsTrack)]
            : [];

    /// <summary>Whether an entry is a track. An entry that states no type counts as a track. Sub-tracks on
    /// some releases have only a position and a title.</summary>
    private static bool IsTrack(JsonElement entry)
        => Text(entry, "type_") is not { } kind || kind.Equals("track", StringComparison.OrdinalIgnoreCase);

    /// <summary>The label that released it and the number on the spine, taken from the first label credit
    /// that names either. Discogs also lists reissue labels next to the original.</summary>
    private static (string? Label, string? CatalogNumber) LabelInfo(JsonElement root)
    {
        if (!root.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            return (null, null);

        foreach (var label in labels.EnumerateArray())
        {
            var name = Text(label, "name");
            var catalogNumber = Text(label, "catno");
            if (name is not null || catalogNumber is not null)
                return (name, catalogNumber);
        }
        return (null, null);
    }

    /// <summary>The barcode, from the identifiers list. Discogs keeps every printed code together,
    /// including matrix numbers, rights-society marks and mould SIDs. So the type must be
    /// checked.</summary>
    private static string? Barcode(JsonElement root)
    {
        if (!root.TryGetProperty("identifiers", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var id in ids.EnumerateArray())
        {
            if (!string.Equals(Text(id, "type"), "Barcode", StringComparison.OrdinalIgnoreCase))
                continue;
            // Printed with spaces on the sleeve, such as "0 28947 75881 5". The barcode is the digits only.
            if (Text(id, "value") is { } value &&
                new string([.. value.Where(char.IsDigit)]) is { Length: >= 8 } digits)
                return digits;
        }
        return null;
    }

    /// <summary>
    /// Discogs splits what others call a genre into two parts. The broad <c>genres</c> value ("Rock") and
    /// the specific <c>styles</c> value ("Post-Punk"). Both go through the app's own lists, so a shelf ends
    /// up with one name per kind of music, not Discogs' three. See <see cref="Genres"/>.
    ///
    /// <para><b>Both halves are read.</b> The styles are kept with the genre. A record with a detailed
    /// style is then known by that style, not only by its broad genre. Discogs' split is the same split RPM
    /// makes, so both halves are used here. This is the one place in the app where both halves are
    /// available at no extra cost.</para>
    /// </summary>
    private static GenreChoice Choice(JsonElement root)
        => Genres.PickPair(Strings(root, "genres"), Strings(root, "styles"));

    /// <summary>
    /// The front cover of a release, from its images. Null when it has none.
    /// </summary>
    /// <param name="maxSize">The size the cover is wanted at. Discogs offers each image in two sizes:
    /// <c>uri150</c>, a 150 px thumbnail, and <c>uri</c>, the full scan. They are not interchangeable. 150
    /// px suits a list row but is too small for a sleeve. A sleeve is the size that gets embedded in the
    /// track files and kept.</param>
    public static string? FrontCoverUrl(string releaseJson, int maxSize = 250)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        if (!doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var image in images.EnumerateArray())
        {
            // "primary" is the front. The other images are the labels and the run-out.
            if (Text(image, "type") is { } t && !t.Equals("primary", StringComparison.OrdinalIgnoreCase))
                continue;

            return maxSize <= ThumbnailSize
                ? Text(image, "uri150") ?? Text(image, "uri")
                : Text(image, "uri") ?? Text(image, "uri150");
        }
        return null;
    }

    /// <summary>
    /// A release's other pictures: everything Discogs holds for it except the front cover.
    ///
    /// <para>These are the collector's own photographs: the disc on a table, the A and B labels, the
    /// run-out groove and the inner sleeve. They are the only place a catalogue has a picture of the record
    /// itself. Discogs types them "secondary" and says nothing more. So which of them holds a disc is for
    /// the caller to decide by looking. This code cannot tell.</para>
    ///
    /// <para>Both sizes come back for each picture. The 150 px thumbnail is enough to see whether something
    /// is round. The full upload is fetched only for the picture that is chosen.</para>
    /// </summary>
    public static IReadOnlyList<ReleaseImage> OtherImages(string releaseJson)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        if (!doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return [];

        var others = new List<ReleaseImage>();
        foreach (var image in images.EnumerateArray())
        {
            if (Text(image, "type") is { } t && t.Equals("primary", StringComparison.OrdinalIgnoreCase))
                continue;

            var full = Text(image, "uri");
            var thumb = Text(image, "uri150");
            // Either size alone is enough. The probe falls back to the upload, and the upload falls back to
            // the thumbnail. An entry with neither is not a picture.
            if (full is not { Length: > 0 } && thumb is not { Length: > 0 })
                continue;
            others.Add(new ReleaseImage(thumb ?? full!, full ?? thumb!));
        }
        return others;
    }

    /// <summary>How many photographs the release holds besides its front cover. These are the same pictures
    /// <see cref="OtherImages(string)"/> returns, counted from the release document that the listing was
    /// already read from. This is read here, not fetched, because a fetch is the expensive part.</summary>
    private static int PhotoCount(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return 0;

        var count = 0;
        foreach (var image in images.EnumerateArray())
        {
            if (Text(image, "type") is { } t && t.Equals("primary", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Text(image, "uri") is { Length: > 0 } || Text(image, "uri150") is { Length: > 0 })
                count++;
        }
        return count;
    }

    /// <summary>
    /// The disc number a Discogs position names, or 0 when it names none. A multi-disc set writes the disc
    /// into the position, such as "2-3", "2.3" or "CD2-05". The digits before the first separator are the
    /// disc. A single-disc position ("5", "A1", "B2") has no separator and no disc.
    /// </summary>
    public static uint ParseDiscNumber(string? position)
    {
        if (string.IsNullOrWhiteSpace(position))
            return 0;

        var p = position.Trim();
        int sep = p.IndexOfAny(DiscSeparators);
        if (sep <= 0)
            return 0;

        // The head may carry a label ("CD2", "Disc2"); pull its trailing digits.
        var head = p[..sep];
        int i = head.Length;
        while (i > 0 && char.IsDigit(head[i - 1]))
            i--;

        return uint.TryParse(head.AsSpan(i), out var disc) && disc > 0 ? disc : 0;
    }

    private static readonly char[] DiscSeparators = { '-', '.', ':' };

    /// <summary>The only size Discogs renders besides the full scan.</summary>
    private const int ThumbnailSize = 150;

    // ── the shapes ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The facts about a pressing that are not on any tag: the catalogue number the label printed on it,
    /// the day it came out, and what Discogs files it under.
    ///
    /// <para>This is separate from <see cref="ParseRelease"/>, which is about tracks. ParseRelease runs
    /// during a rip, when track times matter. These facts are read once, when an album page is opened, and
    /// then kept.</para>
    /// </summary>
    /// <param name="Catno">Null when the release has no catalogue number. Discogs writes "none" for that
    /// case. That word is not a catalogue number, so it is never returned as one.</param>
    /// <param name="Released">As Discogs gives it: <c>2005-06-06</c>, or just <c>2005</c>. It is kept in
    /// that form and not parsed to a date, because many of them are not full dates.</param>
    /// <param name="Styles">The narrow labels, such as "Synth-pop". They describe a record better than the
    /// genre above them, such as "Electronic". They are the basis for a "more like this" list.</param>
    /// <param name="Barcode">The EAN or UPC from the packaging, digits only. Null where the release names
    /// no barcode. That is true of most releases pressed before barcodes were used.</param>
    /// <param name="Edition">What the record is, in Discogs' own words, such as "Vinyl, LP, Album, Limited
    /// Edition, Red Translucent". It is the one fact on the page that tells two copies of an album apart
    /// when the catalogue number does not.</param>
    public sealed record ReleaseFacts(
        string? Catno,
        string? Released,
        string? Label,
        string? Artist,
        IReadOnlyList<string> Styles,
        IReadOnlyList<string> Genres,
        string? Barcode = null,
        string? Edition = null);

    /// <summary>Reads the identity and filing of a <c>/releases/{id}</c> answer.</summary>
    public static ReleaseFacts ParseReleaseFacts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? catno = null;
        string? label = null;

        // Labels are objects. A release can name several, such as a licensor or a distributor. The first
        // with a usable catalogue number is used, because that is the number printed on the spine.
        if (root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in labels.EnumerateArray())
            {
                label ??= Text(l, "name");
                if (Text(l, "catno") is { } c && !c.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    catno = c;
                    label = Text(l, "name") ?? label;
                    break;
                }
            }
        }

        // "released" holds the full date where it is known. "year" is the fallback, and here it is a
        // number.
        var released = Text(root, "released");
        if (released is null && Year(root) is var year and > 0)
            released = year.ToString(CultureInfo.InvariantCulture);

        return new ReleaseFacts(
            catno,
            released,
            label,
            Artists(root),
            Strings(root, "styles"),
            Strings(root, "genres"),
            Barcode(root),
            Edition(root));
    }

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray()
                   .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                   .Where(s => !string.IsNullOrWhiteSpace(s))
                   .Select(s => s!)]
            : [];

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Blank(v.GetString()) : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>An id is a number on a release and a string in some answers.</summary>
    private static string? IdOf(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetInt64().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.String => Blank(e.GetString()),
        _ => null,
    };

    /// <summary>Year is a number on a release and a string, such as "1977", in a search result.</summary>
    public static uint Year(JsonElement e)
    {
        if (!e.TryGetProperty("year", out var y))
            return 0;
        if (y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var n))
            return n > 0 ? (uint)n : 0;
        var s = y.ValueKind == JsonValueKind.String ? y.GetString() : null;
        return s is { Length: >= 4 } && uint.TryParse(s.AsSpan(0, 4), out var parsed) ? parsed : 0;
    }

    /// <summary>
    /// Splits a search result title, such as "Fleetwood Mac - Rumours", into artist and album title. This
    /// is the only place a search result names the artist. Only the FIRST separator splits. Otherwise a
    /// title such as "F♯ A♯ ∞ - Reissue" would lose half of its name.
    /// </summary>
    public static (string? Artist, string Title) SplitTitle(string title)
    {
        var i = title.IndexOf(" - ", StringComparison.Ordinal);
        return i > 0
            ? (Named(title[..i].Trim()), title[(i + 3)..].Trim())
            : (null, title.Trim());
    }

    /// <summary>
    /// An artist's name with Discogs' disambiguation number taken off it: "The Biz (5)" becomes "The Biz".
    ///
    /// <para>Discogs gives every act a name that is unique in its database. When a name is already taken,
    /// the next act to want it gets a number after it. That number is part of Discogs' filing, not part of
    /// anybody's name. If it were written into a tag, the result would be a band nobody has heard of. It
    /// would also stay there. The number is what the wall groups records by, what an artist's page opens
    /// from, and what the catalogues are asked about next time.</para>
    ///
    /// <para>Only a number is removed, and only at the very end, and only where something comes before it.
    /// "Sunn O)))" keeps its brackets. An act whose real name ends in a bracketed word keeps that too,
    /// because only the one shape Discogs appends is matched.</para>
    ///
    /// <para>This is applied where a name is READ, not where it is written. So everything downstream (the
    /// tag, the search that goes back out, the folder a record is filed in) works from the name, not from
    /// the filing.</para>
    /// </summary>
    public static string Named(string name)
    {
        var trimmed = name.Trim();
        var stripped = Disambiguation.Replace(trimmed, "").TrimEnd();
        return stripped.Length > 0 ? stripped : trimmed;
    }

    /// <summary>Discogs' own suffix, and nothing else that looks like it: whitespace, then digits in
    /// brackets, then the end of the name.</summary>
    private static readonly Regex Disambiguation =
        new(@"\s*\(\d+\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string? Join(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? Blank(string.Join(", ", a.EnumerateArray().Select(x => x.GetString()).Where(s => s is { Length: > 0 })))
            : null;

    private static string? First(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(s => s is { Length: > 0 })
            : null;

    /// <summary>
    /// The formats block, read for a reader rather than as a field. This gives the format name, the
    /// descriptions, and whatever the entry says about the object itself.
    ///
    /// <para>{"name":"Vinyl","text":"Red Translucent","descriptions":["LP","Album","Limited Edition"]}
    /// becomes "Vinyl, LP, Album, Limited Edition, Red Translucent". The format comes first, because that
    /// is what someone looking for their own copy checks first. The entry's own words come last.</para>
    ///
    /// <para>Each word is said once. A release with two discs repeats the whole block for each disc. The
    /// descriptions often repeat the name ("Vinyl", "Vinyl, LP"). A plain join would read like a
    /// stutter.</para>
    /// </summary>
    private static string? Edition(JsonElement root)
    {
        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
            return null;

        var said = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var format in formats.EnumerateArray())
        {
            Say(Text(format, "name"));

            if (format.TryGetProperty("descriptions", out var descriptions)
                && descriptions.ValueKind == JsonValueKind.Array)
                foreach (var description in descriptions.EnumerateArray())
                    Say(description.GetString());

            Say(Text(format, "text"));
        }

        return Blank(string.Join(", ", said));

        void Say(string? word)
        {
            if (word is { Length: > 0 } && !string.IsNullOrWhiteSpace(word) && seen.Add(word.Trim()))
                said.Add(word.Trim());
        }
    }

    /// <summary>A release's formats are objects, such as
    /// {"name":"Vinyl","descriptions":["LP","Album"]}.</summary>
    private static string? Formats(JsonElement root)
    {
        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
            return null;
        return Blank(string.Join(", ",
            formats.EnumerateArray().Select(f => Text(f, "name")).Where(n => n is { Length: > 0 })));
    }

    private static string? Artists(JsonElement e) =>
        e.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
            ? Blank(string.Join(", ", a.EnumerateArray()
                .Select(x => Text(x, "name"))
                .Where(n => n is { Length: > 0 })
                .Select(n => Named(n!))))
            : null;

    /// <summary>"4:23" or "1:02:33" into seconds. Blank is normal. Many entries have no time. That is a
    /// track without a duration, not a broken listing.</summary>
    public static double? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        double total = 0;
        foreach (var part in text.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                return null;
            total = total * 60 + v;
        }
        return total > 0 ? total : null;
    }

    /// <summary>Discogs ranks results but does not score them. So position is the signal, and the first
    /// result is the best.</summary>
    public static double Relevance(int index, int limit) =>
        limit <= 0 ? 0 : Math.Clamp(1.0 - (double)index / Math.Max(limit, 1), 0, 1);
}
