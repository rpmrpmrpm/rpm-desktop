using System.Text.RegularExpressions;
using RPM.Core.Models;
using RPM.Core.Scanning;

namespace RPM.Core.Splitting;

/// <summary>
/// Flags files that are probably a whole album or record in one file. It is cheap and works offline. It
/// uses the duration from the tags that were already read, plus a look at the folder for a sibling cue sheet.
///
/// <para>Length alone is not enough to decide this. A twenty-three minute song is a song, and a record full
/// of songs is still a record full of songs. So a long file counts as an album file only when nothing about
/// it contradicts that. It must not say which track of a record it is. And it must not sit in a folder with
/// an album's worth of sibling files, where the long one is simply the long one.</para>
/// </summary>
public static partial class AlbumFileDetector
{
    /// <summary>What "long" means: past this, a file is longer than songs usually run. On its own this is a
    /// question, not an answer. See the rules in <see cref="Classify"/>.</summary>
    public const double MinCandidateDurationSeconds = 20 * 60;

    /// <summary>Long enough that nothing else has to agree. Songs this long exist. A folder full of them does
    /// not. Files this long are worth asking about.</summary>
    public const double LoneAlbumDurationSeconds = 45 * 60;

    /// <summary>When the file name says what it is ("full album", "side A"), this shorter length is enough. A
    /// side of a 7" record runs shorter than most album sides.</summary>
    public const double HintedAlbumDurationSeconds = 8 * 60;

    /// <summary>This many audio files in one folder is an album's worth of tracks. A long file among them is a
    /// long song. Marking it would ask the user to split a song into itself.</summary>
    public const int AlbumSiblingCount = 5;

