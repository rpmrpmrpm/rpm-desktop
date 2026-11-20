using RPM.Core.Library;
using RPM.Core.Threading;

namespace RPM.Core.Tests;

/// <summary>
/// Background work gives way to the interface, and runs at its normal priority for as long as it asks for or
/// holds a library gate. On macOS the lever is the thread's scheduling priority, so these tests read it back
/// there. Elsewhere they check the bookkeeping, which decides when the lever is pulled.
/// </summary>
public class GiveWayTests
{
    /// <summary>Runs <paramref name="body"/> on a thread of its own, so a test never leaves a pool thread
    /// lowered.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failed = ex; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the test thread did not finish");
        if (failed is not null)
            throw new Xunit.Sdk.XunitException(failed.ToString());
    }

    [Fact]
    public void A_scope_lowers_the_thread_and_its_end_puts_it_back()
    {
        OnOwnThread(() =>
        {
            var before = GiveWay.DarwinPriority;
            using (GiveWay.Begin())
            {
                Assert.True(GiveWay.IsLowered);
                if (OperatingSystem.IsMacOS())
                    Assert.Equal(GiveWay.DarwinLevel, GiveWay.DarwinPriority);
            }
            Assert.False(GiveWay.IsLowered);
            Assert.Equal(before, GiveWay.DarwinPriority);
        });
    }

    [Fact]
    public void A_gate_held_inside_a_scope_is_held_at_normal_priority()
    {
        OnOwnThread(() =>
        {
            var gate = new Gate("test");
            using (GiveWay.Begin())
            {
                using (gate.Take())
                {
                    Assert.False(GiveWay.IsLowered);
                    if (OperatingSystem.IsMacOS())
                        Assert.NotEqual(GiveWay.DarwinLevel, GiveWay.DarwinPriority);

                    using (gate.Take())
                        Assert.False(GiveWay.IsLowered);   // a reentrant take changes nothing

                    Assert.False(GiveWay.IsLowered);       // still inside the outer hold
                }

                Assert.True(GiveWay.IsLowered);
                if (OperatingSystem.IsMacOS())
                    Assert.Equal(GiveWay.DarwinLevel, GiveWay.DarwinPriority);
            }
        });
    }

    /// <summary>The audio pump and its decoder are raised above the interface's default, on macOS by the
    /// scheduling priority, because the system refuses a QoS class for a .NET thread.</summary>
    [Fact]
    public void The_audio_threads_are_raised()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        int? pump = null, decode = null;
        OnOwnThread(() =>
        {
            RPM.Core.Audio.AudioThread.EnterPlayback();
            pump = GiveWay.DarwinPriority;
        });
        OnOwnThread(() =>
        {
            RPM.Core.Audio.AudioThread.EnterDecode();
            decode = GiveWay.DarwinPriority;
        });

        Assert.Equal(47, pump);
        Assert.Equal(37, decode);
    }

    [Fact]
    public void A_scope_begun_while_a_gate_is_held_lowers_the_thread_only_when_the_gate_is_let_go()
    {
        OnOwnThread(() =>
        {
            var gate = new Gate("test");
            using (GiveWay.Begin())
            {
                var hold = gate.Take();
                using (GiveWay.Begin())
                    Assert.False(GiveWay.IsLowered);
                hold.Dispose();
                Assert.True(GiveWay.IsLowered);
            }
            Assert.False(GiveWay.IsLowered);
        });
    }

    [Fact]
    public void A_refused_take_leaves_the_thread_as_it_was()
    {
        OnOwnThread(() =>
        {
            var gate = new Gate("test");
            gate.Close(TimeSpan.Zero);
            using (GiveWay.Begin())
            {
                Assert.Throws<ObjectDisposedException>(() => gate.Take());
                Assert.True(GiveWay.IsLowered);
            }
            Assert.False(GiveWay.IsLowered);
        });
    }
}
