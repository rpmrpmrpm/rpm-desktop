# LyricBench

Scores the lyric aligner on real records, so that a change to it, new weights above all, can be compared
with the version it replaces.

```
dotnet run --project tools/LyricBench -- --audio "<record>" --lrc "<sheet>"
dotnet run --project tools/LyricBench -- --manifest bench/lyrics.tsv
```

`--help` lists the flags.

## What it can and cannot tell you

There is no ground truth. Nobody publishes a word-stamped corpus of pop records, and hand-stamping one
is the work the aligner exists to avoid. So nothing here is accuracy. What it reports is:

- **Does the sweep track the singer.** How far a line runs past the next line's stamp. This is the sheet's own
  bound, and the only outside fact available. It shows a windowing fault, where most lines run 1 to 2
  seconds long and their last words never light.
- **What the model made of it.** Mean token score, and how much of the window the path spent on blank.
  This is the pair that new weights would move.
- **What is left over.** Words split by a hole (a letter placed, a bar of nothing, then the rest of the
  word). This makes a word *measure* long without having been sung long.

## `--against`: repeatability

Give it a second recording of the same take and the same words are placed off both, so the two answers
can be subtracted. That is repeatability, not accuracy, and it is worth more than it sounds: a threshold
tuned finer than the aligner's own spread is fitting noise.

Run against Aaliyah's *Are You That Somebody?* and its acapella:

```
02 - Are You That Somebody (A Capella).flac sits at +1.32s in the album version's clock

101 lines, 586 words placed off both
   mean |apart|             0.32s
   bias                     -0.08s
   mean token score         -3.49 here, -3.95 on the reference
   words split by a hole    15 here, 17 on the reference
```

Two results:

1. **The spread is about a third of a second.** That is the aligner's resolution on singing. Telling a
   stranded letter from a held note, or tuning the held-word threshold finer than that, is fitting noise.
2. **An isolated vocal scores no better than the full mix**, and slightly worse, with the same holes. The
   model is trained on read speech, and singing is out of distribution whether or not there is a band
   behind it. So separating the vocals before aligning them would cost a separation model and give
   nothing measurable.

The lever with room left is a model trained on singing rather than speech. `CtcVocabulary` reads the
alphabet from the JSON beside the weights rather than hardcoding it, so trying one is `--models` and a
folder.

## `--voice`: finding where somebody sings

This is a different question from the rest of this bench, and the only one here with outside ground
truth. The DJ wants to know where somebody is singing so that a changeover never lays two voices over each other,
and it wants that for every record, not for the minority with a sheet. wav2vec2 is already shipped and
its CTC output carries a blank ("nothing is being said in this frame"), so a sweep of the whole record
would answer it with no new weights, in theory.

The ground truth is written by a person: an LRC sheet marks its instrumental stretches with a note
character or an empty line, both of which `LyricSheet.IsWordless` already reads. So a wordless line's
stamp to the next line's stamp is a stretch a human said nobody sings in.

Run over 61 records that mark at least one break (256 minutes of audio, swept at 15x real time):

```
  smoothed   frames sung   frames break   mean blank sung   mean blank break   separation   best cut
  20 ms           241542          98340             0.819              0.906        0.596   0.99 (64 % right)
  2 s             241542          98340             0.827              0.906        0.736   0.96 (74 % right)
  8 s             241542          98340             0.836              0.903        0.733   0.96 (74 % right)
```

Three readings, and the third rules the idea out:

1. **74 % right is not a result.** 71 % of the labelled frames are sung, so answering "somebody is
   singing" every time already scores 71 %. The model is worth four points over saying nothing at all.
2. **Reading it at mix length helps.** Separation goes from 0.596 at the model's own 20 ms to 0.736
   over two seconds, and holds there over eight. Any detector that produces this curve should be read
   over a bar rather than a frame, because that is the length a changeover asks about.
3. **Seven records in sixty-one read backwards**, with separation under 0.5: the instrumental measured
   emptier of voice than the singing. Across records the spread is min 0.19, median 0.76, max 1.00.
   That rules these weights out. A weak detector that degrades gracefully could still bias a mix; one
   that inverts on a record in nine does not hedge, it confidently mixes into the chorus.

This is the same finding as `--against`, reached from the other side: the model is trained on read
speech and singing is out of distribution for it. It cannot place words in singing especially well and
it cannot tell singing from a band either.

This rules out only these weights, not the idea itself. The published singing models are the
ISMIR 2022 one (arxiv 2207.09747) and `mikezzb/wav2vec2-base-singing`. The second has the same base
architecture, so it drops in behind `--models`. Both are fine-tuned on DALI *after vocal extraction*.
They expect a separated stem, not a full mix. And once a separator is in the pipeline the detection
question no longer needs a language model: the vocal stem's own energy is the curve.
