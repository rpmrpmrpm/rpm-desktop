using RPM.Core.Models;

namespace RPM.Core.Import;

/// <summary>
/// Renders a destination-relative path for a track from a template string.
///
/// Tokens: {artist} {albumartist} {album} {title} {track} {track:00} {disc} {year} {genre}
///          {work} {movement}
/// '/' in the template separates folders. Each rendered segment is sanitised independently.
/// {albumartist} falls back to {artist}; {artist}/{album}/{title} fall back to Unknown labels.
/// A {track...} token renders empty (with trailing separator trimmed) when the track number is 0.
/// </summary>
public sealed record NamingTemplate(string Name, string Pattern)
{
    public static readonly NamingTemplate ArtistAlbumNumberedTitle =
        new("Artist / Album / ## - Title", "{albumartist}/{album}/{track:00} - {title}");

    public static readonly NamingTemplate ArtistAlbumArtistTitle =
        new("Artist / Album / Artist - Title", "{albumartist}/{album}/{artist} - {title}");

    public static readonly NamingTemplate ArtistNumberedTitle =
        new("Artist / ## - Title", "{albumartist}/{track:00} - {title}");

    public static readonly NamingTemplate FlatArtistTitle =
        new("Artist - Title (single folder)", "{artist} - {title}");

    /// <summary>
    /// The classical shape: the work is a folder, and its movements are the files inside it.
    ///
    /// <para>Without this, a 14-CD box lands as two hundred files in one directory. There, the only thing
    /// telling a Mozart sonata's Allegro from a Haydn one is a number. Both tokens collapse when the
    /// track is part of no work, so a record that is half works and half single pieces files correctly
    /// under one template. That is what a recital disc actually is.</para>
    /// </summary>
    public static readonly NamingTemplate ArtistAlbumWorkMovement =
        new("Artist / Album / Work / ## - Movement",
            "{albumartist}/{album}/{work}/{disc:00}-{track:00} - {movement}");

    public static readonly IReadOnlyList<NamingTemplate> Presets =
    [
        ArtistAlbumNumberedTitle,
        ArtistAlbumArtistTitle,
        ArtistNumberedTitle,
        FlatArtistTitle,
        ArtistAlbumWorkMovement,
    ];

    public const string UnknownArtist = "Unknown Artist";

    /// <summary>The tokens a pattern may use, in the order a picker offers them. They are kept here so the
    /// settings editor and this renderer never drift on what is legal.</summary>
    public static readonly IReadOnlyList<string> Tokens =
    [
        "{albumartist}", "{artist}", "{album}", "{title}",
        "{track:00}", "{track}", "{disc}", "{disc:00}", "{year}", "{genre}",
        "{work}", "{movement}",
    ];

    /// <summary>A representative track for previewing a pattern in the editor. Every token resolves
    /// to something, so a pattern's effect is visible before it is ever used on real files.</summary>
    public static readonly TrackInfo SampleTrack = new()
    {
        FilePath = "",
        Extension = ".flac",
        Artist = "Radiohead",
        AlbumArtist = "Radiohead",
        Album = "OK Computer",
        Title = "Paranoid Android",
        TrackNumber = 2,
        TrackCount = 12,
        DiscNumber = 1,
        Year = 1997,
        Genre = "Alternative",
    };

    /// <summary>The same, for a pattern that names works. A preview of "{work}" against a track that
    /// is part of none says nothing about what the pattern does.</summary>
    public static readonly TrackInfo SampleMovement = new()
    {
        FilePath = "",
        Extension = ".flac",
        Artist = "Bart van Oort",
        AlbumArtist = "Wolfgang Amadeus Mozart",
        Album = "Complete Keyboard Works",
        Title = "Sonata In C Major, KV. 330: Allegretto",
        Work = "Sonata In C Major, KV. 330",
        MovementName = "Allegretto",
        MovementNumber = 3,
        Composers = "Wolfgang Amadeus Mozart",
        TrackNumber = 8,
        DiscNumber = 1,
        Year = 2006,
        Genre = "Classical",
    };

    /// <summary>Renders a pattern for display without ever throwing. The editor calls this on every
    /// keystroke, where a half-typed pattern is normal, not an error.</summary>
    public string Preview(TrackInfo? track = null)
    {
        try { return Render(track ?? SampleTrack); }
        catch { return ""; }
    }

