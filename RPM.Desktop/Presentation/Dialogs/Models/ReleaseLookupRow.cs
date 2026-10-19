using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Metadata.Providers;
using RPM.Core.Splitting;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs.Models;

/// <summary>One release, as the lookup window lists it. It shows the cover of that pressing and the
/// few facts that tell it apart from the others: where and when it came out, on what, and under which
/// catalogue number. It also shows a <b>Match</b>. This says how well the track lengths line up with
/// the recording. The reason for the number is one click away.
///
/// <para>The match appears after the row does. Scoring a release means fetching its track listing. Each
/// fetch takes about a second, because requests are rate limited. So the row shows what it knows, and
/// says "checking" until the rest arrives. The number is honest about what it can measure. A correct
/// track count with no listed times is a strong match that the lengths cannot confirm. The row shows it
/// that way, and not as a low percentage.</para>
/// </summary>
[RpmBindable]
public sealed partial class ReleaseLookupRow : INotifyPropertyChanged
{
    private ReleaseMatch? _match;
    private bool _matchPending;
    private ReleaseDetail? _detail;
    private ImageSource? _cover;

    // Not required, although it is always set. Uno's bindable-metadata generator creates every
    // [Bindable] type with a parameterless constructor, and a required member does not compile through
    // that.
    public ReleaseSearchResult Release { get; init; } =
        new(new ReleaseRef("", ""), "", null, 0, null, null, null, 0);

    /// <summary>The full listing, fetched while the match is scored. It is passed to the split dialog
    /// when a release is picked, so the lookup is not paid for twice.</summary>
    /// <remarks>It raises change events for the photo icons as well as for itself. The number of photos
    /// a pressing has is read from this same document, so the icons can only appear once it
    /// arrives.</remarks>
    public ReleaseDetail? Detail
    {
        get => _detail;
        set
        {
            _detail = value;
            OnChanged();
            OnChanged(nameof(PhotosVisibility));
            OnChanged(nameof(PhotosText));
            OnChanged(nameof(PhotosTooltip));
        }
    }

    public string Title => Release.Title;

    /// <summary>Artist · 1977 · GB · 12" Vinyl · K 56344. These fields separate one pressing from the
    /// next. The catalogue number is stamped in the run-out groove of the record, so it is the one
    /// field the user can check against the physical record.
    /// </summary>
    public string Subtitle
    {
        get
        {
            var bits = new List<string>(6);
            if (Release.Artist is { Length: > 0 }) bits.Add(Release.Artist);
            if (Release.Year > 0) bits.Add(Release.Year.ToString());
            if (Release.Country is { Length: > 0 }) bits.Add(Release.Country);
            if (Release.Format is { Length: > 0 }) bits.Add(Release.Format);
            if (Release.CatalogNumber is { Length: > 0 }) bits.Add(Release.CatalogNumber);
            if (Release.Note is { Length: > 0 }) bits.Add(Release.Note);
            return string.Join(" · ", bits);
        }
    }

    /// <summary>Where the pressing's sleeve is, according to the source. Null when no archive holds
    /// one. Also null at first for every release whose search result came without a picture.
    ///
    /// <para>This is settable. For those rows the address is not known until the list finds one. What
    /// it finds must survive the row being picked. The search result that the window returns is the
    /// only thing the later sleeve fetch can use. A row that worked out its own address and then
    /// discarded it would leave that fetch with nothing.</para>
    /// </summary>
    public string? CoverUrl { get; set; }

