using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace RPM.Core.Migration;

/// <summary>
/// Reads the iTunes library XML: the file Apple Music writes with File &gt; Library &gt; Export
/// Library, Windows iTunes writes when sharing is on, and MusicBee writes when "Save a copy of the
/// library tags as an iTunes formatted XML file" is on.
/// </summary>
/// <remarks>
/// <para>The file is an Apple property list: <c>dict</c> elements of <c>key</c> and value pairs, and
/// <c>array</c> elements. It is read once, forward only, with an <see cref="XmlReader"/>, and the
/// values are kept as plain dictionaries and lists. A library of 20,000 tracks is about 40 MB.</para>
/// <para>Playlist entries name tracks by "Track ID", which can change between two exports. Tracks are
/// identified by "Persistent ID", which does not, and by "Track ID" only when the file has no
/// persistent ids (MusicBee).</para>
/// </remarks>
public static partial class ITunesLibraryReader
{
    /// <summary>Reads a library. <paramref name="kind"/> must be <see cref="SourceKind.MusicBee"/> or
    /// <see cref="SourceKind.ITunes"/>: the file format is the same, the name shown is not.</summary>
    public static SourceLibrary Read(Stream xml, SourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (kind is not (SourceKind.MusicBee or SourceKind.ITunes))
            throw new ArgumentException("An iTunes library file is a MusicBee or iTunes library.", nameof(kind));

        var root = ReadPlist(xml) as Dictionary<string, object?>
            ?? throw new FormatException("The file is not an iTunes library: it has no top-level dict.");

        var tracks = new List<SourceTrack>();
        var skipped = new List<SourceSkip>();
        var idOfTrack = new Dictionary<string, string>(StringComparer.Ordinal);

        if (root.GetValueOrDefault("Tracks") is Dictionary<string, object?> trackDict)
        {
            foreach (var (trackKey, value) in trackDict)
            {
                if (value is not Dictionary<string, object?> t)
                    continue;
                var trackId = t.GetValueOrDefault("Track ID") is long n ? n.ToString(CultureInfo.InvariantCulture) : trackKey;
                switch (ReadTrack(t, trackId))
                {
                    case SourceTrack track:
                        tracks.Add(track);
                        idOfTrack[trackId] = track.SourceId;
                        break;
                    case SourceSkip skip:
                        skipped.Add(skip);
                        break;
                }
            }
        }

        var playlists = root.GetValueOrDefault("Playlists") is List<object?> list
            ? ReadPlaylists(list, idOfTrack)
            : [];

        return new SourceLibrary(
            kind,
            LibraryIdOf(root),
            kind == SourceKind.MusicBee ? "MusicBee" : "iTunes",
            tracks,
            playlists,
            skipped);
    }

