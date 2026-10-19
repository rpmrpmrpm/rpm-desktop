using System.Collections.ObjectModel;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The behaviour of one card: playing what it holds, loading its cover once (and only once, across every
/// recycle), and pulsing its cloud badge while the album is being uploaded to the cloud or removed from
/// it.
/// </summary>
public sealed partial class AlbumWallView
{
    // ── track / album playback ──
    //
    // The presses inside an open drawer are handled by the drawer (see AlbumBandView). The drawer is wired
    // to these same hooks when it is realized. See ApplyBandVisual. What is left here is what the wall
    // itself must handle: a mark made from a menu, which lands on whatever is on screen.

    /// <summary>
    /// Updates one song's heart wherever the wall shows it. This is called when the right-click menu's
    /// favourite entry changes a song, so the line under the pointer changes with the menu.
    /// </summary>
    public void ApplyTrackFavorite(string filePath, bool favorite)
    {
        foreach (var line in _band.Current?.Lines ?? [])
            if (line.IsTrack && string.Equals(line.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                line.IsFavorite = favorite;
    }

    /// <summary>Puts one record's sticker on, or takes it off, where it stands. This is the menu's
    /// favourite entry, but for the record rather than the song. The wall is not rebuilt for this. A
    /// sticker only concerns one record.</summary>
    public void ApplyAlbumFavorite(string folder, bool favorite)
    {
        if (_cardByFolder.TryGetValue(folder, out var card))
            card.IsFavorite = favorite;

        // The sticker itself is on the open drawer's sleeve, so the change must be applied to the drawer.
        // The card only holds the value for the next drawer that is opened on it.
        if (_band.Current is { } band
            && string.Equals(band.Card.Folder, folder, StringComparison.OrdinalIgnoreCase))
            band.IsFavorite = favorite;
    }

    // ── the focus pull: the sharp layer fading in over the resident one ──
    //
    // The tween is in Common.SharpFade. It uses ONE clock for every tile that fades anywhere. That clock
    // beats at the same rate as the badges and the playing mark, for the same reason. On Skia, a write
    // repaints the whole window, so the cost of a fade is its writes, not its arithmetic. A separate
    // Task.Yield loop per tile would write each 200 ms fade many thousands of times. See that class for
    // the cost this would have on a scroll.

    /// <summary>
    /// The tile has been given another record, or it has been given back its sharp picture.
    ///
    /// <para>The element is recycled, so it arrives with whatever opacity it was left at. A tile that was
    /// fully sharp would show the next record's small picture at full strength, through a layer that still
    /// claims to be opaque. The fade would then never run again, because the layer was already at one. So
    /// the opacity is reset to zero whenever there is no sharp picture to show.</para>
    ///
    /// <para>This is also where the layer is put on the fade's watch. It is the first moment the element
    /// is in hand, and this runs on every bind. Both halves are keyed on the same fact: whether this
    /// record has a sharp picture. So it does not matter which of the rebind and the binding's own push
    /// arrives first. See <see cref="Common.SharpFade.Watch"/>, which also explains why this does not use
    /// <c>ImageOpened</c>.</para>
    /// </summary>
    private void SharpCover_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Image layer)
            Common.SharpFade.Watch(layer);

        if ((args.NewValue as AlbumCard)?.Sharp is null)
        {
            Common.SharpFade.Stop(sender);
            sender.Opacity = 0;
        }
    }

    // ── what a tile does when it is bound ──

    /// <summary>A recycled tile rebound to another record. The same rule as the row's own realize
    /// applies: show what the card already holds, and request nothing. The loader works out the
    /// rest.</summary>
    private void Card_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        // A tile recycled under the pointer keeps what it was showing. The key on its sleeve is the only
        // thing on a tile that depends on the pointer, not on the record.
        ShowOpenKey(sender, false);

        WallDiag.Time("bind", () => CardBound(args));
    }

    private void CardBound(DataContextChangedEventArgs args)
    {
        if (args.NewValue is not AlbumCard card)
            return;
        Stream.Realized(card);
        Load();
    }

    // ── the key on the sleeve ──

    /// <summary>
    /// The pointer comes onto a tile's artwork. This is the only time the way into the record's own page
    /// is drawn. The key is drawn on a picture. If it were shown all the time, the wall would be a wall
    /// of keys, and the wall is for covers.
    ///
    /// <para>The key is found by name inside the tile's own template, not held in a field. The wall
    /// recycles its tiles, so a field would point at whichever tile was bound last.</para>
    /// </summary>
    private void Art_PointerEntered(object sender, PointerRoutedEventArgs e) => ShowOpenKey(sender, true);

    private void Art_PointerExited(object sender, PointerRoutedEventArgs e) => ShowOpenKey(sender, false);

    /// <summary>
    /// Puts the key on the sleeve up, or takes it down.
    /// </summary>
    /// <remarks>
    /// <b>The two directions are not symmetrical.</b> The key is deferred in the template
    /// (<c>x:Load</c>). Asking for it by name is what BUILDS it. So a hide that searched for it would
    /// build a key on every card it was asked to hide. That is every card on every rebind, and the saving
    /// would be spent on building keys.
    ///
    /// <para>So only the SHOW path looks the key up by name. The hide path uses <see cref="_openKey"/>,
    /// which is the one key that is up at the moment. At most one key is up, because the pointer is in
    /// one place. A card whose key was never built has nothing to take down, so it asks for
    /// nothing.</para>
    /// </remarks>
    private void ShowOpenKey(object sender, bool show)
    {
        if (!show)
        {
            DropOpenKey();
            return;
        }

        if ((sender as FrameworkElement)?.FindName("OpenSlot") is not FrameworkElement key)
            return;

        // Whatever was up before this one is taken down, wherever it is. A pointer that leaves A and
        // enters B usually reports the leaving first. But a tile recycled from under the pointer does not
        // report it at all. A key left lit on a card that the pointer is no longer on would never go out.
        if (!ReferenceEquals(key, _openKey))
            DropOpenKey();

        key.Opacity = 1;
        key.IsHitTestVisible = true;
        _openKey = key;
    }

    private void DropOpenKey()
    {
        if (_openKey is not { } key)
            return;
        _openKey = null;
        key.Opacity = 0;
        key.IsHitTestVisible = false;
    }

    /// <summary>The one key on the wall that is up at the moment, or null. See <see cref="ShowOpenKey"/>
    /// for why it is held rather than looked up.</summary>
    private FrameworkElement? _openKey;

    /// <summary>The press is marked as handled, so the tile under the key does not also open its drawer.
    /// The key and the tile it sits on are two ways in, and one press uses only one of them.</summary>
    private void OpenPage_PointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumCard card)
            MenuInvoked?.Invoke(AlbumMenuCommand.AlbumPage, card.Folder);
    }


    /// <summary>
    /// Reads one record's cloud mark from the mirror. This is a job like any other, and the loader
    /// decides when it runs. It comes after every cover on screen. A corner cloud that arrives a moment
    /// late costs nothing. A picture that arrives late means the wall is not working.
    ///
    /// <para>The mark is written to the CARD, never to the recycled container. So an answer that comes
    /// back after the tile has been handed to another record cannot land on the wrong album.</para>
    /// </summary>
    private async Task ReadBadgeAsync(AlbumCard card)
    {
        if (CloudBadgeLoader is not { } load)
            return;

        // Set before the read, not after. The read either answers or throws. A badge that throws must not
        // be offered again as a job on every pass of the loader.
        card.BadgeRead = true;
        var badge = await Task.Run(() => load(card.Folder));
        SetBadge(card, badge);
    }

    /// <summary>
    /// Reads one album again, from disk and from the mirror. Call this after its files have been retagged
    /// or its cover has been replaced.
    ///
    /// <para>Nothing here happens on its own. A card is built once per folder and then reused by every
    /// rebuild that still lists that folder (see <see cref="SetAlbums"/>). The loader only fetches what a
    /// card does not already have. So a library change that leaves an album where it is, which is every
    /// edit that does not move files, changes nothing on the wall unless it is said here.</para>
    /// </summary>
    public void RefreshAlbum(string folder)
    {
        if (!_cardByFolder.TryGetValue(folder, out var card))
            return;

        // Everything read from the old picture is now stale. Clearing it is how the loader is told to
        // read it again: it looks for cards that lack what they should have. The card keeps the picture
        // it is showing meanwhile, so the tile sharpens in place instead of going blank for the length of
        // a disk read.
        card.Tint = null;
        ForgetGround(folder);   // the ground was read off the picture being replaced
        card.CoverMissing = false;
        card.MissedEdge = 0;
        card.CoverEdge = 0;
        card.Tiny = null;
        card.Sharp = null;   // drop the old sharp layer so it does not cover the new picture.
        Load();
        // An open drawer holds the tracklist and the large cover it fetched when it opened. Both now
        // belong to the old record. If the drawer is shut, this does nothing.
        _band.Reopen(folder);
    }

    /// <summary>
    /// Reads every card's cloud badge again. Call this after the cloud state changes, so the corner marks
    /// stay up to date without a full wall rebuild. The read is off the UI thread. The answers are
    /// applied back on the UI thread in one loop.
    ///
    /// <para><b>One query for the whole wall, not one per card.</b> A query for each card
    /// would be a thousand takes of the one gate that every library read goes through, on a thousand-record
    /// shelf. This refresh runs, among other moments, when a shared shelf is opened, or when its lender
    /// starts or stops answering. Those are exactly the moments when somebody is scrolling. A single read
    /// has held the gate for about 19 s.</para>
    /// </summary>
    public async void RefreshBadges()
    {
        if (CloudBadgesLoader is not { } load)
            return;

        var cards = _cards.ToList();
        List<(AlbumCard Card, AlbumCloudBadge Badge)> badges;
        try
        {
            badges = await Task.Run(() =>
            {
                var marks = load([.. cards.Select(c => c.Folder)]);
                return cards
                    .Select(c => (c, marks.TryGetValue(c.Folder, out var b) ? b : AlbumCloudBadge.None))
                    .ToList();
            });
        }
        catch
        {
            return;   // stale corners until the next cloud change, not a fault
        }

        // Through SetBadge, like every other writer. It keeps the busy count true, and so keeps the
        // breath true. It is cheap per card, and it only reaches SyncBadgePulse on an actual change.
        foreach (var (card, badge) in badges)
            SetBadge(card, badge);
    }

    /// <summary>
    /// Reads the cloud mark on every line of the OPEN drawer. This is the tracklist's half of
    /// <see cref="RefreshBadges"/>, and it is sent on the same signal.
    ///
    /// <para>The lines are re-badged where they stand, not rebuilt. A rebuilt list would lose the
    /// drawer's scroll position. It would also blink a tracklist that somebody is reading, just because
    /// one of its songs finished uploading. When the wall is shut, which is most of the time, this does
    /// nothing.</para>
    /// </summary>
    public async void RefreshTrackBadges()
    {
        if (_band.ExpandedFolder is not { } folder || TracksLoader is not { } load ||
            _band.Current is not { Lines.Count: > 0 })
            return;

        IReadOnlyList<TrackInfo> tracks;
        try
        {
            tracks = await load(folder, CancellationToken.None);
        }
        catch
        {
            return;   // stale marks until the next cloud change, not a fault
        }

        // The drawer may have been shut, or pointed at a neighbouring album, while the folder was being
        // read.
        if (!string.Equals(_band.ExpandedFolder, folder, StringComparison.OrdinalIgnoreCase) ||
            _band.Current is not { } band)
            return;

        AlbumTrackItem.ApplyCloud(band.Lines, tracks, CloudUploadsStopped?.Invoke() == true,
            ShelfReading?.Invoke() ?? RPM.Core.Cloud.ShelfReading.Own);
        _lineBreath.Sync();
    }

    /// <summary>The breath on the open drawer's track marks. It belongs to the wall, not to the cards. A
    /// line is made and dropped by the drawer that shows it, and the tiles outlive every drawer. It is
    /// set in the constructor, once the band conductor it reads through exists.</summary>
    private readonly Common.CloudLineBreath _lineBreath;

    // ── the badge's breath while an album is going up or coming back out ──

    private bool _pulsing;

    /// <summary>
    /// How many cards on the wall are mid-transfer. This is a count, not a search.
    ///
    /// <para>The count is the whole point. <see cref="SyncBadgePulse"/> runs once for each badge that
    /// comes back. Earlier, it answered by walking every card in the collection. <c>Any</c> stops early
    /// only when something IS uploading. So in the usual case, where nothing is uploading, it scanned the
    /// whole library on the UI thread, once for each card realized. On a large wall, that cost grew with
    /// rows times columns times the size of the collection. It arrived as a burst of continuations at the
    /// moment the repeater was realizing rows. This caused a visible dip while scrolling.</para>
    /// </summary>
    private int _busyCards;

    /// <summary>
    /// Writes a card's cloud mark, and keeps <see cref="_busyCards"/> in step with it. Every writer must go
    /// through here. Otherwise the count is wrong, and the breath is driven by the wrong value.
    /// </summary>
    private void SetBadge(AlbumCard card, AlbumCloudBadge badge)
    {
        var was = card.IsCloudBusy;
        card.CloudBadge = badge;
        var now = card.IsCloudBusy;
        if (was == now)
            return;

        // Only cards the wall is holding are counted. A read or a refresh can answer for a card that a
        // feed has dropped since. If that card were counted, the count would never come back down, and
        // the badge would breathe over a wall with nothing going up.
        if (!_cardByFolder.TryGetValue(card.Folder, out var live) || !ReferenceEquals(live, card))
            return;

        _busyCards += now ? 1 : -1;
        SyncBadgePulse();
    }

    /// <summary>Counts the wall's busy cards from scratch. This is the one pass over the whole
    /// collection. It runs where the collection itself is replaced (<see cref="SetAlbums"/>), not once
    /// per badge.</summary>
    private void RecountBusyCards()
    {
        _busyCards = 0;
        foreach (var card in _cards)
            if (card.IsCloudBusy)
                _busyCards++;
        SyncBadgePulse();
    }

    /// <summary>
    /// Breathes only while some album on the wall is busy. It stops as soon as none is. Joining and
    /// leaving <see cref="BadgeBreath"/> is always a full stop. A looping storyboard does not work that
    /// way under Uno, for an element that stays in the tree. The playlist rows breathe on this same clock
    /// too, so the tile and the row for one album stay in step.
    /// </summary>
    private void SyncBadgePulse()
    {
        var wanted = _busyCards > 0;
        if (wanted == _pulsing)
            return;

        if (wanted)
        {
            _pulsing = true;
            BadgeBreath.Join(OnBadgePulseTick);
        }
        else
        {
            StopBadgePulse();
        }
    }

    private void StopBadgePulse()
    {
        if (!_pulsing)
            return;
        _pulsing = false;
        BadgeBreath.Leave(OnBadgePulseTick);

        // Nothing is busy any more, so no badge should be left caught mid-breath.
        foreach (var card in _cards)
            card.CloudPulse = RPM.Core.Cloud.CloudBadgePulse.Rest;
    }

    private void OnBadgePulseTick(double opacity)
    {
        foreach (var card in _cards)
            if (card.IsCloudBusy)
                card.CloudPulse = opacity;
    }
}
