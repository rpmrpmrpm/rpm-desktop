using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Radio;
using RPM.Core.Threading;
using RPM.Desktop.Presentation.Main.Home;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Services.Audio;
using RPM.Desktop.Services.Catalog;
using Loc = RPM.Desktop.Strings.Loc;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Radio in Pegasus: the collection's third screen, opened by the side menu's Internet Radio. It has a
/// banner of stations at the top, a row headed "Essential stations in &lt;country&gt;" under it, and a
/// row of favourite stations under that.
///
/// <list type="bullet">
/// <item><b>The banner</b> holds the stations listened to most, then the favourites, then the essential
/// stations, seven at most. It is drawn the way home's radio card is (see RadioBannerHost) and stepped the
/// way home's banner is: the card in front is the remote's one stop over it, Left and Right step it, and
/// OK tunes it.</item>
/// <item><b>Essential stations</b> are the directory's most played stations in the listener's country (the
/// system's region), or everywhere where that is not known or lists nothing.</item>
/// <item><b>Favourite stations</b> are the ones saved with the heart, in the tuner or in the main window's
/// radio pane.</item>
/// </list>
///
/// <para>Left held for two seconds opens the side menu, as on home; Right held for two seconds opens
/// radio's wheel (see PegasusChrome.RadioWheel), which leads to the tuner and the search. Back goes to
/// home. The mini player is over it and goes and comes back by home's rules (see
/// PegasusChrome.PlayerAway).</para>
///
/// <para><b>Built in code at the collection's scale</b>, like the side menu and the record page. What is on
/// it is read once a visit and again only when the directory's answer is old (<see cref="RadioGoodForMs"/>)
/// or the favourites change. Everything learnt about a station afterwards (its mark, its colours, what
/// it is and what it is playing) is written to its <see cref="StationFace"/> and read by whatever is
/// drawing it. Several readings can be in flight at once. The newest one wins: an older reading that
/// lands late drops what it found, and a face is written only while its host is still the one it was
/// made for.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How long the directory's answer stands before a visit asks again: the radio pane's own
    /// figure (RadioModel.ResultsGoodFor), for the same reason.</summary>
    private const long RadioGoodForMs = 30 * 60 * 1000;

    /// <summary>The most stations on the banner, and how many of them may be the ones heard most.</summary>
    private const int RadioBannerMost = 7;
    private const int RadioBannerHeard = 5;

    /// <summary>How many essential stations stand in their row.</summary>
    private const int RadioEssentialCount = 12;

    /// <summary>How long one step of the banner takes.</summary>
    private const int RadioBannerSlideMs = 380;

    /// <summary>The five banner hosts, at offsets -2 to 2 from the card in front. Index 2 is in
    /// front.</summary>
    private readonly RadioBannerHost[] _bannerHosts = new RadioBannerHost[5];

    /// <summary>The remote's one stop over the card in front: OK tunes it, Left and Right step the banner.
    /// It takes no pointer input; a press on a card is its own (see BannerHost_Tapped).</summary>
    private RemoteLine? _radioBannerKey;

    /// <summary>The stations on the banner, in order, and which of them is in front.</summary>
    private readonly List<StationFace> _radioBanner = [];
    private int _radioFront;

    /// <summary>The cards in the two rows, held so a resize can size them and a rebuild can let go of
    /// them.</summary>
    private readonly List<RadioRowCard> _radioEssentialCards = [];
    private readonly List<RadioRowCard> _radioFavoriteCards = [];

    /// <summary>Every face on the screen, by station, so a station on the banner and in a row is one face
    /// and asks the network once.</summary>
    private readonly Dictionary<string, StationFace> _radioFaces = new(StringComparer.Ordinal);

    /// <summary>The reading that filled the screen. A reading that comes back to find a newer one started
    /// drops what it found.</summary>
    private int _radioRead;

    /// <summary>When the directory last answered, on the monotonic clock. Zero until it has, and again
    /// after it failed, so the next visit asks again.</summary>
    private long _radioAnsweredAt;

    /// <summary>Whether a reading has landed at all, answered or not. What the view's opening cover waits
    /// on when it opens onto radio (see CollectionIsReady).</summary>
    private bool RadioIsReady { get; set; }

    /// <summary>The errands drawing the faces of the screen as it was last built. Cancelled when it is
    /// built again.</summary>
    private CancellationTokenSource? _radioFacesRun;

    /// <summary>Where a step of the banner has got to: the hosts stand <see cref="RadioBannerShift"/> card
    /// widths from their places, easing to nought over <see cref="RadioBannerSlideMs"/>.</summary>
    private double _bannerFrom;
    private double _bannerT = 1;
    private double RadioBannerShift => _bannerFrom * (1 - PaneGlide.EaseAt(_bannerT));

    /// <summary>The banner's geometry, written in SizeRadio.</summary>
    private double _bannerWidth, _bannerHeight, _bannerPitch, _bannerStrip;

    private Fader _radioScreenFade = null!;

    private static RadioService Radio => App.Services.GetRequiredService<RadioService>();

    /// <summary>Whether radio is the screen the collection shows.</summary>
    private bool RadioUp => LibraryUp && _collection == CollectionScreen.Radio;

    /// <summary>Whether radio is in front with nothing over it: no page, no tuner, no search and no
    /// wheel. What its edge keys and Back's step to home ask.</summary>
    public bool RadioHomeInFront =>
        RadioUp && !RecordUp && !TunerUp && !RadioSearchOpen && !RadioWheelOpen && !LibraryMenuOpen;

    /// <summary>Builds the parts of radio that stand for the life of the chrome: the banner's hosts and its
    /// key, the wheel and the tuner's dial key. From the constructor.</summary>
    private void BuildRadio()
    {
        _radioScreenFade = new Fader(RadioScreen);
        _radioSearchFade = new Fader(RadioSearchLayer);

        var icons = (FontFamily)Resources["PegasusFa"];
        for (var i = 0; i < _bannerHosts.Length; i++)
        {
            var host = new RadioBannerHost(icons);
            var offset = i - 2;
            host.Root.Tapped += (_, e) =>
            {
                e.Handled = true;
                BannerHost_Tapped(offset);
            };
            _bannerHosts[i] = host;
            RadioBanner.Children.Add(host.Root);
        }

        _radioBannerKey = new RemoteLine(new Grid(), TuneBannerFront, menu: null, turn: StepRadioBanner)
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        RadioBanner.Children.Add(_radioBannerKey);

        Radio.Changed += () => DispatcherQueue.TryEnqueue(ShowWhatRadioPlays);

        // The server's answer moved: a station hidden, a logo changed, a title, tags or a description
        // edited. The screen is built again from the reading it was built from, without asking the
        // directory, so an answer that lands after the screen was built is shown (see RadioStateChanged).
        RadioState.Changed += () => DispatcherQueue.TryEnqueue(RadioStateChanged);

        Radio.Favorites.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            // The favourites row is the one part of the screen a press elsewhere changes (the heart in the
            // tuner), so it follows them; the banner keeps the stations it was read with until the next
            // reading, so a heart does not rearrange it under the listener.
            if (RadioIsReady)
                BuildFavoritesRow();
        });

        BuildRadioWheel();
        BuildTunerDialKey();
    }

    /// <summary>
    /// The side menu's Internet Radio: the collection stays up and crosses to radio from home or the wall,
    /// over the length the other crosses take. Read if it never has been, or if what it shows is old.
    /// </summary>
    private void CrossToRadio()
    {
        if (!LibraryUp || RecordUp || _collection == CollectionScreen.Radio)
            return;

        var from = _collection;
        _collection = CollectionScreen.Radio;
        LayOutLibraryTop();
        PaintLibraryBack();

        _radioScreenFade.To(1, ScreenCrossMs, null);
        LeaveScreen(from);
        ApplyRadioShown();
        SizeRadio(QueueScale);

        ReadRadioIfDue();
        FocusScreenLater(null);   // with the remote in use: the banner's card
    }

    /// <summary>
    /// Radio's own Visibility and its rooms, from the two facts they depend on: the collection layer is in
    /// the tree, and radio is the screen it shows. Called wherever either changes. The rooms are told
    /// directly because an element is told nothing when an ancestor is collapsed, and a room left drifting
    /// behind a collapsed screen costs the drawing thread for as long as the window is open (see
    /// CardRoom.Shown).
    /// </summary>
    private void ApplyRadioShown()
    {
        var up = LibraryLayer.Visibility == Visibility.Visible && _collection == CollectionScreen.Radio;
        RadioScreen.Visibility = up ? Visibility.Visible : Visibility.Collapsed;

        foreach (var host in _bannerHosts)
            if (host is not null)
                host.Room.Shown = up;
        foreach (var card in _radioEssentialCards)
            card.Room.Shown = up;
        foreach (var card in _radioFavoriteCards)
            card.Room.Shown = up;
    }

    // ── reading ──

    /// <summary>Reads radio if it never has been or the directory's answer is old; otherwise only the
    /// favourites, which need no network and may have changed in the main window.</summary>
    private void ReadRadioIfDue()
    {
        var fresh = _radioAnsweredAt != 0
                    && Environment.TickCount64 - _radioAnsweredAt < RadioGoodForMs;
        if (fresh)
        {
            BuildFavoritesRow();
            return;
        }

        ReadRadioAsync();
    }

    /// <summary>
    /// What radio shows: the stations heard most and the favourites, which are on this computer, and the
    /// essential stations, which the directory answers on a worker. The screen is built once the
    /// directory has answered or failed; a failure still builds the banner and the favourites.
    /// </summary>
    private async void ReadRadioAsync()
    {
        var read = ++_radioRead;
        var radio = Radio;

        // The server's word on hidden stations and changed logos. Asked at most once an hour, and nothing
        // waits on it (see RadioState).
        RadioState.Refresh();

        var (code, country) = ListenerCountry();
        RadioSpinner.Spinning = _radioBanner.Count == 0;

        IReadOnlyList<RadioStation> found = [];
        var answered = true;
        try
        {
            found = await Task.Run(() => radio.Directory.SearchAsync(new RadioQuery
            {
                CountryCode = code,
                Limit = RadioEssentialCount * 4,   // the fold below takes several streams of one station
            }));

            // A country the directory lists nothing for: the most played stations anywhere.
            if (found.Count == 0 && code is not null)
            {
                code = null;
                found = await Task.Run(() => radio.Directory.SearchAsync(new RadioQuery
                {
                    Limit = RadioEssentialCount * 4,
                }));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            answered = false;
            RPM.Core.Diagnostics.Scope.Said("radio", $"essential stations: the directory did not answer ({ex.GetType().Name})");
        }

        if (read != _radioRead)
            return;

        RadioSpinner.Spinning = false;
        _radioAnsweredAt = answered ? Environment.TickCount64 : 0;

        _radioLastReading = new RadioReading(RadioResults.OnePerStation(found), answered, code is null ? null : country);
        var (heard, favorites, essential) = BuildRadioFrom(_radioLastReading);
        RadioIsReady = true;

        // The view's opening cover waits on this when the view opened onto radio (see CollectionIsReady).
        if (RadioUp)
            LibraryFilled?.Invoke();

        RPM.Core.Diagnostics.Scope.Said(
            "radio",
            $"read: {heard} heard, {favorites} favourites, {essential} essential in {code ?? "every country"}, directory {(answered ? "answered" : "failed")}");
    }

    /// <summary>One reading of the directory, folded: what the screen is built from, kept so a change
    /// in the server's answer can build it again without asking the directory.</summary>
    private sealed record RadioReading(IReadOnlyList<RadioStation> Essential, bool Answered, string? Country);

    /// <summary>The reading the screen was last built from. Null until the first one lands.</summary>
    private RadioReading? _radioLastReading;

    /// <summary>
    /// Builds the screen from a reading, with the server's answer as it stands now: the hidden stations
    /// left out, and the edits read as each station is drawn (Display, ShownTags, BlurbAsync). The heard
    /// stations and the favourites are read again, as they are on this computer. Returns how many of
    /// each were built, for the log.
    /// </summary>
    private (int Heard, int Favorites, int Essential) BuildRadioFrom(RadioReading reading)
    {
        var radio = Radio;
        var essential = Shown(reading.Essential).Take(RadioEssentialCount).ToList();
        var heard = Shown(radio.Listens.Top(RadioBannerHeard).Select(l => l.Station).ToList());
        var favorites = Shown(radio.Favorites.Stations);

        BuildRadioHome(heard, favorites, essential, reading.Answered, reading.Country);
        return (heard.Count, favorites.Count, essential.Count);

        // The stations taken out of the app go first, before anything else looks at the list, and two that
        // print alike get their straplines back. The radio pane's own rule (RadioModel.Show).
        static List<RadioStation> Shown(IReadOnlyList<RadioStation> stations) =>
            [.. RadioNames.Distinguish([.. stations.Where(s => !RadioState.IsHidden(s))])];
    }

    /// <summary>
    /// The server's answer changed after the screen was built. Built again from the same reading, so a
    /// hidden station leaves, a changed logo is asked for at its new address, and an edited title, tags
    /// or description shows. The banner goes back to its first card, which only happens when the answer
    /// actually moved (RadioState raises Changed for nothing else).
    ///
    /// <para>Before the first reading there is nothing to rebuild, and the reading reads the answer as it
    /// stands when it lands, so either order ends with the answer shown.</para>
    /// </summary>
    private void RadioStateChanged()
    {
        if (!RadioIsReady || _radioLastReading is not { } reading)
            return;

        var (heard, favorites, essential) = BuildRadioFrom(reading);

        // A card that had the focus was let go with the rest; the screen's default takes it, unless the
        // wheel or the search has it.
        RefocusIfLost();

        RPM.Core.Diagnostics.Scope.Said(
            "radio",
            $"built again for the server's answer: {heard} heard, {favorites} favourites, {essential} essential");
    }

    /// <summary>The listener's country, as the system's region states it: its code for the directory and
    /// its name for the heading. Nothing where the region is not a country (the invariant one).</summary>
    /// <remarks>
    /// The name is the one the radio pane prints in its rows (RadioCountries.Short over the English name),
    /// not the region's own display name. That one is in Windows' display language, which the app's strings
    /// need not be: on a Dutch Windows with the app in English the heading would read "Essential
    /// stations in Nederland".
    /// </remarks>
    private static (string? Code, string Name) ListenerCountry()
    {
        try
        {
            var region = RegionInfo.CurrentRegion;
            var code = region.TwoLetterISORegionName;
            if (code.Length != 2 || !char.IsLetter(code[0]) || !char.IsLetter(code[1]) || code == "IV")
                return (null, "");
            code = code.ToUpperInvariant();
            return (code, RadioCountries.Short(region.EnglishName, code));
        }
        catch (ArgumentException)
        {
            return (null, "");
        }
    }

    /// <summary>
    /// Builds the screen from one reading: the banner, the essential row and the favourites row, every
    /// station on them one face. The faces of the last build are let go and their errands cancelled.
    /// </summary>
    private void BuildRadioHome(
        IReadOnlyList<RadioStation> heard,
        IReadOnlyList<RadioStation> favorites,
        IReadOnlyList<RadioStation> essential,
        bool answered,
        string? country)
    {
        _radioFacesRun?.Cancel();
        _radioFacesRun = new CancellationTokenSource();
        var ct = _radioFacesRun.Token;

        _radioFaces.Clear();

        // The banner: heard most, then favourites, then essential, each station once.
        _radioBanner.Clear();
        foreach (var station in heard.Concat(favorites).Concat(essential))
        {
            if (_radioBanner.Count >= RadioBannerMost)
                break;
            if (_radioBanner.Any(f => f.Station.Uuid == station.Uuid))
                continue;
            _radioBanner.Add(FaceFor(station));
        }
        _radioFront = 0;
        _bannerFrom = 0;
        _bannerT = 1;

        RadioEssentialHeading.Text = country is { Length: > 0 }
            ? Loc.Format("Viz_RadioEssential", country)
            : Loc.Get("Viz_RadioPopular");
        BuildRow(_radioEssentialCards, RadioEssentialRow, essential, wide: true);
        RadioEssentialNote.Text = answered ? "" : Loc.Get("Radio_DirectoryDown");
        RadioEssentialNote.Visibility = answered ? Visibility.Collapsed : Visibility.Visible;
        RadioEssentialScroll.Visibility = essential.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // A new build is new faces, so the favourites row is built again on them rather than kept.
        foreach (var card in _radioFavoriteCards)
            card.Drop();
        _radioFavoriteCards.Clear();
        RadioFavoritesRow.Children.Clear();
        BuildFavoritesRow();

        RadioBanner.Visibility = _radioBanner.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowBanner();
        SizeRadio(QueueScale);
        ApplyRadioShown();
        ShowWhatRadioPlays();

        // Every face on the screen is asked for once. The banner's card in front is also asked what it is
        // playing (see AskRadioOnAir).
        foreach (var face in _radioFaces.Values)
            _ = LoadFaceAsync(face, ct);
        foreach (var face in _radioBanner)
            _ = LoadLineAsync(face, ct);
        AskRadioOnAir();
    }

    /// <summary>The favourites row, from the favourites as they stand. Asked by every build and by the
    /// favourites changing.</summary>
    private void BuildFavoritesRow()
    {
        var favorites = Radio.Favorites.Stations.Where(s => !RadioState.IsHidden(s)).ToList();

        // The same stations in the same order: the row stands as it is, so a revisit does not redraw every
        // card and take the remote's place in the row away with them.
        if (favorites.Select(s => s.Uuid).SequenceEqual(_radioFavoriteCards.Select(c => c.Face.Station.Uuid))
            && (favorites.Count > 0 || RadioFavoritesNote.Visibility == Visibility.Visible))
            return;

        BuildRow(_radioFavoriteCards, RadioFavoritesRow, favorites, wide: false);
        RadioFavoritesScroll.Visibility = favorites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RadioFavoritesNote.Visibility = favorites.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        SizeRadio(QueueScale);
        ApplyRadioShown();

        if (_radioFacesRun is { } run)
            foreach (var card in _radioFavoriteCards)
                _ = LoadFaceAsync(card.Face, run.Token);
    }

    /// <summary>One row of cards, built again from the stations given. The cards it held let go of their
    /// faces first.</summary>
    private void BuildRow(List<RadioRowCard> cards, StackPanel row, IReadOnlyList<RadioStation> stations, bool wide)
    {
        foreach (var card in cards)
            card.Drop();
        cards.Clear();
        row.Children.Clear();

        var plain = (Style)Resources["PegasusPlainButton"];
        var icons = (FontFamily)Resources["PegasusFa"];
        foreach (var station in stations)
        {
            var card = new RadioRowCard(FaceFor(station), wide, plain, icons, TuneFromRadio);
            cards.Add(card);
            row.Children.Add(card.Root);
        }
    }

    /// <summary>The face of one station on this build, made the first time it is asked for.</summary>
    private StationFace FaceFor(RadioStation station)
    {
        if (!_radioFaces.TryGetValue(station.Uuid, out var face))
        {
            face = new StationFace(station);
            _radioFaces[station.Uuid] = face;
        }
        return face;
    }

    // ── a face ──

    /// <summary>
    /// A station's colours and its mark, the way home's radio card asks for them
    /// (HomeView.LoadStationFaceAsync): the station's raster mark paints the room first, then the mark
    /// itself is decoded at the largest size anything on this screen draws it. Written to the face, so a
    /// host or card that has moved on is not written.
    /// </summary>
    private async Task LoadFaceAsync(StationFace face, CancellationToken ct)
    {
        if (face.FaceAsked)
            return;
        face.FaceAsked = true;

        try
        {
            var answer = await RadioStationItem.LogoBytesAsync(face.Station, ct);
            if (ct.IsCancellationRequested)
                return;

            // Unanswered, or answered with no mark: the room keeps the colours the name chose. The walk is
            // asked again by the next build, which is the next reading.
            if (answer.Answers(out var mark, out _) && mark is { Length: > 0 }
                && await Cancellable.RunAsync(() => CoverRoom.Of(mark), ct) is { Ran: true, Value: var painted }
                && !ct.IsCancellationRequested)
                face.ShowRoom(painted);

            var found = await RadioStationItem.LogoForAsync(face.Station, RadioMarkEdge());
            if (ct.IsCancellationRequested)
                return;
            face.ShowLogo(found);
        }
        catch (OperationCanceledException)
        {
            // Built again, or the view closed.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A station's site that fails to answer says nothing about the station. Its colour stands.
            RPM.Core.Diagnostics.Scope.Said("radio", $"face of {face.Station.Display}: {ex.GetType().Name}");
        }
    }

    /// <summary>The pixel edge every mark on this screen is decoded at: the banner's, which is the largest
    /// any of them is drawn, on this display.</summary>
    private int RadioMarkEdge()
    {
        var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
        var drawn = Math.Max(_bannerHeight * 0.40, 160 * QueueScale) * raster;
        return (int)(Math.Ceiling(drawn / 64) * 64);
    }

    /// <summary>The line under a banner card's name: what the station says it is, or what Wikipedia says.
    /// Nothing is drawn where nothing is found, which is most stations.</summary>
    private static async Task LoadLineAsync(StationFace face, CancellationToken ct)
    {
        if (face.LineAsked)
            return;
        face.LineAsked = true;

        try
        {
            if (await RadioStationItem.BlurbAsync(face.Station, ct) is { Length: > 0 } says
                && !ct.IsCancellationRequested)
                face.ShowBlurb(says);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A site or an encyclopedia that fails to answer says nothing about the station.
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// What the tuned station announces, written to its face wherever it is on screen, and cleared from
    /// every other face. Raised by the tuner's changes, hopped onto this thread, and asked once by every
    /// build, because a song announced before the screen was built has no event coming.
    /// </summary>
    private void ShowWhatRadioPlays()
    {
        var radio = Radio;
        var tuned = radio.Current;
        var announced = radio.NowPlayingTitle;

        foreach (var face in _radioFaces.Values)
        {
            var isTuned = tuned is not null && face.Station.Is(tuned.Uuid);
            face.ShowPlaying(isTuned ? HomeView.Printable(announced, face.Station) : null);
        }

        SyncTunerPlayer();
    }

    /// <summary>
    /// Asks the station in front of the banner what it is playing, for the line under its name. Only the
    /// one in front, and not a station that is tuned (the tuner has its title already). Read again on the
    /// way back, because the station may have been tuned while the peek was out. Home's radio card asks the
    /// same way (HomeView.AskWhatIsOn), and the answer is held for a minute and a half
    /// (RadioStationItem.OnAirAsync).
    /// </summary>
    private async void AskRadioOnAir()
    {
        if (_radioBanner.Count == 0 || _radioFacesRun is not { } run)
            return;

        var face = _radioBanner[_radioFront];
        var ct = run.Token;
        if (IsTuned(face))
            return;

        try
        {
            var announced = HomeView.Printable(
                await RadioStationItem.OnAirAsync(face.Station, ct), face.Station);
            if (ct.IsCancellationRequested || IsTuned(face))
                return;
            face.ShowOnAir(announced);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A station that fails to answer says nothing about what is on it.
        }
        catch (OperationCanceledException)
        {
        }

        static bool IsTuned(StationFace face) => Radio.Current is { } now && face.Station.Is(now.Uuid);
    }

    // ── the banner ──

    /// <summary>Hands each host the face at its offset from the card in front, and places them.</summary>
    private void ShowBanner()
    {
        var n = _radioBanner.Count;
        for (var i = 0; i < _bannerHosts.Length; i++)
        {
            var offset = i - 2;
            _bannerHosts[i].Show(n == 0 || (n == 1 && offset != 0)
                ? null
                : _radioBanner[(((_radioFront + offset) % n) + n) % n]);

            // Only the card in front drifts: the room is a few milliseconds of the drawing thread a frame,
            // and the cards beside it are dimmed and half hidden.
            _bannerHosts[i].Room.Drift = offset == 0;
        }

        if (_radioBannerKey is { } key)
            key.IsTabStop = n > 0;
        PlaceBanner();
    }

    /// <summary>
    /// Writes every host's place, size and dimming from where a step has got to. A host's look follows
    /// its distance from the middle, so a card sliding into the middle grows and brightens as it comes and
    /// nothing changes size at the moment of the step. The nearest is drawn on top.
    /// </summary>
    private void PlaceBanner()
    {
        if (_bannerWidth <= 0 || _bannerHosts[0] is null)
            return;

        var shift = RadioBannerShift;
        var middle = _bannerStrip / 2;
        for (var i = 0; i < _bannerHosts.Length; i++)
        {
            var at = i - 2 + shift;
            var away = Math.Min(Math.Abs(at), 1);
            var host = _bannerHosts[i];
            host.Place(middle + at * _bannerPitch - _bannerWidth / 2, 1 - 0.06 * away, 0.45 * away);
            Canvas.SetZIndex(host.Root, 10 - (int)Math.Round(Math.Abs(at) * 2));
        }
    }

    /// <summary>Left or Right on the banner's card: the next card comes in front, sliding from the side
    /// it was on. False where there is nothing to step to, and the remote bounces.</summary>
    private bool StepRadioBanner(int way)
    {
        var n = _radioBanner.Count;
        if (n < 2)
            return false;

        // Turned round mid-step, the slide carries on from where it is rather than jumping.
        var now = RadioBannerShift;
        _radioFront = (((_radioFront + way) % n) + n) % n;
        _bannerFrom = now + way;
        _bannerT = 0;
        ShowBanner();
        AskRadioOnAir();
        return true;
    }

    /// <summary>One frame of the banner's step, and of the wheel's turn.</summary>
    private void TickRadio(double dt)
    {
        if (_bannerT < 1)
        {
            _bannerT = Math.Min(1, _bannerT + dt / (RadioBannerSlideMs / 1000.0));
            PlaceBanner();
        }

        TickRadioWheel(dt);
        TickTuner();
    }

    /// <summary>A press on a banner card: the one in front is tuned, one beside it comes in
    /// front.</summary>
    private void BannerHost_Tapped(int offset)
    {
        if (offset == 0)
            TuneBannerFront();
        else
            StepRadioBanner(Math.Sign(offset));
    }

    private void TuneBannerFront()
    {
        if (_radioBanner.Count > 0)
            TuneFromRadio(_radioBanner[_radioFront].Station);
    }

    /// <summary>A station chosen anywhere on radio: tuned, and said so along the foot, as home does
    /// (Home_StationRequested). A station puts nothing on the deck, so the screen stays.</summary>
    private void TuneFromRadio(RadioStation station)
    {
        Radio.Tune(station);
        Announced?.Invoke(Loc.Format("Radio_TunedTo", station.Display));
    }

    // ── the remote ──

    /// <summary>Where the focus starts on radio: the banner's card, else the first card of a row.</summary>
    private Control? RadioFocusTarget()
    {
        if (_radioBannerKey is { IsTabStop: true } key)
            return key;
        if (_radioEssentialCards.Count > 0)
            return _radioEssentialCards[0].Root;
        return _radioFavoriteCards.Count > 0 ? _radioFavoriteCards[0].Root : null;
    }

    /// <summary>
    /// An Up or Down on radio that would pass over the banner lands on the banner's card: from a row's
    /// outer cards nothing on the banner shares their column, and the search by layout would go past it to
    /// the corner key. Home keeps the same rule (HomeView.OverBanner). Above and below are read off each
    /// element's middle against the banner's edges.
    /// </summary>
    private Control? RadioOverBanner(FrameworkElement from, FocusNavigationDirection direction, Control? next)
    {
        if (_radioBannerKey is not { IsTabStop: true } key || ReferenceEquals(from, key)
            || direction is not (FocusNavigationDirection.Up or FocusNavigationDirection.Down)
            || RadioBanner.Visibility != Visibility.Visible)
            return next;

        var top = RadioBanner.TransformToVisual(Root).TransformPoint(default).Y;
        var bottom = top + RadioBanner.ActualHeight;
        var fromMiddle = MiddleOf(from);

        var passes = direction == FocusNavigationDirection.Up
            ? fromMiddle > bottom && (next is null || MiddleOf(next) < top)
            : fromMiddle < top && (next is null || MiddleOf(next) > bottom);
        return passes ? key : next;

        double MiddleOf(FrameworkElement element) =>
            element.TransformToVisual(Root).TransformPoint(default).Y + element.ActualHeight / 2;
    }

    // ── sizes ──

    /// <summary>
    /// Radio's sizes at the collection's scale: the banner's cards off the width (three across, the one in
    /// front 0.45 of the strip and the two beside it under its edges), the
    /// rows' cards (four essential and six favourites across), the headings, and the wheel, the search and
    /// the tuner, which are sized here too because they are one screen's.
    /// </summary>
    private void SizeRadio(double scale)
    {
        var width = LibraryLayer.ActualWidth > 0 ? LibraryLayer.ActualWidth : Root.ActualWidth;
        var height = LibraryLayer.ActualHeight > 0 ? LibraryLayer.ActualHeight : Root.ActualHeight;
        if (width <= 1)
            return;

        var inset = LibraryEdgeInset;
        RadioContent.Margin = new Thickness(inset, HomeTopPad, inset, 34 * scale);
        var strip = Math.Max(320, width - inset * 2);

        // The banner: the card in front and the two behind it span the strip. Each card beside it is the
        // front card's size, drawn down a little, its middle 0.6 of a card's width from the middle.
        _bannerStrip = strip;
        _bannerWidth = strip / 2.14;
        _bannerHeight = Math.Min(_bannerWidth / 2.17, height * 0.40);
        _bannerPitch = _bannerWidth * 0.6;
        RadioBanner.Height = _bannerHeight;
        RadioBanner.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, strip, _bannerHeight) };
        foreach (var host in _bannerHosts)
            host?.Size(_bannerWidth, _bannerHeight, scale);
        if (_radioBannerKey is { } key)
        {
            key.Width = _bannerWidth;
            key.Height = _bannerHeight;
            key.Margin = new Thickness((strip - _bannerWidth) / 2, 0, 0, 0);
            key.CornerRadius = new CornerRadius(12 * scale);
        }
        PlaceBanner();

        // The rows.
        var gap = 22 * scale;
        var essential = (strip - gap * 3) / 4;
        var favorite = (strip - gap * 5) / 6;
        RadioEssentialRow.Spacing = RadioFavoritesRow.Spacing = gap;
        foreach (var card in _radioEssentialCards)
            card.Size(essential, scale);
        foreach (var card in _radioFavoriteCards)
            card.Size(favorite, scale);

        var heading = new Thickness(0, 30 * scale, 0, 12 * scale);
        RadioEssentialHeading.FontSize = RadioFavoritesHeading.FontSize = 22 * scale;
        RadioEssentialHeading.Margin = RadioFavoritesHeading.Margin = heading;
        RadioEssentialNote.FontSize = RadioFavoritesNote.FontSize = 14 * scale;

        SizeRadioWheel(scale, height);
        SizeRadioSearch(scale, width, height);
        SizeTuner();
    }

    /// <summary>Radio's scroller moved: the mini player goes on a scroll down, by the rule home's and the
    /// wall's scrollers keep (see Collection_Scrolled).</summary>
    private void RadioScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        var offset = RadioScroll.VerticalOffset;
        var moved = offset - _radioScrollAt;
        _radioScrollAt = offset;
        if (RadioUp && Math.Abs(moved) > 0.5)
            Collection_Scrolled(moved);
    }

    private double _radioScrollAt;

    // ── closing what is open over radio ──

    /// <summary>Closes radio's wheel or its search, whichever is open, for Back. Names what it closed, or
    /// null where neither was open.</summary>
    public string? CloseRadioOverlay()
    {
        if (RadioWheelOpen)
        {
            ShowRadioWheel(false);
            return "wheel";
        }
        if (RadioSearchOpen)
        {
            ShowRadioSearch(false);
            return "search";
        }
        return null;
    }

    /// <summary>Closes both at once, for the collection going down or crossing away from radio.</summary>
    private void CloseRadioOverlays()
    {
        ShowRadioWheel(false);
        ShowRadioSearch(false);
    }
}
