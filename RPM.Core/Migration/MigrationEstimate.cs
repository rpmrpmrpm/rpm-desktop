namespace RPM.Core.Migration;

/// <summary>A song rpm already has, as the estimate compares it.</summary>
public sealed record KnownSong(string Artist, string Album, string Title, double DurationSeconds);

/// <summary>The summary's numbers: how many source tracks rpm probably has already, and how many bytes
/// the rest are. "About", because the run decides by the audio itself.</summary>
public sealed record Estimate(int AboutAlreadyInRpm, long AboutBytesNeeded, long TotalBytes);

/// <summary>What the summary shows before anything is read or downloaded.</summary>
public static class MigrationEstimate
{
    /// <summary>How close two lengths must be, in seconds, for two tracks to count as the same song.</summary>
    public const double LengthTolerance = 2.0;

    public static Estimate Of(SourceLibrary library, IReadOnlyList<KnownSong> known)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(known);

        var byKey = known
            .GroupBy(k => Key(k.Artist, k.Album, k.Title))
            .ToDictionary(g => g.Key, g => g.Select(k => k.DurationSeconds).ToList(), StringComparer.Ordinal);

        int already = 0;
        long needed = 0, total = 0;
        foreach (var track in library.Tracks)
        {
            total += track.SizeBytes;
            if (byKey.TryGetValue(Key(track.Artist, track.Album, track.Title), out var lengths)
                && lengths.Any(l => Close(l, track.DurationSeconds)))
                already++;
            else
                needed += track.SizeBytes;
        }
        return new Estimate(already, needed, total);
    }

    /// <summary>Whether a track and a known song are the same song: the same artist, album and title
    /// (trimmed, any case) and lengths within <see cref="LengthTolerance"/> seconds.</summary>
    public static bool SameSong(string artist, string album, string title, double seconds, KnownSong known) =>
        Key(artist, album, title) == Key(known.Artist, known.Album, known.Title) && Close(seconds, known.DurationSeconds);

    /// <summary>The comparison key: artist, album and title, trimmed and in lower case.</summary>
    public static string Key(string? artist, string? album, string? title) =>
        $"{Norm(artist)}\u0001{Norm(album)}\u0001{Norm(title)}";

    private static string Norm(string? text) => (text ?? "").Trim().ToLowerInvariant();

    /// <summary>A looser form for matching a song whose tags rpm corrected after its own import: lower
    /// case, accents removed, and only letters and digits kept ("Beyoncé" and "Beyonce!" are the same).</summary>
    public static string Loose(string? text)
    {
        var decomposed = (text ?? "").Normalize(System.Text.NormalizationForm.FormD);
        var kept = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (char.IsLetterOrDigit(c))
                kept.Append(char.ToLowerInvariant(c));
        return kept.ToString();
    }

    private static bool Close(double a, double b) => Math.Abs(a - b) <= LengthTolerance;
}

/// <summary>The albums of a library, in the order the source lists them. An import works one album at a
/// time, so a cloud import that runs out of space never stops half-way through one.</summary>
public static class AlbumBatch
{
    public static IReadOnlyList<IReadOnlyList<SourceTrack>> Group(IReadOnlyList<SourceTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var order = new List<List<SourceTrack>>();
        var byKey = new Dictionary<string, List<SourceTrack>>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            var artist = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist;
            var key = $"{artist.Trim().ToLowerInvariant()}\u0001{track.Album.Trim().ToLowerInvariant()}";
            if (!byKey.TryGetValue(key, out var album))
            {
                byKey[key] = album = [];
                order.Add(album);
            }
            album.Add(track);
        }
        return order;
    }
}
