namespace RPM.Core.Startup;

/// <summary>What a missing piece costs, and so whether the app may open without it.</summary>
public enum PreflightSeverity
{
    /// <summary>The app cannot do its job without this. If it is missing, the app refuses to
    /// start.</summary>
    Required,

    /// <summary>A feature is lost, and the rest of the app still works. It is reported, never
    /// fatal.</summary>
    Optional,
}

/// <summary>Where a piece of the payload is looked for.</summary>
public enum PreflightKind
{
    /// <summary>An external executable the app shells out to, found by its own resolver.</summary>
    Tool,

    /// <summary>A file shipped beside the app, such as a model or an asset.</summary>
    File,
}

/// <summary>
/// One thing the installed app expects to find.
/// </summary>
/// <param name="Name">What it is called on disk ("ffmpeg", "beat_this.onnx"). It is shown in the
/// report.</param>
/// <param name="PurposeKey">The localisation key of the one line that says what it is for.</param>
/// <param name="Locate">
/// Where it is, or null when it cannot be found. This is a function, not a path. A tool is resolved
/// in steps (bundled copy, then PATH, then the usual install folders). A file is not.
/// </param>
/// <param name="MinBytes">
/// The smallest size that a real copy reaches. This is not a version check. It catches a file that
/// is there but is no good. Examples are a zero-byte placeholder from an interrupted install, or the
/// empty file that an antivirus leaves when it quarantines a large binary it does not recognise. Set
/// it well below the true size.
/// </param>
public sealed record PreflightItem(
    string Name,
    string PurposeKey,
    Func<string?> Locate,
    PreflightSeverity Severity = PreflightSeverity.Required,
    long MinBytes = 1024);

/// <summary>Why one item failed.</summary>
public enum PreflightFault
{
    /// <summary>Nothing of that name anywhere it was looked for.</summary>
    Missing,

    /// <summary>Found, but too small to be the real file. It is truncated or quarantined.</summary>
    Damaged,

    /// <summary>Found, but could not be opened. The cause is permissions, a locked file or a broken
    /// symlink.</summary>
    Unreadable,
}

/// <param name="Path">Where it was found, when it was found at all.</param>
/// <param name="Detail">The size that was too small, or the exception message. It is whatever names the
/// fault.</param>
public sealed record PreflightFailure(
    PreflightItem Item,
    PreflightFault Fault,
    string? Path = null,
    string? Detail = null);

/// <param name="Failures">Everything wrong, required and optional together, in manifest order.</param>
public sealed record PreflightReport(IReadOnlyList<PreflightFailure> Failures)
{
    /// <summary>What must be fixed before the app can open.</summary>
    public IReadOnlyList<PreflightFailure> Blocking =>
        [.. Failures.Where(f => f.Item.Severity == PreflightSeverity.Required)];

    /// <summary>What is merely lost: a feature that will be absent.</summary>
    public IReadOnlyList<PreflightFailure> Degraded =>
        [.. Failures.Where(f => f.Item.Severity == PreflightSeverity.Optional)];

    /// <summary>True when the app may open. Optional failures do not stop it.</summary>
    public bool CanStart => Blocking.Count == 0;
}

/// <summary>
/// Checks that the app was installed whole, before it tries to run.
///
/// <para>A production install is not assembled on the machine. No script downloads anything. The
/// installer either wrote the files or it did not. So the failures this catches are an
/// interrupted install, an antivirus that quarantined an unsigned binary, a half-applied update, or a
/// user who moved the .exe out of its folder. Without this check, each of these shows up later and
/// somewhere else. A track has no length (ffmpeg missing). An album never gets a beat grid (the model
/// missing). Identification quietly never matches (fpcalc missing). None of these says that the app
/// is not all there, which is the real cause in every case.</para>
///
/// <para>The check is a few stat() calls against a manifest. It costs nothing to run on every launch,
/// and it runs before any service is built.</para>
/// </summary>
public static class Preflight
{
    /// <summary>Runs the manifest and reports everything wrong with it. Never throws.</summary>
    public static PreflightReport Check(IEnumerable<PreflightItem> manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var failures = new List<PreflightFailure>();
        foreach (var item in manifest)
        {
            string? path;
            try
            {
                path = item.Locate();
            }
            catch (Exception ex)
            {
                // A resolver that throws is treated the same as one that finds nothing. It must not
                // stop the launch, because this whole class is the safety net.
                failures.Add(new PreflightFailure(item, PreflightFault.Missing, null, ex.Message));
                continue;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                failures.Add(new PreflightFailure(item, PreflightFault.Missing));
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    failures.Add(new PreflightFailure(item, PreflightFault.Missing, path));
                else if (info.Length < item.MinBytes)
                    failures.Add(new PreflightFailure(
                        item, PreflightFault.Damaged, path, $"{info.Length:n0} bytes"));
            }
            catch (Exception ex)
            {
                failures.Add(new PreflightFailure(item, PreflightFault.Unreadable, path, ex.Message));
            }
        }

        return new PreflightReport(failures);
    }

    /// <summary>
    /// The report as text, for the clipboard and the log. It is plain English on purpose. It is pasted
    /// into a bug report or a support thread, where a translated path helps nobody.
    /// </summary>
    public static string Describe(PreflightReport report, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            "RPM preflight",
            $"app folder: {baseDirectory}",
            $"os: {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})",
            "",
        };

        if (report.Failures.Count == 0)
        {
            lines.Add("everything present");
            return string.Join(Environment.NewLine, lines);
        }

        foreach (var f in report.Failures)
        {
            var severity = f.Item.Severity == PreflightSeverity.Required ? "REQUIRED" : "optional";
            var where = f.Path is null ? "not found" : f.Path;
            var detail = f.Detail is null ? "" : $" — {f.Detail}";
            lines.Add($"[{severity}] {f.Item.Name}: {f.Fault.ToString().ToLowerInvariant()} ({where}){detail}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
