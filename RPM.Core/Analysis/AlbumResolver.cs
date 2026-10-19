using System.Text.RegularExpressions;
using RPM.Core.Models;

namespace RPM.Core.Analysis;

/// <summary>What the user is assembling. Drives the default album treatment.</summary>
public enum OrganizeGoal
{
    /// <summary>Preserve or complete album structures (compilations stay together).</summary>
    Albums,

    /// <summary>Collect songs; album folders only where an album is substantially present.</summary>
    Songs,
}

/// <summary>How a group's tracks show what they are on disk.</summary>
public enum GroupKind
{
    /// <summary>One folder, one (or two) artists, shared album: an album rip.</summary>
    OriginalAlbum,

    /// <summary>One folder, many artists, shared album: a compilation rip (Now, soundtrack, ...).</summary>
    Compilation,

    /// <summary>No album cohesion: loose songs.</summary>
    LooseSongs,
}

/// <summary>Per-group filing decision, defaulted from goal and kind, and the user can override
/// it.</summary>
public enum GroupTreatment
{
    /// <summary>File under the group's album (compilation or album), AlbumArtist accordingly.</summary>
    KeepAlbum,

    /// <summary>Ignore the group; file each track under its own (recognised or tagged) album.</summary>
    SplitToOriginal,

    /// <summary>No album folder: artist/title only.</summary>
    LooseSongs,
}

/// <summary>A cluster of tracks that belong together on disk.</summary>
public sealed class TrackGroup
{
    public required string Id { get; init; }
    public required GroupKind Kind { get; init; }
    public required string? AlbumName { get; init; }
    public required string? AlbumArtist { get; init; }
    public required IReadOnlyList<TrackInfo> Tracks { get; init; }

    /// <summary>Tracks present / expected (from TrackCount tags), 0 when unknown.</summary>
    public double Completeness { get; init; }

    public GroupTreatment DefaultTreatment(OrganizeGoal goal) => (goal, Kind) switch
    {
        (_, GroupKind.LooseSongs) => GroupTreatment.LooseSongs,
        (OrganizeGoal.Albums, _) => GroupTreatment.KeepAlbum,
        // Songs goal: substantially-present albums keep their folder, thin ones dissolve.
        _ => Completeness >= 0.6 ? GroupTreatment.KeepAlbum : GroupTreatment.LooseSongs,
    };
}

