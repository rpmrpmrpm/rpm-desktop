using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RPM.Core.Migration;

/// <summary>A playlist file and its bytes. <paramref name="Path"/> is where the file is, so relative
/// entries can be resolved against its folder.</summary>
public sealed record PlaylistFileInput(string Path, byte[] Bytes);

/// <summary>
/// Reads .m3u, .m3u8 and .pls playlists, which almost every music player can export. They hold
/// playlists only: no ratings, favourites or play counts.
/// </summary>
/// <remarks>
/// <para>Paths are resolved here as text, not through <see cref="System.IO.Path"/>, because a playlist
/// written on Windows can be read on a Mac and the other way round. A path that starts with a drive
/// (<c>C:\</c>), two backslashes or a slash is absolute and is kept as it is. A relative path is
/// joined to the playlist's folder with the separator that folder uses.</para>
/// <para>The tracks are the distinct files the playlists name, in the order they are first seen. Their
/// title, artist and length come from the first entry that names them (#EXTINF, or TitleN and LengthN
/// in a .pls); an entry with neither is named after its file.</para>
/// </remarks>
public static partial class PlaylistFileReader
{
    public static SourceLibrary Read(IReadOnlyList<PlaylistFileInput> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var tracks = new List<SourceTrack>();
        var byPath = new HashSet<string>(StringComparer.Ordinal);
        var playlists = new List<SourcePlaylist>();
        var skipped = new List<SourceSkip>();

        foreach (var file in files)
        {
            var text = Decode(file);
            var entries = Extension(file.Path) == ".pls" ? ReadPls(text) : ReadM3u(text);

            var trackIds = new List<string>();
            var unresolved = new List<string>();
            foreach (var entry in entries)
            {
                if (Resolve(file.Path, entry.Location) is not { } path)
                {
                    unresolved.Add(entry.Location);
                    skipped.Add(new SourceSkip(entry.Display ?? entry.Location, SkipReason.NotAFile));
                    continue;
                }

                trackIds.Add(path);
                if (byPath.Add(path))
                    tracks.Add(TrackOf(path, entry));
            }

            var playlistPath = file.Path.Normalize(NormalizationForm.FormC);
            playlists.Add(new SourcePlaylist(playlistPath, FileStem(playlistPath), [], false, trackIds, unresolved));
        }

        return new SourceLibrary(SourceKind.PlaylistFiles, "", "", tracks, playlists, skipped);
    }

    private sealed record Entry(string Location, string? Display, double Seconds);

    private static SourceTrack TrackOf(string path, Entry entry)
    {
        var (artist, title) = SplitDisplay(entry.Display);
        return new SourceTrack(
            SourceId: path,
            File: new LocalFile(path),
            Title: title ?? FileStem(path),
            Artist: artist,
            Album: "",
            AlbumArtist: "",
            TrackNumber: 0,
            DiscNumber: 0,
            DurationSeconds: entry.Seconds,
            SizeBytes: 0,
            Rating: 0,
            Favorite: false,
            Plays: 0,
            LastPlayed: null,
            Added: null);
    }

    /// <summary>"Artist - Title" as the two parts; a display without " - " is a title alone.</summary>
    private static (string Artist, string? Title) SplitDisplay(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
            return ("", null);
        var at = display.IndexOf(" - ", StringComparison.Ordinal);
        return at > 0 ? (display[..at].Trim(), display[(at + 3)..].Trim()) : ("", display.Trim());
    }

    private static List<Entry> ReadM3u(string text)
    {
        var entries = new List<Entry>();
        string? display = null;
        double seconds = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                // #EXTINF:<seconds> [attributes],<display>. The display may itself hold commas.
                var body = line["#EXTINF:".Length..];
                var comma = body.IndexOf(',');
                var head = comma >= 0 ? body[..comma] : body;
                display = comma >= 0 ? body[(comma + 1)..].Trim() : null;
                var number = LeadingNumber().Match(head);
                seconds = number.Success && double.TryParse(number.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 0;
                continue;
            }
            if (line.StartsWith('#'))
                continue;

            entries.Add(new Entry(line, display, seconds));
            display = null;
            seconds = 0;
        }
        return entries;
    }

    private static List<Entry> ReadPls(string text)
    {
        var files = new SortedDictionary<int, string>();
        var titles = new Dictionary<int, string>();
        var lengths = new Dictionary<int, double>();
        foreach (var raw in text.Split('\n'))
        {
            var match = PlsLine().Match(raw.Trim());
            if (!match.Success)
                continue;
            var n = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var value = match.Groups[3].Value.Trim();
            switch (match.Groups[1].Value.ToLowerInvariant())
            {
                case "file": files[n] = value; break;
                case "title": titles[n] = value; break;
                case "length":
                    lengths[n] = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 0;
                    break;
            }
        }
        return [.. files.Select(f => new Entry(f.Value, titles.GetValueOrDefault(f.Key), lengths.GetValueOrDefault(f.Key)))];
    }

    /// <summary>The entry as an absolute path in normal form C, or null when it is not a file (a web
    /// address).</summary>
    internal static string? Resolve(string playlistPath, string location)
    {
        if (location.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return ITunesLibraryReader.LocalPathOf(location);
        if (UrlScheme().IsMatch(location))
            return null;

        string path;
        if (IsAbsolute(location))
        {
            path = location;
        }
        else
        {
            var windows = playlistPath.Contains('\\') && !playlistPath.StartsWith('/');
            var separator = windows ? '\\' : '/';
            var cut = playlistPath.LastIndexOfAny(['/', '\\']);
            var folder = cut >= 0 ? playlistPath[..cut] : "";
            path = Collapse(folder + separator + location.Replace(windows ? '/' : '\\', separator), separator);
        }
        return path.Normalize(NormalizationForm.FormC);
    }

    private static bool IsAbsolute(string path) =>
        path.StartsWith('/') || path.StartsWith(@"\\", StringComparison.Ordinal) || DrivePath().IsMatch(path);

    /// <summary>Removes "." segments and folds ".." into the segment before it.</summary>
    private static string Collapse(string path, char separator)
    {
        var parts = path.Split(separator);
        var kept = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == "." || part.Length == 0 && i > 0 && i < parts.Length - 1)
                continue;
            // Never fold away the root: "" before a leading slash, or a drive such as "C:".
            if (part == ".." && kept.Count > 1)
            {
                kept.RemoveAt(kept.Count - 1);
                continue;
            }
            kept.Add(part);
        }
        return string.Join(separator, kept);
    }

    /// <summary>The text of the file. .m3u8 is always UTF-8. .m3u and .pls are UTF-8 when the bytes are
    /// valid UTF-8, and Windows-1252 otherwise, which is what older Windows players write.</summary>
    private static string Decode(PlaylistFileInput file)
    {
        var bytes = file.Bytes.AsSpan();
        var bom = Encoding.UTF8.Preamble;
        if (bytes.StartsWith(bom))
            return Encoding.UTF8.GetString(bytes[bom.Length..]);
        if (Extension(file.Path) == ".m3u8")
            return Encoding.UTF8.GetString(bytes);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    private static string Extension(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[dot..].ToLowerInvariant() : "";
    }

    private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    private static string FileStem(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    [GeneratedRegex("^[A-Za-z]:[\\\\/]")]
    private static partial Regex DrivePath();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]+://")]
    private static partial Regex UrlScheme();

    [GeneratedRegex("^-?[0-9]+(\\.[0-9]+)?")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex("^(File|Title|Length)([0-9]+)=(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlsLine();
}
