using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Main.LeftPane;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The album card for a record that is several things under one title: a page for each, with dots under
/// the card to go between them. The track pane's card does the same (see TrackInfoView.AlbumPages), and
/// the two cut a title into the same parts.
///
/// <para><b>Why.</b> "Eine kleine Nachtmusik / Divertimento / Ein musikalischer Spaß" has no article of
/// its own, so the card had only the catalogue's fact line to say about a record of three well-known
/// works. Asked one at a time, each part has its own. A two-in-one, "Album A / Album B", is the same
/// shape and gets the same pages.</para>
///
/// <para><b>Which page opens.</b> The whole title, where it has an article of its own; otherwise the
/// first part. A page somebody turned to stays put when the record's own answer lands after it.</para>
/// </summary>
public sealed partial class AlbumDetailView
{
    /// <summary>The record the papers were last asked about, and the token that ends with it. A part's
    /// lookup is asked about the same act, and given up when the record is.</summary>
    private string _papersArtist = "";

    private string _papersTitle = "";
    private CancellationToken _papersRecord;

    /// <summary>The pages of the card, in order: the whole title first where it has an article, then the
    /// parts. Empty for a record that is one thing.</summary>
    private IReadOnlyList<AlbumPart> _albumParts = [];

    private int _albumAt;
    private CancellationTokenSource? _albumPartLookup;

    /// <summary>The dossier the whole title was answered with, for its own page.</summary>
    private TrackDossier _albumWhole = TrackDossier.Empty;

    private Storyboard? _albumTurn;

    /// <summary>One page: what it is called, and whether it is the whole record rather than one
    /// part.</summary>
    private readonly record struct AlbumPart(string Title, bool Whole);

    private void ClearAlbumPages()
    {
        _albumPartLookup?.Cancel();
        _albumParts = [];
        _albumAt = 0;
        _albumWhole = TrackDossier.Empty;
        PaintAlbumDots();
    }

    /// <summary>Opens the card on its pages where the record is several things. False where it is one,
    /// and the card is painted as it always was.</summary>
    private bool PageAlbumParts(TrackDossier papers)
    {
        var parts = TrackInfoView.AlbumParts(_papersTitle);
        if (parts.Count < 2 || string.IsNullOrWhiteSpace(_papersArtist))
            return false;

        var pages = new List<AlbumPart>(parts.Count + 1);
        if (papers.AlbumArticle is not null)
            pages.Add(new AlbumPart(_papersTitle, true));
        pages.AddRange(parts.Select(p => new AlbumPart(p, false)));

        // The second paint of one record (the cache first, then the answer) keeps the page that is
        // showing, so a dot pressed during the wait is not undone by the answer.
        var showing = _albumAt >= 0 && _albumAt < _albumParts.Count ? _albumParts[_albumAt] : (AlbumPart?)null;
        var opening = showing is { } was ? Math.Max(0, pages.IndexOf(was)) : 0;

        _albumWhole = papers;
        _albumParts = pages;
        _albumAt = -1;
        SelectAlbumPart(opening);
        return true;
    }

    /// <summary>Turns the card to one page: the page slides in from the side its dot is on, and grows
    /// into its article when that arrives.</summary>
    private async void SelectAlbumPart(int at)
    {
        if (at < 0 || at >= _albumParts.Count)
            return;

        _albumPartLookup?.Cancel();

        // A first page simply arrives; a dot pressed travels from its side.
        var way = _albumAt < 0 || at == _albumAt ? 0 : at > _albumAt ? 1 : -1;
        _albumAt = at;
        _albumFolded = true;
        PaintAlbumDots();   // the dot moves on the press, not on the answer

        var part = _albumParts[at];
        if (part.Whole)
        {
            _albumArticle = _albumWhole.AlbumArticle;
            PaintAlbumPage(_albumWhole.Album, part.Title, way);
            return;
        }

        var service = App.Services.GetRequiredService<TrackInfoService>();
        if (service.Cached(_papersArtist, part.Title) is { } known)
        {
            _albumArticle = known.AlbumArticle;
            PaintAlbumPage(known.Album, part.Title, way);
            return;
        }

        // Nothing known yet: the part's name, so the card is never blank while the answer is on its way.
        _albumArticle = null;
        PaintAlbumPage(null, part.Title, way);

        var title = _papersTitle;
        var lookup = CancellationTokenSource.CreateLinkedTokenSource(_papersRecord);
        _albumPartLookup = lookup;
        try
        {
            var asked = await service.GetAsync(_papersArtist, part.Title, ct: lookup.Token);
            if (lookup.IsCancellationRequested || _papersTitle != title || _albumAt != at
                || !asked.Answers(out var dossier, out _))
                return;

            _albumArticle = dossier.AlbumArticle;
            PaintAlbumPage(dossier.Album, part.Title, 0);   // the same page, growing into what came back
        }
        catch (OperationCanceledException)
        {
            // Another page, or another record.
        }
        catch
        {
            // No answer: the page keeps the part's name.
        }
        finally
        {
            if (ReferenceEquals(_albumPartLookup, lookup))
                _albumPartLookup = null;
            lookup.Dispose();
        }
    }

