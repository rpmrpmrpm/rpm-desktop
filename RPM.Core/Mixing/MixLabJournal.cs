using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Analysis;

namespace RPM.Core.Mixing;

/// <summary>A changeover flattened for writing down: no track objects, no curves, just the numbers that
/// decide what is heard.</summary>
public sealed record PlanSnapshot(
    string Style,
    double StartAtSeconds,
    double CueInSeconds,
    double TempoRatio,
    double OutgoingTempoRatio,
    double FadeSeconds,
    int FadeBars,
    double BarSeconds,
    double MixBpm,
    int OverlapBars,

    /// <summary>The largest difference between the two grids inside the changeover, in milliseconds.
    /// The fade length and the tempo pull were both chosen to keep this small. See
    /// TransitionPlan.GridDriftSeconds.</summary>
    double GridDriftMs = 0,

    /// <summary>Where the pull changes, and to what, when one pull would not hold the pair. This is
    /// written down because a record whose speed changes during the changeover is the first thing a
    /// reader of this file will want to know about.</summary>
    string? Pull = null,

    string? Arc = null)
{
    public static PlanSnapshot? Of(TransitionPlan? plan) =>
        plan is null
            ? null
            : new PlanSnapshot(
                plan.Style.ToString(),
                Round(plan.StartAtSeconds), Round(plan.CueInSeconds),
                Round(plan.TempoRatio, 4), Round(plan.OutgoingTempoRatio, 4),
                Round(plan.FadeSeconds), plan.FadeBars,
                Round(plan.BarSeconds), Round(plan.MixBpm),
                plan.OverlapBars,
                Round(plan.GridDriftSeconds * 1000, 1),
                plan.TempoSteps.Count > 1
                    ? string.Join(", ", plan.TempoSteps.Select(s => $"{Round(s.AtSeconds, 1)}s x{Round(s.Ratio, 5)}"))
                    : null,

                // The arc as it was drawn, point by point. The journal helps tune the planner. A
                // fact such as "the DJ pulled the old record out early here" is in these numbers.
                plan.Arc?.ToString());

    private static double Round(double value, int places = 3) => Math.Round(value, places);
}

/// <summary>What a record looked like at the moment someone had an opinion about it.</summary>
public sealed record TrackSnapshot(
    string Path,
    string? Title,
    string? Artist,
    double Bpm,
    double BpmConfidence,
    double DownbeatConfidence,
    int BeatsPerBar,
    double TempoStability,
    double IntroEndSeconds,
    double OutroStartSeconds,
    double IntroLevel,
    double OutroLevel,
    int PhraseBars,
    int DropCount,
    bool Modelled,
    bool Edited)
{
    public static TrackSnapshot? Of(MixTrack? track) =>
        track is null
            ? null
            : new TrackSnapshot(
                track.Path, track.Title, track.Artist,
                Math.Round(track.Bpm, 3), Math.Round(track.BpmConfidence, 3),
                Math.Round(track.DownbeatConfidence, 3), track.BeatsPerBar,
                Math.Round(track.TempoStability, 3),
                Math.Round(track.Analysis?.IntroEndSeconds ?? 0, 3),
                Math.Round(track.Analysis?.OutroStartSeconds ?? 0, 3),
                Math.Round(track.IntroLevel, 3), Math.Round(track.OutroLevel, 3),
                track.Phrases.Bars, track.Drops.Count,
                track.IsModelled, track.Analysis?.IsEdited == true);
}

/// <summary>
/// One thing that happened in the lab, written down whole.
/// </summary>
/// <param name="Kind">"plan" when a changeover was worked out, "edit" when someone changed
/// something.</param>
public sealed record MixLabEntry(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("at")] string AtUtc,
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("outgoing")] TrackSnapshot? Outgoing,
    [property: JsonPropertyName("incoming")] TrackSnapshot? Incoming,
    [property: JsonPropertyName("outgoingBpm")] double OutgoingBpm,
    [property: JsonPropertyName("positionSeconds")] double PositionSeconds,
    [property: JsonPropertyName("planned")] PlanSnapshot? Planned,
    [property: JsonPropertyName("asked")] MixOverrides? Asked,
    [property: JsonPropertyName("result")] PlanSnapshot? Result,
    [property: JsonPropertyName("gridChanges")] IReadOnlyList<string> GridChanges,
    [property: JsonPropertyName("trace")] IReadOnlyList<string> Trace,
    [property: JsonPropertyName("wasted")] IReadOnlyList<string> Wasted,
    [property: JsonPropertyName("proposals")] IReadOnlyList<MixProposal> Proposals);

