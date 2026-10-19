using System.Text.Json;

namespace TempoBench;

/// <summary>
/// Reads the reference tempo of every track in a corpus from a tempo_eval checkout.
///
/// <para>The annotations are JAMS files, one per track, each with several annotations of the same
/// record by different people at different times. Which one is used matters more than anything else
/// in a run: GTZAN's version 1.0 says one reggae track is 57 BPM, and the annotations derived from
/// Marchand's beats say 114. A system measured against one and compared with a table built on the
/// other is not a valid comparison. So the version is given on the command line, never guessed, and
/// it is printed with the results.</para>
/// </summary>
internal static class References
{
    /// <summary>
    /// The annotated beat times per track, for the corpora that have them (GTZAN does, GiantSteps
    /// does not). Empty when the corpus has none, which is not an error; it means drift cannot be
    /// measured for that corpus.
    /// </summary>
    /// <remarks>The same JAMS files hold beats, bar lines and eighth notes under one namespace,
    /// identified by <c>sandbox.annotation_type</c>. Reading the wrong one measures the grid against
    /// the bar lines and reports the record as four times too slow.</remarks>
    public static Dictionary<string, double[]> LoadBeats(string refsRoot, string corpus)
    {
        var dir = Path.Combine(refsRoot, corpus, "jams");
        var found = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir))
            return found;

        foreach (var file in Directory.EnumerateFiles(dir, "*.jams"))
        {
            var text = File.ReadAllText(file).Replace(": NaN", ": null", StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("annotations", out var annotations))
                continue;

            foreach (var annotation in annotations.EnumerateArray())
            {
                if (annotation.GetProperty("namespace").GetString() != "beat")
                    continue;

                if (!annotation.TryGetProperty("sandbox", out var sandbox) ||
                    !sandbox.TryGetProperty("annotation_type", out var kind) ||
                    kind.GetString() != "beat")
                    continue;

                var beats = annotation.GetProperty("data").EnumerateArray()
                    .Select(e => e.GetProperty("time").GetDouble())
                    .Where(t => t >= 0)
                    .Order()
                    .ToArray();

                if (beats.Length >= 8)
                    found[Path.GetFileNameWithoutExtension(file)] = beats;

                break;
            }
        }

        return found;
    }

    /// <summary>The reference tempo per track, keyed by file name without extension.</summary>
    public static Dictionary<string, double> Load(string refsRoot, string corpus, string version)
    {
        var dir = Path.Combine(refsRoot, corpus, "jams");
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"No JAMS references at {dir}.");

        var found = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var versionsSeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(dir, "*.jams"))
        {
            // JAMS writes an unset duration as a bare `NaN`, which is not valid JSON and which no
            // parser accepts. Nothing here reads a duration, so it is replaced with null first.
            var text = File.ReadAllText(file).Replace(": NaN", ": null", StringComparison.Ordinal);

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("annotations", out var annotations))
                continue;

            foreach (var annotation in annotations.EnumerateArray())
            {
                if (annotation.GetProperty("namespace").GetString() != "tempo")
                    continue;

                var annotationVersion = annotation.TryGetProperty("annotation_metadata", out var meta) &&
                                    meta.TryGetProperty("version", out var v)
                    ? v.GetString() ?? ""
                    : "";

                versionsSeen.Add(annotationVersion);
                if (annotationVersion != version)
                    continue;

                // Some corpora have a second, less salient tempo for a record that can be counted two
                // ways. The one with the highest confidence is the reference; the other is the kind
                // of error that Accuracy2 accepts.
                double best = 0, bestConfidence = double.NegativeInfinity;
                foreach (var entry in annotation.GetProperty("data").EnumerateArray())
                {
                    double bpm = entry.GetProperty("value").GetDouble();
                    double confidence = entry.TryGetProperty("confidence", out var c) &&
                                        c.ValueKind == JsonValueKind.Number
                        ? c.GetDouble()
                        : 1.0;

                    if (bpm > 0 && confidence > bestConfidence)
                        (best, bestConfidence) = (bpm, confidence);
                }

                if (best > 0)
                    found[Path.GetFileNameWithoutExtension(file)] = best;
            }
        }

        if (found.Count == 0)
            throw new InvalidOperationException(
                $"No tempo annotations at version '{version}' in {dir}. " +
                $"Versions there: {string.Join(", ", versionsSeen.Order())}.");

        return found;
    }
}
