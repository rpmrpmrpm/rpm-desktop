using System.Diagnostics;

namespace RPM.Core.Files;

/// <summary>
/// Opens the file manager on a file, with the file itself picked out: Finder on macOS, File Explorer
/// on Windows, and whatever is registered on Linux.
///
/// <para>Tools that work on files from elsewhere offer this. It answers "which of these two is the good
/// rip?". The tags can be the same for both, and the folder they sit in is not.</para>
///
/// <para>Each platform takes the path as an argument value rather than through a shell, so there is
/// nothing to escape. Failure is silent and reported as false: a file manager that will not open is
/// not something the caller can do anything about.</para>
/// </summary>
public static class OsReveal
{
    /// <summary>Shows <paramref name="path"/> in the file manager. False if nothing opened.</summary>
    public static bool Reveal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (OperatingSystem.IsMacOS())
                return Start("open", ["-R", path]);

            if (OperatingSystem.IsWindows())
                // The comma is part of the switch, and Explorer wants the path quoted inside the one
                // argument: /select,"C:\dir\file.flac".
                return Start("explorer.exe", [$"/select,\"{path}\""]);

            // No selecting on Linux: xdg-open takes a folder and opens it.
            return Start("xdg-open", [Path.GetDirectoryName(path) ?? path]);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the folder itself, with nothing picked out inside it. That is what a record needs, rather
    /// than what a file needs. An album is a folder: the tracks, the cover and the log sit together, and
    /// none of them is highlighted.
    /// </summary>
    /// <returns>False if nothing opened.</returns>
    public static bool OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (OperatingSystem.IsMacOS())
                return Start("open", [path]);

            if (OperatingSystem.IsWindows())
                return Start("explorer.exe", [path]);

            return Start("xdg-open", [path]);
        }
        catch
        {
            return false;
        }
    }

    private static bool Start(string file, string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return Process.Start(info) is not null;
    }
}
