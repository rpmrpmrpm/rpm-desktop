using System.Text;

namespace RPM.Core.Migration;

/// <summary>
/// Finds a file another app lists, on the disk.
///
/// <para>The readers give paths in Unicode normal form C, so two spellings of one name compare equal.
/// macOS opens a file by either form, but Windows and Linux open only the form the name is stored in, and
/// a file copied from a Mac keeps its decomposed name. When the path is not there as written, each part
/// of it is looked up among the entries of its folder by comparing both in normal form C.</para>
/// </summary>
internal static class LocalPaths
{
    /// <summary>The path as it is on the disk, or null when there is no such file.</summary>
    public static string? Find(string path) =>
        Find(path, File.Exists, Directory.Exists, dir => Directory.EnumerateFileSystemEntries(dir));

    /// <summary>The same, with the file system replaced (for tests).</summary>
    internal static string? Find(string path, Func<string, bool> fileExists, Func<string, bool> dirExists, Func<string, IEnumerable<string>> entries)
    {
        if (fileExists(path))
            return path;

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return null;
        var parts = path[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            var next = Path.Combine(current, parts[i]);
            if (last ? fileExists(next) : dirExists(next))
            {
                current = next;
                continue;
            }

            var wanted = parts[i].Normalize(NormalizationForm.FormC);
            string? found = null;
            try
            {
                found = entries(current).FirstOrDefault(e =>
                    string.Equals(Path.GetFileName(e).Normalize(NormalizationForm.FormC), wanted, StringComparison.Ordinal)
                    && (last ? fileExists(e) : dirExists(e)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (found is null)
                return null;
            current = found;
        }
        return current;
    }
}
