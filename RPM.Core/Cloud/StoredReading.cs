using System.Text.Json.Serialization;
using RPM.Core.Analysis;
using RPM.Core.Library;

namespace RPM.Core.Cloud;

/// <summary>
/// What the API asks the transcode function to measure in the store's copy of one track. Shared by the
/// API, which writes it, and the function, which reads it, so the two cannot disagree on a field name.
/// </summary>
/// <param name="Sha256">The blob the user's track points at.</param>
/// <param name="DurationSeconds">The track's length from the store's own catalogue row, which places the
/// clips (see <see cref="SoundClips.Starts"/>). Zero when the row has no length. The function then measures
/// the length itself.</param>
/// <param name="Level">Measure the loudness (<see cref="LoudnessReading"/>).</param>
/// <param name="Mood">Measure the arousal numbers (<see cref="MoodReading"/>).</param>
/// <param name="Sound">Hear the record with the CLAP model.</param>
/// <param name="Fingerprint">Take the Chromaprint fingerprint with fpcalc.</param>
public sealed record StoredMeasureAsk(
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
    [property: JsonPropertyName("level")] bool Level,
    [property: JsonPropertyName("mood")] bool Mood,
    [property: JsonPropertyName("sound")] bool Sound,
    [property: JsonPropertyName("fingerprint")] bool Fingerprint)
{
    /// <summary>Whether anything is asked for at all.</summary>
    [JsonIgnore]
    public bool AsksAnything => Level || Mood || Sound || Fingerprint;
}

