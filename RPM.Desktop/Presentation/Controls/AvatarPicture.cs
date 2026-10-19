using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>A person's picture in a round frame, filled from the middle.
///
/// <para><c>Stretch="UniformToFill"</c> does not do this here. It scales the picture to cover the square,
/// which is correct, but then draws it from the top-left corner. A photograph wider than it is tall keeps
/// its left edge and loses its right side. A face in the middle of the original is pushed against the side
/// of the circle. A picture twice as wide as it is tall loses its whole right half.</para>
///
/// <para>So the position is worked out by hand. This is the usual fix for this part of Uno. The art
/// display's <c>Stand</c> uses the same method. The picture gets an exact size and is placed from the
/// top-left. The overhang is split evenly between the two edges, and the frame clips what falls outside.
/// This still works after a resize or a late download, because both simply ask for the placement
/// again.</para>
///
/// <para><see cref="Source"/> is typed loosely on purpose. Every call site holds a <see cref="Uri"/>, the
/// address a picture is fetched from. An <c>Image</c> accepts a <see cref="Uri"/> only because XAML
/// converts it on the way in. Accepting the same kinds of value as an <c>Image</c> lets this control
/// replace one without changing the bindings.</para>
/// </summary>
public sealed partial class AvatarPicture : BorderBase
{
    private readonly Image _picture = new()
    {
        Stretch = Stretch.Fill,   // The size below is exact and in proportion, so Fill distorts nothing.
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private Windows.Foundation.Size _box;

    public AvatarPicture()
    {
        Child = _picture;

        // Use e.NewSize, not ActualWidth. Inside this event ActualWidth still holds the old size, so
        // placing against it puts every picture in the wrong place.
        SizeChanged += (_, e) =>
        {
            _box = e.NewSize;
            Place();
        };

        // The picture's size is not known until it is decoded. For a downloaded picture that happens some
        // time after it is set. Placing only when the source is set would centre every picture against a
        // size of zero.
        _picture.ImageOpened += (_, _) => Place();
    }

    /// <summary>The picture. It can be an <see cref="ImageSource"/>, a <see cref="Uri"/>, or an address
    /// string. These are the same values an <c>Image</c>'s own Source accepts.</summary>
    public object? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(object), typeof(AvatarPicture),
        new PropertyMetadata(null, (d, e) => ((AvatarPicture)d).OnSourceChanged(e.NewValue)));

    private void OnSourceChanged(object? value)
    {
        var mine = ++_asked;

        switch (value)
        {
            // A BitmapImage that will fetch its own address. This frame cannot measure that input. Under
            // Uno, PixelWidth is filled in only when a bitmap is set from a stream. So this case would
            // reach Place with no size and use the top-left cover. Instead it takes the same path as a
            // plain Uri: the bytes are downloaded and decoded here. The case is checked rather than
            // blocked, because Source is as loose as an Image's own Source.
            case BitmapImage { PixelWidth: 0, UriSource: { } address } when Fetchable(address):
                Fetch(address, mine);
                break;

            case ImageSource source:
                Draw(source);
                break;

            // A picture the framework opens itself, such as ms-appx or ms-resource. There is nothing to
            // download, and no size can be read from it. It is set directly, and Place uses the fallback
            // cover below.
            case Uri uri when !Fetchable(uri):
                Draw(new BitmapImage(uri));
                break;

            case Uri uri:
                Fetch(uri, mine);
                break;

            case string text when Uri.TryCreate(text, UriKind.Absolute, out var parsed):
                OnSourceChanged(parsed);
                break;

            default:
                Draw(null);
                break;
        }
    }

    /// <summary>The number of the latest source set. A frame can be given a second picture while the first
    /// is still downloading, for example when a list row is recycled. The first picture must not appear
    /// afterwards. The download is the only step here that can finish late, so it checks this number
    /// first.</summary>
    private int _asked;

