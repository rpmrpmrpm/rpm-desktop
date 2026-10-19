using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The workbench's transport: each deck can be played, held and scrubbed on its own, the next record
/// can be cued early and auditioned, and the set continues normally once the user lets go.
/// </summary>
public class DjDeckTransportTests
{
    private const int Rate = 44100, Channels = 2, Block = 2205;

    // ── one deck, held ────────────────────────────────────────────────────────

    [Fact]
    public void A_held_deck_is_silence_that_does_not_move()
    {
        var deck = new Deck(Deck.Side.A, Rate, Channels, _ => new ToneSource(0.5f, 60));
        deck.Load(Track("x", 128), cueSeconds: 0, tempoRatio: 1);
        deck.Prime();
        deck.Start();
        deck.Fader = 1;

        var buffer = new float[Block * Channels];
        deck.Read(buffer, Block);
        double before = deck.PositionSeconds;

        deck.Hold();
        Assert.True(deck.IsHeld);

        // A full block, not a short one: a short block means the source has run out.
        Assert.Equal(Block, deck.Read(buffer, Block));
        Assert.Equal(0, buffer[0], 6);
        Assert.Equal(before, deck.PositionSeconds, 6);
        Assert.False(deck.HasRunOut);

        deck.Resume();
        Assert.Equal(Block, deck.Read(buffer, Block));
        Assert.NotEqual(0, buffer[Block * Channels - 1]);
    }

    [Fact]
    public void A_parked_deck_is_cued_again()
    {
        var deck = new Deck(Deck.Side.A, Rate, Channels, _ => new ToneSource(0.5f, 60));
        deck.Load(Track("x", 128), cueSeconds: 0, tempoRatio: 1);
        deck.Prime();
        deck.Start();
        deck.Fader = 1;

        var buffer = new float[Block * Channels];
        deck.Read(buffer, Block);

        deck.Park();
        Assert.False(deck.IsRunning);
        Assert.Equal(0, deck.Read(buffer, Block));
        Assert.True(deck.IsLoaded, "parking is not unloading");
    }

    // ── the booth's per-deck transport ────────────────────────────────────────

    [Fact]
    public void Holding_the_live_deck_freezes_the_set_where_it_stands()
    {
        using var dj = Booth(
            Track("one", 128, duration: 120, outro: 100),
            Track("two", 128, duration: 120, outro: 100));

        var buffer = new float[Block * Channels];
        for (int i = 0; i < 20; i++)
            dj.Read(buffer, Block);

        Assert.True(dj.HoldDeck(DjDeck.Live, true));
        double at = dj.LivePositionSeconds;

        for (int i = 0; i < 40; i++)
            Assert.Equal(Block, dj.Read(buffer, Block));

        Assert.Equal(at, dj.LivePositionSeconds, 3);
        Assert.Equal(0, buffer[0], 6);

        Assert.True(dj.HoldDeck(DjDeck.Live, false));
        dj.Read(buffer, Block);
        Assert.True(dj.LivePositionSeconds > at, "the record plays on from where it was held");
    }

    [Fact]
    public void The_next_record_can_be_cued_early_and_auditioned_alone()
    {
        using var dj = Booth(
            Track("one", 128, duration: 120, outro: 100),
            Track("two", 128, duration: 120, outro: 100));

        var buffer = new float[Block * Channels];
        dj.Read(buffer, Block);

        // Nothing is cued this early automatically.
        Assert.Null(dj.CuedTrack);

        // When asked, it is: the same plan the outro would make, made now.
        Assert.True(dj.CueNext());
        int guard = 0;
        while (dj.PendingPlan is null && guard++ < 200)
            dj.Read(buffer, Block);
        Assert.NotNull(dj.PendingPlan);
        Assert.Equal("two", dj.CuedTrack?.Path);

        // Audition it with the live deck held: the only signal in the mix is the next record's.
        Assert.True(dj.HoldDeck(DjDeck.Live, true));
        Assert.True(dj.AuditionCued(true));
        Assert.True(dj.IsAuditioning);

        dj.Read(buffer, Block);
        dj.Read(buffer, Block);
        Assert.Equal(0.25f, Math.Abs(buffer[Block * Channels - 1]), 2);

        // Scrub it while it plays.
        Assert.True(dj.SeekDeck(DjDeck.Cued, 30));
        dj.Read(buffer, Block);
        Assert.Equal(30, dj.CuedPositionSeconds, 0);

        // Released: the deck stops and the playhead returns to the plan's cue point.
        double cue = dj.PendingPlan!.CueInSeconds;
        Assert.True(dj.AuditionCued(false));
        Assert.False(dj.IsAuditioning);
        Assert.Equal(cue, dj.CuedPositionSeconds, 1);

        dj.HoldDeck(DjDeck.Live, false);
        dj.Read(buffer, Block);
        Assert.Equal(1f, Math.Abs(buffer[Block * Channels - 1]), 2);
    }

