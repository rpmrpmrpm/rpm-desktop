using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Sharing;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>
/// The rows under "On this network", one <see cref="NearbyRow"/> per computer.
///
/// <para>The host calls <see cref="Show"/> with a fresh <see cref="SharingService.Nearby"/> whenever
/// the service says something changed. Rows are matched by device id and kept, so a redraw updates a
/// row in place rather than rebuilding it. What a button means is decided here, against the service;
/// the rows only say which was pressed.</para>
/// </summary>
public sealed partial class NearbyList : UserControl
{
    private readonly SharingService? _sharing;
    private readonly PeerFaces? _faces;

    /// <summary>The rows on screen, by device id.</summary>
    private readonly Dictionary<string, NearbyRow> _rows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Redraws the time left once a second while any row shows one.</summary>
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public NearbyList()
    {
        InitializeComponent();

        _sharing = App.Services.GetService<SharingService>();
        _faces = App.Services.GetService<PeerFaces>();

        _tick.Tick += (_, _) => Tick();
        Unloaded += (_, _) => _tick.Stop();
        Loaded += (_, _) => KeepTicking();
    }

    /// <summary>How many rows are on screen. Zero means the host can hide the section.</summary>
    public int Count => _rows.Count;

    /// <summary>Draws the rows for what is true now. On the UI thread.</summary>
    public void Show(IReadOnlyList<NearbyComputer> items)
    {
        var wanted = new HashSet<string>(items.Select(i => i.DeviceId), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _rows.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            RowsPanel.Children.Remove(_rows[gone]);
            _rows.Remove(gone);
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!_rows.TryGetValue(item.DeviceId, out var row))
            {
                row = NewRow();
                _rows[item.DeviceId] = row;
            }

            // In the order given, moving a row only when it is in the wrong place: moving the row
            // that holds the focused code box would take the focus with it.
            int at = RowsPanel.Children.IndexOf(row);
            if (at != i)
            {
                if (at >= 0)
                    RowsPanel.Children.RemoveAt(at);
                RowsPanel.Children.Insert(Math.Min(i, RowsPanel.Children.Count), row);
            }

            row.Apply(item, Label(item));
        }

        KeepTicking();
    }

    private string Label(NearbyComputer item) =>
        _faces is { } faces
            ? faces.Label(item.DeviceId, item.Face, item.Name)
            : PeerLabel.For(PeerFace.Nobody, item.Face, item.Name);

    private NearbyRow NewRow()
    {
        var row = new NearbyRow();
        row.AskRequested += OnAsk;
        row.CodeEntered += OnCode;
        row.CancelRequested += (_, id) => _sharing?.Cancel(id);
        row.DeclineRequested += (_, id) => _sharing?.Decline(id);
        row.DismissRequested += (_, id) => _sharing?.Dismiss(id);
        return row;
    }

    /// <summary>
    /// Asks that computer. Needs an account, asked for here rather than at the far end of the
    /// handshake: sharing is arranged between accounts, so a pairing made without one is an
    /// arrangement neither machine can keep.
    /// </summary>
    private async void OnAsk(NearbyRow row, string deviceId)
    {
        if (_sharing is not { } sharing)
            return;

        if (!await Dialogs.SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForSharing")))
            return;

        sharing.Ask(deviceId);
    }

    private async void OnCode(NearbyRow row, string deviceId, string code)
    {
        bool delivered = _sharing is { } sharing && await sharing.SubmitCodeAsync(deviceId, code);

        // The row may have been handed to another computer or removed while the code was out; the
        // board has the answer either way, and this only says whether it arrived.
        if (string.Equals(row.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            row.Sent(delivered);
    }

    private void Tick()
    {
        if (_sharing is { } sharing)
            Show(sharing.Nearby());
        else
            KeepTicking();
    }

    private void KeepTicking()
    {
        bool counting = IsLoaded && _rows.Values.Any(r => r.IsCounting);
        if (counting && !_tick.IsEnabled)
            _tick.Start();
        else if (!counting && _tick.IsEnabled)
            _tick.Stop();
    }
}
