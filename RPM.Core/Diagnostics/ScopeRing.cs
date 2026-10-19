using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace RPM.Core.Diagnostics;

/// <summary>
/// Somewhere to put a fact that nobody may be listening for.
///
/// <para>A memory-mapped file of fixed slots, written round and round. A write is one
/// <see cref="Interlocked.Increment(ref long)"/> on the cursor and one store. There is no lock, no
/// allocation, no syscall, and no branch on whether anybody is reading. That last one is the point.
/// The instrument is most needed when the drawing thread is not answering. A diagnostic that writes to
/// a console from that thread goes silent exactly when it is needed.</para>
///
/// <para><b>The oldest is overwritten, deliberately.</b> The most useful records are the latest ones.
/// A ring that stopped when it filled would hold the wrong part of the run.</para>
/// </summary>
public sealed class ScopeRing : IDisposable
{
    internal const int HeaderBytes = 128;

    /// <summary>"RPMSCOPE", read as a little-endian eight-byte word.</summary>
    internal const long Magic = 0x45504F43534D5052;

    internal const int Version = 1;

    internal const int OffMagic = 0;
    internal const int OffVersion = 8;
    internal const int OffRecordSize = 12;
    internal const int OffCapacity = 16;
    internal const int OffCursor = 24;
    internal const int OffPid = 32;
    internal const int OffMonoStart = 40;
    internal const int OffUnixStart = 48;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _mask;

    private readonly unsafe byte* _base;

    /// <summary>How many slots. Always a power of two, so the index is a mask.</summary>
    public int Capacity { get; }

    private unsafe ScopeRing(MemoryMappedFile file, MemoryMappedViewAccessor view, int capacity)
    {
        _file = file;
        _view = view;
        Capacity = capacity;
        _mask = capacity - 1;

        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p;
    }

    /// <summary>
    /// Makes a ring, replacing whatever was there.
    /// </summary>
    /// <param name="path">The file. Its directory is made if it is not there.</param>
    /// <param name="capacity">Slots, rounded up to a power of two so the index is a mask rather than
    /// a division. The default is 262,144 slots of 64 bytes, which is 16 MB and several minutes of
    /// activity at normal rates.</param>
    public static unsafe ScopeRing Create(string path, int capacity = 262_144)
    {
        capacity = RoundUpToPowerOfTwo(Math.Max(8, capacity));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var bytes = HeaderBytes + (long)capacity * 64;

        // Truncate rather than reuse. A ring left behind by a previous run has a cursor describing
        // records this run never wrote, and nothing downstream could tell the two apart.
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.SetLength(bytes);

        var file = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, mapName: null, bytes, MemoryMappedFileAccess.ReadWrite);
        var view = file.CreateViewAccessor(0, bytes, MemoryMappedFileAccess.ReadWrite);

        var ring = new ScopeRing(file, view, capacity);
        var h = ring._base;

        *(long*)(h + OffCapacity) = capacity;
        *(int*)(h + OffVersion) = Version;
        *(int*)(h + OffRecordSize) = 64;
        *(int*)(h + OffPid) = Environment.ProcessId;
        *(long*)(h + OffMonoStart) = ScopeClock.Nanos();
        *(long*)(h + OffUnixStart) = ScopeClock.UnixNanos();
        *(long*)(h + OffCursor) = 0;

        // The magic goes down LAST, for the same reason a record's sequence does: a reader that opens
        // the file between the truncate and the header sees something that is not yet a ring, and the
        // magic is how it knows to wait rather than to believe the zeroes.
        Volatile.Write(ref *(long*)(h + OffMagic), Magic);
        return ring;
    }

    /// <summary>How many records have ever been written. Never decreases.</summary>
    public unsafe long Written => Volatile.Read(ref *(long*)(_base + OffCursor));

    /// <summary>When the ring was made, in Unix nanoseconds, as written in its header.</summary>
    public unsafe long StartUnixNanos => *(long*)(_base + OffUnixStart);

    /// <summary>
    /// Puts one down. Never throws, never blocks, never allocates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Write(in ScopeRecord record)
    {
        var slot = Interlocked.Increment(ref *(long*)(_base + OffCursor)) - 1;
        var target = (ScopeRecord*)(_base + HeaderBytes + ((slot & _mask) << 6));

        *target = record;

        // The sequence goes down last and with a release, which orders every store above it. A reader
        // that finds anything but this slot's number in that field is looking at a record that is
        // either not finished or already gone, and it knows which.
        Volatile.Write(ref target->Seq, slot + 1);
    }

    private static int RoundUpToPowerOfTwo(int n)
    {
        var p = 8;
        while (p < n)
            p <<= 1;
        return p;
    }

    public unsafe void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
