using System.Runtime.InteropServices;
using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

public class ScopeRecordTests
{
    [Fact]
    public void Record_IsExactlySixtyFourBytes()
    {
        // A cache line. If this changes, every reader in two languages changes with it, so it is
        // pinned here and not discovered there.
        Assert.Equal(64, Marshal.SizeOf<ScopeRecord>());
    }

    [Fact]
    public void Record_FieldsSitWhereTheReadersExpect()
    {
        Assert.Equal(0, Marshal.OffsetOf<ScopeRecord>(nameof(ScopeRecord.Seq)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<ScopeRecord>(nameof(ScopeRecord.Nanos)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<ScopeRecord>(nameof(ScopeRecord.JobId)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<ScopeRecord>(nameof(ScopeRecord.ParentId)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<ScopeRecord>(nameof(ScopeRecord.A)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<ScopeRecord>(nameof(ScopeRecord.B)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<ScopeRecord>(nameof(ScopeRecord.NameId)).ToInt32());
        Assert.Equal(52, Marshal.OffsetOf<ScopeRecord>(nameof(ScopeRecord.Detail)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<ScopeRecord>(nameof(ScopeRecord.Kind)).ToInt32());
        Assert.Equal(58, Marshal.OffsetOf<ScopeRecord>(nameof(ScopeRecord.Thread)).ToInt32());
        Assert.Equal(60, Marshal.OffsetOf<ScopeRecord>(nameof(ScopeRecord.Flags)).ToInt32());
    }

    [Fact]
    public void Kinds_HaveStableNumbers()
    {
        // The Python reader carries these numbers. They may be added to and never renumbered.
        Assert.Equal(1, (int)ScopeKind.JobEnqueue);
        Assert.Equal(10, (int)ScopeKind.GateWait);
        Assert.Equal(20, (int)ScopeKind.FrameBegin);
        Assert.Equal(30, (int)ScopeKind.Input);
        Assert.Equal(40, (int)ScopeKind.Throw);
        Assert.Equal(50, (int)ScopeKind.ClockSync);
    }
}
