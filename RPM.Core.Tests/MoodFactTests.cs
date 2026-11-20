using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Fingerprint;

namespace RPM.Core.Tests;

/// <summary>
/// The arousal numbers as the recording pool holds them. A list built for an account with no desktop is cut from
/// these, so they must come back exactly as they were measured, and "nothing to describe" must stay an answer.
/// </summary>
public class MoodFactTests
{
    [Fact]
    public void A_reading_comes_back_as_it_was_written()
    {
        var mood = new MoodReading(0.0570806082805996, 3.0586668755385, 16.0);

        var read = MoodFact.Read(MoodFact.From(mood));

        Assert.Equal(mood, read);
    }

    [Fact]
    public void Nothing_to_describe_comes_back_as_zero_across_all_three()
    {
        var read = MoodFact.Read(MoodFact.From(MoodReading.Unknown));

        Assert.Equal(new MoodReading(0, 0, 0), read);
    }

    [Fact]
    public void A_fact_this_build_cannot_use_is_not_read()
    {
        var good = MoodFact.From(new MoodReading(0.1, 2, 3));

        Assert.Null(MoodFact.Read(null));
        Assert.Null(MoodFact.Read(good with { Generation = MoodFact.Generation + 1 }));
        Assert.Null(MoodFact.Read(good with { Field = RecordingFact.Sound }));
        Assert.Null(MoodFact.Read(good with { Value = "not json" }));
        Assert.Null(MoodFact.Read(good with { Value = """{"brightness":"x","onsets":"1","dynamics":"1"}""" }));
        Assert.Null(MoodFact.Read(good with { Value = """{"brightness":"-1","onsets":"1","dynamics":"1"}""" }));
    }

    [Fact]
    public void The_numbers_are_written_as_text()
    {
        var fact = MoodFact.From(new MoodReading(0.25, 3.5, 9));

        Assert.Equal(RecordingFact.Mood, fact.Field);
        Assert.Contains("\"brightness\":\"0.25\"", fact.Value);
        Assert.Contains("\"onsets\":\"3.5\"", fact.Value);
        Assert.Contains("\"dynamics\":\"9\"", fact.Value);
        Assert.Contains(RecordingFact.Mood, RecordingFact.Fields);
    }
}