    /// <summary>The library's own id. MusicBee may not write one; then the music folder stands in, so
    /// two reads of the same library still give the same id.</summary>
    private static string LibraryIdOf(Dictionary<string, object?> root)
    {
        if (Text(root, "Library Persistent ID") is { Length: > 0 } id)
            return id;
        if (Text(root, "Music Folder") is { Length: > 0 } folder)
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..16];
        return "";
    }

    private static object ReadTrack(Dictionary<string, object?> t, string trackId)
    {
        var title = Text(t, "Name") ?? "";
        var artist = Text(t, "Artist") ?? "";
        var what = artist.Length > 0 ? $"{artist} - {title}" : title;

        if (Flag(t, "Podcast") || Flag(t, "Has Video") || Flag(t, "Movie") || Flag(t, "TV Show")
            || Flag(t, "iTunesU") || Flag(t, "Audiobook"))
            return new SourceSkip(what, SkipReason.NotMusic);

        var type = Text(t, "Track Type");
        var location = Text(t, "Location");
        if (type == "URL" || location is not null && !location.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return new SourceSkip(what, SkipReason.NotAFile);
        if (type == "Remote" || location is null || LocalPathOf(location) is not { } path)
            return new SourceSkip(what, SkipReason.NotOnThisComputer);

        var rating = Flag(t, "Rating Computed") ? 0 : Math.Clamp((int)Math.Round(Number(t, "Rating") / 10.0), 0, 10);

        return new SourceTrack(
            SourceId: Text(t, "Persistent ID") is { Length: > 0 } persistent ? persistent : trackId,
            File: new LocalFile(path),
            Title: title,
            Artist: artist,
            Album: Text(t, "Album") ?? "",
            AlbumArtist: Text(t, "Album Artist") ?? "",
            TrackNumber: (int)Number(t, "Track Number"),
            DiscNumber: (int)Number(t, "Disc Number"),
            DurationSeconds: Number(t, "Total Time") / 1000.0,
            SizeBytes: Number(t, "Size"),
            Rating: rating,
            Favorite: Flag(t, "Loved") || Flag(t, "Favorited"),
            Plays: (int)Number(t, "Play Count"),
            LastPlayed: t.GetValueOrDefault("Play Date UTC") as DateTimeOffset?,
            Added: t.GetValueOrDefault("Date Added") as DateTimeOffset?);
    }

    private static List<SourcePlaylist> ReadPlaylists(List<object?> list, Dictionary<string, string> idOfTrack)
    {
        var dicts = list.OfType<Dictionary<string, object?>>().ToList();

        // Folders, by their persistent id, so a playlist can name the folders it is in.
        var folders = new Dictionary<string, (string Name, string? Parent)>(StringComparer.Ordinal);
        foreach (var p in dicts)
            if (Flag(p, "Folder") && Text(p, "Playlist Persistent ID") is { } id)
                folders[id] = (Text(p, "Name") ?? "", Text(p, "Parent Persistent ID"));

        var playlists = new List<SourcePlaylist>();
        foreach (var p in dicts)
        {
            // The whole library, the built-in lists (Music, Movies, Podcasts, Downloaded) and folders
            // are not playlists anybody made.
            if (Flag(p, "Master") || p.ContainsKey("Distinguished Kind") || Flag(p, "Folder"))
                continue;

            var trackIds = new List<string>();
            var unresolved = new List<string>();
            if (p.GetValueOrDefault("Playlist Items") is List<object?> items)
            {
                foreach (var item in items.OfType<Dictionary<string, object?>>())
                {
                    if (item.GetValueOrDefault("Track ID") is not long n)
                        continue;
                    var trackId = n.ToString(CultureInfo.InvariantCulture);
                    if (idOfTrack.TryGetValue(trackId, out var sourceId))
                        trackIds.Add(sourceId);
                    else
                        unresolved.Add(trackId);
                }
            }

            playlists.Add(new SourcePlaylist(
                SourceId: Text(p, "Playlist Persistent ID")
                    ?? (p.GetValueOrDefault("Playlist ID") is long pid ? pid.ToString(CultureInfo.InvariantCulture) : Text(p, "Name") ?? ""),
                Name: Text(p, "Name") ?? "",
                Folder: FolderChain(Text(p, "Parent Persistent ID"), folders),
                Smart: p.ContainsKey("Smart Info") || p.ContainsKey("Genius Track ID"),
                TrackIds: trackIds,
                Unresolved: unresolved));
        }
        return playlists;
    }

    private static List<string> FolderChain(string? parent, Dictionary<string, (string Name, string? Parent)> folders)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // A broken file could name a folder as its own ancestor; the set stops the walk.
        while (parent is not null && seen.Add(parent) && folders.TryGetValue(parent, out var folder))
        {
            chain.Add(folder.Name);
            parent = folder.Parent;
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// A <c>file:</c> URL as a path on disk, or null when it is not one. Windows drive paths come back
    /// with backslashes (<c>C:\Music\a.mp3</c>), network shares as <c>\\server\share\a.mp3</c>, and
    /// everything else as a POSIX path. The path is in Unicode normal form C: macOS writes decomposed
    /// accents, and two spellings of one name must compare equal.
    /// </summary>
    internal static string? LocalPathOf(string location)
    {
        if (!location.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = location["file://".Length..];
        if (rest.StartsWith("localhost/", StringComparison.OrdinalIgnoreCase))
            rest = rest["localhost".Length..];

        var path = Uri.UnescapeDataString(rest);
        if (DrivePath().IsMatch(path))
            path = path[1..].Replace('/', '\\');
        else if (!path.StartsWith('/'))
            path = @"\\" + path.Replace('/', '\\');

        return path.Length == 0 ? null : path.Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex("^/[A-Za-z]:/")]
    private static partial Regex DrivePath();

    private static string? Text(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) as string;

    private static long Number(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) switch
    {
        long n => n,
        double x => (long)x,
        _ => 0,
    };

    private static bool Flag(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) is true;

    // ── the property list ──

    private static object? ReadPlist(Stream xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var reader = XmlReader.Create(xml, settings);
        reader.MoveToContent();
        if (reader.LocalName != "plist")
            throw new FormatException("The file is not a property list.");
        if (reader.IsEmptyElement)
            return null;
        reader.Read();
        return reader.NodeType == XmlNodeType.Element ? ReadValue(reader) : null;
    }

    /// <summary>Reads the element the reader is on and leaves the reader on the node after it.</summary>
    private static object? ReadValue(XmlReader reader)
    {
        switch (reader.LocalName)
        {
            case "dict":
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (reader.IsEmptyElement)
                {
                    reader.Read();
                    return dict;
                }
                reader.Read();
                while (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName != "key")
                        throw new FormatException($"Expected a key in a dict, found <{reader.LocalName}>.");
                    var key = reader.ReadElementContentAsString();
                    dict[key] = reader.NodeType == XmlNodeType.Element ? ReadValue(reader) : null;
                }
                reader.ReadEndElement();
                return dict;
            }
            case "array":
            {
                var list = new List<object?>();
                if (reader.IsEmptyElement)
                {
                    reader.Read();
                    return list;
                }
                reader.Read();
                while (reader.NodeType == XmlNodeType.Element)
                    list.Add(ReadValue(reader));
                reader.ReadEndElement();
                return list;
            }
            case "true":
                reader.Skip();
                return true;
            case "false":
                reader.Skip();
                return false;
            case "integer":
                return long.TryParse(reader.ReadElementContentAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0L;
            case "real":
                return double.TryParse(reader.ReadElementContentAsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0d;
            case "date":
                return DateTimeOffset.TryParse(reader.ReadElementContentAsString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
            case "string":
                return reader.ReadElementContentAsString();
            default:
                // <data> (smart playlist rules) and anything unknown: not needed.
                reader.Skip();
                return null;
        }
    }
}
