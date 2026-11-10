using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using RPM.Core.Library;

namespace RPM.Core.Nas;

/// <summary>What came of moving a library's files.</summary>
public abstract record MoveResult
{
    private MoveResult() { }

    /// <summary>Every file is copied and checked.</summary>
    public sealed record Done(int Files) : MoveResult;

    /// <summary>The move was cancelled or the destination went away. Nothing switched; a new run continues.</summary>
    public sealed record Stopped(int Copied, int Total) : MoveResult;

    /// <summary>The files do not fit. Nothing was copied.</summary>
    public sealed record NoRoom(long Needed, long Free) : MoveResult;

    /// <summary>A file could not be copied or its copy did not match. Nothing switched.</summary>
    public sealed record Failed(string Path, string Detail) : MoveResult;
}

public readonly record struct MoveProgress(int Copied, int Total);

/// <summary>Copies one file to another path. The default reads and writes with a large buffer.</summary>
public delegate Task FileCopy(string from, string to, CancellationToken cancellation);

/// <summary>Renames a file over another. The default is <see cref="File.Move(string, string, bool)"/>.</summary>
public delegate void FileRename(string from, string to);

/// <summary>
/// Copies a library's files to a new folder, checking each copy, so the library can then be opened there.
///
/// <para><b>The source is never changed or deleted.</b> Each file is copied to a temporary name, compared
/// with the source by size and SHA-256, and only then renamed. The journal records each finished file, so
/// a move that stops (the network dropped, the computer slept) continues with the next file and never
/// counts a half-copied one. The caller switches to the new folder only on <see cref="MoveResult.Done"/>.</para>
/// </summary>
public sealed class LibraryMove(FileCopy copy, FileRename rename)
{
    /// <summary>Copies with a 1 MB buffer and renames with <see cref="File.Move(string, string, bool)"/>.</summary>
    public static LibraryMove Default { get; } = new(DefaultCopyAsync, (from, to) => File.Move(from, to, overwrite: true));

    private const string Temporary = ".rpmmove";

    /// <summary>Files under <c>.rpm</c> that belong to the library in its old place and are made new in its
    /// new place: the database (carried by <see cref="CarryDatabase"/>), the library id and the claim.</summary>
    private static readonly string[] NotCopied =
        ["library.db", "library.db-wal", "library.db-shm", "library.json", NasClaim.FileName];

    private static readonly string[] SystemFiles = [".DS_Store", "Thumbs.db", "desktop.ini"];

    public async Task<MoveResult> RunAsync(
        string from, string to, MoveJournal journal, long freeBytes, IProgress<MoveProgress>? progress,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var files = Files(from);
        var pending = files.Where(f => !journal.IsDone(f.Relative, f.Size, f.Modified) || !File.Exists(Path.Combine(to, f.Relative))).ToList();
        var needed = pending.Sum(f => f.Size);
        if (needed > freeBytes)
            return new MoveResult.NoRoom(needed, freeBytes);

        var copied = files.Count - pending.Count;
        progress?.Report(new MoveProgress(copied, files.Count));
        foreach (var file in pending)
        {
            if (cancellation.IsCancellationRequested)
            {
                journal.Save();
                return new MoveResult.Stopped(copied, files.Count);
            }

            var source = Path.Combine(from, file.Relative);
            var target = Path.Combine(to, file.Relative);
            var part = target + Temporary;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await copy(source, part, cancellation);
                if (new FileInfo(part).Length != file.Size || !await SameAsync(source, part, cancellation))
                {
                    File.Delete(part);
                    journal.Save();
                    return new MoveResult.Failed(file.Relative, "the copy does not match the original");
                }

                try
                {
                    rename(part, target);
                }
                catch (IOException) when (File.Exists(part))
                {
                    // Some shares refuse a rename (seen with Samba over a mounted folder). The checked copy is
                    // then copied to its name and checked again there.
                    File.Copy(part, target, overwrite: true);
                    File.Delete(part);
                    if (!await SameAsync(source, target, cancellation))
                    {
                        File.Delete(target);
                        journal.Save();
                        return new MoveResult.Failed(file.Relative, "the copy does not match the original");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                journal.Save();
                return new MoveResult.Stopped(copied, files.Count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                journal.Save();
                // A destination that has gone (the share dropped) is a stop, not a broken file: the next
                // run continues.
                return Directory.Exists(to)
                    ? new MoveResult.Failed(file.Relative, ex.Message)
                    : new MoveResult.Stopped(copied, files.Count);
            }

            journal.Done(file.Relative, file.Size, file.Modified);
            copied++;
            if (copied % 50 == 0)
                journal.Save();
            progress?.Report(new MoveProgress(copied, files.Count));
        }

        journal.Save();
        return new MoveResult.Done(files.Count);
    }

    private sealed record SourceFile(string Relative, long Size, DateTime Modified);

    private static List<SourceFile> Files(string root)
    {
        var files = new List<SourceFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                     IgnoreInaccessible = false,
                 }))
        {
            var relative = Path.GetRelativePath(root, path);
            var name = Path.GetFileName(path);
            if (SystemFiles.Contains(name, StringComparer.OrdinalIgnoreCase) || name.EndsWith(Temporary, StringComparison.Ordinal))
                continue;
            var first = relative.Split(Path.DirectorySeparatorChar)[0];
            if (first == ".rpm" && NotCopied.Contains(name, StringComparer.Ordinal) &&
                Path.GetDirectoryName(relative) == ".rpm")
                continue;
            var info = new FileInfo(path);
            files.Add(new SourceFile(relative, info.Length, info.LastWriteTimeUtc));
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        return files;
    }

