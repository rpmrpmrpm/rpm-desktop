using System.IO.MemoryMappedFiles;

namespace RPM.Core.Diagnostics;

/// <summary>
/// Reads a ring back, without touching it.
///
/// <para>Opened read-only and mapped read-only, so a bug here cannot become a bug there. This is the
/// reader the tests and the collector use. The Python viewer has its own copy of the same logic, and
/// <c>ScopeFormatTests</c> checks that the two agree by writing a fixture that the Python tests read.</para>
/// </summary>
public sealed class ScopeReader : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _mask;

    private readonly unsafe byte* _base;

    public int Capacity { get; }

    /// <summary>How many records the writer says it has written.</summary>
    public unsafe long Written => Volatile.Read(ref *(long*)(_base + ScopeRing.OffCursor));

    /// <summary>When the ring was made, in Unix nanoseconds, from its header.</summary>
    public unsafe long StartUnixNanos => *(long*)(_base + ScopeRing.OffUnixStart);

    /// <summary>The ring clock's reading at the moment of <see cref="StartUnixNanos"/>. Used to put a
    /// wall time on records written before the first clock sync.</summary>
    public unsafe long StartMonoNanos => *(long*)(_base + ScopeRing.OffMonoStart);

    /// <summary>The oldest slot still in the file. Below this, the records are gone.</summary>
    public long Oldest => Math.Max(0, Written - Capacity);

    /// <summary>How many records the last <see cref="Read"/> asked for and found already overwritten.</summary>
    public long Dropped { get; private set; }

    private unsafe ScopeReader(MemoryMappedFile file, MemoryMappedViewAccessor view, int capacity)
    {
        _file = file;
        _view = view;
        Capacity = capacity;
        _mask = capacity - 1;

        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p;
    }

    public static unsafe ScopeReader Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var file = MemoryMappedFile.CreateFromFile(
            stream, mapName: null, capacity: 0, MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: false);
        var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        long magic;
        long capacity;

        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        try
        {
            magic = Volatile.Read(ref *(long*)(p + ScopeRing.OffMagic));
            capacity = *(long*)(p + ScopeRing.OffCapacity);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        if (magic != ScopeRing.Magic)
        {
            view.Dispose();
            file.Dispose();
            throw new InvalidDataException($"{path} is not a scope ring");
        }

        return new ScopeReader(file, view, (int)capacity);
    }

    /// <summary>
    /// Copies out everything written since <paramref name="fromSeq"/>, oldest first.
    /// </summary>
    /// <param name="fromSeq">The count of records already seen. Zero means "from the beginning",
    /// and <see cref="Written"/> means "only what is new".</param>
    /// <param name="into">Where to put them. Never more than this holds.</param>
    /// <returns>How many were copied. <see cref="Dropped"/> says how many were asked for and gone.</returns>
    public unsafe int Read(long fromSeq, Span<ScopeRecord> into)
    {
        var written = Written;
        var oldest = Math.Max(0, written - Capacity);

        Dropped = Math.Max(0, oldest - fromSeq);
        var from = Math.Max(fromSeq, oldest);

        var count = (int)Math.Min(into.Length, Math.Max(0, written - from));
        var wrote = 0;

        for (var i = 0; i < count; i++)
        {
            var slot = from + i;
            var source = (ScopeRecord*)(_base + ScopeRing.HeaderBytes + ((slot & _mask) << 6));

            // A sequence that is not this slot's is a record half-written or already overwritten.
            // Read it, copy it, then read the sequence AGAIN: if it moved under us, the copy is not
            // trustworthy and is dropped rather than repaired.
            if (Volatile.Read(ref source->Seq) != slot + 1)
                continue;

            var copy = *source;

            if (Volatile.Read(ref source->Seq) != slot + 1)
                continue;

            into[wrote++] = copy;
        }

        return wrote;
    }

    public unsafe void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
