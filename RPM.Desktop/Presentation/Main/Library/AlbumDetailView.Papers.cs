using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Metadata.Providers;
using RPM.Core.Net;
using RPM.Desktop.Converters;
using RPM.Desktop.Services.Catalog;
using RPM.Desktop.Strings;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The papers panel: what the free catalogues have written down about this record, and about the act
/// that made it.
///
/// <para><b>The cache answers first, always.</b> <see cref="TrackInfoService.Cached"/> checks what is
/// already in memory or on disk, without waiting. The two cards are painted from it before anything is
/// requested. Only then is <see cref="TrackInfoService.GetAsync"/> awaited. It reads the same two
/// caches again and goes to the network only where they miss. So a record opened before, or one that
/// has been playing with the track pane open, shows its papers at once. A record nobody has looked up
/// yet shows an empty column for the time MusicBrainz's limit of one request per second takes, and then
/// it fills.</para>
///
/// <para>The same service is used by the track pane and the artist page, so the three surfaces agree on
/// who an artist is and which release group a record belongs to.</para>
/// </summary>
public sealed partial class AlbumDetailView
{
    /// <summary>The two articles, kept so the fold and the source links have something to work on after
    /// the cards are painted.</summary>
    private EncyclopediaArticle? _albumArticle;
    private EncyclopediaArticle? _artistArticle;

    /// <summary>Whether each card shows its lead paragraph or the whole article. Both start folded. The
    /// panel holds four blocks, and a long biography at the top would push the doors below the point a
    /// reader scrolls to.</summary>
    private bool _albumFolded = true;
    private bool _artistFolded = true;

    /// <summary>
    /// Reads the papers for one record and paints them, cache first.
    ///
    /// <para>Started beside the other three loads when the page opens, and never waited on. The record
    /// is already drawn, and this fills in around it. It is cancelled with the rest when the page
    /// closes or another record is opened on top of it.</para>
    /// </summary>
    /// <param name="tracks">How many songs the record has. A single and an album with the same title
    /// are separate entries in the catalogue. The song count tells them apart.</param>
    private async Task PapersAsync(string artist, string album, int tracks, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            ShowPapers(TrackDossier.Empty, PapersStanding.Answered);
            return;
        }

        var service = App.Services.GetRequiredService<TrackInfoService>();

        // What is already known, read on this frame. Nothing is requested and nothing is awaited, so a
        // record opened a second time never flashes its empty state.
        var known = service.Cached(artist, album);
        ShowPapers(known ?? TrackDossier.Empty, known is not null ? PapersStanding.Answered : PapersStanding.Asking);

        Asked<TrackDossier> asked;
        try
        {
            asked = await service.GetAsync(artist, album, tracks, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            asked = Asked.Failed<TrackDossier>($"{ex.GetType().Name}: {ex.Message}");
        }

        // Not this record's column any more. A cancelled read can finish on its way out. Painting it
        // over the record that replaced it would settle a column whose own lookup is still running.
        if (ct.IsCancellationRequested)
            return;

        // No network, a rate limit or a server error: the column says the information could not be
        // loaded. It never says there is none. The cache is not checked again here, because GetAsync
        // checks it before it asks anybody. So a failure means the cache held nothing.
        if (asked.Answers(out var papers, out _))
            ShowPapers(papers, PapersStanding.Answered);
        else
            ShowPapers(known ?? TrackDossier.Empty, PapersStanding.Unanswered);
    }

    /// <summary>Where the column's lookup stands. There are three states, because "nothing is known
    /// about this record" and "the question got no answer" are different things to say.</summary>
    private enum PapersStanding
    {
        /// <summary>Asked, and no answer yet.</summary>
        Asking,

        /// <summary>The answer arrived. What it holds is what the catalogues know.</summary>
        Answered,

        /// <summary>The API or Wikipedia gave no answer. Nothing is known either way.</summary>
        Unanswered,
    }

