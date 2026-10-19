using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// What the booth says it is doing (see <see cref="BoothView"/>).
///
/// <para>Without one named state, the workbench would work out its own answer from a dozen separate
/// readings, and the states that have no name are the ones that go wrong: a record playing with the
/// DJ working towards its changeover and a record playing with the DJ having given up on one look
/// identical, and every control on the page would stay live over both. These tests are about that
/// difference.</para>
/// </summary>
public class BoothViewTests
{
    private const int Rate = 44100, Channels = 2;

    /// <summary>A record's worth of steady tone, long enough to be seeked about in.</summary>
    private sealed class Tone(string label, double seconds, double ratio) : IPcmSource
    {
        private long _left = (long)(seconds * Rate / ratio);

        public string Label { get; } = label;

        public int Read(float[] buffer, int frames)
        {
            if (_left <= 0)
                return 0;
            int give = (int)Math.Min(frames, _left);
            Array.Fill(buffer, 0.5f, 0, give * Channels);
            _left -= give;
            return give;
        }

        public void Dispose() => _left = 0;
    }

    private static MixTrack Record(string name, double duration = 300, double? outro = null)
    {
        string path = $"/library/{name}.flac";
        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = name,
            TaggedDuration = duration,
            Analysis = Grid(path, duration, outro),
        };
    }

    /// <summary>A reading with a grid that covers the whole record, so the planner has bar lines to
    /// snap its marks to.</summary>
    private static TrackAnalysis Grid(string path, double duration, double? outro)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / 128)
            beats.Add(at);

        return new TrackAnalysis
        {
            FilePath = path,
            DurationSeconds = duration,
            Bpm = 128,
            BpmConfidence = 0.9,
            BeatTimes = beats,
            FirstDownbeat = 0,
            BeatsPerBar = 4,
            Key = new MusicalKey(9, true),
            KeyConfidence = 0.8,
            EnergyCurve = [0.3f, 0.3f],
            Intensity = 0.5,
            IntroEndSeconds = 0,
            OutroStartSeconds = outro ?? duration - 30,
            Version = TrackAnalysis.AnalyzerVersion,
        };
    }

    private static Dj Booth(params MixTrack[] tracks) =>
        new(new DjCrate(tracks), Rate, Channels,
            options => new Tone(options.Label ?? options.Source, 600 - options.StartSeconds, options.TempoRatio),
            cueRunner: work => { work(); return Task.CompletedTask; },
            chance: () => 1);

    /// <summary>Pumps the set for a while, so the opening record is on a deck and turning.</summary>
    private static void Play(Dj dj, int blocks = 200)
    {
        var block = new float[512 * Channels];
        for (int i = 0; i < blocks && dj.Read(block, 512) > 0; i++) { }
    }

    [Fact]
    public void A_set_that_has_not_been_pumped_yet_is_nothing_rather_than_mixing()
    {
        using var dj = Booth(Record("one"), Record("two"));

        var look = dj.Look(paused: false);

        Assert.Equal(BoothState.Nothing, look.State);
        Assert.False(look.CanEditTheMix);
        Assert.False(look.CanDriveTheDecks);
        Assert.False(look.CanScrubTheLiveDeck);
        Assert.Equal("No set running", look.Headline);
    }

    [Fact]
    public void A_record_playing_with_records_left_in_the_crate_is_mixing()
    {
        using var dj = Booth(Record("one"), Record("two"));
        Play(dj);

        var look = dj.Look(paused: false);

        Assert.Equal(BoothState.Mixing, look.State);
        Assert.Equal(NoMixBecause.None, look.Why);
        Assert.True(look.LiveTurning);
        Assert.True(look.CanEditTheMix);
        Assert.True(look.CanScrubTheLiveDeck);
        Assert.True(look.CanSkip);

        // The decks are the DJ's, so their own keys take no presses.
        Assert.False(look.CanDriveTheDecks);
    }

    [Fact]
    public void The_last_record_of_a_set_is_playing_out_and_says_so()
    {
        using var dj = Booth(Record("only"));
        Play(dj);

        var look = dj.Look(paused: false);

        Assert.Equal(BoothState.DeckPlaying, look.State);
        Assert.Equal(NoMixBecause.NothingLeftInTheCrate, look.Why);
        Assert.False(look.CanEditTheMix);          // there is no changeover to edit
        Assert.Contains("no mix", look.Headline);
    }

    /// <summary>
    /// The main case this reading exists for. Moving the playhead into the record's last minute,
    /// past the point a changeover could start from, turns the mix off for the rest of that record:
    /// the record plays out and the next one follows. The booth must say so, or the workbench goes on
    /// showing a beat-matched blend with its marks and its bar count, and the changeover never comes.
    /// </summary>
    [Fact]
    public void A_needle_moved_past_the_mix_point_gives_up_on_the_mix_and_says_which()
    {
        var one = Record("one", duration: 300, outro: 270);
        using var dj = Booth(one, Record("two"));
        Play(dj);

        Assert.Equal(BoothState.Mixing, dj.Look(paused: false).State);

        // Into the last minute, past the mix-out point: the drag somebody makes in the lab to watch
        // the changeover happen.
        Assert.True(dj.SeekTo(285));
        Play(dj, blocks: 4);

        var look = dj.Look(paused: false);

        Assert.Equal(BoothState.DeckPlaying, look.State);
        Assert.Equal(NoMixBecause.ScrubbedPastTheMixPoint, look.Why);
        Assert.True(look.LiveTurning);              // the record is still playing, the DJ is not mixing
        Assert.False(look.CanEditTheMix);
        Assert.Contains("past the mix point", look.Headline);
    }

    [Fact]
    public void Scrubbing_back_out_of_the_last_stretch_puts_the_mix_on_again()
    {
        var one = Record("one", duration: 300, outro: 270);
        using var dj = Booth(one, Record("two"));
        Play(dj);

        dj.SeekTo(285);
        Play(dj, blocks: 4);
        Assert.Equal(NoMixBecause.ScrubbedPastTheMixPoint, dj.Look(paused: false).Why);

        dj.SeekTo(30);
        Play(dj, blocks: 4);

        var look = dj.Look(paused: false);
        Assert.Equal(BoothState.Mixing, look.State);
        Assert.Equal(NoMixBecause.None, look.Why);
        Assert.True(look.CanEditTheMix);
    }

    [Fact]
    public void Hands_on_the_decks_is_a_deck_playing_and_not_a_dj_mixing()
    {
        using var dj = Booth(Record("one"), Record("two"));
        Play(dj);

        Assert.True(dj.TakeTheDecks(true));
        var look = dj.Look(paused: false);

        Assert.Equal(BoothState.DeckPlaying, look.State);
        Assert.Equal(NoMixBecause.HandsOnTheDecks, look.Why);

        // The one state where each deck answers its own transport, and the marks may still be moved:
        // stopping the set to move something and then playing it back is what the workbench is for.
        Assert.True(look.CanDriveTheDecks);
        Assert.True(look.CanEditTheMix);
        Assert.Contains("Decks yours", look.Headline);

        Assert.True(dj.TakeTheDecks(false));
        Assert.Equal(BoothState.Mixing, dj.Look(paused: false).State);
    }

    [Fact]
    public void A_paused_set_has_no_deck_turning_however_the_platters_are_left()
    {
        using var dj = Booth(Record("one"), Record("two"));
        Play(dj);

        var look = dj.Look(paused: true);

        Assert.True(look.Paused);
        Assert.False(look.LiveTurning);
        Assert.False(look.CuedTurning);

        // Paused is not a changeover: the strip stays live, which is the purpose of pausing.
        Assert.True(look.CanEditTheMix);
    }

    [Fact]
    public void A_changeover_that_has_started_is_nobodys_to_touch()
    {
        var one = Record("one", duration: 300, outro: 270);
        using var dj = Booth(one, Record("two"));
        Play(dj);

        // The next key IS a changeover (see Dj.IsChangingOver, which is true from the press).
        dj.SkipToNext();
        Play(dj, blocks: 1);

        var look = dj.Look(paused: false);

        Assert.Equal(BoothState.HandingOver, look.State);
        Assert.False(look.CanEditTheMix);
        Assert.False(look.CanDriveTheDecks);
        Assert.False(look.CanScrubTheLiveDeck);
        Assert.False(look.CanSkip);
        Assert.False(look.CanTakeOrGiveTheDecks);
        Assert.Contains("nothing adjustable", look.Headline);
    }

    [Fact]
    public void A_finished_set_is_over_and_offers_nothing()
    {
        using var dj = Booth(Record("only", duration: 5));
        Play(dj, blocks: 100_000);

        var look = dj.Look(paused: false);

        Assert.Equal(BoothState.Over, look.State);
        Assert.False(look.CanEditTheMix);
        Assert.False(look.CanDriveTheDecks);
        Assert.False(look.CanTakeOrGiveTheDecks);
        Assert.Equal("Set finished", look.Headline);
    }

    /// <summary>
    /// The audio thread deciding a changeover while the interface asks for everything it can ask for,
    /// at the same time.
    ///
    /// <para><b>What this proves and what it does not.</b> It proves the booth survives the two
    /// drivers running at once: no throw, no deck read while it is being taken apart, no set that
    /// stops producing audio. It does <b>not</b> prove that <c>Dj._plan</c> is necessary: it passes
    /// with the lock removed (this is checked, not assumed). The failure the lock prevents is too
    /// quiet for a soak test to see: the audio thread passes "there is a plan and its decoder has
    /// loaded", the interface nulls the plan and unloads the deck in the gap, and <c>BeginFade</c>
    /// finds an empty platter and returns. The result is a changeover that does not happen, on a set
    /// that plays on normally. Catching that needs an interleaving harness inside <c>Read</c>; until
    /// there is one, the lock is justified by reading the code, not by this test.</para>
    /// </summary>
    [Fact]
    public void The_booth_survives_the_interface_and_the_audio_thread_at_once()
    {
        var records = Enumerable.Range(0, 12)
            .Select(i => Record($"r{i}", duration: 180, outro: 150))
            .ToArray();

        // The real cue runner: a decoder opening is a task on the pool, so the plan can be half set
        // up when an ask lands. The inline runner every other test uses would hide exactly that.
        using var dj = new Dj(
            new DjCrate(records), Rate, Channels,
            options => new Tone(options.Label ?? options.Source, 600 - options.StartSeconds, options.TempoRatio),
            chance: () => 1);

        Play(dj, blocks: 50);

        // Driven by counts, not by a clock. Running both threads for a fixed time and then asserting
        // how much each got through would, on a loaded runner, test how busy the machine is rather
        // than anything about the booth.
        const int Blocks = 20_000;
        using var stop = new CancellationTokenSource();
        using var handsAreOn = new ManualResetEventSlim(false);
        int asks = 0;

        // The interface, asking for everything it can ask for, until the audio stops.
        var hands = Task.Run(() =>
        {
            handsAreOn.Set();
            var rng = new Random(20260820);
            while (!stop.IsCancellationRequested)
            {
                switch (rng.Next(8))
                {
                    case 0: dj.SeekTo(rng.Next(0, 175)); break;
                    case 1: dj.Replan(); break;
                    case 2: dj.RejectCued(); break;
                    case 3: dj.TakeTheDecks(true); break;
                    case 4: dj.TakeTheDecks(false); break;
                    case 5: dj.SeekDeck(DjDeck.Cued, rng.Next(0, 60)); break;
                    case 6: dj.CueNext(); break;
                    default: _ = dj.Look(paused: false); break;
                }
                Interlocked.Increment(ref asks);
            }
        });

        // Waited for, not assumed. The block loop below is pure arithmetic and gets through twenty
        // thousand of them in a few milliseconds. On a runner with every core busy the pool may not
        // schedule the hands at all before it finishes; then the two never overlap and the test
        // proves nothing. This wait makes sure they overlap.
        Assert.True(handsAreOn.Wait(TimeSpan.FromSeconds(30)), "the hands thread never started");

        // And the audio thread, pumping underneath it.
        var block = new float[512 * Channels];
        int blocks = 0;
        for (; blocks < Blocks; blocks++)
        {
            int got = dj.Read(block, 512);

            // Zero is the end of the set and nothing else. Twenty thousand blocks is under four
            // minutes of audio against twelve three-minute records, so a zero here means the booth's
            // state was broken by the concurrent calls.
            Assert.True(got > 0, $"the set stopped producing audio after {blocks} blocks");
        }

        stop.Cancel();
        hands.Wait(TimeSpan.FromSeconds(10));

        Assert.False(dj.HasFinished);
        Assert.True(asks > 0, "the hands never got a turn; this proved nothing");

        // And it still gives a view that can be shown, rather than fields that do not fit together.
        var look = dj.Look(paused: false);
        Assert.NotEqual(BoothState.Over, look.State);
        Assert.False(string.IsNullOrWhiteSpace(look.Headline));
    }


    /// <summary>
    /// The changeover the booth SAYS it will make is the changeover it makes.
    ///
    /// <para>A workbench that worked out its own plan (same planner, different inputs) would draw a
    /// changeover minutes before the real one that does not match it: the window's overrides rather
    /// than the booth's, its own choice of next record rather than the crate's, and no luck value.
    /// <c>Dj.WouldPlan</c> is the booth's own answer, and this is what makes it useful: asked long
    /// before the changeover, its answer is the one that runs.</para>
    /// </summary>
    [Fact]
    public void What_the_booth_says_it_will_play_is_what_it_plays()
    {
        var records = new[]
        {
            Record("one", duration: 200, outro: 170),
            Record("two", duration: 200, outro: 170),
        };

        using var dj = Booth(records);
        Play(dj, blocks: 200);

        // Asked early: a whole record ahead of the changeover, which is the point.
        var said = dj.WouldPlan(out bool settled);
        Assert.NotNull(said);
        Assert.False(settled, "nothing is cued this early; the answer is what the booth would do");

        // Read on until the changeover has actually finished, and take the plan then. Not from the
        // TrackChanged handler: TrackChanged is raised at the fade's half way mark, and
        // CurrentPlan is still the plan the OUTGOING record came in on until the fade ends.
        var block = new float[512 * Channels];
        for (int i = 0; i < 200_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            if (!dj.IsTransitioning && dj.NowPlaying?.Path == said!.Incoming.Path)
                break;
        }

        var ran = dj.CurrentPlan;
        Assert.NotNull(ran);
        Assert.Equal(said!.Incoming.Path, ran!.Incoming.Path);
        Assert.Equal(said.Style, ran.Style);
        Assert.Equal(said.FadeBars, ran.FadeBars);
        Assert.Equal(said.StartAtSeconds, ran.StartAtSeconds, 2);
        Assert.Equal(said.CueInSeconds, ran.CueInSeconds, 2);
    }

    /// <summary>
    /// The luck is held, so the changeover the workbench shows is the one that runs, including the
    /// one time in four a cut is an echo out instead.
    ///
    /// <para>Asked repeatedly and a whole record early, the answer has to be the same answer. A draw
    /// taken inside the planner would give a different one every time the picture was refreshed,
    /// eight times a second.</para>
    /// </summary>
    [Fact]
    public void The_luck_is_held_so_the_same_question_gets_the_same_answer()
    {
        var unread = Record("two");
        unread = unread with { Analysis = null };

        // A booth whose luck always lands in the echo out's quarter.
        using var dj = new Dj(
            new DjCrate([Record("one"), unread]), Rate, Channels,
            options => new Tone(options.Label ?? options.Source, 600 - options.StartSeconds, options.TempoRatio),
            cueRunner: work => { work(); return Task.CompletedTask; },
            chance: () => 0.1);

        Play(dj);

        var first = dj.WouldPlan(out _);
        Assert.NotNull(first);
        Assert.Equal(TransitionStyle.EchoOut, first!.Style);

        // Asked again, and again: the same changeover, not a new random draw each time.
        for (var i = 0; i < 20; i++)
        {
            var again = dj.WouldPlan(out _);
            Assert.Equal(first.Style, again!.Style);
            Assert.Equal(first.CueInSeconds, again.CueInSeconds, 6);
        }
    }

    /// <summary>
    /// A record nobody has read still gets an answer, because there is one to give: the changeover out
    /// of it is decided from the grid of the record PLAYING, and it is a real choice between the cut
    /// and the echo out. It is not settled: the reading is very likely still on its way, and as soon
    /// as it arrives this becomes a beat-match instead.
    /// </summary>
    [Fact]
    public void A_record_nobody_has_read_is_shown_the_changeover_it_would_get()
    {
        var unread = Record("two");
        unread = unread with { Analysis = null };

        using var dj = Booth(Record("one"), unread);
        Play(dj);

        var plan = dj.WouldPlan(out bool settled);

        Assert.NotNull(plan);
        Assert.False(settled);
        Assert.Equal(TransitionStyle.Cut, plan!.Style);      // the booth's luck is 1, so not the echo
    }

    [Fact]
    public void The_last_record_of_a_set_has_nothing_to_say_about_a_changeover()
    {
        using var dj = Booth(Record("only"));
        Play(dj);

        Assert.Null(dj.WouldPlan(out _));
    }
}
