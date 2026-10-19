using RPM.Core.Library;

namespace RPM.Desktop.Presentation.Common;

/// <summary>Finds the stored <see cref="PlaylistCover.Records"/> covers whose folders are gone. A
/// folder is gone when it no longer exists at its path. A folder that cannot be reached only because
/// its disk is not plugged in does not count.
///
/// <para>This is a pure function. Given the nodes and a way to ask whether a path exists now, it
/// decides which nodes to drop, and it changes nothing itself. <see cref="Find"/> is the whole rule and
/// the only entry point. The rule is kept here, not in <c>MainPage.Panes.cs</c>, so that it can be
/// tested directly, and not only through a real directory and a real database.</para>
/// </summary>
internal static class StaleRecordCovers
{
    /// <summary>The macOS convention for an externally mounted volume. Everything under
    /// <c>/Volumes/Name</c> is on that named volume. The whole prefix disappears from the file system
    /// when the volume is unmounted. Nothing under it exists any more, including the mount point
    /// itself.</summary>
    private const string MacVolumePrefix = "/Volumes/";

    /// <summary>The udisks2 convention, used by most modern Linux distributions. The app ships for
    /// linux-arm, linux-arm64 and linux-x64, so this is a real platform. Two segments follow the
    /// prefix: the user name, then the volume name.</summary>
    private const string LinuxRunMediaPrefix = "/run/media/";

    /// <summary>An older convention that some desktop environments still use in place of
    /// <see cref="LinuxRunMediaPrefix"/>. The shape is the same: the user, then the volume.</summary>
    private const string LinuxMediaPrefix = "/media/";

    /// <summary>The manual mount convention. There is one segment and no per-user layer, because
    /// whoever ran
    /// <c>mount</c> chose the name directly.</summary>
    private const string LinuxMntPrefix = "/mnt/";

    /// <summary>The ids of every node whose <see cref="PlaylistCover.Records"/> cover names a folder
    /// that is gone. A folder that is only unreachable because its own volume is not mounted is not
    /// counted.
    /// </summary>
    /// <param name="nodes">Every playlist node to check. A node that is not a <see
    /// cref="PlaylistCover.Records"/> cover is skipped. The other three kinds cannot go stale, because
    /// nothing outside the choice itself is part of the picture.</param>
    /// <param name="pathExists">Answers whether a directory exists right now. It is passed in, not read
    /// from the real disk, and that is what makes this testable. The same function must answer
    /// differently for a folder and for the volume that the folder lives on, and a test can give it
    /// those answers.</param>
    internal static IReadOnlyList<long> Find(
        IReadOnlyList<PlaylistNode> nodes, Func<string, bool> pathExists)
    {
        var stale = new List<long>();
        foreach (var node in nodes)
            if (node.Cover is PlaylistCover.Records { Folders: var folders }
                && folders.Any(f => IsGenuinelyGone(f, pathExists)))
                stale.Add(node.Id);

        return stale;
    }

    /// <summary>A folder counts as gone only when the disk it would be on is still there to answer for
    /// it. Unplugging the external drive that holds four chosen albums must not look the same as
    /// someone moving all four albums out of their folders. The first means "ask again later". The
    /// second cannot be recovered from by a Records choice on its own.
    /// </summary>
    private static bool IsGenuinelyGone(string folder, Func<string, bool> pathExists)
    {
        if (pathExists(folder))
            return false;

        // No named volume to doubt: this is an ordinary path on the machine's own disk. This does not guard
        // against that disk disappearing during the session, and neither does any other read in this app.
        var volume = VolumeRootOf(folder);
        return volume is null || pathExists(volume);
    }

    /// <summary>The mount point that a path belongs to, when the path names one explicitly. Null for a
    /// path that does not, which <see cref="IsGenuinelyGone"/> reads as "the machine's own disk".
    ///
    /// <para>This is based on naming conventions. It is not a real mount table lookup. It recognises
    /// the layouts that removable drives use for this app's users: macOS's <c>/Volumes</c>, and Linux's
    /// <c>/run/media</c>, <c>/media</c> and <c>/mnt</c>. Nothing more general is recognised. A general
    /// check, "is this path a mount point?", has no portable answer. It would need <c>/proc/mounts</c>
    /// or each platform's own volume API. A path that does not match one of these four prefixes is read
    /// as being on the machine's own disk. That is right for the ordinary case. It is wrong only for a
    /// removable drive mounted somewhere this code does not look.</para>
    /// </summary>
    private static string? VolumeRootOf(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Path.GetPathRoot returns "C:\" for any path on that drive. Checking that the root exists
            // is cheap, and it exists unless the machine itself is gone.
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : root;
        }

        return MountRoot(path, MacVolumePrefix, segments: 1)
            ?? MountRoot(path, LinuxRunMediaPrefix, segments: 2)
            ?? MountRoot(path, LinuxMediaPrefix, segments: 2)
            ?? MountRoot(path, LinuxMntPrefix, segments: 1);
    }

    /// <summary>The mount point named by <paramref name="prefix"/>, plus the next <paramref
    /// name="segments"/> path segments after it. For example, <c>/Volumes/</c> plus 1 segment turns
    /// <c>/Volumes/Backup/Music/Album</c> into <c>/Volumes/Backup</c>. <c>/media/</c> plus 2 segments
    /// turns <c>/media/user/Backup/Music/Album</c> into <c>/media/user/Backup</c>. Returns null when
    /// the path does not start with the prefix, or when nothing follows the prefix to name a
    /// volume.</summary>
    private static string? MountRoot(string path, string prefix, int segments)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var rest = path[prefix.Length..];
        var at = 0;
        for (var i = 0; i < segments; i++)
        {
            var slash = rest.IndexOf('/', at);
            if (slash < 0) { at = rest.Length; break; }
            at = slash + 1;
        }

        var name = rest[..at].TrimEnd('/');
        return name.Length == 0 ? null : prefix + name;
    }
}
