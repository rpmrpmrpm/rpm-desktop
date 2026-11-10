using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RPM.Core.Diagnostics;

namespace RPM.Core.Library;

/// <summary>What rpm may do with the files that are in a library folder.</summary>
public enum FileHandling
{
    /// <summary>rpm names and files the music under the naming template, writes tags and ids into the
    /// files, and may move, replace and delete them. This is every library rpm made itself.</summary>
    Organised,

    /// <summary>The folder held music before rpm, and the person chose to keep it as it is. rpm never
    /// writes, renames, moves, replaces or deletes a file in it. It may add new files, and it keeps its
    /// own data in <c>.rpm</c>.</summary>
    Kept,
}

/// <summary>
/// Where a library's <see cref="FileHandling"/> is stored: the marker <c>.rpm/files.json</c> in the music
/// folder, and the key <see cref="MetaKey"/> in the database. Both are written, because a library on a
/// network volume has one database per computer, and each computer must respect the choice. The library
/// is kept when either says kept.
/// </summary>
public static class FileKeeping
{
    public const string MetaKey = "files";
    public const string MarkerName = "files.json";
    private const string KeptValue = "kept";

    public static string MarkerPath(string libraryRoot) =>
        Path.Combine(libraryRoot, LibraryDatabase.DataFolderName, MarkerName);

    /// <summary>
    /// The library's mode. <b>This fails closed.</b> A marker that is there but cannot be read, or that
    /// holds anything other than the kept value, is read as kept: a library that should be kept and is
    /// treated as organised would have its files changed, and that cannot be undone.
    /// </summary>
    public static FileHandling Read(string libraryRoot, LibraryDatabase? db)
    {
        if (db?.GetMeta(MetaKey) == KeptValue)
            return FileHandling.Kept;

        // The folders are listed rather than asked File.Exists: File.Exists answers false when a folder
        // cannot be read, and that would read a kept library as organised.
        try
        {
            if (!Directory.EnumerateDirectories(libraryRoot, LibraryDatabase.DataFolderName).Any())
                return FileHandling.Organised;
            return Directory.EnumerateFiles(Path.Combine(libraryRoot, LibraryDatabase.DataFolderName), MarkerName).Any()
                ? FileHandling.Kept
                : FileHandling.Organised;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return FileHandling.Kept;
        }
    }

    /// <summary>Stores the mode. Kept writes the marker and the database key. Organised removes both.</summary>
    public static void Write(string libraryRoot, LibraryDatabase? db, FileHandling mode)
    {
        var marker = MarkerPath(libraryRoot);
        if (mode == FileHandling.Kept)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, JsonSerializer.Serialize(new Dictionary<string, string> { ["files"] = KeptValue }) + "\n");
            db?.SetMeta(MetaKey, KeptValue);
        }
        else
        {
            db?.SetMeta(MetaKey, null);
            File.Delete(marker);
        }
    }
}

/// <summary>A change to a file in a kept library folder was refused. Nothing was written.</summary>
public sealed class KeptFileException(string path, string action)
    : IOException($"{path} is in a folder whose files are kept as they are, so it was not {action}.")
{
    public string FilePath { get; } = path;
}

/// <summary>
/// The guard that keeps the files of a kept library unchanged. The roots of the kept libraries that are
/// open are registered here, and every primitive that changes a file or a folder in place asks
/// <see cref="Refuse"/> first. It is a backstop: the code above it is also told the library is kept and
/// does not try. A path it missed fails here, with nothing written, and not in the person's collection.
///
/// <para>rpm's own data under a kept root is allowed: anything in <c>.rpm/</c>, and the hidden temporary
/// names (<c>.rpm-copy-*</c>, <c>.rpm-part-*</c>) that a new file is written under before it is renamed
/// into place. Creating a file where none exists is also allowed, through <see cref="RefuseReplace"/>.</para>
///
/// <para>Paths are compared without regard to case, on every system. On a case-sensitive disk this can
/// refuse a change outside the kept folder whose path differs only in case. That direction is safe.</para>
/// </summary>
public static class KeptFiles
{
    private static readonly object Gate = new();
    private static string[] _roots = [];

    /// <summary>Registers a kept library root. Called when the library is opened.</summary>
    public static void Open(string libraryRoot)
    {
        var root = Normal(libraryRoot);
        lock (Gate)
        {
            if (!_roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                _roots = [.. _roots, root];
        }
    }

    /// <summary>Removes a root. Called when the library is closed.</summary>
    public static void Close(string libraryRoot)
    {
        var root = Normal(libraryRoot);
        lock (Gate)
            _roots = [.. _roots.Where(r => !string.Equals(r, root, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Whether the path is inside a kept library and is not rpm's own data.</summary>
    public static bool Covers(string path)
    {
        var full = Normal(path);
        foreach (var root in _roots)
        {
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            var relative = full[root.Length..];
            if (relative.Length == 0)
                return true;
            var first = relative.Split(Path.DirectorySeparatorChar)[0];
            if (string.Equals(first, LibraryDatabase.DataFolderName, StringComparison.OrdinalIgnoreCase))
                return false;
            return !Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))
                .StartsWith(".rpm-", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Throws <see cref="KeptFileException"/> when <paramref name="path"/> is in a kept folder.
    /// Call it before writing, moving, renaming or deleting the file or folder at the path.</summary>
    public static void Refuse(string path, string action)
    {
        if (!Covers(path))
            return;
        Said.Line("kept", $"refused: {action} {path}");
        throw new KeptFileException(path, action);
    }

    /// <summary>For the best-effort primitives that answer false instead of throwing: true, and a log
    /// line, when the path is in a kept folder. The caller then returns its failure without writing.</summary>
    public static bool Declines(string path, string action)
    {
        if (!Covers(path))
            return false;
        Said.Line("kept", $"declined: {action} {path}");
        return true;
    }

    /// <summary>Like <see cref="Refuse"/>, but only when something is already at the path. A new file
    /// may be created in a kept folder; an existing one may not be replaced.</summary>
    public static void RefuseReplace(string path, string action)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Refuse(path, action);
    }

    // The root ends with a separator, so /music does not cover /music-old.
    private static string Normal(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }
}

/// <summary>
/// Track ids for a kept library, where no id may be written into a file. The id is derived from the
/// library id and the file's path, so every computer that opens the same folder gets the same ids without
/// writing anything. A file that is moved outside rpm gets a new id.
/// </summary>
public static class KeptIds
{
    /// <summary>A name-based GUID (RFC 9562 version 8) from SHA-256 of
    /// <c>rpm-kept-track\n&lt;library id&gt;\n&lt;relative path&gt;</c>. The path uses forward slashes and
    /// Unicode form C, so a Mac and a Windows computer give the same id.</summary>
    public static string For(string libraryId, string relativePath)
    {
        var path = relativePath.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"rpm-kept-track\n{libraryId}\n{path}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString("D");
    }
}