    [Fact]
    public void A_changeover_still_lands_after_an_audition()
    {
        using var dj = Booth(
            Track("one", 128, duration: 120, outro: 100),
            Track("two", 128, duration: 120, outro: 100));

        MixTrack? landed = null;
        dj.TrackChanged += track => landed = track;

        var buffer = new float[Block * Channels];
        dj.Read(buffer, Block);

        Assert.True(dj.CueNext());
        int guard = 0;
        while (dj.PendingPlan is null && guard++ < 200)
            dj.Read(buffer, Block);

        Assert.True(dj.AuditionCued(true));
        for (int i = 0; i < 100; i++)
            dj.Read(buffer, Block);
        Assert.True(dj.AuditionCued(false));

        // The set continues and the changeover happens by itself, from the plan's own cue. The
        // opening record was reported on the first block, so only changeovers from here count.
        landed = null;
        guard = 0;
        while (landed is null && guard++ < 5000 && !dj.HasFinished)
            dj.Read(buffer, Block);

        Assert.Equal("two", landed?.Path);
    }

    [Fact]
    public void Scrubbing_the_playing_record_ends_the_audition_instead_of_stranding_it()
    {
        using var dj = Booth(
            Track("one", 128, duration: 120, outro: 100),
            Track("two", 128, duration: 120, outro: 100));

        var buffer = new float[Block * Channels];
        dj.Read(buffer, Block);

        Assert.True(dj.CueNext());
        int guard = 0;
        while (dj.PendingPlan is null && guard++ < 200)
            dj.Read(buffer, Block);
        Assert.True(dj.AuditionCued(true));

        // The scrub puts the cued record back in the crate. The audition must end with it, not keep
        // running on an empty deck with the set waiting for ever.
        Assert.True(dj.SeekTo(40));
        Assert.False(dj.IsAuditioning);

        MixTrack? landed = null;
        dj.TrackChanged += track => landed = track;
        guard = 0;
        while (landed is null && guard++ < 5000 && !dj.HasFinished)
            dj.Read(buffer, Block);
        Assert.Equal("two", landed?.Path);
    }

    [Fact]
    public void An_edit_that_replans_ends_the_audition_the_proper_way()
    {
        using var dj = Booth(
            Track("one", 128, duration: 120, outro: 100),
            Track("two", 128, duration: 120, outro: 100));

        var buffer = new float[Block * Channels];
        dj.Read(buffer, Block);

        Assert.True(dj.CueNext());
        int guard = 0;
        while (dj.PendingPlan is null && guard++ < 200)
            dj.Read(buffer, Block);
        Assert.True(dj.AuditionCued(true));

        Assert.True(dj.Replan());
        Assert.False(dj.IsAuditioning);

        // And the set is not stuck: it plans again and the changeover happens.
        MixTrack? landed = null;
        dj.TrackChanged += track => landed = track;
        guard = 0;
        while (landed is null && guard++ < 5000 && !dj.HasFinished)
            dj.Read(buffer, Block);
        Assert.Equal("two", landed?.Path);
    }

    [Fact]
    public void A_hand_edit_lands_on_a_deck_that_already_knows_its_record()
    {
        // The guard that stops the background sweep from overwriting a known reading must not refuse
        // EVERY reading, including the user's edit. Otherwise every edit to a playing record silently
        // does nothing, and the workbench buttons appear broken.
        var deck = new Deck(Deck.Side.A, Rate, Channels, _ => new ToneSource(0.5f, 60));
        var track = Track("x", 128);
        deck.Load(track, cueSeconds: 0, tempoRatio: 1);

        // A measured reading for a record the deck already knows: not newer, only arrived later.
        // Refused.
        var other = GridEdit.Regrid(track.Analysis!, 120, 0, 4) with { IsEdited = false };
        deck.AdoptAnalysis(track with { Analysis = other });
        Assert.Equal(128, deck.Track?.Bpm ?? 0, 1);

        // A user's octave edit: twice the count, marked edited. It must be accepted.
        var doubled = GridEdit.Octave(track.Analysis!, 2);
        deck.AdoptAnalysis(track with { Analysis = doubled });
        Assert.Equal(256, deck.Track?.Bpm ?? 0, 1);

        // And the measured reading put BACK by the user undoing the edit is accepted too.
        deck.AdoptAnalysis(track with { Analysis = track.Analysis });
        Assert.Equal(128, deck.Track?.Bpm ?? 0, 1);
    }