    /// <summary>Whether the bytes behind an address can be downloaded here: from the network or from a
    /// local file. Any other address is opened by the framework.</summary>
    internal static bool Fetchable(Uri uri) =>
        uri.IsAbsoluteUri
        && (uri.IsFile || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The picture is downloaded as bytes and decoded here. This is the only way this frame learns
    /// the picture's size.
    ///
    /// <para><c>new BitmapImage(uri)</c> downloads its own address but never sets
    /// <see cref="BitmapSource.PixelWidth"/>. Under Uno that property is set only when a bitmap is loaded
    /// from a stream. So a picture given as a URL reached <see cref="Place"/> with no size. It then used
    /// the fallback cover and was drawn from the top-left corner, which is the problem this control exists
    /// to fix.</para>
    ///
    /// <para>The picture is cleared while the new bytes download. The frame is reused for different people,
    /// and showing the last person's face is worse than showing an empty tile.</para>
    /// </summary>
    private async void Fetch(Uri uri, int asked)
    {
        var address = uri.AbsoluteUri;

        if (Kept.TryGetValue(address, out var held))
        {
            Draw(held);
            return;
        }

        Draw(null);

        var answer = await RemoteImage.LoadAsync(address);

        // A newer picture was set while this download was running. Ignore this result.
        if (asked != _asked)
            return;

        // Only a loaded picture is kept. A failed download draws the empty tile and is not kept, so the
        // next display downloads it again.
        var loaded = answer.Match(picture => picture, _ => null);
        if (loaded is not null)
            Keep(address, loaded);

        Draw(loaded);
    }

    private void Draw(ImageSource? source)
    {
        _picture.Source = source;
        Place();
    }

    /// <summary>Pictures already decoded. A flyout rebuilds its rows each time it opens. Without this
    /// cache, each open would download every picture again.</summary>
    private static readonly Dictionary<string, BitmapImage> Kept =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The most pictures kept. There is one for each person the user shares with, plus the user.
    /// When the limit is reached the whole cache is cleared, rather than removing old entries one at a
    /// time. The set is small either way.</summary>
    private const int FacesKept = 16;

    private static void Keep(string address, BitmapImage picture)
    {
        if (Kept.Count >= FacesKept)
            Kept.Clear();

        Kept[address] = picture;
    }

    /// <summary>Covers the frame from the middle. The picture is scaled so that it leaves no gap, placed
    /// from the top-left, and the overhang is removed equally from both edges.
    ///
    /// <para>Until the picture has a measured size there is nothing to centre, so the framework's own cover
    /// is used. Place is called again as soon as the decode reports a size.</para>
    /// </summary>
    private void Place()
    {
        if (_box.Width <= 0 || _box.Height <= 0)
            return;

        if (_picture.Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } bitmap)
        {
            _picture.Stretch = Stretch.UniformToFill;
            _picture.Width = double.NaN;
            _picture.Height = double.NaN;
            _picture.Margin = new Thickness(0);
            return;
        }

        var drawn = Cover(bitmap.PixelWidth, bitmap.PixelHeight, _box.Width, _box.Height);

        _picture.Stretch = Stretch.Fill;
        _picture.Width = drawn.Width;
        _picture.Height = drawn.Height;
        _picture.Margin = new Thickness(drawn.X, drawn.Y, 0, 0);
    }

    /// <summary>Where a picture is drawn to cover a box from the middle. It is scaled by whichever edge is
    /// shorter than the box. Then it is offset by half of the part that does not fit, so the overhang is
    /// removed from both sides equally. Both offsets are zero or negative. That makes them a margin for a
    /// picture hung from the top-left.
    /// </summary>
    internal static (double X, double Y, double Width, double Height) Cover(
        double pictureWidth, double pictureHeight, double boxWidth, double boxHeight)
    {
        var scale = Math.Max(boxWidth / pictureWidth, boxHeight / pictureHeight);
        var width = pictureWidth * scale;
        var height = pictureHeight * scale;

        return (-(width - boxWidth) / 2, -(height - boxHeight) / 2, width, height);
    }
}
