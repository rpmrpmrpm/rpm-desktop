namespace RPM.Desktop.Services.Account;

/// <summary>
/// What this account may use on this machine: the features that rpm enforces itself, because there is
/// no server in the way to refuse them.
///
/// <para><b>Why this is a type and not a bool.</b> Advanced recognition is limited where its cost is
/// paid. The clip goes to the Lambda, and the Lambda says no. (<see cref="Presentation.Main.Models.RecognitionReach"/>
/// only decides whether to bother asking.) DJ mode and word alignment are paid for here. The models run
/// on this machine, using this computer's power, and nothing downstream would refuse them. So the
/// refusal has to be a value that a caller is given, not a rule a caller must remember to apply. There
/// is no public constructor. The only ways to make one are to pass in what is known about the account
/// (<see cref="For"/>), or to ask for the free reach by name.</para>
///
/// <para><b>The gate goes in front of the work, not in front of the result.</b> A refused DJ set is
/// refused before <see cref="RPM.Core.Mixing.Dj"/> starts. So no beat grid is read, no key is found, and
/// no window is sent to the cloud. A refused alignment is refused before
/// <see cref="Services.Lyrics.LyricAlignment"/> is resolved. So the ONNX weights are never loaded.
/// Gating the answer instead of the work would cost just as much and save nothing.</para>
/// </summary>
public readonly record struct SupporterReach
{
    private SupporterReach(bool supports, bool tester)
    {
        _supports = supports;
        _tester = tester;
    }

    private readonly bool _supports;

    /// <summary>A closed beta tester: the supporter features without paying, so no supporter's mark.</summary>
    private readonly bool _tester;

    /// <summary>Everything an account gets without supporting rpm. It is named rather than defaulted,
    /// so a caller that never asked about the account cannot end up asserting the opposite by accident.</summary>
    public static SupporterReach Free => new(false, false);

    /// <summary>
    /// Whether the DJ may be given a set: the transition planner, the beat and key reading behind it,
    /// and the mix itself.
    /// </summary>
    public bool MayDj => _supports;

    /// <summary>
    /// Whether lyric words may be placed by listening to the audio, instead of being shared out evenly
    /// across the line. A model reads the recording while it plays. The lyric sheet itself is not
    /// limited: everybody gets the words, the scroll and the line they are on.
    /// </summary>
    public bool MayAlignWords => _supports;

    /// <summary>
    /// Whether the supporter's backdrop may be shown behind the app.
    ///
    /// <para>This is checked where the backdrop is loaded, not where it is picked. A stored id outlives
    /// the support that chose it. A check on the picker alone would leave a lapsed supporter with the
    /// gold backdrop for good. It would also show a picture nobody can buy back, behind an account that
    /// was reinstalled with its settings file.</para>
    /// </summary>
    public bool MaySupporterBackdrop => _supports;

    /// <summary>
    /// Whether the supporter's mark is shown beside this account's name.
    ///
    /// <para>This is the one entitlement that is not a capability. Nothing is unlocked by it, and nothing
    /// is paid for by it. It is here anyway, because the badge must disappear at the same time as the rest
    /// when support lapses. A heart beside a name whose DJ access has just been removed would show the app
    /// disagreeing with itself.</para>
    ///
    /// <para>A closed beta tester has the supporter features but does not wear the mark. The mark says
    /// "Supporter", and a tester does not pay.</para>
    /// </summary>
    public bool MayWearTheBadge => _supports && !_tester;

    /// <summary>
    /// What the account's standing allows.
    ///
    /// <para>Null means nobody has ever told us. This gives the free reach. Unlike recognition, there is
    /// no server here to make the real decision, so an unknown standing cannot be let through because
    /// something else will catch it later. A supporter who is offline keeps their access through
    /// <see cref="SupporterService.Standing"/>. That answers from the last thing the server said, and it
    /// outlives the session. So this method sees "not a supporter" only for an account that was never
    /// told otherwise, or whose support has run out.</para>
    /// </summary>
    public static SupporterReach For(SupporterService.Status? support)
        => new(support is { IsSupporter: true }, support is { IsSupporter: true, IsTester: true });
}