    [Fact]
    public void A_jump_to_just_before_the_mix_point_still_gets_the_planned_mix()
    {
        // Outro at 100 of 120: the mix-out point is inside the record's last minute, as for nearly
        // every record. Jumping to just before it must not trigger the scrubbed-to-the-end rule and
        // turn the changeover into a cut: the user wants to hear the mix point. Play-out is only for
        // a jump AT or PAST the mix point, not near it.
        using var dj = Booth(
            Track("one", 128, duration: 120, outro: 100),
            Track("two", 128, duration: 120, outro: 100));

        var buffer = new float[Block * Channels];
        dj.Read(buffer, Block);

        Assert.True(dj.SeekTo(85));
        int guard = 0;
        while (dj.PendingPlan is null && guard++ < 2000)
            dj.Read(buffer, Block);

        Assert.Equal(TransitionStyle.BeatMatched, dj.PendingPlan?.Style);
    }

    [Fact]
    public void A_jump_past_the_mix_point_plays_the_record_out()
    {
        using var dj = Booth(
            Track("one", 128, duration: 120, outro: 100),
            Track("two", 128, duration: 120, outro: 100));

        var buffer = new float[Block * Channels];
        dj.Read(buffer, Block);

        // Past the planned handover: there is no room for the planned mix, and the jump clearly means
        // "I want to be here". The record plays to the end and the next one follows.
        Assert.True(dj.SeekTo(105));
        int guard = 0;
        while (dj.PendingPlan is null && guard++ < 2000)
            dj.Read(buffer, Block);

        Assert.Equal(TransitionStyle.Cut, dj.PendingPlan?.Style);
    }

    [Fact]
    public void The_transport_refuses_a_deck_with_nothing_to_hold()
    {
        using var dj = Booth(
            Track("one", 128, duration: 120, outro: 100),
            Track("two", 128, duration: 120, outro: 100));

        var buffer = new float[Block * Channels];
        dj.Read(buffer, Block);

        Assert.False(dj.HoldDeck(DjDeck.Cued, true), "nothing is cued yet");
        Assert.False(dj.AuditionCued(true), "nothing to audition yet");
        Assert.False(dj.SeekDeck(DjDeck.Cued, 10), "the cued needle belongs to the plan outside an audition");
    }

    // ── harness ───────────────────────────────────────────────────────────────

    /// <summary>A tone whose value marks the record: "one" is 1.0, "two" is 0.25, so a block of mix
    /// says which record is audible.</summary>
    private sealed class ToneSource(float value, double seconds) : IPcmSource
    {
        private long _left = (long)(seconds * Rate);

        public string Label => "tone";

        public int Read(float[] buffer, int frames)
        {
            if (_left <= 0) return 0;
            int give = (int)Math.Min(frames, _left);
            for (int i = 0; i < give * Channels; i++)
                buffer[i] = value;
            _left -= give;
            return give;
        }

        public void Dispose() => _left = 0;
    }

    private static Dj Booth(params MixTrack[] tracks) =>
        new(
            new DjCrate(tracks), Rate, Channels,
            options => new ToneSource(
                options.Source == "one" ? 1f : 0.25f,
                120 - options.StartSeconds),
            cueRunner: work => { work(); return Task.CompletedTask; },
            chance: () => 1);

    private static MixTrack Track(string path, double bpm, double duration = 120, double? outro = null)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / bpm)
            beats.Add(at);

        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = path,
            TaggedDuration = duration,
            Analysis = new TrackAnalysis
            {
                FilePath = path,
                DurationSeconds = duration,
                Bpm = bpm,
                BpmConfidence = 0.9,
                BeatTimes = beats,
                FirstDownbeat = 0,
                BeatsPerBar = 4,
                EnergyCurve = [0.3f, 0.3f],
                Intensity = 0.5,
                IntroEndSeconds = 8,
                OutroStartSeconds = outro ?? Math.Max(1, duration - 20),
            },
        };
    }
}
