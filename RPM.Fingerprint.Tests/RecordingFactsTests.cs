using RPM.Fingerprint;

namespace RPM.Fingerprint.Tests;

/// <summary>
/// Which of two readings of one thing wins. The rule that decides what a stranger's analysis is
/// allowed to do to everybody else's, so it is written down here rather than left inside a store.
/// </summary>
public class RecordingFactsTests
{
    private static RecordingFact Fact(
        string value = "{\"lufs\":-9.4}",
        int generation = 1,
        bool modelled = false,
        double confidence = 1,
        string field = RecordingFact.Level) =>
        new(field, generation, value, modelled, confidence);

    [Fact]
    public void Nothing_known_takes_what_arrives()
    {
        Assert.Equal(FactVerdict.Take, RecordingFacts.Judge(Fact(), null));
    }

    [Fact]
    public void A_newer_generation_replaces_an_older_one()
    {
        Assert.Equal(FactVerdict.Take, RecordingFacts.Judge(Fact(generation: 2), Fact(generation: 1)));
    }

    [Fact]
    public void An_older_generation_does_not_come_back()
    {
        // A machine that has not been updated must not undo what a newer one worked out.
        Assert.Equal(FactVerdict.Keep, RecordingFacts.Judge(Fact(generation: 1), Fact(generation: 3)));
    }

    [Fact]
    public void A_models_reading_replaces_the_dsp_at_the_same_generation()
    {
        Assert.Equal(
            FactVerdict.Take,
            RecordingFacts.Judge(Fact(value: "a", modelled: true), Fact(value: "b", modelled: false)));
    }

    [Fact]
    public void The_dsp_never_replaces_a_models_reading()
    {
        Assert.Equal(
            FactVerdict.Keep,
            RecordingFacts.Judge(Fact(value: "a", modelled: false), Fact(value: "b", modelled: true)));
    }

    [Fact]
    public void An_old_models_reading_still_loses_to_a_newer_generation()
    {
        // Generation first, provenance second: a modelled row from an old analyser is every bit as out
        // of date as a DSP one from the same analyser.
        Assert.Equal(
            FactVerdict.Take,
            RecordingFacts.Judge(Fact(generation: 4, modelled: false), Fact(generation: 3, modelled: true)));
    }

    [Fact]
    public void The_same_answer_arrived_at_twice_is_agreement()
    {
        // What turns one person's reading into a fact. The measurement is deterministic, so two honest
        // clients on the same audio produce the same string.
        Assert.Equal(FactVerdict.Agree, RecordingFacts.Judge(Fact(value: "x"), Fact(value: "x")));
    }

    [Fact]
    public void The_same_code_disagreeing_with_itself_is_a_dispute_and_changes_nothing()
    {
        // Same generation, same kind of reading, different numbers. Something is wrong that this cannot
        // see. So what is stored stays, and the disagreement is counted rather than averaged.
        Assert.Equal(FactVerdict.Dispute, RecordingFacts.Judge(Fact(value: "x"), Fact(value: "y")));
    }

    [Fact]
    public void A_reading_too_hesitant_to_be_worth_serving_is_refused()
    {
        Assert.Equal(FactVerdict.Refuse, RecordingFacts.Judge(Fact(confidence: 0.01), null));
    }

    [Fact]
    public void A_field_this_store_does_not_keep_is_refused()
    {
        Assert.Equal(FactVerdict.Refuse, RecordingFacts.Judge(Fact(field: "vibes"), null));
    }

    [Fact]
    public void A_fact_with_nothing_in_it_is_refused()
    {
        Assert.Equal(FactVerdict.Refuse, RecordingFacts.Judge(Fact(value: ""), null));
        Assert.Equal(FactVerdict.Refuse, RecordingFacts.Judge(Fact(generation: 0), null));
        Assert.Equal(FactVerdict.Refuse, RecordingFacts.Judge(Fact(confidence: 1.5), null));
        Assert.Equal(
            FactVerdict.Refuse,
            RecordingFacts.Judge(Fact(value: new string('x', RecordingFact.MaxValueLength + 1)), null));
    }

    [Fact]
    public void Two_facts_about_different_things_are_not_compared()
    {
        Assert.Equal(
            FactVerdict.Refuse,
            RecordingFacts.Judge(Fact(field: RecordingFact.Level), Fact(field: RecordingFact.Grid)));
    }

    [Fact]
    public void One_field_moving_on_leaves_the_others_alone()
    {
        // The whole point of stamping per field rather than per row: a better way of reading the tempo
        // replaces the tempo. It does not invalidate the loudness, which nothing has improved.
        var storedLevel = Fact(field: RecordingFact.Level, generation: 1, value: "level-1");
        var storedGrid = Fact(field: RecordingFact.Grid, generation: 6, value: "grid-6");

        var betterGrid = Fact(field: RecordingFact.Grid, generation: 7, value: "grid-7");

        Assert.Equal(FactVerdict.Take, RecordingFacts.Judge(betterGrid, storedGrid));
        Assert.Equal(FactVerdict.Agree, RecordingFacts.Judge(Fact(field: RecordingFact.Level, value: "level-1"), storedLevel));
    }

    [Fact]
    public void A_number_reads_the_same_on_every_machine()
    {
        // Two clients that measured the same thing have to produce the same string, or agreement is
        // never seen and every second reading looks like a dispute.
        Assert.Equal("-9.4", RecordingFacts.Number(-9.4));
        Assert.Equal("0", RecordingFacts.Number(0));
        Assert.Equal("1.5", RecordingFacts.Number(1.5d));
    }
}