/// <summary>
/// Groups analysed tracks by their on-disk evidence and classifies each group, so filing
/// can honour what the user actually has (a compilation rip stays a compilation even when
/// recognition names each track's original studio album). Pure and deterministic: the
/// interactive layer sits on top.
/// </summary>
public static partial class AlbumResolver
{
    [GeneratedRegex(@"^(cd|disc|disk)\s*[-_ ]?\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscFolder();

    [GeneratedRegex(@"[\s\-–—_:,]*[\(\[]?\s*(cd|disc|disk)\s*[-_ ]?\d+\s*[\)\]]?$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscSuffix();

    /// <summary>
    /// Strips the per-disc decorations that rips often add ("Album - CD 1", "Album (Disc 2)"),
    /// so multi-disc sets cluster as ONE album instead of one per disc.
    /// </summary>
    public static string NormalizeAlbum(string album)
    {
        var n = DiscSuffix().Replace(album.Trim(), "").Trim();
        return n.Length == 0 ? album.Trim() : n;
    }

    /// <summary>The folder a file's record lives in: its own, unless that is a "CD 2" folder, in which
    /// case it is the parent. A two-disc set is one record kept in two boxes.</summary>
    public static string DiscFolderParent(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? "";
        var name = Path.GetFileName(dir);
        return name is not null && DiscFolder().IsMatch(name) ? Path.GetDirectoryName(dir) ?? dir : dir;
    }

    /// <summary>Minimum distinct artists for a shared-album folder to read as a compilation.</summary>
    private const int CompilationArtistThreshold = 3;

    public const string VariousArtists = "Various Artists";

    public static IReadOnlyList<TrackGroup> Resolve(IEnumerable<TrackInfo> tracks)
    {
        // Group by folder; "CD 1"-style folders collapse into their parent so multi-disc
        // rips form one group.
        var byFolder = tracks.GroupBy(t => DiscFolderParent(t.FilePath), StringComparer.OrdinalIgnoreCase);

        var groups = new List<TrackGroup>();
        foreach (var folder in byFolder)
        {
            var members = folder.ToList();

            // Album cohesion: the dominant album tag and its share of the folder.
            var albumClusters = members
                .Where(t => !string.IsNullOrWhiteSpace(t.Album))
                .GroupBy(t => NormalizeAlbum(t.Album!), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ToList();
            var dominant = albumClusters.FirstOrDefault();
            double albumShare = members.Count == 0 ? 0 : (double)(dominant?.Count() ?? 0) / members.Count;
            double taggedShare = members.Count == 0 ? 0 : (double)albumClusters.Sum(g => g.Count()) / members.Count;
            string? folderAlbum = CleanFolderAlbum(Path.GetFileName(folder.Key));

            // Dominant tag wins; the folder name only stands in when tags are largely ABSENT.
            // Tags that disagree with each other mean there is no shared album, so the group is loose.
            string? albumName = albumShare >= 0.6 ? dominant!.Key
                : taggedShare <= 1.0 / 3 ? folderAlbum
                : null;

            var artists = members
                .Select(t => t.AlbumArtist ?? t.Artist)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            GroupKind kind;
            string? albumArtist;
            bool taggedVarious = artists.Any(a => a.Equals(VariousArtists, StringComparison.OrdinalIgnoreCase));
            if (albumName is null || members.Count < 2)
            {
                kind = GroupKind.LooseSongs;
                albumArtist = null;
                albumName = null;
            }
            else if (artists.Count >= CompilationArtistThreshold || taggedVarious)
            {
                kind = GroupKind.Compilation;
                albumArtist = VariousArtists;
            }
            else
            {
                kind = GroupKind.OriginalAlbum;
                albumArtist = artists.FirstOrDefault();
            }

            // Completeness from TrackCount tags (max seen), per disc-agnostic count.
            var expected = members.Max(t => t.TrackCount);
            double completeness = expected > 0 ? Math.Min(1.0, (double)members.Count / expected) : 0;

            groups.Add(new TrackGroup
            {
                Id = folder.Key,
                Kind = kind,
                AlbumName = albumName,
                AlbumArtist = albumArtist,
                Tracks = members,
                Completeness = completeness,
            });
        }

        return groups;
    }

    /// <summary>
    /// The effective filing metadata for one track under a treatment: what Album/AlbumArtist
    /// the name template and the tags should use. The recognised or tagged original album is
    /// preserved in Grouping when the group album wins.
    /// </summary>
    public static TrackInfo ApplyTreatment(TrackInfo track, TrackGroup group, GroupTreatment treatment)
        => treatment switch
        {
            GroupTreatment.KeepAlbum => track with
            {
                Album = group.AlbumName ?? track.Album,
                AlbumArtist = group.AlbumArtist ?? track.AlbumArtist ?? track.Artist,
                Grouping = !string.IsNullOrWhiteSpace(track.Album)
                           && group.AlbumName is not null
                           && !string.Equals(track.Album, group.AlbumName, StringComparison.OrdinalIgnoreCase)
                           && string.IsNullOrWhiteSpace(track.Grouping)
                    ? track.Album
                    : track.Grouping,
            },
            GroupTreatment.SplitToOriginal => track with
            {
                AlbumArtist = track.Artist,
            },
            GroupTreatment.LooseSongs => track with
            {
                Album = null,
                AlbumArtist = track.Artist,
            },
            _ => track,
        };

    private static string? CleanFolderAlbum(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return null;
        // Strip leading "Artist - " and bracketed year/format junk commonly found in rips.
        var name = Regex.Replace(folderName, @"[\[\(](19|20)\d{2}[\]\)]", "").Trim();
        name = Regex.Replace(name, @"\s{2,}", " ");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
