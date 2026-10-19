namespace RPM.Core.Library;

/// <summary>
/// What kind of release a record is. Set by the listener at import or in the album editor, written into the files
/// (<see cref="Metadata.ReleaseTypeTag"/>), and used by the artist page to put the record on a shelf
/// (<see cref="ArtistShelves"/>). <c>Soundtrack</c> is included so the Soundtracks shelf can be set too.
/// </summary>
public enum ReleaseType
{
    Album,
    Ep,
    Single,
    Live,
    Mixtape,
    Compilation,
    Soundtrack,
}

/// <summary>Reading and writing <see cref="ReleaseType"/> in MusicBrainz's words, and the title guess used when a
/// record has no type.</summary>
public static class ReleaseTypes
{
    /// <summary>Every type, in the order the editor offers them.</summary>
    public static readonly IReadOnlyList<ReleaseType> All =
    [
        ReleaseType.Album, ReleaseType.Ep, ReleaseType.Single, ReleaseType.Live, ReleaseType.Mixtape,
        ReleaseType.Compilation, ReleaseType.Soundtrack,
    ];

    /// <summary>
    /// The values written to the file: MusicBrainz's primary type, then its secondary type where there is one. The
    /// same words MusicBrainz Picard writes, so a file tagged by either reads the same in both.
    /// </summary>
    public static IReadOnlyList<string> ToTagValues(ReleaseType type) => type switch
    {
        ReleaseType.Album => ["album"],
        ReleaseType.Ep => ["ep"],
        ReleaseType.Single => ["single"],
        ReleaseType.Live => ["album", "live"],
        ReleaseType.Mixtape => ["album", "mixtape/street"],
        ReleaseType.Compilation => ["album", "compilation"],
        ReleaseType.Soundtrack => ["album", "soundtrack"],
    };

    /// <summary>
    /// The type a file's values name, or null when they name none this app files by ("broadcast", "other",
    /// nothing). Each value may itself hold several words separated by semicolons or commas, which is how some
    /// taggers write a multi-valued field into a single one. A slash is not a separator: "mixtape/street" is one word.
    /// </summary>
    public static ReleaseType? FromTagValues(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        string? primary = null;
        var secondary = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            foreach (var raw in value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                var word = raw.Trim().ToLowerInvariant();
                if (word is "album" or "ep" or "single" or "broadcast" or "other")
                    primary ??= word;
                else if (word.Length > 0)
                    secondary.Add(word);
            }
        }

        return FromMusicBrainz(primary, secondary);
    }

    /// <summary>
    /// The type a MusicBrainz release group's primary and secondary types name. Used for the file's values and to
    /// fill the editor from a catalogue lookup.
    ///
    /// <para>When the types name more than one shelf, the artist page's existing order decides: single, then live,
    /// then soundtrack, then compilation, then mixtape, then EP, then album. A live single is a single, and a live EP
    /// is live.</para>
    /// </summary>
    public static ReleaseType? FromMusicBrainz(string? primary, IEnumerable<string>? secondary)
    {
        var first = primary?.Trim().ToLowerInvariant();
        var others = new HashSet<string>(
            (secondary ?? []).Select(s => s.Trim().ToLowerInvariant()), StringComparer.Ordinal);

        if (first == "single") return ReleaseType.Single;
        if (others.Contains("live")) return ReleaseType.Live;
        if (others.Contains("soundtrack")) return ReleaseType.Soundtrack;
        if (others.Contains("compilation")) return ReleaseType.Compilation;
        if (others.Contains("mixtape/street") || others.Contains("mixtape")) return ReleaseType.Mixtape;
        if (first == "ep") return ReleaseType.Ep;
        if (first == "album") return ReleaseType.Album;
        return null;
    }

    /// <summary>
    /// What the title suggests when nothing has been set: the same four guesses the artist page makes
    /// (<see cref="AlbumKind"/>), in its order. Null where the title suggests nothing, which is not the same as
    /// "album": nobody has said.
    /// </summary>
    public static ReleaseType? FromTitle(string? title)
    {
        if (AlbumKind.IsSingle(title)) return ReleaseType.Single;
        if (AlbumKind.IsLive(title)) return ReleaseType.Live;
        if (AlbumKind.IsSoundtrack(title)) return ReleaseType.Soundtrack;
        if (AlbumKind.IsCompilation(title)) return ReleaseType.Compilation;
        return null;
    }

    /// <summary>The shelf a record goes on: the type the listener set, else the title's guess, else an album. A set
    /// type always wins, which is the point of setting it: "Live Through This" set to Album leaves the live shelf.
    /// </summary>
    public static ReleaseType ShelfOf(ReleaseType? set, string? title) => set ?? FromTitle(title) ?? ReleaseType.Album;

    /// <summary>The stored name of a type in the library database, and back. The enum's own name, so the column reads
    /// plainly and cannot be shifted by reordering the enum.</summary>
    public static string ToStored(ReleaseType type) => type.ToString();

    public static ReleaseType? FromStored(string? stored) =>
        Enum.TryParse<ReleaseType>(stored, ignoreCase: true, out var type) && Enum.IsDefined(type) ? type : null;
}