    /// <summary>Paints one page and moves it there: in from the side for a dot pressed, and in every
    /// case from the height the card had to the height the new page needs, so the cards under it slide
    /// down rather than jump.</summary>
    private void PaintAlbumPage(AlbumProfile? album, string part, int way)
    {
        var was = AlbumPapersBlock.Visibility == Visibility.Visible ? AlbumPapersPage.ActualHeight : 0;
        PaintAlbumPapers(album, part);

        _albumTurn?.Stop();
        AlbumPapersShift.X = 0;
        AlbumPapersPage.Opacity = 1;
        AlbumPapersPage.Height = double.NaN;

        var turn = new Storyboard();
        if (way != 0)
        {
            // Set before the animation as well as animated from, so the first frame is already off its
            // mark.
            AlbumPapersShift.X = way * PageTravel;
            AlbumPapersPage.Opacity = 0;
            turn.Children.Add(Move(AlbumPapersShift, "X", way * PageTravel, 0, dependent: false));
            turn.Children.Add(Move(AlbumPapersPage, "Opacity", 0, 1, dependent: false));
        }

        if (was > 0.5 && AlbumPapersPage.ActualWidth > 0)
        {
            AlbumPapersPage.Measure(new Windows.Foundation.Size(AlbumPapersPage.ActualWidth, double.PositiveInfinity));
            var wants = AlbumPapersPage.DesiredSize.Height;
            if (wants > 0 && Math.Abs(was - wants) > 0.5)
            {
                AlbumPapersPage.Height = was;
                turn.Children.Add(Move(AlbumPapersPage, "Height", was, wants, dependent: true));

                // Back to Auto at the end, always: a page left at one work's height would clip the next.
                turn.Completed += (_, _) => AlbumPapersPage.Height = double.NaN;
            }
        }

        if (turn.Children.Count == 0)
            return;

        _albumTurn = turn;
        turn.Begin();

        static DoubleAnimation Move(DependencyObject target, string property, double from, double to, bool dependent)
        {
            var move = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(PageTravelTime),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                // Height is a layout property; its animation has to say so, or it is dropped.
                EnableDependentAnimation = dependent,
            };
            Storyboard.SetTarget(move, target);
            Storyboard.SetTargetProperty(move, property);
            return move;
        }
    }

    /// <summary>How far off to the side a page starts, and how long it takes to arrive: the track
    /// pane's numbers, so a page turns the same way on both.</summary>
    private const double PageTravel = 26;

    private static readonly TimeSpan PageTravelTime = TimeSpan.FromMilliseconds(220);

    private void AlbumPapersClip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement window)
            window.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
            };
    }

    /// <summary>The dots under the album card: one per page, the one showing lit.</summary>
    private void PaintAlbumDots()
    {
        AlbumPapersDots.Children.Clear();
        AlbumPapersDots.Visibility = _albumParts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_albumParts.Count <= 1)
            return;

        for (var i = 0; i < _albumParts.Count; i++)
        {
            var at = i;
            var button = new Button
            {
                Content = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = PapersStrongBrush,
                    Opacity = i == _albumAt ? 1 : 0.3,
                },
                Width = 20,
                Height = 18,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            button.Resources["ButtonBackgroundPointerOver"] = PapersPlateLitBrush;
            button.Resources["ButtonBackgroundPressed"] = PapersPlateBrush;

            ToolTipService.SetToolTip(button, _albumParts[i].Title);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, _albumParts[i].Title);
            button.Click += (_, _) => SelectAlbumPart(at);
            AlbumPapersDots.Children.Add(button);
        }
    }
}