    private static async Task<bool> SameAsync(string a, string b, CancellationToken cancellation)
    {
        using var hashA = SHA256.Create();
        using var hashB = SHA256.Create();
        await using (var streamA = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, useAsync: true))
            await hashA.ComputeHashAsync(streamA, cancellation);
        await using (var streamB = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            await hashB.ComputeHashAsync(streamB, cancellation);
        return hashA.Hash!.AsSpan().SequenceEqual(hashB.Hash);
    }

    private static async Task DefaultCopyAsync(string from, string to, CancellationToken cancellation)
    {
        await using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, useAsync: true);
        await using var target = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await source.CopyToAsync(target, 1 << 20, cancellation);
    }

    /// <summary>
    /// Writes one consistent copy of the library's database to <paramref name="to"/>, with SQLite's backup
    /// API through a connection of its own, so the library can stay open while it is read. Nothing is
    /// written when <paramref name="to"/> already exists: a database is never replaced.
    /// </summary>
    /// <param name="from">The database the library uses now (<see cref="MirrorLocation.DatabasePath"/>).</param>
    /// <param name="to">Where the library's new place keeps its database: for a folder on a NAS, the
    /// folder for its new library id under this computer's libraries folder.</param>
    public static void CarryDatabase(string from, string to)
    {
        if (File.Exists(to) || !File.Exists(from))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        var part = to + ".part";
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = from, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = part, Pooling = false }.ToString()))
        {
            source.Open();
            target.Open();
            source.BackupDatabase(target);
        }

        File.Move(part, to, overwrite: false);
    }
}

/// <summary>Which files of a move are copied and checked, kept on disk so a move that stops continues.</summary>
public sealed class MoveJournal
{
    private readonly string _path;
    private readonly Dictionary<string, Entry> _done;

    internal sealed record Entry(long Size, DateTime Modified);

    private MoveJournal(string path, Dictionary<string, Entry> done)
    {
        _path = path;
        _done = done;
    }

    public static MoveJournal Open(string path)
    {
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize(File.ReadAllText(path), MoveJson.Default.DictionaryStringEntry) is { } read)
                return new MoveJournal(path, read);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new MoveJournal(path, new Dictionary<string, Entry>(StringComparer.Ordinal));
    }

    /// <summary>Whether this file was copied and checked, and has not changed since.</summary>
    public bool IsDone(string relative, long size, DateTime modified) =>
        _done.TryGetValue(relative, out var entry) && entry.Size == size && entry.Modified == modified;

    public void Done(string relative, long size, DateTime modified) => _done[relative] = new Entry(size, modified);

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_done, MoveJson.Default.DictionaryStringEntry));
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>Removes the journal after the library has switched.</summary>
    public void Delete()
    {
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

[JsonSerializable(typeof(Dictionary<string, MoveJournal.Entry>))]
internal sealed partial class MoveJson : JsonSerializerContext;