    /// <summary>Whether this pattern says which disc a file is from. A merged multi-disc import needs to
    /// know this. A pattern that never mentions the disc names every disc's track 1 the same thing, in one
    /// folder (see <see cref="Analysis.MultiDiscImport.NamedInMergedAlbum"/>).</summary>
    public bool MentionsDisc => Pattern.Contains("{disc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Renders the relative path (forward slashes) for the track, extension appended.
    /// A FOLDER segment built around {album} is dropped entirely when the track has no
    /// album. Loose songs file directly under the artist instead of an "Unknown Album"
    /// bucket. The album-resolver's LooseSongs treatment relies on this.
    /// </summary>
    public string Render(TrackInfo track)
    {
        string artist = track.Artist ?? UnknownArtist;
        string albumArtist = track.AlbumArtist ?? artist;
        string album = track.Album ?? "";
        string title = track.Title ?? Path.GetFileNameWithoutExtension(track.FilePath);
        string work = track.Work ?? "";

        var segments = Pattern.Split('/');
        var rendered = new List<string>(segments.Length);
        for (var i = 0; i < segments.Length; i++)
        {
            bool isFolder = i < segments.Length - 1;
            if (isFolder && string.IsNullOrEmpty(album) && segments[i].Contains("{album}", StringComparison.Ordinal))
                continue;
            // A work folder on a track that is part of no work is not an "Unknown Work" bucket. The piece
            // belongs one level up, beside the works, which is where it sits on the record.
            if (isFolder && string.IsNullOrEmpty(work) && segments[i].Contains("{work}", StringComparison.Ordinal))
                continue;
            string value = RenderSegment(segments[i], track, artist, albumArtist, album, title, work);
            rendered.Add(PathSanitizer.SanitizeSegment(value));
        }

        return string.Join('/', rendered) + track.Extension;
    }

    private static string RenderSegment(
        string segment, TrackInfo track, string artist, string albumArtist, string album, string title,
        string work)
    {
        var sb = new System.Text.StringBuilder(segment);
        sb.Replace("{albumartist}", albumArtist)
          .Replace("{artist}", artist)
          .Replace("{album}", album)
          // The movement alone where the file names one, and the whole title otherwise. So a
          // single-piece track under a "{movement}" pattern is named rather than left blank.
          .Replace("{movement}", string.IsNullOrWhiteSpace(track.MovementName) ? title : track.MovementName!)
          .Replace("{work}", work)
          .Replace("{title}", title)
          .Replace("{genre}", track.Genre ?? "")
          .Replace("{year}", track.Year > 0 ? track.Year.ToString() : "");

        string result = sb.ToString();

        // Number tokens: "{track}" / "{track:00}" and "{disc}" / "{disc:00}". The format is applied when the
        // value is above 0. The token renders empty when it is 0, and a dangling leading separator is
        // trimmed. So a single-disc album's "{disc}-" collapses away rather than leaving a stray "-".
        result = RenderNumberToken(result, "track", track.TrackNumber);
        result = RenderNumberToken(result, "disc", track.DiscNumber);

        return result;
    }

    /// <summary>Replaces the "{name}" / "{name:fmt}" token in <paramref name="input"/> with the number,
    /// or with nothing (trimming a separator the now-empty leading token left behind) when it is 0.</summary>
    private static string RenderNumberToken(string input, string name, uint value)
    {
        string open = "{" + name;
        int idx = input.IndexOf(open, StringComparison.Ordinal);
        if (idx < 0)
            return input;

        int end = input.IndexOf('}', idx);
        if (end < 0)
            return input;

        string token = input[idx..(end + 1)];
        // Guard "{disc}" against matching a longer token: the char after the name must open a format
        // spec or close the token.
        if (token[open.Length] is not (':' or '}'))
            return input;

        string format = token.Contains(':') ? token[(token.IndexOf(':') + 1)..^1] : "";
        string text = value > 0 ? value.ToString(format.Length > 0 ? format : null) : "";
        input = input.Replace(token, text);

        // "  - Title" / "-01 Title" when the number is absent: drop the dangling leading separator.
        if (text.Length == 0)
            input = input.TrimStart(' ', '-', '.', '_');

        return input;
    }
}
