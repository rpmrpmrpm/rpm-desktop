using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The album card for a record that is several things under one title: a page for each, with dots under
/// the card to go between them.
///
/// <para><b>Why.</b> A classical record often holds several works: "Eine kleine Nachtmusik / Divertimento /
/// Ein musikalischer Spaß". No encyclopedia has an article about that title, so the card had nothing to say
/// about a record that is three well-known pieces. Asked one at a time, each part has its own article (the
/// server describes a classical record by the work it records). A two-in-one, "Album A / Album B", is the
/// same shape and gets the same pages.</para>
///
/// <para><b>Which page opens.</b> The work the playing track belongs to, where its tags name one; otherwise
/// the first. Where the whole title does have an article of its own (a compilation known by that name), it
/// is the first page, and the parts follow it.</para>
///
/// <para>The parts are cut on " / ", spaces and all, so "AC/DC" and "Live/Dead" stay whole.</para>
/// </summary>
public sealed partial class TrackInfoView
{
    /// <summary>The pages of the card, in order: the whole title first where it has an article, then the
    /// parts. Empty for a record that is one thing.</summary>
    private IReadOnlyList<AlbumPart> _albumParts = [];

    private int _albumAt;
    private CancellationTokenSource? _albumPartLookup;

    /// <summary>The dossier the whole title was answered with, for its own page.</summary>
    private TrackDossier _albumWhole = TrackDossier.Empty;

    /// <summary>The work the playing track is a movement of, from its tags, or null.</summary>
    private string? _trackWork;

    /// <summary>One page: what it is called, and whether it is the whole record rather than one part.</summary>
    private readonly record struct AlbumPart(string Title, bool Whole);

    /// <summary>The title of the page showing, for a card whose page has not been answered yet: the card
    /// still says which part it is about.</summary>
    private string? AlbumPartShowing =>
        _albumAt >= 0 && _albumAt < _albumParts.Count ? _albumParts[_albumAt].Title : null;

    /// <summary>The parts of a title, or nothing where it is one thing.</summary>
    internal static IReadOnlyList<string> AlbumParts(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || !title.Contains(" / ", StringComparison.Ordinal))
            return [];

        var parts = title.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parts.Count > 1 ? parts : [];
    }

    private void ClearAlbumPages()
    {
        _albumPartLookup?.Cancel();
        _albumParts = [];
        _albumAt = 0;
        _albumWhole = TrackDossier.Empty;
        PaintAlbumDots();
    }

    /// <summary>Opens the card on its pages where the record is several things. False where it is one, and
    /// the card is painted as it always was.</summary>
    private bool PageAlbumParts(TrackDossier dossier)
    {
        var parts = AlbumParts(_albumName);
        if (parts.Count < 2)
        {
            _albumParts = [];
            return false;
        }

        _albumWhole = dossier;
        var whole = dossier.AlbumArticle is not null;
        _albumParts = [.. (whole ? [new AlbumPart(_albumName!, true)] : Array.Empty<AlbumPart>())
                          .Concat(parts.Select(p => new AlbumPart(p, false)))];

        // The track's own work, where one of the parts is it.
        var opening = 0;
        if (_trackWork is { Length: > 0 } work)
        {
            var match = _albumParts.ToList().FindIndex(p => !p.Whole && Names(p.Title, work));
            if (match >= 0)
                opening = match;
        }

        _albumAt = -1;
        SelectAlbumPart(opening);
        return true;

        static bool Names(string part, string work)
        {
            var a = NameMatch.Squash(part);
            var b = NameMatch.Squash(work);
            return a.Length > 0 && b.Length > 0 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
        }
    }

    /// <summary>Turns the card to one page: the page slides in from the side its dot is on, and grows into
    /// its article when that arrives.</summary>
    private async void SelectAlbumPart(int at)
    {
        if (at < 0 || at >= _albumParts.Count)
            return;

        _albumPartLookup?.Cancel();

        // A first page simply arrives; a dot pressed travels from its side.
        var way = _albumAt < 0 || at == _albumAt ? 0 : at > _albumAt ? 1 : -1;
        _albumAt = at;
        _albumExpanded = false;
        PaintAlbumDots();   // the dot moves on the press, not on the answer

        var part = _albumParts[at];
        if (part.Whole)
        {
            _albumArticle = _albumWhole.AlbumArticle;
            PaintAlbumPage(_albumWhole.Album, way);
            return;
        }

        if (Dossiers.Cached(_artistCredit, part.Title) is { } known)
        {
            _albumArticle = known.AlbumArticle;
            PaintAlbumPage(known.Album, way);
            return;
        }

        // Nothing known yet: the part's name, so the card is never blank while the answer is on its way.
        _albumArticle = null;
        PaintAlbumPage(null, way);

        var path = _path;
        var lookup = new CancellationTokenSource();
        _albumPartLookup = lookup;
        try
        {
            var asked = await Dossiers.GetAsync(_artistCredit, part.Title, ct: lookup.Token);
            if (lookup.IsCancellationRequested || _path != path || _albumAt != at
                || !asked.Answers(out var dossier, out _))
                return;

            _albumArticle = dossier.AlbumArticle;
            PaintAlbumPage(dossier.Album);   // the same page, growing into what came back
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

    /// <summary>The dots under the album card: one per page, the one showing lit. The artist card's dots,
    /// drawn the same way (see PaintArtistDots).</summary>
    private void PaintAlbumDots()
    {
        AlbumDots.Children.Clear();
        AlbumDots.Visibility = _albumParts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
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
                    Fill = Ink("InkStrong"),
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
            button.Resources["ButtonBackgroundPointerOver"] = Ink("InkPlateLit");
            button.Resources["ButtonBackgroundPressed"] = Ink("InkPlate");

            ToolTipService.SetToolTip(button, _albumParts[i].Title);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, _albumParts[i].Title);
            button.Click += (_, _) => SelectAlbumPart(at);
            AlbumDots.Children.Add(button);
        }
    }
}
