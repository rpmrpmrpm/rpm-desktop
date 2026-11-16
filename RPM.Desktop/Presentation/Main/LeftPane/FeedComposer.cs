using Microsoft.UI.Xaml.Controls;
using RPM.Core.Cloud;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The box a post or a reply is written in, as on the phones' profile page: the text, the Post key at its
/// right, and for a post the "Add the song I'm playing" switch while something plays. Post is pressable
/// while there is text and nothing is being sent. A failed send keeps the text and says so under the box.
/// </summary>
internal sealed partial class FeedComposer : UserControl
{
    private readonly TextBox _text;
    private readonly Button _send;
    private readonly ToggleSwitch? _withSong;
    private readonly TextBlock? _songLine;
    private readonly FrameworkElement? _songRow;
    private readonly TextBlock _trouble;
    private readonly Func<string, RpmSong?, Task<bool>> _post;
    private bool _sending;

    /// <param name="prompt">The placeholder text.</param>
    /// <param name="post">Sends the text, with the song when the switch is on. Answers whether it was
    /// sent; the box is emptied only then.</param>
    /// <param name="offerSong">Show the switch that adds the song playing (posts only).</param>
    public FeedComposer(string prompt, Func<string, RpmSong?, Task<bool>> post, bool offerSong)
    {
        _post = post;
        var frame = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = FeedCards.White(0x14),
            BorderBrush = FeedCards.White(0x22),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 8, 8, 8),
        };

        var stack = new StackPanel { Spacing = 8 };
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _text = new TextBox
        {
            PlaceholderText = prompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = RpmCommunityClient.LongestPost,
            MaxHeight = 140,
            IsSpellCheckEnabled = true,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _text.TextChanged += (_, _) => Sync();
        row.Children.Add(_text);

        _send = new RpmPrimaryButton
        {
            Content = Loc.Get("Rpm_Profile_Post"),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsEnabled = false,
        };
        _send.Click += async (_, _) => await SendAsync();
        Grid.SetColumn(_send, 1);
        row.Children.Add(_send);
        stack.Children.Add(row);

        if (offerSong)
        {
            var songRow = new Grid { ColumnSpacing = 10 };
            songRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            songRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = Loc.Get("Rpm_Profile_WithSong"), FontSize = 12, Foreground = FeedCards.White(0xB3) });
            _songLine = new TextBlock { FontSize = 11, Foreground = FeedCards.White(0x77), TextTrimming = TextTrimming.CharacterEllipsis };
            words.Children.Add(_songLine);
            songRow.Children.Add(words);
            _withSong = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_withSong, Loc.Get("Rpm_Profile_WithSong"));
            Grid.SetColumn(_withSong, 1);
            songRow.Children.Add(_withSong);
            _songRow = songRow;
            stack.Children.Add(songRow);
            // What plays changes while the page is open, so the line is read again on focus.
            _text.GotFocus += (_, _) => ShowSong();
            ShowSong();
        }

        _trouble = new TextBlock
        {
            FontSize = 12,
            Foreground = FeedCards.White(0xAA),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        stack.Children.Add(_trouble);
        frame.Child = stack;
        Content = frame;
    }

    /// <summary>Puts the caret in the box.</summary>
    public void FocusText() => _text.Focus(FocusState.Programmatic);

    private void ShowSong()
    {
        if (_songRow is null || _songLine is null)
            return;
        var song = FeedPlaying.Now();
        _songRow.Visibility = song is null ? Visibility.Collapsed : Visibility.Visible;
        _songLine.Text = song is null ? "" : FeedPlaying.Line(song);
    }

    private void Sync() => _send.IsEnabled = !_sending && !string.IsNullOrWhiteSpace(_text.Text);

    private async Task SendAsync()
    {
        var body = _text.Text.Trim();
        if (_sending || body.Length == 0)
            return;
        _sending = true;
        Sync();
        _trouble.Visibility = Visibility.Collapsed;
        var song = _withSong?.IsOn == true ? FeedPlaying.Now() : null;
        var sent = await _post(body, song);
        _sending = false;
        if (sent)
        {
            _text.Text = "";
            if (_withSong is not null)
                _withSong.IsOn = false;
        }
        else
        {
            _trouble.Text = Loc.Get("Rpm_Profile_SendFailed");
            _trouble.Visibility = Visibility.Visible;
        }
        Sync();
    }
}

/// <summary>The song playing on this computer, for a post and for "Add the song I'm playing". A radio
/// station is not a song, so it gives none.</summary>
internal static class FeedPlaying
{
    public static RpmSong? Now()
    {
        if (App.Services.GetRequiredService<RadioService>().Current is not null)
            return null;
        var playback = App.Services.GetRequiredService<PlaybackService>();
        if (playback.NowPlaying is { Title: { Length: > 0 } announced } now)
            return new RpmSong(now.Artist ?? "", announced, now.Album ?? "");
        if (playback.PlayingPath is not { Length: > 0 } path)
            return null;
        try
        {
            return App.Services.GetRequiredService<LibraryService>().TrackAt(path) is { Title: { Length: > 0 } title } track
                ? new RpmSong(track.Artist ?? track.AlbumArtist ?? "", title, track.Album ?? "")
                : null;
        }
        catch (InvalidOperationException)
        {
            // The library is not open.
            return null;
        }
    }

    /// <summary>"Title by Artist", as the phones write a song.</summary>
    public static string Line(RpmSong song) =>
        string.IsNullOrEmpty(song.Artist) ? song.Title : Loc.Format("Rpm_Profile_NowPlaying", song.Title, song.Artist);
}
