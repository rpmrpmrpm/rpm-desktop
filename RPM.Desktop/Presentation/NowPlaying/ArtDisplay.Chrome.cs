using System.Diagnostics;
using RPM.Core.Art;
using RPM.Core.Library;
using RPM.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;


namespace RPM.Desktop.Presentation.NowPlaying;

/// <summary>
/// The stage surface that the controller drives (<see cref="IArtStage"/>). It holds the words over the
/// picture, the loading state, the carousel, and the now-playing chrome. The chrome is also published for
/// other surfaces that show the same track.
/// </summary>
public sealed partial class ArtDisplay
{
    // The stage surface (IArtStage): text, chrome, loading and carousel

    /// <summary>
    /// The track's title, artist and album, printed bottom-left over the scrim.
    /// </summary>
    /// <param name="work">The composition this track is part of, and <paramref name="movement"/> is which
    /// movement of it. When both are given, the header prints them on two lines: the work first, then the
    /// movement that is playing. "Sonata In C Major, KV. 330" and "Allegro Moderato" are two separate
    /// facts, so one line would read as one long name. A track that is part of no work, or a whole work in
    /// one track, has nothing for the second line, so the header shows only the title.</param>
    public void SetText(string? title, string? artist, string? album,
                        string? work = null, string? movement = null)
    {
        bool split = !string.IsNullOrWhiteSpace(work) && !string.IsNullOrWhiteSpace(movement);

        // The guest is named on the line under this one, so the title does not need its own copy of the
        // credit. The title is the largest text and has the least room. Only what is drawn loses the
        // credit. TrackTitle is the song as it was tagged, and the surfaces that read it from here (the
        // full-screen player, the visualizer's foot) print it in full. The title is masked on the way to
        // the pixels, the same as the lyric sheet. See TitleCensor. What is kept in TrackTitle and the rest
        // is the song as it was tagged. Surfaces that read it mask it themselves, and a lookup made with a
        // starred name would find nothing.
        TitleText.Text = TitleCensor.Shown(TitleAsides.WithoutCredits(split ? work : title));
        MovementText.Text = split ? TitleCensor.Shown(movement!) : "";
        MovementText.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        ArtistText.Text = artist ?? "";
        AlbumText.Text = TitleCensor.Shown(album) ?? "";

        TrackTitle = title;
        TrackArtist = artist;
        TrackAlbum = album;

        // The same words for the parked state. It is the same banner in its small form, not a second
        // surface.
        ParkedTitle.Text = TitleCensor.Shown(TitleAsides.WithoutCredits(title));
        ParkedArtist.Text = artist ?? "";
        ParkedAlbum.Text = TitleCensor.Shown(album) ?? "";
        ParkedArtist.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;
        ParkedAlbum.Visibility = string.IsNullOrWhiteSpace(album) ? Visibility.Collapsed : Visibility.Visible;
        SyncParked();

        // The record on the deck is this ALBUM's pressing. It is set here, not from the cover, because this
        // is called once per track with the album's name, while ShowCover can run twice for one track
        // (first the local art, then the answer that there is none).
        //
        // Only when there IS a track. Stopping calls this with everything null. A null album is a key like
        // any other (the untagged pressing), so the record would change colour on the way out and then turn
        // to the idle pressing just before it leaves. A record coming off the deck keeps the colour it was
        // playing in. The title shows whether there is a track. The album can be blank on a track with no
        // tags, and that track still gets the untagged pressing. The pressing waits for the cover, because
        // the colour comes from the sleeve and nothing about the sleeve is known yet. Only the album to
        // wait for is recorded here. ShowCover presses the record, both for a cover and for no cover.
        if (!string.IsNullOrWhiteSpace(title))
            _pressingAlbum = (album ?? "").Trim();

        ChromeChanged?.Invoke();
    }

    // The chrome, republished
    //
    // The header is where a track's identity is resolved (library row or tags) and where its cover is
    // decoded, once. A second surface that wants the same chrome (the full-screen visualizer shows it along
    // its foot) reads these properties instead of reading tags and decoding art again. Then the two
    // surfaces cannot drift apart, and the work is done only once.

    /// <summary>The title, artist and album of the displayed track, as last given to <see
    /// cref="SetText"/>.</summary>
    public string? TrackTitle { get; private set; }
    public string? TrackArtist { get; private set; }
    public string? TrackAlbum { get; private set; }

