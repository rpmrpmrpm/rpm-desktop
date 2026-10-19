using System.Runtime.InteropServices;

namespace RPM.Core.Diagnostics;

/// <summary>
/// What a record says it is.
///
/// <para><b>The numbers are permanent.</b> The Python reader carries them as its own enumeration and
/// nothing in either language checks the agreement, so a renumbering here would silently relabel
/// every trace anybody has kept. They may be added to; they may not be changed.</para>
/// </summary>
public enum ScopeKind : ushort
{
    None = 0,

    JobEnqueue = 1,
    JobAdmit = 2,
    JobStart = 3,
    JobBlock = 4,
    JobResume = 5,
    JobEnd = 6,
    JobFault = 7,
    JobCancel = 8,

    GateWait = 10,
    GateHold = 11,

    FrameBegin = 20,
    FrameEnd = 21,
    FrameOverrun = 22,
    Layout = 23,
    Render = 24,

    Input = 30,
    DispatchDepth = 31,

    Throw = 40,

    ClockSync = 50,

    /// <summary>A stack sample, from the collector.</summary>
    Sample = 60,
    GcSuspend = 61,
    GcRestart = 62,
    Contention = 63,
    WaitHandle = 64,
    PoolQueue = 65,
    Counter = 66,
    ThreadCpu = 67,
    HttpRequest = 68,
    HttpConnection = 69,

    /// <summary>A line the app said about itself: the name is the channel, the detail is the words.
    ///
    /// <para>The line is written here and not to the debug channel. Each <c>Debug.WriteLine</c> that
    /// reaches an attached debugger is a round trip to it, and a diagnostic that writes a line per
    /// realised row would stop the process hundreds of times a second. In the ring the line costs one
    /// record. It lands on the same clock as the stalls and throws it is about, so it can be read
    /// beside them.</para></summary>
    Said = 70,

    /// <summary>The run has begun ending normally: the main window closed, or the process is exiting.
    /// A run with this record and no crash is settled by the crash harvest. A crash after it is still
    /// reported, except a Windows fail-fast on close, which is a known crash (see
    /// <c>RPM.Core.Crashes.CrashHarvest</c>).</summary>
    Closing = 71,
}

/// <summary>
/// One thing that happened, in sixty-four bytes.
///
/// <para><b>Fixed width, and that is the whole design.</b> A variable-length record needs a length to
/// be read, a length has to be written before the payload, and a reader that arrives between the two
/// reads a length that does not describe what follows it. Fixed width means the slot number alone says
/// where a record is and how far away the next one is, so a reader can be wrong about a record without
/// ever being lost in the file.</para>
///
/// <para><b><see cref="Seq"/> is written last.</b> It carries the slot number, so a reader that finds
/// anything else in that field is looking at a record either half-written or already overwritten, and
/// knows which. This is the only synchronisation between writer and reader, and it costs one release
/// store.</para>
///
/// <para>There is no text in it. <see cref="NameId"/> and <see cref="Detail"/> index a table that only
/// ever grows, because a string in a record is an allocation, and an allocation on this path would
/// make the instrument part of what it is measuring.</para>
///
/// <para>Mutable, and laid out explicitly, so a caller can fill one with an object initialiser and the
/// writer can store the sequence after everything else.</para>
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct ScopeRecord
{
    /// <summary>The slot this record was written into, plus one. Written last, with a release.</summary>
    [FieldOffset(0)] public long Seq;

    /// <summary>When, from <see cref="ScopeClock"/>.</summary>
    [FieldOffset(8)] public long Nanos;

    /// <summary>The job this is about. Zero where the kind has no job.</summary>
    [FieldOffset(16)] public long JobId;

    /// <summary>The job that started it. The causal edge, and the field that turns a list into a tree.</summary>
    [FieldOffset(24)] public long ParentId;

    /// <summary>Kind-specific. Usually a duration in nanoseconds.</summary>
    [FieldOffset(32)] public long A;

    /// <summary>Kind-specific. Usually processor nanoseconds, or a count.</summary>
    [FieldOffset(40)] public long B;

    /// <summary>What it is called, in the string table.</summary>
    [FieldOffset(48)] public int NameId;

    /// <summary>Who it waited behind, which device, or which host: a second string, or a small number.</summary>
    [FieldOffset(52)] public int Detail;

    /// <summary>Which <see cref="ScopeKind"/>.</summary>
    [FieldOffset(56)] public ushort Kind;

    /// <summary>A dense thread id of our own. The operating system's is too wide and is not stable.</summary>
    [FieldOffset(58)] public ushort Thread;

    /// <summary>Lane in the low byte, device in the next, room for two more.</summary>
    [FieldOffset(60)] public int Flags;
}