/// <summary>
/// What the store measured in its own copy of a track: the answer to <c>POST recordings/measure</c>.
///
/// <para>This is for a track that is only in the cloud. Its file was uploaded and then deleted from the
/// computer, or it was uploaded by another of the user's computers. No computer holds the file, so no
/// computer can measure it. The store holds the only copy and measures it there, with the desktop's own
/// code. The app stores the answer as it stores a reading of its own file.</para>
///
/// <para>Each reading is null when it was not asked for. An answer that was asked for is never null unless
/// <see cref="Error"/> is set. "Could not be measured" is an answer too, and the app stores it, so the file
/// is not asked about again until it changes.</para>
///
/// <para>Written by the transcode function and read by the app, so both use this one type.</para>
/// </summary>
/// <param name="HasCopy">False when the store holds no copy of this track for this account. Nothing was
/// measured.</param>
/// <param name="Generation">The <see cref="SoundFact.Generation"/> of the build that measured. The app
/// uses the sound only when this is its own generation, because another generation hears the record at
/// other points or with another model.</param>
/// <param name="FingerprintVersion">The <see cref="LibraryDatabase.FingerprintVersion"/> of the build that
/// measured. The app uses the fingerprint only when this is its own version.</param>
/// <param name="Level">The loudness, or null when it was not asked for.</param>
/// <param name="Mood">The arousal numbers, or null when they were not asked for. An unknown reading means
/// "nothing to describe".</param>
/// <param name="Sound">The embedding packed by <see cref="SoundEmbedding.Pack"/>, in base64. Null when the
/// sound was not asked for, or when there was nothing to hear (see <see cref="SoundHeard"/>).</param>
/// <param name="SoundHeard">True when the sound was asked for and the record was read. With a null
/// <see cref="Sound"/> this means "nothing to hear".</param>
/// <param name="Fingerprint">The fingerprint, or null when it was not asked for.</param>
/// <param name="Error">Set when the store could not read the record this time. The rest of the answer is
/// not used, nothing is stored, and the app asks again later.</param>
public sealed record StoredReading(
    [property: JsonPropertyName("copy")] bool HasCopy,
    [property: JsonPropertyName("generation")] int Generation = 0,
    [property: JsonPropertyName("fingerprintVersion")] int FingerprintVersion = 0,
    [property: JsonPropertyName("level")] StoredLevel? Level = null,
    [property: JsonPropertyName("mood")] StoredMood? Mood = null,
    [property: JsonPropertyName("sound")] string? Sound = null,
    [property: JsonPropertyName("soundHeard")] bool SoundHeard = false,
    [property: JsonPropertyName("fingerprint")] StoredFingerprint? Fingerprint = null,
    [property: JsonPropertyName("error")] string? Error = null)
{
    /// <summary>The answer for a track the store holds no copy of.</summary>
    public static StoredReading NoCopy { get; } = new(HasCopy: false);

    /// <summary>The answer for a record the store could not read this time.</summary>
    public static StoredReading Failed(string why) => new(
        HasCopy: true, Generation: SoundFact.Generation, FingerprintVersion: LibraryDatabase.FingerprintVersion,
        Error: why);

    /// <summary>The answer for a record that was read. Each argument is null when that reading was not
    /// asked for.</summary>
    /// <param name="fingerprint">The serialized fingerprint and the length fpcalc read, or an empty blob for
    /// a file fpcalc could not read.</param>
    public static StoredReading Read(
        LoudnessReading? level, MoodReading? mood, SoundResult? sound, (double Seconds, byte[] Blob)? fingerprint) => new(
        HasCopy: true,
        Generation: SoundFact.Generation,
        FingerprintVersion: LibraryDatabase.FingerprintVersion,
        Level: level is { } l ? new StoredLevel(l.Known ? l.Lufs : null, l.Known ? l.TruePeak : 0) : null,
        Mood: mood is { } m ? new StoredMood(m.Brightness, m.Onsets, m.Dynamics) : null,
        Sound: sound?.Embedding is { Length: SoundEmbedding.Dimensions } embedding
            ? Convert.ToBase64String(SoundEmbedding.Pack(embedding))
            : null,
        SoundHeard: sound is { Outcome: SoundOutcome.Measured or SoundOutcome.NothingToHear },
        Fingerprint: fingerprint is { } f ? new StoredFingerprint(f.Seconds, Convert.ToBase64String(f.Blob)) : null);

    /// <summary>Whether this answer can be stored at all: the store had a copy and read it.</summary>
    [JsonIgnore]
    public bool IsUsable => HasCopy && Error is null;

    /// <summary>
    /// The embedding to store, or why there is none.
    ///
    /// <para><see cref="SoundOutcome.ModelDidNotAnswer"/> covers every answer the app must not store: an
    /// error, another generation, a record that was not heard, and bytes that are not an embedding.</para>
    /// </summary>
    public SoundResult SoundFor(int generation)
    {
        if (!IsUsable || Generation != generation || !SoundHeard)
            return SoundResult.ModelDidNotAnswer;
        if (Sound is not { Length: > 0 } packed)
            return SoundResult.NothingToHear;

        return FromBase64(packed) is { } bytes && SoundEmbedding.Unpack(bytes) is { } embedding
            ? new SoundResult(SoundOutcome.Measured, embedding)
            : SoundResult.ModelDidNotAnswer;
    }

    /// <summary>The level to store, or null when there is none to store. An unknown reading is an answer
    /// ("cannot be measured") and is returned.</summary>
    public LoudnessReading? LevelFor() =>
        IsUsable && Level is { } l
            ? l.Lufs is { } lufs && double.IsFinite(lufs) ? new LoudnessReading(lufs, l.TruePeak) : LoudnessReading.Unknown
            : null;

    /// <summary>The arousal numbers to store, or null when there are none to store.</summary>
    public MoodReading? MoodFor() => IsUsable && Mood is { } m ? m.Reading : null;

    /// <summary>The fingerprint to store, as <see cref="LibraryDatabase.SaveTrackFingerprint"/> takes it, or
    /// null when there is none to store: not asked, another version, or bytes that are not base64. An empty
    /// blob is the answer "fpcalc could not read this file".</summary>
    public (double Seconds, byte[] Blob)? FingerprintFor(int version) =>
        IsUsable && FingerprintVersion == version && Fingerprint is { } f && FromBase64(f.Blob) is { } blob
            ? (f.Seconds, blob)
            : null;

    private static byte[]? FromBase64(string? text)
    {
        if (text is null)
            return null;
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
    }
}

/// <summary>A <see cref="LoudnessReading"/> as it is written in a <see cref="StoredReading"/>. JSON has no
/// infinity, so an unknown reading is a missing <see cref="Lufs"/>.</summary>
public sealed record StoredLevel(
    [property: JsonPropertyName("lufs")] double? Lufs,
    [property: JsonPropertyName("truePeak")] double TruePeak);

/// <summary>The three arousal numbers of a <see cref="MoodReading"/>, as they are written in a
/// <see cref="StoredReading"/>.</summary>
public sealed record StoredMood(
    [property: JsonPropertyName("brightness")] double Brightness,
    [property: JsonPropertyName("onsets")] double Onsets,
    [property: JsonPropertyName("dynamics")] double Dynamics)
{
    [JsonIgnore]
    public MoodReading Reading => new(Brightness, Onsets, Dynamics);
}

/// <summary>A fingerprint as it is written in a <see cref="StoredReading"/>: the length fpcalc read and the
/// serialized fingerprint in base64, empty for a file fpcalc could not read.</summary>
public sealed record StoredFingerprint(
    [property: JsonPropertyName("seconds")] double Seconds,
    [property: JsonPropertyName("blob")] string Blob);
