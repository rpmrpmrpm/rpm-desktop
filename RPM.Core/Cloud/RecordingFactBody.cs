using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Fingerprint;

namespace RPM.Core.Cloud;

/// <summary>One measurement about a record, as it travels.</summary>
/// <param name="Agreements">How many separate people have arrived at the same value. It is sent back
/// to the client, never sent up. A client that counted its own agreement would be agreeing with
/// itself.</param>
public sealed record RecordingFactBody(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("generation")] int Generation,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("modelled")] bool Modelled = false,
    [property: JsonPropertyName("confidence")] double Confidence = 1,
    [property: JsonPropertyName("agreements")] int Agreements = 0);

/// <summary>Everything the pool knows about one record.</summary>
public sealed record KnownRecording(string RecordingId, IReadOnlyList<RecordingFactBody> Facts)
{
    /// <summary>The one fact of a given kind, or null when the pool has none.</summary>
    public RecordingFactBody? Fact(string field) =>
        Facts.FirstOrDefault(f => string.Equals(f.Field, field, StringComparison.Ordinal));
}

/// <summary>
/// How the levelling is written down in the pool.
///
/// <para>This has its own type because both ends must agree on the format exactly. Agreement matters
/// here in a way it often does not. Two people who measure the same record produce the same numbers,
/// and the store recognises that only if they also produce the same <em>string</em>. A machine that
/// wrote -9.4 as -9.40 would look like it disagreed with everyone else.</para>
/// </summary>
public static partial class LevelFact
{
    /// <summary>The generation of this measurement. Bump it when a change to the meter would give a
    /// different answer for the same audio. Levelling and the beat grid have separate generation
    /// numbers, because they change at different times. A change to one should not invalidate the
    /// other.</summary>
    public const int Generation = 1;

    private sealed record Body(
        [property: JsonPropertyName("lufs")] string Lufs,
        [property: JsonPropertyName("peak")] string Peak);

    /// <summary>The shape above, as the source generator describes it. The published build is Native
    /// AOT, which cannot serialize by reflection, so the serializer code must be generated at build
    /// time.</summary>
    [JsonSerializable(typeof(Body))]
    private sealed partial class LevelFactJson : JsonSerializerContext;

    /// <summary>A reading as the pool holds it, or null when there is nothing worth saying. A track
    /// that cannot be measured is not published. The result depends on this machine's decoder as much
    /// as on the record, and another machine may be able to measure it.</summary>
    public static RecordingFactBody? From(double lufs, double truePeak)
    {
        if (!double.IsFinite(lufs) || lufs >= 0 || !double.IsFinite(truePeak) || truePeak < 0)
            return null;

        var body = JsonSerializer.Serialize(new Body(Number(lufs), Number(truePeak)), LevelFactJson.Default.Body);
        return new RecordingFactBody(RecordingFact.Level, Generation, body);
    }

    /// <summary>Reads a stored reading back, or null when it is not one.</summary>
    public static (double Lufs, double TruePeak)? Read(RecordingFactBody? fact)
    {
        if (fact is null || fact.Generation != Generation || string.IsNullOrEmpty(fact.Value))
            return null;

        try
        {
            var body = JsonSerializer.Deserialize(fact.Value, LevelFactJson.Default.Body);
            if (body is null
                || !double.TryParse(body.Lufs, NumberStyles.Float, CultureInfo.InvariantCulture, out var lufs)
                || !double.TryParse(body.Peak, NumberStyles.Float, CultureInfo.InvariantCulture, out var peak))
                return null;

            return double.IsFinite(lufs) && double.IsFinite(peak) ? (lufs, peak) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A number written the way everybody writes it: rounded to a hundredth of a decibel, then
    /// formatted with the invariant culture.
    ///
    /// <para>The rounding is what makes agreement visible. Two meters reading the same audio give the
    /// same value to far more places than anyone can hear, but not to every place a double has. Without
    /// rounding, every second reading of a record would look like a disagreement.</para>
    /// </summary>
    private static string Number(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