    /// <summary>The decoded cover on the display, or null when the track has none. It is cut to the largest
    /// square that any surface draws. A surface that draws the cover small should use
    /// <see cref="TrackCoverSquare"/> instead.</summary>
    public BitmapImage? TrackCover { get; private set; }

    /// <summary>The same cover, at the size that small squares draw it. See
    /// <see cref="LoadedCover.Square"/>. It is null when that decode failed. A small square should then use
    /// <see cref="TrackCover"/>.</summary>
    public BitmapImage? TrackCoverSquare { get; private set; }

    /// <summary>
    /// What that cover stands ON, where it is a mark with nothing behind it, such as a station's logo. Null
    /// for an ordinary sleeve, which is its own background.
    ///
    /// <para>Republished with the cover, like the rest of this group. A second surface that shows the same
    /// record gets the same answer. Otherwise a station with a made background in one place and a bare logo
    /// on black in another would look like two different pictures of one station.</para>
    /// </summary>
    public BitmapImage? TrackGround { get; private set; }

    /// <summary>The cover's own pixel size, for surfaces that draw a mark at its own size instead of
    /// filling a square with it. Zero when it is not known. See ArtDisplay.Cover's Frame.</summary>
    public int TrackCoverWidth { get; private set; }

    public int TrackCoverHeight { get; private set; }

    /// <summary>
    /// Whether that cover fills its square or is fitted whole inside it. See <see
    /// cref="LoadedCover.Fills"/>. The answer is worked out once here, not guessed from the ground.
    ///
    /// <para>A ground means "fitted whole", but the reverse is not always true. A station's wide logo is
    /// its own background and is still fitted, because filling it would crop away the station's name. A
    /// surface that tested the ground alone would crop the logos that should be fitted whole.</para>
    /// </summary>
    public bool TrackCoverFills { get; private set; } = true;

    /// <summary>The album's photographed disc art, when the folder has one. It is the same decoded,
    /// downscaled bitmap that the header's record shows. It also says whether the picture is the whole
    /// record or only its label. Most albums have no disc art. For those, a second surface draws the record
    /// the same way this one does.</summary>
    public ScannedDisc? TrackDisc { get; private set; }

    /// <summary>The colour this sleeve's record is pressed in, or null for black vinyl. It is published so
    /// that a second record (Pegasus shows one full screen) uses the same pressing, and does not judge the
    /// same cover again. See <see cref="VinylPressing"/>.</summary>
    public Windows.UI.Color? TrackPressing { get; private set; }

    /// <summary>The cover's dominant tone, its vivid accent, and the next colour that is different from
    /// those two. All three are read in the same decode pass. Each is null when the sleeve does not have
    /// one. Anything drawn beside the cover takes its palette from these. See
    /// <see cref="VinylPressing.PaletteFromArt"/>.</summary>
    public Windows.UI.Color? TrackTint { get; private set; }
    public Windows.UI.Color? TrackGlow { get; private set; }
    public Windows.UI.Color? TrackShade { get; private set; }

    /// <summary>Raised whenever any of the above changes.</summary>
    public event Action? ChromeChanged;

    /// <summary>Switches the display from the idle chrome to the track chrome. The record and the waves are
    /// not changed. Safe to call when already on the track chrome.</summary>
    public void EnterTrack()
    {
        if (!_idle)
            return;

        // A park that the listener asked for is a standing instruction. The player cannot overrule it. This
        // is called each time the art comes back, which is every new track and every new album. If the
        // drawer were opened here, a park the listener chose would last only until the next song. So it
        // stays shut until the chevron is clicked. See HoldParked.
        if (_heldParked)
            return;

        _idle = false;
        RefreshNav();
        SyncParked();   // The full chrome returns, so the parked lines step aside.
        AnimateRpmOpacity(RpmPlayOpacity);   // Playing: the mark dims to the muted tone.
        _rpmZoomWait?.Stop();                    // Cancel a park zoom that is still waiting for the drawer.
        AnimateRpmScale(1, RecordFlyDuration);   // Shrink back from any parked zoom.
        _deck?.Cue();                  // A record is wanted. It drops once it has a face to land in.
        IdleChanged?.Invoke(false);    // The shell opens the art drawer and pins the grids.

        Animate(IdleChrome, 0);
        AnimateContent(1);
    }