    [GeneratedRegex(@"full\s*album|side\s*[ab12]\b|vinyl\s*rip|whole\s*(cd|album|record)|complete\s*album",
        RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex FilenameHint();

    /// <summary>Returns a candidate when the track looks like an album in one file, else null.</summary>
    public static AlbumFileCandidate? Detect(TrackInfo track)
        => Classify(track, FindSiblingCue(track.FilePath), CountAudioFiles(track.FilePath));

    /// <summary>
    /// Batched detection for a whole scan. The cue lookup and the sibling count are read from a
    /// <see cref="FolderScanIndex"/>. That index reads each directory once, instead of using the per-file
    /// <see cref="Directory.GetFiles(string,string)"/> that the single-file overload runs. A folder of N
    /// tracks costs one directory scan, not N. That is the difference between a quick scan and a UI freeze
    /// of about a minute on a big library.
    /// </summary>
    public static AlbumFileCandidate? Detect(TrackInfo track, FolderScanIndex folder)
        => Classify(track, folder.CueFor(track.FilePath), folder.AudioFileCount(track.FilePath));

    /// <summary>
    /// The rules, in the order they settle the question.
    ///
    /// <para>A cue sheet decides it outright. A cue sheet is the track list of the file it names, so nothing
    /// is left to guess. Everything after it is a guess about a file that nobody described. Each way of
    /// guessing wrong has its own way out.</para>
    /// </summary>
    private static AlbumFileCandidate? Classify(TrackInfo track, string? cue, int folderAudioFiles)
    {
        var hint = AlbumFileHint.None;
        if (cue is not null)
            hint |= AlbumFileHint.CueSheet;
        if (track.DurationSeconds >= MinCandidateDurationSeconds)
            hint |= AlbumFileHint.LongDuration;

        bool named = FilenameHint().IsMatch(Path.GetFileNameWithoutExtension(track.FilePath));
        if (named)
            hint |= AlbumFileHint.FilenameHint;

        if (cue is not null)
            return new AlbumFileCandidate(track.FilePath, track.DurationSeconds, cue, hint);

        // The tags say which track of a record it is. Whatever it is, it is not the whole record.
        if (DeclaresItselfATrack(track))
            return null;

        // A folder with an album's worth of files around it: this is the long song on somebody's album.
        if (folderAudioFiles >= AlbumSiblingCount)
            return null;

        bool isAlbumFile =
            (named && track.DurationSeconds >= HintedAlbumDurationSeconds)
            || track.DurationSeconds >= LoneAlbumDurationSeconds
            // Long, and nearly alone in its folder: one file, or the two sides of a record.
            || (track.DurationSeconds >= MinCandidateDurationSeconds && folderAudioFiles <= 2);

        return isAlbumFile
            ? new AlbumFileCandidate(track.FilePath, track.DurationSeconds, cue, hint)
            : null;
    }

    /// <summary>
    /// True when the tags say this file is one track of a record. That means a track number after the first,
    /// or a track count that only an album has. A track 1 with no track count is not counted as a track. A
    /// tagger writes exactly that onto a file that holds a whole side.
    /// </summary>
    private static bool DeclaresItselfATrack(TrackInfo track)
        => track.TrackNumber >= 2 || track.TrackCount >= 3;

    /// <summary>How many audio files share the folder, including this one. The result is 1 when the folder
    /// cannot be read. A file whose neighbours cannot be seen is treated as alone, not as surrounded.</summary>
    private static int CountAudioFiles(string audioPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(audioPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return 1;
            return Directory.EnumerateFiles(dir)
                .Count(f => AudioFileScanner.AudioExtensions.Contains(Path.GetExtension(f)));
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>Finds the cue sheet that describes this audio file. A `.cue` with the same name is used first.
    /// Otherwise it is the folder's only `.cue`, if that cue's FILE line names this file.</summary>
    public static string? FindSiblingCue(string audioPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(audioPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            var sameStem = Path.Combine(dir, Path.GetFileNameWithoutExtension(audioPath) + ".cue");
            if (File.Exists(sameStem))
                return sameStem;

            var cues = Directory.GetFiles(dir, "*.cue");
            if (cues.Length != 1)
                return null;

            var sheet = CueSheetParser.Parse(RPM.Core.Files.ForeignText.ReadFile(cues[0]));
            return string.Equals(sheet?.AudioFileName, Path.GetFileName(audioPath), StringComparison.OrdinalIgnoreCase)
                ? cues[0]
                : null;
        }
        catch
        {
            return null; // The folder or the cue file cannot be read, so there is no candidate.
        }
    }
}

/// <summary>
/// An index of what each folder holds: its <c>.cue</c> files, and how many audio files it has. It is read
/// once per directory. So a whole-folder detection pass costs one
/// <see cref="Directory.GetFiles(string,string)"/> call per directory, not one per file. Cue sheets are
/// parsed at most once each, on demand. Build it from the batch of audio paths first. Then pass it to
/// <see cref="AlbumFileDetector.Detect(TrackInfo, FolderScanIndex)"/> for every track.
/// </summary>
public sealed class FolderScanIndex
{
    // Directory to its .cue full paths (an empty array if there are none, or the folder is unreadable).
    private readonly Dictionary<string, string[]> _cuesByDir;
    // Directory to how many of the batch's audio files live in it.
    private readonly Dictionary<string, int> _audioCountByDir;
    // Cue path to the audio file its FILE line names. It is parsed on first use and then cached. Null means
    // the cue is unreadable or names no file.
    private readonly Dictionary<string, string?> _audioNameByCue = new(StringComparer.OrdinalIgnoreCase);

    private FolderScanIndex(Dictionary<string, string[]> cuesByDir, Dictionary<string, int> audioCountByDir)
    {
        _cuesByDir = cuesByDir;
        _audioCountByDir = audioCountByDir;
    }

    /// <summary>Scans each distinct directory of <paramref name="audioPaths"/> once for its cue sheets. It also
    /// counts the audio files in the batch for each directory.</summary>
    public static FolderScanIndex Build(IEnumerable<string> audioPaths)
    {
        var cues = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in audioPaths)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                continue;

            counts[dir] = counts.TryGetValue(dir, out var n) ? n + 1 : 1;
            if (cues.ContainsKey(dir))
                continue;
            try
            {
                cues[dir] = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.cue") : [];
            }
            catch
            {
                cues[dir] = [];
            }
        }
        return new FolderScanIndex(cues, counts);
    }

    /// <summary>How many audio files of the scanned batch share this file's folder, including this one.</summary>
    public int AudioFileCount(string audioPath)
    {
        var dir = Path.GetDirectoryName(audioPath);
        return dir is not null && _audioCountByDir.TryGetValue(dir, out var n) ? n : 1;
    }

    /// <summary>The cue sheet that describes this audio file. The same-name <c>.cue</c> is used first. Otherwise
    /// it is the folder's only <c>.cue</c>, when that cue's FILE line names this file. It uses the listing that
    /// was scanned in advance, so it does not probe the directory again for each file. It mirrors
    /// <see cref="AlbumFileDetector.FindSiblingCue"/>.</summary>
    public string? CueFor(string audioPath)
    {
        var dir = Path.GetDirectoryName(audioPath);
        if (dir is null || !_cuesByDir.TryGetValue(dir, out var cues) || cues.Length == 0)
            return null;

        var sameStem = Path.GetFileNameWithoutExtension(audioPath) + ".cue";
        foreach (var cue in cues)
        {
            if (string.Equals(Path.GetFileName(cue), sameStem, StringComparison.OrdinalIgnoreCase))
                return cue;
        }

        if (cues.Length != 1)
            return null;

        return string.Equals(AudioNameOf(cues[0]), Path.GetFileName(audioPath), StringComparison.OrdinalIgnoreCase)
            ? cues[0]
            : null;
    }

    private string? AudioNameOf(string cuePath)
    {
        if (_audioNameByCue.TryGetValue(cuePath, out var name))
            return name;
        try
        {
            name = CueSheetParser.Parse(RPM.Core.Files.ForeignText.ReadFile(cuePath))?.AudioFileName;
        }
        catch
        {
            name = null;
        }
        _audioNameByCue[cuePath] = name;
        return name;
    }
}
