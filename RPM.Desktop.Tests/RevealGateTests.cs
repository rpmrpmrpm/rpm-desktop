using RPM.Desktop.Presentation.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// When the splash may come off.
///
/// <para>Three facts and two bounds. The facts: the first screen has a SHAPE, its PICTURES have landed,
/// and the drawing thread has been answering. The bounds: a patience counted from the shape, for pictures
/// that never arrive, and a backstop counted from launch, for a screen that never reports at all.</para>
///
/// <para>Each is here for a reason. The shape is needed because a gate that asks only about the thread
/// opens over an empty wall. The read is on a worker, so the thread is idle BECAUSE the screen is not
/// ready. The pictures are needed because a screen with shape and no covers is an app that appears and
/// then fills in. The two bounds are separate because one bound counted from launch can expire before
/// there is a window to put anything in.</para>
///
/// <para>Calm does not decide WHETHER to reveal. Once the fade runs on the compositor, a calm thread is the
/// wrong test: the gate would open on quiet before the covers had landed, and the app would fade up onto
/// blank squares. Calm now only decides which way is reported.</para>
/// </summary>
public class RevealGateTests
{
    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(20);

    private static RevealGate Gate() => new(Floor, Patience, Backstop);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>One turn: how late it was, where the run is, when the screen got its shape (null while it
    /// has none), and whether its pictures are in.</summary>
    private static RevealWay? Turn(RevealGate gate, double gapMs, double atMs, double? upAtMs, bool dressed) =>
        gate.Turn(gapMs, Ms(atMs), upAtMs is { } up ? Ms(up) : null, dressed);

    /// <summary>An on-time turn.</summary>
    private static RevealWay? Calm(RevealGate gate, double atMs, double? upAtMs, bool dressed) =>
        Turn(gate, 8, atMs, upAtMs, dressed);

    /// <summary>A turn the thread was held through.</summary>
    private static RevealWay? Held(RevealGate gate, double atMs, double? upAtMs, bool dressed) =>
        Turn(gate, 200, atMs, upAtMs, dressed);

    // ── the floor ──

    [Fact]
    public void ADressedScreenBeforeTheFloorIsStillNotRevealed()
    {
        var gate = Gate();

        // Twenty on-time turns on a screen that is entirely finished, all well before the brand floor.
        for (var i = 0; i < 20; i++)
            Assert.Null(Calm(gate, 100 + i * 8, upAtMs: 50, dressed: true));
    }

    /// <summary>The floor binds the patience. Stated with its own bounds rather than the app's: with the
    /// patience the app actually uses there is no moment where it expires before the floor, and the rule
    /// still has to hold for a caller that chooses otherwise.</summary>
    [Fact]
    public void PatienceDoesNotFireBeforeTheFloor()
    {
        var gate = new RevealGate(floor: Ms(1500), patience: Ms(200), backstop: Ms(20000));

        // Shape at 100ms, so the patience is spent by 300ms: still a long way inside the floor.
        for (var at = 300d; at < 1500; at += 50)
            Assert.Null(Held(gate, at, upAtMs: 100, dressed: false));

        Assert.Equal(RevealWay.Ceiling, Held(gate, 1500, upAtMs: 100, dressed: false));
    }

    // ── the pictures ──

    /// <summary>The fault the second fact guards against: a screen with shape, an idle thread, and blank
    /// squares where the covers go. Every turn here is on time and past the floor.</summary>
    [Fact]
    public void AScreenWithNoPicturesYetIsNotRevealed()
    {
        var gate = Gate();
        var up = Floor.TotalMilliseconds + 8;

        for (var i = 0; i < 40; i++)
            Assert.Null(Calm(gate, up + i * 8, upAtMs: up, dressed: false));
    }

    /// <summary>Dressed past the floor reveals, and does not wait for a calm thread to do it. The fade is
    /// on the compositor and does not need one.</summary>
    [Fact]
    public void ADressedScreenOnABusyThreadIsStillRevealed()
    {
        var gate = Gate();
        var up = Floor.TotalMilliseconds + 8;

        Assert.Null(Held(gate, up, upAtMs: up, dressed: false));
        Assert.Equal(RevealWay.Ceiling, Held(gate, up + 8, upAtMs: up, dressed: true));
    }

    /// <summary>And where the thread HAS been answering while the pictures were landing, the same moment
    /// is reported as quiet: which is the Skia head's question, since that is where the reveal is still
    /// driven by the drawing thread.</summary>
    [Fact]
    public void ADressedScreenOnACalmThreadIsRevealedAsQuiet()
    {
        var gate = Gate();
        var up = Floor.TotalMilliseconds + 8;

        // The thread answers while the covers are still coming in.
        for (var i = 0; i < RevealGate.QuietTicks; i++)
            Assert.Null(Calm(gate, up + i * 8, upAtMs: up, dressed: false));

        Assert.Equal(
            RevealWay.Quiet,
            Calm(gate, up + RevealGate.QuietTicks * 8, upAtMs: up, dressed: true));
    }

    /// <summary>The run of calm is counted only while the screen has its shape. A read that leaves the
    /// thread idle for a second must not bank a second's worth of quiet: that read is on a worker, so the
    /// thread is idle because there is nothing to draw yet.</summary>
    [Fact]
    public void CalmGatheredBeforeTheShapeDoesNotCount()
    {
        var gate = Gate();
        var at = Floor.TotalMilliseconds + 8;

        // A hundred on-time turns with no screen at all: the whole of home's read of the shelf.
        for (var i = 0; i < 100; i++)
            Assert.Null(Calm(gate, at + i * 8, upAtMs: null, dressed: false));

        // The shape arrives. The run starts from here, not from the idle stretch behind it: so the
        // first dressed turn is reported busy rather than quiet.
        var up = at + 100 * 8;
        Assert.Equal(RevealWay.Ceiling, Calm(gate, up, upAtMs: up, dressed: true));
    }

    /// <summary>A late turn starts the run again. A thread held for two hundred milliseconds and then
    /// free for two turns has taken a breath, not gone quiet.</summary>
    [Fact]
    public void ALateTurnStartsTheRunAgain()
    {
        var gate = Gate();
        var up = Floor.TotalMilliseconds + 8;

        for (var i = 0; i < RevealGate.QuietTicks; i++)
            Calm(gate, up + i * 8, upAtMs: up, dressed: false);

        Assert.Null(Held(gate, up + 100, upAtMs: up, dressed: false));

        // Short of a fresh run, so the reveal that follows is not the quiet one.
        for (var i = 0; i < RevealGate.QuietTicks - 2; i++)
            Assert.Null(Calm(gate, up + 200 + i * 8, upAtMs: up, dressed: false));

        Assert.Equal(RevealWay.Ceiling, Calm(gate, up + 300, upAtMs: up, dressed: true));
    }

    /// <summary>The bar is two frames, not any lateness at all: a turn a millisecond late on a thread
    /// doing nothing must not cost the fade.</summary>
    [Fact]
    public void ATurnUnderTheBarCountsAsOnTime()
    {
        var gate = Gate();
        var up = Floor.TotalMilliseconds + 8;

        for (var i = 0; i < RevealGate.QuietTicks; i++)
            Assert.Null(Turn(gate, RevealGate.HeldMs, up + i * 8, up, dressed: false));

        Assert.Equal(
            RevealWay.Quiet,
            Turn(gate, RevealGate.HeldMs, up + RevealGate.QuietTicks * 8, up, dressed: true));
    }

    // ── the two bounds ──

    /// <summary>Pictures that never land. Bounded from the SHAPE, so it is the same wait whether the app
    /// took one second to get there or ten.</summary>
    [Theory]
    [InlineData(2000)]
    [InlineData(16000)]
    public void PicturesThatNeverLandAreRevealedAPatienceAfterTheShape(double upAtMs)
    {
        var gate = Gate();
        var due = upAtMs + Patience.TotalMilliseconds;

        for (var at = upAtMs; at < due; at += 100)
            Assert.Null(Held(gate, at, upAtMs, dressed: false));

        Assert.Equal(RevealWay.Ceiling, Held(gate, due, upAtMs, dressed: false));
    }

    /// <summary>The cold-start fault, and the reason the bounds are two. If the shell loads late and the
    /// screen comes up later still, a single bound counted from launch can expire before there is
    /// anything to reveal. Then the app is shown with an empty wall behind it. Nothing before the screen
    /// may open this gate short of the backstop.</summary>
    [Fact]
    public void ASlowStartIsNotRevealedJustBecauseLaunchWasLongAgo()
    {
        var gate = Gate();

        for (var at = 6000d; at < Backstop.TotalMilliseconds; at += 250)
            Assert.Null(Calm(gate, at, upAtMs: null, dressed: false));
    }

    /// <summary>The backstop depends only on the clock. A screen that never reports, such as a landing with
    /// no reading behind it or a read that died, still opens the app.</summary>
    [Fact]
    public void AScreenThatNeverComesUpIsRevealedAtTheBackstop()
    {
        var gate = Gate();

        Assert.Null(Calm(gate, Backstop.TotalMilliseconds - 1, upAtMs: null, dressed: false));
        Assert.Equal(
            RevealWay.Ceiling,
            Calm(gate, Backstop.TotalMilliseconds, upAtMs: null, dressed: false));
    }
}