    /// <summary>Back to the empty display: nothing playing, nothing selected. The chrome returns to the "No
    /// track" placeholder; the manager clears the art around this call.</summary>
    public void EnterIdle()
    {
        if (_idle)
            return;

        _idle = true;
        RefreshNav();
        SyncParked();   // Tucked away with something playing: the parked lines take over from the chrome.

        Animate(IdleChrome, 1);
        // Clear the album art first, then park. Otherwise the cover would move up over the rpm mark as the
        // band slides, and its slow 2 s crossfade would stay there long after the drawer has gone. Hide it
        // quickly. Once it is off, lift the disc, brighten the mark and slide the band up.
        AnimateContent(0, ArtHideDuration, onCompleted: () =>
        {
            if (!_idle)
                return;   // A track came back during the hide. Do not park on top of it.
            AnimateRpmOpacity(RpmParkBaseOpacity);   // Parked: the mark brightens to its parked strength.
            _deck?.Park();                 // The disc lifts off to the top right and fades.
            IdleChanged?.Invoke(true);     // The shell parks the art drawer and pins the grids.
            // Then, once the drawer has finished sliding up, the mark zooms to its parked size. This runs
            // only while still idle, so a quick unpark during the slide cancels the zoom.
            _rpmZoomWait?.Stop();
            _rpmZoomWait = new DispatcherTimer { Interval = WashDuration };
            _rpmZoomWait.Tick += (_, _) =>
            {
                _rpmZoomWait?.Stop();
                if (_idle)
                {
                    AnimateRpmScale(RpmParkedScale, RpmZoomDuration);
                    SyncTagline();   // The tagline appears with the grown mark, not before it.
                }
            };
            _rpmZoomWait.Start();
        });
    }

    private DispatcherTimer? _rpmZoomWait;

    /// <summary>How fast the tagline appears and disappears. It is quick both ways. It appears with the
    /// park zoom, and on the way out it must be gone before the drawer slides a picture into its
    /// place.</summary>
    private static readonly TimeSpan TaglineFadeDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>The tagline beside the parked mark. It shows only while the drawer is parked with nothing
    /// playing. When parked with a track, the parked lines use this row. When open, the band is the
    /// artwork.
    ///
    /// <para>The tagline shows only once the mark has reached its parked size. On a park, the zoom waits
    /// until the drawer has finished sliding (see <see cref="EnterIdle"/>). A tagline shown earlier would
    /// float in mid-air, to the right of a mark that is still at its plain size. This method is called
    /// again from the zoom tick. If playback stops while already parked, there is no slide to wait for, and
    /// the same test lets the tagline show at once.</para></summary>
    private void SyncTagline()
    {
        var show = _idle && string.IsNullOrWhiteSpace(TrackTitle);
        // Use the size that was commanded, not the transform's current value. The zoom tick calls this as
        // soon as it starts the grow. In Uno a dependent animation leaves ScaleX at its old value until the
        // animation finishes. Reading the transform would show 1 on every park, so the tagline would never
        // appear. See _rpmScaleTarget.
        if (show && _rpmScaleTarget <= 1)
            return;   // The mark has not been told to grow yet. The zoom tick calls back here.

        if (show && TaglineTail.Text.Length == 0 && TaglineHead.Text.Length == 0)
        {
            // A leading "re-" is drawn in a dimmer tone. It is the prefix of the word, with the hyphen.
            // This is applied only when the translation starts with it. The hyphen is an English style.
            // Other languages spell the prefix into the word, or say it in another way. They get the line
            // in one tone, not a few characters picked out at random.
            var line = RPM.Desktop.Strings.Loc.Get("Art_ParkedTagline");
            var split = line.StartsWith("re-", StringComparison.Ordinal) ? 3
                      : line.StartsWith("re", StringComparison.Ordinal) ? 2
                      : 0;
            TaglineHead.Text = line[..split];
            TaglineTail.Text = line[split..];
            PlaceTagline();   // In case the first show comes before the first layout pass.
        }

        Animate(ParkedTagline, show ? 1 : 0, duration: TaglineFadeDuration);
    }

