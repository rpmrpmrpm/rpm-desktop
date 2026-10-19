using System.Collections.Concurrent;
using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Works out the gain a track should play at. It combines the library's measurements, the settings, and
/// the listener's choices for the album.
///
/// <para>The decision itself is in <see cref="Normalization"/>. It is kept in the core library, so it
/// can be tested without a database. This class does the fetching: which folder a track is in, what the
/// album has been set to, whether it counts as classical, and what its tracks measured.</para>
///
/// <para>Answers are cached per album, because a record is played track by track. Each track asks the
/// same questions about the same folder. The whole cache is cleared when the library or the settings
/// change. That is cheap, and it is the only rule needed. A wrong gain that stays in the cache would be
/// hard to find.</para>
/// </summary>
public sealed class NormalizationService
{
    private readonly LibraryService _library;
    private readonly SettingsService _settings;

    /// <summary>The answer for each album folder. A record that plays through does not ask
    /// again.</summary>
    private readonly ConcurrentDictionary<string, AlbumFacts> _albums = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The album's answers, except for the track being played. These are whether the album is
    /// levelled at all, and what the whole album measured.</summary>
    private readonly record struct AlbumFacts(bool? Override, bool IsClassical, Level Level);

    public NormalizationService(LibraryService library, SettingsService settings)
    {
        _library = library;
        _settings = settings;

        _library.Changed += Forget;
        _settings.Changed += Forget;
    }

    /// <summary>Clears everything remembered. A new measurement, a record being switched over, or a
    /// changed setting can each change these answers.</summary>
    public void Forget() => _albums.Clear();

    /// <summary>
    /// The gain to play <paramref name="absolutePath"/> at, in dB. The value is zero when no change is
    /// made. That happens when levelling is off, when this record is excluded, or when the track has
    /// not been measured yet. Files outside the collection folder also get zero.
    /// </summary>
    /// <param name="inAlbumOrder">Whether the track is played as part of its album, rather than as one
    /// track in a queue made from several albums. Album gain is used in album order, and track gain
    /// otherwise. See <see cref="Normalization.Resolve"/>.</param>
    public double GainFor(string absolutePath, bool inAlbumOrder = true)
        => Decide(absolutePath, inAlbumOrder).GainDb;

    /// <summary>The full decision, for code that needs to report which reading was used.</summary>
    public NormalizeDecision Decide(string absolutePath, bool inAlbumOrder = true)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !_library.IsOpen)
            return NormalizeDecision.None;

        try
        {
            if (Relative(absolutePath) is not { } relative)
                return NormalizeDecision.None;

            var row = _library.Db.FindByRelativePath(relative);
            if (row is null)
                return NormalizeDecision.None;

            var album = FactsFor(row.Folder);

            var context = new NormalizeContext(
                _settings.NormalizeMode,
                _settings.NormalizeClassical,
                album.Override,
                album.IsClassical,
                inAlbumOrder);

            var track = new Level(row.LoudnessLufs, row.TruePeak);
            var decision = Normalization.Resolve(context, track, album.Level);

            Explain(row.FileName, context, track, album.Level, decision);
            return decision;
        }
        catch
        {
            // If the database does not answer, the music must still play. The record then plays at the
            // level it was mastered at, which is what happened before this feature existed.
            return NormalizeDecision.None;
        }
    }

    /// <summary>
    /// Writes a log line that states the decision and its reason, once for each track started.
    ///
    /// <para>The line is needed because each way this can go wrong is silent. A record played at a
    /// level nobody chose sounds the same as a record played at its mastered level. The possible causes
    /// are the mode setting, the classical rule, the album's own setting, and a track that has not been
    /// measured. The sound does not show which one applies. So the log gives the reason, not only the
    /// number.</para>
    /// </summary>
    private static void Explain(
        string name, NormalizeContext context, Level track, Level album, NormalizeDecision decision)
    {
        if (decision.Basis is not NormalizeBasis.None)
        {
            var level = decision.Basis is NormalizeBasis.Album ? album : track;
            Said.Line("vollevelling",
                $"{name}: {decision.Basis.ToString().ToLowerInvariant()} " +
                $"{level.Lufs:F2} LUFS, peak {level.TruePeak ?? 0:F3} → " +
                $"{decision.GainDb:+0.0;-0.0;0.0} dB");
            return;
        }

        // No gain was applied. The reason is the important part of this line.
        var why = context.AlbumOverride is false ? "this record is set to never"
            : context.Mode is NormalizeMode.Off ? "levelling is off"
            : context.AlbumIsClassical && !context.IncludeClassical ? "classical record"
            : "not measured yet";

        Said.Line("vollevelling", $"{name}: no gain — {why}");
    }

    /// <summary>Whether two tracks are on the same album. This tells a side played in order apart from
    /// a queue that someone assembled. It decides which reading the gain comes from.</summary>
    public bool SameAlbum(string? onePath, string? otherPath)
    {
        if (onePath is null || otherPath is null || !_library.IsOpen)
            return false;

        try
        {
            if (Relative(onePath) is not { } one || Relative(otherPath) is not { } other)
                return false;

            return string.Equals(
                _library.Db.FindByRelativePath(one)?.Folder,
                _library.Db.FindByRelativePath(other)?.Folder,
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private AlbumFacts FactsFor(string folder) => _albums.GetOrAdd(folder, key =>
    {
        var db = _library.Db;
        var meta = db.GetAlbumMeta(key);

        var readings = db.QueryAlbumLoudness(key);
        double lufs = ReplayGain.AlbumLufs(readings);
        double peak = db.QueryAlbumTruePeak(key);

        // A record counts as classical only when the listener has marked it so. A record that is not
        // marked is levelled with the rest of the collection.
        return new AlbumFacts(
            meta.Normalize,
            meta.Classical == true,
            new Level(double.IsFinite(lufs) ? lufs : null, peak > 0 ? peak : null));
    });

    /// <summary>The path as the library stores it. This is null for a file outside the collection. Such
    /// a file has no row, no album, and so no gain.</summary>
    private string? Relative(string absolutePath)
    {
        var root = _library.Root;
        if (string.IsNullOrEmpty(root))
            return null;

        var full = Path.GetFullPath(absolutePath);
        var rooted = Path.GetFullPath(root);
        if (!full.StartsWith(rooted, StringComparison.OrdinalIgnoreCase))
            return null;

        return Path.GetRelativePath(rooted, full).Replace(Path.DirectorySeparatorChar, '/');
    }
}
