using RPM.Core.Metadata;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RPM.Desktop.Presentation.Controls;
using Windows.Foundation; // Size: the measure ArriveAbout takes before it fixes the block's height
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// What the free encyclopedias say about a record, under what its own files say: the opening paragraph of
/// its article, and the genres the catalogues file it under.
///
/// <para>Those two and nothing else. The card in the library window carries the record's title, its year
/// and two links out as well, and every one of those is either already on the screen a few lines above or a
/// door out of a drawer with nowhere to send anybody.</para>
///
/// <para>Both album screens use it, the one a cover on the wall opens and the one a row in the drawer
/// opens, because it is the same question about the same record, and a listener who reads about a record in
/// one of them should not find the other silent about it.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>The same service that the pane in the library window uses. A record read about in one place
    /// is not looked up again in the other. Both go through one memory cache, one folder on disk and one
    /// queue to a catalogue that allows one request each second.</summary>
    private static TrackInfoService Dossiers => App.Services.GetRequiredService<TrackInfoService>();

    /// <summary>
    /// Fills a slot that is already standing in the right place with what is known about a record.
    ///
    /// <para>The slot is the point. The lookup takes seconds, and these screens are laid out in one pass. A
    /// block added whenever the catalogue answered would land under the tracklist, not under the record it
    /// is about. So the slot is added empty and collapsed, and it takes no room until there is something in
    /// it.</para>
    ///
    /// <para><paramref name="stillOpen"/> is a function rather than a token, because the two screens count
    /// their openings separately, and each knows its own. It is asked after the wait. So an answer about a
    /// record that the person has already left cannot paint over the one they are looking at.</para>
    ///
    /// <para>Nothing is shown while it waits, and nothing is shown if it fails. This is an aside about a
    /// record whose sleeve is already on the screen. A spinner would promise something worth waiting for,
    /// and a line saying that the catalogues have never heard of it would be a paragraph about an
    /// absence.</para>
    /// </summary>
    /// <param name="grew">Told how much taller the panel just became, once, when the block lands after a
    /// wait. Each screen answers for its own layout, see ArriveAbout.</param>
    /// <param name="couch">At the TV's couch sizes, as the record page is (PAGE_TEXT: the article and the
    /// genres 19, the article's lines 27.4 apart), rather than the drawer's 12.5, 18 and 11.</param>
    private async void ShowAboutAsync(
        StackPanel slot, string? artist, string? album, int tracks, Func<bool> stillOpen, double scale,
        Action<double>? grew = null, bool couch = false)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return;

        // Whatever is already known lands on this frame. Opening the same record twice should not fade its
        // own blurb in again, and a record that has played is a record already asked about.
        var dossier = Dossiers.Cached(artist, album);
        var waited = dossier is null;

        // The test is on the dossier itself rather than on waited. The two say the same thing here, but the
        // compiler follows a null test on the value itself. The block below returns when the dossier is
        // null, so after it the dossier is known to be set on both paths.
        if (dossier is null)
        {
            // The turning record where the words will be. It is shown only on a real wait. A catalogue that
            // answers in milliseconds from the cache would show a spinner as a flicker, which looks like a
            // fault.
            var waiting = new RecordSpinner
            {
                Spinning = true,
                Width = 24 * scale,
                Height = 24 * scale,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, AboutLead * 2 * scale, 0, 0),
            };
            slot.Children.Add(waiting);
            slot.Visibility = Visibility.Visible;

            try
            {
                // Not cancelled when the record is left, on purpose. The service keys its in-flight lookups
                // by the same question, so the answer this abandons is the one waiting the next time
                // anybody opens this record. Dropping it would only mean asking again. An unanswered lookup
                // is not the same as an empty result. The slot is closed in both cases.
                dossier = (await Dossiers.GetAsync(artist, album, tracks)).Answers(out var answer, out _)
                    ? answer
                    : null;
            }
            catch
            {
                // Offline, rate-limited, or a catalogue that said no. The screen is whole without this.
                dossier = null;
            }

            // The spinner is stopped whatever happened, and before the open check. A spinner left turning
            // in a page that was closed during the wait would keep repainting the window behind whatever is
            // up now (see RecordSpinner).
            waiting.Spinning = false;

            if (!stillOpen())
                return;

            // The record goes whatever the answer was. The slot goes back to taking no room until there is
            // something to put in it.
            slot.Children.Clear();
            slot.Visibility = Visibility.Collapsed;

            if (dossier is null)
                return;
        }

        if (!stillOpen())
            return;

        var prose = dossier.AlbumArticle?.Lead;
        var genres = Shelved(dossier.Album?.Tags ?? []);
        if (string.IsNullOrWhiteSpace(prose) && genres.Count == 0)
            return;

        // No heading over it. "About the album" on a screen that is nothing but one album would only repeat
        // what the reader can already see. The block simply carries on down the column at the spacing the
        // lines above it keep.
        var first = true;

        if (genres.Count > 0)
        {
            var chips = new WrapRow
            {
                HorizontalSpacing = 6 * scale,
                VerticalSpacing = 6 * scale,
                Margin = new Thickness(6 * scale, Lead(), 10 * scale, 0),
            };
            foreach (var genre in genres)
                chips.Children.Add(Chip(genre, scale, couch ? RecordPageText : 11));
            slot.Children.Add(chips);
        }

        if (!string.IsNullOrWhiteSpace(prose))
            slot.Children.Add(new TextBlock
            {
                Text = Shorten(prose!, slot.ActualWidth, (couch ? RecordPageText : 12.5) * scale),
                FontSize = (couch ? RecordPageText : 12.5) * scale,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = (couch ? 27.4 : 18) * scale,

                // The ceiling, whatever the sentence arithmetic in Shorten made of the width. Ten lines is
                // what the panel can hold without pushing the record above it off the screen. The limit has
                // to hold every time, not only usually, because the panel does not scroll.
                MaxLines = AboutMaxLines,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6 * scale, Lead(), 10 * scale, 0),
            });

        slot.Visibility = Visibility.Visible;

        // Faded in, and only when it was waited for. An answer already in hand belongs to the screen being
        // built, so it arrives with the rest of it. An answer that lands seconds later changes a page
        // somebody is already reading, so it fades in rather than appearing at once.
        if (waited)
            ArriveAbout(slot, grew);

        // The gap above whichever of the two comes first. It is the same gap the count line keeps from the
        // artist above it, so this reads as the next line down, not as a section of its own. Everything
        // after it is spaced by the slot itself.
        double Lead()
        {
            if (!first)
                return 0;
            first = false;
            return AboutLead * scale;
        }
    }

    /// <summary>The air above the block: the count line's own gap from the name and artist above it, so the
    /// record's facts and what is said about them read as one column of lines.</summary>
    private const double AboutLead = 12;

    /// <summary>The most lines of prose the panel shows. The panel does not scroll. It stands outside the
    /// scroller so it can stay put. So a long article would push the record off the screen with no way to
    /// get it back.</summary>
    private const int AboutMaxLines = 10;

    /// <summary>How long the words take to come up. Slow on purpose: this arrives seconds after the page
    /// did, on a screen somebody is already reading. Anything quicker looks like a flash.</summary>
    private static readonly TimeSpan AboutFade = TimeSpan.FromMilliseconds(900);

    /// <summary>
    /// Lets the block arrive. The words fade up, and the owner of the panel is told how much taller it just
    /// became, so it can move to match.
    ///
    /// <para>The measure is handed out rather than acted on here. How a panel should move when it grows
    /// depends on the layout it sits in, and the two screens that use this block do not answer it the same
    /// way. The record's panel is centred, so it opens around its own middle and has to move up by half of
    /// what it gained. The drawer's panel is a column in a scroller, where nothing above the block moves at
    /// all.</para>
    ///
    /// <para>The height is not animated. Animating it meant a dependent animation: a measure of the panel
    /// and an arrange of the whole page on every frame, with a mask surface copying the tracklist and a
    /// shader running behind it. The screen flickered under that load. The block now arrives at its full
    /// height at once, and a transform carries it up. That costs the compositor only.</para>
    ///
    /// <para>The fade holds its end rather than stopping. A storyboard that stops puts its property back to
    /// the value it had before it ran, which is zero here. The last frame of the fade would then be
    /// followed by one fully transparent frame, before the handler put it right. That single frame was the
    /// flicker.</para>
    /// </summary>
    private static void ArriveAbout(StackPanel slot, Action<double>? grew)
    {
        // The width it will actually wrap against: the panel it stands in. It is measured against that,
        // rather than against infinity, so the paragraph does not come back one very long line tall.
        var room = slot.Parent as FrameworkElement;
        var width = room?.ActualWidth is > 1 and var w ? w : RecordInfoCap;

        slot.Measure(new Size(width, double.PositiveInfinity));
        grew?.Invoke(slot.DesiredSize.Height);

        slot.Opacity = 0;

        var rise = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(AboutFade),
            // Out rather than in-out: it leaves at once and settles. That is what something arriving on a
            // page already being read should look like. An ease-in would read as a hesitation.
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(rise, slot);
        Storyboard.SetTargetProperty(rise, "Opacity");

        var board = new Storyboard();
        board.Children.Add(rise);
        Services.Shell.Storyboards.Play(board);
    }

    /// <summary>
    /// The article's opening, cut to fit <see cref="AboutMaxLines"/> lines and cut at a full stop.
    ///
    /// <para>There are two parts, and each answers a different half of the problem. MaxLines on the block
    /// is what guarantees ten lines. But it clips wherever the tenth line happens to end, which is often in
    /// the middle of a word, and it gives the reader an ellipsis in the middle of a name. So the text is
    /// cut here first, at the last sentence that fits. The block's own limit stays as the backstop for when
    /// this estimate is too long.</para>
    ///
    /// <para>This is an estimate. How many characters fit on a line depends on the font's metrics, and
    /// nothing here measures a glyph. The divisor is generous on purpose. Cutting too much loses a
    /// sentence. Cutting too little leaves the ragged clip this exists to avoid. So the estimate errs
    /// towards the shorter text.</para>
    ///
    /// <para>A single sentence longer than the whole budget is left alone, rather than cut at a space. It
    /// has no full stop to cut at, and MaxLines will handle it.</para>
    /// </summary>
    private static string Shorten(string prose, double width, double fontSize)
    {
        // Before layout has given the slot a width, take the panel at its widest. The cut is then as
        // generous as the screen could allow, and the block's own limit catches the rest.
        var usable = width > 1 ? width : RecordInfoCap;
        var perLine = Math.Max(24, usable / (fontSize * AboutGlyphWidth));
        var budget = (int)(perLine * AboutMaxLines);

        if (prose.Length <= budget)
            return prose;

        // The last sentence that ends inside the budget. The search includes the character at the budget
        // boundary, so a full stop sitting exactly on the boundary still counts.
        var cut = prose.LastIndexOfAny(['.', '!', '?'], Math.Min(prose.Length - 1, budget));
        return cut > 0 ? prose[..(cut + 1)] : prose;
    }

    /// <summary>How wide a character is taken to be, as a fraction of the font size. The estimate is
    /// generous, so the cut lands short of the ceiling rather than past it.</summary>
    private const double AboutGlyphWidth = 0.58;

    /// <summary>The empty block to stand in the layout until there is something to put in it.</summary>
    private static StackPanel AboutSlot(double scale)
        => new() { Spacing = 8 * scale, Visibility = Visibility.Collapsed };

    /// <summary>
    /// The genres, mapped onto the ones this app actually shelves by. This is the same reading that the
    /// library window's card gives, using the same table.
    ///
    /// <para>Anything that does not map is dropped rather than shown as it is. A chip with a name the rest
    /// of the app does not know is a label the reader cannot act on. It matches no shelf, no filter and
    /// nothing in the picker.</para>
    /// </summary>
    private static List<string> Shelved(IReadOnlyList<string> tags)
    {
        var shelved = new List<string>();
        foreach (var tag in tags)
            if (Genres.Match(tag) is { } genre && !shelved.Contains(genre, StringComparer.OrdinalIgnoreCase))
                shelved.Add(genre);
        return shelved;
    }

    /// <summary>One genre, shown in the drawer's style: a white, semi-transparent pill. It is the same
    /// plate that the count under the record's name is written on.</summary>
    private static Border Chip(string text, double scale, double size) => new()
    {
        Padding = new Thickness(9 * scale, 3 * scale, 9 * scale, 3 * scale),
        CornerRadius = new CornerRadius(10 * scale),
        Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
        Child = new TextBlock
        {
            Text = text,
            FontSize = size * scale,
            Opacity = 0.85,
        },
    };
}
