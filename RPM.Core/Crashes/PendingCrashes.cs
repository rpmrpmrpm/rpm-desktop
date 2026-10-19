using System.Text.Json;

namespace RPM.Core.Crashes;

/// <summary>The reports in <c>crashes/pending</c>. They are read when they are needed, not counted by
/// whoever wrote them.</summary>
public sealed class PendingCrashes(string directory)
{
    /// <summary>Every readable report. A report that cannot be read is deleted, because it can never be
    /// sent.</summary>
    public IReadOnlyList<PendingCrash> Read()
    {
        if (!Directory.Exists(directory))
            return [];

        var reports = new List<PendingCrash>();
        foreach (var path in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllText(path), CrashJson.Default.PendingCrash) is { CrashId.Length: > 0 } report)
                {
                    reports.Add(report);
                    continue;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            catch (JsonException) { }

            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return reports;
    }

    public void Remove(string crashId)
    {
        try { File.Delete(Path.Combine(directory, $"{crashId}.json")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes every report file without reading it. There is nothing in them to decide
    /// on.</summary>
    public void RemoveAll()
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.GetFiles(directory, "*.json"))
        {
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