/// <summary>
/// Writes down the corrections a person makes in a session.
///
/// <para>A person who moves a changeover eight bars earlier has said where
/// <see cref="TransitionPlanner.MixOutPoint"/> is wrong. They have said it more precisely than they
/// could in words. But that is only useful if the pair is kept: what the code chose, and what the
/// person chose instead. One pair is an anecdote. Many pairs, with the record's measurements attached
/// to each, show the correction the planner needs. Anyone can read the file later, without the session
/// that produced it.</para>
///
/// <para>One JSON object per line. Lines are only appended, never rewritten. A session can end in a
/// crash. A file that is only appended to keeps every line written before the crash.</para>
///
/// <para>Best effort throughout. Nothing here may interrupt a set. A full disk or a path that cannot be
/// written loses a line, but it does not stop the mix.</para>
/// </summary>
public sealed partial class MixLabJournal
{
    /// <summary>One journal line's shape, and how it is written.</summary>
    /// <remarks>The context is used instead of a JsonSerializerOptions instance. A published build is
    /// Native AOT, and AOT serializes from generated metadata, not by reflection.</remarks>
    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false)]
    [JsonSerializable(typeof(MixLabEntry))]
    private sealed partial class MixLabJournalJson : JsonSerializerContext;

    private readonly object _gate = new();
    private readonly string _session;

    /// <summary>The file being appended to. Null when the folder could not be written, and then the
    /// journal does nothing.</summary>
    public string? Path { get; }

    /// <summary>How many lines have been written. The screen shows this to show that recording is
    /// happening.</summary>
    public int Count { get; private set; }

    /// <param name="directory">Where to keep the file. The folder is created if it does not
    /// exist.</param>
    public MixLabJournal(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _session = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, $"mixlab-{_session}.jsonl");
        }
        catch
        {
            Path = null;
        }
    }

    /// <summary>A changeover was worked out. This is written before anyone has an opinion about it, so
    /// the changeovers nobody corrected are in the file too. A planner is judged as much by the
    /// changeovers that went unremarked as by the ones somebody had to fix.</summary>
    public void Planned(
        MixTrack? outgoing, double outgoingBpm, double positionSeconds, TransitionPlan? plan) =>
        Write(Entry("plan", outgoing, outgoingBpm, positionSeconds, plan, plan, null, []));

    /// <summary>Someone changed something: the grid, a cue point, or the length of the
    /// changeover.</summary>
    public void Edited(
        MixTrack? outgoing, double outgoingBpm, double positionSeconds,
        TransitionPlan? planned, TransitionPlan? result, MixOverrides? asked,
        IReadOnlyList<string>? gridChanges) =>
        Write(Entry("edit", outgoing, outgoingBpm, positionSeconds, planned, result, asked,
            gridChanges ?? []));

    private MixLabEntry Entry(
        string kind, MixTrack? outgoing, double outgoingBpm, double positionSeconds,
        TransitionPlan? planned, TransitionPlan? result, MixOverrides? asked,
        IReadOnlyList<string> gridChanges)
    {
        var incoming = (result ?? planned)?.Incoming;

        return new MixLabEntry(
            kind,
            DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            _session,
            TrackSnapshot.Of(outgoing),
            TrackSnapshot.Of(incoming),
            Math.Round(outgoingBpm, 3),
            Math.Round(positionSeconds, 3),
            PlanSnapshot.Of(planned),
            asked,
            PlanSnapshot.Of(result),
            gridChanges,
            (result ?? planned)?.Trace ?? [],

            // Only signals that were measured and are read by nothing. An unmeasured signal is not
            // wasted on this record, and a used signal is not the question.
            [.. MixInputs.Wasted(outgoing).Select(s => $"{s.Name}={s.Value}"),
             .. MixInputs.Wasted(incoming).Select(s => $"in.{s.Name}={s.Value}")],

            MixProposals.For(outgoing, outgoingBpm, incoming, result ?? planned));
    }

    private void Write(MixLabEntry entry)
    {
        if (Path is null)
            return;

        try
        {
            var line = JsonSerializer.Serialize(entry, MixLabJournalJson.Default.MixLabEntry);
            lock (_gate)
            {
                File.AppendAllText(Path, line + Environment.NewLine);
                Count++;
            }
        }
        catch
        {
            // The line is lost, but the mix keeps going.
        }
    }
}
