using System.Text;
using RPM.Core.Sharing;

namespace RPM.Core.Nas;

/// <summary>Which library on a NAS a computer opens, and what a new one is called.</summary>
public static class NasLibraryChoice
{
    /// <summary>
    /// The library on the NAS that belongs to the signed-in account: the one whose claim carries its
    /// fingerprint. Null when the computer is signed out or no library has that account, and then a new
    /// library is made.
    /// </summary>
    public static NasLibraryEntry? Choose(NasHello hello, string? accountFingerprint)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (string.IsNullOrEmpty(accountFingerprint))
            return null;
        return hello.Libraries.FirstOrDefault(l => string.Equals(l.Account, accountFingerprint, StringComparison.Ordinal));
    }

    /// <summary>
    /// A folder name for a new library: the share name with the characters SMB refuses taken out, and
    /// " 2", " 3" and so on added when the name is taken. Names are compared without regard to case,
    /// because SMB compares them that way.
    /// </summary>
    public static string NewFolderName(string shareName, IReadOnlyCollection<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var clean = new StringBuilder();
        foreach (var c in shareName ?? "")
        {
            if (char.IsControl(c) || "\\/:*?\"<>|".Contains(c, StringComparison.Ordinal))
                continue;
            clean.Append(c);
        }

        // Windows and SMB drop a trailing dot or space, so a name ending in one would not be the folder made.
        var name = clean.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
            name = "rpm";

        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name))
            return name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} {n}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }
}