    /// <summary>The sleeve itself, once its bytes have arrived. Null until then, and always null for a
    /// pressing with no picture.
    /// </summary>
    /// <remarks>Fetched by the window through <see cref="Common.RemoteImage"/>, not handed to a
    /// <c>BitmapImage</c> as a URL. The app has one way to fetch a picture, and this is it. It sends
    /// the user agent that the archives expect. It asks a second time when a storage node returns a 5xx
    /// error, so the cover is not lost for the life of the row. It also matters here: the bitmap exists
    /// only if the bytes did. So anything drawn from this property states a fact, and does not repeat a
    /// claim.</remarks>
    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            _cover = value;
            OnChanged();
            OnChanged(nameof(ArtVisibility));
        }
    }

    /// <summary>Which catalogue this row came from, for the mark beside it. Only Discogs is named on
    /// the row. A result from the app's own catalogue has no mark, because a mark on every row would
    /// tell the user nothing.</summary>
    public Visibility DiscogsVisibility =>
        Release.Ref.Provider.Equals("discogs", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>The sleeve icon: the picture is here. It is based on the bitmap and not on the address,
    /// because they are not the same claim. An address is where a cover would be if the pressing had
    /// one, and many pressings do not.</summary>
    public Visibility ArtVisibility => Cover is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The photographs icon: how many pictures of this pressing the catalogue holds, beyond
    /// its cover. The disc art comes from these, so it is worth reading before choosing between two
    /// pressings. But a catalogue does not say which picture is the disc, so the row shows the count
    /// and no more. The count is not known until the listing arrives (see <see cref="Detail"/>). That
    /// is why the icon appears with the match, not with the row.</summary>
    public Visibility PhotosVisibility =>
        _detail is { Photos: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public string PhotosText => _detail is { Photos: > 0 } d ? d.Photos.ToString() : "";

    public string PhotosTooltip =>
        _detail is { Photos: > 0 } d ? Loc.Format("Lookup_PhotosTooltip", d.Photos) : "";

    /// <summary>How well this release's track lengths match the audio. Set once its listing
    /// arrives.</summary>
    public ReleaseMatch? Match
    {
        get => _match;
        set
        {
            _match = value;
            _matchPending = false;
            RaiseMatchChanged();
        }
    }

    /// <summary>This release is queued for scoring. The badge says "checking" instead of staying
    /// blank.</summary>
    public bool MatchPending
    {
        get => _matchPending;
        set { _matchPending = value; RaiseMatchChanged(); }
    }

    private void RaiseMatchChanged()
    {
        OnChanged(nameof(Match));
        OnChanged(nameof(MatchText));
        OnChanged(nameof(MatchBrush));
        OnChanged(nameof(MatchVisibility));
        OnChanged(nameof(Verdict));
        OnChanged(nameof(ReasonLines));
    }

    public Visibility MatchVisibility =>
        _matchPending || _match is { Basis: not MatchBasis.None } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The text of the pill. A length match shows as a percentage. The other cases say what
    /// they are: the count is right but there are no times to confirm it, or the count
    /// differs.</summary>
    public string MatchText => _match switch
    {
        null => _matchPending ? Loc.Get("Match_Checking") : "",
        { Basis: MatchBasis.Lengths } m => Loc.Format("Match_Percent", (int)Math.Round(m.Score * 100)),
        { Basis: MatchBasis.CountOnly } m => Loc.Format("Match_CountOnly", m.ExpectedCount),
        { Basis: MatchBasis.TotalOnly } m => Loc.Format("Match_CountDiffers", m.ExpectedCount, m.DetectedCount),
        _ => "",
    };

    public Brush MatchBrush => new SolidColorBrush(_match switch
    {
        { Basis: MatchBasis.Lengths, Score: >= ReleaseMatcher.GoodMatch } => Color.FromArgb(0xFF, 0x4C, 0xD9, 0x64), // lines up
        { Basis: MatchBasis.Lengths } => Color.FromArgb(0xFF, 0xFF, 0xD4, 0x66),                                     // roughly
        { Basis: MatchBasis.CountOnly } => Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF),                                   // count only
        { Basis: MatchBasis.TotalOnly } => Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF),                                   // differs
        _ => Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),                                                                 // checking
    });

    /// <summary>The one-line verdict at the top of the Match popup. It says what the number means. The
    /// reasons below it explain how the number was reached.</summary>
    public string Verdict => _match switch
    {
        null => Loc.Get("Match_Checking"),
        { Basis: MatchBasis.Lengths, Score: >= ReleaseMatcher.GoodMatch } => Loc.Get("Match_VerdictGood"),
        { Basis: MatchBasis.Lengths } => Loc.Get("Match_VerdictLoose"),
        { Basis: MatchBasis.CountOnly } => Loc.Get("Match_VerdictCountOnly"),
        { Basis: MatchBasis.TotalOnly } => Loc.Get("Match_VerdictTotalOnly"),
        _ => Loc.Get("Match_VerdictNone"),
    };

    /// <summary>The reasons shown in the popup, one line at a time. The first line is the track count.
    /// Next comes what the match basis allows: the length error and the drift, or the fact that there
    /// are no times, or that the count differs. The last line says where the listing came from.
    /// </summary>
    /// <remarks>This is not a plain IReadOnlyList. It is a list type of its own, because it is bound
    /// to an ItemsSource. Under Native AOT, an ItemsSource needs a type that CsWinRT has generated
    /// WinRT interfaces for. See <see cref="Common.TextLineList"/> (RPM007). A plain IReadOnlyList
    /// returned from the collection expression below is a type the compiler makes up, and the popup
    /// would be empty.
    /// </remarks>
    public Common.TextLineList ReasonLines
    {
        get
        {
            if (_match is not { } m)
                return [Loc.Get("Match_Checking")];

            var lines = new Common.TextLineList
            {
                m.CountMatches
                    ? Loc.Format("Match_ReasonCountMatch", m.ExpectedCount)
                    : Loc.Format("Match_ReasonCountDiff", m.ExpectedCount, m.DetectedCount),
            };

            switch (m.Basis)
            {
                case MatchBasis.Lengths:
                    if (!double.IsNaN(m.MeanErrorSeconds))
                        lines.Add(Loc.Format("Match_ReasonLengths", m.MeanErrorSeconds.ToString("0.0")));
                    lines.Add(Loc.Format("Match_ReasonDrift", DriftText(m.DriftFactor)));
                    break;
                case MatchBasis.CountOnly:
                    lines.Add(Loc.Get("Match_ReasonNoTimes"));
                    break;
                case MatchBasis.TotalOnly:
                    lines.Add(Loc.Get("Match_ReasonTotal"));
                    break;
            }

            lines.Add(Loc.Format("Match_ReasonSource", ProviderName(Release.Ref.Provider)));
            return lines;
        }
    }

    private static string DriftText(double drift) => ((drift - 1) * 100).ToString("+0.0;-0.0;0.0") + "%";

    // The name shown to the user. 'rpm' is already written the same way everywhere, so it passes
    // through unchanged. Only Discogs needs a capital letter.
    private static string ProviderName(string id) => id switch
    {
        "discogs" => "Discogs",
        _ => id,
    };

    public override string ToString() => Title;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Builds a row. The sleeve is not fetched here. The row holds its address, and the
    /// window fills in the picture when the bytes arrive. See <see cref="Cover"/>.</summary>
    public static ReleaseLookupRow From(RankedRelease ranked) => new()
    {
        Release = ranked.Release,
        CoverUrl = ranked.CoverUrl,
        Match = ranked.Match,
        Detail = ranked.Detail,
    };
}
