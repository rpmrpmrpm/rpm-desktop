using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Analysis;
using RPM.Fingerprint;

namespace RPM.Core.Cloud;

/// <summary>
/// How a record's arousal numbers (<see cref="MoodReading"/>) are written down in the recording pool
/// (<see cref="RecordingFact.Mood"/>).
///
/// <para>The numbers are written as text, as the other facts write theirs, so a reader in any language gets
/// exactly the digits that were written. A record that was measured and had nothing to describe is written
/// as zero across all three, as the library stores it: an answer, so it is not measured again.</para>
/// </summary>
public static partial class MoodFact
{
    /// <summary>The generation of this measurement. Bump it when <see cref="MoodMeter"/> changes what it
    /// measures, because the numbers then mean something else.</summary>
    public const int Generation = 1;

    private sealed record Body(
        [property: JsonPropertyName("brightness")] string Brightness,
        [property: JsonPropertyName("onsets")] string Onsets,
        [property: JsonPropertyName("dynamics")] string Dynamics);

    /// <summary>The shape above, as the source generator describes it. The published build is Native AOT,
    /// which cannot serialize by reflection.</summary>
    [JsonSerializable(typeof(Body))]
    private sealed partial class MoodFactJson : JsonSerializerContext;

    /// <summary>A reading as the pool holds it.</summary>
    public static RecordingFactBody From(MoodReading mood)
    {
        var (b, o, d) = mood.Known ? (mood.Brightness, mood.Onsets, mood.Dynamics) : (0d, 0d, 0d);
        var body = JsonSerializer.Serialize(new Body(Text(b), Text(o), Text(d)), MoodFactJson.Default.Body);
        return new RecordingFactBody(RecordingFact.Mood, Generation, body);
    }

    /// <summary>A stored reading read back, or null when it is not one this build can use. A reading of zero
    /// across all three is returned as it is: the record was measured and had nothing to describe.</summary>
    public static MoodReading? Read(RecordingFactBody? fact)
    {
        if (fact is null || fact.Field != RecordingFact.Mood || fact.Generation != Generation
            || string.IsNullOrEmpty(fact.Value))
            return null;

        try
        {
            var body = JsonSerializer.Deserialize(fact.Value, MoodFactJson.Default.Body);
            return body is not null && Number(body.Brightness) is { } b && Number(body.Onsets) is { } o
                   && Number(body.Dynamics) is { } d
                ? new MoodReading(b, o, d)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0
            ? value
            : null;
}
