using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Sharing;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>
/// The deck's sharing chip.
///
/// <para>The flyout has two lists. At the top are the collections this computer can be standing in. The
/// user's own collection comes first, and the one on screen is lit. Under it is "On this network". It
/// shows every computer that can be asked, and every pairing that is in progress or has just ended.
/// <see cref="NearbyList"/> draws this part.</para>
///
/// <para>Everything here is read from the service on every change. Nothing about a pairing is kept on the
/// chip. So closing the flyout is harmless: the request is on the board, not in the flyout, and opening it
/// again shows where it stands. The dot on the chip is read the same way
/// (<see cref="SharingService.NeedsAttention"/>).</para>
///
/// <para>The flyout opens by itself once for each request from another computer, and once per session for
/// each computer found on the network (<see cref="SharingService.TryClaimAutoOpen"/>). At all other times
/// the person opens it.</para>
/// </summary>
public sealed partial class ShareChip : UserControl
{
    private readonly SharingService? _sharing;
    private readonly Services.Library.LibraryService? _library;

    /// <summary>The names and pictures of the people who own the shared computers. This returns what is known
    /// now and never waits for a lookup. A name or picture that arrives late is shown on the next redraw.</summary>
    private readonly PeerFaces? _faces;

    /// <summary>Whether there is an account to open somebody else's shelf with. It is read on every redraw
    /// and not kept, because signing in and out can change at any time.</summary>
    private readonly Services.Account.AccountGate? _gate;

    public ShareChip()
    {
        InitializeComponent();

        _sharing = App.Services.GetService<SharingService>();
        _library = App.Services.GetService<Services.Library.LibraryService>();
        _faces = App.Services.GetService<PeerFaces>();
        _gate = App.Services.GetService<Services.Account.AccountGate>();

        // The list draws the rows and reports which one was picked. This chip decides what the pick means.
        LibraryList.Picked += LibraryPicked;

        Loaded += (_, _) =>
        {
            if (_sharing is { } sharing)
                sharing.Changed += OnChanged;
            if (_library is { } library)
                library.Changed += OnChanged;
            if (_faces is { } faces)
                faces.Changed += OnChanged;

            // Signing out takes the chip off the deck and signing in puts it back, without either
            // waiting for a restart.
            if (_gate is { } gate)
                gate.Changed += OnChanged;

            Sync();
        };
        Unloaded += (_, _) =>
        {
            if (_sharing is { } sharing)
                sharing.Changed -= OnChanged;
            if (_library is { } library)
                library.Changed -= OnChanged;
            if (_faces is { } faces)
                faces.Changed -= OnChanged;
            if (_gate is { } gate)
                gate.Changed -= OnChanged;
        };
    }

    /// <summary>From any thread.</summary>
    private void OnChanged() => DispatcherQueue.TryEnqueue(Sync);

    /// <summary>
    /// Draws the chip for what is true now: the shelves, the computers on the network, the dot, and whether
    /// the chip is on the deck at all.
    /// </summary>
    private void Sync()
    {
        if (_sharing is not { } sharing)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var rows = Rows();
        var nearby = sharing.Nearby();

        if (rows.Count > 0)
            LibraryList.Show(rows);
        LibraryList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        NearbyPanel.Show(nearby);
        NearbyPanel.Visibility = nearby.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        AttentionDot.Visibility = sharing.NeedsAttention ? Visibility.Visible : Visibility.Collapsed;

        bool any = rows.Count > 0 || nearby.Count > 0;
        Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        if (!any)
        {
            ShareFlyout.Hide();
            return;
        }

        if (ClaimAutoOpen(sharing, nearby) && !ShareFlyout.IsOpen)
            DispatcherQueue.TryEnqueue(() => ShareFlyout.ShowAt(ChipButton));
    }

    /// <summary>
    /// Whether something on the list has not yet opened the flyout this session. This covers a new request
    /// from another computer, identified by its id, and a computer seen for the first time, identified by
    /// its device id. Each one is claimed, not only the first, so two that arrive together open the flyout
    /// only once.
    /// </summary>
    private static bool ClaimAutoOpen(SharingService sharing, IReadOnlyList<NearbyComputer> nearby)
    {
        bool open = false;
        foreach (var item in nearby)
        {
            var key = item.Pairing switch
            {
                { Direction: PairingDirection.Asked, State: PairingState.ShowingCode } asked => "question:" + asked.Id,
                null when item.Kind is NearbyKind.CanAsk => "found:" + item.DeviceId,
                _ => null,
            };

            if (key is not null && sharing.TryClaimAutoOpen(key))
                open = true;
        }

        return open;
    }

    /// <summary>The collections on offer, with their names and details. They are rebuilt on every change and
    /// not kept in step. There are six rows at most. One of the facts in them is whether another computer
    /// answered a moment ago.</summary>
    private List<SharedLibraryRow> Rows()
    {
        if (_sharing is not { } sharing)
            return [];

        // Started here, not earlier, because this is when the names are needed. The lookups are worth nothing
        // before then. It does not block. The rows below are built from whatever is already known.
        // Anything unknown raises Changed, and this method runs again.
        _faces?.Learn(sharing.Peers);

        // The user's own row is included, and its picture is fetched like anyone else's. So this is also where
        // a lookup that failed gets another try. It costs nothing while a lookup is in progress or an
        // answer is already known.
        _faces?.LearnSelf(SelfLookup.IfDue);

        Func<string, PeerFace> faceOf =
            _faces is { } faces ? faces.Face : static _ => PeerFace.Nobody;

        return SharedLibraryRow.Build(
            sharing.Peers,
            _gate is { IsOpen: true } ? ShelfAccess.Open : ShelfAccess.NeedsAccount,
            PeerLibraryStore.Exists,
            sharing.IsReachable,
            faceOf,
            _library?.ViewingPeer,
            _faces?.Self ?? PeerFace.Nobody,
            Loc.Get("Share_MyLibrary"),
            Loc.Get("Settings_ShareOnline"),
            Loc.Get("Settings_ShareOffline"));
    }

    /// <summary>
    /// Switches to another collection, or back to the user's own. Choosing the one already on screen does
    /// nothing except close the flyout, so the wall does not flicker.
    ///
    /// <para>Another person's collection needs an account. Half of a borrowed shelf is records that the
    /// lender put in the cloud, and the signed URL that plays one is issued to whoever asks. So choosing
    /// one asks for sign-in first, then switches. Switching back to the user's own collection never needs
    /// sign-in. It is always allowed, because a way out that asks for an account would trap the user on a
    /// shelf they cannot use.</para>
    /// </summary>
    private async void LibraryPicked(string deviceId)
    {
        if (_sharing is not { } sharing)
            return;

        ShareFlyout.Hide();

        var viewing = _library?.ViewingPeer ?? "";
        if (string.Equals(deviceId, viewing, StringComparison.OrdinalIgnoreCase))
            return;

        if (deviceId.Length == 0)
        {
            sharing.CloseLibrary();
            Sync();
            return;
        }

        if (!await Dialogs.SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForSharing")))
            return;

        sharing.OpenLibrary(deviceId);
        Sync();
    }
}
