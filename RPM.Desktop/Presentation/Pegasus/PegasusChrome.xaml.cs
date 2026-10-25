using System.Collections.Specialized;
using System.Diagnostics;
using RPM.Core.Import;
using RPM.Core.Lyrics;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Playback;
using RPM.Desktop.Presentation.NowPlaying;
using RPM.Desktop.Presentation.RPaMp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The record, the cover and the words, laid over <see cref="PegasusStage"/>.
///
/// <para>The record is the app's own <c>RecordDisc</c>, dressed from what the header has already resolved:
/// the same pressing colour, the same disc art when the album has one, and the same cover on its label. So
/// this is not a picture of the record that is playing. It is the record that is playing, at the size of
/// the screen.</para>
///
/// <para>It turns on the same terms as in the header: one revolution every <see cref="RevolutionSeconds"/>,
/// ramped in and out over <see cref="SpinUpSeconds"/> with a smoothstep curve, so there is no jerk at
/// either end. It runs from the render tick, not from a Storyboard. A Forever dependency animation does not
/// stop cleanly, and this must stop when the music does.</para>
///
/// <para>There is one difference. The header stops the turn at once when playback stops, and the disc
/// freezes where it stood. Full screen, the record is the whole picture, so it slows down instead, using
/// the same curve in reverse.</para>
///
/// <para>Every per-frame write here is composition only (a rotation and a translation). Nothing in this
/// control dirties layout while it runs. The stage next door depends on that.</para>
/// </summary>
public sealed partial class PegasusChrome : UserControl
{
    /// <summary>One turn of the record. This is the same figure the header uses.</summary>
    private const double RevolutionSeconds = 1.8;

    /// <summary>How long the record takes to reach full speed, and to slow down again.</summary>
    private const double SpinUpSeconds = 1.0;

    private readonly Stopwatch _clock = new();
    private readonly SolidColorBrush _frameBrush = new(Color.FromArgb(0x00, 0, 0, 0));

    private RPaMpModel? _amp;
    private bool _running;
    private double _lastTick;
    private double _ramp;      // 0 stopped, 1 at speed
    private double _angle;

    /// <summary>
    /// How far the record is out of its sleeve: 1 is fully out and turning, 0 is tucked away behind the
    /// cover and hidden.
    ///
    /// <para>This is driven by whether a record is on the deck, not by whether it is playing this second.
    /// Pausing keeps the disc where it is, so it does not slide away and vanish. The deck empties the
    /// moment playback ends, and that is when there is no record left to show.</para>
    /// </summary>
    private double _discOut;

    /// <summary>How far out the disc sits when it is fully out. Relayout works this out with the rest of
    /// the geometry. It is multiplied by <see cref="_discOut"/> every frame.</summary>
    private double _discHome;

    /// <summary>How long the record takes to slide home and back out. It is a little longer than the spin
    /// ramp, so the disc is still sliding when the turning stops. The two do not end together.</summary>
    private const double DiscTuckSeconds = 1.15;

    private bool _pressed;
    private Color? _pressedHue;

    // ── the lyric ──

