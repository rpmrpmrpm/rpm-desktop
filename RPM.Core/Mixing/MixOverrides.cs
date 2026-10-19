namespace RPM.Core.Mixing;

/// <summary>
/// What someone asked for that the planner would not have chosen on its own.
///
/// <para>This is not a settings object. Each field is a hand placed over a decision the code took. The
/// value of this arrangement is the difference between the two. A DJ who moves the changeover eight
/// bars earlier on forty records has shown where <see cref="TransitionPlanner.MixOutPoint"/> is wrong.
/// Reading the code would not show that. So every field is recorded next to what the planner had chosen
/// instead. That pair is what gets written down. See <c>MixLabEntry</c>.</para>
///
/// <para>A request is not an order. The planner still decides what is physically possible. A fade
/// cannot run past the end of the record it fades. Two grids that drift apart keep drifting apart, even
/// when someone asks for thirty-two bars. When a request is cut down, the cut is written in the plan's
/// <see cref="TransitionPlan.Trace"/>. The request itself is kept here unchanged. So the fact "the DJ
/// asked for 32 bars and the arithmetic allowed 8" stays on record.</para>
/// </summary>
public sealed record MixOverrides
{
    /// <summary>How many bars the changeover should run. The planner halves it as usual when the record
    /// has no room for it, or when the grids will not hold together that long.</summary>
    public int? FadeBars { get; init; }

    /// <summary>Which kind of changeover to make. A beat-matched mix asked for on two records that
    /// cannot be matched is refused. The alternative would be a mix that is named one thing and sounds
    /// like another.</summary>
    public TransitionStyle? Style { get; init; }

    /// <summary>Where on the outgoing record the changeover starts, in that record's own seconds. It
    /// snaps to the bar line, like any other start.</summary>
    public double? StartAtSeconds { get; init; }

    /// <summary>Where the needle goes down on the incoming record, in its own seconds. It snaps to the
    /// bar line.</summary>
    public double? CueInSeconds { get; init; }

    /// <summary>The volume arc drawn for the changeover. Null means the equal-power crossfade. A
    /// roll-out ignores this, because its levels are its own choreography. See
    /// <see cref="TransitionPlan.Arc"/>.</summary>
    public FadeArc? Arc { get; init; }

    /// <summary>The knobs the planner starts from, when someone turned one. See
    /// <see cref="MixPolicy"/>. Null, or all defaults, is the planner as shipped.</summary>
    public MixPolicy? Policy { get; init; }

    /// <summary>True when nothing was asked for. Every plan starts in this state.</summary>
    public bool IsEmpty =>
        FadeBars is null && Style is null && StartAtSeconds is null && CueInSeconds is null &&
        Arc is null && Policy is null or { IsDefault: true };

    /// <summary>What was asked for, for a log line and for the screen.</summary>
    public override string ToString()
    {
        if (IsEmpty)
            return "none";

        var parts = new List<string>(6);
        if (FadeBars is { } bars) parts.Add($"fade {bars} bars");
        if (Style is { } style) parts.Add($"style {style}");
        if (StartAtSeconds is { } at) parts.Add($"start {MixInputs.Sec(at)}");
        if (CueInSeconds is { } cue) parts.Add($"cue {MixInputs.Sec(cue)}");
        if (Arc is not null) parts.Add("arc drawn");
        if (Policy is { IsDefault: false } policy) parts.Add($"policy {policy}");
        return string.Join(", ", parts);
    }
}
