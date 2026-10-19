using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>
/// The aligner decides which frame of audio each letter was sung on. Everything that uses it (which
/// word is highlighted, and when) is that answer converted to seconds. It can be tested without a
/// model: the input is a table of what the model heard, and a table can be written by hand. Every
/// posterior below is written to mean one obvious thing, so a failing test shows the aligner
/// disagreeing with something simple, not with a number nobody can check.
/// </summary>
public class CtcAlignerTests
{
    // A four-token model: blank, A, B, and the separator between words.
    private const int Blank = 0, A = 1, B = 2, Bar = 3;
    private const int Vocab = 4;

    /// <summary>Frames from a short description: one string per frame, each naming the main token of
    /// that frame. "-" is the blank. The named token gets a high score and the rest a low one, which is
    /// what a confident model's frame looks like.</summary>
    private static float[] Heard(params string[] frames)
    {
        var scores = new float[frames.Length * Vocab];
        for (var t = 0; t < frames.Length; t++)
        {
            var loud = frames[t] switch
            {
                "-" => Blank,
                "A" => A,
                "B" => B,
                "|" => Bar,
                _ => throw new ArgumentException($"no token '{frames[t]}'"),
            };

            for (var v = 0; v < Vocab; v++)
                scores[t * Vocab + v] = v == loud ? 4f : -4f;
        }
        return scores;
    }

    private static IReadOnlyList<TokenFrames> Align(float[] scores, params int[] tokens)
        => CtcAligner.Align(scores, scores.Length / Vocab, Vocab, tokens, Blank);

    // ── the alignment itself ──

    [Fact]
    public void One_token_held_across_every_frame_takes_every_frame()
    {
        var aligned = Align(Heard("A", "A", "A", "A"), A);

        var only = Assert.Single(aligned);
        Assert.Equal(0, only.FromFrame);
        Assert.Equal(3, only.ToFrame);
    }

    [Fact]
    public void Two_tokens_land_where_they_were_heard()
    {
        var aligned = Align(Heard("A", "A", "A", "B", "B"), A, B);

        Assert.Equal(2, aligned.Count);
        Assert.Equal((0, 2), (aligned[0].FromFrame, aligned[0].ToFrame));
        Assert.Equal((3, 4), (aligned[1].FromFrame, aligned[1].ToFrame));
    }

    [Fact]
    public void Blank_frames_around_the_singing_belong_to_no_token()
    {
        // The usual shape of a real window: a short silence, the words, a short silence. The padding
        // must not be given to the first and last letters, or every word's span is too long at both
        // ends and the highlight starts early and ends late.
        var aligned = Align(Heard("-", "-", "A", "B", "-", "-"), A, B);

        Assert.Equal(2, aligned[0].FromFrame);
        Assert.Equal(3, aligned[1].ToFrame);
    }

    [Fact]
    public void The_same_token_twice_is_two_tokens_and_not_one_held()
    {
        // This is why the trellis puts blanks between tokens. "AA" heard as A, blank, A is two letters.
        // Without a required blank between them no path could express that, and the second A could
        // not be told apart from the first A held longer.
        var aligned = Align(Heard("A", "-", "A"), A, A);

        Assert.Equal(2, aligned.Count);
        Assert.Equal(0, aligned[0].FromFrame);
        Assert.Equal(2, aligned[1].FromFrame);
    }

    [Fact]
    public void Two_of_the_same_token_need_room_for_the_blank_between_them()
    {
        // The cost: "AA" needs three frames however it is sung (one for each letter and one for the
        // blank between them), so two frames are refused.
        Assert.Empty(Align(Heard("A", "A"), A, A));

        // With a third frame it aligns, and uses the middle frame for the blank even though the audio
        // there is a clear A. That is the correct answer: the sheet says two letters were sung, and
        // the only paths that express that put a blank in the middle.
        var aligned = Align(Heard("A", "A", "A"), A, A);

        Assert.Equal(2, aligned.Count);
        Assert.Equal(0, aligned[0].ToFrame);
        Assert.Equal(2, aligned[1].FromFrame);
    }

    [Fact]
    public void A_blank_between_different_tokens_may_be_stepped_over()
    {
        // Between two DIFFERENT tokens the blank is optional, so a model that goes from one letter
        // directly to the next still aligns. Only the same-token case needs it.
        var aligned = Align(Heard("A", "B"), A, B);

        Assert.Equal(2, aligned.Count);
        Assert.Equal(0, aligned[0].FromFrame);
        Assert.Equal(1, aligned[1].FromFrame);
    }

    [Fact]
    public void The_tokens_come_back_in_the_order_they_were_given()
    {
        var aligned = Align(Heard("A", "B", "A", "B"), A, B, A, B);

        Assert.Equal(4, aligned.Count);
        for (var i = 1; i < aligned.Count; i++)
            Assert.True(aligned[i].FromFrame > aligned[i - 1].FromFrame,
                        "a later letter cannot start before an earlier one");
    }

    [Fact]
    public void The_alignment_covers_the_words_even_when_the_audio_disagrees()
    {
        // The model heard nothing like the transcript. It must still return the best path it can, not
        // nothing: the sheet says these words were sung, and a lyric highlight that disappears whenever
        // the model is unsure is worse than one that is approximately right.
        var aligned = Align(Heard("B", "B", "B", "B"), A, B);

        Assert.Equal(2, aligned.Count);
        Assert.True(aligned[0].FromFrame <= aligned[1].FromFrame);
    }

