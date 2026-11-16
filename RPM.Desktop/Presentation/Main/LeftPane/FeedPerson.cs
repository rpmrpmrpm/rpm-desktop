using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RPM.Core.Cloud;
using Windows.Foundation;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The top of a person's page in the Feed view, as on the phones' profile page: the banner, the photo over
/// its foot (a gold ring for a supporter), the name with the supporter's heart, the badge, the size of the
/// collection, when they joined, and what they are playing. Sizes are the phone's (<c>ProfileScreen.kt</c>).
/// </summary>
internal static class FeedPerson
{
    /// <summary>The banner's height. The phone's is 190 with the status bar under it; this is what shows.</summary>
    private const double BannerTall = 150;
    private const double PhotoEdge = 120;

    /// <summary>How far the photo's centre stands above the banner's foot.</summary>
    private const double PhotoLift = 14;

    private static readonly Color Gold = Color.FromArgb(0xFF, 0xEB, 0xC6, 0x5A);

    public static FrameworkElement Top(RpmPersonProfile person)
    {
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(BannerAndPhoto(person));

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        nameRow.Children.Add(new TextBlock
        {
            Text = person.DisplayName,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = person.Supporter ? new SolidColorBrush(Gold) : FeedCards.White(0xFF),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (person.Supporter)
            nameRow.Children.Add(FeedCards.Glyph("\uF004", 16, new SolidColorBrush(Gold)));
        top.Children.Add(nameRow);

        if (ProfileBadges.NameOf(person.Badge) is { } badge)
        {
            var pill = BadgePill(badge, ProfileBadges.ColourOf(person.BadgeColour));
            pill.Margin = new Thickness(0, 6, 0, 0);
            top.Children.Add(pill);
        }

        if (Counts(person.Counts) is { } counts)
        {
            top.Children.Add(new TextBlock
            {
                Text = counts,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = FeedCards.White(0x88),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
        if (person.CreatedAt > 0)
        {
            var since = DateTimeOffset.FromUnixTimeMilliseconds(person.CreatedAt).LocalDateTime
                .ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
            top.Children.Add(new TextBlock
            {
                Text = Loc.Format("Rpm_Profile_MemberSince", since),
                FontSize = 12,
                Foreground = FeedCards.White(0x88),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        if (person.Listening is { } now)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 4),
            };
            row.Children.Add(FeedCards.Glyph("\uF04B", 11, FeedCards.White(0xFF)));
            row.Children.Add(new TextBlock
            {
                Text = Loc.Format("Rpm_Profile_NowPlaying", now.Title, now.Artist),
                FontSize = 14,
                Foreground = FeedCards.White(0xEE),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            top.Children.Add(row);
        }
        return top;
    }

    /// <summary>The banner with the photo standing over its foot. Without an uploaded banner the plate is
    /// left plain. A banner that follows what the person plays (<c>bannerSource</c> "listening") is not
    /// drawn yet, so that plate is plain too.</summary>
    private static FrameworkElement BannerAndPhoto(RpmPersonProfile person)
    {
        var block = new Grid { Height = BannerTall + PhotoEdge / 2 - PhotoLift };

        var banner = new Grid
        {
            Height = BannerTall,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(12),
            Background = FeedCards.White(0x14),
        };
        // Fetched by AvatarPicture, which sends the app key the API asks for and fills the plate from the
        // middle. A plain BitmapImage sends no key, and the API refuses it.
        var api = App.Services.GetRequiredService<RpmCommunityClient>().BaseAddress;
        if (person.HasBanner && api is not null)
        {
            banner.Children.Add(new AvatarPicture
            {
                CornerRadius = new CornerRadius(12),
                Source = new Uri(api, RpmCommunityClient.BannerPath(person.UserId, person.BannerUpdatedAt)),
            });
        }
        block.Children.Add(banner);

        var photo = new Grid
        {
            Width = PhotoEdge,
            Height = PhotoEdge,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        photo.Children.Add(FeedCards.Photo(person.UserId, person.HasAvatar, person.AvatarUpdatedAt, person.DisplayName, PhotoEdge));
        photo.Children.Add(new Ellipse
        {
            Stroke = person.Supporter ? new SolidColorBrush(Gold) : FeedCards.White(0x33),
            StrokeThickness = person.Supporter ? 3 : 2,
        });
        block.Children.Add(photo);
        return block;
    }

    /// <summary>"1,610 albums, 23,329 tracks", grouped as the reader's language groups thousands. Null
    /// when the person's app has not sent its counts.</summary>
    private static string? Counts(RpmCollectionCounts? counts)
    {
        if (counts is not { Albums: > 0 })
            return null;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var records = counts.Albums == 1 ? Loc.Get("Stats_AlbumsOne") : Loc.Format("Stats_Albums", counts.Albums.ToString("N0", culture));
        var tracks = counts.Tracks == 1 ? Loc.Get("Rpm_Profile_TracksOne") : Loc.Format("Rpm_Queue_Count", counts.Tracks.ToString("N0", culture));
        return Loc.Format("Rpm_Library_CountsJoin", records, tracks);
    }

    /// <summary>A badge under the name: its name in italics on a pill in the badge's colour, lit along a
    /// diagonal band, as the phones draw it (<c>BadgePill</c>).</summary>
    private static FrameworkElement BadgePill(string name, Color colour)
    {
        var dark = Mix(colour, Color.FromArgb(0xFF, 0, 0, 0), 0.35);
        var light = Mix(colour, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.55);
        var face = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) };
        face.GradientStops.Add(Stop(dark, 0.55, 0));
        face.GradientStops.Add(Stop(colour, 0.60, 0.30));
        face.GradientStops.Add(Stop(light, 0.70, 0.42));
        face.GradientStops.Add(Stop(colour, 0.60, 0.52));
        face.GradientStops.Add(Stop(dark, 0.55, 1));
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) };
        rim.GradientStops.Add(Stop(colour, 0.70, 0));
        rim.GradientStops.Add(Stop(light, 1, 0.45));
        rim.GradientStops.Add(Stop(colour, 0.70, 1));
        return new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = face,
            BorderBrush = rim,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 4, 14, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = name,
                FontSize = 14,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Foreground = new SolidColorBrush(Mix(colour, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.8)),
            },
        };
    }

    private static GradientStop Stop(Color colour, double alpha, double offset) =>
        new() { Color = Color.FromArgb((byte)Math.Round(alpha * 255), colour.R, colour.G, colour.B), Offset = offset };

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        0xFF,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
