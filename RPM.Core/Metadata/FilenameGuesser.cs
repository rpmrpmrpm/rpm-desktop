using System.Text.RegularExpressions;
using RPM.Core.Models;

namespace RPM.Core.Metadata;

/// <summary>
/// Last-resort metadata: guesses artist/title from common file-name shapes like
/// "Artist - Title.mp3", "03 - Artist - Title.mp3" or "03. Title.mp3".
/// </summary>
public static partial class FilenameGuesser
{
    [GeneratedRegex(@"^(?<track>\d{1,3})?[\s.\-_]*(?<artist>[^-]+?)\s+-\s+(?<title>.+)$")]
    private static partial Regex ArtistDashTitle();

    [GeneratedRegex(@"^(?<track>\d{1,3})[\s.\-_]+(?<title>.+)$")]
    private static partial Regex TrackTitle();

    /// <summary>Fills Title/Artist/TrackNumber from the file name when the info is missing.</summary>
    public static TrackInfo Enrich(TrackInfo info)
    {
        if (info.HasUsableMetadata)
            return info;

        string stem = Path.GetFileNameWithoutExtension(info.FilePath)
            .Replace('_', ' ')
            .Trim();

        var m = ArtistDashTitle().Match(stem);
        if (m.Success)
        {
            return info with
            {
                Artist = info.Artist ?? m.Groups["artist"].Value.Trim(),
                Title = info.Title ?? m.Groups["title"].Value.Trim(),
                TrackNumber = info.TrackNumber > 0 ? info.TrackNumber
                    : m.Groups["track"].Success && uint.TryParse(m.Groups["track"].Value, out uint t) ? t : 0,
                Source = MetadataSource.FilenameGuess,
            };
        }

        m = TrackTitle().Match(stem);
        if (m.Success && string.IsNullOrWhiteSpace(info.Title))
        {
            return info with
            {
                Title = m.Groups["title"].Value.Trim(),
                TrackNumber = info.TrackNumber > 0 ? info.TrackNumber
                    : uint.TryParse(m.Groups["track"].Value, out uint t) ? t : 0,
                Source = MetadataSource.FilenameGuess,
            };
        }

        return info;
    }
}
