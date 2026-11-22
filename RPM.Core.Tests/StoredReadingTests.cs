using System.Text.Json;
using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The store's answer for a track that is only in the cloud: what it measured in its own copy.
///
/// <para>The app stores what this says as if it had measured the file itself. So the one thing that must
/// not happen is an answer that says nothing about the record (an error, another model or recipe) being
/// stored as "could not be measured". That would keep the track out of the feeling lists, the levelling
/// and the recognition until its file changed.</para>
/// </summary>
public class StoredReadingTests
{
    private static float[] Embedding(int seed)
    {
        var random = new Random(seed);
        var v = new float[SoundEmbedding.Dimensions];
        for (int i = 0; i < v.Length; i++)
            v[i] = (float)(random.NextDouble() * 2 - 1);
        return SoundEmbedding.Average([v])!;
    }

    /// <summary>The answer as it crosses the wire: written by the function, read back by the app.</summary>
    private static StoredReading Wire(StoredReading reading) =>
        JsonSerializer.Deserialize<StoredReading>(JsonSerializer.Serialize(reading))!;

    private static StoredReading SoundOnly(SoundResult sound) => StoredReading.Read(null, null, sound, null);

    [Fact]
    public void Every_reading_comes_back_as_measured()
    {
        var embedding = Embedding(1);
        var answer = Wire(StoredReading.Read(
            new LoudnessReading(-11.25, 0.97), new MoodReading(0.25, 3.5, 9.0),
            new SoundResult(SoundOutcome.Measured, embedding), (231.5, [9, 8, 7])));

        var sound = answer.SoundFor(SoundFact.Generation);
        Assert.Equal(SoundOutcome.Measured, sound.Outcome);
        Assert.True(SoundEmbedding.Cosine(embedding, sound.Embedding!) > 0.99999);
        Assert.Equal(new LoudnessReading(-11.25, 0.97), answer.LevelFor());
        Assert.Equal(new MoodReading(0.25, 3.5, 9.0), answer.MoodFor());
        var fingerprint = answer.FingerprintFor(LibraryDatabase.FingerprintVersion)!.Value;
        Assert.Equal(231.5, fingerprint.Seconds);
        Assert.Equal(new byte[] { 9, 8, 7 }, fingerprint.Blob);
    }

    [Fact]
    public void Readings_that_found_nothing_are_answers()
    {
        var answer = Wire(StoredReading.Read(LoudnessReading.Unknown, MoodReading.Unknown, SoundResult.NothingToHear, (0, [])));

        Assert.Equal(SoundOutcome.NothingToHear, answer.SoundFor(SoundFact.Generation).Outcome);
        Assert.False(answer.LevelFor()!.Value.Known);
        Assert.False(answer.MoodFor()!.Value.Known);
        Assert.Empty(answer.FingerprintFor(LibraryDatabase.FingerprintVersion)!.Value.Blob);
    }

    [Fact]
    public void Readings_not_asked_for_are_not_there()
    {
        var answer = Wire(StoredReading.Read(null, null, null, null));

        Assert.Null(answer.LevelFor());
        Assert.Null(answer.MoodFor());
        Assert.Null(answer.FingerprintFor(LibraryDatabase.FingerprintVersion));
        Assert.Equal(SoundOutcome.ModelDidNotAnswer, answer.SoundFor(SoundFact.Generation).Outcome);
    }

    [Fact]
    public void An_answer_that_says_nothing_about_the_record_is_not_stored()
    {
        var heard = StoredReading.Read(LoudnessReading.Unknown, MoodReading.Unknown,
            new SoundResult(SoundOutcome.Measured, Embedding(2)), (1, [1]));

        // Another generation hears the record at other points or with another model; another recipe
        // fingerprints it differently.
        Assert.Equal(SoundOutcome.ModelDidNotAnswer, Wire(heard).SoundFor(SoundFact.Generation + 1).Outcome);
        Assert.Null(Wire(heard).FingerprintFor(LibraryDatabase.FingerprintVersion + 1));

        // The store could not read the record this time, or holds no copy: nothing in the answer is used.
        foreach (var unusable in new[] { StoredReading.Failed("S3 timed out"), StoredReading.NoCopy, heard with { Error = "x" } })
        {
            var read = Wire(unusable);
            Assert.Equal(SoundOutcome.ModelDidNotAnswer, read.SoundFor(SoundFact.Generation).Outcome);
            Assert.Null(read.LevelFor());
            Assert.Null(read.MoodFor());
            Assert.Null(read.FingerprintFor(LibraryDatabase.FingerprintVersion));
        }

        // The model did not answer, so the record was not heard.
        Assert.Equal(SoundOutcome.ModelDidNotAnswer,
            Wire(SoundOnly(SoundResult.ModelDidNotAnswer)).SoundFor(SoundFact.Generation).Outcome);

        // Bytes that are not an embedding or a fingerprint.
        Assert.Equal(SoundOutcome.ModelDidNotAnswer, (heard with { Sound = "not base64!" }).SoundFor(SoundFact.Generation).Outcome);
        Assert.Equal(SoundOutcome.ModelDidNotAnswer,
            (heard with { Sound = Convert.ToBase64String(new byte[10]) }).SoundFor(SoundFact.Generation).Outcome);
        Assert.Null((heard with { Fingerprint = new StoredFingerprint(1, "not base64!") })
            .FingerprintFor(LibraryDatabase.FingerprintVersion));
    }

    /// <summary>The field names are the API between the function, the server and the app, which are built
    /// and deployed apart. An unknown level has no infinity in it, which JSON cannot carry.</summary>
    [Fact]
    public void The_wire_names_are_fixed()
    {
        var json = JsonSerializer.Serialize(StoredReading.Read(
            LoudnessReading.Unknown, new MoodReading(0.1, 2, 3), new SoundResult(SoundOutcome.Measured, Embedding(3)), (1, [1])));

        foreach (var name in new[] { "\"copy\":", "\"generation\":", "\"fingerprintVersion\":", "\"level\":", "\"lufs\":null",
                                     "\"truePeak\":", "\"mood\":", "\"brightness\":", "\"onsets\":", "\"dynamics\":",
                                     "\"sound\":", "\"soundHeard\":", "\"fingerprint\":", "\"seconds\":", "\"blob\":", "\"error\":" })
            Assert.Contains(name, json);

        var ask = JsonSerializer.Serialize(new StoredMeasureAsk(new string('a', 64), 240, Level: true, Mood: false, Sound: true, Fingerprint: true));
        foreach (var name in new[] { "\"sha256\":", "\"durationSeconds\":", "\"level\":", "\"mood\":", "\"sound\":", "\"fingerprint\":" })
            Assert.Contains(name, ask);
        Assert.DoesNotContain("asksAnything", ask, StringComparison.OrdinalIgnoreCase);
    }
}
