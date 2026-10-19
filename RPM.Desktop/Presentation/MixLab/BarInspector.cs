#if DEBUG
#if RPM_SKIA
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Analysis;
using RPM.Core.Mixing;
using Windows.UI;

namespace RPM.Desktop.Presentation.MixLab;

/// <summary>
/// What the analyser knows about the bar under one deck's needle. It is laid out so that it can be read
/// at a glance while the set plays. The levels are small bars, not only numbers, so a DJ can compare
/// the bass of the two records without doing any sums.
/// </summary>
public sealed partial class BarInspector : StackPanel
{
    private readonly TextBlock _heading = new()
    {
        FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xE6, 0xE9, 0xEE)),
    };

    private readonly TextBlock _count = Line();
    private readonly TextBlock _where = Line();

    /// <summary>Where the two cue points are. The section line above says which part of the record the
    /// needle is in. This line gives the exact boundaries, which is what an edit is made against. It
    /// also says when a cue point was never measured, because a record with no cues and a record cued
    /// at zero look the same on the picture above.</summary>
    private readonly TextBlock _cues = Line();

    /// <summary>The span the record was measured to repeat over. The dashed lines on the timeline show
    /// it. Nothing is planned against it. See MixLabTimeline.DrawPhrases.</summary>
    private readonly TextBlock _phrase = Line();
    private readonly TextBlock _grid = Line();
    private readonly TextBlock _drop = Line();
    private readonly StackPanel _levels = new() { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };

    public BarInspector()
    {
        Spacing = 2;
        Children.Add(_heading);
        Children.Add(_count);
        Children.Add(_where);
        Children.Add(_cues);
        Children.Add(_phrase);
        Children.Add(_grid);
        Children.Add(_drop);
        Children.Add(_levels);
    }

    /// <summary>What the panel was last built for. A paused screen updates eight times a second, and it
    /// does not rebuild the rows while the needle has not moved.</summary>
    private string _shownFor = "";

    /// <summary>Fills the panel for the bar under <paramref name="seconds"/> of this record, or empties
    /// it when nothing is on the deck.</summary>
    public void Show(string deckName, MixTrack? track, double seconds)
    {
        string key = $"{deckName}|{track?.Path}|" +
                     $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(track?.Analysis)}|{seconds:F2}";
        if (key == _shownFor)
            return;
        _shownFor = key;

        _levels.Children.Clear();

        if (track?.Analysis is not { } analysis)
        {
            _heading.Text = deckName;
            _count.Text = "No track loaded.";
            _where.Text = "";
            _cues.Text = "";
            _phrase.Text = "";
            _grid.Text = "";
            _drop.Text = "";
            return;
        }

        var facts = BarFacts.At(analysis, seconds);
        _heading.Text = $"{deckName}   {MixInputs.Sec(seconds)}";

        _count.Text = facts.HasGrid
            ? $"Bar {facts.BarNumber}, bar {facts.PhraseSlot} of {facts.PhraseBars} in the phrase, " +
              $"{facts.BarSeconds:F2}s per bar, {facts.BeatsPerBar} beats"
            : "No grid.";

        _where.Text = $"Section: {facts.Section switch
        {
            BarSection.BeforeTheMusic => "lead in",
            BarSection.Intro => "intro",
            BarSection.Body => "body",
            BarSection.Outro => "outro",
            BarSection.AfterTheMusic => "tail",
            _ => "unknown",
        }}";

        _cues.Text = $"Intro ends {Cue(analysis.IntroEndSeconds, seconds)}, " +
                     $"outro starts {Cue(analysis.OutroStartSeconds, seconds)}";

        // The measured phrase. The dashed rose lines on the picture above show it. It is shown on its
        // own line, apart from the "bar N of 4" line. That four is a constant the mix is aligned to.
        // This line is a measurement of the record, and the two are different things.
        _phrase.Text = analysis.Phrases.IsKnown
            ? $"Phrase: {analysis.Phrases.Bars} bars, from bar {analysis.Phrases.FirstBar} " +
              $"(confidence {analysis.Phrases.Confidence:F2})"
            : "Phrase: not measured";

        // Red means the tracker has given up on music that is still playing. Past the end of the music
        // the grid is extrapolated on every record, so that part is shown in grey.
        _grid.Text = facts.OnExtrapolatedGrid
            ? facts.ExtrapolatedOverMusic
                ? "Grid: extrapolated (past the last tracked beat)"
                : "Grid: extrapolated (past the end of the music)"
            : facts.HasGrid ? "Grid: tracked" : "";
        _grid.Foreground = new SolidColorBrush(facts.ExtrapolatedOverMusic
            ? Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B)
            : Color.FromArgb(0xFF, 0x8A, 0x90, 0x9A));

        _drop.Text = facts.SecondsToNearestDrop is { } away
            ? away >= 0
                ? $"Next drop in {away:F1}s"
                : $"Last drop {-away:F1}s ago"
            : "No drops measured";

        Level("energy", facts.Energy, Color.FromArgb(0xFF, 0xE6, 0xE9, 0xEE));
        if (facts.Bass is { } bass) Level("bass", bass, Color.FromArgb(0xFF, 0x5A, 0x8D, 0xFA));
        if (facts.Mid is { } mid) Level("mid", mid, Color.FromArgb(0xFF, 0x6F, 0xA8, 0xD8));
        if (facts.Top is { } top) Level("top", top, Color.FromArgb(0xFF, 0x9A, 0xC8, 0xE8));
        if (facts.VocalShare is { } vocal) Level("vocal", vocal, Color.FromArgb(0xFF, 0xF2, 0xC0, 0x4C));
    }

    /// <summary>One cue point and how far the needle is from it. Zero means "not measured", not "the
    /// top of the record". Every reading this app makes puts both cues on a bar line inside the music,
    /// so a zero is a cue that was never found. See <see cref="CuePointFinder"/>.</summary>
    private static string Cue(double at, double seconds) =>
        at > 0 ? $"{MixInputs.Sec(at)} ({Away(at - seconds)})" : "not measured";

    private static string Away(double seconds) =>
        seconds >= 0 ? $"in {seconds:F1}s" : $"{-seconds:F1}s ago";

    private void Level(string name, double value, Color color)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        var label = Line(name);
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var bar = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var holder = new Grid();
        holder.Children.Add(track);
        holder.Children.Add(bar);
        holder.SizeChanged += (_, _) => bar.Width = Math.Max(0, holder.ActualWidth * Math.Clamp(value, 0, 1));
        Grid.SetColumn(holder, 1);
        row.Children.Add(holder);

        var number = Line($"{value:F2}");
        Grid.SetColumn(number, 2);
        row.Children.Add(number);

        _levels.Children.Add(row);
    }

    private static TextBlock Line(string text = "") => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x90, 0x9A)),
        TextWrapping = TextWrapping.Wrap,
    };
}
#endif
#endif
