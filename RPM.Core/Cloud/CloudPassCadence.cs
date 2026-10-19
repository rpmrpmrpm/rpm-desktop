namespace RPM.Core.Cloud;

/// <summary>
/// How often the passes that are not uploads are allowed to run.
///
/// <para>
/// A sync run does five things besides sending audio: it writes the playlists again, repairs the
/// album art, makes the head clips that the server is owed, reconciles the catalogue, and frees the
/// disk. None of them is triggered by having something to upload (a retagged album produces no
/// upload at all). None of them may run on every kick either, because the queue is kicked on every
/// library change, and a rename typed one letter at a time is a change for each keystroke.
/// </para>
///
/// <para>The two shapes here are the answer: once a session, and again after any run that actually
/// sent something, or on a clock of its own.</para>
/// </summary>
public static class CloudPassCadence
{
    /// <summary>
    /// How often the library is compared against the cloud's view of it. Short enough that a
    /// correction made on the desktop reaches the phone within a few minutes, and long enough that a
    /// rename typed one letter at a time does not cause a listing for each keystroke.
    /// </summary>
    public static readonly TimeSpan CatalogInterval = TimeSpan.FromMinutes(3);

    /// <summary>How often the mirror reads the catalogue on its own, when nobody asks. This is the
    /// minimum rate at which an edit made on the other machine arrives without anyone pressing a
    /// button.</summary>
    public static readonly TimeSpan MirrorInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often the mirror's timer asks <see cref="MirrorPassDue"/>. Much shorter than the interval,
    /// so the interval is measured from the last read and not from when the timer started.
    ///
    /// <para>If the timer ticked every <see cref="MirrorInterval"/>, a read that happened a few seconds
    /// after the timer started (the one that startup forces) would make the first tick find the pass
    /// not yet due, and the next read would come twenty minutes after the first and not ten.</para>
    /// </summary>
    public static readonly TimeSpan MirrorTick = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A pass that is due once a session, and again after any run that sent something.
    ///
    /// <para>The playlists and the album-art repair both work this way. A playlist that is renamed with
    /// no new music behind it produces no upload, so "only after an upload" would leave the phone
    /// playing the old order. Asking the server which covers it holds on every kick would be a request
    /// for each keystroke of a rename.</para>
    /// </summary>
    public static bool DueOncePerSession(bool uploadedSomething, bool alreadyDoneThisSession) =>
        uploadedSomething || !alreadyDoneThisSession;

    /// <summary>
    /// A pass on its own clock, which any run that sent something also brings forward.
    ///
    /// <para>This is the catalogue reconciliation. A metadata edit is neither an upload nor a new
    /// session, so it needs a clock. An upload run has read the listing anyway, so it may use it.</para>
    /// </summary>
    public static bool DueOnTheClock(bool uploadedSomething, DateTime lastPassUtc, DateTime nowUtc) =>
        uploadedSomething || nowUtc - lastPassUtc > CatalogInterval;

    /// <summary>
    /// Whether the mirror should read the catalogue now.
    ///
    /// <para>Forced when the user asked, or when something changed that makes the held answer wrong:
    /// signing in, opening a different library, or a pull that rewrote the very rows that the last
    /// listing described.</para>
    /// </summary>
    public static bool MirrorPassDue(bool force, DateTime lastPassUtc, DateTime nowUtc) =>
        force || nowUtc - lastPassUtc >= MirrorInterval;
}