    /// <summary>Zooms the RenderTransform of the rpm mark on both axes together, about its centre. The grow
    /// overshoots a little, which gives the park a small bounce. The shrink back to plain size just eases
    /// out.</summary>
    private void AnimateRpmScale(double to, TimeSpan duration)
    {
        if (_rpmScale is null)
            return;
        _rpmScaleTarget = to;
        var storyboard = new Storyboard();
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(duration),
                EnableDependentAnimation = true,
                EasingFunction = to > 1
                    ? new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
                    : new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(anim, _rpmScale);
            Storyboard.SetTargetProperty(anim, prop);
            storyboard.Children.Add(anim);
        }
        Services.Shell.Storyboards.Play(storyboard);
        // The parked grow also raises the mark to full opacity in its last step. This goes through the
        // tracked opacity fader, not this storyboard. The fader cancels the slow park-base brighten. If it
        // did not, that 2 s fade would finish late and pull the opacity back down.
        if (to > 1)
            AnimateRpmOpacity(1.0, duration);
    }

    // The RpmMark opacity uses its own tracked storyboard. Before each new fade, the old storyboard is
    // stopped. It is stopped only after the new one is running, so the opacity does not flicker back.
    // Without this, the slow park-base brighten (2 s) would snap back to its base value when it ends, on
    // top of the zoom's lift to full opacity.
    private Storyboard? _rpmOpSb;
    // The last opacity the mark was told to reach. Its base Opacity is 1.0. Uno reverts to that base when
    // Stop() is called, and it reports the base (not the live value) when Opacity is read during an
    // animation. So a fade with no From, or one that reads the live value, starts the park brighten at 1.0
    // and eases down. That gives a brief full-white flash before the grow. Storing the last target gives a
    // reliable From. The previous fade is always finished by the next park or play, so the target equals
    // the live value at that point. The only mid-flight change is the zoom's lift to 1.0, which only makes
    // the mark brighter.
    private double _rpmOpTarget = RpmPlayOpacity;
    private void AnimateRpmOpacity(double to, TimeSpan? duration = null)
    {
        var old = _rpmOpSb;
        var from = _rpmOpTarget;
        _rpmOpTarget = to;
        var fade = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration ?? FadeDuration),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(fade, RpmMark);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        _rpmOpSb = sb;
        sb.Begin();
        old?.Stop();
    }

    /// <summary>Whether a backdrop fetch is in progress. Nothing is drawn for it. See the navigation note
    /// in the XAML. The header menu's flip and refresh items are greyed out while it is on, and that is the
    /// only place this state is shown.</summary>
    private bool _loading;

    public void SetLoading(bool on)
    {
        _loading = on;
        RefreshNav();
    }

    private int _carouselIndex, _carouselCount;

    /// <summary>The number of backdrops the artist has, and which one is shown. The header menu's count
    /// line reads, for example, "Picture 3 of 8". The flip items are enabled only when there is more than
    /// one picture.</summary>
    public void SetCarousel(int index, int count)
    {
        _carouselIndex = index;
        _carouselCount = count;
        RefreshNav();
    }

    private void RefreshNav()
    {
        var many = _carouselCount > 1;

        // The disabled top line of the menu names the picture that is shown, out of the total. It is shown
        // only when the artist has more than one backdrop.
        CountItem.Text = many
            ? RPM.Desktop.Strings.Loc.Format("Art_CtxPictureCount", _carouselIndex + 1, _carouselCount)
            : "";
        CountItem.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        CountSep.Visibility = many ? Visibility.Visible : Visibility.Collapsed;

        // Flip is enabled only with more than one picture. Refresh is offered for any track that is
        // playing, even one with no backdrops yet, because its purpose is to look for some. Both are
        // disabled while a load is in progress, including a load they started.
        PrevItem.IsEnabled = many && !_loading;
        NextItem.IsEnabled = many && !_loading;
        RefreshItem.IsEnabled = !_loading && ArtistPhotoShown;

        // Choosing a picture needs only an artist to file it under. It is deliberately NOT disabled when
        // there is no artwork yet. A header with no photograph from the sources is where a user most wants
        // to add their own, so a disabled item would fail exactly when it is needed. It is also off where
        // the photograph is switched off in Settings. Every item in this menu is about a picture that the
        // header is set not to show, so offering to find one would ask the app to do work it has been told
        // not to do.
        ChooseItem.IsEnabled = !string.IsNullOrWhiteSpace(TrackArtist) && !_loading && ArtistPhotoShown;

        // Placing a picture needs a photograph to place. The controller owns the carousel, so it knows
        // whether one exists, and it is asked rather than guessed. Removing a picture asks the same
        // question (is a photograph shown, filed under an artist?), so the answer is read once for both.
        var placeable = !_loading && Controller?.FramingSubject() is not null;
        PositionItem.IsEnabled = placeable;
        RemoveItem.IsEnabled = placeable;
    }

    // Placing the photograph by hand

    /// <summary>
    /// Opens the placement window for the photograph in the header, and passes the result to the
    /// controller. The window belongs to the view. What the answer means (bake it, remember it for this
    /// artist, or forget the one that was there) is decided by the manager, as it is for the file picker.
    /// </summary>
    private void Position_Click(object sender, RoutedEventArgs e) => _ = PlaceArtworkAsync();

    /// <inheritdoc cref="IArtStage.OfferPlacement"/>
    public void OfferPlacement() => _ = PlaceArtworkAsync();

    /// <summary>True while the placement window is open, so that a second one cannot open on top of it. The
    /// menu item and the invitation after choosing a picture can both start within a moment of each
    /// other.</summary>
    private bool _placing;

    private async Task PlaceArtworkAsync()
    {
        if (_placing || Controller is not { } controller || XamlRoot is null)
            return;

        if (controller.FramingSubject() is not { } subject)
            return;   // There is nothing on the header to place.

        _placing = true;
        try
        {
            var placement = await Dialogs.PositionArtworkDialog.ShowAsync(
                XamlRoot, subject, Shape, AutomaticFraming(subject.Url));

            // Cancelled: the header keeps its current picture. Nothing is applied while the window is open,
            // so there is nothing to undo.
            if (placement is not { } chosen)
                return;

            controller.Frame(subject, chosen.Framing);
        }
        catch (Exception ex)
        {
            // A window that will not open does not stop playback. Without this log the menu item would do
            // nothing, and that would look the same as a window that opened and was cancelled. The reason
            // is written to the scope ring. Read it with `rpmscope log`.
            Note("the placement window would not open", ex);
        }
        finally
        {
            _placing = false;
        }
    }

    /// <summary>Writes one failure to the scope ring under "window". Without this, a caught exception is
    /// silent, and a broken menu item looks like one that does nothing.</summary>
    private static void Note(string what, Exception ex)
    {
        try
        {
            RPM.Core.Diagnostics.Said.Line("window", $"art menu: {what}: {ex.GetType().Name}: {ex.Message}");
        }
        catch { }
    }

    // The flip and refresh menu items, and the recrop timer, only pass input on. They forward to the
    // manager, which is the one path that changes the art.
    private void Prev_Click(object sender, RoutedEventArgs e) => Controller?.Step(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Controller?.Step(+1);
    private void Refresh_Click(object sender, RoutedEventArgs e) => Controller?.Refresh();
    private void Remove_Click(object sender, RoutedEventArgs e) => Controller?.RemoveImage();

    /// <summary>
    /// Asks the user for a picture file on this computer, and passes its path to the manager. The picker is
    /// the only part that belongs to the view. What the choice means is decided in one place, as it is for
    /// the flip and the refresh above.
    /// </summary>
    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (Controller is not { } controller)
            return;

        try
        {
            var picker = Services.Shell.Pickers.OpenFile(
                Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
                Services.Art.OwnArt.Extensions);

            if (await picker.PickSingleFileAsync() is { } file)
                controller.UseImage(file.Path);
        }
        catch (Exception ex)
        {
            // The picker needs a window on the WinUI head. Without one it throws COMException 0x80070578.
            // This catch logs the failure, so that it does not look like a click that does nothing.
            Note("choosing a picture failed", ex);
        }
    }

    /// <summary>Opens the header's flip and refresh menu at the given point. The art sits BEHIND the
    /// shell's transparent content grid, so its own Root never gets the right-click. The shell catches the
    /// gesture over the header band and passes it here. Does nothing when idle, because there is nothing to
    /// flip or refresh.</summary>
    public void ShowHeaderMenu(FrameworkElement placementTarget, Windows.Foundation.Point position)
    {
        if (_idle)
            return;
        RefreshNav();   // Update the item state (count line, enabled flips) for the current carousel.
        ArtMenu.ShowAt(placementTarget, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = position });
    }
}