    /// <summary>
    /// Paints the panel from one dossier.
    ///
    /// <para>Every block asks the same question of itself: is there anything here to read? A block with
    /// nothing to read hides itself. A heading over an empty card is a heading over a gap in the
    /// data.</para>
    /// </summary>
    /// <param name="standing">Where the lookup stands. While it is still running, an empty panel shows
    /// nothing at all. When it did not answer, the panel says so. It never says that nothing is written
    /// about this record, because only an answer can say that.</param>
    private void ShowPapers(TrackDossier papers, PapersStanding standing)
    {
        _albumArticle = papers.AlbumArticle;
        _artistArticle = papers.ArtistArticle;
        _albumFolded = true;
        _artistFolded = true;

        PaintAlbumPapers(papers.Album);
        PaintArtistPapers(papers.Artist);
        PaintLinks(papers.Links);

        var anything = AlbumPapersBlock.Visibility == Visibility.Visible
                       || ArtistPapersBlock.Visibility == Visibility.Visible
                       || LinksBlock.Visibility == Visibility.Visible;

        PapersEmpty.Text = Loc.Get(standing == PapersStanding.Unanswered ? "Album_PapersUnanswered" : "Album_NoPapers");
        PapersEmpty.Visibility = !anything && standing != PapersStanding.Asking
            ? Visibility.Visible : Visibility.Collapsed;

        // The busy state: the spinning record and its text. They show only during the wait, when the
        // column has nothing in it yet and the answer is still out. This is decided here, from the same
        // two facts the empty line uses, so the four states of this column (busy, written, nothing
        // written, not answered) are decided in one place and cannot disagree. A card the cache already
        // answered goes straight to written, and the spinner never shows.
        //
        // The pressing well is hidden with them. It is read from the shelf, not from the network, so it
        // could show during the wait. That would look like a finished card with a spinning record above
        // it, which reads as stopped rather than still loading. So the column is either busy or
        // written.
        var busy = !anything && standing == PapersStanding.Asking;
        PapersBusy.Spinning = busy;
        PapersBusyBlock.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PressingWell.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── the record ──

    private void PaintAlbumPapers(AlbumProfile? album)
    {
        var facts = AlbumFactLine(album);
        var tags = album?.Tags ?? [];
        var worth = facts.Length > 0 || tags.Count > 0 || _albumArticle is not null;

        AlbumPapersBlock.Visibility = worth ? Visibility.Visible : Visibility.Collapsed;
        if (!worth)
            return;

        // The catalogue's name is censored in the same way as the name under the sleeve, so the two
        // lines agree about what may be printed.
        AlbumPapersName.Text = TitleCensor.Shown(album?.Title ?? _albumArticle?.Title) ?? "";
        AlbumPapersName.Visibility = AlbumPapersName.Text.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;

        AlbumPapersFacts.Text = facts;
        AlbumPapersFacts.Visibility = facts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        PaintChips(AlbumPapersGenres, tags);
        PaintArticle(_albumArticle, folded: true, AlbumPapersProse, AlbumPapersMore, AlbumPapersSource);
    }

    // ── the act ──

    private void PaintArtistPapers(ArtistProfile? artist)
    {
        var facts = ArtistFactLine(artist);
        var worth = facts.Length > 0 || _artistArticle is not null;

        ArtistPapersBlock.Visibility = worth ? Visibility.Visible : Visibility.Collapsed;
        if (!worth)
            return;

        ArtistPapersName.Text = artist?.Name ?? _artistArticle?.Title ?? "";
        ArtistPapersName.Visibility = ArtistPapersName.Text.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;

        ArtistPapersFacts.Text = facts;
        ArtistPapersFacts.Visibility = facts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        PaintArticle(_artistArticle, folded: true, ArtistPapersProse, ArtistPapersMore, ArtistPapersSource);
    }

    // ── an article, its fold, and the credit under it ──

    /// <summary>
    /// One card's prose: the lead paragraph or the whole text, the button that switches between them,
    /// and the credit that says where it came from.
    ///
    /// <para>Wikipedia asks to be named, so the source link is shown whenever there is an article. A
    /// reader should know where the text came from.</para>
    ///
    /// <para>The fold button is shown only when there is more than the lead to unfold. An article of
    /// one paragraph with a "Show more" button that adds nothing is worse than no button.</para>
    /// </summary>
    private static void PaintArticle(
        EncyclopediaArticle? article, bool folded, TextBlock prose, Button more, Button source)
    {
        if (article is null)
        {
            prose.Visibility = Visibility.Collapsed;
            more.Visibility = Visibility.Collapsed;
            source.Visibility = Visibility.Collapsed;
            return;
        }

        prose.Visibility = Visibility.Visible;
        prose.Text = folded ? article.Lead : article.Extract;

        more.Visibility = article.HasMore ? Visibility.Visible : Visibility.Collapsed;
        more.Content = Loc.Get(folded ? "TrackInfo_ShowMore" : "TrackInfo_ShowLess");

        source.Visibility = Visibility.Visible;
        source.Content = Loc.Get("TrackInfo_ReadOnWikipedia");
    }

    private void AlbumPapersMore_Click(object sender, RoutedEventArgs e)
    {
        _albumFolded = !_albumFolded;
        PaintArticle(_albumArticle, _albumFolded, AlbumPapersProse, AlbumPapersMore, AlbumPapersSource);
    }

    private void ArtistPapersMore_Click(object sender, RoutedEventArgs e)
    {
        _artistFolded = !_artistFolded;
        PaintArticle(_artistArticle, _artistFolded, ArtistPapersProse, ArtistPapersMore, ArtistPapersSource);
    }

    private void AlbumPapersSource_Click(object sender, RoutedEventArgs e) => Open(_albumArticle?.Url);

    private void ArtistPapersSource_Click(object sender, RoutedEventArgs e) => Open(_artistArticle?.Url);

    // ── the genres ──

    /// <summary>
    /// The record's genres as chips, put through the app's own shelf of names first.
    ///
    /// <para>MusicBrainz tags are free text. "rock", "classic rock", "album rock" and "rock music" are
    /// four tags for one genre, and four chips would say the same thing four times. After matching to
    /// the app's own genres and removing duplicates, what is left is the two or three a reader would
    /// name.</para>
    /// </summary>
    private void PaintChips(LeftPane.Controls.WrapRow row, IReadOnlyList<string> values)
    {
        var shelved = new List<string>();
        foreach (var value in values)
            // Written in full. This file is in Presentation.Main, where the Main.Genres namespace (the
            // genres wall) is closer than Core's class of the same name, so the class is named in full.
            if (RPM.Core.Metadata.Genres.Match(value) is { } genre
                && !shelved.Contains(genre, StringComparer.OrdinalIgnoreCase))
                shelved.Add(genre);

        row.Children.Clear();
        row.Visibility = shelved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var value in shelved)
            row.Children.Add(new Border
            {
                Background = PapersPlateBrush,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3, 9, 3),
                Child = new TextBlock
                {
                    Text = value,
                    FontSize = 11,
                    Foreground = PapersInkBrush,
                },
            });
    }

    // ── the doors out ──

    /// <summary>
    /// The only sites this box offers, in display order. These are the sites the track pane lists: the
    /// record's official site and the places the act posts itself. Streaming services are not listed,
    /// although a listener may use them for this music.
    ///
    /// <para>Wikipedia is left out on purpose, although it is known. The two cards above this box
    /// already link to that page, so listing it again would repeat the link.</para>
    /// </summary>
    private static readonly ReferenceSite[] ElsewhereSites =
    [
        ReferenceSite.OfficialSite,
        ReferenceSite.Discogs,
        ReferenceSite.Bandcamp,
        ReferenceSite.Facebook,
        ReferenceSite.X,
        ReferenceSite.Instagram,
    ];

    private void PaintLinks(IReadOnlyList<ReferenceLink> links)
    {
        LinksRow.Children.Clear();

        // The list is filtered, then put in this box's order, not the catalogue's order. A filter that
        // kept the incoming order would move the doors around depending on which catalogue answered
        // first.
        var shown = new List<ReferenceLink>();
        foreach (var site in ElsewhereSites)
            if (links.FirstOrDefault(l => l.Site == site) is { } found)
                shown.Add(found);

        LinksBlock.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var link in shown)
        {
            // The name, then the address it goes to. The name alone is a row of labels that look alike.
            // The address shows WHICH page, for example which Discogs page.
            var face = new Grid { ColumnSpacing = 8 };
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = SiteName(link),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);

            // Dimmed rather than given a second colour. At 0.6 opacity it stands apart from the name
            // without a second colour having to be chosen.
            var address = new TextBlock
            {
                Text = link.Url,
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(address, 1);

            face.Children.Add(name);
            face.Children.Add(address);

            var button = new Button
            {
                Content = face,
                // The card padding the blocks above use, because this row is the card here. The
                // Elsewhere block has no plate of its own, and each door is one.
                Padding = new Thickness(14, 6, 14, 6),
                MinHeight = 0,
                MinWidth = 0,
                CornerRadius = new CornerRadius(6),
                // Stretched, so the doors form one column with one left edge, not a row of pills of
                // different widths. The address also needs a width to trim against.
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Foreground = PapersStrongBrush,
                Background = PapersPlateBrush,
                BorderThickness = new Thickness(0),
                Tag = link.Url,
            };

            // The pointer states come from the theme. A visual state overrides the values set above, so
            // hovering would bring back the theme's own white. The page's brushes are set in their
            // place. A row lights up by getting a little more plate, not by changing colour.
            button.Resources["ButtonForegroundPointerOver"] = PapersStrongBrush;
            button.Resources["ButtonForegroundPressed"] = PapersStrongBrush;
            button.Resources["ButtonBackgroundPointerOver"] = PapersPlateLitBrush;
            button.Resources["ButtonBackgroundPressed"] = PapersPlateBrush;

            ToolTipService.SetToolTip(button, link.Url);
            button.Click += (s, _) => Open((s as FrameworkElement)?.Tag as string);
            LinksRow.Children.Add(button);
        }
    }

    /// <summary>What a link is called. Named sites are brands and keep their own spelling in every
    /// language. Only "the act's own site" is a description, and only that is translated. Anything not
    /// recognised is labelled with its host, which is the most accurate label available.</summary>
    private static string SiteName(ReferenceLink link) => link.Site switch
    {
        ReferenceSite.OfficialSite => Loc.Get("TrackInfo_OfficialSite"),
        ReferenceSite.Other => link.Host,
        ReferenceSite.LastFm => "Last.fm",
        ReferenceSite.AppleMusic => "Apple Music",
        ReferenceSite.AllMusic => "AllMusic",
        ReferenceSite.YouTube => "YouTube",
        ReferenceSite.SoundCloud => "SoundCloud",
        _ => link.Site.ToString(),
    };

    /// <summary>Opens a link from catalogue data, http or https only (see WebLink).</summary>
    private static async void Open(string? url)
    {
        if (RPM.Core.Net.WebLink.Parse(url) is { } link)
            await Windows.System.Launcher.LaunchUriAsync(link.Uri);
    }

    // ── the two fact lines ──

    /// <summary>
    /// What kind of record it is and when it first came out, for example "Album · 1977" or "Album ·
    /// Live · 1970".
    ///
    /// <para>The track pane builds the same line from the same fields. Two copies of one shape can
    /// drift apart, so anyone who merges them should start here. They are not shared now, because the
    /// pane's copy is private to a file this page should not edit.</para>
    ///
    /// <para>Only the year is shown. A full release date is three facts where one is wanted. A day and
    /// a month say which pressing a cataloguer filed first, not anything about the record. Readers mean
    /// the year when they ask when an album came out.</para>
    /// </summary>
    private static string AlbumFactLine(AlbumProfile? album)
    {
        if (album is null)
            return "";

        var parts = new List<string>(4);
        if (album.Disambiguation is { Length: > 0 } note) parts.Add(note);
        if (album.PrimaryType is { Length: > 0 } type) parts.Add(type);
        parts.AddRange(album.Kinds);
        if (ReleaseYear(album.FirstReleased) is { Length: > 0 } year) parts.Add(year);

        return string.Join("  ·  ", parts);
    }

    /// <summary>Who they are and where they are from, for example "Group · United Kingdom · 1972". It
    /// has the same shape as the line above, so the same note about two copies applies.</summary>
    private static string ArtistFactLine(ArtistProfile? artist)
    {
        if (artist is null)
            return "";

        var parts = new List<string>(4);
        if (artist.Kind is { Length: > 0 } kind) parts.Add(kind);
        if (artist.Area is { Length: > 0 } area) parts.Add(area);
        if (ReleaseYear(artist.Began) is { Length: > 0 } began) parts.Add(began);

        return string.Join("  ·  ", parts);
    }

    /// <summary>The year off a MusicBrainz date, which may be "1967", "1967-07" or
    /// "1967-07-12".</summary>
    private static string ReleaseYear(string? date)
        => date is { Length: >= 4 } && int.TryParse(date[..4], out _) ? date[..4] : "";

    // ── the panel's own ink ──
    //
    // Built once and shared by every chip and every door. A brush per row would mean a brush per record
    // opened, and this panel can hold a dozen of each.

    private static readonly Brush PapersPlateBrush =
        new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

    private static readonly Brush PapersPlateLitBrush =
        new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));

    private static readonly Brush PapersStrongBrush =
        new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private static readonly Brush PapersInkBrush =
        new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));
}
