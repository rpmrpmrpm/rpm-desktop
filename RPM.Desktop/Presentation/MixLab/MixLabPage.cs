#if DEBUG
#if RPM_SKIA
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Analysis;
using RPM.Core.Mixing;
using RPM.Desktop.Services.Audio;
using RPM.Desktop.Services.Platform;
using Windows.UI;

namespace RPM.Desktop.Presentation.MixLab;

/// <summary>
/// The mix lab: two decks, the changeover between them, and every number that decided it.
///
/// <para>It is made to be argued with. Every number the planner produced is shown next to the reasoning
/// that produced it. Every measurement the planner did not read is shown next to what it would have
/// changed. Anything a person disagrees with can be moved, and then heard. The disagreement is written
/// down. A DJ who moves the mix-out point eight bars earlier on forty records has described a fault in
/// <see cref="TransitionPlanner.MixOutPoint"/> more precisely than any amount of reading the code
/// would. The journal from a session is that description. It is later turned into planner rules. That
/// is why the planner takes its numbers from <see cref="MixPolicy"/> knobs and not from
/// constants.</para>
///
/// <para>Both records are equal. Each deck has its own transport. One record can be held while the
/// other plays. The record coming in can be auditioned from its cue point and scrubbed. Each deck also
/// has its own edit panel for the grid and the cue points, and its own timeline. The changeover itself
/// is edited in the middle strip. There you set where it starts and ends, which style it uses, the
/// volume arc that each record follows, and the policy knobs the planner starts from.</para>
///
/// <para>Debug builds only. It reads the booth directly and puts hand edits into a running set. That is
/// the right amount of access for a workbench. None of it belongs in a shipped app.</para>
/// </summary>
public sealed partial class MixLabPage : Page
{
    private readonly DjService _dj;
    private readonly PlaybackService _playback;
    private readonly MixLabJournal _journal;
    private readonly MixLabPeaks _peaks = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(120) };

    // ── the picture ──
    private readonly MixDeckView _deckA = new("Playing");
    private readonly MixDeckView _deckB = new("Coming in");

    /// <summary>The two records under one needle, a couple of bars wide. See BeatAlignView. The pair
    /// above answers "where in each record are we". This answers "are the two on the same
    /// beat".</summary>
    private readonly BeatAlignView _align = new();
    private readonly MixArcEditor _arc = new();
    /// <summary>Each deck's channel on the mixer. See MixerStrip. This is the one part of a changeover
    /// that has no picture anywhere else on this page.</summary>
    private readonly MixerStrip _mixerA = new("Playing");
    private readonly MixerStrip _mixerB = new("Coming in");
    private readonly TextBlock _heading = Label(16, bold: true);

    /// <summary>
    /// Said out loud across the top when no changeover is going to be made from the record playing.
    /// This is especially true when a needle that somebody moved is the reason.
    ///
    /// <para>The state line also shows this, in the same dim ink as the output lag and the bar count.
    /// That is easy to miss. A workbench that says "the mix is off, and you turned it off" in the
    /// eleventh word of a grey line has not really said it. Moving the needle into the last stretch of
    /// a record turns the mix off for the rest of that record. A person does this by accident every few
    /// minutes. Without this banner, the only way to find out is that the changeover never
    /// comes.</para>
    /// </summary>
    private readonly Border _warning = new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(9, 4, 9, 5),
        Margin = new Thickness(0, 3, 0, 1),
        HorizontalAlignment = HorizontalAlignment.Left,
        Visibility = Visibility.Collapsed,
    };

    private readonly TextBlock _warningText = Label(13, bold: true);
    private readonly TextBlock _state = Label(12, dim: true);
    private readonly StackPanel _planList = new() { Spacing = 3 };
    private readonly TextBlock _journalText = Label(11, dim: true);

    // ── the mix strip's controls ──
    private readonly ComboBox _fadeBars = Condensed.Drop(84);
    private readonly ComboBox _mode = Condensed.Drop(180);
    private readonly ComboBox _style = Condensed.Drop(190);
    private readonly Slider _pull = Knob(5, 25, TransitionPlanner.MaxTempoPull * 100);
    private readonly Slider _drift = Knob(5, 60, TransitionPlanner.MaxDriftSeconds * 1000);
    private readonly Slider _noise = Knob(0, 100, TransitionPlanner.NoiseChance * 100);

    private Button? _skip;

    /// <summary>The set's own transport, a pair like each deck's: whichever key names the state the set
    /// is in is the lit one.</summary>
    private Button? _setPlay;
    private Button? _setPause;

    /// <summary>The rehearsal switch. When it is on, the set plays the same changeover again and again
    /// instead of moving on. See <see cref="Dj.RehearseChangeover"/>.</summary>
    private readonly Microsoft.UI.Xaml.Controls.Primitives.ToggleButton _loop = new()
    {
        Content = MixDeckView.Glyph(Fa.Repeat),
        Padding = new Thickness(9, 5, 9, 5),
    };

    /// <summary>
    /// Who picks the changeover, and by what rule.
    ///
    /// <para>There are three states, not a style box with a tick box beside it. A tick box would be a
    /// second control called Auto. One question is "am I choosing, or is the app?" The other is "what
    /// does the app choose by?" A control for each question would look like two copies of the same
    /// control.</para>
    /// </summary>
    private enum MixMode
    {
        /// <summary>A hand picks the changeover, out of the style box.</summary>
        Manual,

        /// <summary>The planner picks. It makes the one changeover this app has always made: the
        /// beat-matched blend, or whatever the pair of records can support instead.</summary>
        Usual,

        /// <summary>The planner picks a changeover to suit the two records. See
        /// <see cref="MixTechniques.Best"/> and <see cref="MixPolicy.Techniques"/>.</summary>
        Suit,

        /// <summary>
        /// Auto X: everything <see cref="Usual"/> makes, laid out on the records' own phrasing rather
        /// than on the fours. See <see cref="MixPolicy.PhraseBlend"/>.
        ///
        /// <para>It makes exactly the changeovers that <see cref="Usual"/> makes, and no others. These
        /// are the beat-matched blend, started on a measured phrase line and run for two whole phrases.
        /// It also makes the same fade or cut where the pair cannot be matched. The next key still
        /// rolls out. A pair with no measured phrase is planned exactly as Usual plans it.</para>
        ///
        /// <para>The drop swap and the bass swap are off. See <see cref="MixPolicy.PhraseMoves"/>. The
        /// phrase layout is what makes those two plannable. So if the layout alone were turned on, they
        /// would be turned on with it, and this mode would make changeovers that Auto never makes.
        /// Alignment is the only thing this mode changes.</para>
        /// </summary>
        Phrase,
    }

    /// <summary>One entry in the mode list.</summary>
    private sealed record ModeItem(MixMode Mode)
    {
        public override string ToString() => Mode switch
        {
            MixMode.Manual => "Manual",
            MixMode.Suit => "Auto (suit the records)",
            MixMode.Phrase => "Auto X",
            _ => "Auto",
        };
    }

    /// <summary>
    /// One entry in the style list: a changeover, under the name a DJ uses for it, not the name the
    /// enum uses.
    ///
    /// <para>It is an object with a display name, not the bare enum value. The box is read by people.
    /// "Cut on the one" and "Loop roll out" are what these moves are called. "Slam" and "RollOut" are
    /// what the code calls them.</para>
    /// </summary>
    private sealed record StyleItem(TransitionStyle Style)
    {
        public override string ToString() => MixTechniques.For(Style).Name;
    }

    /// <summary>
    /// Which of the three modes is in force. This is the source of truth. The two boxes are drawn from
    /// it, and they are never read to find it.
    ///
    /// <para>Auto is the mode that every set outside this window is planned with. The workbench does
    /// not start in Auto X. If it did, the lab would be the one place in the app where the changeover
    /// under study is not the changeover the app makes. Auto X is one item away in the box, for anybody
    /// who wants to compare the two.</para>
    /// </summary>
    private MixMode _askedMode = MixMode.Usual;

    /// <summary>Whether the style box is showing a settled answer yet. This is shown beside the box. In
    /// an auto mode the box is only a readout, and a readout that cannot say "not yet" makes a promise
    /// it cannot keep.</summary>
    private readonly TextBlock _autoPick = Label(12, dim: true);

    /// <summary>Why the workbench is empty while a set starts. The lab holds the set until the model
    /// has read the two records that its first changeover uses. This reason is given in two places: the
    /// heading before there is a booth, and the state line after it.</summary>
    private const string Waiting =
        "Beat model running. The set starts when the first two tracks are read.";

    /// <summary>How much audio the workbench lets sit between the mixer and the speakers: 200 ms in the
    /// decode ring and 200 ms on the device. Four blocks each. That is enough that an ordinary
    /// scheduling gap passes unheard, and little enough that a press and its sound are one event. A
    /// record that stutters here is the price of hearing what you just did.</summary>
    private const double TightSeconds = 0.4;

    /// <summary>What the changeover would be if it happened from here. It is recalculated on the slow
    /// tick, and straight after any hand change. The planner is pure and cheap. A preview that is one
    /// changeover behind is worse than having none.</summary>
    private TransitionPlan? _preview;

    /// <summary>
    /// True when <see cref="_preview"/> is the changeover that the set has actually set up. False while
    /// it is the one that would happen if it were worked out from here.
    ///
    /// <para>The difference matters. A speculative plan is worked out without a luck value, so it can
    /// never come back as one of the two rationed moves, the echo out and the rewind. That is true
    /// however likely either one is. Anything shown from a speculative plan is a guess that can still
    /// change, and the display says so.</para>
    /// </summary>
    private bool _previewSettled;
    private MixTrack? _outgoing, _incoming;
    private DateTime _previewedAt = DateTime.MinValue;
    private string _builtFor = "";

    // The changes that hands have asked for, on top of what the planner would do. They are kept here,
    // not read from the controls. The controls show the settled plan (see Refresh). An empty box beside
    // a decided changeover would read as "nothing decided", which is wrong. Only a change made by a
    // hand becomes an ask.
    private double? _askedStart, _askedCue;
    private int? _askedBars;
    private TransitionStyle? _askedStyle;
    private FadeArc? _askedArc;

    /// <summary>Where a drag started, so that the journal records one edit and not one per
    /// pixel.</summary>
    private TrackAnalysis? _dragFrom;
    private TransitionPlan? _dragPlan;

    /// <summary>The reading that each record had when the session started. So an edit can always be
    /// measured against what the analyser said, and it can always be undone.</summary>
    private readonly Dictionary<string, TrackAnalysis> _measured = new(StringComparer.Ordinal);

    /// <summary>Deck B's play key was pressed before anything was on the deck. The record is being
    /// cued, and the audition starts as soon as it arrives.</summary>
    private bool _wantAudition;

    /// <summary>The record that the workbench has already asked to have cued. This makes the ask once
    /// per record, not on every tick. It is cleared as soon as the record is on the deck.</summary>
    private string _cueAskedFor = "";

    /// <summary>Where a hand put the second deck's needle before the record was on the platter, and
    /// whether the deck has been told yet. This is held until the needle is really there. A record that
    /// starts playing while its seek is still in progress would play a second of the wrong place
    /// first.</summary>
    private (double At, bool Sent)? _cuedNeedle;

    /// <summary>True while this page is moving its own controls, so a slider put back to its default is
    /// not read as a hand turning it.</summary>
    private bool _quiet;

    /// <summary>
    /// The booth that this page has already handed its ask to. See <see cref="HandOverTheAsk"/>.
    ///
    /// <para>This is kept as a reference, not as a flag. A set can end and another can start while this
    /// window stays open. The new set has never been told what the boxes on screen are showing.</para>
    /// </summary>
    private Dj? _told;

    /// <summary>The last ask that the set turned down, and when. It is shown on the state line for a
    /// few seconds. A refusal that is not reported makes a control look as if it worked.</summary>
    private (string What, DateTime At)? _refused;

    /// <summary>How long a refusal stays on the line. Long enough to be read, short enough that it is
    /// gone before the next thing is tried.</summary>
    private static readonly TimeSpan RefusalShowsFor = TimeSpan.FromSeconds(4);

    /// <summary>A knob is being dragged. The ask is committed once the hand has been still for a
    /// moment, not once per pixel. A drag is one opinion, not two hundred journal lines.</summary>
    private bool _knobDirty;
    private DateTime _knobTouchedAt = DateTime.MinValue;

    /// <summary>The arc that every changeover starts from. It is built once. The strip shows it
    /// whenever nothing was drawn. A new instance on each tick would repaint for no reason.</summary>
    private static readonly FadeArc DefaultArc = FadeArc.EqualPower();

    public MixLabPage(DjService dj, PlaybackService playback)
    {
        _dj = dj;
        _playback = playback;
        _journal = new MixLabJournal(Path.Combine(LocalData.Dir, "mixlab"));

        Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x10, 0x14));
        RequestedTheme = ElementTheme.Dark;
        Content = Build();

        WireDeck(_deckA, DjDeck.Live);
        WireDeck(_deckB, DjDeck.Cued);

        _arc.ArcMoved += arc => { _askedArc = arc; Preview(now: true); };
        _arc.ArcCommitted += _ => CommitAsk("volume arc drawn");

        // There are two clocks, on purpose. The panels are text and tables, and they are redrawn eight
        // times a second. The playheads are lines against bar lines, and they are redrawn every frame.
        // The still layers behind them are cached pictures, so a frame is only a blit and a line. The
        // frame clock runs only while a deck is audibly moving (see Refresh). A Rendering subscription
        // asks for a frame at every vsync, and a renderer with no dirty rectangles then repaints
        // everything. A lab that sits paused must not keep the whole app painting for nothing.
        _tick.Tick += (_, _) => Refresh();

        // The end of a rehearsed changeover. The DJ has already put both records back and set the
        // run-up. The stop is done by this page, because pause belongs to the engine. A source that
        // held its own pause as well would need two pauses to get out of.
        //
        // This event is raised on the audio thread, so it comes back to this one before anything is
        // touched. It is stopped the same way the pause key stops the set, with both decks included. A
        // rehearsal ends standing in front of the changeover with both records to hand. That is the
        // point of playing one over and over. A stop that left the decks with the DJ would offer
        // neither of them.
        _dj.ChangeoverRehearsed += () => DispatcherQueue.TryEnqueue(() => RunSet(false));

        // The workbench runs the audio buffers short: 200 ms in the decode ring and 200 ms on the
        // device. A normal player uses about a second in the decode ring and half a second on the
        // device, and nobody can hear the difference there. In the workbench, the delay is the
        // measurement. A platter stopped, a fader moved or a needle put down would all take effect a
        // second after the click, and that would make the workbench impossible to judge. The short
        // buffers are put back when the lab closes, because the longer margin is worth having
        // everywhere else.
        Loaded += (_, _) =>
        {
            _playback.WorkAhead(TightSeconds);
            Refresh();
            _tick.Start();
        };
        Unloaded += (_, _) =>
        {
            _playback.WorkAhead(null);
            _tick.Stop();
            SetFraming(false);
        };
    }

    private bool _framing;

    private void SetFraming(bool on)
    {
        if (_framing == on)
            return;
        _framing = on;
        if (on)
            CompositionTarget.Rendering += OnFrame;
        else
            CompositionTarget.Rendering -= OnFrame;
    }

    // ── layout ────────────────────────────────────────────────────────────────
    /// <summary>
    /// The workbench, in the order the work happens: the two records side by side, the changeover
    /// between them under both, and the readings under that.
    ///
    /// <para>The two decks share a row because they are the same thing twice, and the comparison is the
    /// whole job. A record on the left and the record it hands over to on the right are at the same
    /// zoom, with their needles level. So the pair can be compared at the same scale.</para>
    ///
    /// <para>Nothing above the bottom tables scrolls. A ScrollViewer pans by direct manipulation, and
    /// that takes the pointer off a marker in the middle of a drag. So the waveforms, the arc and every
    /// control that answers to a drag stay in fixed rows. Only the tables underneath move.</para>
    /// </summary>

    private UIElement Build()
    {
        var root = new Grid { Padding = new Thickness(12), RowSpacing = 10 };
        for (int i = 0; i < 4; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(_heading);

        _warning.Child = _warningText;
        header.Children.Add(_warning);

        header.Children.Add(_state);
        header.Children.Add(_journalText);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // The pair.
        var decks = Side(1, 1);
        Put(decks, Section(_deckA), 0);
        Put(decks, Section(_deckB), 1);
        Grid.SetRow(decks, 1);
        root.Children.Add(decks);

        // And the same two records again, zoomed in to the needle. This is directly under the decks,
        // because it is the same pair at a different scale. Up there, one pixel is a few hundred
        // milliseconds. Whether two kicks are one kick is decided at a much smaller time scale.
        var alignment = Section("Beat alignment", _align);
        Grid.SetRow(alignment, 2);
        root.Children.Add(alignment);

        // The changeover, and the changeover it came out as. Two thirds to one: the left part holds the
        // controls that a hand uses, and the right part is a readout that nobody types into.
        var mix = Side(2, 1);
        Put(mix, Section("Changeover", MixStrip()), 0);
        Put(mix, Section("Plan", _planList), 1);
        Grid.SetRow(mix, 3);
        root.Children.Add(mix);

        // The mixer: each deck's fader and its three knobs, as the set is running them.
        //
        // The bar information under each needle and the measurement tables are not shown here. They are
        // facts that you check once about a record. They are not what you need during a changeover.
        // What you need to watch during a changeover is what has no picture anywhere else: the levels
        // moving, and the bass changing hands.
        var mixer = Side(1, 1);
        Put(mixer, Section(_mixerA), 0);
        Put(mixer, Section(_mixerB), 1);
        Grid.SetRow(mixer, 4);
        root.Children.Add(mixer);

        return root;
    }

    /// <summary>Two panels side by side, in the given proportion.</summary>
    private static Grid Side(double left, double right)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(right, GridUnitType.Star) });
        return grid;
    }

    private static void Put(Grid grid, UIElement panel, int column)
    {
        // Grid declares its attached properties on FrameworkElement in WinUI and on UIElement in Uno.
        // Everything put in a column here is a FrameworkElement.
        Grid.SetColumn((FrameworkElement)panel, column);
        grid.Children.Add(panel);
    }

    /// <summary>The changeover's own controls: the set transport, the shape of the mix, the volume arc,
    /// and the knobs the planner starts from.</summary>
    private UIElement MixStrip()
    {
        var strip = new StackPanel { Spacing = 6 };

        // The row is built first and added last: on a deck the picture is on top and the keys are under
        // it, and the card that drives both decks reads the same way.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        // Play and pause as a pair, like the two decks below. The lit one is where the set is, not what
        // the key would do, so all three transports on the page read the same way at a glance.
        _setPlay = MixDeckView.Icon(Fa.Play, "Play the set", () => RunSet(true));
        _setPause = MixDeckView.Icon(Fa.Pause, "Pause the set", () => RunSet(false));
        row.Children.Add(_setPlay);
        row.Children.Add(_setPause);

        _skip = MixDeckView.Icon(Fa.Next, "Skip to next track", () => _dj.SkipToNext());
        row.Children.Add(_skip);
        row.Children.Add(MixDeckView.Icon(Fa.Undo, "Undo all edits", Revert));

        // Plays the same changeover over and over. This is a toggle, not a key, because the set stays
        // in this mode. The whole workbench is for this mode. A mix judged once every four minutes is a
        // mix tuned from memory.
        ToolTipService.SetToolTip(_loop,
            "Play the changeover again and again. When one finishes, both records go back and the " +
            "set pauses a couple of phrases in front of it. Press play to hear it again.");
        _loop.Click += (_, _) =>
        {
            _dj.RehearseChangeover = _loop.IsChecked == true;
            Refresh();
        };
        row.Children.Add(_loop);

        row.Children.Add(new Border { Width = 10 });

        row.Children.Add(Label(12, dim: true, text: "fade bars"));
        foreach (int n in new[] { 1, 2, 4, 8, 16, 32 })
            _fadeBars.Items.Add(n);
        _fadeBars.SelectionChanged += (_, _) =>
        {
            if (_quiet)
                return;
            _askedBars = _fadeBars.SelectedItem as int?;
            CommitAsk("fade length asked for");
        };
        row.Children.Add(_fadeBars);

        row.Children.Add(Label(12, dim: true, text: "style"));

        // Who picks, then what was picked. In an auto mode, the second box is a readout of the first
        // box's answer, and it is disabled for the duration. It is the same list. A hand that wants
        // that changeover every time only has to switch to Manual, and it is already selected. Three
        // modes. The planner's own way, which is the same thing laid out on the phrasing. And a hand
        // picking one of the four changeovers this app makes. Auto and Auto X differ in one respect,
        // and it is the one worth hearing: the same pair of records, on the fours and on the phrase,
        // with everything else held still.
        //
        // Auto (suit the records) is not on the list. It is the only mode that reaches the six moves
        // taken from other rooms. Four of those are not finished: the cut on the one, the filter fade,
        // the echo out and the spinback. A mode that produces an unfinished changeover three times an
        // hour would make the whole workbench look broken. It is still in the enum, and Core can still
        // reach it (MixPolicy.Techniques) for whoever finishes them.
        foreach (var m in new[] { MixMode.Usual, MixMode.Phrase, MixMode.Manual })
            _mode.Items.Add(new ModeItem(m));
        _mode.SelectionChanged += (_, _) =>
        {
            if (_quiet || (_mode.SelectedItem as ModeItem)?.Mode is not { } picked || picked == _askedMode)
                return;

            _askedMode = picked;

            // Switches to Manual with the phrase blend, which is what Manual has always made. Auto's
            // own changeover is on the list beside it, in the style box, so it can be picked on
            // purpose. If Manual defaulted to it, switching to Manual would change nothing, and there
            // would be no reason to switch.
            //
            // Not whatever is on screen. A speculative plan can be a cut or a fade. Neither is in the
            // box, so the box would be left empty and the changeover would have no name.
            _askedStyle = picked == MixMode.Manual
                ? _preview?.Style is { } shown && _style.Items.OfType<StyleItem>().Any(i => i.Style == shown)
                    ? shown
                    : TransitionStyle.PhraseBlend
                : null;

            CommitAsk($"changeover picked by {new ModeItem(picked)}");
        };
        row.Children.Add(_mode);

        // The four. The planner can make other changeovers, such as a cut, a fade or a roll out. It
        // makes them because the pair of records leaves it no choice. A hand does not pick those. They
        // happen when there is nothing to pick.
        //
        // Both blends are on the list on purpose. A hand in Manual could only reach the phrase blend.
        // The ordinary changeover, the one Auto makes at the outro on the fours, with the new record's
        // intro playing underneath, could not be picked by hand. The two are now separate styles, so
        // both are on the list.
        foreach (var s in new[]
                 {
                     TransitionStyle.BeatMatched,
                     TransitionStyle.PhraseBlend,
                     TransitionStyle.BassSwap,
                     TransitionStyle.DoubleDrop,
                 })
            _style.Items.Add(new StyleItem(s));
        _style.SelectionChanged += (_, _) =>
        {
            if (_quiet || _askedMode != MixMode.Manual)
                return;
            _askedStyle = (_style.SelectedItem as StyleItem)?.Style;
            CommitAsk("style asked for");
        };
        row.Children.Add(_style);
        row.Children.Add(_autoPick);

        // The volume arc: what each record's level does through the fade, and where the bass changes
        // hands. Starts as the equal-power curve the decks would follow anyway.
        strip.Children.Add(_arc);

        var knobs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        knobs.Children.Add(Label(11, dim: true, text: "pull %"));
        knobs.Children.Add(_pull);
        knobs.Children.Add(Label(11, dim: true, text: "drift ms"));
        knobs.Children.Add(_drift);
        knobs.Children.Add(Label(11, dim: true, text: "noise %"));
        knobs.Children.Add(_noise);
        knobs.Children.Add(MixDeckView.Push("Reset all", ResetAsk));

        // What the two curves above are. This is shown outside the strip, not drawn on it. Both top
        // corners of the strip are always covered by a curve at full level, because that is what a
        // crossfade is. So a legend inside the strip would sit on top of the thing it names.
        knobs.Children.Add(new Border { Width = 6 });
        knobs.Children.Add(Key(Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF), "outgoing level"));
        knobs.Children.Add(Key(Color.FromArgb(0xFF, 0x4C, 0xD9, 0x8A), "incoming level"));
        knobs.Children.Add(Key(Color.FromArgb(0xFF, 0xF2, 0xC0, 0x4C), "on a downbeat"));

        // The two voice lanes under each deck's ruler. They are named here, because an unlabelled pair
        // of bands is hard to read. The top lane is what the changeover is placed by. The bottom lane
        // is the record itself. Drawing both shows whether the lyric sheet's timings drift away from
        // the song.
        knobs.Children.Add(new Border { Width = 6 });
        knobs.Children.Add(Key(Color.FromArgb(0xFF, 0xE8, 0xC0, 0x6A), "voices: lyric sheet"));
        knobs.Children.Add(Key(Color.FromArgb(0xFF, 0x6A, 0xC8, 0xE8), "voices: measured"));
        knobs.Children.Add(Key(Color.FromArgb(0xFF, 0xF2, 0x8A, 0xB0), "voices: both agree (used)"));

        // A slider fires for every pixel of a drag. The ask is applied once the hand has been still for
        // a moment (see Refresh). A drag is one opinion, not two hundred journal lines.
        foreach (var knob in new[] { _pull, _drift, _noise })
            knob.ValueChanged += (_, _) =>
            {
                if (_quiet)
                    return;
                _knobDirty = true;
                _knobTouchedAt = DateTime.UtcNow;
            };
        strip.Children.Add(knobs);
        strip.Children.Add(row);

        return strip;
    }

    // ── deck wiring ───────────────────────────────────────────────────────────

    private void WireDeck(MixDeckView view, DjDeck which)
    {
        view.Scrubbed += at =>
        {
            if (which == DjDeck.Live)
            {
                _dj.SeekTo(at);
                return;
            }

            // A needle put down on a record that no deck is holding yet. The click is not refused,
            // because a refused click does nothing on a deck that looks ready. The place is remembered,
            // the record is asked for, and it lands with its needle already there. Pressing play plays
            // it from where the needle was put.
            if (_dj.Booth is not { } booth || !booth.SeekDeck(DjDeck.Cued, at))
                _cuedNeedle = (at, Sent: false);
        };

        view.TransportAsked += play => RunDeck(which, play);
        view.CueAsked += () => CueDeck(which);
        view.RejectAsked += RejectCued;

        view.MarkerMoved += (handle, at) => Move(which, handle, at);
        view.MarkerDropped += _ => Settle(which);
        view.DownbeatPicked += at => NameTheOne(which, at);

        view.RegridAsked += (bpm, perBar) => Regrid(which, bpm, perBar);
        view.OctaveAsked += factor => GridTouch(which, a => GridEdit.Octave(a, factor));
        view.NudgeAsked += seconds => GridTouch(which, a => GridEdit.NudgePhase(a, seconds));
    }

    /// <summary>
    /// What the booth is doing, in one reading. See <see cref="BoothView"/>. It is read fresh each
    /// time. A handler can run between two ticks, and the reading from the last tick would then be
    /// older than what the hand has just done.
    /// </summary>
    private BoothView Look() => _dj.Booth?.Look(_dj.IsPaused) ?? BoothView.Empty;

    /// <summary>The reading this frame of the interface was drawn from. Written once per
    /// <see cref="Refresh"/> and read by everything the page shows, so that no two lines on screen are
    /// about two different instants.</summary>
    private BoothView _look = BoothView.Empty;

    /// <summary>
    /// Whether the set is running. That is, the DJ has the decks and the engine is turning. The whole
    /// transport of the page is answered from this one question. The set's keys light up when it is
    /// true. The decks' keys work only when it is false. Pausing and playing the set changes it.
    ///
    /// <para>This is not the engine's own pause. Playing a record by hand turns the engine back on. A
    /// page that read the engine would call that "the set is running". Then the set's key would light
    /// up, the deck keys would shut, and a record that you had just started playing could not be
    /// stopped.</para>
    /// </summary>
    private bool SetIsRunning => _dj.Booth is { HandsOn: false } && !_dj.IsPaused;

    /// <summary>
    /// The set's transport. Either the DJ works, or the DJ stands back and hands the pair of decks
    /// over.
    ///
    /// <para>Playing first puts the booth back in order. The record coming in goes back to the plan's
    /// cue point, so the two decks show the changeover that is about to happen. The record that is
    /// playing carries on from wherever a hand left it. Pausing stops both platters and hands them
    /// over.</para>
    /// </summary>
    private void RunSet(bool play)
    {
        var booth = _dj.Booth;
        if (play == SetIsRunning)
            return;

        _wantAudition = false;
        _cuedNeedle = null;

        if (play)
        {
            // The DJ takes the decks back in order. The record coming in is parked on the plan's cue
            // point, and both platters are let go, before any sound is heard.
            booth?.TakeTheDecks(false);
            _playback.Resume();
        }
        else if (booth?.TakeTheDecks(true) != false)
        {
            // Both platters stop where they stand. The engine keeps running, and it plays the silence
            // that the two stopped decks make. There are two reasons for this. The first is that the
            // booth answers a hand's asks on the audio thread. A stopped engine is a booth that never
            // hears "put the next record on", so it never puts one on. Without the engine running, deck
            // two could not be played at all. The second reason is what a stopped engine sounds like
            // when it starts again. A second of the record that was decoded before the platter stopped
            // is still in the device's queue. It would play on top of whatever was really asked for.
            booth?.HoldDeck(DjDeck.Live, true);
            booth?.HoldDeck(DjDeck.Cued, true);
        }
        else
        {
            // A changeover is running, so the decks cannot be taken by the DJ. A fade moves both decks
            // together. So the engine is stopped instead. That freezes the whole changeover where it
            // stands. It does not pretend that the pause did not happen.
            _playback.Pause();
        }

        Refresh();
    }

    /// <summary>
    /// One deck's transport. Its only job is to play or pause this record. Both decks work the same
    /// way, and either can play while the other plays. That is what the pair is for. One record can
    /// play to find the spot to leave it on, and the other can play to hear where it comes in.
    ///
    /// <para>This is only reached while the decks are under the DJ's control. The keys are shut while
    /// the DJ is working. Asking for the state that a deck is already in does nothing. That is why
    /// there are two keys and not one.</para>
    /// </summary>
    private void RunDeck(DjDeck which, bool play)
    {
        if (_dj.Booth is not { HandsOn: true } booth)
            return;
        if (play == (booth.IsDeckPlaying(which) && !_dj.IsPaused))
            return;

        if (!play)
        {
            // Stopped where it stands. The audition does not end here. Ending it would put the needle
            // back on the plan's cue point. A pause that moved the record would be no use to somebody
            // looking for a spot in it. The audition ends when the DJ takes the decks back.
            _wantAudition = false;
            booth.HoldDeck(which, true);
            Refresh();
            return;
        }

        // The record coming in may not be on a platter yet. Outside a changeover, the DJ cues it about
        // twelve seconds ahead. Before that, it is only a choice. So ask for the changeover to be
        // worked out now, and start the record as soon as it arrives (see Refresh).
        if (which == DjDeck.Cued && !booth.IsAuditioning)
        {
            _wantAudition = true;
            if (booth.PendingPlan is null)
                booth.CueNext();
            if (!TryStartAudition(booth) && _playback.IsPaused)
                _playback.Resume();
            Refresh();
            return;
        }

        booth.HoldDeck(which, false);

        // A deck cannot be heard through a stopped engine. Letting a platter go is a request to hear
        // it.
        if (_playback.IsPaused)
            _playback.Resume();
        Refresh();
    }

    private bool TryStartAudition(Dj booth)
    {
        if (!_wantAudition || booth.IsAuditioning)
            return false;
        if (booth.PendingPlan is null || booth.CuedTrack is null)
            return false;
        if (!booth.AuditionCued(true))
            return false;

        _wantAudition = false;
        if (_playback.IsPaused)
            _playback.Resume();
        return true;
    }

    /// <summary>One deck's cue key. The live deck's needle goes eight bars before the changeover. That
    /// is far enough back for the whole sequence to run as it would for real. The next record is cued
    /// about twelve seconds ahead, and the tempo is ridden across before anything fades. The cued
    /// deck's needle goes back to its cue-in point.</summary>
    /// <summary>
    /// Turns down the record cued for the changeover, and has the set pick another. Like any other
    /// opinion, it is written to the journal. The journal records which record was passed over, and on
    /// which pair. That is the main value of this key.
    /// </summary>
    private void RejectCued()
    {
        // Whatever the second deck shows is the planner's answer. This is true whether or not a deck
        // holds the record yet. See Dj.Reject.
        if (_dj.Booth is not { } booth || (booth.CuedTrack ?? _preview?.Incoming) is not { } turned)
            return;

        var planned = _preview;
        if (!booth.Reject(turned))
            return;

        Preview(now: true);
        _journal.Edited(
            booth.LiveTrack, booth.PlayingBpm, booth.LivePositionSeconds,
            planned, _preview, Asked(), [$"turned down {turned.Display}"]);

        _builtFor = "";
        Refresh();
    }

    /// <summary>Whether a needle is standing on a mark, close enough that saying so is true. The
    /// tolerance is a tenth of a second. That is less than a beat at any tempo. It is also wide enough
    /// that a deck paused by hand on the point still reads as being on it.</summary>
    private static bool At(double needle, double mark) => Math.Abs(needle - mark) < 0.1;

    /// <summary>
    /// The cue key. It puts this deck's needle on its own end of the changeover, and holds it there.
    /// That is the mix point on the record playing, or the cue point on the record coming in.
    ///
    /// <para>This is a jump and hold, as on a real deck. The needle goes to the mark and waits there
    /// until the key is released. The key lights while the needle stands there (see
    /// <see cref="MixDeckView.ShowEdits"/>). So the row shows where the deck is, not only where it can
    /// be sent.</para>
    /// </summary>
    private void CueDeck(DjDeck which)
    {
        if (_dj.Booth is not { HandsOn: true } booth || _preview is not { } plan)
            return;

        if (which == DjDeck.Live)
        {
            _dj.SeekTo(plan.StartAtSeconds);
        }
        else
        {
            // The cue key overrules wherever a hand had put the needle.
            _cuedNeedle = null;
            booth.SeekDeck(DjDeck.Cued, plan.CueInSeconds);
        }

        booth.HoldDeck(which, true);
        Refresh();
    }

    // ── frames and ticks ──────────────────────────────────────────────────────

    /// <summary>
    /// One frame: where the needles are, and nothing else. Anything expensive is done on the slow
    /// timer. The one thing that must never happen here is the interface asking the audio thread a
    /// question it has to wait for an answer to.
    ///
    /// <para>The positions are corrected for output latency. A deck counts the frames it has PRODUCED.
    /// Between that point and the speakers are the decode-ahead ring and the sound device's own queue.
    /// Together these can hold most of a second on a sound card, and several seconds on a Cast
    /// receiver. Without the correction, every bar line sits about a bar early. This window exists to
    /// judge whether a changeover lands on the beat. So an uncorrected picture would disagree with what
    /// the listener hears, and the listener is right. The lag is scaled by each deck's pull. It is a
    /// length of time in OUTPUT, but the grids are measured in each record's own seconds.</para>
    /// </summary>
    private void OnFrame(object? sender, object e)
    {
        if (_dj.Booth is not { } booth)
            return;

        double lag = _playback.OutputLagSeconds;

        // The needle position is used, not the file position. During a roll, a rolled record's file
        // position stops dead, because a loop is a copy of a stretch the deck has already played past.
        // If the picture used the file position, both needles would stop for eight bars in the middle
        // of the sound the room hears. See Deck.NeedleSeconds. The lag correction covers audio that has
        // been handed to the device but not yet heard. A deck that is not running has no audio in
        // flight, so there is nothing to correct. Otherwise the reading drifts by a few milliseconds as
        // the buffer drains. On the beat alignment strip, which shows four seconds across, that is a
        // pixel or two of grid shaking under a needle that has not moved.
        double liveLag = booth.IsDeckPlaying(DjDeck.Live) ? lag * booth.LiveTempoRatio : 0;
        double liveAt = Math.Max(0, booth.LiveNeedleSeconds - liveLag);
        _deckA.Timeline.SetPlayhead(liveAt);

        // This is the speed the deck is really running at, not the speed a plan says it should run at.
        // The plan is the wrong source for two reasons. First, it is null as soon as the changeover
        // starts. That would set the ratio back to 1 for the whole stretch this picture is for. Second,
        // the plan states an intent, even before that. The deck is moved onto its pull over several
        // bars. So during that move, the plan's number and the deck's number are different.
        double cuedRatio = booth.CuedTempoRatio;
        double cuedLag = booth.IsDeckPlaying(DjDeck.Cued) ? lag * cuedRatio : 0;
        double cuedAt = Math.Max(0, booth.CuedNeedleSeconds - cuedLag);
        _deckB.Timeline.SetPlayhead(cuedAt);

        // The close-up, from the same two needles and the same lag correction. It is driven from here,
        // not from the slow tick, for the same reason as the playheads above. Eight readings a second
        // would make a picture measured in milliseconds stutter.
        //
        // It is shown only when the cued needle is really moving. A changeover starts by letting the
        // record go and holding it for up to a bar. During that hold, the deck reports itself as
        // playing while it emits nothing, and its grid sits on the cue point. So the strip would draw a
        // moving record against a parked one and show the distance between them as a gap. That gap can
        // read up to half a beat, at the exact moment when somebody is checking whether the two records
        // met. See Dj.IsDeckTurning.
        _align.Show(
            booth.LiveTrack, _peaks.For(booth.LiveTrack), liveAt, booth.LiveTempoRatio,
            booth.CuedTrack, _peaks.For(booth.CuedTrack),
            booth.IsDeckTurning(DjDeck.Cued) ? cuedAt : -1, cuedRatio);

        // This is also what was done to those two numbers on the way here. It lets a gap on the strip
        // be blamed on the mix or on the drawing, without guessing.
        _align.ShowWorkings(
            lag,
            booth.LiveNeedleSeconds,
            booth.IsDeckTurning(DjDeck.Cued) ? booth.CuedNeedleSeconds : -1);

        // The mixer. The values come from the decks themselves, not from the plan. What is drawn has to
        // be what the set is running. Otherwise the picture of the bass swap would show the intention
        // behind the swap, not the swap. It is driven from the render loop with everything else here. A
        // fader that crosses over sixteen bars and is redrawn eight times a second would look as if it
        // jumps.
        _mixerA.Show(booth.LiveTrack is not null, booth.LiveFader,
            booth.LiveEq.LowDb, booth.LiveEq.MidDb, booth.LiveEq.HighDb);
        _mixerB.Show(booth.CuedTrack is not null, booth.CuedFader,
            booth.CuedEq.LowDb, booth.CuedEq.MidDb, booth.CuedEq.HighDb);
    }

    /// <summary>
    /// Gives the booth what the boxes on this page are showing. This happens as soon as there is a
    /// booth to give it to, and again each time there is a different booth.
    ///
    /// <para>The booth must be given the page's settings at once. Otherwise the marks drawn on the
    /// record could come from one set of rules, while the changeover that the set makes comes from
    /// another. A mix start could then be drawn at the wrong place, under a line that says "no
    /// changeover cued". Switching the mode away and back works around this, because that calls
    /// <see cref="CommitAsk"/>. CommitAsk is the only other place where the booth is told
    /// anything.</para>
    ///
    /// <para>This is not journalled. Nobody asked for anything. This is the page and the set agreeing
    /// on what they are both already showing.</para>
    /// </summary>
    private void HandOverTheAsk(Dj booth)
    {
        if (ReferenceEquals(_told, booth))
            return;

        _told = booth;
        booth.Overrides = Asked();
        booth.Replan();
        Preview(now: true);
        _builtFor = "";
    }

    private void Refresh()
    {
        var booth = _dj.Booth;
        if (booth is not null)
            HandOverTheAsk(booth);

        // One reading, taken at the top, for everything below. Every question the page asks about the
        // booth is answered from it: which of the four states the booth is in, why no changeover is
        // made where one is expected, and which controls would actually reach the set.
        _look = Look();

        if (booth is null)
        {
            var reading = _dj.ReadingAhead;
            _heading.Text = reading.Total > 0 ? "Analysing" : "No set running";
            _state.Text = reading.Total > 0
                ? $"{reading.Read} of {reading.Total} read. {Waiting}"
                : "Start a DJ set to see it here.";
            _deckA.Timeline.Show(null, null, null, null);
            _deckB.Timeline.Show(null, null, null, null);
            _deckA.ShowHeader(null, 0, 0, 0, 0, "", MixDeckView.Mood.Idle);
            _deckB.ShowHeader(null, 0, 0, 0, 0, "", MixDeckView.Mood.Idle);
            _mixerA.Show(loaded: false, 0, 0, 0, 0);
            _mixerB.Show(loaded: false, 0, 0, 0, 0);

            // There is nothing to ask for either. There is no set to plan a changeover for, so each of
            // these would be an opinion about nothing.
            _deckA.ShowEdits(null, canEditReading: false, canEditMix: false, hasMixMarks: false, canCue: false);
            _deckB.ShowEdits(null, canEditReading: false, canEditMix: false, hasMixMarks: false, canCue: false);
            _deckA.ShowGrid(null, editable: false);
            _deckB.ShowGrid(null, editable: false);
            _deckA.Timeline.CanScrub = false;
            _deckB.Timeline.CanScrub = false;
            _warning.Visibility = Visibility.Collapsed;
            _deckA.ShowTransport(playing: false, enabled: false, "Play this track", "Pause this track");
            _deckB.ShowTransport(playing: false, enabled: false, "Play from the cue point", "Stop");
            MixDeckView.Reach(_skip!, false);
            _fadeBars.IsEnabled = _mode.IsEnabled = _style.IsEnabled = false;
            _pull.IsEnabled = _drift.IsEnabled = _noise.IsEnabled = false;
            _arc.IsEditable = false;

            SetFraming(false);
            return;
        }

        var look = _look;
        bool paused = look.Paused;

        // Whose the decks are. This is asked once. While the set is running, the decks belong to the
        // DJ. The DJ watches for the outro on one deck and cues the next record on the other. So the
        // deck keys show what each deck is doing, and they take no presses. When the set is paused,
        // both decks belong to the hand, and every key on them works. A paused set is not a stopped
        // engine. Its two platters are stopped, but the engine keeps turning, so the booth can still be
        // asked for things. See RunSet.
        bool mine = look.HandsOnTheDecks;
        bool running = !mine && !paused;

        // The frame clock runs only while a needle is really moving. When the needles stand still, they
        // are placed once here instead. The slow tick is enough for a picture where nothing moves, and
        // it costs the compositor nothing between ticks.
        bool moving = look.LiveTurning || look.CuedTurning;
        SetFraming(moving);
        if (!moving)
            OnFrame(this, EventArgs.Empty);

        // Whether the changeover can still be changed. The test is not just "a fade has not started".
        // While a decoder opens on the free deck, Dj.Replan refuses outright. So the test must also
        // cover that time. Otherwise every box on the strip takes changes that never reach the set, and
        // the set keeps playing the changeover it had already planned. See BoothView.CanEditTheMix.
        bool editable = look.CanEditTheMix;
        _outgoing = booth.LiveTrack;
        double bpm = booth.PlayingBpm;

        double lag = _playback.OutputLagSeconds * booth.LiveTempoRatio;
        double position = Math.Max(0, booth.LivePositionSeconds - lag);

        // The changeover, from the booth. One question, one answer. See Dj.WouldPlan.
        Preview();

        _incoming = booth.CuedTrack ?? _preview?.Incoming;

        // The record coming in belongs ON its platter for as long as the decks are under the hand's
        // control.
        //
        // A set puts it there about twelve seconds before the changeover, and not earlier. That is
        // right for a set, but it is useless for a workbench. Until then, the second deck shows a
        // choice, not a held record. A choice has no needle to move, no cue key to press and nothing to
        // play. This is asked once per record. So a crate with nothing left in it is not asked the same
        // question eight times a second.
        if (mine && booth.PendingPlan is null && booth.CuedTrack is null &&
            _preview?.Incoming.Path is { Length: > 0 } wanted && _cueAskedFor != wanted)
        {
            _cueAskedFor = wanted;
            booth.CueNext();
        }
        else if (booth.PendingPlan is not null)
        {
            _cueAskedFor = "";
        }

        // The needle also goes to the place a hand put it before there was a record to put it on. This
        // is asked once, and then the page waits. The seek reopens a decoder. Asking again on every
        // tick would replace the seek that is about to land with an identical one.
        if (_cuedNeedle is { } want && booth.CuedTrack is not null)
        {
            if (!want.Sent)
                _cuedNeedle = (want.At, Sent: booth.SeekDeck(DjDeck.Cued, want.At));
            else if (Math.Abs(booth.CuedPositionSeconds - want.At) < 0.25)
                _cuedNeedle = null;
        }

        // An audition asked for before the record has arrived starts as soon as it has. It waits for
        // the needle to arrive, or the record would play a second from somewhere else first.
        if (_cuedNeedle is null)
            TryStartAudition(booth);

        Remember(_outgoing);
        Remember(_incoming);

        // Two different states can look the same on the deck. One is a set that has ended. The other is
        // a set that is built and holding, while the model reads the two records that its first
        // changeover uses. The second state can last a minute of an empty workbench on a cold library.
        // Saying nothing about it makes the lab look broken.
        var (readSoFar, waitingOn) = _dj.ReadingAhead;
        _heading.Text = _outgoing is not null
            // An arrow between the two records promises that one hands over to the other. There are
            // several ways that this can stop being true while both records are still on screen. The
            // needle can be put past the mix point. The crate can be empty. The pair can be put back.
            // In those cases the heading names the record that is playing, and it does not promise a
            // hand-over.
            ? look.CanEditTheMix || look.State is BoothState.HandingOver
                ? $"{_outgoing.Display}  ->  {_incoming?.Display ?? "(nothing left)"}"
                : $"{_outgoing.Display}   (no changeover out of this record)"
            : waitingOn > 0
                ? $"Analysing ({readSoFar} of {waitingOn} tracks read)"
                : "No track loaded";

        // The banner shows what is off, and who turned it off. It is loud on purpose. See _warning.
        ShowWarning(look, waitingOn > 0);

        // While the wait is on, the line shows the wait. Nothing has been played yet. So the position,
        // the tempo and the lag are only zeros that look like readings. The rest of the page already
        // says there is nothing here.
        _state.Text = waitingOn > 0 ? Waiting : string.Join("   ", new[]
        {
            // Which of the four things is happening, named. A record playing while the DJ works towards
            // its changeover looks the same as a record playing while the DJ has given up on one, on a
            // screen that only says "a record is playing". The difference between those two is the
            // whole point of this window. It is shown here, and the reason is shown too. See
            // BoothView.Headline.
            look.Headline,

            // The last thing the set turned down, if it is still worth saying.
            _refused is { } no && DateTime.UtcNow - no.At < RefusalShowsFor ? no.What : "",

            $"{position:F1}s of {_outgoing?.DurationSeconds ?? 0:F0}s",
            $"{bpm:F1} BPM",
            $"output lag {lag * 1000:F0} ms" +
                (_preview is { BarSeconds: > 0 } p ? $" ({lag / p.BarSeconds:F2} bars)" : ""),
            booth.IsChangingOver ? $"changeover {booth.ChangeoverProgress:P0}" : "",

            // Whether a changeover is held ready, and where. This is the one thing that changes without
            // anybody touching it. A plan is cued about twelve seconds ahead. It is dropped when the
            // needle is moved past its start. It is made again from wherever the needle lands. Without
            // this line, the user would not see any of these changes. A mix would just stop being about
            // to happen, and the only way to notice would be that it did not happen.
            booth.IsChangingOver ? ""
                : booth.PendingPlan is { } ready
                    ? $"changeover cued at {MixInputs.Sec(ready.StartAtSeconds)}"

                    // The position where the changeover will be. "No changeover cued" on its own gives
                    // no sense of the wait. A plan is held ready twelve seconds out, so most of a
                    // record reads as "no changeover cued". Nothing says whether the wait is thirty
                    // seconds or three minutes. The position is also the only number that the marks
                    // drawn on the record can be checked against. That is the reason it is shown.
                    : look.ArmsAtSeconds > 0
                        ? $"no changeover cued (the DJ arms at {MixInputs.Sec(look.ArmsAtSeconds)})"
                        : _outgoing is not null && _incoming is not null ? "no changeover cued" : "",

            // The needle has gone past the point where the set was going to hand over. The set picks up
            // again from wherever the needle is, so this is not the end of the mix. But a mix mark
            // drawn behind the playhead means that the changeover was missed. Drawing that mark without
            // saying so would suggest a changeover that is still ahead.
            look.NeedleIsPastTheArmPoint ? "needle is past the arm point" : "",

            booth.IsMatchingIn ? "tempo ride" : "",
            booth.IsAuditioning ? "auditioning" : "",
            _outgoing?.Analysis?.IsEdited == true ? "EDITED" : "",
        }.Where(s => s.Length > 0));

        _journalText.Text = _journal.Path is null
            ? "Journal: not writable"
            : $"Journal: {_journal.Path} ({_journal.Count} lines)";

        // The decks. The cue point drawn on the record coming in is the plan's, not the analyser's.
        _deckA.Timeline.Show(_outgoing, _preview, _peaks.For(_outgoing), cueInSeconds: null);
        _deckB.Timeline.Show(_incoming, null, _peaks.For(_incoming), cueInSeconds: _preview?.CueInSeconds);

        // Whether a click on either waveform moves that deck's needle. This is the booth's answer, not
        // a guess. The live deck refuses clicks for the whole of a changeover. The cued deck's needle
        // belongs to the plan that cued it, unless a hand has that deck. Otherwise the click would move
        // the playhead to the new place, and then it would snap back a frame later. The second deck
        // also takes a needle put down on a record that no platter is holding yet. The place is
        // remembered, and the record lands with its needle already there (see WireDeck). This works
        // only while the decks are under the DJ's control. That is the only state in which the
        // remembered seek can be delivered. A click that is recorded but never acted on would be
        // misleading.
        _deckA.Timeline.CanScrub = look.CanScrubTheLiveDeck;
        _deckB.Timeline.CanScrub =
            look.CanScrubTheCuedDeck || (look.CanDriveTheDecks && _incoming is not null);

        double meetAt = _preview?.MixBpm ?? 0;
        double apart = _preview?.TempoMismatch ?? 0;
        // Each panel's state comes from the same reading that the rest of the page uses. So a deck that
        // reads "Playing" has a needle that is really moving. It is not a deck that has only been let
        // go. During the hold at the bar line, those two are different. See BoothView.LiveTurning.
        _deckA.ShowHeader(_outgoing, booth.PlayingBpm, booth.LiveTempoRatio, meetAt, apart, DeckState(booth, DjDeck.Live, paused),
                          look.LiveTurning ? MixDeckView.Mood.Playing : MixDeckView.Mood.Paused);
        _deckB.ShowHeader(_incoming, CuedTempo(booth), booth.CuedTempoRatio, meetAt, apart, DeckState(booth, DjDeck.Cued, paused),
                          _incoming is null ? MixDeckView.Mood.Idle
                          : !_incoming.IsAnalyzed ? MixDeckView.Mood.Analysing
                          : look.CuedTurning ? MixDeckView.Mood.Playing
                          : MixDeckView.Mood.Cued);
        // Each deck's own transport. A key is lit when that deck's needle is really moving. It is not
        // lit just because the deck was let go. A deck that counts down to its bar line reports itself
        // as running while it emits no sound. A key that is lit over a needle standing still would show
        // a picture that disagrees with what the room hears. See BoothView.LiveTurning.
        _deckA.ShowTransport(
            playing: look.LiveTurning,
            enabled: _outgoing is not null && look.CanDriveTheDecks,
            "Play this track", "Pause this track");
        _deckB.ShowTransport(
            playing: look.CuedTurning,
            enabled: look.CanDriveTheDecks && (_incoming is not null || booth.IsNextReady),
            "Play this track", "Pause this track");

        // The set's own pair, lit on the state the set is in rather than on what a press would do.
        if (_setPlay is not null && _setPause is not null)
        {
            MixDeckView.Light(_setPlay, on: running);
            MixDeckView.Light(_setPause, on: !running);

            // These keys are also shut for the whole of a changeover. That is the one time when neither
            // answer is available. A fade moves both decks together. Dj.TakeTheDecks turns both
            // directions down. A key that can be pressed but does nothing would say that the workbench
            // has stopped listening.
            MixDeckView.Reach(_setPlay, look.CanTakeOrGiveTheDecks);
            MixDeckView.Reach(_setPause, look.CanTakeOrGiveTheDecks);

            ToolTipService.SetToolTip(_setPlay,
                "Play the set. The DJ takes both decks back and the record coming in goes to its cue point.");
            ToolTipService.SetToolTip(_setPause, "Pause the set. Both decks are yours while it is paused.");
        }

        MixDeckView.Light(_loop, on: _loop.IsChecked == true);
        _deckA.ShowGrid(_outgoing, look.CanEditTheReading);
        _deckB.ShowGrid(_incoming, look.CanEditTheReading);

        // Which marks each deck can be asked to move. The changeover's own marks sit on whichever deck
        // the plan put them on. The start and end are on the record playing. The cue is on the record
        // coming in. So a deck with neither has no mix marks to offer.
        _deckA.ShowEdits(
            _outgoing, look.CanEditTheReading, editable,
            hasMixMarks: _preview is not null,

            // A cue key is a transport key, because it moves the needle. It is offered only while the
            // decks are under the DJ's control, for the same reason as the play keys.
            canCue: _preview is not null && look.CanDriveTheDecks,

            // Lit while the needle stands on the point that this deck's cue key sends it to. That is
            // the mix point on the record playing, or the cue point on the record coming in. It shows
            // where the deck IS, not only where it can be sent.
            atCue: _preview is { } aim && At(position, aim.StartAtSeconds));
        _deckB.ShowEdits(
            _incoming, look.CanEditTheReading, editable,
            hasMixMarks: _preview is not null,
            // The cue key is offered only once a record is really on this platter. Until the DJ cues
            // it, or the play key asks for it early, the second deck shows a choice. There is no needle
            // on a choice to send anywhere.
            canCue: _preview is not null && look.CanDriveTheDecks && look.CuedOnAPlatter,

            // This is offered only while the record is still a choice. Once the changeover has started,
            // the record is only frames on the audio thread. Then there is nothing to turn down. This
            // is the same reason that every other control in the strip is shut while a changeover runs.
            //
            // The key works on the PLANNED record, not only the cued one. What this deck shows is the
            // planner's answer from the moment a set starts. The record reaches a real deck only about
            // twelve seconds before the changeover. A key that could only turn down the cued record
            // would do nothing for most of every record.
            canReject: _incoming is not null && look.CanRejectTheNextRecord,

            atCue: _preview is { } cued && At(booth.CuedPositionSeconds, cued.CueInSeconds));

        // The mix strip. The boxes show what the changeover has SETTLED on. That is a hand's ask where
        // there is one, and the plan's own choice where there is not. An empty box beside a decided
        // changeover would read as "nothing decided". The boxes are filled quietly. The page's writes
        // only update the display. Only a change made by a hand becomes an ask.
        _quiet = true;
        try
        {
            // This shows what the changeover SETTLED on, never what was asked for. The two often
            // differ, and they always differ in the same direction. The planner halves the count, for
            // drift and for the room left on the record. So a box that showed the ask would report a
            // sixteen-bar mix while the set plays four. This is the one number on this strip that the
            // picture beside it also shows, because the arc's bar lines are drawn from the same plan.
            // If the two disagreed, a fade length that nobody asked for would look like one that
            // somebody chose.
            int? bars = _preview is { } settled && MixTechniques.CountsBars(settled.Style)
                ? settled.FadeBars
                : null;
            _fadeBars.SelectedItem = bars is { } b && _fadeBars.Items.Contains(b) ? b : null;

            // A changeover with no bars to count says so in the box. The box is not emptied. An empty
            // dropdown beside a decided changeover reads as a question that nobody has answered.
            _fadeBars.PlaceholderText =
                _preview is { } shownStyle && !MixTechniques.CountsBars(shownStyle.Style) ? "None" : "";

            _mode.SelectedItem = _mode.Items.OfType<ModeItem>().FirstOrDefault(i => i.Mode == _askedMode);

            // In Manual, the box shows what a hand asked for. In either auto mode, it shows what the
            // planner chose, and it is disabled below. A control that changes its value on its own is
            // only alarming while it still looks like a control.
            var showing = _askedMode == MixMode.Manual ? _askedStyle : _preview?.Style;
            _style.SelectedItem = showing is { } onScreen
                ? _style.Items.OfType<StyleItem>().FirstOrDefault(i => i.Style == onScreen)
                : null;

            // Whether that readout is settled. A speculative plan can never come back as one of the two
            // rationed moves, however likely either move is. A name with nothing beside it would
            // promise a changeover that has not been made yet. The booth works out this answer itself,
            // from its own overrides and its own crate. So the picture and the plan are one thing. A
            // changeover that is not cued yet can still change its mind. That happens when a reading
            // lands or a record is turned down. When it does, the next frame draws the new one. What is
            // not settled is the PLATTER, not the choice. The second deck shows the record that this
            // changeover is with. That is why it is drawn one record ahead. A record reaches a real
            // deck only about twelve seconds before the mix. "Not cued" beside a deck with a record on
            // it would read as "nothing has been decided". That is the opposite of what it means.
            _autoPick.Text = _preview is not null && !_previewSettled ? "(not on the deck yet)" : "";
        }
        finally
        {
            _quiet = false;
        }

        // And what the strip can be asked for.
        //
        // A changeover that has started cannot be changed. It is frames on the audio thread, and
        // Dj.Replan refuses it. If the boxes stayed open, they would take changes and quietly drop
        // them. That would make the set look as if it is not listening. So the boxes are shut for the
        // duration instead.
        bool rolling = _preview is { Style: TransitionStyle.RollOut };
        MixDeckView.Reach(_skip!, look.CanSkip);
        // The fade length answers to a hand only where a hand picks the changeover. In either auto
        // mode, the box shows what the planner settled on, and it is shut for the duration. The style
        // box beside it is shut in the same way. It is also shut on a changeover with no bars to count,
        // whatever the mode, because there is nothing to ask for there.
        _fadeBars.IsEnabled =
            editable && _preview is { } counted && MixTechniques.CountsBars(counted.Style) &&
            _askedMode == MixMode.Manual;

        _mode.IsEnabled = editable && _preview is not null;

        // Only a hand's mode may change the style. In either auto mode, the box shows the planner's
        // answer. A control that can be turned, but whose value is written back over on the next tick,
        // takes changes and drops them.
        _style.IsEnabled = editable && _preview is not null && _askedMode == MixMode.Manual;

        // These three are policy. Policy is read when a changeover is worked out. They stay live
        // outside a changeover, because that is when they are set. This includes the noise setting. It
        // only affects a roll-out, so it has no effect on anything that is on screen when it is turned.
        _pull.IsEnabled = _drift.IsEnabled = _noise.IsEnabled = editable;

        // The rehearsal switch follows the service, not only its own presses. The setting is kept in
        // the service, so it survives one set ending and another starting.
        _loop.IsChecked = _dj.RehearseChangeover;

        ToolTipService.SetToolTip(_mode,
            "Who picks the changeover, and by what rule. " +
            "Auto: the planner does, at the outro and on the fours. " +
            "Auto X: the same changeovers, lined up on the record's own phrasing instead. " +
            "Manual: you do, out of the box beside this. " +
            "The next key rolls out in all three.");
        ToolTipService.SetToolTip(_style,
            "The changeover itself, in Manual. " +
            "Beat-matched blend: 8 bars at the outro, on the fours, with the new record's intro " +
            "playing underneath (this is what Auto makes). " +
            "Phrase blend: two whole phrases, started on the record's own phrase line, neither intro " +
            "nor outro mixed over (this is what Auto X makes). " +
            "Bass swap: three phrases, the bottom end changing hands across the middle one. " +
            "Double drop: the two records' drops landed together, the old one cut in front of it. " +
            "In Auto this is a readout of what the planner chose.");
        ToolTipService.SetToolTip(_fadeBars,
            "How long the changeover runs. This is what it settled on, not what was asked for: the " +
            "planner halves the count when the records would drift apart or the record runs out of " +
            "room. Yours to set in Manual. None means this changeover has no length in bars " +
            "(a cut, a blend, or a cut on the one).");
        ToolTipService.SetToolTip(_noise,
            "Chance of a rising noise over a roll out. Roll outs come from the next key.");

        _arc.Arc = _askedArc ?? _preview?.Arc ?? DefaultArc;
        _arc.Bars = _preview?.FadeBars ?? 0;
        // Not every changeover has levels that a hand can draw. See MixTechniques.FollowsAnArc. The
        // roll-out, the echo out and the rewind each have a choreography instead. An editor left live
        // over one of them would draw a curve that is then ignored.
        _arc.IsEditable = editable && _preview is { } shown && MixTechniques.FollowsAnArc(shown.Style);

        // A knob drag has come to rest. The ask is applied once, as one opinion.
        if (_knobDirty && DateTime.UtcNow - _knobTouchedAt > TimeSpan.FromMilliseconds(400))
        {
            _knobDirty = false;
            CommitAsk("policy knob turned");
        }

        Rebuild();
    }

    /// <summary>
    /// The banner. It is shown only where no changeover is coming. It says what happened, not a state
    /// name. The needle case is shown separately from the others. It is the only case that a person
    /// caused themselves, and the only one they can undo.
    /// </summary>
    private void ShowWarning(BoothView look, bool reading)
    {
        var (text, ink) = look.Why switch
        {
            NoMixBecause.ScrubbedPastTheMixPoint => (
                "No mix out of this record: you moved the needle past the mix point. "
                + "Drag it back before the mix point to put the changeover on again.",
                Color.FromArgb(0xFF, 0xF2, 0x8A, 0x4C)),

            NoMixBecause.TheMomentHasPassed => (
                "The changeover that was set up has been missed: the needle is past the point it "
                + "was to start from. The set will choose another from here.",
                Color.FromArgb(0xFF, 0xF2, 0x8A, 0x4C)),

            NoMixBecause.NothingLeftInTheCrate => (
                "Last record of the set. Nothing follows it, so there is no changeover to look at.",
                Color.FromArgb(0xFF, 0x8A, 0x9A, 0xB0)),

            NoMixBecause.HandsOnTheDecks => (
                "The decks are yours. The set is not watching for the outro and will not mix until "
                + "you press play on it.",
                Color.FromArgb(0xFF, 0x6A, 0xC8, 0xE8)),

            NoMixBecause.Auditioning => (
                "Auditioning the record coming in. The set is holding until you stop it.",
                Color.FromArgb(0xFF, 0x6A, 0xC8, 0xE8)),

            _ => ("", default(Color)),
        };

        // Nothing to say while the set is still being read. The workbench is empty for a reason it
        // already gives. A second notice under it would look like two faults.
        if (reading || text.Length == 0)
        {
            _warning.Visibility = Visibility.Collapsed;
            return;
        }

        _warningText.Text = text;
        _warningText.Foreground = new SolidColorBrush(ink);
        _warning.Background = new SolidColorBrush(Color.FromArgb(0x22, ink.R, ink.G, ink.B));
        _warning.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, ink.R, ink.G, ink.B));
        _warning.BorderThickness = new Thickness(1);
        _warning.Visibility = Visibility.Visible;
    }

    private string DeckState(Dj booth, DjDeck which, bool paused)
    {
        if (which == DjDeck.Live)
        {
            return booth.IsDeckHeld(DjDeck.Live) ? "Paused (deck)"
                 : paused ? "Paused"
                 : "Playing";
        }

        if (_incoming is null)
            return "No track";
        if (!_incoming.IsAnalyzed)
            return "Analysing";
        if (booth.IsAuditioning)
            return $"Auditioning {MixInputs.Sec(booth.CuedPositionSeconds)}";

        return booth.CuedTrack is null
            ? "Not cued"
            : $"Cued {MixInputs.Sec(booth.CuedPositionSeconds)}";
    }

    /// <summary>
    /// The cued deck's tempo. This is the tempo that the deck runs at once it has been cued. Before
    /// that, it is the tempo that the record says it has. The number lets the user compare the cued
    /// deck with the live deck while the next record is still being chosen.
    ///
    /// <para>It is counted at the live deck's octave. That is the whole point of showing it. A deck
    /// reports the tempo it is filed at, multiplied by the pull it is under. For example, a record
    /// filed at 87 that sounds like 174 would show 87 beside the other deck's 174 for the whole
    /// changeover. The mix itself is correct, because <see cref="TransitionPlanner"/> matched the
    /// octave before it worked out the pull. The number on screen must be the tempo the deck is
    /// contributing, not the number in the table.</para>
    /// </summary>
    private double CuedTempo(Dj booth)
    {
        double bpm = booth.CuedTrack is not null ? booth.CuedBpm : _incoming?.Analysis?.Bpm ?? 0;
        double live = booth.PlayingBpm;

        return bpm > 0 && live > 0 ? TempoEstimator.MatchLevel(bpm, live) : bpm;
    }

    // ── the preview and the asks ──────────────────────────────────────────────

    /// <summary>What the changeover out of the record playing would be, worked out from where the
    /// needle is now. It is exactly the call the DJ makes for real, so what is drawn is what will
    /// run.</summary>
    private void Preview(bool now = false)
    {
        _previewedAt = DateTime.UtcNow;

        // This is the booth's answer, with no arithmetic of this page's own. See Dj.WouldPlan. A window
        // that works out its own changeover draws a plan that nobody is going to play. The two could
        // come apart in many ways.
        bool settled = false;
        var plan = _dj.Booth?.WouldPlan(out settled);

        // A mark being dragged is drawn where the hand holds it, on the booth's own plan. It is the
        // same plan with one mark moved. That is what a drag is. It is not a second changeover worked
        // out from the ask, and this page does not do that. When the hand lets go, the drawing stops
        // being an overlay. The ask goes to the booth (see CommitAsk), and it comes back as the plan.
        if (plan is not null && _dragPlan is not null)
        {
            settled = false;
            if (_askedStart is { } start)
                plan = plan with { StartAtSeconds = start };
            if (_askedCue is { } cue)
                plan = plan with { CueInSeconds = cue };
            if (_askedBars is { } bars && plan.BarSeconds > 0)
                plan = plan with { FadeBars = bars, FadeSeconds = bars * plan.BarSeconds };
        }

        _preview = plan;
        _previewSettled = _preview is not null && settled;
    }

    /// <summary>Everything the hands are currently asking for, or null when they agree with the planner
    /// entirely.</summary>
    private MixOverrides? Asked()
    {
        var policy = new MixPolicy
        {
            MaxTempoPull = _pull.Value / 100,
            MaxDriftSeconds = _drift.Value / 1000,
            NoiseChance = _noise.Value / 100,
            Techniques = _askedMode == MixMode.Suit,

            // The phrase layout is left off here. It is the STYLE that asks for the phrase layout now.
            // See TransitionPlanner, which reads the named style before it reads this. If the layout
            // were on for the whole of Manual, a hand could not ask for the ordinary changeover at all.
            // Every style in the box would be rebuilt on the phrase, including the one whose whole
            // definition is that it is not. Auto X is Auto with the phrasing, and nothing else. The
            // phrase layout also makes the drop swap and the bass swap possible. Both are switched off
            // here. Auto makes a beat-matched blend, a fade or a cut. A mode described as "the same, on
            // the phrase" would be wrong if it quietly handed over on a double drop.
            PhraseBlend = _askedMode == MixMode.Phrase,

            PhraseMoves = _askedMode != MixMode.Phrase,
        };

        var asked = new MixOverrides
        {
            FadeBars = _askedBars,
            Style = _askedStyle,
            StartAtSeconds = _askedStart,
            CueInSeconds = _askedCue,
            Arc = _askedArc,
            Policy = policy.IsDefault ? null : policy,
        };
        return asked.IsEmpty ? null : asked;
    }

    /// <summary>Applies what the hands asked for to the set. The next plan is worked out from it, then
    /// the pair is written down.</summary>
    private void CommitAsk(string what)
    {
        var booth = _dj.Booth;
        if (booth is null)
            return;

        var planned = _preview;
        booth.Overrides = Asked();

        // Whether the ask reached the set. Replan refuses two states. One is a changeover that has
        // started. The other is a free deck that the cue thread still has a decoder opening on. The
        // refusal must be checked here, because otherwise it is lost. The strip would redraw from the
        // new ask, and the journal would record the edit as made. But the changeover that runs would be
        // the one planned before the edit. So when a plan exists and it survives, the ask did not land,
        // and the page says so.
        bool hadOne = booth.PendingPlan is not null;
        bool landed = booth.Replan() || !hadOne;

        Preview(now: true);
        if (!landed)
        {
            _refused = ("That did not land: the next record is being cued", DateTime.UtcNow);
            Refresh();
            return;
        }

        if (planned is not null || _preview is not null)
            _journal.Edited(
                booth.LiveTrack, booth.PlayingBpm, booth.LivePositionSeconds,
                planned, _preview, Asked(), [what]);

        _builtFor = "";
        Refresh();
    }

    /// <summary>Clears every ask, so that the planner's own answer is used again. The controls are
    /// moved quietly. A slider put back to its default is moved by this page, not by the DJ. It must
    /// not be journalled as six separate opinions.</summary>
    private void ResetAsk()
    {
        _askedStart = _askedCue = null;
        _askedBars = null;
        _askedStyle = null;
        _askedArc = null;
        _askedMode = MixMode.Usual;
        _knobDirty = false;

        _quiet = true;
        try
        {
            _fadeBars.SelectedItem = null;
            _pull.Value = TransitionPlanner.MaxTempoPull * 100;
            _drift.Value = TransitionPlanner.MaxDriftSeconds * 1000;
            _noise.Value = TransitionPlanner.NoiseChance * 100;
        }
        finally
        {
            _quiet = false;
        }

        CommitAsk("everything back to the planner");
    }

    // ── the edits ─────────────────────────────────────────────────────────────

    private MixTrack? TrackOn(DjDeck which) => which == DjDeck.Live ? _outgoing : _incoming;

    /// <summary>Whether an edit made now would reach the set. This is the booth's own answer, taken
    /// fresh. See <see cref="BoothView.CanEditTheMix"/>. A changeover that has started refuses edits.
    /// So does a free deck that has a decoder opening on it. This check covers both cases.</summary>
    private bool CanEdit => Look().CanEditTheMix;

    /// <summary>Whether a correction to a RECORD's own reading would reach the set. This is a wider
    /// question than <see cref="CanEdit"/>. See <see cref="BoothView.CanEditTheReading"/>.</summary>
    private bool CanEditReading => Look().CanEditTheReading;

    /// <summary>
    /// A handle is being dragged. The grid and cue handles edit the RECORD's reading. The mix handles
    /// edit THIS CHANGEOVER's plan. Both show on the picture at once, but nothing is written down until
    /// the hand lets go.
    /// </summary>
    private void Move(DjDeck which, MixLabHandle handle, double seconds)
    {
        // One reading for the whole gesture. The two permissions are checked separately. The mix marks
        // need a changeover to be coming. The grid marks only need the set to be listening.
        var look = Look();
        if (!look.CanEditTheReading)
            return;

        // The changeover's own marks: asks, not readings.
        switch (handle)
        {
            case MixLabHandle.MixStart or MixLabHandle.MixEnd or MixLabHandle.CueIn
                when !look.CanEditTheMix:
                return;

            case MixLabHandle.MixStart:
                _dragPlan ??= _preview;
                _askedStart = seconds;
                Preview(now: true);
                Refresh();
                return;

            case MixLabHandle.MixEnd when _preview is { BarSeconds: > 0 } plan:
                _dragPlan ??= _preview;
                _askedBars = Math.Clamp(
                    (int)Math.Round((seconds - plan.StartAtSeconds) / plan.BarSeconds), 1, 64);
                Preview(now: true);
                Refresh();
                return;

            case MixLabHandle.CueIn:
                _dragPlan ??= _preview;
                _askedCue = seconds;
                Preview(now: true);
                Refresh();
                return;
        }

        if (TrackOn(which)?.Analysis is not { } before)
            return;

        _dragFrom ??= before;
        _dragPlan ??= _preview;

        var after = handle switch
        {
            MixLabHandle.Downbeat => GridEdit.DownbeatAt(before, seconds),
            MixLabHandle.MixIn => GridEdit.WithCues(before, introEndSeconds: seconds),
            MixLabHandle.MixOut => GridEdit.WithCues(before, outroStartSeconds: seconds),
            _ => before,
        };

        // There is no replan for each pointer move. A replan throws the cued record back in the crate.
        // The next audio block would then start a decoder for each pixel of the drag. The reading is
        // applied to the set at once. The plan catches up when the hand lets go.
        Apply(which, before, after, journal: false, replan: false);
    }

    /// <summary>The hand has let go. The plan is worked out once, from where everything landed. One
    /// line is written for the whole drag.</summary>
    private void Settle(DjDeck which)
    {
        var from = _dragFrom;
        var planned = _dragPlan;
        _dragFrom = null;
        _dragPlan = null;

        if (_dj.Booth is not { } booth)
            return;

        // A mix-handle drag has no reading diff; the ask itself is the edit.
        if (from is null)
        {
            if (planned is not null)
                CommitAsk("mix marks moved");
            return;
        }

        // The replan that the drag skipped on purpose is done once here, and this checks whether it
        // landed. A drag that is let go while a decoder is opening on the free deck is refused by the
        // set. The reading still changed underneath a plan that was made from the old reading.
        bool hadOne = booth.PendingPlan is not null;
        bool landed = booth.Replan() || !hadOne;
        Preview(now: true);
        if (!landed)
            _refused = ("The grid changed but the changeover did not: the next record is being cued",
                        DateTime.UtcNow);

        var now = TrackOn(which)?.Analysis;
        if (now is null)
            return;

        var changes = GridEdit.Diff(from, now);
        if (changes.Count == 0)
            return;

        _journal.Edited(
            booth.LiveTrack, booth.PlayingBpm, booth.LivePositionSeconds, planned, _preview,
            Asked(), changes);

        _builtFor = "";
        Refresh();
    }

    /// <summary>
    /// A beat was clicked and named as the one. The whole grid takes its phase from that beat.
    ///
    /// <para>There is another way to do this. Dragging any bar line slides the line until it sits where
    /// the ear says it should be. That is the wrong tool for the most common mistake the tracker makes.
    /// The tracker is usually not a few milliseconds out, but a beat or two out. Sliding the grid by a
    /// whole beat by eye is fiddly, and it lands somewhere near. Naming the beat lands exactly, because
    /// the beat is the number that is used.</para>
    /// </summary>
    private void NameTheOne(DjDeck which, double seconds)
    {
        if (CanEditReading && TrackOn(which)?.Analysis is { } before)
            Apply(which, before, GridEdit.DownbeatAt(before, seconds));
    }

    private void Regrid(DjDeck which, double bpm, int perBar)
    {
        if (!CanEditReading || TrackOn(which)?.Analysis is not { } before)
            return;
        if (Math.Abs(bpm - before.Bpm) < 0.005 && perBar == before.BeatsPerBar)
            return;

        Apply(which, before, GridEdit.Regrid(before, bpm, GridEdit.FirstDownbeatSeconds(before), perBar));
    }

    private void GridTouch(DjDeck which, Func<TrackAnalysis, TrackAnalysis> edit)
    {
        if (CanEditReading && TrackOn(which)?.Analysis is { } before)
            Apply(which, before, edit(before));
    }

    /// <summary>
    /// Puts an edited reading into the set, and writes down what changed unless a drag is going to
    /// write it down itself when the hand lets go.
    /// </summary>
    private void Apply(
        DjDeck which, TrackAnalysis before, TrackAnalysis after, bool journal = true, bool replan = true)
    {
        if (GridEdit.Diff(before, after).Count == 0)
            return;

        var booth = _dj.Booth;
        var planned = _preview;
        if (!_dj.ApplyEdit(after, replan))
            return;

        Preview(now: true);

        if (journal)
            _journal.Edited(
                booth?.LiveTrack, booth?.PlayingBpm ?? 0, booth?.LivePositionSeconds ?? 0,
                planned, _preview, Asked(),
                GridEdit.Diff(before, after));

        (which == DjDeck.Live ? _deckA : _deckB).ForgetShownGrid();
        _builtFor = "";
        Refresh();
    }

    /// <summary>Back to what the analyser said, for every record this session has touched.</summary>
    private void Revert()
    {
        foreach (var measured in _measured.Values)
            _dj.ApplyEdit(measured);

        _deckA.ForgetShownGrid();
        _deckB.ForgetShownGrid();
        ResetAsk();
    }

    /// <summary>Keeps the reading a record arrived with, once, so an edit can always be undone and
    /// always measured against what a machine actually said.</summary>
    private void Remember(MixTrack? track)
    {
        if (track?.Analysis is { IsEdited: false } analysis)
            _measured.TryAdd(track.Path, analysis);
    }

    // ── the tables ────────────────────────────────────────────────────────────

    /// <summary>Rebuilds the tables. This happens only when what they show has really changed. It never
    /// happens in the middle of a drag, because the timelines already redraw on every pointer move.
    /// Fifty rows of text per pointer event would be far too slow.</summary>
    private void Rebuild()
    {
        if (_dragFrom is not null || _dragPlan is not null)
            return;

        // The settled flag is part of the key. A guess and the changeover it turns out to be often have
        // the same numbers. That is the point of previewing one. Without the flag, the readout would
        // keep saying "pending" after the set had decided.
        string key = $"{_outgoing?.Path}|{_incoming?.Path}|{_preview?.FadeBars}|{_preview?.Style}|" +
                     $"{_preview?.StartAtSeconds:F2}|{_preview?.CueInSeconds:F2}|{_previewSettled}|" +
                     $"{_outgoing?.Analysis?.IsEdited}|{_incoming?.Analysis?.IsEdited}";
        if (key == _builtFor)
            return;
        _builtFor = key;

        BuildPlan();
    }

    /// <summary>
    /// The changeover as it stands, one fact to a line. The name is on the left, in the dim ink that
    /// every label on this page uses. The number is on the right.
    ///
    /// <para>Each row is a separate label and value. It is not one monospaced block. A block with the
    /// columns lined up by spaces only lines up while every name is shorter than the padding. It also
    /// reads like program output, not like the answer to a question.</para>
    /// </summary>
    private void BuildPlan()
    {
        _planList.Children.Clear();

        if (_preview is not { } plan)
        {
            _planList.Children.Add(Label(11, dim: true, text: "No plan."));
            return;
        }

        // The name a DJ calls it, the same one the style box shows, not the enum's spelling.
        Row("Style", MixTechniques.For(plan.Style).Name + (_previewSettled ? "" : "   (pending)"));
        Row("Start", $"{MixInputs.Sec(plan.StartAtSeconds)} on the record playing");
        Row("Cue in", $"{MixInputs.Sec(plan.CueInSeconds)} on the record coming in");
        Row("Fade", $"{plan.FadeBars} bars of {plan.BarSeconds:F3}s ({plan.FadeSeconds:F2}s)");
        Row("Mix tempo", $"{plan.MixBpm:F2} BPM");
        Row("Pull", $"out x{plan.OutgoingTempoRatio:F4}, in x{plan.TempoRatio:F4}");
        Row("Release", $"{plan.ReleaseSeconds:F1}s");

        // Where the pull moves through the changeover, on the records that need it. See TempoStep.
        // Nothing on the ordinary changeover, where the row above is the whole story.
        if (plan.TempoSteps.Count > 1)
            Row("Pull steps", string.Join(", ", plan.TempoSteps.Select(s => $"x{s.Ratio:F4} at {s.AtSeconds:F1}s")));

        // The one reading that says whether the mix will hold. The length and the pull above were both
        // chosen to keep it low. So this line shows why they have the values they have.
        if (plan.FadeBars > 0)
            Row("Grid drift", $"{plan.GridDriftSeconds * 1000:F0} ms at worst");

        if (plan.OverlapBars > 0)
            Row("Overlap", $"{plan.OverlapBars} bars");
        if (plan.Arc is not null)
            Row("Arc", "drawn by hand");
        if (plan.Overrides is { } asked)
            Row("Asked for", asked.ToString());

        void Row(string name, string value)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Put(line, Label(11, dim: true, text: name), 0);

            var read = Mono(12, value);
            read.TextWrapping = TextWrapping.Wrap;
            Put(line, read, 1);

            _planList.Children.Add(line);
        }
    }

    // ── small furniture ───────────────────────────────────────────────────────

    private static FrameworkElement Section(UIElement body) => new Border
    {
        Padding = new Thickness(10),
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0x19, 0x1F)),
        Child = body,
    };

    private static FrameworkElement Section(string title, UIElement body)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Label(11, bold: true, dim: true, text: title.ToUpperInvariant()));
        panel.Children.Add(body);
        return Section(panel);
    }

    /// <summary>One entry of the arc's legend: a swatch of the colour and what it means.</summary>
    private static UIElement Key(Color colour, string label)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(colour),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(Label(11, dim: true, text: label));
        return row;
    }

    private static Slider Knob(double from, double to, double value) => new()
    {
        Minimum = from,
        Maximum = to,
        Value = value,
        Width = 110,
        StepFrequency = 1,
    };



    private static TextBlock Label(double size, bool bold = false, bool dim = false, string text = "") =>
        new()
        {
            Text = text,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = new SolidColorBrush(dim
                ? Color.FromArgb(0xFF, 0x8A, 0x90, 0x9A)
                : Color.FromArgb(0xFF, 0xE6, 0xE9, 0xEE)),
        };

    private static TextBlock Mono(double size, string text = "")
    {
        var block = Label(size, text: text);
        block.FontFamily = new FontFamily("Consolas");
        return block;
    }

    private static TextBlock Wrapped(double size, string text, bool dim)
    {
        var block = Label(size, dim: dim, text: text);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }
}
#endif
#endif
