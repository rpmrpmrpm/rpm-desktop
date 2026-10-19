using System.Collections.Concurrent;
using System.Text;

namespace RPM.Core.Diagnostics;

/// <summary>
/// The names, kept once each and referred to by number.
///
/// <para>A record carries no text, because text in a record is a length to agree about and an
/// allocation to make, and an allocation on the write path would make the instrument part of what it
/// is measuring. So a name is written down the first time it is used and is an integer forever after.
/// The dictionary lookup falls on the FIRST use of each name. There are a few hundred names over a
/// session, nearly all of them in the first seconds. Every use after that reads a field the caller
/// already holds.</para>
///
/// <para><b>Append-only, and the count is written last.</b> A reader that attaches an hour into a run
/// has to resolve every name in every record it can still see, so nothing is ever removed and nothing
/// is ever renumbered. The count at the head is raised only once an entry is completely written, so a
/// reader sees an entry whole or does not see it at all.</para>
/// </summary>
public sealed class ScopeNames : IDisposable
{
    /// <summary>"RPMSCPNM", read as a little-endian eight-byte word.</summary>
    private const long Magic = 0x4D4E5043534D5052;

    private const int HeadBytes = 12;

    /// <summary>How many distinct names a table will hold. See <see cref="Id"/>.</summary>
    private const int MaxNames = 20_000;

    private readonly FileStream _file;
    private readonly Lock _write = new();
    private readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly List<string> _byId = [];

    private ScopeNames(FileStream file) => _file = file;

    /// <summary>
    /// A table with nothing in it, replacing whatever was there.
    ///
    /// <para>For a producer that has just made a fresh ring: no record in it refers to an old id, so
    /// the previous run's words are not merely useless but dangerous, since an id means nothing
    /// except relative to the table that issued it.</para>
    /// </summary>
    public static ScopeNames Fresh(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        var names = new ScopeNames(file);
        names.WriteHead(0);
        return names;
    }

    /// <summary>Opens the table, reading back whatever a previous run left in it.</summary>
    public static ScopeNames Open(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        var names = new ScopeNames(file);

        if (file.Length >= HeadBytes)
            names.ReadExisting();
        else
            names.WriteHead(0);

        return names;
    }

    /// <summary>The number for a name, making one if this is the first time it has been seen.</summary>
    /// <returns>Zero for nothing and for the empty string, which are the same fact.</returns>
    public int Id(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return 0;

        // Bounded, because lines the app says go through here too, and each of those is unique. A few
        // hundred names is what a run of the instrument itself produces. Anything past MaxNames is
        // dropped, so a long session's log cannot grow the file and the dictionary without end. A
        // dropped line reads as "no name". That is honest: it shows that a line was said, not what it was.
        if (_byId.Count >= MaxNames)
            return 0;

        if (_ids.TryGetValue(name, out var existing))
            return existing;

        lock (_write)
        {
            if (_ids.TryGetValue(name, out existing))
                return existing;

            var bytes = Encoding.UTF8.GetBytes(name);
            if (bytes.Length > ushort.MaxValue)
                bytes = bytes[..ushort.MaxValue];

            _file.Seek(0, SeekOrigin.End);
            _file.WriteByte((byte)(bytes.Length & 0xFF));
            _file.WriteByte((byte)(bytes.Length >> 8));
            _file.Write(bytes);
            _file.Flush();

            var id = _byId.Count + 1;
            _byId.Add(name);
            _ids[name] = id;

            // Only now is the entry whole, so only now does the count admit to it.
            WriteHead(_byId.Count);
            return id;
        }
    }

    /// <summary>The name for a number. Null for zero, and for anything this table has not read yet.</summary>
    public string? Name(int id)
    {
        lock (_write)
            return id >= 1 && id <= _byId.Count ? _byId[id - 1] : null;
    }

    /// <summary>Every name, in the order they were given numbers.</summary>
    public IReadOnlyList<string> All()
    {
        lock (_write)
            return _byId.ToArray();
    }

    private void WriteHead(int count)
    {
        Span<byte> head = stackalloc byte[HeadBytes];
        BitConverter.TryWriteBytes(head[..8], Magic);
        BitConverter.TryWriteBytes(head[8..], count);
        _file.Seek(0, SeekOrigin.Begin);
        _file.Write(head);
        _file.Flush();
    }

    private void ReadExisting()
    {
        _file.Seek(0, SeekOrigin.Begin);
        var head = new byte[HeadBytes];

        if (_file.ReadAtLeast(head, HeadBytes, throwOnEndOfStream: false) != HeadBytes ||
            BitConverter.ToInt64(head, 0) != Magic)
        {
            // Not one of ours, or a previous run died before the head was written. Start again rather
            // than hand out ids that mean something else.
            _file.SetLength(0);
            WriteHead(0);
            return;
        }

        var count = BitConverter.ToInt32(head, 8);
        var length = new byte[2];

        for (var i = 0; i < count; i++)
        {
            if (_file.ReadAtLeast(length, 2, throwOnEndOfStream: false) != 2)
                break;

            var n = length[0] | (length[1] << 8);
            var bytes = new byte[n];
            if (_file.ReadAtLeast(bytes, n, throwOnEndOfStream: false) != n)
                break;

            var name = Encoding.UTF8.GetString(bytes);
            _byId.Add(name);
            _ids[name] = _byId.Count;
        }
    }

    public void Dispose() => _file.Dispose();
}