    // ── refusing, not guessing ──

    [Fact]
    public void Fewer_frames_than_letters_is_no_alignment()
    {
        // Two letters cannot be sung in one frame. The words do not fit the audio, which is a valid
        // answer: the caller keeps whatever timing it already had.
        Assert.Empty(Align(Heard("A"), A, B));
    }

    [Fact]
    public void An_empty_transcript_or_an_empty_window_is_no_alignment()
    {
        Assert.Empty(Align(Heard("A", "B")));
        Assert.Empty(CtcAligner.Align([], 0, Vocab, [A], Blank));
    }

    [Fact]
    public void A_letter_the_model_cannot_spell_is_no_alignment()
    {
        // Instead of reading past the end of the score table, which is the kind of bug that produces a
        // wrong answer instead of a crash.
        Assert.Empty(Align(Heard("A", "B"), A, 99));
    }

    // ── logits or log-probabilities, the caller need not know ──

    [Fact]
    public void Normalising_something_already_normalised_changes_nothing()
    {
        // This is why the caller does not need to say which kind of input it is: log-softmax over
        // log-probabilities subtracts the log of one. So a caller that passes either kind gets the same
        // answer, and cannot silently make the alignment worse by choosing the wrong one.
        var logits = Heard("A", "A", "B", "B");

        var once = Align(logits, A, B);

        var logProbs = new float[logits.Length];
        for (var t = 0; t < logits.Length / Vocab; t++)
        {
            double sum = 0;
            for (var v = 0; v < Vocab; v++)
                sum += Math.Exp(logits[t * Vocab + v]);
            for (var v = 0; v < Vocab; v++)
                logProbs[t * Vocab + v] = (float)(logits[t * Vocab + v] - Math.Log(sum));
        }

        var twice = Align(logProbs, A, B);

        Assert.Equal(once.Count, twice.Count);
        for (var i = 0; i < once.Count; i++)
            Assert.Equal((once[i].FromFrame, once[i].ToFrame), (twice[i].FromFrame, twice[i].ToFrame));
    }

    // ── the alphabet, and words in and out of it ──

    private static CtcVocabulary Alphabet() => new(
        new Dictionary<char, int> { ['A'] = A, ['B'] = B, ['|'] = Bar }, Blank, '|');

    [Fact]
    public void A_line_is_spelled_with_a_separator_between_its_words_and_none_at_the_end()
    {
        var spelled = Alphabet().Spell("AB BA");

        Assert.NotNull(spelled);
        Assert.Equal([A, B, Bar, B, A], spelled!.Tokens);
        Assert.Equal([0, 0, -1, 1, 1], spelled.Owners);
        Assert.Equal(2, spelled.Words.Length);
    }

    [Fact]
    public void Letters_the_model_cannot_spell_are_dropped_and_the_word_survives()
    {
        // One comma must not make a line impossible to align. The letters on each side are still sung
        // where they were sung, and a comma is not sung at all.
        var spelled = Alphabet().Spell("A,B");

        Assert.NotNull(spelled);
        Assert.Equal([A, B], spelled!.Tokens);
        Assert.Single(spelled.Words);
    }

    [Fact]
    public void A_word_of_nothing_but_punctuation_takes_no_slot()
    {
        // It would be a word the aligner never places, and the caller reads frames back by index, so an
        // extra empty slot moves every word after it onto the wrong letters.
        var spelled = Alphabet().Spell("AB !!! BA");

        Assert.NotNull(spelled);
        Assert.Equal(2, spelled!.Words.Length);
        Assert.DoesNotContain(-1, spelled.Owners[^1..]);
    }

    [Fact]
    public void A_line_with_nothing_the_model_can_read_spells_nothing()
    {
        Assert.Null(Alphabet().Spell("!!! ???"));
        Assert.Null(Alphabet().Spell("   "));
        Assert.Null(Alphabet().Spell(null));
    }

    [Fact]
    public void Case_does_not_matter_to_a_model_trained_on_capitals()
    {
        var spelled = Alphabet().Spell("ab");

        Assert.NotNull(spelled);
        Assert.Equal([A, B], spelled!.Tokens);
    }

    [Fact]
    public void A_word_runs_from_its_first_letter_to_its_last()
    {
        const string line = "AB BA";
        var spelled = Alphabet().Spell(line)!;
        var aligned = Align(Heard("A", "B", "|", "B", "B", "A"), spelled.Tokens);

        var words = CtcVocabulary.Words(spelled, aligned);

        Assert.Equal(2, words.Count);
        Assert.Equal("AB", line.Substring(words[0].Start, words[0].Length));
        Assert.Equal("BA", line.Substring(words[1].Start, words[1].Length));

        // The first word ends on its B, not on the separator after it, and the second starts on its own
        // first letter. The gap between two words is sung by neither.
        Assert.Equal(0, words[0].FromFrame);
        Assert.Equal(1, words[0].ToFrame);
        Assert.Equal(3, words[1].FromFrame);
        Assert.Equal(5, words[1].ToFrame);
    }

    [Fact]
    public void An_answer_about_a_different_spelling_is_refused_rather_than_read_across()
    {
        // Lengths that do not match mean the two are about different lines. Reading one against the
        // other would highlight the wrong words with full confidence, so it is refused.
        var spelled = Alphabet().Spell("AB BA")!;

        Assert.Empty(CtcVocabulary.Words(spelled, Align(Heard("A", "B"), A, B)));
    }
}
