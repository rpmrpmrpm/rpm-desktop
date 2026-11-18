using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Analysis;
using RPM.Fingerprint;

namespace RPM.Core.Cloud;

/// <summary>
/// How a record's CLAP embedding is written down in the recording pool (<see cref="RecordingFact.Sound"/>).
///
/// <para>The value names the model and carries the embedding packed as
/// <see cref="SoundEmbedding.Pack"/> packs it, in base64. A model that has not heard the record the same
/// way cannot use the numbers, so a reading from another model is never read as this one's, even at the
/// same generation.</para>
/// </summary>
public static partial class SoundFact
{
    /// <summary>The generation of this measurement. Bump it when the model, the clips
    /// (<see cref="SoundClips"/>) or the features (<see cref="ClapFeatures"/>) change, because each of
    /// those gives a different embedding for the same audio.</summary>
    public const int Generation = 1;

    /// <summary>The model the embedding comes from, as its publisher names it.</summary>
    public const string Model = "laion/larger_clap_music_and_speech";

    private sealed record Body(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("q15")] string Packed);

    /// <summary>The shape above, as the source generator describes it. The published build is Native AOT,
    /// which cannot serialize by reflection.</summary>
    [JsonSerializable(typeof(Body))]
    private sealed partial class SoundFactJson : JsonSerializerContext;

    /// <summary>An embedding as the pool holds it, or null when it is not one.</summary>
    public static RecordingFactBody? From(ReadOnlySpan<float> embedding)
    {
        if (embedding.Length != SoundEmbedding.Dimensions)
            return null;
        foreach (var v in embedding)
            if (!float.IsFinite(v))
                return null;

        var body = JsonSerializer.Serialize(
            new Body(Model, Convert.ToBase64String(SoundEmbedding.Pack(embedding))), SoundFactJson.Default.Body);
        return new RecordingFactBody(RecordingFact.Sound, Generation, body, Modelled: true);
    }

    /// <summary>A stored embedding read back, or null when it is not one this build can use.</summary>
    public static float[]? Read(RecordingFactBody? fact)
    {
        if (fact is null || fact.Field != RecordingFact.Sound || fact.Generation != Generation
            || string.IsNullOrEmpty(fact.Value))
            return null;

        try
        {
            var body = JsonSerializer.Deserialize(fact.Value, SoundFactJson.Default.Body);
            if (body is null || body.Model != Model || string.IsNullOrEmpty(body.Packed))
                return null;

            var bytes = new byte[SoundEmbedding.PackedBytes];
            return Convert.TryFromBase64String(body.Packed, bytes, out int written) && written == bytes.Length
                ? SoundEmbedding.Unpack(bytes)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
