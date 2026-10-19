namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Where the run has got to. Moved by the startup sequence and by nothing else.
///
/// <para>Ordered, and the order is load-bearing: a pass asks whether the run has REACHED its phase,
/// which is a comparison, so inserting a value in the middle of this changes what every pass in the app
/// is waiting for. Add at the end, or change the sequence deliberately.</para>
/// </summary>
public enum Phase
{
    /// <summary>No window yet. Nothing that is not the window itself.</summary>
    Cold = 0,

    /// <summary>The splash is up and its animation is the only thing on screen that matters. The
    /// drawing thread is drawing one rectangle and one wordmark; anything that takes it away from that
    /// is taking away the whole of what the user can see.</summary>
    Opening = 1,

    /// <summary>The library is open and the first screen is filling in. The splash animation is still
    /// running and the user is shown what is happening. So work is allowed, but only work that the screen
    /// is waiting for.</summary>
    Loading = 2,

    /// <summary>The interface is up and can be used. Everything else is let in from here, in the order
    /// <see cref="Wave"/> states.</summary>
    Ready = 3,
}

/// <summary>The order the background passes are let in once the interface is up.
/// <para><b>Why an order at all.</b> Without an order, every pass would start at the same instant. The
/// library raises one event on opening, and eleven services answer it. They would then queue behind
/// each other on the one database gate that the interface also needs. In one measurement, eleven
/// workers and the drawing thread were all busy in the first twelve seconds. The interface waited
/// 2,735 ms for a playlist tree behind a fingerprint query. The splash then came off at its emergency ceiling
/// with no animation, because the drawing thread was never free to run one.</para>
/// <para>An order does not make any of them faster. It makes them not all be first.</para>
/// <para>Ordered, like <see cref="Phase"/>, and compared. The same warning applies.</para>
/// </summary>
public enum Wave
{
    /// <summary>What the screen is waiting on: reconciling the folder, the album index, the playlist
    /// tree. A user looking at the window is looking at the result of these.</summary>
    First = 0,

    /// <summary>What the user will look at next, and will notice the absence of: covers, artist
    /// portraits, and the records that other machines hold.</summary>
    Second = 1,

    /// <summary>What nobody is waiting for and nobody would notice arriving an hour later: loudness,
    /// fingerprints, world play counts, catalogue styles, writing measurements back into files.</summary>
    Last = 2,
}

/// <summary>What a pass competes for, which is the only thing about it the governor needs to know.
/// <para>Named after the thing that is scarce, not after the pass. Two passes in the same lane slow
/// each other down, whatever they are called. Two passes in different lanes mostly do not. The library
/// lane matters most. It is the database gate, which the interface also needs and cannot wait for
/// politely.</para>
/// </summary>
public enum Lane
{
    /// <summary>Reads or writes the library database in bulk. The gate the interface also goes
    /// through, so a pass in here competes with the window directly rather than in the background.
    /// </summary>
    Library = 0,

    /// <summary>Bytes off the volume the collection is on: decoding a file, reading a cover, hashing.
    /// </summary>
    Disk = 1,

    /// <summary>A request over the wire. It is slow, and slow somewhere else. It holds nothing here while
    /// it waits, so several can be in flight without costing the interface anything.</summary>
    Network = 2,

    /// <summary>Measuring, decoding, fingerprinting: work that wants cores and will take every one it
    /// is given.</summary>
    Processor = 3,
}
