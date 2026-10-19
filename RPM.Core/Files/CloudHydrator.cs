namespace RPM.Core.Files;

/// <summary>
/// Pulls cloud placeholders down onto the disk, because somebody asked for them.
///
/// <para>The whole of <see cref="CloudFile"/> exists to make sure this never happens by accident, so
/// this is the one place allowed to do it on purpose. It is the same act either way: opening a
/// placeholder and reading a byte is what makes OneDrive or the macOS file provider fetch it. The
/// difference is that here a person asked for it.</para>
///
/// <para>One file at a time, on purpose. Parallel downloads of a folder of placeholders can saturate
/// the user's network connection, and the screen would not show why.</para>
/// </summary>
public static class CloudHydrator
{
    /// <summary>What became of one file.</summary>
    /// <param name="Path">The track asked for.</param>
    /// <param name="Downloaded">True when it is now on the disk.</param>
    /// <param name="Error">Why not, when it is not: no network, provider signed out, file gone.</param>
    public sealed record Result(string Path, bool Downloaded, string? Error);

    /// <summary>
    /// Fetches each path in turn, reporting (done, total, current file name) as it goes. Never
    /// throws: a file that will not come down is reported, and the rest are still tried, because the
    /// commonest reason is one file rather than the connection.
    /// </summary>
    public static IReadOnlyList<Result> Download(
        IReadOnlyList<string> paths,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<Result>(paths.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, paths.Count, System.IO.Path.GetFileName(paths[i])));
            results.Add(Fetch(paths[i], ct));
        }
        progress?.Report((paths.Count, paths.Count, ""));
        return results;
    }

    private static Result Fetch(string path, CancellationToken ct)
    {
        try
        {
            // Reading the first byte is the request. The provider blocks until the file is whole, and
            // there is no start-and-tell-me-later that works the same on both platforms. So the call
            // returns when the file is really local, which is what the caller needs to know before it
            // reads the tags again.
            using (var stream = File.OpenRead(path))
            {
                Span<byte> one = stackalloc byte[1];
                stream.ReadAtLeast(one, 1, throwOnEndOfStream: false);
            }

            ct.ThrowIfCancellationRequested();

            // Believe the disk, not the read. A provider that handed back a byte and then evicted the
            // file again has not done what was asked.
            return CloudFile.IsDataless(path)
                ? new Result(path, false, "still not local")
                : new Result(path, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Result(path, false, ex.Message);
        }
    }
}