    /// <summary>
    /// Asks what is being sung, when the record has changed and somebody is looking.
    ///
    /// <para>There are three guards, and each one is part of the feature, not an optimisation. It runs only
    /// while the visualizer is up, so ordinary listening never tells anyone which record is playing. It
    /// runs once per record, because the answer cannot change while a track plays. It does not run at all
    /// for something with no length. A radio stream has no duration to match on and no stable title, so a
    /// lookup for one cannot succeed.</para>
    /// </summary>
    private async void RefreshLyrics()
    {
        var deck = _amp?.Deck;
        var key = deck is null ? null : $"{deck.Artist}|{deck.Title}|{deck.Album}|{(int)deck.DurationSeconds}";

        if (key == _lyricKey)
            return;

        // Any lookup still running belongs to the record that has just ended. Cancel it.
        _lyricLookup?.Cancel();
        _lyricLookup = null;
        _lyricKey = key;

        // Whether an answer is still on its way. This is decided BEFORE the old sheet is dropped. That way
        // anyone watching sees "gone, but one is on its way", not a bare "gone". This is what lets a
        // listener stay on the lyric view across a change of record, instead of losing it for the second it
        // takes to ask.
        var asking = _running && deck is not null && deck.DurationSeconds > 0;
        LyricsPending = asking;
        Drop();

        if (!asking)
            return;

        var cts = new CancellationTokenSource();
        _lyricLookup = cts;

        var sheet = LyricSheet.Empty;
        try
        {
            sheet = await App.Services.GetRequiredService<LyricsService>()
                .ForAsync(deck!.Artist, deck.Title, deck.Album, deck.DurationSeconds, deck.FilePath, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;   // A newer lookup is running and now owns the flag.
        }
        catch
        {
            // Nothing came back. The screen treats that as an answer.
        }

        // The record may have changed while the lookup was running.
        if (cts.IsCancellationRequested || _lyricKey != key)
            return;

        LyricsPending = false;
        _sheet = sheet;
        BuildLyrics();
        LyricsChanged?.Invoke();

        void Drop()
        {
            if (!_sheet.IsSynced && _lyricLines.Length == 0)
                return;
            _sheet = LyricSheet.Empty;
            BuildLyrics();
            LyricsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Takes a sheet that was swapped somewhere else. The track pane's "another version" does this. The
    /// reader is saying that the words on screen are timed against the wrong cut of this song.
    ///
    /// <para>This is the only code here that shows a sheet without having asked for one, and it must. <see
    /// cref="RefreshLyrics"/> asks once per record. A swap made while the record is still playing would not
    /// be seen here until the track ended. Two screens can show these words at the same time, and they must
    /// not show them with two different timings.</para>
    ///
    /// <para>It is torn down with the screen (see <see cref="SetActive"/>). A visualizer that nobody is
    /// looking at has no sheet to keep in step.</para>
    /// </summary>
    private void OnLyricReplaced(string key, LyricSheet sheet)
    {
        // Move back to our own thread before anything is read. The press happened on another thread.
        DispatcherQueue?.TryEnqueue(Apply);

        void Apply()
        {
            // The record may have changed between the press and here. The swap belongs to the record it was
            // made on. It is checked in the service's own terms, because _lyricKey is not stored in them.
            if (!_running || _amp?.Deck is not { } deck)
                return;
            if (!string.Equals(key, LyricsService.Key(deck.Artist, deck.Title, deck.Album, deck.DurationSeconds),
                               StringComparison.OrdinalIgnoreCase))
                return;

            // Only a sheet is passed in here. AnotherAsync returns Empty when there is no other sheet. That
            // is not a reason to remove the words from a screen that has them.
            if (!sheet.IsSynced)
                return;

            _sheet = sheet;
            BuildLyrics();
            LyricsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Lays the sheet out as one TextBlock per line, once.
    ///
    /// <para>The lines are built here and are not rebuilt while the song plays. After this, following the
    /// singer is a translate and a few opacity changes. That is composition work only, so it costs the
    /// stage next door nothing. A list control, or text rewritten as the song moves, would put a layout
    /// pass in front of the shader on every line.</para>
    /// </summary>
    private void BuildLyrics()
    {
        // Only the lines are removed. The glow is part of the canvas itself and lasts for every sheet.
        foreach (var old in _asideBlocks)
            if (old is not null)
                LyricBody.Children.Remove(old);

        foreach (var old in _lyricLines)
            LyricBody.Children.Remove(old);
        _lyricAt = -2;
        _lyricScroll = 0;
        _lyricShift.Y = 0;
        _glowH = _glowY = _glowBass = 0;

        if (!_sheet.IsSynced)
        {
            _lyricLines = [];
            _lyricDots = [];
            _lyricWordSpans = [];
            _lyricWordMeasured = [];
            _lyricWordRuns = [];
            _lyricGlyphs = [];
            _lyricBreaks = [];
            _lyricAside = [];
            _asideBlocks = [];
            _asideOpen = [];
            LyricLayer.Visibility = Visibility.Collapsed;
            ApplyLyricFootHits();   // The sheet is gone, so the bar under it stops taking presses.
            return;
        }

        _lyricLines = new TextBlock[_sheet.Lines.Count];
        _lyricDots = new Microsoft.UI.Xaml.Documents.Run[]?[_sheet.Lines.Count];
        _asked.Clear();
        _lyricWordMeasured = new bool[_sheet.Lines.Count];
        _lyricGlyphs = new (int, int, int)[]?[_sheet.Lines.Count];
        _lyricAside = new bool[_sheet.Lines.Count];
        _asideBlocks = new TextBlock?[_sheet.Lines.Count];
        _asideOpen = new double[_sheet.Lines.Count];
        _lyricWordSpans = new WordSpan[]?[_sheet.Lines.Count];
        _lyricWordRuns = new Microsoft.UI.Xaml.Documents.Run[]?[_sheet.Lines.Count];
        _lyricBreaks = new int[]?[_sheet.Lines.Count];
        for (int i = 0; i < _lyricLines.Length; i++)
        {
            var block = new TextBlock
            {
                Text = LineText(i),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = _lyricInk.Dim,
                Opacity = 0,

                // The same weight on every line, sung or not. Weight changes the width of the words.
                // Lighting the current line by making it bolder would re-wrap it and move everything under
                // it on each beat, so the sheet would twitch each time the singer moved on. The line being
                // sung is shown with brightness alone. This weight is just the whole sheet standing up a
                // little against a background that changes colour every second.
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            // Each line reports its own height once layout has given it one. A wrapped line is taller than
            // an unwrapped one, and only the framework knows by how much. The updates are coalesced. A
            // sheet is laid out all at once, so N lines would each fire SizeChanged, and each would
            // re-stack all N lines. That is N squared work for one answer, and only the last one is
            // correct.
            block.SizeChanged += (_, _) => QueuePlaceLyrics();

            // A line with no words is a break. The writer put a break here, and the sheet keeps it (see
            // LyricSheet), so the highlight does not sit on a verse nobody is singing. An empty line says
            // nothing, though. A reader would see the sheet stop moving and could not tell a break from a
            // stall. So it is drawn as three dots, which fill in across the length of the break. The dots
            // show how long the break has left. A whole line in brackets is a second voice. This is checked
            // on the line as WRITTEN, not as displayed, so the censor cannot turn a verse into an aside by
            // starring a word at either end.
            _lyricAside[i] = IsAside(_sheet.Lines[i].Text);

            // A second singer gets a row of their own under the line. It is a SEPARATE block, not a second
            // row inside the line's own block. Everything on this sheet is keyed by line index: the
            // timings, the word spans, the letter runs, the dots and the scroll target. Splitting one sheet
            // line into two visual lines would have to be handled in each of those. Kept beside the line,
            // the index keeps its meaning, and the extra row is simply something the stack knows how to
            // make room for.
            if (AsideGroups(_sheet.Lines[i].Text).Count > 0)
            {
                var aside = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = _lyricInk.Dim,
                    Opacity = 0,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                };
                aside.SizeChanged += (_, _) => QueuePlaceLyrics();
                _asideBlocks[i] = aside;
                // Closed. Nothing is being sung yet. A sheet that opened every one of these on layout would
                // show its whole second voice at once, and then fold it away line by line.
                _asideOpen[i] = 0;
                LyricBody.Children.Add(aside);
            }

            if (string.IsNullOrWhiteSpace(_sheet.Lines[i].Text))
                _lyricDots[i] = FillDots(block);
            else
                FillWords(block, i, []);

            _lyricLines[i] = block;
            LyricBody.Children.Add(block);
        }

        _lineY = new double[_lyricLines.Length];
        _lineH = new double[_lyricLines.Length];

        LyricLayer.Visibility = Visibility.Visible;
        ApplyLyricFootHits();
        LayoutLyrics();

        // Beside the record, a new sheet is a column of a different width, so the group is centred again.
        // The record glides to where this song puts it, and the room is told where the words now are.
        RetargetBeside(glide: true);
        PushWordBox();
    }

    /// <summary>
    /// Every stretch of a line that belongs to a second voice: the bracketed groups, in the order they were
    /// written, as half-open ranges over the text.
    ///
    /// <para>Sheets write two singers as one line, with the second singer in brackets. Sometimes the
    /// bracket closes the line: "It all falls down (and when it falls down, who you gon' call now?)".
    /// Sometimes it is scattered through the line: "Oh, when it all (uh)". Left inline, this is one line of
    /// two people's words. It runs off the column and wraps into the verse below, and nothing on the screen
    /// says which half belongs to whom. A whole bracketed line is handled the same way (see <see
    /// cref="IsAside"/>). Here the words are on one row instead of two, so they are shown the same way:
    /// underneath, and smaller.</para>
    ///
    /// <para><b>Every group is handled, however short.</b> An ad-lib is the same kind of thing as a sung
    /// answer. "(uh)" is a second voice in the room, just as "(come on, come on)" is. Keeping the short
    /// groups inline while the long ones moved would put one voice in two places on one screen. Several
    /// groups on one line are joined with commas on the row below, as they would be if the row were written
    /// by hand.</para>
    ///
    /// <para>A line bracketed end to end is not this case. It is an aside whole, and it has no verse to sit
    /// under.</para>
    /// </summary>
    private static List<(int Open, int Close)> AsideGroups(string? text)
    {
        var found = new List<(int, int)>();
        if (text is not { Length: > 2 } line || IsAside(line))
            return found;

        var depth = 0;
        var open = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '(')
            {
                if (depth++ == 0)
                    open = i;
            }
            else if (line[i] == ')' && depth > 0 && --depth == 0 && open >= 0)
            {
                // There must be text inside the brackets and text outside them. A line that is all bracket,
                // with nothing around it, is an aside written untidily.
                if (line[(open + 1)..i].Trim().Length > 0)
                    found.Add((open, i + 1));
                open = -1;
            }
        }

        return found.Count > 0 && line.Length > found.Sum(g => g.Item2 - g.Item1) ? found : [];
    }

    /// <summary>The second voice's row for a line: its bracketed groups, without the brackets, joined with
    /// commas. This is what the row shows where there are no runs to build it from. It is also how the row
    /// is measured (see WidestLine).</summary>
    private static string AsideText(string text, List<(int Open, int Close)> groups) =>
        string.Join(", ", groups.Select(g => text[(g.Open + 1)..(g.Close - 1)].Trim()));

    /// <summary>The verse text of a line: the line with its bracketed stretches taken out, and the double
    /// spaces that leaves behind tidied away. This is what the verse row shows when there are no word
    /// timings to hang runs off.</summary>
    private static string Verse(string text, List<(int Open, int Close)> groups)
    {
        var verse = new System.Text.StringBuilder(text.Length);
        var at = 0;
        foreach (var (open, close) in groups)
        {
            verse.Append(text[at..open]);
            at = close;
        }
        verse.Append(text[at..]);

        return string.Join(' ', verse.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    /// <summary>Whether a line is an aside. It is bracketed from end to end, which is how every sheet
    /// writes a backing vocal. A line that only CONTAINS brackets is not an aside. For example, "I wish I
    /// understood (yeah)" is the verse, with a shout in it.</summary>
    private static bool IsAside(string? text)
    {
        var line = text?.Trim();
        return line is { Length: > 2 } && line[0] == '(' && line[^1] == ')'
               && line.IndexOf(')', 1) == line.Length - 1;   // One bracket pair around the whole line.
    }

    /// <summary>
    /// One line as it should be shown.
    ///
    /// <para>The censor is applied HERE, when the line is drawn, and not earlier. The sheet in memory and
    /// the copy on disk both stay as they were written. This makes the censor reversible with a key press,
    /// without fetching the song again (see <see cref="LyricCensor"/>).</para>
    /// </summary>
    private string LineText(int i) => LyricCensor.Mask(_sheet.Lines[i].Text, _maskProfanity);

    /// <summary>Starts with the same value as <see cref="SettingsService.MaskProfanity"/>. A sheet built
    /// before the host has set anything is already correct, and is not rewritten a frame later.</summary>
    private bool _maskProfanity;

    // ── how anything INSIDE a line is lit ──
    //
    // Both the break dots and the word sweep need one glyph of a line at its own strength, and that changes
    // on every frame. There is one way to do that here, and it is not the obvious one.
    //
    // CHANGING A BRUSH DOES NOT WORK, whether its Opacity or its Color. An inline's brush is read when the
    // block's text is laid out. Writing to that brush afterwards does not notify the block, because the
    // paragraph has been built and nothing marks it as stale. It partly works, which is worse than not
    // working at all: the new strengths appear only the next time something ELSE marks the block as
    // changed. The dots would not move, and the words would change only when the line changed.
    //
    // ASSIGNING THE RUN'S Foreground DOES WORK. It is a property of the run, so setting it marks the line
    // as stale, and the paragraph is built again. For this reason the strengths below are a fixed LADDER of
    // brushes, not one brush per glyph. Changing a strength means handing the run a different brush. A new
    // brush per glyph per frame would be sixty allocations a second for every word on screen.

    /// <summary>How many strengths anything inside a line can be drawn at. There are enough to make a sweep
    /// across a word look like a fade rather than a switch. There are few enough that most frames land on a
    /// rung they are already on, and write nothing.</summary>
    private const int InkSteps = 24;

    /// <summary>The ladder itself, from black up to the lit colour. It is shared by every dot and every
    /// word on screen. It is static, because it depends only on the lit colour. One record's sheet has no
    /// more claim on it than the next.</summary>
    private static readonly SolidColorBrush[] InkLadder = BuildInkLadder();

    private static SolidColorBrush[] BuildInkLadder()
    {
        var ladder = new SolidColorBrush[InkSteps + 1];
        for (var i = 0; i <= InkSteps; i++)
        {
            var by = (double)i / InkSteps;
            ladder[i] = new SolidColorBrush(Color.FromArgb(
                0xFF,
                (byte)(LyricBrushes.LitColor.R * by),
                (byte)(LyricBrushes.LitColor.G * by),
                (byte)(LyricBrushes.LitColor.B * by)));
        }
        return ladder;
    }

    /// <summary>The rung nearest to a strength between nought and one. The rungs are scaled toward black,
    /// not made transparent. The sheet is read over a dark stage, so a colour at four tenths looks like a
    /// word at four tenths. Unlike an opacity, a colour survives the trip onto the screen.</summary>
    private static SolidColorBrush Ink(double strength)
        => InkLadder[Math.Clamp((int)Math.Round(strength * InkSteps), 0, InkSteps)];

    /// <summary>Puts a run at a strength, but only if it is not already there. Most frames move nothing far
    /// enough to change a rung. A write that changes nothing still makes the line rebuild, so it is
    /// skipped.
    /// </summary>
    private static void Light(Microsoft.UI.Xaml.Documents.Run run, double strength)
    {
        var brush = Ink(strength);
        if (!ReferenceEquals(run.Foreground, brush))
            run.Foreground = brush;
    }

    // ── the three dots on a line with nothing sung on it ──

    /// <summary>The three dots of a break line, or null on a line that has words. They live inside the
    /// line's own TextBlock. The sheet's layout, its scroll and its dimming all measure and move
    /// TextBlocks. A break drawn as some other kind of element would be a second thing for all of them to
    /// handle.</summary>
    private Microsoft.UI.Xaml.Documents.Run[]?[] _lyricDots = [];

    /// <summary>How faint a dot is before its turn. All three dots are visible from the start. What changes
    /// is which of them have been reached.</summary>
    private const double DotRest = 0.16;

    /// <summary>How long a break with no line after it is assumed to run, in seconds. Only the last line of
    /// a sheet can be such a break. The dots should finish, not stop at two thirds.</summary>
    private const double DotTailSeconds = 6;

    /// <summary>Puts three dots in a break line and returns their runs. The spaces between the dots take
    /// the block's own foreground, so they are invisible either way.</summary>
    private Microsoft.UI.Xaml.Documents.Run[] FillDots(TextBlock block)
    {
        var dots = new Microsoft.UI.Xaml.Documents.Run[3];
        for (var d = 0; d < dots.Length; d++)
        {
            if (d > 0)
                block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " });

            dots[d] = new Microsoft.UI.Xaml.Documents.Run { Text = "•", Foreground = Ink(DotRest) };
            block.Inlines.Add(dots[d]);
        }
        return dots;
    }

    /// <summary>
    /// Fills the dots in across the break, and empties them again once it is over.
    ///
    /// <para>This is read from the playhead on every frame, not animated. That is what makes it meaningful.
    /// The dots are not decoration that marks time. They show how far through the break the record is. If
    /// you seek into the middle of a break, the dots are already two-thirds filled, which is the
    /// truth.</para>
    /// </summary>
    private void TickDots(double pos, int at)
    {
        if (_lyricDots.Length == 0)
            return;

        for (var i = 0; i < _lyricDots.Length; i++)
        {
            if (_lyricDots[i] is not { } dots)
                continue;

            // Every break except the one being played stays at rest. This is written every time, not only
            // when the line changes, because a seek can leave a half-filled break behind it in either
            // direction.
            if (i != at)
            {
                foreach (var dot in dots)
                    Light(dot, DotRest);
                continue;
            }

            var start = _sheet.Lines[i].Seconds;
            var end = i + 1 < _sheet.Lines.Count ? _sheet.Lines[i + 1].Seconds : start + DotTailSeconds;
            var span = Math.Max(0.05, end - start);
            var through = Math.Clamp((pos - start) / span, 0, 1);

            for (var d = 0; d < dots.Length; d++)
            {
                // Each dot takes its own share of the break and fills across it. Together the three dots
                // read as one bar filling up, not as three lights blinking on.
                var share = Math.Clamp(through * dots.Length - d, 0, 1);
                Light(dots[d], DotRest + (1 - DotRest) * share);
            }
        }
    }

    // ── the word being sung, on the line being sung ──

    /// <summary>How faint a word is before the singer reaches it, within a line that is already the lit
    /// one. It is much fainter than an unsung line. The whole line is being read, and a reader whose eye
    /// runs ahead of the singer must be able to see where it is going. This only shows where the voice has
    /// got to.</summary>
    private const double WordRest = 0.42;

    /// <summary>
    /// How much of a word's own moment the highlight takes to arrive, when that moment was GUESSED. It is a
    /// fraction of the moment: at six tenths, a word is at full strength by about its own middle.
    ///
    /// <para><b>A lower value means an earlier and sharper arrival.</b> The word snaps up near its start.
    /// <b>A higher value means a later and gentler arrival.</b> This is stated plainly, because the number
    /// reads backwards from the effect: turning it down makes the arrival earlier.</para>
    ///
    /// <para>The whole span is the wrong window for a guess. If the word fades in across all of its own
    /// moment, it only reaches full strength once the singer has FINISHED saying it. The brightest word on
    /// the line is then always the one just sung. The eye reads the brightest word as the current one, so
    /// the display looks like it is lagging behind.</para>
    ///
    /// <para>Six tenths is used rather than less, because a guessed span cannot be trusted to the tenth. A
    /// slower arrival is also a blurrier one, and blur is the right way to show a number that might be
    /// wrong. Where the span was MEASURED there is nothing to blur. See <see
    /// cref="MeasuredArrival"/>.</para>
    /// </summary>
    private const double WordArrivesOver = 0.60;

    /// <summary>
    /// How long the highlight takes to arrive when the word's moment was MEASURED off the record, in
    /// seconds. Unlike the guess above, it arrives so as to FINISH on that moment, rather than to start
    /// there.
    ///
    /// <para>The two cases need opposite treatment, which is not obvious. A guessed span is a statement
    /// about the line, with the word's own position inside it estimated. Easing into it hides the estimate.
    /// A measured span says when this word begins. If the highlight then spends the first half of the word
    /// arriving, the word is dim at the very moment it is sung. That is the "words come in too late"
    /// problem that measuring was meant to fix. So the fade runs BEFORE the start of the word and lands on
    /// it. The word is at full strength as the singer reaches it.</para>
    ///
    /// <para>This is short, and shorter still on a short word. A syllable must not be lit while the
    /// syllable before it is still being sung.</para>
    /// </summary>
    private const double MeasuredArrival = 0.16;

    /// <summary>
    /// How long the light takes to arrive at one LETTER. This sets how soft the edge of the sweep is.
    ///
    /// <para>This is wider than a letter usually lasts, on purpose. A letter of an ordinary sung word lasts
    /// forty or fifty milliseconds. If the arrival were exactly that long, each letter would be fully lit
    /// before the next one began, and the line would look like a row of lights coming on in order. At a
    /// fifth of a second, three or four letters are always part-way lit at once. Then the eye sees one soft
    /// edge travelling along the line.</para>
    ///
    /// <para>This is a minimum, not a fixed width, and it only applies up to <see cref="SlowestGlyph"/>.
    /// Past that point the edge would be wider than the sweep is long. A fade that began a second before
    /// the letter it is about would be the light running ahead of the singer, not a soft edge.</para>
    /// </summary>
    private const double GlyphSoftness = 0.20;

    /// <summary>
    /// The slowest the light may cross a letter, in seconds, however long the word it is in was measured to
    /// last.
    ///
    /// <para><b>A word's measured length is not always a length of singing.</b> The aligner reports a word
    /// as running from the first frame of its first letter to the last frame of its last letter. Where the
    /// model loses the thread in the middle of a word, those two frames are separated by a gap with nothing
    /// in it: a letter placed, then a bar of blanks, then the rest of the word. Measured over four records,
    /// about one word in twenty carries a gap of over half a second. The gap averages near a second and
    /// reaches two and a quarter. The aligner's own confidence does not tell those letters from sung ones,
    /// because the scores on either side of a gap are the same. So the span cannot be repaired where it is
    /// made, and it should not be. The span is what the record says, and <see cref="SungAt"/> needs the
    /// whole span to know when a line is over.</para>
    ///
    /// <para>The damage is fixed here, where it happens. If the time were shared out evenly, a word
    /// stretched across a gap would crawl. A three letter word would take a second and a third, one letter
    /// every four hundred milliseconds, while the singer is already two words on. The eye reads that as the
    /// sheet stalling mid-word and then lurching. So the light crosses a word at a singing rate, and then
    /// waits on it. A seventh of a second per character is the same rate that <see cref="LyricTiming"/>
    /// caps a whole line at. It is the same argument, one level down. A sweep that finishes early and holds
    /// is a small error that nobody reads as one. A sweep still crawling through the middle of a word is
    /// the failure people notice.</para>
    /// </summary>
    private const double SlowestGlyph = 1.0 / 7.0;

    /// <summary>
    /// How quickly a HELD last word comes up, in seconds. It comes up as one word, not letter by letter.
    ///
    /// <para>A hold cannot be swept. The last word of a line is where a singer carries a note. The record
    /// reports that word as lasting two or three seconds, and the measurement is correct. But the letters
    /// of the word were sung in the first part of that time, and the rest is one vowel being held. If the
    /// word were shared out across its letters, the last letter would light seconds after it was sung. The
    /// eye would see the sheet crawling through the end of every line.</para>
    ///
    /// <para>So a held last word is not swept at all. It arrives whole and quickly, as a word did before
    /// letters were drawn one at a time. It then stays lit for as long as the note is carried. That is the
    /// truth, and it is what the singer is doing.</para>
    ///
    /// <para>Only the LAST word is treated this way, and only a held one. In the middle of a line, a word
    /// that measures long is usually the model having lost the thread, not a singer holding a note. That
    /// case is handled by <see cref="SlowestGlyph"/>. The other words of a line are swept as they always
    /// were.</para>
    /// </summary>
    private const double HeldArrival = 0.22;

    /// <summary>Where each word of each line sits in its text, and when it is sung. This is null for a line
    /// that nothing sweeps across, such as a break, or a line whose words could not be read off.</summary>
    private WordSpan[]?[] _lyricWordSpans = [];

    /// <summary>
    /// The lines a writer put in brackets: the second voice, the answering line, the ad lib. These are not
    /// the song's own words, and they are shown differently.
    ///
    /// <para>A sheet marks them because they are not sung by whoever is carrying the line. They sit under
    /// it or answer it, and a reader following along needs to know which is which. If they were held in the
    /// stack at full size and full weight, they would look equal to the verse. A chorus with an ad lib
    /// after every phrase would then turn into twice as many lines of the same thing.</para>
    ///
    /// <para>So they are drawn smaller. Once they are done, they are removed rather than staying in the
    /// column as one more dimmed line. An aside is a moment, not a lyric to be read back.</para>
    /// </summary>
    private bool[] _lyricAside = [];

    /// <summary>How much smaller an aside is drawn than the verse it answers.</summary>
    private const double AsideScale = 0.76;

    /// <summary>Which lines have had their words MEASURED off the record, rather than shared out across the
    /// line. Only these are placed to the tenth, and only these are lit to arrive ON the word rather than
    /// INTO it. See <see cref="MeasuredArrival"/>.</summary>
    private bool[] _lyricWordMeasured = [];

    /// <summary>One run per LETTER. This lets the light move THROUGH a word rather than switch it on. A run
    /// is the only thing inside a TextBlock that can be lit on its own. A word lit as one thing pops. A
    /// word lit one letter at a time reads as a sweep passing over it, which is what a karaoke line is.
    /// Runs are used rather than separate elements, so the sheet's layout, its scroll and its dimming keep
    /// measuring and moving one block per line. They are lit through <see cref="Light"/>. See the note on
    /// the ink ladder for why that has to be done this way, and not by holding a brush per
    /// letter.</summary>
    private Microsoft.UI.Xaml.Documents.Run[]?[] _lyricWordRuns = [];

    /// <summary>For each letter run, which word it belongs to and where in that word it sits. The word's
    /// own moment is divided across its letters when the frame is drawn, not stored here. So a line
    /// re-timed by the aligner needs nothing rebuilt. The same runs simply light on better numbers.
    /// </summary>
    private (int Word, int At, int Of)[]?[] _lyricGlyphs = [];

    /// <summary>
    /// Lays one line out as a run per word, each with its own ink, so the singer can be followed through it
    /// rather than only to it.
    ///
    /// <para>The words and their moments come from <see cref="LyricTiming"/>. The sheet's own word stamps
    /// are used where it has them. Otherwise the line is spread across its words. For everything LRCLIB
    /// serves, the second case is what happens. See the note there on how good a guess that is.</para>
    ///
    /// <para>This is built from the DISPLAYED text, so the offsets match what is actually on screen. The
    /// censor's asterisks are swept over like any other word.</para>
    /// </summary>
    /// <param name="breaks">Character offsets in the displayed text where the line must be broken onto a
    /// new row, from <see cref="BreaksFor"/>. The value is given at every call and is never defaulted. A
    /// sheet built without them wraps in the middle of words, which this arrangement exists to prevent. An
    /// empty list is the correct value for a line that fits on one row. It is also used on the first build,
    /// when the column is not measured yet. <see cref="RewrapLyrics"/> then supplies the real
    /// breaks.</param>
    private void FillWords(TextBlock block, int i, IReadOnlyList<int> breaks)
    {
        var text = LineText(i);
        var until = i + 1 < _sheet.Lines.Count ? _sheet.Lines[i + 1].Seconds : double.PositiveInfinity;
        var spans = LyricTiming.Sweep(text, _sheet.Lines[i].Seconds, until, _sheet.Lines[i].Words);

        // Which stretches belong to the second voice, in the DISPLAYED text. This is asked again here,
        // rather than carried over from earlier. The censor can change the length of the string, and every
        // offset below indexes what is actually on screen.
        var aside = i < _asideBlocks.Length ? _asideBlocks[i] : null;
        var groups = aside is null ? [] : AsideGroups(text);
        if (aside is not null && groups.Count == 0)
        {
            // The line has a second voice as written, but not as displayed. For example, the censor has
            // starred the closing word. The row is removed, rather than left standing empty under the line.
            _asideBlocks[i] = null;
            LyricBody.Children.Remove(aside);
            aside = null;
        }

        if (spans.Count == 0)
        {
            // There is nothing to sweep. The two halves are still two voices, so they are still split. This
            // is done by hand, since there are no runs to hang them from.
            if (aside is not null && groups.Count > 0)
            {
                block.Text = Verse(text, groups);
                aside.Text = AsideText(text, groups);
            }
            return;
        }

        // The block is emptied first. Text and Inlines are two views of one content. Adding runs to a block
        // that already has text appends to it rather than replacing it, so the line would read twice.
        block.Text = string.Empty;
        block.Inlines.Clear();

        if (aside is not null)
        {
            aside.Text = string.Empty;
            aside.Inlines.Clear();
        }

        // Which bracketed group an offset falls in, or -1 for the verse itself.
        int GroupAt(int offset)
        {
            for (var g = 0; g < groups.Count; g++)
                if (offset >= groups[g].Open && offset < groups[g].Close)
                    return g;
            return -1;
        }

        // Which block a letter belongs in. All the runs stay in ONE ordered list, whichever block they are
        // in. A run is lit by writing its Foreground, and it does not matter what holds it. So the sweep
        // crosses from the verse into the second voice and back without needing to know that it has.
        TextBlock Into(int offset) => aside is not null && GroupAt(offset) >= 0 ? aside : block;

        // Whether what is at this offset is a second voice. That is a bracketed stretch of a line, or a
        // line that is bracketed end to end and is an aside whole.
        bool Second(int offset) => GroupAt(offset) >= 0 || _lyricAside[i];

        // The brackets themselves are not shown. They are punctuation doing the job of a layout. They tell
        // the reader "somebody else is singing this". Once the words have a row of their own that says the
        // same thing, the marks are two characters of noise. One of them lands in the middle of the sweep
        // and lights up like a syllable.
        //
        // They are blanked, not cut out. The runs and the spans are two views of one string, and the sweep
        // indexes between them. Removing a character would shift every offset after it. A run with no text
        // holds its place, draws nothing, and can be lit as much as needed.
        string Shown(string run, int offset) => Second(offset) ? run.Replace("(", "").Replace(")", "") : run;

        // Which group the row last took a word from. The next word from a DIFFERENT group is joined to it
        // with a comma, not a space. Two ad-libs written apart on the line are two things said. Running
        // them together would read as one.
        var voiced = -1;

        var runs = new List<Microsoft.UI.Xaml.Documents.Run>(text.Length);
        var glyphs = new List<(int Word, int At, int Of)>(text.Length);
        var at = 0;

        for (var k = 0; k < spans.Count; k++)
        {
            // The gap before this word is given no ink of its own. It takes the block's foreground, and it
            // is whitespace either way. Only the letters are lit. The gap that spans the split is the
            // bracket and the space in front of it. It belongs to the second voice's row, and it is trimmed
            // there. Otherwise the row would begin with the indent the verse ended on.
            var group = GroupAt(spans[k].Start);

            // What goes BEFORE this word. In the row, it is a space between words of one group, and a comma
            // between groups. In the verse, it is whatever was written there. The whitespace and brackets
            // between the verse and a group belong to neither. They are not drawn. If they were emitted
            // into the verse, the verse would end on a space, or on two.
            if (group >= 0 && aside is not null)
            {
                if (voiced >= 0)
                    aside.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                    {
                        Text = group == voiced ? " " : ", ",
                    });
                voiced = group;
            }
            else if (spans[k].Start > at && GroupAt(at) < 0 && at > 0)
            {
                block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = Shown(text[at..spans[k].Start], at),
                });
            }

            // The row this word starts, when it is not the row the last one ended on.
            //
            // The row ends are written out by hand, rather than left to the text engine. Uno cuts a paragraph into segments at RUN boundaries. It then fills each row
            // greedily with whatever segment comes next. A run that will not fit ends the row wherever it
            // stands. One run per letter is needed for the sweep and cannot be avoided, so every letter
            // becomes a place where the row may end. A long line would then break as "the industr / y wants".
            // Neither wrapping mode helps. The choice between them is only reached for a segment that will
            // not fit a row of its own, and a single letter always fits.
            //
            // So the row ends are put in by hand, at word starts, and the engine never has to choose.
            if (breaks.Contains(spans[k].Start) && GroupAt(spans[k].Start) < 0)
                block.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());

            // One run per letter. This looks costly, but it is not. A run is a string and a brush. The
            // block still measures once, and only the two or three letters the light is passing over are
            // written on any frame.
            var of = spans[k].Length;
            for (var c = 0; c < of; c++)
            {
                var glyph = new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = Shown(text.Substring(spans[k].Start + c, 1), spans[k].Start + c),
                    Foreground = Ink(WordRest),
                };
                Into(spans[k].Start + c).Inlines.Add(glyph);
                runs.Add(glyph);
                glyphs.Add((k, c, of));
            }

            at = spans[k].Start + of;
        }

        // Whatever is left after the last word. This is only the verse's text, and only where the verse is
        // what it trails. A bracket that closes the line adds nothing to the row.
        if (at < text.Length && GroupAt(at) < 0)
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Shown(text[at..], at) });

        _lyricWordSpans[i] = [.. spans];
        _lyricWordRuns[i] = [.. runs];
        _lyricGlyphs[i] = [.. glyphs];
    }

    // ── the measured timing, where this machine can measure it ──

    /// <summary>Lines already asked about, so that a line is never asked twice. The answer does not change
    /// while a record plays. Asking again would decode the same window and get the same reply.</summary>
    private readonly HashSet<int> _asked = [];

    /// <summary>How far ahead of the singer the alignment runs. This is one line. It has to finish before
    /// the words are sung. Asking for more would spend the machine's time on a sheet the reader may leave
    /// after the first chorus.</summary>
    private const int AlignAhead = 1;

    /// <summary>
    /// Asks what the record itself says about when these words are sung, and puts the answer in place of
    /// the guess.
    ///
    /// <para>Only the TIMES change. The runs, their text and their offsets are all built from the same
    /// line, and none of them move. So there is nothing to rebuild and nothing to re-wrap. The next frame
    /// of <see cref="TickWords"/> simply reads better numbers. This is why the spans are kept apart from
    /// the runs.</para>
    ///
    /// <para>This is fire and forget, and it stays silent about failure. A machine with no model weights, a
    /// machine that cannot keep up, or a record that will not decode, leaves the guessed timing where it
    /// was. That is a working answer, not an empty screen.</para>
    /// </summary>
    private async void AlignLines(int at)
    {
        if (!_alignWords || _sheet is not { IsSynced: true } sheet)
            return;

        if (App.Services.GetRequiredService<LyricAlignment>() is not { IsAvailable: true } alignment)
            return;

        var path = _amp?.Deck?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        for (var i = Math.Max(0, at); i <= at + AlignAhead && i < sheet.Lines.Count; i++)
        {
            if (!_asked.Add(i))
                continue;

            // Break lines have no words to place. A line whose sweep could not be built has nothing to put
            // an answer into.
            if (i >= _lyricWordSpans.Length || _lyricWordSpans[i] is null)
                continue;

            var line = i;
            var text = LineText(line);

            try
            {
                var placed = await alignment.ForLineAsync(
                    path, sheet, line, text, _amp?.Position ?? -1);

                // The record may have changed while the lookup was running. The answer belongs to the sheet
                // it was asked of. This is checked by identity, because a new sheet is a new object.
                if (placed is null || !ReferenceEquals(_sheet, sheet)
                    || line >= _lyricWordSpans.Length || _lyricWordSpans[line] is null)
                    continue;

                _lyricWordSpans[line] = [.. placed];
                if (line < _lyricWordMeasured.Length)
                    _lyricWordMeasured[line] = true;

                // Other devices can use this too. What the record said about this line is filed under the
                // track playing.
                App.Services.GetRequiredService<LyricTimingShare>().Note(
                    App.Services.GetRequiredService<Services.Audio.PlaybackService>().PlayingRpmId, sheet, line, placed);
            }
            catch
            {
                // Failing here is harmless. The record is already playing, and the guess stays in place.
            }
        }
    }

    /// <summary>
    /// Whether the words are placed by listening to the record, or shared out across the line.
    ///
    /// <para>This is the listener's own setting. It is kept in settings and handed down by the host. This
    /// control does not read it directly. That way there is one place that decides, and one place that
    /// stores it, as with the censor beside it.</para>
    /// </summary>
    public bool AlignWords
    {
        get => _alignWords;
        set
        {
            if (_alignWords == value)
                return;
            _alignWords = value;

            // If this is turned on during a record, the lines already on screen are asked about from where
            // the singer is now. If it is turned off, nothing is un-asked. A timing already measured is a
            // better answer than the guess it replaced. Throwing it away would make the sheet worse, just
            // to show that the switch had an effect.
            if (_alignWords)
                AlignLines(_lyricAt);
        }
    }

    private bool _alignWords;

    /// <summary>
    /// Which line is being sung. This is not always the line the sheet's stamps point at.
    ///
    /// <para>An LRC file says when each line STARTS. It never says when a line ends. So the only end it can
    /// offer is the start of the next line. Those are different facts. It is normal for a singer to hold
    /// the last word of a line into the start of the next one. It is also normal for a stamp to be written
    /// a beat early. If the sheet moved on at the stamp, it would move while the words are still being
    /// sung, and the line's sweep would be cut short and jump flat in the middle of a word.</para>
    ///
    /// <para>Where a line's words have been MEASURED, the record itself says when the line ends. So the
    /// line is held until its last word is done. It is only held until the NEXT line actually starts,
    /// though. Two lines cannot both be sung at once. When the singer reaches the next line, that line has
    /// the floor, however long the last one ran.</para>
    ///
    /// <para>A guessed line has no measured end to rely on. It falls through to the sheet's own stamps.
    /// This is the behaviour it has always had.</para>
    ///
    /// <para><b>A line waits for its voice.</b> A line measured to start well after its stamp would take
    /// over at the stamp and stand there unlit until the singer reached it. So the line before it stays up
    /// until the light is about to arrive on the new line's first word (<see cref="VoiceLead"/> before it),
    /// but never longer than <see cref="VoiceWaitLongest"/> past the stamp. A measurement that went astray
    /// cannot hold the sheet back for longer than that.</para>
    /// </summary>
    private int SungAt(double pos)
    {
        var at = _sheet.IndexAt(pos);
        if (at < 0)
            return at;

        // Where the line on the sheet actually begins: its first measured word where it has been measured,
        // and its own stamp otherwise, which is all there is to go on.
        double? measuredStart = at < _lyricWordMeasured.Length && _lyricWordMeasured[at]
                                && at < _lyricWordSpans.Length && _lyricWordSpans[at] is { Length: > 0 } next
            ? next[0].From
            : null;
        var stamp = _sheet.Lines[at].Seconds;

        var last = at - 1;
        if (last >= 0 && last < _lyricWordMeasured.Length && _lyricWordMeasured[last]
            && _lyricWordSpans[last] is { Length: > 0 } previous
            && pos < previous[^1].To)
            return pos < (measuredStart ?? stamp) ? last : at;

        // The line before is done (or was never measured), and this one is measured to start well after its
        // stamp. The line before stays up until the light is about to reach this one, within the limit.
        if (measuredStart is { } starts && pos < starts - VoiceLead && pos < stamp + VoiceWaitLongest)
            return last;

        return at;
    }

    /// <summary>How long before its first measured word a line takes over, in seconds: about when the
    /// light starts to arrive on it (see <see cref="MeasuredArrival"/> and <see cref="GlyphSoftness"/>).
    /// The TV waits the same.</summary>
    private const double VoiceLead = 0.35;

    /// <summary>The longest a line waits past its own stamp for its first measured word, in seconds.</summary>
    private const double VoiceWaitLongest = 4.0;

    /// <summary>
    /// Brings the words of the sung line up as the singer reaches them.
    ///
    /// <para>This is read off the playhead on every frame, not animated, for the same reason the break dots
    /// are. If you seek into the middle of a line, it is already half swept, which is the truth. Only the
    /// sung line is written. Every other line was set flat on the last change of line. <see cref="Light"/>
    /// writes only where a word has actually changed its rung, so a quiet frame touches nothing. The text
    /// never changes, so nothing re-wraps and nothing re-stacks.</para>
    /// </summary>
    private void TickWords(double pos, int at)
    {
        if (at < 0 || at >= _lyricWordRuns.Length)
            return;
        if (_lyricWordRuns[at] is not { } runs || _lyricWordSpans[at] is not { } spans)
            return;

        if (_lyricGlyphs[at] is not { } glyphs)
            return;

        for (var k = 0; k < runs.Length && k < glyphs.Length; k++)
        {
            var (word, c, of) = glyphs[k];
            if (word >= spans.Length)
                continue;

            var span = spans[word];
            var held = span.To - span.From;

            // A held last word is not swept. Every letter of it takes the WORD's own moment, not a share of
            // it. So the whole word comes up together, and then stays lit while the note is carried. "Held"
            // means the record says the word lasted longer than it takes to sing. The extra time is the
            // hold, and a hold has nothing in it to sweep across. See HeldArrival.
            var carried = word == spans.Length - 1 && held > of * SlowestGlyph;

            // Otherwise, this is how long the light spends on each letter: the word's own moment, shared
            // out evenly. It is never slower than a singing rate. The share is even because nothing here
            // knows better. The record was asked about words, not letters. The rate is capped because a
            // word's measured length is not always a length of singing. See SlowestGlyph.
            var step = carried ? 0 : Math.Min(held / Math.Max(1, of), SlowestGlyph);
            var opens = span.From + step * c;

            // The arrival takes a window wider than one letter, so two or three letters are always
            // mid-arrival together. That overlap is the effect. A hard edge that steps from letter to
            // letter reads as a row of lights. A soft edge reads as a wipe. The arrival lands ON the
            // letter, not into it, for the same reason a measured word does. See MeasuredArrival.
            var arriving = carried ? HeldArrival : Math.Max(GlyphSoftness, step);
            var through = Math.Clamp((pos - (opens - arriving)) / arriving, 0, 1);

            Light(runs[k], WordRest + (1 - WordRest) * through);
        }
    }

    /// <summary>
    /// Whether ordinary swearing and named drugs are masked, as well as slurs. This is the listener's own
    /// setting. It is kept in settings and handed down by the host. This control does not read it directly,
    /// so there is one place that decides and one place that stores it.
    ///
    /// <para>Slurs are NOT governed by this setting. They are masked either way. This is the one thing this
    /// screen does without being asked, and the one thing it will not stop doing on request. The setting is
    /// for the wider list. It applies to every record played. What the track's own advisory flag says about
    /// itself has no bearing on it.</para>
    ///
    /// <para>Changing it rewrites the text of the lines already on screen, rather than rebuilding the
    /// sheet. The blocks, their handlers and the scroll all survive. A word that re-wraps its line is
    /// caught by the same <c>SizeChanged</c> that catches a line wrapping for any other reason.</para>
    /// </summary>
    public bool MaskProfanity
    {
        get => _maskProfanity;
        set
        {
            if (_maskProfanity == value)
                return;
            _maskProfanity = value;
            for (int i = 0; i < _lyricLines.Length && i < _sheet.Lines.Count; i++)
            {
                // This is never a break line. Writing Text replaces a block's inlines, and the inlines of a
                // break line are the three dots and their inks. A line with no words has nothing to mask,
                // so writing to it would only throw the dots away.
                if (_lyricDots.Length > i && _lyricDots[i] is not null)
                    continue;

                // Nor is a line drawn as words, for one more reason. Writing Text would throw away every
                // run and every ink, and the sweep would stop dead on a keypress about swearing. The runs
                // are rewritten in place instead. Masking keeps a word's length, with the first letter kept
                // and the rest starred, so the offsets still land. They are checked anyway, because a list
                // that has slipped would put the wrong halves of the wrong words on screen.
                if (_lyricWordRuns.Length > i && _lyricWordRuns[i] is { } runs
                    && _lyricWordSpans[i] is { } spans && _lyricGlyphs[i] is { } glyphs)
                {
                    var masked = LineText(i);
                    for (var k = 0; k < runs.Length && k < glyphs.Length; k++)
                    {
                        var (word, c, _) = glyphs[k];
                        if (word >= spans.Length)
                            continue;

                        var offset = spans[word].Start + c;
                        if (offset < masked.Length)
                            runs[k].Text = masked.Substring(offset, 1);
                    }
                    continue;
                }

                _lyricLines[i].Text = LineText(i);
            }

            // Masking keeps a word's length, but not its width. A masked word is narrower or wider than the
            // word it replaces. So a line that fitted a row may no longer fit, and a line that did not fit
            // may now fit. The rows are worked out again. A line whose breaks come out where they were is
            // left untouched.
            RewrapLyrics();
        }
    }

    private bool _placeQueued;

    /// <summary>Re-stacks the sheet once, after the layout pass that asked for it has finished. Every line
    /// reports its height in the same pass. This turns those N answers into one walk down the canvas,
    /// instead of N walks.</summary>
    private void QueuePlaceLyrics()
    {
        if (_placeQueued)
            return;
        _placeQueued = true;
        if (DispatcherQueue?.TryEnqueue(() => { _placeQueued = false; PlaceLyrics(); }) != true)
        {
            _placeQueued = false;
            PlaceLyrics();
        }
    }

    /// <summary>
    /// The width the lyric is read through, for a window this wide. It is wide enough that most lines fit
    /// on one row. A rap verse or a long chorus line has a great many words. It still stops short of the
    /// full width of a big monitor, which would read as a paragraph rather than a lyric.
    ///
    /// <para>This has its own method because three things are placed against it: the sheet, the intro card
    /// that stands in the sheet's place, and the box the room is told to keep the mirror ball out
    /// of.</para>
    /// </summary>
    private static double LyricColumn(double w) => Math.Min(w * 0.78, 1400);

    /// <summary>
    /// Lays out the foot: the seek bar and the keys along the bottom of the lyric view and of the bare room.
    ///
    /// <para>The bar runs the width of the lyric's column. The foot is only drawn where the lyric stands
    /// alone or nothing stands at all, and in both the column is the middle one. It can only be scrubbed
    /// while one of those views is on screen (see ApplyLyricFootHits). An invisible bar must not catch a
    /// drag meant for the picture. The whole foot (the bar and the keys together) is lifted off the bottom
    /// edge, so the margin belongs to the container, not the bar. Otherwise the keys would hang off the end
    /// of the screen. On a tall screen the whole foot is drawn larger as one block (LyricFootScale), at the
    /// figure that the column's own bar and keys use. The bar is laid out at the column over that figure,
    /// so once it is drawn larger, it is the column's width again.</para>
    ///
    /// <para>It is called from <see cref="Relayout"/> as well as from <see cref="LayoutLyrics"/>. The bare
    /// room has a foot with no sheet behind it, and LayoutLyrics does nothing without a sheet.</para>
    /// </summary>
    private void LayoutFoot(double w, double h)
    {
        var footScale = QueueScale;
        LyricFootScale.Factor = footScale;
        LyricSeek.Width = LyricColumn(w) / footScale;
        LyricFoot.Margin = new Thickness(0, 0, 0, Math.Max(32, h * 0.045));
    }

    private void LayoutLyrics()
    {
        if (_lyricLines.Length == 0)
            return;

        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        var size = Math.Clamp(h * 0.030, 15, 34);

        // Beside the record, the column is as wide as the longest line. So the longest line is measured
        // first, at this size, before anything is wrapped against the column.
        if (_lyricBeside)
            _sheetWidest = WidestLine(size);

        var (left, column) = SheetColumn(w, h);

        _lyricSize = size;
        _lyricColumn = column;
        _lyricLeft = left;

        LayoutFoot(w, h);
        for (int i = 0; i < _lyricLines.Length; i++)
        {
            var block = _lyricLines[i];
            block.FontSize = i < _lyricAside.Length && _lyricAside[i] ? size * AsideScale : size;
            block.Width = column;
            Canvas.SetLeft(block, left);

            // The second voice is drawn at the same size as a whole bracketed line, because it is the same
            // kind of thing: somebody else singing under the verse.
            if (i < _asideBlocks.Length && _asideBlocks[i] is { } aside)
            {
                aside.FontSize = size * AsideScale;
                aside.Width = column;
                Canvas.SetLeft(aside, left);
            }
        }

        // The rows each line breaks into, now that there is a column to break them against. This comes
        // before PlaceLyrics, because where a line breaks decides how tall it is, and PlaceLyrics stacks on
        // the heights.
        RewrapLyrics();

        // Where each line sits comes from PlaceLyrics, once the framework has said how tall the lines are.
        PlaceLyrics();
    }

    /// <summary>
    /// Works out where every line has to break, and rebuilds the lines whose breaks have moved.
    ///
    /// <para>This is called whenever something that decides a break has changed: the column, the type size,
    /// or the words themselves when the mask goes on or off. A line whose breaks come out the same is left
    /// alone. Rebuilding it would throw away its runs and their inks, and the sweep would stop dead in the
    /// middle of a word for a resize that changed nothing about it.</para>
    /// </summary>
    private void RewrapLyrics()
    {
        if (_lyricColumn <= 0 || _lyricSize <= 0)
            return;

        for (var i = 0; i < _lyricLines.Length; i++)
        {
            // A break line's content is its dots. A line with no timed words is a single run, which the
            // engine already breaks on words by itself. Neither needs any work.
            if (_lyricDots.Length > i && _lyricDots[i] is not null)
                continue;
            if (_lyricWordSpans.Length <= i || _lyricWordSpans[i] is not { Length: > 0 } spans)
                continue;

            // A line with a second voice is measured wrongly, so it is not measured at all. Its bracketed
            // stretches are drawn in a row of their own, and what is left in the verse is narrower than the
            // string this would measure. So every break would be worked out against words that are not on
            // that row. Those lines are the short ones anyway, having given half their words away. They
            // keep the engine's own wrapping until there is a reason to do better.
            if (_asideBlocks.Length > i && _asideBlocks[i] is not null)
                continue;

            var wanted = BreaksFor(LineText(i), spans, _lyricLines[i].FontSize, _lyricLines[i].FontFamily);
            if (_lyricBreaks.Length > i && Same(_lyricBreaks[i], wanted))
                continue;

            _lyricBreaks[i] = wanted;
            FillWords(_lyricLines[i], i, wanted);
        }

        static bool Same(int[]? had, int[] now)
        {
            if (had is null || had.Length != now.Length)
                return false;
            for (var k = 0; k < now.Length; k++)
                if (had[k] != now[k])
                    return false;
            return true;
        }
    }

    /// <summary>
    /// How wide the sheet's longest line is drawn, unwrapped, at <paramref name="size"/>. The lyric's
    /// column beside the record is made from this (see <see cref="Beside"/>).
    /// </summary>
    /// <remarks>
    /// Each line is measured as it is DRAWN, which is not always its text. A line with a second voice is
    /// two rows: the verse at full size, and the bracketed words under it at <see cref="AsideScale"/>. Both
    /// rows are measured. A whole bracketed line is drawn at <see cref="AsideScale"/>. A break is three
    /// dots and is narrower than any line, so it is not measured. The same ruler is used as for working out
    /// the breaks (<see cref="Measured"/>), so the column and the wrapping agree about every width.
    /// </remarks>
    private double WidestLine(double size)
    {
        double widest = 0;
        for (var i = 0; i < _lyricLines.Length; i++)
        {
            if (_lyricDots.Length > i && _lyricDots[i] is not null)
                continue;

            var text = LineText(i);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var family = _lyricLines[i].FontFamily;
            var groups = _asideBlocks.Length > i && _asideBlocks[i] is not null ? AsideGroups(text) : [];
            if (groups.Count > 0)
            {
                widest = Math.Max(widest, Measured(Verse(text, groups), size, family));
                widest = Math.Max(widest, Measured(AsideText(text, groups), size * AsideScale, family));
                continue;
            }

            var lineSize = i < _lyricAside.Length && _lyricAside[i] ? size * AsideScale : size;
            widest = Math.Max(widest, Measured(text, lineSize, family));
        }

        return widest;
    }

    /// <summary>The row ends in each line. A layout pass that changes nothing rebuilds nothing. Null for a
    /// line that has never been measured.</summary>
    private int[]?[] _lyricBreaks = [];

    /// <summary>
    /// Works out where one line has to be broken, so that it breaks between words.
    ///
    /// <para>This is greedy and measured, which is the only way to get it right. How wide a word is depends
    /// on the font, the size and the letters. The same line breaks in different places on two windows. Each
    /// row takes as many words as fit, and ends before the first word that does not.</para>
    ///
    /// <para>The candidates are the start of each sung word, not every space in the string. Those are the
    /// offsets the runs are built from. So a break can always be placed exactly where it was asked for. A
    /// break offset that fell between the runs would never be emitted.</para>
    ///
    /// <para>A single word wider than the whole column is left to the engine, which breaks it across rows
    /// by letter. That is the one case where breaking inside a word is the right answer. A reader would do
    /// the same with a word that does not fit the page.</para>
    /// </summary>
    private int[] BreaksFor(string text, WordSpan[] spans, double size, FontFamily family)
    {
        if (string.IsNullOrWhiteSpace(text) || size <= 0)
            return [];

        // The common case is a line that fits on one row. Most of a sheet fits on a row, so it needs no
        // decisions.
        var whole = Measured(text, size, family);
        if (whole <= 0)
            return [];   // Nothing can be measured yet. Leave the line as it is rather than guess.
        if (whole <= _lyricColumn - WrapSlack)
            return [];

        var breaks = new List<int>();
        var from = 0;
        var last = -1;

        foreach (var span in spans)
        {
            if (span.Start <= from)
                continue;

            // Measured from the start of the row, not from the word's own width. The widths of two words
            // side by side are not their widths added together, because the space between them counts.
            if (Measured(text[from..span.Start].TrimEnd(), size, family) <= _lyricColumn - WrapSlack)
            {
                last = span.Start;
                continue;
            }

            // This word is the one that overflows. The row ends before it. If it is the first word of the
            // row and is still too wide, the row ends after it instead, since a row has to hold something.
            var at = last > from ? last : span.Start;
            if (at <= from)
                continue;

            breaks.Add(at);
            from = at;
            last = -1;
        }

        return [.. breaks];
    }

    /// <summary>How much of the column is kept free when deciding a break. This is a hair. It is there
    /// because the measurement and the engine's own arithmetic are two passes over the same text. A row
    /// measured at exactly the column's width can be judged one pixel too wide by the engine. The engine
    /// would then break it again, by letter, which is the thing this is meant to prevent.</summary>
    private const double WrapSlack = 2;

    /// <summary>
    /// How wide a piece of text is, in the face the lyric is drawn in.
    ///
    /// <para>This is measured by a real <see cref="TextBlock"/>, not worked out from the font. The layout
    /// engine that lays the sheet out is the only thing that agrees with itself about kerning, fallback
    /// faces and the width of a space. The block lives on the canvas, off to one side and invisible. A
    /// block that is not in a tree has no face to measure with.</para>
    /// </summary>
    private double Measured(string text, double size, FontFamily family)
    {
        if (_lyricRuler is null)
        {
            _lyricRuler = new TextBlock
            {
                TextWrapping = TextWrapping.NoWrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,   // The weight that every line is drawn at.
                IsHitTestVisible = false,
                Opacity = 0,
            };
            Canvas.SetLeft(_lyricRuler, -100000);
            LyricHost.Children.Add(_lyricRuler);
        }

        _lyricRuler.FontFamily = family;
        _lyricRuler.FontSize = size;
        _lyricRuler.Text = text;
        _lyricRuler.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return _lyricRuler.DesiredSize.Width;
    }

    /// <summary>The block that nothing is drawn with. See <see cref="Measured"/>. It lives on the host
    /// rather than the body, so that emptying the sheet does not remove the ruler.</summary>
    private TextBlock? _lyricRuler;

    /// <summary>
    /// Stacks the lines, each below the last, at the height each one actually turned out to be.
    ///
    /// <para>The lines are not placed at a fixed pitch, because a fixed pitch cannot work. A line long
    /// enough to wrap takes two rows. Stepping down by one row regardless drops the next line on top of the
    /// second half of it. Every long line in a verse would overlap the one after it.</para>
    ///
    /// <para>The heights are READ from the layout, never worked out from the font. How many rows a line
    /// takes depends on where the words happen to break, and only the framework knows that. This is called
    /// from each line's own SizeChanged, so a wrap that appears on a resize re-stacks everything below it.
    /// Setting a line's position cannot change its size, so this cannot re-trigger itself.</para>
    /// </summary>
    private void PlaceLyrics()
    {
        if (_lyricLines.Length == 0 || _lineY.Length != _lyricLines.Length)
            return;

        // The gap between lines. The leading is spacing now, not a pitch for each whole line.
        var gap = Math.Round(_lyricSize * (LyricLeading - 1.0));

        // This checks whether anything actually MOVED. That is a different question from whether this was
        // called.
        //
        // It matters because of a loop that costs the sheet its frame rate. Lighting a word writes the
        // run's Foreground. That marks the line's text as stale, so the framework re-measures it and raises
        // SizeChanged. SizeChanged asks for a re-place. If a re-place always forced a full repaint, that
        // repaint would light every word of every line. That writes more Foregrounds, and the loop would
        // run on. Every word the sweep touched would cost a walk down the whole sheet. A scroll would snap
        // with no frame to interpolate from. The words stalled, skipped and arrived late. None of it was
        // the model, which stayed about five seconds ahead the whole time.
        //
        // A re-measure that leaves every line exactly where it was has nothing to tell anybody.
        if (!StackLyrics(gap))
            return;

        _lyricAt = -2;      // The paint is keyed on the line changing, so force one.
        TickLyrics(0);
    }

    private double _lyricSize = 24;
    private double _lyricColumn, _lyricLeft;
    private double[] _lineY = [];
    private double[] _lineH = [];

    /// <summary>
    /// Follows the singer: finds the line for the playhead, eases the sheet toward it, and lights it.
    ///
    /// <para>This reads <c>Position</c> straight off the player on every frame. It does not use the
    /// player's own position notification. That notification exists to move a seek bar, and it fires
    /// several times a second. That is fine for a bar, but text sliding past would look jerky. The playhead
    /// is a continuous value, and this is the one place in the app that needs it as one.</para>
    /// </summary>
    private void TickLyrics(double dt)
    {
        if (_lyricLines.Length == 0 || Root.ActualHeight <= 0)
            return;

        // Nothing is followed while the words are not on screen. A record with a sheet keeps it in memory
        // whichever composition is up. Following a singer that nobody is reading would be a scroll, a glow
        // and a walk down every line in the verse, sixty times a second, in front of a shader. The TARGET
        // view is checked as well as the current one. That way a switch toward the lyric finds the sheet
        // already in the right place, and does not ease into it as it arrives.
        if (!_view.IsLyric() && !_viewTo.IsLyric())
            return;

        var pos = _amp?.Position ?? 0;
        var at = SungAt(pos);
        if (_lineY.Length != _lyricLines.Length)
            return;

        // Where the sheet should sit: the MIDDLE of the sung line, on the middle of the screen. The middle
        // is used, not the top, so a line that wraps to two rows still sits centred, and does not hang
        // below where a one-row line would be. Before the first line, the sheet waits with its opening line
        // in place, rather than scrolled off the top. The fold runs first, before the target is read.
        // Folding moves the very numbers the target is read from. A target taken from the last frame's
        // layout would have the sheet easing toward a place that no longer exists.
        FoldAsides(dt, at);

        var idx = Math.Clamp(at < 0 ? 0 : at, 0, _lineY.Length - 1);
        var target = _lineY[idx] + _lineH[idx] / 2;

        // The ease does not depend on the frame rate. When there is no frame to interpolate from, the sheet
        // jumps straight to its place. That happens after a fresh layout, or after a seek that moves the
        // playhead a long way.
        _lyricScroll = dt <= 0 || Math.Abs(target - _lyricScroll) > Root.ActualHeight * 2
            ? target
            : _lyricScroll + (target - _lyricScroll) * (1 - Math.Exp(-dt * LyricGlide));

        _lyricShift.Y = Root.ActualHeight / 2 - _lyricScroll;

        // This runs every frame and is composition only. The light behind the sung line breathes with the
        // low end, which is the same figure the room is lit by. Only opacity and scale are written here.
        // Where the glow sits changes only when the line changes, below.
        //
        // The low end is smoothed, not copied. The stage gives a new reading every frame from a moving
        // signal. A light driven straight from that reading does not breathe. It flickers, because the
        // number jitters by a few percent between frames on nothing but noise.
        //
        // The smoothing is asymmetric on purpose, as in every level meter. It rises almost as fast as the
        // drum, so the light still lands ON the beat. It falls away slowly, so what happens between beats
        // is a decay, not a scramble. One constant for both directions would have to choose between a light
        // that is late and a light that is nervous.
        var bass = _stage?.Bass ?? 0f;
        var toward = bass > _glowBass ? GlowAttack : GlowRelease;
        _glowBass = dt <= 0
            ? bass
            : _glowBass + ((bass - _glowBass) * (1 - Math.Exp(-dt * toward)));

        LyricGlow.Opacity = at < 0 ? 0 : 0.10 + _glowBass * 0.55;
        LyricGlowScale.ScaleX = LyricGlowScale.ScaleY = 1 + _glowBass * 0.14;

        Hush(pos, at);

        // This runs every frame, like the glow. A break filling in is the one thing on this sheet that
        // moves between one line and the next. It is the whole reason a break is worth drawing.
        TickDots(pos, at);

        // The singer's place along the line being sung is updated here too. This is the same argument as
        // the dots, one level down. It is the only thing on the sheet that moves WITHIN a line, and it is
        // what makes the sheet worth following word by word.
        TickWords(pos, at);

        ShapeGlow(dt, at);

        // The lines on their way to their new strength since the last change of line.
        TickLineFades(dt);

        if (at == _lyricAt)
            return;

        // A repaint asked for from outside (a new sheet, a new layout) puts every line straight at its
        // strength. So does a first frame. Only a change of line that the singer made is faded.
        var fade = _lyricAt != -2 && dt > 0;
        var leaving = _lyricAt;
        _lyricAt = at;

        // The line that has arrived, and the one after it, are measured from the record where this machine
        // can manage it. This runs when the line changes, not every frame, and each line is only ever asked
        // about once.
        AlignLines(at);

        if (_lineFadeFrom.Length != _lyricLines.Length)
        {
            _lineFadeFrom = new double[_lyricLines.Length];
            _lineFadeTo = new double[_lyricLines.Length];
        }
        _lineFadeAt = 0;

        // Only on a change of line, which is a few times a minute. The sung line is lit and the ones
        // around it fall away, so the eye is told where it is without the rest becoming a wall of text.
        for (int i = 0; i < _lyricLines.Length; i++)
        {
            var distance = Math.Abs(i - at);
            var block = _lyricLines[i];
            var strength = at < 0
                ? (i == 0 ? 0.55 : Math.Max(0, 0.34 - i * 0.10))
                : distance switch
                {
                    0 => 1.0,
                    1 => 0.42,
                    2 => 0.24,
                    _ => distance <= LyricReach ? 0.12 : 0.0,
                };

            // A second voice that has been sung is over. It stays in the column while it is coming in, so a
            // reader can see the answer arriving. It goes as soon as the singer is past it, so it does not
            // sit there as one more dimmed line competing with the verse. Its ROW stays, though. Removing
            // the space at once would move every line under it at the end of every phrase.
            if (i < _lyricAside.Length && _lyricAside[i] && at >= 0 && i < at)
                strength = 0;

            var words = _lyricWordRuns.Length > i ? _lyricWordRuns[i] : null;

            // Where the fade starts: the strength the line shows now, so a line already on its way carries
            // on from where it is rather than jumping.
            //
            // The line that is handing over has its sweep flattened below. Its letters were at their own
            // strengths, and a line cut short by the next one still had letters at rest. Flattened to full
            // and faded from the line's full strength, it would flash white before it fell away. So its fade
            // starts from the strength its letters were showing on average.
            var from = block.Opacity;
            if (i == leaving && words is { Length: > 0 })
                from *= MeanInk(words);

            // The sung line arrives whole when it is swept. Its letters before the light are at rest, which
            // is the strength it showed as the next line, so nothing jumps as it takes over, and no fade can
            // leave it half lit. A line with no sweep fades up like the others.
            if (distance == 0 && at >= 0 && words is { Length: > 0 })
                from = strength;

            if (!fade)
                from = strength;

            _lineFadeFrom[i] = from;
            _lineFadeTo[i] = strength;
            block.Opacity = from;
            block.Foreground = distance == 0 && at >= 0 ? _lyricInk.Lit : _lyricInk.Dim;

            // A second voice is read with the line it belongs to, so it is dimmed with that line. It is
            // also taken down by how far it has folded, so the row is gone before the space it held has
            // finished closing. Otherwise it would slide under the verse below.
            if (i < _asideBlocks.Length && _asideBlocks[i] is { } voice)
            {
                voice.Opacity = block.Opacity * _asideOpen[i];
                voice.Foreground = block.Foreground;
            }

            // A line drawn as words has its own inks, so the foreground written just above does not reach
            // it. The sung line is left to TickWords, which runs every frame. Every other line is set FLAT,
            // with one strength across all its words. A line nobody is singing must not show a
            // half-finished sweep, because that would claim the sheet is somewhere it is not. Its dimming
            // is the block's own opacity, as it is for a line with no words.
            if ((distance != 0 || at < 0) && words is not null)
                foreach (var word in words)
                    Light(word, 1);
        }
    }

    /// <summary>How long a line takes to go from one strength to the next when the singer moves on, in
    /// seconds. The TV takes the same.</summary>
    private const double LineFadeSeconds = 0.35;

    /// <summary>Where each line's fade starts and ends, and how far into it the sheet is. Set on a change
    /// of line, walked on every frame until it lands.</summary>
    private double[] _lineFadeFrom = [], _lineFadeTo = [];
    private double _lineFadeAt = LineFadeSeconds;

    /// <summary>
    /// One frame of the lines' fade to their new strengths. The lines go from where they were to where the
    /// new sung line puts them, on the same curve as every other fade here. It writes nothing once the fade
    /// has landed, so a line being sung costs nothing between changes of line.
    /// </summary>
    private void TickLineFades(double dt)
    {
        if (_lineFadeAt >= LineFadeSeconds || _lineFadeFrom.Length != _lyricLines.Length)
            return;

        _lineFadeAt = dt <= 0 ? LineFadeSeconds : Math.Min(LineFadeSeconds, _lineFadeAt + dt);
        var eased = Fader.Ease(_lineFadeAt / LineFadeSeconds);

        for (int i = 0; i < _lyricLines.Length; i++)
        {
            double from = _lineFadeFrom[i], to = _lineFadeTo[i];
            if (from == to)
                continue;

            var block = _lyricLines[i];
            block.Opacity = from + ((to - from) * eased);
            if (i < _asideBlocks.Length && _asideBlocks[i] is { } voice)
                voice.Opacity = block.Opacity * _asideOpen[i];
        }
    }

    /// <summary>The average strength of a line's letters, as their inks show it (see <see cref="Ink"/>). A
    /// run whose ink is not on the ladder counts as full.</summary>
    private static double MeanInk(Microsoft.UI.Xaml.Documents.Run[] runs)
    {
        double sum = 0;
        foreach (var run in runs)
        {
            var rung = run.Foreground is SolidColorBrush brush ? Array.IndexOf(InkLadder, brush) : -1;
            sum += rung < 0 ? 1 : (double)rung / InkSteps;
        }
        return runs.Length == 0 ? 1 : sum / runs.Length;
    }

    /// <summary>
    /// Sizes the light behind the sung line and moves it there. It eases, and it is written on every frame
    /// while it is still on its way.
    ///
    /// <para>The light is written when the line changes, and also while the row under the line is opening
    /// or folding. A second voice can change the height of the line within a fifth of a second. A light
    /// sized only once, when the line arrived, would cover the verse and leave the row below it in the
    /// dark. It would then jump to the right size on the next line. Eased on the same clock as everything
    /// else here, it grows with the row it lights.</para>
    ///
    /// <para><b>It is written only while it is moving.</b> Height and position are layout, not composition,
    /// so every one of these writes is a re-arrange. Once the ease has arrived, the numbers stop being
    /// written. A verse of ordinary lines therefore costs one write when each line changes.</para>
    ///
    /// <para>The light is taller than the words by a fixed halo, not by a multiple of them. A multiple
    /// gives the same result while every line is one row high. But it doubles the light as soon as a line
    /// is two rows.</para>
    /// </summary>
    private void ShapeGlow(double dt, int at)
    {
        if (at < 0 || at >= _lineY.Length)
            return;

        var halo = _lyricSize * 1.35 * 1.6;
        var height = _lineH[at] + halo;
        var top = _lineY[at] + (_lineH[at] / 2) - (height / 2);

        // On a fresh sheet, after a seek, or when the light has never been placed, it goes straight to its
        // place. It does not glide across the screen from wherever the last record left it.
        var jump = dt <= 0 || _glowH <= 0 || Math.Abs(top - _glowY) > Root.ActualHeight;

        _glowH = jump ? height : _glowH + ((height - _glowH) * (1 - Math.Exp(-dt * LyricGlide)));
        _glowY = jump ? top : _glowY + ((top - _glowY) * (1 - Math.Exp(-dt * LyricGlide)));

        // Half a point. This is the same figure the line stack settles on. Below this, the change is the
        // ease's own tail rather than a light that is still moving, and writing it would be a re-arrange
        // for nothing.
        //
        // The check is written as "is this NOT close enough". A size that has never been set is NaN, and
        // every comparison with NaN is false. If the check were written the other way round, the light
        // would look already the right size and would never be written. That would hold for the first frame
        // of every record, and nothing after it would correct it.
        if (!Near(LyricGlow.Height, _glowH))
            LyricGlow.Height = _glowH;
        if (!Near(Canvas.GetTop(LyricGlow), _glowY))
            Canvas.SetTop(LyricGlow, _glowY);

        // Its left edge is worked out on its own. The sheet moves between the middle and the column beside
        // the record. A column of the same width in another place would leave the light where the sheet was
        // before it moved.
        var width = _lyricColumn * 0.92;
        if (!Near(LyricGlow.Width, width))
            LyricGlow.Width = width;
        var left = _lyricLeft + (_lyricColumn * 0.04);
        if (!Near(Canvas.GetLeft(LyricGlow), left))
            Canvas.SetLeft(LyricGlow, left);

        static bool Near(double had, double wants) => Math.Abs(had - wants) <= 0.5;
    }

    /// <summary>
    /// Puts every line, and every second voice under one, where it goes. It says whether any of it actually
    /// moved.
    ///
    /// <para>This is separate from <see cref="PlaceLyrics"/>, because the fold needs the stacking WITHOUT
    /// the repaint that follows it. A repaint lights the whole sheet again. The fold runs on every frame
    /// for a fifth of a second. If it went through PlaceLyrics, every word of every line would be lit
    /// thirty times, for a change that moves some rows up by a few points. That is the loop described in
    /// PlaceLyrics.</para>
    /// </summary>
    private bool StackLyrics(double gap)
    {
        var moved = false;
        double y = 0;

        for (int i = 0; i < _lyricLines.Length; i++)
        {
            var block = _lyricLines[i];
            var height = block.ActualHeight > 1 ? block.ActualHeight : _lyricSize * 1.35;

            // This is written only where the value actually changed. The fold calls this every frame for a
            // fifth of a second. On Skia, a write is a repaint whether or not the value differs. A sheet of
            // forty lines that rewrites forty identical tops would cause the same loop as described in
            // PlaceLyrics.
            if (Math.Abs(_lineY[i] - y) > 0.5)
                Canvas.SetTop(block, y);

            // The second voice sits directly under its line. The room it takes is its height times how open
            // it is. That product is the whole of the fold. As it eases to nought, the rows below climb
            // into the space. The sheet's scroll target is worked out from these same numbers, so the sheet
            // closes the gap and follows the singer in one movement. It does not snap shut under a scroll
            // that was already moving somewhere else.
            var room = 0.0;
            if (i < _asideBlocks.Length && _asideBlocks[i] is { } aside)
            {
                var tall = aside.ActualHeight > 1 ? aside.ActualHeight : _lyricSize * AsideScale * 1.35;
                if (Math.Abs(Canvas.GetTop(aside) - (y + height)) > 0.5)
                    Canvas.SetTop(aside, y + height);
                room = tall * _asideOpen[i];
            }

            // Half a pixel. Below that, the change is the framework's own rounding, not a line that has
            // wrapped.
            if (Math.Abs(_lineH[i] - (height + room)) > 0.5 || Math.Abs(_lineY[i] - y) > 0.5)
                moved = true;

            // The line's height INCLUDES the room its second voice takes, so the pair is centred as one
            // thing. The glow behind the sung line covers both of them.
            _lineY[i] = y;
            _lineH[i] = height + room;
            y += height + room + gap;
        }

        return moved;
    }

    /// <summary>
    /// Opens a second voice's row for the line being sung, and folds it away again on either side.
    ///
    /// <para><b>Only while its line is the current one.</b> A row held open ahead of the singer would look
    /// like a bracket under a line nobody has reached yet. The sheet would then seem to have two voices
    /// going at once all the way down the verse. A row held open behind the singer is one more dimmed line
    /// competing with the verse for the eye. A second voice belongs to ONE line, so it is on screen for
    /// exactly as long as that line is.</para>
    ///
    /// <para>Taking the space back at once would jolt every line under it at the end of every phrase. So
    /// the row is eased. It runs on the same clock as the scroll, and the lines below simply close up. What
    /// is left, before and after, is the same as it would be if the line had never had a second
    /// voice.</para>
    /// </summary>
    private void FoldAsides(double dt, int at)
    {
        if (_asideBlocks.Length == 0 || _lineY.Length != _lyricLines.Length)
            return;

        var moved = false;
        for (int i = 0; i < _asideBlocks.Length; i++)
        {
            if (_asideBlocks[i] is null)
                continue;

            // Open for the sung line only, and for nothing else.
            var want = at >= 0 && i == at ? 1.0 : 0.0;
            var open = _asideOpen[i];
            if (Math.Abs(want - open) < 0.001)
            {
                if (open != want)
                {
                    _asideOpen[i] = want;
                    Fade(i);
                    moved = true;
                }
                continue;
            }

            _asideOpen[i] = dt <= 0
                ? want
                : open + ((want - open) * (1 - Math.Exp(-dt * AsideFold)));

            // This is written on every frame of the fold. It is done here, not with the rest of the sheet's
            // dimming. The rest of the dimming runs on a change of line, a few times a minute. A row whose
            // opacity was only set then would take the whole fold at full strength, and vanish at the end
            // of it. The room the row takes and how much of it can be seen are one movement.
            Fade(i);
            moved = true;
        }

        if (moved)
            StackLyrics(Math.Round(_lyricSize * (LyricLeading - 1.0)));

        // The row is read with the line it belongs to, so it takes that line's own dimming, multiplied by
        // how far it has opened.
        void Fade(int i)
        {
            if (_asideBlocks[i] is { } voice)
                voice.Opacity = _lyricLines[i].Opacity * _asideOpen[i];
        }
    }

    /// <summary>
    /// Takes the sheet away while nothing is being sung, and brings it back before the singer starts.
    /// During a long intro, the record's own name stands in its place.
    ///
    /// <para>The rule is <see cref="LyricHush"/>'s, and it depends only on the playhead. This method only
    /// writes the result to the screen. The change is eased, so the hush fades like every other fade in
    /// this window, even though no <see cref="Fader"/> drives it.</para>
    /// </summary>
    private void Hush(double pos, int at)
    {
        // The wait being sat through. Before the first line this is the intro, measured from the top of the
        // record. After the last line it never ends. An infinity is used to say so.
        var intro = at < 0;
        var from = intro ? 0 : _sheet.Lines[at].Seconds;
        var until = at + 1 < _sheet.Lines.Count ? _sheet.Lines[at + 1].Seconds : double.PositiveInfinity;

        var show = Fader.Ease(LyricHush.Show(pos, from, until, intro));

        // The card takes exactly what the sheet gives up, and it is only shown before the first line. A
        // break in the middle of a song is a break in something the listener is already following.
        // Announcing the title there would tell them what they already know. Once the singing has started,
        // this is zero on every frame, and the card is not there.
        //
        // It is never shown beside the record. The record's own title is on screen next to the sheet, and a
        // second one would name the record twice.
        var card = intro && !_lyricBeside ? 1 - show : 0;

        // This runs every frame, and the answer is the same on nearly every one. So it is guarded. Writing
        // an opacity is a composition pass, and the stage next door pays for it.
        if (LyricBody.Opacity != show)
            LyricBody.Opacity = show;
        if (LyricIntro.Opacity != card)
            LyricIntro.Opacity = card;
    }

    /// <summary>The two brushes a lyric is drawn in, made once. Making a brush for each line on each frame
    /// would allocate memory every frame, and the stage next door pays for that.</summary>
    private sealed class LyricBrushes
    {
        /// <summary>The colour the sung line is drawn in. The break dots use it too. Each dot is given a
        /// brush of its own, because each one is at its own strength.</summary>
        public static Color LitColor { get; } = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        /// <summary>The colour of every other line. It is close to the lit colour on purpose. Which line is
        /// being sung is shown with OPACITY, not with hue (see the note on the sheet's dimming). The word
        /// sweep shows its part of the same thing in the same way.</summary>
        public static Color DimColor { get; } = Color.FromArgb(0xFF, 0xEA, 0xEA, 0xEA);

        public SolidColorBrush Lit { get; } = new(LitColor);
        public SolidColorBrush Dim { get; } = new(DimColor);
    }

    // ── the three compositions ──
    //
    // See PegasusView. There are two fields rather than one, because a change of view is a dissolve, and a
    // dissolve has a middle. `_view` is the view that is LAID OUT. It only changes while the words are
    // invisible. `_viewTo` is the view the button has asked for. Pressing again in the middle of a dissolve
    // moves the target, and the swap already in flight lands on the new target.
    private PegasusView _view, _viewTo;
    private bool _swapping;

    // ── the lyric beside the record ──
    //
    // PegasusView.LyricsBeside is the drawn-in composition, with the record and its three lines slid to the
    // left and the lyric beside them. The change into it is a SLIDE, not the dissolve above. The record is
    // the thing being looked at. Taking it off the screen and putting it back somewhere else would make it
    // blink, which is what the dissolve avoids.
    //
    // Where it slides TO is not a fixed place. The record, the gap and the lyric's column are centred on
    // the screen as one group. So the empty space left of the cover is the same as the space right of the
    // lyric (see Beside). The column is as wide as the sheet's longest line. So where the record stands
    // depends on the song, and it moves when the next song's sheet comes in.

    /// <summary>How far the record and its lines are moved from the middle, in points. This is zero where
    /// the drawn-in composition puts them, and <see cref="Beside"/>'s Shift where they stand beside the
    /// lyric. It is eased on the tick (TickBeside) and written as a translate (ApplyBeside), so the slide
    /// costs no layout pass.</summary>
    private double _besideX;

    private double _besideFromX, _besideToX, _besideElapsed, _besideSeconds;

    /// <summary>Whether the record belongs beside the lyric. This follows the TARGET view. Every aim of the
    /// slide is worked out from it (see RetargetBeside).</summary>
    private bool _besideOn;

    /// <summary>How wide the sheet's longest line is drawn, unwrapped, at the type size last laid out. The
    /// lyric's column beside the record is made exactly this wide, so the text's right edge is the group's
    /// right edge (see Beside). It is zero until a sheet has been measured. It is measured in
    /// LayoutLyrics.</summary>
    private double _sheetWidest;

    /// <summary>How long the slide takes. The lyric also fades in over this time. It is about the length of
    /// a whole dissolve (SwapOutMs + SwapInMs), so the two ways of changing view take the same
    /// time.</summary>
    private const int BesideMs = 480;

    /// <summary>Whether the sheet is placed in the column beside the record, rather than across the middle
    /// of the screen. It follows the TARGET view. It changes only on the way into one of the two lyric
    /// views, so a sheet that is fading out is never moved under the eye (see PlaceSheetFor).</summary>
    private bool _lyricBeside;

    /// <summary>How long the words take to leave, and to come back. They leave faster than they arrive. The
    /// eye accepts a fast departure, but it notices a fast arrival.</summary>
    private const int SwapOutMs = 170;
    private const int SwapInMs = 300;

    private Marquee[] _lines = [];

    /// <summary>The mini player's title, artist and album, as lines that scroll when they do not fit its
    /// card. Built with the tree, like <see cref="_lines"/>.</summary>
    private Marquee[] _playerLines = [];

    // Built with the tree, in the constructor. The centrepiece and the lyric each have their own, because
    // each is taken away by a view that keeps the other.
    private Fader _wordsFade = null!, _scrimFade = null!, _centreFade = null!, _lyricFade = null!;
    private Fader _coverAFade = null!, _coverBFade = null!, _placeholderFade = null!, _labelFade = null!;
    private Fader _lyricFootFade = null!, _lyricTransportFade = null!;

    // The seek bars stand down with the keys. They have their own faders, not the faders of their
    // containers. The column and the lyric foot carry the view's opacity. A bar must be able to leave
    // without taking the words or the record with it.
    private Fader _seekFade = null!, _lyricSeekFade = null!;

    // ── the lyric ──

    /// <summary>How tall one line stands, as a multiple of its own type size. This is fixed. Everything
    /// about the scroll depends on it being fixed. The line a singer is on is found by arithmetic, not by
    /// asking the layout system where anything ended up.</summary>
    private const double LyricLeading = 1.62;

    /// <summary>How quickly the scroll settles on the line being sung. It does not depend on the frame
    /// rate. It is slow enough to read as the sheet being drawn along, rather than snapping a line at a
    /// time.</summary>
    private const double LyricGlide = 7.0;

    /// <summary>How far from the sung line a line fades out completely. Three lines on each side stay
    /// readable around the one being sung, without the screen becoming a page of text.</summary>
    private const int LyricReach = 3;

    private readonly LyricBrushes _lyricInk = new();

    private LyricSheet _sheet = LyricSheet.Empty;
    private TextBlock[] _lyricLines = [];
    private TranslateTransform _lyricShift = new();
    private CancellationTokenSource? _lyricLookup;
    private string? _lyricKey;      // The record that the sheet in memory belongs to.

    /// <summary>Held in a field rather than fetched twice, so the hand-off in <see cref="SetActive"/>
    /// removes the subscription from the same object it added it to.</summary>
    private LyricsService? _lyrics;
    /// <summary>The second voice's row for each line, where the line has one. It is null for most lines.
    /// These are kept beside the lines rather than among them. See the note in BuildLyrics.</summary>
    private TextBlock?[] _asideBlocks = [];

    /// <summary>How much room each of those rows takes. It runs from one (open, under the line it belongs
    /// to) to nought (folded away, as though the line never had one). It is eased rather than switched. See
    /// FoldAsides.</summary>
    private double[] _asideOpen = [];

    /// <summary>Where the light behind the sung line is, and how tall it is. It eases toward where the line
    /// wants it. It is held in a field, not read back from the element. What is on the element is the last
    /// value big enough to be worth writing, which is not the same number.</summary>
    private double _glowH;
    private double _glowY;

    /// <summary>The low end as the light follows it, rather than as the stage reports it.</summary>
    private double _glowBass;

    /// <summary>How fast the light comes up on a beat, and how slowly it lets go. It comes up almost at
    /// once, or it would arrive after the drum. It goes down over about half a second, which turns a
    /// jittery reading into a decay.</summary>
    private const double GlowAttack = 22.0;
    private const double GlowRelease = 4.5;

    /// <summary>How fast a sung second voice folds away. This is quicker than the sheet's own glide. The
    /// fold is a thing finishing, and a slow fold would look like the sheet was still deciding.</summary>
    private const double AsideFold = 9.0;

    private double _lyricScroll;    // where the sheet is, in points, easing toward the sung line
    private int _lyricAt = -2;      // the line lit last frame; -2 so the first pass always paints

    /// <summary>Whether there is a synced sheet for the record that is playing. This is the one view that
    /// can be absent. The host leaves <see cref="PegasusView.Lyrics"/> out of the cycle while this is
    /// false, so a press never lands on an empty screen.</summary>
    public bool HasLyrics => _sheet.IsSynced;

    /// <summary>Raised when <see cref="HasLyrics"/> changes: a sheet arrived, or the record moved on and
    /// took it with it.</summary>
    public event Action? LyricsChanged;

    /// <summary>Whether a lookup is still running. This is true from the moment a record changes until its
    /// answer arrives. It lets "there is no sheet" be told apart from "there is no sheet yet". The first is
    /// a reason to leave the lyric view. The second is not.</summary>
    public bool LyricsPending { get; private set; }

    // ── the transport, which stands down when nothing is happening ──

    private const double TransportIdle = 5.0;
    private double _transportSeen;      // how long since the last pointer move
    private bool _transportUp;
    private Fader _transportFade = null!;

    /// <summary>Every fade in the composition, so they can all be walked on the one tick. Built with the
    /// tree. Nothing is added or removed after the constructor.</summary>
    private Fader[] _faders = [];

    public PegasusChrome()
    {
        InitializeComponent();
        CoverFrame.BorderBrush = _frameBrush;
        BuildPlayerKey();   // The remote's single focus stop for the whole mini player (see PegasusChrome.Remote).

        _wordsFade = new Fader(Words);
        _scrimFade = new Fader(WordScrim);
        _centreFade = new Fader(Centre);
        // The LAYER, not the sheet inside it. The view's dissolve has to take the intro card with the
        // words. The sheet's own opacity underneath is the hush's to write.
        _lyricFade = new Fader(LyricLayer);
        _coverAFade = new Fader(CoverA);
        _coverBFade = new Fader(CoverB);
        _placeholderFade = new Fader(CoverPlaceholder);
        _labelFade = new Fader(Record.Stand.Label);
        _lyricFootFade = new Fader(LyricFoot);
        // The lyric foot's whole row of keys, including the lyric key on its left end (see LyricKeys).
        _lyricTransportFade = new Fader(LyricKeys);
        _queueFade = new Fader(QueuePanel);
        // The drawer's two screens are crossed between inside it (see ShowBrowse). They have their own
        // opacities INSIDE the panel's, the same way the transport does. The panel says whether the drawer
        // is up. These say which of its screens is being looked at. The two multiply, and neither needs to
        // know about the other.
        _queueListFade = new Fader(QueueScroll);
        _browseListFade = new Fader(BrowseScroll);
        _albumListFade = new Fader(AlbumScroll);
        // The wash belongs to the record screen but is not inside it. It lines the whole panel, behind the
        // head as well as the list. So it crosses on its own fader, on the same clock.
        _albumBackFade = new Fader(AlbumBackdrop);
        // The sheet a leftward swipe opens over the drawer (see PegasusChrome.Playlists).
        _sheetFade = new Fader(PlaylistSheet);
        // …and the collection over the room, and the mini player's card at the top of it. The card shows
        // what is playing and its keys, or the app's mark when nothing is playing (see
        // PegasusChrome.Library).
        _libraryFade = new Fader(LibraryLayer);
        _libraryPlayerFade = new Fader(LibraryPlayerContent);
        _playerLogoFade = new Fader(LibraryPlayerLogo);
        _playerPillFade = new Fader(LibraryTransportPill);
        // …and the menu the key in the top left corner opens on home.
        _libraryMenuFade = new Fader(LibraryMenu);
        // …and the screen over everything while a collection is switched to (see PegasusChrome.SideMenu).
        _switchFade = new Fader(SwitchLayer);
        // …and the one record a press on the wall opens. It crosses with the wall rather than over it (see
        // PegasusChrome.Record).
        _recordFade = new Fader(RecordLayer);
        // …and the two screens the collection can show. They cross when the wall's back key goes to home
        // (see PegasusChrome.Home).
        _wallScreenFade = new Fader(WallScreen);
        _homeScreenFade = new Fader(HomeHost);
        // …and the ring that stands in while a slow question is being asked of the collection.
        _libraryBusyFade = new Fader(LibraryBusy);
        _seekFade = new Fader(Seek);
        _lyricSeekFade = new Fader(LyricSeek);
        _transportFade = new Fader(TransportRow);

        // …and radio: its screen, its wheel, its search and the tuner. Only the parts that stay for the
        // life of the chrome are built here (see PegasusChrome.Radio).
        BuildRadio();

        // …and the artists and genres screens (see PegasusChrome.ArtistsGenres).
        BuildArtistsGenres();

        _faders =
        [
            _wordsFade, _scrimFade, _centreFade, _lyricFade,
            _coverAFade, _coverBFade, _placeholderFade, _labelFade,
            _lyricFootFade, _lyricTransportFade, _transportFade,
            _seekFade, _lyricSeekFade, _queueFade,
            _queueListFade, _browseListFade, _albumListFade, _albumBackFade, _sheetFade,
            _libraryFade, _libraryPlayerFade, _playerLogoFade, _playerPillFade, _libraryMenuFade, _switchFade,
            _recordFade, _libraryBusyFade,
            _wallScreenFade, _homeScreenFade,
            _radioScreenFade, _radioWheelFade, _radioSearchFade, _tunerFade,
            _artistsFade, _genresFade, _artistPageFade, _genrePageFade, _playlistsFade, _searchFade,
        ];

        // The sheet is moved by one transform on the whole Canvas, not by a translate for each line. The
        // lines never move relative to one another. Only the page moves.
        LyricHost.RenderTransform = _lyricShift;

        _lines =
        [
            new Marquee(TitleText, TitleHost, TitleShift, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            new Marquee(ArtistText, ArtistHost, ArtistShift, Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            new Marquee(AlbumText, AlbumHost, AlbumShift, Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
        ];

        // The mini player's three lines. They scroll by the same rule inside its card (see
        // PegasusChrome.Library, SizeLibrary). They are left-aligned, as the card writes them.
        _playerLines =
        [
            new Marquee(LibraryTitle, LibraryTitleHost, LibraryTitleShift),
            new Marquee(LibraryArtist, LibraryArtistHost, LibraryArtistShift),
            new Marquee(LibraryAlbum, LibraryAlbumHost, LibraryAlbumShift),
        ];

        // Pointer activity anywhere over the composition brings the transport back. PointerMoved is handled
        // here, not subscribed on each button, so crossing the screen at all counts. It is not marked as
        // handled, so the host's own idle timer still sees it. The height of the stack includes the seek
        // bar and the keys, and those come and go. So what has to be centred is worked out from what layout
        // produced. It is not assumed.
        Words.SizeChanged += (_, _) =>
        {
            CentreWords();
            PushWordBox();
        };

        PointerMoved += (_, _) => WakeTransport();
        PointerEntered += (_, _) => WakeTransport();

        // The drag lives on the LIST, not on the row being dragged. A row cannot hold its own capture while
        // it moves through its siblings (see QueueRow_PointerMoved).
        QueueRows.PointerMoved += QueueList_PointerMoved;
        QueueRows.PointerReleased += QueueList_PointerReleased;
        QueueRows.PointerCaptureLost += QueueList_PointerCaptureLost;

        // The shelf's sleeves are fetched for the rows in view. That depends on where the rows were laid
        // out. The fetch runs when the panel gets its size, as well as after the build and on a scroll. A
        // call queued after the build could run before layout, find rows with no height, and leave every
        // sleeve blank until the first scroll.
        BrowseRows.SizeChanged += (_, _) => PumpBrowseArt();

        Unloaded += (_, _) => SetActive(false);
    }

    /// <summary>The room under this chrome. It supplies the one thing the words need from it: the low end
    /// that it is already measuring to light the room with. This is null for a chrome with no room yet,
    /// such as a machine with no GL or a stage that would not compile. In that case the glow simply rests.
    ///
    /// <para>This is taken as the interface, not the stage itself, so nothing in this control names a type
    /// from the GL assembly. See <see cref="IPegasusRoom"/>. A method that names a type from that assembly
    /// cannot run at all where the assembly is missing. The words, the record and the cover should not fail
    /// because the room behind them did.</para></summary>
    // This is internal because IPegasusRoom is. The interface keeps the GL assembly out of every method
    // that does not set up the stage. Nothing outside this assembly has one to hand over.
    internal void SetStage(IPegasusRoom? stage)
    {
        _stage = stage;
        // A room that has only just been set up has missed every layout that has already happened.
        PushWordBox();
    }

    private IPegasusRoom? _stage;

    /// <summary>
    /// Tells the room where this chrome is writing, so the mirror ball hanging in it can keep its light off
    /// the words. See <see cref="IPegasusRoom.SetWordBox"/> for why the room cannot work this out itself.
    ///
    /// <para>This uses where the words ACTUALLY landed, not where they were asked to go. The column's
    /// height depends on a title that may take two lines, and on a seek bar and a row of keys that come and
    /// go. That is layout's answer, not this method's. So the box is pushed from <see cref="Relayout"/>,
    /// and again whenever the column resizes. A box that has not been measured yet is no box at all, rather
    /// than a guess.</para>
    ///
    /// <para>The box is in the chrome's own coordinates, which are also the room's. The queue drawer slides
    /// this composition, and the host slides the GL surface underneath by exactly the same amount (see <see
    /// cref="ShiftChanged"/>). So a box measured here still lands where the words are.</para>
    /// </summary>
    private void PushWordBox()
    {
        if (_stage is null)
            return;

        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        // The bare room writes nothing anywhere, so the ball has the whole of it.
        if (_view == PegasusView.Room)
        {
            _stage.SetWordBox(0, 0, 0, 0);
            return;
        }

        // The lyric is not a block of words in one place. It is a sheet that travels through the middle of
        // the screen, with a bar and a row of keys along its foot. So the box covers its whole column, from
        // top to bottom, not just wherever the line being sung has got to.
        //
        // Beside the record it is the same. The sheet is the moving part, and the record's lines are short
        // and sit under a cover that the ball already keeps off.
        if (_view.IsLyric())
        {
            var (sheetLeft, sheetWidth) = SheetColumn(w, h);
            _stage.SetWordBox(sheetLeft / w, 0.0, (sheetLeft + sheetWidth) / w, 1.0);
            return;
        }

        // In the spread view, the column is centred on the window's height at the left margin. Drawn in, it
        // hangs from under the cover in the middle. Both come from what Relayout placed. So there is one
        // answer to where the words are, and this is not a second opinion about it.
        double width = Words.ActualWidth, height = Words.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            _stage.SetWordBox(0, 0, 0, 0);
            return;
        }

        double left = _view == PegasusView.Spread ? Words.Margin.Left : (w - width) / 2;
        double top = _view == PegasusView.Spread ? (h - height) / 2 : Words.Margin.Top;

        // …and wherever CentreWords has since pushed the stack. That is a transform, so layout does not
        // know about it. It is zero outside the spread composition, where CentreWords does not run.
        top += WordsShift.Y;

        _stage.SetWordBox(left / w, top / h, (left + width) / w, (top + height) / h);
    }

    /// <summary>The transport the seek bar reads, the buttons drive, and the spin follows.</summary>
    public void SetTransport(RPaMpModel amp)
    {
        _amp = amp;
        Seek.DataContext = amp;
        Seek.SetRemainInset(0);   // The countdown sits on the right end of the bar.
        LyricSeek.DataContext = amp;
        LyricSeek.SetRemainInset(0);
        // The mini player over the collection has its own bar and its own keys. The same transport drives
        // both (see PegasusChrome.Library).
        LibrarySeek.DataContext = amp;
        LibrarySeek.SetRemainInset(0);
        LibraryTransport.DataContext = amp;
        LyricTransport.DataContext = amp;
        // This is the ROW, not the keys inside it. The volume slider on its right end binds too, and the
        // keys inherit this anyway.
        TransportRow.DataContext = amp;

        // …and the cast keys and the line at the top follow where the music plays (see PegasusChrome.Cast).
        WatchCast(amp);
    }

    /// <summary>Runs the spin only while the visualizer is up.</summary>
    public void SetActive(bool on)
    {
        if (on == _running)
            return;
        _running = on;

        if (on)
        {
            _lastTick = 0;
            _clock.Restart();
            CompositionTarget.Rendering += OnRendering;
        Services.Shell.FrameWatch.Began("pegasus-chrome");

            // The disc is assumed to be out, not eased into. Opening the visualizer on a playing record
            // should show the disc already out, not slide it out as if the track had just started.
            _discOut = _amp?.Deck is not null ? 1 : 0;
            ApplyDisc();

            // The controls come up on arrival, then go away on the usual count. This matches the pin and
            // close buttons that the host shows. Without it, the controls stay hidden until the pointer
            // moves, which looks like a screen with no way to control it.
            WakeTransport();

            // In step with the track pane for as long as this screen is up. A sheet swapped there is the
            // reader's judgement about the record that is playing. It belongs on both screens at once, not
            // on whichever one they pressed. See OnLyricReplaced.
            _lyrics ??= App.Services.GetRequiredService<LyricsService>();
            _lyrics.Replaced += OnLyricReplaced;

            // Only now is anything asked of LRCLIB (see RefreshLyrics). The key is cleared first, so a
            // record that was already playing when the visualizer opened is looked up, rather than skipped
            // as unchanged.
            _lyricKey = null;
            RefreshLyrics();

            // The collection the window has open, which may have changed while this was down (see
            // PegasusChrome.Follow).
            ListenForCollection(true);
        }
        else
        {
            ListenForCollection(false);
            _clock.Stop();
            CompositionTarget.Rendering -= OnRendering;
        Services.Shell.FrameWatch.Ended("pegasus-chrome");

            // The list goes with the screen, and its subscriptions go with it. A panel nobody can see has
            // no reason to rebuild itself every time a record ends. This runs before the faders settle, so
            // the fade it starts lands with the rest of them.
            SetQueueOpen(false);

            // The slide finishes here, by hand, because it does not use a Fader. The tick that drives it is
            // switched off on the next line. If the slide were left half done, the push it shares a figure
            // with would still be on the composition and on the room behind it. Then the next time the
            // visualizer opened, the record would sit somewhere off to the right, with nothing beside it to
            // explain why.
            _queueSlide = 0;
            _pushSlide = 0;
            ApplyQueueShift();

            // The fades run on that tick, so anything still moving lands now. It is not left at an opacity
            // that nobody will come back to finish. Three passes are made. A fade that lands can start the
            // next part of a swap (the words go out, the arrangement changes, the words come back), and
            // that part has no tick either.
            for (int pass = 0; pass < 3; pass++)
                foreach (var fade in _faders)
                    fade.Settle();

            // A lookup still in flight belongs to a screen that nobody is looking at any more.
            _lyricLookup?.Cancel();
            _lyricLookup = null;

            // The same is true of a swap made after this. The service outlives every screen in the app, so
            // a subscription left on it would keep this control alive.
            if (_lyrics is not null)
                _lyrics.Replaced -= OnLyricReplaced;
        }
    }

    // ── the two compositions ──

    /// <summary>
    /// Moves to one of the three compositions (see <see cref="PegasusView"/>).
    ///
    /// <para>The words change arrangement in the dark. They fade out, the layout is redone while none of
    /// them is on screen, and they fade back in where they now belong. The alternative is watching a column
    /// of text slide across a picture. No path between those arrangements looks like anything but a
    /// mistake.</para>
    ///
    /// <para>The centrepiece does NOT work this way, and the difference is deliberate. It only ever leaves
    /// or arrives. Between the first two views the record stays exactly where it is. So it is dissolved
    /// straight to where it belongs, across the whole swap. Fading it out and back in on every press would
    /// take the record off the screen and put it back in the same place. That reads as a blink for no
    /// reason.</para>
    ///
    /// <para>The stage behind is told at the same moment, and takes its own half-second over the spectrum.
    /// So the two halves of the change overlap.</para>
    ///
    /// <para><paramref name="immediate"/> is for a composition that is SHOWN in this state, rather than
    /// changed into. There is nothing to dissolve from when the screen was black a moment ago.</para>
    /// </summary>
    public void SetView(PegasusView view, bool immediate = false)
    {
        if (immediate)
        {
            _view = _viewTo = view;
            _swapping = false;
            PlaceSheetFor(view);
            Relayout();
            _wordsFade.Set(WordsOpacity);
            _scrimFade.Set(ScrimOpacity);
            _centreFade.Set(CentreOpacity);
            _lyricFade.Set(LyricOpacity);
            _lyricFootFade.Set(LyricFootOpacity);
            _besideOn = view == PegasusView.LyricsBeside;
            RetargetBeside(glide: false);
            return;
        }

        if (_viewTo == view)
            return;

        // The sheet goes where the view it is fading into reads it, before anything fades.
        PlaceSheetFor(view);

        // Between the drawn-in composition and the lyric beside it, the change is a slide, not a dissolve.
        // This only applies from a view that is laid out. So a dissolve already under way finishes as a
        // dissolve. When that dissolve lands, it comes back through here if the target has moved on.
        if (!_swapping && IsSlide(_view, view))
        {
            _view = _viewTo = view;
            Relayout();
            _lyricFade.To(LyricOpacity, BesideMs, null);
            _lyricFootFade.To(LyricFootOpacity, BesideMs, null);

            // This is set after the layout above, never before it. Relayout settles the slide on the aim it
            // is given, and this aim has to reach it as a slide.
            _besideOn = view == PegasusView.LyricsBeside;
            RetargetBeside(glide: true);
            return;
        }

        _viewTo = view;

        // Whatever else changes, the record ends up where the target puts it. A dissolve out of the lyric
        // beside the record carries the record back to the middle while the words are away. From a view
        // with no record on screen (the bare room, the lyric alone, the collection, and the greeting, which
        // are all the bare room to this control), the record goes straight to its place. A record fading in
        // is arriving. If it slid in from the middle as it arrived, that would be two moves for one.
        _besideOn = view == PegasusView.LyricsBeside;
        RetargetBeside(glide: CentreOpacity != 0);

        // A swap already under way picks up the new target when it reaches the middle.
        if (!_swapping)
            BeginSwap();
    }

    /// <summary>Where each part of the composition stands in the view currently laid out.</summary>
    private double WordsOpacity => _view is PegasusView.Room or PegasusView.Lyrics ? 0 : 1;
    private double ScrimOpacity => _view == PegasusView.Spread ? 1 : 0;
    private double CentreOpacity => _view is PegasusView.Room or PegasusView.Lyrics ? 0 : 1;
    private double LyricOpacity => _view.IsLyric() ? 1 : 0;

    /// <summary>The lyric's own foot, its seek bar and keys. These show for the lyric alone and for the bare
    /// room, the two views that draw no column of words (see <see cref="FootDrawn"/>). Beside the record,
    /// the column's own bar and keys are on screen, and they stay where they are.</summary>
    private double LyricFootOpacity => FootDrawn(_view) ? 1 : 0;

    /// <summary>Whether a view draws the foot (the seek bar and keys along the bottom) in place of the
    /// column's own. The lyric alone has no column. Neither has the bare room, and without the foot it would
    /// have no keys at all, not even the layout key that leads out of it.</summary>
    private static bool FootDrawn(PegasusView view) => view is PegasusView.Lyrics or PegasusView.Room;

    private void BeginSwap()
    {
        _swapping = true;

        // The record and the scrim are told once, against the TARGET. They cross the whole swap in one
        // move. Neither of them is rearranged. Each is only shown or hidden.
        var target = _viewTo;
        _centreFade.To(target is PegasusView.Room or PegasusView.Lyrics ? 0 : 1, SwapOutMs + SwapInMs, null);
        _scrimFade.To(target == PegasusView.Spread ? 1 : 0, SwapOutMs + SwapInMs, null);
        _lyricFade.To(target.IsLyric() ? 1 : 0, SwapOutMs + SwapInMs, null);
        _lyricFootFade.To(FootDrawn(target) ? 1 : 0, SwapOutMs + SwapInMs, null);

        _wordsFade.To(0, SwapOutMs, () =>
        {
            // This is the middle of the dissolve. None of the words is on screen, so this is the one moment
            // the arrangement can change without anyone seeing it change.
            _view = _viewTo;
            Relayout();
            _wordsFade.To(WordsOpacity, SwapInMs, () =>
            {
                _swapping = false;

                // If it is pressed again while it is crossing, it goes on to wherever the target now
                // points. This goes through SetView, so a target that is now one slide away is slid to, not
                // dissolved to.
                if (_viewTo != _view)
                {
                    var next = _viewTo;
                    _viewTo = _view;
                    SetView(next);
                }
            });
        });
    }

    /// <summary>Whether going from one view to the other is the slide, rather than the dissolve. The slide
    /// is used between the drawn-in composition and the lyric beside the record, in either
    /// direction.</summary>
    private static bool IsSlide(PegasusView from, PegasusView to) =>
        (from, to) is (PegasusView.Centred, PegasusView.LyricsBeside)
                   or (PegasusView.LyricsBeside, PegasusView.Centred);

    /// <summary>
    /// Aims the record and its lines at where they belong now: <see cref="Beside"/>'s Shift while <see
    /// cref="_besideOn"/> is set, and the middle otherwise.
    /// </summary>
    /// <remarks>
    /// <para><paramref name="glide"/> is for an aim the eye should follow. Examples are a press of the
    /// lyric key, and the next song's sheet coming in beside the record (BuildLyrics). Without it, the
    /// record is put there at once. That is what a resize, or a view being SHOWN rather than changed into,
    /// needs.</para>
    ///
    /// <para><b>A slide already under way is never stopped by an aim that does not glide.</b> Relayout asks
    /// for an aim on every pass. A pass can run in the middle of a press (SetView lays out before it aims),
    /// and in the middle of a dissolve. So a non-gliding aim only moves the slide's END, and the slide
    /// carries on to the corrected place. A gliding aim restarts the slide from wherever the record is.
    /// This is what turns a second press round where it stands.</para>
    /// </remarks>
    private void RetargetBeside(bool glide)
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        var target = _besideOn && w > 0 && h > 0 ? Beside(w, h).Shift : 0;

        if (_besideSeconds > 0)
        {
            if (!glide)
            {
                _besideToX = target;
                return;
            }

            if (Math.Abs(target - _besideToX) < 0.5)
                return;
        }
        else if (Math.Abs(target - _besideX) < 0.5)
        {
            _besideX = target;
            ApplyBeside();
            return;
        }

        if (!glide)
        {
            _besideX = target;
            ApplyBeside();
            return;
        }

        _besideFromX = _besideX;
        _besideToX = target;
        _besideElapsed = 0;
        _besideSeconds = BesideMs / 1000.0;
    }

    /// <summary>One frame of the slide, on the app's one move curve (PaneGlide.EaseAt).</summary>
    private void TickBeside(double dt)
    {
        if (_besideSeconds <= 0)
            return;

        _besideElapsed += dt;
        var t = Math.Clamp(_besideElapsed / _besideSeconds, 0, 1);
        _besideX = _besideFromX + ((_besideToX - _besideFromX) * Main.PaneGlide.EaseAt(t));
        if (t >= 1)
        {
            _besideX = _besideToX;
            _besideSeconds = 0;
        }

        ApplyBeside();
    }

    /// <summary>Writes the slide: the record and its three lines, moved by the one figure.</summary>
    private void ApplyBeside()
    {
        if (CentreBeside.X != _besideX)
            CentreBeside.X = _besideX;
        if (HeadingBeside.X != _besideX)
            HeadingBeside.X = _besideX;
    }

    /// <summary>
    /// Puts the sheet in the column that the view being moved INTO reads it in: beside the record, or
    /// across the middle. Nothing changes for a view without the lyric, so a sheet on its way out is not
    /// moved while it fades. A change re-wraps the sheet for the new width (LayoutLyrics), so it is done
    /// once per change of view, not per frame.
    /// </summary>
    private void PlaceSheetFor(PegasusView target)
    {
        if (!target.IsLyric())
            return;

        var beside = target == PegasusView.LyricsBeside;
        if (beside == _lyricBeside)
            return;

        _lyricBeside = beside;
        LayoutLyrics();
    }

    /// <summary>
    /// The column the sheet is read through, and its left edge, for a window this size: across the middle
    /// (<see cref="LyricColumn"/>), or beside the record (<see cref="Beside"/>).
    /// </summary>
    private (double Left, double Width) SheetColumn(double w, double h)
    {
        if (!_lyricBeside)
        {
            var column = LyricColumn(w);
            return ((w - column) / 2, column);
        }

        var beside = Beside(w, h);
        return (beside.Left, beside.Width);
    }

    /// <summary>
    /// Where the record and the lyric stand beside each other, for a window this size: how far the record
    /// is moved from the middle, and the lyric's column.
    /// </summary>
    /// <remarks>
    /// <para>The record, the gap and the column are one group, centred on the screen. So the space left of
    /// the cover and the space right of the column are the same. The group runs from the cover's left edge
    /// to the far edge of the record's shadow (the disc all the way out of its sleeve). Then comes <see
    /// cref="PegasusLayout.BesideGapFraction"/> of the cover, then the column.</para>
    ///
    /// <para>The column is as wide as the sheet's longest line (<see cref="_sheetWidest"/>), so the text's
    /// right edge is the column's edge. A column wider than the words would be a margin nobody can see. The
    /// column is never wider than <see cref="PegasusLayout.BesideLyricMax"/>. It also leaves at least <see
    /// cref="PegasusLayout.BesideLyricMargin"/> of the width on either side. A longer line wraps. The lines
    /// are centred in the column, so a line shorter than the longest leaves the same air at both of its
    /// ends, inside the group.</para>
    ///
    /// <para>The record's figures are the ones Relayout lays it out with (<see cref="CoverSide"/>, <see
    /// cref="ShapeOf"/>). They are worked out from the window here, not read back from it. So this is right
    /// whether or not a layout has run since the window last changed. The cover's left edge is the group's
    /// left edge, even where a long title under it is wider. On most records the title is not wider.</para>
    /// </remarks>
    private (double Shift, double Left, double Width) Beside(double w, double h)
    {
        var side = CoverSide(w, h);

        // From the cover's centre to the far edge of the record's shadow.
        var reach = (side * PegasusLayout.DiscOffsetFraction) + (ShapeOf(side).Host / 2);
        var gap = side * PegasusLayout.BesideGapFraction;

        var most = Math.Min(
            PegasusLayout.BesideLyricMax,
            w - (2 * w * PegasusLayout.BesideLyricMargin) - (side / 2) - reach - gap);

        // A hair over the longest line, so that it does not wrap on the engine's own rounding (see
        // WrapSlack). Before a sheet has been measured, the whole room is used.
        var column = _sheetWidest > 0 ? Math.Min(_sheetWidest + (2 * WrapSlack) + 2, most) : most;
        column = Math.Max(PegasusLayout.BesideLyricLeast, column);

        var group = (side / 2) + reach + gap + column;
        var centre = ((w - group) / 2) + (side / 2);
        return (centre - (w / 2), centre + reach + gap, column);
    }

    /// <summary>
    /// One element's opacity, eased toward a target on this control's own render tick.
    ///
    /// <para><b>Numbers, not a Storyboard. This avoids a flicker that a storyboard per fade caused.</b>
    /// Three separate problems showed up with a storyboard per fade, and each one was visible on the
    /// transport and the seek bar.</para>
    ///
    /// <para>A <c>DoubleAnimation</c> with no <c>From</c> starts from the property's value as Uno reads it.
    /// Uno returns the BASE value, not the live one, while another animation is on the property. (The
    /// header's RPM mark is written around the same problem, see <c>ArtDisplay.AnimateRpmOpacity</c>.) So a
    /// row whose XAML opacity is 0 "faded" out from 0. It simply vanished. A container whose base is 1
    /// flashed to full white before it faded away.</para>
    ///
    /// <para>Stopping the old storyboard has the same problem from the other end. The animated value is
    /// kept in one slot per property. Stopping the outgoing animation clears the slot that the incoming one
    /// is already writing, and the base value shows for a frame. Starting the new one first does not help,
    /// because it is the same slot.</para>
    ///
    /// <para>A pointer crossing the screen also wakes the transport on EVERY move. So the row was stopped
    /// and restarted sixty times a second, and each restart flashed its base value. That was the
    /// flicker.</para>
    ///
    /// <para>Driven off the tick, the live value is simply known. A redirect in the middle of a fade
    /// carries on from where the last one had got to. A repeat of a fade already under way is left alone.
    /// Nothing is ever stopped, held or reverted. It costs no more than the storyboards did. Those were
    /// dependent animations, so the framework was already writing Opacity a frame at a time on this same
    /// thread.</para>
    /// </summary>
    private sealed class Fader(FrameworkElement element)
    {
        private double _value = element.Opacity;
        private double _from = element.Opacity;
        private double _to = element.Opacity;
        private double _elapsed;    // seconds into the move
        private double _seconds;    // how long it takes; 0 once it has landed
        private Action? _then;

        /// <summary>Eases to <paramref name="to"/> over <paramref name="ms"/>, calling
        /// <paramref name="then"/> once it lands.</summary>
        public void To(double to, int ms, Action? then)
        {
            // Already there, or already on its way there, with nobody waiting on it. This is the pointer
            // case: a fade restarted on every move never arrives anywhere.
            if (then is null && _to == to && (_seconds > 0 || _value == to))
                return;

            _from = _value;
            _to = to;
            _elapsed = 0;
            _seconds = Math.Max(1, ms) / 1000.0;
            _then = then;
        }

        /// <summary>Drops any move in progress and writes the value directly. This is for a state that is
        /// ASSUMED rather than moved into, where there is nothing to dissolve from.</summary>
        public void Set(double to)
        {
            _seconds = 0;
            _then = null;
            _from = _to = to;
            Write(to);
        }

        /// <summary>Lands wherever it was heading, at once. This is for a screen that is going away. The
        /// tick stops with the screen, so a fade left half done would be an opacity that nobody comes back
        /// to finish.</summary>
        public void Settle()
        {
            if (_seconds <= 0)
                return;
            _seconds = 0;
            Write(_to);
            Land();
        }

        /// <summary>One frame of the move.</summary>
        public void Tick(double dt)
        {
            if (_seconds <= 0)
                return;

            _elapsed += dt;
            var t = Math.Clamp(_elapsed / _seconds, 0, 1);
            Write(_from + (_to - _from) * Ease(t));

            if (t >= 1)
            {
                _seconds = 0;
                Land();
            }
        }

        /// <summary>Runs what was waiting for the landing. The slot is cleared FIRST, because the callback
        /// may start the next fade on this same fader. The words coming back in from the middle of a swap
        /// do exactly that.</summary>
        private void Land()
        {
            var then = _then;
            _then = null;
            then?.Invoke();
        }

        private void Write(double v)
        {
            _value = v;
            if (element.Opacity != v)
                element.Opacity = v;
        }

        /// <summary>The quadratic ease-in-out that the storyboards use, written out. It is public so that a fade this class does not drive, such as
        /// the lyric's hush (which works straight off the playhead), bends the same way as every fade that
        /// does.</summary>
        public static double Ease(double t) => t < 0.5 ? 2 * t * t : 1 - 2 * (1 - t) * (1 - t);
    }

    // ── what is playing ──

    /// <summary>
    /// Dresses the composition from the header's republished chrome: the words, the cover, and the record
    /// itself. Nothing is read from a file or decoded here. Each of these is the single decode the header
    /// has already done. That is the reason the chrome is republished at all.
    ///
    /// <para>Every write is guarded. This also runs while a track plays, because the header republishes on
    /// its own schedule. Setting identical text still costs a measure. That is a layout pass, which the
    /// stage next door pays for in dropped frames.</para>
    /// </summary>
    public void Apply(ArtDisplay art)
    {
        // The title is masked on the way in, like the sheet below it (see TitleCensor). The chrome carries
        // the song as it was tagged. This screen is the last step before it is drawn.
        SetText(_lines[0], TitleCensor.Shown(art.TrackTitle));
        SetText(_lines[1], art.TrackArtist);
        SetText(_lines[2], TitleCensor.Shown(art.TrackAlbum));

        // The same two names again, for the card that stands in for the sheet through a long intro. These
        // are plain blocks, not Marquees. The card is centred on the whole screen with no column to clip
        // it, so a long title wraps where a Marquee would have to scroll. Guarded like the rest, because
        // this runs on every republish and an identical string still costs the measure.
        SetPlain(LyricIntroTitle, TitleCensor.Shown(art.TrackTitle));
        SetPlain(LyricIntroArtist, art.TrackArtist);

        // …and the mini player over the collection. It asks the same question of the same chrome, on the
        // one screen that is not a picture of the record (see PegasusChrome.Library). Title, artist and
        // album, in that order. A line with nothing on it is taken out, rather than left as a gap.
        //
        // The lines scroll, in a card that stays one size (see SizeLibrary). An empty line takes its HOST
        // out, not just the words. The host's height is reported (Marquee.Remeasure), so a host left up
        // around nothing would be a gap.
        SetText(_playerLines[0], TitleCensor.Shown(art.TrackTitle));
        SetText(_playerLines[1], art.TrackArtist);
        SetText(_playerLines[2], TitleCensor.Shown(art.TrackAlbum));
        LibraryArtistHost.Visibility = Shown(art.TrackArtist);
        LibraryAlbumHost.Visibility = Shown(art.TrackAlbum);
        ApplyLibraryPlayerArt(art);

        ShowCover(art.TrackCover, art.TrackGround, art.TrackCoverWidth, art.TrackCoverHeight,
                  art.TrackCoverFills);

        // The record's own art: a photographed disc replaces the whole drawn record. Without one, the drawn
        // record wears the cover on its label. These are the two cases the header handles.
        var disc = art.TrackDisc?.Picture;
        if (!ReferenceEquals(Record.Stand.CdPicture, disc))
            Record.Stand.ShowCd(disc, wholeDisc: art.TrackDisc is { CoversRecord: true });


        // The record on the deck may have changed with it. If it has, any sheet in memory belongs to the
        // previous record. This is cheap when nothing moved, because RefreshLyrics compares keys and
        // returns.
        RefreshLyrics();

        Press(art.TrackPressing);

        // The frame is the cover's dominant colour, held back to about a third, so that it frames the cover
        // rather than drawing attention to itself. If the cover has no colour, the frame is a plain dark
        // edge.
        var tint = art.TrackTint;
        var wanted = tint is { } c
            ? Color.FromArgb(0xFF, c.R, c.G, c.B)
            : Color.FromArgb(0xFF, 0x12, 0x16, 0x1E);
        if (_frameBrush.Color != wanted)
            _frameBrush.Color = wanted;

        // A new line of words is a new line of travel. It is measured afresh and wound back, so that it
        // does not begin half way through the last line's scroll.
        static void SetText(Marquee line, string? value)
        {
            var text = value ?? "";
            if (line.Text.Text == text)
                return;
            line.Text.Text = text;
            line.Remeasure();
            line.Rewind();
        }

        // A block that neither travels nor is clipped has nothing to wind back. Only the write itself is
        // worth guarding.
        static void SetPlain(TextBlock block, string? value)
        {
            var text = value ?? "";
            if (block.Text != text)
                block.Text = text;
        }

        static Visibility Shown(string? line) =>
            string.IsNullOrWhiteSpace(line) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The mini player's cover: the header's small square where there is one, and the big one where there
    /// is not. A station's logo is fitted whole, inset from the edges, on the ground the header made for
    /// it. The record view draws the same logo the same way. It is written only on a change, because this
    /// runs on every republish of the header.
    /// </summary>
    private void ApplyLibraryPlayerArt(ArtDisplay art)
    {
        ImageSource? cover = art.TrackCoverSquare ?? art.TrackCover;
        if (!ReferenceEquals(LibraryPlayerArt.Source, cover))
            LibraryPlayerArt.Source = cover;
        if (!ReferenceEquals(LibraryPlayerArtGround.Source, art.TrackGround))
            LibraryPlayerArtGround.Source = art.TrackGround;

        var stretch = art.TrackCoverFills ? Stretch.UniformToFill : Stretch.Uniform;
        if (LibraryPlayerArt.Stretch != stretch)
            LibraryPlayerArt.Stretch = stretch;

        var inset = art.TrackCoverFills ? 0 : PlayerArtEdge * _playerScale * 0.12;   // the card's own scale
        if (Math.Abs(LibraryPlayerArt.Margin.Left - inset) > 0.5)
            LibraryPlayerArt.Margin = new Thickness(inset);

        var placeholder = cover is null ? Visibility.Visible : Visibility.Collapsed;
        if (LibraryPlayerArtPlaceholder.Visibility != placeholder)
            LibraryPlayerArtPlaceholder.Visibility = placeholder;
    }

    /// <summary>Presses this sleeve's record, through the same cache that the header's disc comes from. So
    /// a second copy of a colour already on screen costs nothing. This is fire and forget. Until it lands,
    /// the record keeps the colour it had, which is a better answer than a blank spindle.</summary>
    private async void Press(Color? hue)
    {
        if (_pressed && _pressedHue == hue)
            return;
        _pressed = true;
        _pressedHue = hue;

        var source = hue is { } colour
            ? await VinylPressing.PressAsync(colour)
            : await VinylPressing.PressBlackAsync();

        // A track may have changed while the press was in progress. Only the current colour is applied.
        if (source is not null && _pressedHue == hue)
            Record.Stand.Vinyl.Source = source;
    }

    // ── the cover ──

    /// <summary>How long one cover takes to become the next. It is slow enough to read as a dissolve,
    /// rather than as a change that has already happened by the time the eye gets there.</summary>
    private const int CoverFadeMs = 900;

    /// <summary>
    /// How long an empty cover is given to turn into the next one instead.
    ///
    /// <para>This prevents a flash when the track changes. Skipping a track empties the cover for as long as
    /// it takes the next one to be found and decoded. The sleeve behind it is opaque, so blanking the
    /// moment it goes null would put a black square on screen between two pictures. Held back this long,
    /// the usual case never blanks at all. The next cover arrives inside the grace period and crosses
    /// straight from the one before it. Only a record that really has no artwork is still empty when the
    /// grace runs out, and that one fades to the mark properly.</para>
    /// </summary>
    private static readonly TimeSpan CoverGrace = TimeSpan.FromMilliseconds(700);

    private bool _coverOnA;
    private BitmapImage? _coverWanted;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _coverGrace;

    /// <summary>
    /// Crosses to a new cover. With null, and only once the grace above has run out, it goes back to the
    /// "no artwork" mark. Puts a cover up and, where it is a mark rather than a sleeve, the ground it
    /// stands on.
    /// </summary>
    /// <param name="ground">The made picture behind a transparent logo, or null for a sleeve. See
    /// ArtDisplay.TrackGround.</param> <param name="width">The cover's own pixels, so a mark is drawn at
    /// its own size instead of being enlarged to fill the square. Zero where unknown, and then it fills as
    /// it always did.</param> <param name="fills">Whether the picture fills this square or is fitted whole
    /// in it. This is the header's own answer (ArtDisplay.TrackCoverFills), and it is never worked out
    /// again here. A ground means fitted. A station's wide logo is fitted too, because it has a background
    /// of its own and would lose its name to the crop.</param>
    private void ShowCover(BitmapImage? cover, BitmapImage? ground = null, int width = 0, int height = 0,
                           bool fills = true)
    {
        // The frame this square is. A sleeve fills it. Anything fitted stands at its own size, held off the
        // edges, so that what is behind it can be seen on all four sides. These are the same rules the
        // header draws by. See ArtDisplay.Cover's Frame, where the argument is written out.
        FrameLayers(whole: !fills, width, height);

        CoverGround.Source = ground;
        CoverGround.Opacity = ground is not null ? 1 : 0;

        // A sleeve handed over by the screen the record was played from (see HandOver) stays up while the
        // header has nothing for that record. Once it stops standing with no cover to follow it, the square
        // is cleared the same way an empty cover is.
        if (_standIn is not null && !StandInHolds(cover is not null) && cover is null)
        {
            _coverWanted = null;
            StartCoverGrace();
            return;
        }

        if (ReferenceEquals(_coverWanted, cover))
            return;
        _coverWanted = cover;

        if (cover is null)
        {
            if (_standIn is not null)
                return;   // The stand-in is still up. See above.
            StartCoverGrace();
            return;
        }

        _coverGrace?.Stop();
        CrossTo(cover, CoverFadeMs);
    }

    /// <summary>How the two cover layers sit in the square. They either fill it (a sleeve), or they are
    /// fitted whole at their own size and held off the edges (a mark).</summary>
    private void FrameLayers(bool whole, int width, int height)
    {
        foreach (var layer in new[] { CoverA, CoverB })
        {
            layer.Stretch = whole ? Stretch.Uniform : Stretch.UniformToFill;
            layer.MaxWidth = whole && width > 0 ? width : double.PositiveInfinity;
            layer.MaxHeight = whole && height > 0 ? height : double.PositiveInfinity;
            layer.Margin = new Thickness(whole ? MarkInset : 0);
        }
    }

    private void StartCoverGrace()
    {
        _coverGrace ??= DispatcherQueue.CreateTimer();
        _coverGrace.Interval = CoverGrace;
        _coverGrace.IsRepeating = false;
        _coverGrace.Tick -= OnCoverGrace;
        _coverGrace.Tick += OnCoverGrace;
        _coverGrace.Stop();
        _coverGrace.Start();
    }

    /// <summary>Brings <paramref name="cover"/> up over whatever the square holds, over <paramref
    /// name="fadeMs"/>. With zero, it appears at once. That is used for a handed-over sleeve, which is put
    /// up before the composition is.</summary>
    private void CrossTo(ImageSource cover, int fadeMs)
    {
        var incoming = _coverOnA ? CoverB : CoverA;
        var outgoing = _coverOnA ? CoverA : CoverB;
        _coverOnA = !_coverOnA;

        // Only ONE layer moves. The old cover stays at full strength underneath, and the new one is brought
        // up over the top of it. That difference is what makes a dissolve, and not a blink.
        //
        // Fading both layers at once looks like the obvious way to cross between two pictures, but it is
        // wrong over an opaque sleeve. Halfway through, each layer is at half opacity. Together they only
        // cover three quarters of the square, so a quarter of the black behind shows through. The picture
        // darkens on its way to the next one. Also, if the new bitmap needs a moment before it draws
        // anything, the old one has already gone, and there is only sleeve for that time. Held underneath,
        // the old cover covers exactly that gap.
        //
        // The change goes through the fader, never by writing Opacity directly. A finished storyboard keeps
        // HOLDING the property it animated. So a plain write to Opacity is ignored while one is still on
        // it. That is how a layer left at 1 by an earlier cross could come back as the incoming one, refuse
        // to go back to 0, and make the next cover appear with no dissolve at all. Set() stops the old
        // animation first, so the value takes.
        if (outgoing.Source is not null)
            Fade(outgoing).Set(1);   // A swap caught mid-cross snaps the old cover up, rather than letting it dip.

        Fade(incoming).Set(0);
        incoming.Source = cover;
        Canvas.SetZIndex(incoming, 1);
        Canvas.SetZIndex(outgoing, 0);

        if (fadeMs <= 0)
        {
            Fade(incoming).Set(1);
            Fade(outgoing).Set(0);
            outgoing.Source = null;
            _placeholderFade.Set(0);
            _labelFade.Set(1);
            Record.Stand.Label.Source = cover;
            return;
        }

        // The old layer is emptied only once the new one is all the way in, so no layer is left holding a
        // picture behind the live one.
        Fade(incoming).To(1, fadeMs, () =>
        {
            Fade(outgoing).Set(0);
            outgoing.Source = null;
        });
        _placeholderFade.To(0, fadeMs, null);

        // The label wears the same picture and arrives with it. It does not snap under a sleeve that is
        // still dissolving above it. It always starts from zero, as the header's own label does. Sliding a
        // new bitmap under a layer that is already opaque swaps the art in a single frame. A fade to a
        // value the layer is already at is no fade at all.
        _labelFade.Set(0);
        Record.Stand.Label.Source = cover;
        _labelFade.To(1, fadeMs, null);
    }

    /// <summary>How far a mark is held off this square's edges. This is larger than the header's inset,
    /// because this square is the one picture on a full screen, rather than a tile beside a
    /// title.</summary>
    private const double MarkInset = 34;

    private void OnCoverGrace(object? sender, object e)
    {
        _coverGrace?.Stop();
        if (_coverWanted is not null)
            return;   // One arrived after all, and it has already been crossed to.
        if (StandInHolds(headerHasCover: false))
            return;   // A sleeve was handed over meanwhile, and its record is the one playing.

        // The mark comes up UNDER the cover, not after it, so the square is never empty between the two.
        // This is the same reason as for the cross above.
        _placeholderFade.To(1, CoverFadeMs, null);
        Fade(CoverA).To(0, CoverFadeMs, () => CoverA.Source = null);
        Fade(CoverB).To(0, CoverFadeMs, () => CoverB.Source = null);
        _labelFade.To(0, CoverFadeMs, () => Record.Stand.Label.Source = null);
    }

    private Fader Fade(Image layer) => ReferenceEquals(layer, CoverA) ? _coverAFade : _coverBFade;

    // ── the spin ──

    private void OnRendering(object? sender, object e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastTick;
        _lastTick = now;

        // A frame that took a quarter of a second (because the app was busy, or the window was hidden) must
        // not move the record by a quarter of a turn.
        if (dt <= 0)
            return;
        dt = Math.Min(dt, 0.1);

        // Every fade in the composition is ticked first. A fade that lands can re-lay the words, in the
        // middle of a view swap. The walk below should read the arrangement the fade left, not the one
        // before it.
        foreach (var fade in _faders)
            fade.Tick(dt);

        TickBeside(dt);   // …and the record sliding beside the lyric, or back
        Marquee.Walk(_lines, dt);

        // …and the mini player's, only while the collection it stands over is up, because nothing else
        // shows it.
        if (LibraryUp)
            Marquee.Walk(_playerLines, dt);
        TickLyrics(dt);
        TickQueue(dt);
        TickFocus();   // …and the remote's focus, placed once the screen it was asked for can take it
        TickDrag(dt);
        TickSwipe(dt);   // …and a swiped line coming home
        TickWheel(dt);   // …and the collection travelling under the wheel
        TickCarousel(dt);   // …and the album carousel turning (see PegasusChrome.AlbumCarousel)
        TickRadio(dt);   // …and radio's banner stepping, its wheel turning, and the tuner's waiting menu
        TickArtistsGenres();   // …and the artists list's photographs, three asked at a time

        // The level, whoever set it. It is written only when it has actually moved. So this is a comparison
        // per frame, and nothing at all the rest of the time.
        if (_amp is { } amp && amp.Volume != _volumeShown)
            LayoutVolume();

        // …and the heart beside it, on the same terms. It is read when the deck has moved to another
        // record. Otherwise it is only a comparison of two strings on each frame.
        ReadFavorite();

        var onDeck = _amp?.Deck is not null;

        // The mini player over the collection works from the same reading. It is a player, and there is
        // nothing to play, so see PegasusChrome.Library. A tuned station puts nothing on the deck, so it is
        // asked of the transport separately. Tuned and paused still counts, so the key to resume it stays
        // up.
        ApplyLibraryPlayer(
            onDeck ? LibraryPlayerFor.Record
            : _amp is { IsLiveStream: true } ? LibraryPlayerFor.Station
            : LibraryPlayerFor.Nothing);

        // …and whether the card is up at all. On home it goes away after five seconds of nothing playing.
        // This runs after the line above, so it reads what the card holds as of this frame.
        TickPlayerAway(dt);

        // The transport under the record shows only its play key for a tuned station (see
        // ApplyStationTransport). Read off the frame for the same reason as the mini player above.
        var station = StationTuned;
        if (station != _transportForStation)
            ApplyStationTransport(station);

        // A record that pauses brings the transport up, with the play key focused for the remote, and the
        // transport stays up for as long as it is paused. The screen then shows that it is paused, and OK
        // resumes. This is read off the transport on every frame, so a pause from anywhere counts: the play
        // key, the space bar, a media key, or the deck in the main window.
        var paused = Paused;
        if (paused && !_wasPaused)
            PauseRaisesTransport();
        _wasPaused = paused;

        // The transport stands down after five quiet seconds. Any pointer move resets the count. It goes at
        // once when the deck empties, whatever the count says. Keys that drive a record that is no longer
        // there are an offer the player cannot keep. It does not stand down while the record is paused, nor
        // while the cast menu is open over it: the menu gives the focus back to its key when it closes, and
        // a key that has faded cannot take it.
        if (_transportUp)
        {
            _transportSeen = paused || CastMenuOpen ? 0 : _transportSeen + dt;
            if (!(onDeck || station) || _transportSeen >= TransportIdle)
                FadeTransport(up: false);
        }

        // …and the line at the top: a layout's name, a cast's news, or where the music plays.
        TickTopLine();

        // …and the list after three seconds, under the same rules. The time is shorter than the transport's
        // five, because the list is a bigger thing to leave lying over the picture. The keys are small and
        // sit in the margins. The list is a card in front of the record.
        //
        // The pointer being ON it holds it up. That is what "not hovered" means. A drag holds it up too,
        // because a drag is attention wherever the pointer has got to. The count runs here, not on a timer,
        // so that it stops with the screen. A countdown left running over a visualizer that has been put
        // away would close a panel that nobody can see, from a control that is no longer ticking.
        if (_queueOpen)
        {
            // …and somebody typing in the search field. This is attention with the pointer nowhere near the
            // panel. A drawer that closed mid-word would take the question with it. …and the sheet asking
            // which playlist a song goes on. This is a panel being READ, with the pointer wherever the
            // swipe left it. A drawer that closed under that question would take both the question and the
            // song. …and a drawer lying over the collection, which is a list being read there. A list that
            // left after three seconds would be taken away while the listener was still reading it. A list
            // opened from home is shown on the record screen, not in this drawer. Once the collection has
            // gone, the drawer is the queue over the record again, and it closes itself as it always has.
            // …and the remote's focus on anything in the drawer. A listener reading the drawer with keys is
            // not resting a pointer on it. A line's menu holds the focus too. The menu takes the focus into
            // its own popup, outside the drawer. Without this, the drawer closed under a queue row's menu
            // after three seconds, and took the row it was about to move with it.
            if (_queueHover || _dragRow is not null || _searchFocused || _sheetUp || LibraryUp
                || FocusWithin(QueuePanel) || LineMenuOpen?.Invoke() == true)
                _queueSeen = 0;
            else
                _queueSeen += dt;

            if (_queueSeen >= QueueIdle)
                SetQueueOpen(false);
        }

        // The record slides home and disappears when the deck empties. It comes back out when something is
        // put on. This is worked out BEFORE the standstill return below. A record that has just stopped is
        // exactly the one that has to keep moving for another second.
        _discOut = Math.Clamp(_discOut + (onDeck ? dt : -dt) / DiscTuckSeconds, 0, 1);
        ApplyDisc();

        var playing = _amp is { IsPlaying: true };
        _ramp = Math.Clamp(_ramp + (playing ? dt : -dt) / SpinUpSeconds, 0, 1);
        var speed = Ramp(_ramp);

        // At a standstill there is nothing more to write. This tick runs for as long as the visualizer is
        // up, including all the time it spends paused.
        if (speed <= 0)
            return;

        // The angle, and only the angle. The header's disc also orbits a hair once per turn. That is an
        // eccentric pressing, and it reads as life on a small disc glimpsed at the edge of the screen. At
        // this size it reads as a disc that is not sitting straight. So here the record simply turns about
        // its own centre. The offset that holds it inside its sleeve is set once, in Relayout.
        _angle = (_angle + 360.0 / RevolutionSeconds * speed * dt) % 360.0;
        Record.Rotate.Angle = _angle;
    }

    /// <summary>
    /// Puts the record where <see cref="_discOut"/> says, and fades it once it is home.
    ///
    /// <para>The slide finishes first, and the disappearance follows. The fade only begins over the last
    /// fifth of the travel. So what is seen is a record drawn back into its sleeve and then no longer
    /// there. It is not a record dissolving in mid-air on its way in. The whole control goes, halo
    /// included. Otherwise the soft ring around it would be left behind on the wash with nothing inside
    /// it.</para>
    /// </summary>
    private void ApplyDisc()
    {
        var out01 = Ramp(_discOut);
        DiscShift.X = _discHome * out01;
        Record.Opacity = Math.Clamp(_discOut / 0.20, 0, 1);
    }

    /// <summary>Smoothstep from standstill to full speed, so that there is no jerk at either end.</summary>
    private static double Ramp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Puts the WORDS on the middle of the screen, rather than the column that holds them.
    ///
    /// <para>The column is the three lines, then the seek bar and the keys under them. Centring it as one
    /// block would hang the words above the middle by half of everything below them. That was not very
    /// noticeable while the bar was always on screen. It is obvious once the bar stands down after five
    /// seconds, because then the thing being balanced is mostly invisible.</para>
    ///
    /// <para>So the column is pushed down by half its own tail. That puts the title, artist and album on
    /// the same horizon as the record beside them. Everything below hangs under them at exactly the spacing
    /// it already had. This is a translate, not a margin. It is composition only, so it cannot pull another
    /// layout pass through the window.</para>
    ///
    /// <para>This is only for the spread composition. Drawn in, the words are placed UNDER the cover from
    /// the top, and there is no middle for them to be on.</para>
    /// </summary>
    private void CentreWords()
    {
        if (_view != PegasusView.Spread)
        {
            WordsShift.Y = 0;
            return;
        }

        double text = 0;
        foreach (var line in _lines)
            text += double.IsNaN(line.Host.Height) ? 0 : line.Host.Height;
        text += Words.Spacing * Math.Max(0, _lines.Length - 1);

        var tail = Words.ActualHeight - text;
        WordsShift.Y = tail > 0 ? tail / 2 : 0;
    }

    // ── what is playing next ──
    //
    // ── what is playing next ──
    //
    // The queue, shown over the words. It is opened by the one key that is not about the record playing
    // now.
    //
    // It is built by hand rather than bound. The reason is the same as for the lyric sheet (see
    // BuildLyrics). This screen is a shader with a composition over it. An ItemsControl brings a template,
    // a panel and a layout pass with it. A queue is a few dozen rows at most. It is rebuilt only when
    // something about it has changed: when it opens, when a record starts, or when the list is edited from
    // another window.

    private bool _queueOpen;
    private Fader _queueFade = null!;
    private PlaylistTrack? _queueCurrent;

    /// <summary>The sheet that asks which playlist a swiped song goes on (see
    /// PegasusChrome.Playlists).</summary>
    private Fader _sheetFade = null!;

    /// <summary>The whole collection over the room, and the mini player at the top of it (see
    /// PegasusChrome.Library).</summary>
    private Fader _libraryFade = null!;
    private Fader _libraryPlayerFade = null!, _playerLogoFade = null!, _playerPillFade = null!;
    private Fader _libraryMenuFade = null!;
    private Fader _switchFade = null!;
    private Fader _recordFade = null!;
    private Fader _wallScreenFade = null!;
    private Fader _homeScreenFade = null!;
    private Fader _libraryBusyFade = null!;

    // ── the drag ──
    //
    // This is the same reorder that the phone's queue does (RPM.Mobile PlaylistPage), for the same reasons.
    // The track that was picked up stays under the pointer. The running order re-forms around it, one place
    // at a time. So what is on the screen during the drag is what will be there on release.
    //
    // NOTHING STRUCTURAL HAPPENS while a row is in the air. The rows keep their places in the panel. What
    // moves is a transform on each one. The carried row follows the pointer. The rows it has passed step
    // aside by exactly one row. An earlier attempt walked the row through the panel's Children instead,
    // with one RemoveAt and one Insert for each boundary crossed. That did not work. An element taken out
    // of the visual tree loses its pointer capture on the way out. So the drag ended at the first crossing,
    // the row's index read -1, and the move was dropped without a mark.
    //
    // The collection is told once, when the row has landed. Each Move raises a CollectionChanged. The model
    // answers each one by clearing the prepared chain and renumbering. Doing that six times on the way past
    // six rows would open and throw away six chains.
    //
    // The arithmetic (the half-row step and the edge-scroll ramp) is RPM.Core's QueueGesture. It is the
    // phone's, and it is tested. What is NOT shared is the direction lock and the rubber-band reach. A
    // pointer is not a finger. There is nothing to swipe away here, and a list that scrolls on a wheel
    // never has to work out whether a gesture belongs to it or to the row.
    private Border? _dragRow;
    private int _dragStart = -1;
    private int _dragAt = -1;
    private bool _dragMoved;

    /// <summary>Where the press went down, and where the pointer is now. Both are in the list's own space,
    /// not the screen's. So scrolling the list under a pointer that is held still counts as travel, which
    /// is what running the list on at an edge has to mean. The phone adds the scroll separately, because a
    /// manipulation reports in screen space. Here the coordinate already includes the scroll. The
    /// viewport-relative reading is kept, so a frame with no pointer event of its own can still work out
    /// where the pointer is over content that has moved beneath it.</summary>
    private double _dragStartY;
    private double _pointerViewY;

    /// <summary>Every row is the same height, so where any row sits is arithmetic, not a walk down the list
    /// asking each one. It is read once, when the drag starts.</summary>
    private double _rowHeight;

    /// <summary>The landing: how far the carried row still has to fall, and how long it has been falling.
    /// This is over a stated time, not an eased approach. The queue is told when the row arrives. An
    /// exponential approach closes by a fraction each frame, so it would report that the row had landed
    /// long after it had visibly stopped.</summary>
    private bool _dropping;
    private double _dropFrom;
    private double _dropTo;
    private double _dropAt;

    /// <summary>How long the drop takes, and how quickly a displaced row steps aside.</summary>
    private const double DropSeconds = 0.18;
    private const double StepRate = 16.0;

    /// <summary>How close to an end of the list the carried row has to get before the list starts moving
    /// under it. This also sets how fast the list goes at the very edge: points per frame at sixty frames
    /// per second, scaled by the frame time actually taken. It is fast enough to cross a screenful without
    /// waiting, and slow enough to stop where the listener meant to.</summary>
    private const double EdgeZone = 64;
    private const double MaxScrollStep = 13;

    /// <summary>How far the pointer must travel before a press becomes a drag. Below this it is a press,
    /// and a press plays the track. The two must be told apart from the same gesture.</summary>
    private const double DragSlop = 5.0;

    /// <summary>How long the list stays up with the pointer somewhere else.</summary>
    private const double QueueIdle = 3.0;
    private double _queueSeen;
    private bool _queueHover;

    // ── the slide ──

    /// <summary>How far in the drawer is: 0 is wholly off the left edge, 1 is home. It is eased on the
    /// tick, not animated, for the reason every other move in this control has (see <see cref="Fader"/>).
    /// Two things are written from this one figure, in step, on the same frame: the drawer's own position,
    /// and how far the picture has been pushed aside to make room for it.</summary>
    private double _queueSlide;

    /// <summary>How quickly it travels. It reaches nine tenths in about a quarter of a second. A drawer
    /// should look as if it was pushed, not as if it was faded up.</summary>
    private const double QueueSlideRate = 11.0;

    private double _shiftAt = -1;

    /// <summary>
    /// How far right the composition has been pushed, in pixels, whenever it changes.
    ///
    /// <para>The room behind this is a GL surface that the HOST owns, not this control. So the two cannot
    /// share a transform. A drawer that moved the record but not the room it stands in would tear the
    /// picture in half. This hands the host the one figure that both are written from.</para>
    /// </summary>
    public event Action<double>? ShiftChanged;

    /// <summary>
    /// How far the picture is pushed aside, from 0 to 1 of the drawer's width. It has its own figure, not
    /// <see cref="_queueSlide"/>. The drawer can also be open over the collection (a list pressed on home).
    /// There, the drawer lies over the covers or home like a card, and the room behind them stays put. It
    /// is eased on the same clock. So when the collection goes down under an open drawer, the picture
    /// slides aside instead of jumping.
    /// </summary>
    private double _pushSlide;

    /// <summary>Eases the drawer toward wherever it is going and writes both halves of the move.</summary>
    private void TickQueue(double dt)
    {
        var to = _queueOpen ? 1.0 : 0.0;
        var pushTo = _queueOpen && !LibraryUp ? 1.0 : 0.0;

        // This uses | and not ||, so both figures are moved on every frame.
        if (Slide(ref _queueSlide, to, dt) | Slide(ref _pushSlide, pushTo, dt))
            ApplyQueueShift();
    }

    /// <summary>Moves one figure a frame toward where it is going. It returns false once the figure has
    /// landed and been written, so a drawer at rest writes nothing.</summary>
    private static bool Slide(ref double value, double to, double dt)
    {
        if (Math.Abs(value - to) > 0.0004)
        {
            value += (to - value) * (1.0 - Math.Exp(-dt * QueueSlideRate));
            return true;
        }

        if (value == to)
            return false;           // It has landed and been written.

        value = to;                 // Close enough, so snap to the target.
        return true;
    }

    /// <summary>Puts the drawer where <see cref="_queueSlide"/> says, and the picture where
    /// <see cref="_pushSlide"/> says.</summary>
    private void ApplyQueueShift()
    {
        var width = double.IsNaN(QueuePanel.Width) ? 0 : QueuePanel.Width;

        // The drawer, from wholly off the edge to flush against it.
        QueueShift.X = -width * (1 - _queueSlide);

        // …and everything else moves by the whole of the drawer's width, not half of it. Half would
        // re-centre the record in what is left. That is the tidier arithmetic, and the wrong answer. The
        // words start close to the left edge, so at half the travel the drawer would still be lying over
        // the column it was opened from. Moved by the whole width, nothing that was visible before is
        // behind it now. That is what compensating for it means.
        var push = width * _pushSlide;
        CompositionShift.X = push;

        if (_shiftAt == push)
            return;
        _shiftAt = push;
        ShiftChanged?.Invoke(push);
    }

    /// <summary>Whether the list is up. The host asks this, so Escape can close the list before it closes
    /// the view. Leaving Pegasus when the intention was to close a panel is the kind of thing that a
    /// full-screen player only has to do once to be resented for.</summary>
    public bool IsQueueOpen => _queueOpen;

    /// <summary>The key under the seek bar. It shows what is coming, and it closes again. It is the only
    /// thing that opens this drawer. The swipe and the host's Escape can only ever close it. So a press
    /// that did nothing would leave the queue with no way to open it.</summary>
    private void Queue_Click(object sender, RoutedEventArgs e) => SetQueueOpen(!_queueOpen);

    /// <summary>
    /// The way back to the collection: the whole wall, full screen over the room.
    ///
    /// <para>Without it, the full-screen player has no way out other than the keyboard. A listener who wants to
    /// put something else on would be stuck looking at the record that is playing. This key sits beside the key that
    /// opens the queue, and it never replaces that key. What is coming next and what else there is are two
    /// questions. One key cannot answer both.</para>
    /// </summary>
    private void Back_Click(object sender, RoutedEventArgs e)
        => ShowLibrary(CollectionScreen.Wall, immediate: false);

    /// <summary>
    /// Raised by the lyric key, in the row under the record or in the lyric view's foot. The HOST turns the
    /// view. Which view is up, and whether the lyric may be shown, are decided by the host (see
    /// PegasusPlayer.ToggleLyrics). This control only draws the key.
    /// </summary>
    public event Action? LyricViewRequested;

    private void LyricView_Click(object sender, RoutedEventArgs e) => LyricViewRequested?.Invoke();

    /// <summary>
    /// Raised by the layout key, in the row under the record or in the foot. The HOST moves to the next
    /// layout and keeps the choice (see PegasusPlayer.StepLayout). This control only draws the key, and the
    /// layout's name at the top (see <see cref="ShowLayoutName"/>).
    /// </summary>
    public event Action? LayoutRequested;

    private void Layout_Click(object sender, RoutedEventArgs e) => LayoutRequested?.Invoke();

    /// <summary>
    /// After the layout key: keeps the focus on a layout key, where it was on one. A change of layout can
    /// fade the row the pressed key is in (the bare room draws the foot, the other layouts the column).
    /// The focus goes to the layout key of the transport the new layout draws, so the next press of OK
    /// moves on again. Only while the remote is in use, as every placement of the focus here.
    /// </summary>
    public void KeepLayoutFocus()
    {
        if (FocusWithin(LayoutButton) || FocusWithin(LyricLayoutButton))
            FocusScreenLater(FootDrawn(_viewTo) ? LyricLayoutButton : LayoutButton);
    }

    /// <summary>
    /// Shows or hides the lyric key in the row under the record, and fills it in while the lyric is on. The
    /// host gives the answer, from the facts that decide whether the lyric key is shown: a sheet for this
    /// record, or one still being fetched for a listener who is already following along. The key in the
    /// lyric view's foot needs no such call. That foot is only on screen while the lyric is, and it is
    /// always the filled key.
    /// </summary>
    /// <param name="on">The lyric is the view, beside the record or alone. The key is then the way back,
    /// and it says so.</param>
    public void ShowLyricKey(bool shown, bool on)
    {
        var tip = Strings.Loc.Get(on ? "Viz_LyricsBack" : "Viz_Lyrics");
        LyricViewButton.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        LyricViewHollow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        LyricViewFilled.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        IconName.Set(LyricViewButton, tip);

        // …and its twin in the foot. On the lyric view it is filled, as the way back. On the bare room,
        // which shows the foot too, it is the way to the words, as in the row.
        LyricViewFootButton.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        LyricFootHollow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        LyricFootFilled.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        IconName.Set(LyricViewFootButton, tip);
    }

    /// <summary>Opens or closes the list. This is idempotent, because the host calls it to shut the panel
    /// on a view change, and on Escape, without knowing whether it was ever open.</summary>
    public void SetQueueOpen(bool open)
    {
        // With nothing to show there is no panel at all, rather than an empty one. The key is there for a
        // queue. A card that says "nothing" is a worse answer than a button that appears not to have
        // worked.
        if (open && (_amp is null || _amp.Playlist.Count == 0))
            open = false;

        SetDrawerOpen(open);
    }

    /// <summary>Opens or closes the drawer whatever is in the queue. <see cref="SetQueueOpen"/> is the
    /// queue key's door, and it refuses an empty queue.</summary>
    private void SetDrawerOpen(bool open)
    {
        if (_queueOpen == open)
            return;
        _queueOpen = open;

        if (open)
        {
            // It always opens on the queue, whatever screen it was left on. The drawer is opened by the key
            // under the words, and that key asks what is coming next. A drawer that opened on a shelf of
            // records, because of where somebody was standing in it last time, would be answering a
            // question nobody had asked.
            ResetToQueue();

            BuildQueue();
            LayoutQueue();
            QueuePanel.Visibility = Visibility.Visible;
            QueuePanel.IsHitTestVisible = true;
            _queueFade.To(1, 180, null);
            Watch(true);
            ScrollQueueToCurrent();

            // With the remote in use, the focus goes into the drawer, on the row that is playing.
            FocusScreenLater(QueueLineFor(_queueCurrent));

            // The three seconds start now. If the drawer is opened and then left alone, it closes on its
            // own. The pointer arriving on it stops the count, wherever the count had got to.
            _queueSeen = 0;
            _queueHover = false;
        }
        else
        {
            // With the remote in the drawer, the focus goes back to the key that opens it. This is asked
            // now, while the drawer still holds the focus. The drawer fades for a moment before it lets go.
            if (FocusWithin(QueuePanel))
                FocusScreenLater(QueueButton);

            QueuePanel.IsHitTestVisible = false;
            _queueHover = false;
            CancelDrag();   // A row still in the air when the panel closes has nothing left to land in.
            EndQueueMove(place: false);   // A row the keys were carrying is put back as well.
            Watch(false);
            _queueFade.To(0, 260, () =>
            {
                // Only if nobody re-opened it while it was leaving.
                if (!_queueOpen)
                    QueuePanel.Visibility = Visibility.Collapsed;
            });
        }

        // The key that lit up while the list was up is the way OUT now. It says the same thing whether the
        // list is open or not. The drawer has its own close key in its header.

        void Watch(bool on)
        {
            if (_amp is null)
                return;
            // This is subscribed only while the panel is up. A list nobody is looking at does not need
            // rebuilding every time a record ends. This screen is the one place in the app where a stray
            // layout pass is measured in dropped frames.
            _amp.Playlist.CollectionChanged -= OnQueueChanged;
            _amp.DeckChanged -= OnQueueDeckChanged;
            if (!on)
                return;
            _amp.Playlist.CollectionChanged += OnQueueChanged;
            _amp.DeckChanged += OnQueueDeckChanged;
        }
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_queueOpen)
            return;

        // A record ended, or another window edited the list, while a row was in the air. The rows about to
        // be thrown away include the one being dragged. A drag whose row is no longer in the panel can only
        // end by dropping the move on the floor. So it is abandoned here, visibly, rather than left to fail
        // quietly. Nothing is committed. What the listener was doing was to a list that no longer exists in
        // that order.
        if (_dragRow is not null)
            CancelDrag();

        // The same applies to a row carried by the keys. A move that this drawer put down itself has
        // already ended by the time the queue reports it.
        EndQueueMove(place: false);

        // A reorder this drawer made itself: the rows were put in the new order as the carried row landed,
        // so there is nothing to build. Only the wash moves, because which track is playing has not
        // changed, but which ROW it is on has.
        if (RowsInStep())
        {
            PaintQueue();
            return;
        }

        // The list itself has changed, with rows added, removed or reordered from another window. It is
        // rebuilt whole. Working out which row is which after a reorder costs more than drawing thirty
        // TextBlocks.
        BuildQueue();
        LayoutQueue();
    }

    /// <summary>A record started. Only the two rows that changed are repainted: the one that was playing
    /// and the one that is now playing. The list is not built again just to move a highlight down
    /// it.</summary>
    private void OnQueueDeckChanged()
    {
        if (!_queueOpen)
            return;
        PaintQueue();
        ScrollQueueToCurrent();
    }

    /// <summary>Lays the queue out as one row per track. This runs once per change.</summary>
    private void BuildQueue()
    {
        DropQueueMove();  // A row carried by the keys is among the rows being thrown away.
        LightRow(null);   // The rows being thrown away include the lit one.
        QueueRows.Children.Clear();
        _queueTitles.Clear();
        if (_amp is null)
            return;

        // Read once for the whole list, not for each row. It comes off the window, and the window does not
        // change size in the middle of a loop.
        var scale = DrawerScale;
        _queueBuiltAt = scale;

        foreach (var track in _amp.Playlist)
        {
            // A Border, not a Button. The reason is the drag, not the look. A Button marks PointerPressed
            // as handled on its way past, so a capture taken in a handler on the row would never be asked
            // for, and the reorder would not start. A Button gave a hover wash and a press state. Both are
            // three lines to paint by hand (see PaintRow), because the row's resting colour is already
            // written from here anyway. The row has no padding of its own. The grid has it instead (below),
            // so the line inside the row fills it, and the remote's focus ring goes round the whole row.
            var row = new Border
            {
                Tag = track,
                CornerRadius = new CornerRadius(6 * scale),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),

                // What a reorder moves. Every row has one from the moment it is built. So a drag never has
                // to add one to a row already under the pointer. The rows keep their places in the panel
                // throughout. This is the whole reason the drag works at all.
                RenderTransform = new TranslateTransform(),
            };

            var grid = new Grid { Margin = new Thickness(10 * scale, 8 * scale, 10 * scale, 8 * scale) };
            // The title's column is AUTO, and the spare width is taken by a spacer after the badge. If the
            // title column were starred, it would take the whole row, and the E badge would be flung out to
            // sit against the duration. That would put a mark about the track at the other end of the row
            // from the track's name. Sized to its text, the badge falls right after the last letter of the
            // title. That is where a mark about a title belongs. A long title is stopped from pushing the
            // row apart by its MaxWidth, which the drawer's own width sets (see LayoutQueue).
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // the grip
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // the sleeve
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // the title
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // the E
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // the time

            // The grip takes the place of a row number. A number says what position a track is in, which is
            // already obvious from where the row is. The grip says that the row can be MOVED. That is the
            // thing that is not obvious, and nothing else on the screen says it.
            var grip = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)Resources["PegasusFa"],
                FontSize = 12 * scale,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10 * scale, 0),
                Opacity = 0.42,
            };
            Grid.SetColumn(grip, 0);

            // The sleeve, at the size the row is built around. The image sits in a frame. A track whose art
            // has not arrived, or has none, shows a quiet square where the cover goes. This avoids a hole
            // in the row.
            var art = new Image { Stretch = Stretch.UniformToFill };
            var sleeve = new Border
            {
                Width = QueueArt * scale,
                Height = QueueArt * scale,
                CornerRadius = new CornerRadius(3 * scale),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 10 * scale, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = art,
            };
            Grid.SetColumn(sleeve, 1);

            // The title alone. The artist is left off the row on purpose. A queue is nearly always one
            // record or one artist deep, so repeating the name down every line would use up the width the
            // titles need. The artist is also written under the record on this screen.
            //
            // NoWrap and trimmed. The lyric's own lines scroll when they do not fit, because a lyric has to
            // be read in full. A queue is scanned instead. Thirty rows each scrolling sideways would be a
            // screen nobody could look at.
            var label = new TextBlock
            {
                Text = track.Title,
                FontSize = 14 * scale,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8 * scale, 0),
            };
            Grid.SetColumn(label, 2);
            _queueTitles.Add(label);

            // The badge is built only for the rows that wear one, rather than being built on every row and
            // hidden. Nine rows in ten do not wear one, and a collapsed control is still a control the
            // layout has to walk past.
            if (track.IsExplicit)
            {
                var badge = new ExplicitBadge
                {
                    // IsExplicit is set here even though the row is only built for tracks that wear the
                    // badge. The badge starts COLLAPSED and un-hides itself from this property. A
                    // hand-built row has no binding to do that, so if this is not set, the badge is built
                    // and never shown.
                    IsExplicit = true,
                    Width = 13 * scale,
                    Height = 13 * scale,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8 * scale, 0),
                    // The letter is knocked out of the fill. It has to match the surface BEHIND the badge,
                    // which here is the drawer's near-black, not the library dark that it defaults to.
                    LetterBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x08, 0x08, 0x08)),
                };
                Grid.SetColumn(badge, 3);
                grid.Children.Add(badge);
            }

            var time = new TextBlock
            {
                Text = track.DurationText,
                FontSize = 12 * scale,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.55,
            };
            Grid.SetColumn(time, 5);

            grid.Children.Add(grip);
            grid.Children.Add(sleeve);
            grid.Children.Add(label);
            grid.Children.Add(time);

            // INSIDE the row, so the remote can stand on it and OK plays it. The row itself stays the
            // Border that the reorder moves, and the panel's children stay Borders (see
            // QueueRow_PointerMoved). The line takes no pointer input, so the press and the drag reach the
            // row as before. A held OK or the Menu key opens the queue's own menu (see
            // PegasusChrome.QueueRemote), not the song's. That menu's entries are about putting a song on,
            // and this song is already on.
            var queued = track;
            row.Child = new RemoteLine(grid, () => PlayQueued(queued), () => QueueMenu(queued), turn: null)
            {
                CornerRadius = row.CornerRadius,
            };

            ShowCoverAsync(art, Path.GetDirectoryName(track.FilePath) ?? "", track.FilePath);

            row.PointerEntered += QueueRow_PointerEntered;
            row.PointerExited += QueueRow_PointerExited;
            row.PointerPressed += QueueRow_PointerPressed;
            row.PointerMoved += QueueRow_PointerMoved;
            row.PointerReleased += QueueRow_PointerReleased;

            QueueRows.Children.Add(row);
        }

        PaintQueue();
    }

    /// <summary>The sleeve on a queue row, square, in points, before the drawer's scale.</summary>
    private const double QueueArt = 34;

    /// <summary>Everything on a row that is NOT the title, in points: the grip and its margin, the sleeve
    /// and its margin, the badge and its margin, the duration, the row's padding and the list's own
    /// margins. What is left of the drawer's width after this is what a title may use. It is scaled with
    /// the row, because every one of those pieces is.</summary>
    private const double QueueRowFurniture = 182;

    /// <summary>The gap between one row and the next, before the scale. Small: the rows carry their own
    /// wash, and a gap wide enough to read as a list of cards is a list you can see fewer of.</summary>
    private const double QueueRowGap = 3;

    /// <summary>
    /// How much bigger than its base size the drawer is drawn. It is taken from the window's HEIGHT, the
    /// same figure the cover and the words already scale from. So the list grows with the picture beside
    /// it, rather than staying a phone-sized column next to a record two feet across.
    ///
    /// <para>There is no change on a laptop screen, so a small window is unaffected. On a big monitor it
    /// grows by half again. A 13 point title is legible at a desk, but not from the other side of a room,
    /// which is where this screen is usually read from.</para>
    /// </summary>
    private double QueueScale => Math.Clamp(Root.ActualHeight / 900.0, 1.0, 1.5);

    /// <summary>The same figure for the host, so that what it draws over this screen (its messages along
    /// the foot) is the size of everything else written here. See <see cref="QueueScale"/>.</summary>
    public double TextScale => QueueScale;

    /// <summary>
    /// The figure the drawer is drawn at. It is <see cref="QueueScale"/> grown so that the drawer's names,
    /// written at 14 points, come out at the size that the rest of the view uses for names. That is 24
    /// points before the scale, which is the size of home's banner titles and the side menu. Everything in
    /// the drawer grows by this figure together: the text, the sleeves, the rows, the head, and its own
    /// width. So it keeps its proportions.
    /// </summary>
    private double DrawerScale => QueueScale * DrawerGrowth;

    private const double DrawerGrowth = 24.0 / 14.0;

    /// <summary>The scale the rows currently in the panel were built at. A window that has grown under an
    /// open drawer can then be told apart from one that has only been nudged.</summary>
    private double _queueBuiltAt;

    /// <summary>The title of every row, so the drawer can cap them when it is sized. This is kept as a list
    /// rather than found by walking the tree. The rows are built here, and a row's title is the one thing
    /// in it that has to change when the window does.</summary>
    private readonly List<TextBlock> _queueTitles = [];

    /// <summary>
    /// One decode per ALBUM, not one per row. A queue is often one record deep (twelve rows from one
    /// folder, for example). Each of those rows would otherwise read the same file from disk and decode the
    /// same picture twelve times, on the thread that is also driving a shader.
    ///
    /// <para>The cache is kept across lists rather than cleared with each one. The same records come round
    /// again. A queue rebuilt on every track change would otherwise re-read the record it is
    /// playing.</para>
    ///
    /// <para>The cache is capped. These are the drawer's own lists (a queue and a shelf), and both are
    /// bounded. But a decoded cover is about a quarter of a megabyte, held as a texture. Without a cap,
    /// nothing here would ever release one. So the oldest entries are removed first. A record that has been
    /// removed is one thumbnail read from disk away from being back. The wall of the whole collection keeps
    /// none of this. It drops each cover as its row is recycled, the same way the library window's wall
    /// does.</para>
    /// </summary>
    private readonly Dictionary<string, BitmapImage?> _queueArt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The order the covers came in, so the cap knows which to drop.</summary>
    private readonly Queue<string> _queueArtAge = new();

    /// <summary>How many decoded covers are held at once. This is more than the wall shows at any one time
    /// (five rows of eight, plus the repeater's own buffer on either side). So scrolling back a screen or
    /// two finds the pictures still here.</summary>
    private const int QueueArtCap = 256;

    /// <summary>
    /// Puts a track's sleeve in its row, once it is found.
    ///
    /// <para>The sleeve is looked for in the same two places the rest of the app checks, in the same order:
    /// the album folder's own cover file, then the picture embedded in the track. This runs off the UI
    /// thread, because both are file reads. This screen is the one place in the app where a synchronous
    /// read is measured in dropped frames.</para>
    ///
    /// <para>A record this machine does not hold has neither, and keeps the quiet square. The cloud could
    /// answer for those, as the album wall's loader does. But that would be one request per row, on a panel
    /// that closes after three seconds.</para>
    ///
    /// <para>The FOLDER is what is asked for. A track's path is used only where one is known. The browse
    /// screen next door knows a record by the folder it is in, and it has not read any of its tracks. So it
    /// can only be answered by the cover file. Both callers share one cache, which is the point of keying
    /// on the folder. A record met on the browse screen is already decoded by the time it is playing, and
    /// its tracks are in the queue.</para>
    /// </summary>
    private async void ShowCoverAsync(Image target, string folder, string? filePath = null)
    {
        // The row may have been rebuilt, or the whole drawer closed, while the disk was being read. Writing
        // to an Image that is no longer in the tree is harmless. Checking first costs more than it saves.
        // What matters is that the CACHE is filled either way, so the rebuild finds it waiting.
        if (await CoverAsync(folder, filePath) is { } bitmap)
            target.Source = bitmap;
    }

    /// <summary>
    /// The same read, for a caller that wants the PICTURE rather than a place to put it.
    ///
    /// <para>The wall of the collection binds its tiles to the record's own card, not to the element that
    /// shows it. A repeater hands one element to record after record as the wall scrolls. A cover written
    /// to the element that the read was started for would be the wrong sleeve under the right name.</para>
    /// </summary>
    private async Task<BitmapImage?> CoverAsync(string folder, string? filePath = null)
    {
        if (_queueArt.TryGetValue(folder, out var cached))
            return cached;

        byte[]? bytes = null;
        try
        {
            bytes = await Task.Run(() =>
            {
                // Looked up by PATH, not by content. The wall of the collection fills through here a screen
                // of covers at a time. A content key would have to read every one of those scans in full
                // (megabytes each) to work out that their thumbnails were already on disk.
                if (FolderArtWriter.FindFolderImage(folder) is { } image)
                    return ThumbCache.GetFile(image, ArtScaler.RowEdge);
                if (filePath is not null && TagReader.LoadArt(filePath)?.Bytes is { Length: > 0 } embedded)
                    return ThumbCache.Get(embedded, ArtScaler.RowEdge);
                return null;
            });
        }
        catch
        {
            // A file that has gone, or a picture that will not read. The square stays quiet.
        }

        BitmapImage? bitmap = null;
        if (bytes is { Length: > 0 })
        {
            try
            {
                var stream = Presentation.Common.BitmapBytes.Stream(bytes);
                bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
            }
            catch
            {
                bitmap = null;
            }
        }

        Remember(folder, bitmap);
        return bitmap;
    }

    /// <summary>Puts one cover in the cache, and takes the oldest out once the cache is over its cap. A
    /// miss is remembered too. A folder with no picture in it is a read that is not worth doing
    /// twice.</summary>
    private void Remember(string folder, BitmapImage? bitmap)
    {
        if (_queueArt.TryAdd(folder, bitmap))
            _queueArtAge.Enqueue(folder);
        else
            _queueArt[folder] = bitmap;

        while (_queueArtAge.Count > QueueArtCap)
        {
            var oldest = _queueArtAge.Dequeue();
            // A tile that is still showing a cover holds its own reference. This only stops the cache from
            // being the reason that every cover ever met stays in memory.
            _queueArt.Remove(oldest);
        }
    }

    /// <summary>Marks the record that is playing. This walks the rows rather than binding. <c>IsCurrent</c>
    /// is already kept true by the model. This is the one thing about a row that changes after it is
    /// built.</summary>
    private void PaintQueue()
    {
        foreach (var child in QueueRows.Children)
            if (child is Border row)
                PaintRow(row, hovered: false);

        // …and the row the pointer is on keeps its wash. Repainting the list is about which record is
        // playing. It has nothing to say about where the pointer is. A row that went dark under a still
        // pointer would stay dark until the pointer left it and came back.
        if (_litRow is { } lit)
            PaintDrawerRow(lit, lit: true);

        _queueCurrent = _amp?.Playlist.FirstOrDefault(t => t.IsCurrent);
    }

    /// <summary>
    /// One row's paint: its resting colour, or the pointer's.
    ///
    /// <para>This is written from here, not by a control template, because these rows are Borders (see <see
    /// cref="BuildQueue"/> for why a Button cannot be used). It does everything a Button did for the row.
    /// It also means the playing row's wash and the hover wash are decided in one place. Otherwise one
    /// would be a resource in the XAML and the other a line of code.</para>
    /// </summary>
    private static void PaintRow(Border row, bool hovered)
    {
        if (row.Child is not RemoteLine { Content: Grid grid } || row.Tag is not PlaylistTrack track)
            return;

        var playing = track.IsCurrent;

        // A breath of white under the row that is playing, a brighter one under the pointer, and nothing
        // under the rest. The colour is white, not the sleeve's colour. This card is drawn over a room
        // already painted in that colour, so a wash of it would sink into the thing it has to stand out
        // from.
        byte alpha = (hovered, playing) switch
        {
            (true, true) => 0x3D,
            (true, false) => 0x26,
            (false, true) => 0x1F,
            _ => 0x00,
        };
        row.Background = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));

        // The playing row is brought up out of the others. Weight is safe here in a way it is not on the
        // lyric sheet next door. These lines never wrap, so a bolder one cannot re-stack the list under it,
        // as a bolder lyric line would.
        grid.Opacity = playing ? 1.0 : 0.70;
        foreach (var cell in grid.Children)
            if (cell is TextBlock text)
                text.FontWeight = playing
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
    }

    // Through the drawer's own one-lit-row rule, not painted here, so a queue scrolled under a still
    // pointer cannot leave a trail of lit rows behind it. See LightRow.
    private void QueueRow_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        DrawerRow_PointerEntered(sender, e);

    private void QueueRow_PointerExited(object sender, PointerRoutedEventArgs e) =>
        DrawerRow_PointerExited(sender, e);

    /// <summary>A press plays the track. This happens only once it is clear that it was a press, and not
    /// the start of a drag. <see cref="_dragMoved"/> settles that.</summary>
    private void QueueRow_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // A drag that has started holds its capture on the list, so the release goes there and this never
        // runs. What is left here is a press that stayed still. The row plays.
        if (_dragRow is null || _dragMoved || _dropping)
            return;

        _dragRow = null;
        _dragStart = _dragAt = -1;

        if (sender is Border { Tag: PlaylistTrack track })
            PlayQueued(track);
    }

    /// <summary>Plays one track from the queue, where it stands in the queue. This happens on a press on
    /// its row, or on OK.</summary>
    private void PlayQueued(PlaylistTrack track)
    {
        _amp?.PlayTrack(track);
        WakeTransport();
    }

    private void QueueClose_Click(object sender, RoutedEventArgs e)
    {
        // Standing in an open record, this key means the collection, not the way out (see ApplyHead, which
        // dresses it). The drawer closes with the press either way. The wall is a screen to choose from. A
        // drawer left standing over it would be the last record still asking to be read.
        var toLibrary = _screen == DrawerScreen.Album;

        SetQueueOpen(false);
        WakeTransport();

        // Over home, the collection is already up, and it stays on home (see ShowLibrary).
        if (toLibrary)
            ShowLibrary(CollectionScreen.Wall, immediate: false);
    }

    // Hover, for the countdown above. This handles PointerMoved as well as Entered, and that is not belt
    // and braces. Entered can be missed when the pointer arrives already inside the panel. The panel comes
    // up UNDER a pointer that has not moved since the press that opened it. A list that vanished three
    // seconds after it was opened, with the pointer sitting on it, would look like a bug rather than a
    // screen tidying itself.
    private void Queue_PointerEntered(object sender, PointerRoutedEventArgs e) => QueueSeen();
    private void Queue_PointerMoved(object sender, PointerRoutedEventArgs e) => QueueSeen();

    private void Queue_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _queueHover = false;
        LightRow(null);   // The pointer has left the drawer, so no row in it is under the pointer.
    }

    private void QueueSeen()
    {
        _queueHover = true;
        _queueSeen = 0;
    }

    // ── rearranging ──

    private void QueueRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border row || _dropping)
            return;

        _dragRow = row;
        _dragStart = _dragAt = QueueRows.Children.IndexOf(row);
        _dragMoved = false;
        _dragStartY = e.GetCurrentPoint(QueueRows).Position.Y;
        _pointerViewY = e.GetCurrentPoint(QueueScroll).Position.Y;
    }

    /// <summary>
    /// The press has begun to travel. Nothing is captured yet. The pointer is still over the row it came
    /// down on, so the row's own events are enough. The moment it becomes a drag, the capture goes to the
    /// LIST, not to the row.
    ///
    /// <para>The row cannot hold the capture. A row travelling through its siblings is removed from the
    /// panel and put back at its new index. An element that leaves the visual tree loses its capture on the
    /// way out. That fires PointerCaptureLost at the one instant the row belongs to no panel. The drag
    /// would end there, with the row's index reading -1, and the move would be dropped without a mark. The
    /// list stays where it is all the way through, so its capture survives the reparenting.</para>
    /// </summary>
    private void QueueRow_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragRow is not { } row || !ReferenceEquals(sender, row) || _dragMoved || _dropping)
            return;

        var y = e.GetCurrentPoint(QueueRows).Position.Y;

        // Still inside the slop. This may yet turn out to be a press, so nothing is disturbed.
        if (Math.Abs(y - _dragStartY) < DragSlop)
            return;

        // Every row is the same height. A list whose rows have not been arranged yet cannot be worked out
        // from. What is wanted is the PITCH, which is one row's top to the next row's top, so the gap
        // between rows counts. Everything below uses this as the distance a row travels to change places. A
        // figure a few points short of it drifts a whole row out over a long list.
        _rowHeight = QueueRows.Children.Count > 0 && QueueRows.Children[0] is FrameworkElement first
            ? first.ActualHeight + QueueRows.Spacing
            : 0;
        if (_rowHeight <= 0)
            return;

        _dragMoved = true;
        _pointerViewY = e.GetCurrentPoint(QueueScroll).Position.Y;
        row.Opacity = 0.86;
        Canvas.SetZIndex(row, 1);   // Draw over the neighbours as it passes them.
        QueueRows.CapturePointer(e.Pointer);

        e.Handled = true;
    }

    /// <summary>
    /// Every move after the first one. These arrive here, not at the row, because the list holds the
    /// capture.
    ///
    /// <para>This only remembers where the pointer is. Following it, changing places and running the list
    /// on all happen on the frame instead (<see cref="TickDrag"/>). The list has to keep moving while a
    /// pointer is held still at an edge, and a pointer that has stopped sends no events.</para>
    /// </summary>
    private void QueueList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragRow is null || !_dragMoved)
            return;

        _pointerViewY = e.GetCurrentPoint(QueueScroll).Position.Y;
        _queueSeen = 0;   // A drag counts as attention, whatever the pointer is over.
        e.Handled = true;
    }

    private void QueueList_PointerReleased(object sender, PointerRoutedEventArgs e) => Land();

    private void QueueList_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => Land();

    /// <summary>
    /// One frame of a drag: the carried row under the pointer, the order re-formed around it, and the
    /// list running on if the row is held at an end.
    /// </summary>
    private void TickDrag(double dt)
    {
        if (_dropping)
        {
            TickDrop(dt);
            return;
        }

        if (_dragRow is not { } row || !_dragMoved || _rowHeight <= 0)
            return;

        // Where the pointer is over the CONTENT. That is the viewport position plus however far the list
        // has been scrolled under it. This is recomputed on every frame from the last reading, not taken
        // from an event. So a pointer held still at an edge keeps travelling while the list moves.
        var travel = _pointerViewY + QueueScroll.VerticalOffset - _dragStartY;

        // How far the carried row is being held from the slot it occupies now. This is a loop and not an
        // if, because a list running on at an end can cross several boundaries between two frames.
        var y = travel - ((_dragAt - _dragStart) * _rowHeight);

        while (QueueGesture.Step(y, _rowHeight) is var step && step != 0)
        {
            var to = _dragAt + step;
            if (to < 0 || to >= QueueRows.Children.Count)
                break;

            _dragAt = to;
            y -= step * _rowHeight;
        }

        // This is the whole travel, not what is left of it after the slots are taken. The carried row never
        // leaves the line it was built on. What changes is which slot it is HOLDING. So what puts it under
        // the pointer is the distance the pointer has gone, full stop. The phone writes the remainder,
        // because on the phone the carried row really is at _dragAt. Here it is still at _dragStart, so
        // writing the remainder would spring the row back a row at every boundary.
        Shift(row).Y = travel;
        SettleRows(dt);
        RunListOn(y, dt);

        _queueSeen = 0;
    }

    /// <summary>
    /// Steps every row that the carried row has passed aside by exactly one row, and lets the rest return
    /// home.
    ///
    /// <para>This is eased on the tick, like everything else in this control. What the eye has to follow is
    /// a row moving over to make room, not a row that has jumped into the space just vacated. The offsets
    /// are written from where the carried track is NOW, not added up for each crossing. So a boundary
    /// crossed and then crossed back leaves nothing behind.</para>
    /// </summary>
    private void SettleRows(double dt)
    {
        var ease = 1.0 - Math.Exp(-dt * StepRate);

        for (var i = 0; i < QueueRows.Children.Count; i++)
        {
            if (i == _dragStart || QueueRows.Children[i] is not Border row)
                continue;

            var to = QueueGesture.Displaced(i, _dragStart, _dragAt) * _rowHeight;

            var shift = Shift(row);
            shift.Y += (to - shift.Y) * ease;
        }
    }

    /// <summary>
    /// Scrolls the list when the carried row is held near an end. It scrolls faster the closer the row
    /// gets, the way dragging a file to the edge of a window scrolls it. Without this, a reorder could only
    /// reach as far as the drawer shows. On a queue of any length, that is most of the way to useless.
    /// </summary>
    private void RunListOn(double y, double dt)
    {
        if (QueueScroll.ScrollableHeight <= 0)
            return;

        // This is worked out rather than measured from the row. Every row is the same height, so where the
        // carried row is on the screen is its slot, minus how far the list is scrolled, plus how far it is
        // being held from that slot.
        var top = (_dragAt * _rowHeight) + y - QueueScroll.VerticalOffset;

        // This is scaled by the frame time actually taken. The phone's figure is per frame, because a phone
        // draws at a rate it decides. This screen is capped at sixty, but it drops below that under a heavy
        // visualizer. A list that slows down as the room works harder would fight the pointer.
        var step = QueueGesture.EdgeStep(top, _rowHeight, QueueScroll.ViewportHeight,
                                         EdgeZone, MaxScrollStep * dt * 60);
        if (step == 0)
            return;

        var to = Math.Clamp(QueueScroll.VerticalOffset + step, 0, QueueScroll.ScrollableHeight);
        if (Math.Abs(to - QueueScroll.VerticalOffset) < 0.5)
            return;

        QueueScroll.ChangeView(null, to, null, disableAnimation: true);
    }

    /// <summary>
    /// Puts the carried row down. It falls into the slot it is holding, and the queue is told when it gets
    /// there. By then the rows already show exactly what the new order will say. So the Move itself is not
    /// a movement at all. The rebuild it triggers redraws the same picture.
    /// </summary>
    private void Land()
    {
        if (_dragRow is null || !_dragMoved)
            return;

        _dragMoved = false;
        _dropping = true;
        _dropFrom = Shift(_dragRow).Y;
        _dropTo = (_dragAt - _dragStart) * _rowHeight;
        _dropAt = 0;

        QueueRows.ReleasePointerCaptures();
    }

    /// <summary>One frame of the landing. This is over a stated time, not an eased approach, because
    /// something waits on the end of it (see <see cref="_dropping"/>).</summary>
    private void TickDrop(double dt)
    {
        if (_dragRow is not { } row)
        {
            _dropping = false;
            return;
        }

        _dropAt = Math.Min(_dropAt + dt, DropSeconds);
        var t = _dropAt / DropSeconds;

        Shift(row).Y = _dropFrom + ((_dropTo - _dropFrom) * (t * t * (3 - (2 * t))));
        SettleRows(dt);

        if (_dropAt < DropSeconds)
            return;

        var from = _dragStart;
        var to = _dragAt;

        _dropping = false;
        row.Opacity = 1;
        Canvas.SetZIndex(row, 0);
        _dragRow = null;
        _dragStart = _dragAt = -1;

        PaintRow(row, hovered: true);

        // The collection needs no change when the row goes back where it came from.
        if (from == to || _amp is null)
        {
            ResetShifts();
            return;
        }

        // The rows are PUT in the new order before the collection is told, and their offsets are dropped in
        // the same step. The two cancel exactly. A row at slot i held a row above is a row at slot i-1 held
        // nowhere. So this moves nothing on the screen. The CollectionChanged that follows finds the panel
        // already showing what the queue is about to show, and leaves every row where it is (see
        // OnQueueChanged). Telling the collection first is what flickered. The rebuild threw away thirty
        // rows to draw the same thirty back, and every sleeve on them went out and came back through an
        // async fetch. A list that blinks each time a row is moved is the result.
        QueueRows.Children.RemoveAt(from);
        QueueRows.Children.Insert(to, row);

        // The two halves of this must land in the SAME frame. Where a row sits is decided by layout, and
        // how far it is drawn from there is decided by a transform. Those are different passes. Reordering
        // the children here only asks for a new arrangement, which arrives on the next frame. Dropping the
        // offsets takes effect on this frame. The gap between them is one frame with the rows still in
        // their old places and nothing holding them off their lines. Every row would snap back to where it
        // was and away again, which reads as the list flinching as the listener lets go. So the arrangement
        // is done now, on demand. The frame that draws the new order is then the frame that stops
        // compensating for the old one.
        QueueRows.UpdateLayout();
        ResetShifts();

        _amp.Playlist.Move(from, to);
    }

    /// <summary>Gives up the drag without telling the collection anything. The rows are about to be built
    /// again, so where any of them sits is not worth undoing. Only the capture and the flags matter.
    /// Leaving either one behind would eat the next gesture.</summary>
    private void CancelDrag()
    {
        if (_dragRow is { } row)
        {
            row.Opacity = 1;
            Canvas.SetZIndex(row, 0);
        }

        _dragRow = null;
        _dragStart = _dragAt = -1;
        _dragMoved = false;
        _dropping = false;

        QueueRows.ReleasePointerCaptures();
    }

    /// <summary>
    /// Whether the panel is already showing the queue in the order that the queue is now in: one row per
    /// track, each on the right one.
    ///
    /// <para>This is asked, not assumed. The drawer knows it put the rows straight before telling the
    /// collection. But the collection belongs to the app, and anything may have changed it in the meantime.
    /// A panel that trusted its own last move would keep showing a track that had been taken off.</para>
    /// </summary>
    private bool RowsInStep()
    {
        if (_amp is null || QueueRows.Children.Count != _amp.Playlist.Count)
            return false;

        for (var i = 0; i < QueueRows.Children.Count; i++)
            if (QueueRows.Children[i] is not Border row || !ReferenceEquals(row.Tag, _amp.Playlist[i]))
                return false;

        return true;
    }

    /// <summary>Puts every row back on its own line. This is for the case where the list is not being
    /// rebuilt behind it, such as a row dragged and put back where it came from.</summary>
    private void ResetShifts()
    {
        foreach (var child in QueueRows.Children)
            if (child is Border row)
                Shift(row).Y = 0;
    }

    /// <summary>How far a row is drawn from the line it belongs on. Every row is given one of these when
    /// it is built, so this is a cast rather than a search.</summary>
    private static TranslateTransform Shift(Border row) => (TranslateTransform)row.RenderTransform;

    /// <summary>Brings the playing row into view, without a jump when it is already there.</summary>
    private void ScrollQueueToCurrent()
    {
        if (!_queueOpen || _queueCurrent is null)
            return;

        var i = _amp?.Playlist.IndexOf(_queueCurrent) ?? -1;
        if (i < 0 || i >= QueueRows.Children.Count)
            return;

        // This is queued rather than measured now. The rows were added in this pass and have no height
        // until the framework has arranged them. Asking where one is before then means asking about
        // nothing.
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!_queueOpen || i >= QueueRows.Children.Count)
                return;
            if (QueueRows.Children[i] is not FrameworkElement row)
                return;

            var top = row.TransformToVisual(QueueRows).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            var target = top - (QueueScroll.ViewportHeight - row.ActualHeight) / 2;
            QueueScroll.ChangeView(null, Math.Max(0, target), null, disableAnimation: false);
        });
    }

    /// <summary>Sizes the card to the window and to the column it sits over. This is called from <see
    /// cref="Relayout"/>, and again whenever the list is rebuilt. A longer list needs a taller card, up to
    /// the ceiling this sets.</summary>
    private void LayoutQueue()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        // Everything with a fixed size in here is drawn at this multiple of it, and that includes the
        // drawer's own width. A list whose type and sleeves have grown, but whose column has not, is a list
        // of trimmed titles. The drawer's own figure is used (see DrawerScale). The mini player below keeps
        // the view's figure.
        var scale = DrawerScale;

        // If the window has grown or shrunk far enough under an open drawer, the rows are the wrong size
        // for it. They keep the sizes they had when they were built. So the only way to resize them is to
        // build them again. That is thirty TextBlocks, and it happens on a resize, not on a frame. It is
        // never done mid-drag, because the rows a reorder is moving through would be swapped underneath it.
        if (_queueOpen && _dragRow is null && Math.Abs(_queueBuiltAt - scale) > 0.001)
        {
            BuildQueue();
            ScrollQueueToCurrent();   // the rows are new, and a rebuilt list starts at the top
        }

        // …and the shelf next door, and the record open off it. These keep their sizes in the same way.
        if (Math.Abs(_drawerBuiltAt - scale) > 0.001)
            RebuildForScale(scale);

        // The gap between one row and the next. It is written here, not in the XAML, because it belongs to
        // the same scale. The drag reads its pitch back from it (see QueueRow_PointerMoved).
        QueueRows.Spacing = QueueRowGap * scale;

        // The width is wider than the words, because a row carries a number and a time as well as the
        // title. It is still a drawer down one side, never a pane over half the screen. The ceiling is a
        // share of the width, not a flat number. Everything the picture beside it needs has to fit in what
        // is left. On a narrow window, that is the limit that binds. The floor is what the row itself
        // needs. A grip, a sleeve, a badge and a time take about 140 of it, at this scale, before a title
        // has anywhere to start.
        var width = Math.Clamp(Words.Width > 0 ? Words.Width * 1.25 : w * 0.30,
                               280 * scale,
                               Math.Max(280 * scale, Math.Min(460 * scale, w * 0.36)));
        QueuePanel.Width = width;

        // This is how much a title may take before it is trimmed. Without this limit, the title column
        // would size to the whole of a long title. The column is Auto, so that the E falls in beside the
        // last letter rather than at the far end of the row. Without the limit, a long title would push the
        // duration off the drawer.
        var titleMax = Math.Max(60, width - QueueRowFurniture * scale);
        foreach (var title in _queueTitles)
            title.MaxWidth = titleMax;

        // …and the shelf's own names, which carry an E beside them on the same terms. Their row holds less
        // furniture than a queue row, so they are capped from their own figure.
        var browseMax = BrowseTitleMax(scale);
        foreach (var title in _browseTitles)
            title.MaxWidth = browseMax;

        // The head of the drawer (the two arrows and the two words) grows with the rows under it. So it
        // does not end up as a row of stamps over a list twice its size. It is written next door because
        // both screens share that head (see SizeBrowseHead).
        SizeBrowseHead(scale);

        // …and the mini player over the collection. It grows on the view's figure, not the drawer's (see
        // PegasusChrome.Library).
        SizeLibrary(QueueScale);

        // Full height, flush to the edge. The height and the margin are what make the difference between a
        // drawer and a card. Both come from the window, not from the composition.
        QueuePanel.Margin = new Thickness(0);

        // The width may have changed under an open drawer, during a resize while it is up. The slide and
        // the push are both worked out from it. So it is written now, rather than on the next tick.
        // Otherwise the resize would land a frame late, and the picture and the drawer would disagree for a
        // frame.
        ApplyQueueShift();
    }

    // ── the volume ──
    //
    // This is the same knob that the deck's own slider turns. It is one Volume on one model. It is drawn
    // the same way as the seek bar above it, because it is the same gesture and should not look like a
    // different kind of control. Nothing is bound. The value is read on the tick, and the geometry is
    // written only when the value has actually changed. This also keeps it right when the level is set from
    // somewhere else entirely: the deck, a keyboard key, or the speaker's own knob while casting.

    private int _volumeShown = -1;
    private bool _volumeDragging;

    private void Volume_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutVolume();

    private void Volume_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _volumeDragging = true;
        VolumeBar.CapturePointer(e.Pointer);
        SetVolumeFrom(e);
        e.Handled = true;
    }

    private void Volume_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_volumeDragging)
            return;
        SetVolumeFrom(e);
        e.Handled = true;
    }

    private void Volume_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_volumeDragging)
            return;
        _volumeDragging = false;
        VolumeBar.ReleasePointerCapture(e.Pointer);
        WakeTransport();
    }

    private void SetVolumeFrom(PointerRoutedEventArgs e)
    {
        if (_amp is null)
            return;
        var w = VolumeCanvas.ActualWidth;
        if (w <= 0)
            return;
        var x = e.GetCurrentPoint(VolumeCanvas).Position.X;
        _amp.Volume = (int)Math.Round(Math.Clamp(x / w, 0, 1) * 100);
        LayoutVolume();
    }

    /// <summary>Puts the line, the filled run and the bar where the level says. These are the same three
    /// rectangles the seek bar lays out, and they are laid out the same way: from a fraction, on a
    /// Canvas.</summary>
    private void LayoutVolume()
    {
        var w = VolumeCanvas.ActualWidth;
        if (w <= 0)
            return;

        var level = Math.Clamp((_amp?.Volume ?? 0) / 100.0, 0, 1);
        _volumeShown = _amp?.Volume ?? -1;

        const double lineH = 3, knobW = 3, barH = 14;
        var lineTop = (barH - lineH) / 2;

        Canvas.SetTop(VolumeTrack, lineTop);
        VolumeTrack.Width = w;

        Canvas.SetTop(VolumeFill, lineTop);
        VolumeFill.Width = Math.Max(0, level * w);

        // The bar is inset by its own width at the far end. So at full volume it sits ON the end of the
        // line, not half off it. The seek bar does the same at the end of a track.
        Canvas.SetTop(VolumeKnob, 0);
        Canvas.SetLeft(VolumeKnob, level * Math.Max(0, w - knobW));
    }

    // ── the transport ──

    /// <summary>Whether the record on the deck, or the tuned station, is paused: loaded, and not playing.
    /// False with nothing on the deck and no station. A paused station holds the transport up with its
    /// play key, as a paused record does, so OK resumes it (the TV does the same).</summary>
    private bool Paused => _amp is { IsLoaded: true, IsPlaying: false } amp && (amp.Deck is not null || StationTuned);

    /// <summary>Whether a station is tuned and nothing is on the deck: the record turning shows the
    /// station, and the transport has only its play key to offer (see <see cref="ApplyStationTransport"/>).
    /// </summary>
    private bool StationTuned => _amp is { IsLiveStream: true } amp && amp.Deck is null;

    /// <summary>Whether the transport is dressed for a station now (see <see cref="ApplyStationTransport"/>).
    /// </summary>
    private bool _transportForStation;

    /// <summary>
    /// Dresses the transport for a tuned station, or back for a record. A station shows the play key and
    /// nothing else, in the row under the record and in the lyric's foot alike: a live stream has no queue,
    /// no previous or next, no heart, nothing to go back to in the row and no volume worth a second knob
    /// (the TV's player, 2026-10-12: "it should be the only button visible"). Previous and next would step
    /// the record queue, which turns the station off. The play key is the deck's own command, so it pauses
    /// and resumes the station as the main window's key does.
    ///
    /// <para>The strips are hidden whole, not key by key. The back key's Visibility belongs to MouseKeys,
    /// and the lyric key's and the volume's to their own code, so hiding the panels around them leaves those
    /// owners alone. The play key is the screen's default stop (see DefaultFocusTarget). A focus that was on
    /// a key that has now gone is put back on it, so the remote is not left on nothing.</para>
    /// </summary>
    private void ApplyStationTransport(bool station)
    {
        _transportForStation = station;

        // Asked before the keys go: a focused key that is collapsed still reports its old size until the
        // next layout.
        var focusGoes = station
            && (FocusWithin(TransportLeftKeys) || FocusWithin(TransportRightKeys) || FocusWithin(LyricFootKeys)
                || FocusWithin(TransportPrevButton) || FocusWithin(TransportNextButton)
                || FocusWithin(LyricPrevButton) || FocusWithin(LyricNextButton));

        var others = station ? Visibility.Collapsed : Visibility.Visible;
        TransportLeftKeys.Visibility = others;
        TransportPrevButton.Visibility = others;
        TransportNextButton.Visibility = others;
        TransportRightKeys.Visibility = others;
        LyricFootKeys.Visibility = others;
        LyricPrevButton.Visibility = others;
        LyricNextButton.Visibility = others;

        if (focusGoes && !LibraryUp && !_queueOpen)
            FocusScreenLater(null);
    }

    /// <summary>Whether the record was paused on the last frame, so a pause is acted on once, as it
    /// happens.</summary>
    private bool _wasPaused;

    /// <summary>Whether the controls are being held up by the record's state rather than by a pointer or a
    /// key: the record is paused, or the cast menu is open. The host keeps its own keys and the pointer up
    /// for as long as this holds (see PegasusPlayer.HideKeys).</summary>
    public bool HoldsControls => Paused || CastMenuOpen;

    /// <summary>
    /// The record has just paused. The transport comes up. While the remote is in use on the turning record,
    /// the focus goes to the play key, so OK resumes. That happens only where the focus is not already on
    /// the transport. A listener who paused with the play key, or who is on the seek bar, keeps their place.
    /// </summary>
    private void PauseRaisesTransport()
    {
        WakeTransport();
        if (LibraryUp || _queueOpen)
            return;
        if (FocusWithin(TransportRow) || FocusWithin(Seek) || FocusWithin(LyricKeys) || FocusWithin(LyricSeek))
            return;
        FocusScreenLater(null);   // the screen's default: the play key of the transport that is drawn
    }

    private void WakeTransport()
    {
        // Nothing is on the deck and no station is tuned, so there is nothing to drive. The keys stay away,
        // rather than offering a play button for a record that is not there. A tuned station has its play
        // key (see ApplyStationTransport).
        if (_amp?.Deck is null && !StationTuned)
            return;

        _transportSeen = 0;
        FadeTransport(up: true);
    }

    /// <summary>
    /// Brings the transport and the bar up, or stands them down.
    ///
    /// <para>This is guarded on the state alone, and that guard is load-bearing. Every pointer move over
    /// the screen comes through here. Without the guard, a move that lands during the 140 ms wake-up would
    /// start the fade again. That would happen sixty times a second while the pointer moves. The faders
    /// would each absorb that on their own. But the hit-testing of the row and the bar is set here too, and
    /// deciding it again on every pointer move is wasted work.</para>
    /// </summary>
    private void FadeTransport(bool up)
    {
        if (_transportUp == up)
            return;
        _transportUp = up;

        // A faded transport must not catch the pointer. The column's row and the lyric's are the same
        // transport drawn in two places, and only one of them is on screen at a time. The column's is the
        // whole ROW, so the list key on its left end goes quiet with the keys beside it.
        TransportRow.IsHitTestVisible = up;
        LyricKeys.IsHitTestVisible = up;

        // The bar goes with them. It is a control, not part of the picture. The record, the cover and the
        // words are what this screen is. Everything that only drives them leaves together.
        Seek.IsHitTestVisible = up;
        LyricSeek.IsHitTestVisible = up;

        // A tooltip opened over one of these keys goes with them. The remote's focus opens one, and without
        // this it would stay on screen alone after the keys had faded.
        if (!up)
            CloseOpenToolTips(XamlRoot);

        // Out is slower than in. The eye accepts a slow departure, and it wants the controls at once when
        // it asks for them.
        var ms = up ? 140 : 420;
        var to = up ? 1 : 0;
        _transportFade.To(to, ms, null);
        _lyricTransportFade.To(to, ms, null);
        _seekFade.To(to, ms, null);
        _lyricSeekFade.To(to, ms, null);
    }

    /// <summary>
    /// Whether the lyric's own foot (its seek bar and the keys under it) may be pressed.
    ///
    /// <para>This has its own call, rather than being a line inside <see cref="Relayout"/>. The two things
    /// it depends on change on different schedules. The VIEW changes through a swap, and a swap relays out.
    /// The SHEET arrives whenever LRCLIB answers, and that does not relay out. A sheet that lands on a
    /// screen already showing the lyric takes the view it was already on. So nothing relays out, and the
    /// bar along the foot of the words stays dead until the next resize.</para>
    /// </summary>
    private void ApplyLyricFootHits()
        => LyricFoot.IsHitTestVisible = (_view == PegasusView.Lyrics && _sheet.IsSynced) || _view == PegasusView.Room;

    // ── layout ──

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

    /// <summary>
    /// The narrowest the row of keys under the words may be, in its own unscaled points, before its parts
    /// meet. There are three keys on each end (three 36s and two gaps of 2, which is 112). The transport
    /// is centred on it (36, 42 and 36, with gaps of 10, which is 134), and there are 6 of air between
    /// them. The transport's left end is at half the row less 67, so the row has to be at least twice 118
    /// plus 134.
    ///
    /// <para>The block that holds the row is drawn larger on a tall screen (ControlsScale). A larger block
    /// in the same column is a narrower row inside it. So the scale is held to what keeps the row at least
    /// this wide. If the keys in the row change, change this too.</para>
    /// </summary>
    private const double ControlsLeast = 370;

    /// <summary>The same, with the volume beside the keys on the right end: 8 of gap, the glyph (about 14),
    /// 6 of gap and the 56 of the bar, which is 84 more on that end. The row has to be at least twice 202
    /// plus 134.</summary>
    private const double ControlsWithVolume = 540;

    /// <summary>The sleeve's side for a window this size, from the shared fractions, floored and capped. It
    /// is worked out in one place because two things are placed by it: the record (Relayout), and the
    /// lyric's column beside the record (SheetColumn). The column has to start where the record
    /// ends.</summary>
    private static double CoverSide(double w, double h) =>
        Math.Clamp(h * PegasusLayout.CoverFraction,
                   PegasusLayout.CoverMin,
                   Math.Max(PegasusLayout.CoverMin, w * PegasusLayout.CoverMaxWidthFraction));

    /// <summary>The record behind a sleeve: the disc, its halo, how far its shadow is thrown, and the
    /// square box that holds all of them. See Relayout, which explains each figure.</summary>
    private readonly record struct RecordShape(
        double Disc, double Halo, double ThrowX, double ThrowY, double Reach, double Host);

    private static RecordShape ShapeOf(double side)
    {
        var disc = side * 0.96;
        var halo = disc * PegasusLayout.DiscHaloScale;
        var throwX = disc * PegasusLayout.DiscShadowThrowX;
        var throwY = disc * PegasusLayout.DiscShadowThrowY;
        var reach = Math.Max(((halo - disc) / 2) + Math.Max(throwX, throwY), 0);
        return new RecordShape(disc, halo, throwX, throwY, reach, disc + (reach * 2));
    }

    /// <summary>
    /// Places the composition for the window it is in. Everything is worked out from the cover's side. The
    /// cover's side comes from the shared fractions in <see cref="PegasusLayout"/>. These are the same
    /// numbers the stage uses to work out where to start its spectrum. So the bars come out from behind
    /// this record at every window size, not only at the size that was tried.
    /// </summary>
    private void Relayout()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        var side = CoverSide(w, h);

        // The sleeve. Its frame is part of the square (a Border's Width is its outer size), so the picture
        // inside is a little smaller than the record behind it. That is what a sleeve is.
        CoverFrame.Width = side;
        CoverFrame.Height = side;
        CoverFrame.BorderThickness = new Thickness(Math.Max(1, Math.Round(side * 0.014)));
        CoverFrame.CornerRadius = new CornerRadius(Math.Max(2, side * 0.012));
        CoverPlaceholder.FontSize = Math.Max(14, side * 0.24);

        // The record is a shade smaller than the sleeve, so its arc sits inside the frame's top and bottom.
        // It is pushed right until a quarter of it shows.
        var shape = ShapeOf(side);
        var disc = shape.Disc;
        var halo = shape.Halo;

        // The light in this room comes from the LEFT, a little above. That is the same side the words are
        // written on, and the side the sleeve's own frame catches. So the record does not sit in a ring of
        // its own shade. It THROWS a shadow, down and to the right, and its lit edge meets the wash with
        // nothing under it. That is the whole of the effect. The halo is a soft disc, moved off centre, and
        // the record itself covers the half of it that falls on the lit side.
        var throwX = shape.ThrowX;
        var throwY = shape.ThrowY;

        // The HOST holds the disc AND every place the shadow can land. Sized to the disc only, it clipped
        // its own shadow. The control's box ended at the vinyl's edge, and the soft ring past it was cut
        // off square along the sides it overhung. The box is the halo plus the throw, on both sides, so it
        // stays centred on the disc. That keeps the spin's wobble and the slide out of the sleeve working
        // from the same centre as before.
        var reach = shape.Reach;
        var host = shape.Host;
        Record.Width = host;
        Record.Height = host;

        // Everything inside is then placed against the host's square, from its top-left corner. The disc is
        // dead centre in it, and the halo is centred and then moved away from the light.
        Record.Box.Width = disc;
        Record.Box.Height = disc;
        Record.Box.HorizontalAlignment = HorizontalAlignment.Left;
        Record.Box.VerticalAlignment = VerticalAlignment.Top;
        Record.Box.Margin = new Thickness(reach, reach, 0, 0);

        Record.ShadowHalo.Width = halo;
        Record.ShadowHalo.Height = halo;
        Record.ShadowHalo.HorizontalAlignment = HorizontalAlignment.Left;
        Record.ShadowHalo.VerticalAlignment = VerticalAlignment.Top;
        Record.ShadowHalo.Margin = new Thickness((host - halo) / 2 + throwX, (host - halo) / 2 + throwY, 0, 0);
        // Held well back. The halo is drawn for the header, where the disc sits on a busy band and needs to
        // be separated from it. Here it has a whole quiet room around it, and any more would read as a
        // ring. Softening it here is the safe way to take it down. The SPREAD view (see DiscHaloScale) has
        // a floor under it. Pulling it in far enough would eat the pressing's own rim, and the record would
        // look a size smaller.
        Record.ShadowHalo.Opacity = 0.55;

        // Where the record sits when it is all the way out. It is not written straight to the transform.
        // The disc slides home when the deck empties, so what is on screen is this figure multiplied by how
        // far out the disc currently is. Nothing about this figure changes while the record spins. Only how
        // much of it is used changes.
        _discHome = side * PegasusLayout.DiscOffsetFraction;
        ApplyDisc();

        // ── the words ──
        //
        // In the spread view, the words run down the left. They are never under the cover. The column takes
        // whichever is smaller: its share of the width, or whatever is actually left between the margin and
        // the cover's edge.
        //
        // Drawn in, the words sit centred under the cover, in a column half again as wide as it is. That
        // gives a title somewhere to be without reaching past the picture it belongs to. They are placed
        // from the top, rather than centred with a margin. A top margin on a centred element moves it only
        // half as far as it says.
        //
        // Nothing that has been faded away may still catch the pointer. An invisible transport is still a
        // transport as far as a click is concerned. This applies to every view that DRAWS the column, not
        // only to the views that are not the empty room. The lyric takes the words away as completely as
        // the room does. Otherwise its faded-out seek bar and keys would sit over the words and catch every
        // press meant for them.
        var showing = _view is PegasusView.Spread or PegasusView.Centred or PegasusView.LyricsBeside;
        Words.IsHitTestVisible = showing;
        Centre.IsHitTestVisible = showing;

        // The list belongs to the column it opens over. Where the words are gone (the bare room, or the
        // lyric), the list is gone too. The key that opens it went with them.
        if (!showing)
            SetQueueOpen(false);

        ApplyLyricFootHits();

        double column;
        if (_view != PegasusView.Spread)
        {
            column = Math.Max(240, Math.Min(w * 0.56, side * 1.5));
            Words.HorizontalAlignment = HorizontalAlignment.Center;
            Words.VerticalAlignment = VerticalAlignment.Top;
            Words.Margin = new Thickness(0, h / 2 + side / 2 + Math.Max(20, side * 0.08), 0, 0);
        }
        else
        {
            var left = w * PegasusLayout.TextLeftFraction;
            var gap = (w - side) / 2 - left - side * 0.12;
            column = Math.Max(120, Math.Min(w * PegasusLayout.TextWidthFraction, gap));
            Words.HorizontalAlignment = HorizontalAlignment.Left;
            Words.VerticalAlignment = VerticalAlignment.Center;
            Words.Margin = new Thickness(left, 0, 0, 0);
        }
        Words.Width = column;
        LayoutQueue();

        // The transport row has three things in it. Only the middle one is sure of its place: the keys are
        // centred, and the two strips of keys hang off the two ends, the volume with the right one. On a
        // narrow column they would meet in the middle. So the volume stands down. It is the one with
        // somewhere else to be, since the deck has the same slider, the keyboard has its volume keys and
        // the machine has its own.
        //
        // The volume stays where the column holds the row with it at the desktop's own size. The block is
        // then drawn larger only as far as the row with the volume still fits. A row that has dropped the
        // volume may grow further.
        var volume = column >= ControlsWithVolume;
        var controls = Math.Max(1, Math.Min(QueueScale, column / (volume ? ControlsWithVolume : ControlsLeast)));
        ControlsScale.Factor = controls;
        VolumeGroup.Visibility = volume ? Visibility.Visible : Visibility.Collapsed;

        // The foot along the bottom of the lyric view and of the bare room. The bare room has no sheet, so
        // LayoutLyrics, which also lays it out, may not run.
        LayoutFoot(w, h);

        var title = Math.Clamp(side * 0.075, 16, 44);
        // The artist and the album at the Android TV app's couch proportions, 0.75 and 0.62 of the title
        // where the desktop had 0.60 and 0.45, and the time left at its 18 where the desktop had 12
        // (FOBO 77d2d7f03), so the two lines under the title read from across a room.
        TitleText.FontSize = title;
        ArtistText.FontSize = title * 0.75;
        AlbumText.FontSize = title * 0.62;
        Seek.RemainSize = LyricSeek.RemainSize = 18;

        // The intro card uses the same figure, in the same proportion. It is meant to be recognised as the
        // drawn-in composition's words, met again in the middle of the screen. It is held to the width the
        // lyric's own lines are read through, not the whole window. So a long title breaks where a sung
        // line would, and the card stays a card instead of becoming a banner.
        LyricIntroTitle.FontSize = title;
        LyricIntroArtist.FontSize = title * 0.75;
        LyricIntro.MaxWidth = LyricColumn(w);

        // The column each line is read through, and then its own width within it. Both come from the one
        // number that the seek bar under them is also stretched to. A line whose travel and whose clip come
        // from different widths would finish its walk with its tail already past the edge.
        //
        // The font size has just changed too, so every line is a different width than it was. They are
        // measured here, not near the tick.
        foreach (var line in _lines)
        {
            // Centred under the cover, a line that FITS sits in the middle of its column. A line that does
            // not fit still starts at the left edge and travels. A line that is going to move has to begin
            // where reading begins.
            line.Centred = _view != PegasusView.Spread;
            line.Fit(column);
            line.Remeasure();
        }
        CentreWords();

        // The gap above the bar and the keys is the room the bar's countdown floats up into. It rides 24
        // above the line, inside the block, so it is 24 at the block's scale. Air is added above it, off
        // the cover's size. The gap is set on the block, not on the bar, so the scale does not grow it a
        // second time. The air above the controls is 8% of the cover's side (44 points on a 1440 high
        // screen). It stops the controls sitting too close to the album's name and to the lyric above them.
        ControlsScale.Margin = new Thickness(0, (24 * controls) + Math.Max(20, side * 0.08), 0, 0);

        WordScrim.Width = Math.Max(240, w * 0.42);

        // The sheet's type size is worked out from this window, so a resize relays the sheet out once here.
        // The scroll does not have to ask for it on every frame.
        LayoutLyrics();

        // The record and its lines are placed where the lyric beside them puts them, for this size. This
        // comes after the sheet, because the column beside the record is as wide as the sheet's longest
        // line at this size.
        RetargetBeside(glide: false);

        // …and the room is told where all of that ended up, so the ball keeps its light off it.
        PushWordBox();
    }
}
