using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Sharing;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>
/// One computer under "On this network", drawn from a <see cref="NearbyComputer"/>.
///
/// <para>Kept by <see cref="NearbyList"/> for as long as the computer has a row, and redrawn in place
/// by <see cref="Apply"/> on every change. Built once rather than per redraw because of the code box:
/// a status reply arrives every few seconds and a row rebuilt for it would drop the digits being
/// typed and the caret with them.</para>
/// </summary>
public sealed partial class NearbyRow : UserControl
{
    /// <summary>What the two buttons do in the current state.</summary>
    private enum RowAction { None, Ask, Connect, Cancel, Decline, Dismiss }

    private NearbyComputer? _item;
    private string _label = "";
    private RowAction _primary;
    private RowAction _secondary;

    /// <summary>The attempt the code box belongs to. A new attempt empties the box.</summary>
    private long _attempt;

    /// <summary>The last code could not be delivered. Cleared by the next send and by a new attempt.</summary>
    private bool _sendFailed;

    /// <summary>A code is on its way. Connect stays off until the reply.</summary>
    private bool _sending;

    public NearbyRow() => InitializeComponent();

    public string DeviceId => _item?.DeviceId ?? "";

    /// <summary>Raised for the two buttons. The list decides what each one means to the app.</summary>
    public event Action<NearbyRow, string>? AskRequested;
    public event Action<NearbyRow, string, string>? CodeEntered;
    public event Action<NearbyRow, string>? CancelRequested;
    public event Action<NearbyRow, string>? DeclineRequested;
    public event Action<NearbyRow, string>? DismissRequested;

    /// <summary>Whether this row shows a time limit, so the list knows to keep redrawing it.</summary>
    public bool IsCounting => _item?.Pairing is { IsLive: true, ExpiresAtUtc: var at } && at > DateTime.MinValue;

    /// <summary>Draws the row for what is true now. Called on every change and once a second while a
    /// time limit is shown.</summary>
    /// <param name="label">What to call the computer: its owner's name when known, else its own.</param>
    public void Apply(NearbyComputer item, string label)
    {
        _item = item;
        _label = label;

        var pairing = item.Pairing;
        if (pairing is not null && pairing.Attempt != _attempt)
        {
            _attempt = pairing.Attempt;
            _sendFailed = false;
            _sending = false;
            CodeBox.Text = "";
        }

        bool entryWasShown = EntryPanel.Visibility is Visibility.Visible;

        NameText.Text = label;
        InitialText.Text = label.Length > 0 ? char.ToUpperInvariant(label[0]).ToString() : "";

        CodeText.Visibility = Visibility.Collapsed;
        EntryPanel.Visibility = Visibility.Collapsed;
        TimeText.Visibility = Visibility.Collapsed;

        var (status, primary, secondary) = item.Kind switch
        {
            NearbyKind.CanAsk => ("", RowAction.Ask, RowAction.None),
            NearbyKind.Full => (Loc.Get("Share_LimitReached"), RowAction.None, RowAction.None),
            NearbyKind.NeedsUpdateThere => (Loc.Format("Share_NeedsUpdateThere", label), RowAction.None, RowAction.None),
            NearbyKind.NeedsUpdateHere => (Loc.Format("Share_NeedsUpdateHere", label), RowAction.None, RowAction.None),
            _ => Describe(pairing!),
        };

        StatusText.Text = status;
        StatusText.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (pairing is { IsLive: true } && pairing.State is PairingState.ShowingCode or PairingState.WaitingForCode)
        {
            var left = pairing.ExpiresAtUtc - DateTime.UtcNow;
            if (left < TimeSpan.Zero)
                left = TimeSpan.Zero;

            TimeText.Text = Loc.Format("Share_TimeLeft", $"{(int)left.TotalMinutes}:{left.Seconds:D2}");
            TimeText.Visibility = Visibility.Visible;
        }

        // Empty for the moment between a round starting and the asking computer's nonce arriving.
        if (pairing is { State: PairingState.ShowingCode, Code.Length: > 0 })
        {
            CodeText.Text = pairing.Code;
            CodeText.Visibility = Visibility.Visible;
        }

        if (pairing is { State: PairingState.WaitingForCode })
        {
            EntryPanel.Visibility = Visibility.Visible;

            var error = _sendFailed ? Loc.Format("Share_Unreachable", label)
                      : pairing.WrongCode ? Loc.Format("Share_CodeWrongNewCode", label, pairing.TriesLeft)
                      : "";
            EntryError.Text = error;
            EntryError.Visibility = error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Focus is set only when the box first appears, not on every redraw. The row is redrawn
            // once a second for the time left, and taking focus that often would stop the user typing.
            if (!entryWasShown)
                CodeBox.Focus(FocusState.Programmatic);
        }

        _primary = primary;
        _secondary = secondary;

        PrimaryText.Text = primary switch
        {
            RowAction.Ask when pairing is { Direction: PairingDirection.Asking } => Loc.Get("Share_AskAgain"),
            RowAction.Ask => Loc.Get("Share_Ask"),
            RowAction.Connect => Loc.Get("Share_Connect"),
            _ => "",
        };
        PrimaryButton.Visibility = primary is RowAction.None ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.IsEnabled = primary is not RowAction.Connect || (CodeBox.Text.Length == 6 && !_sending);

        SecondaryText.Text = secondary switch
        {
            RowAction.Cancel => Loc.Get("Share_Cancel"),
            RowAction.Decline => Loc.Get("Share_Decline"),
            RowAction.Dismiss => Loc.Get("Share_Dismiss"),
            _ => "",
        };
        SecondaryButton.Visibility = secondary is RowAction.None ? Visibility.Collapsed : Visibility.Visible;

        Buttons.Visibility = primary is RowAction.None && secondary is RowAction.None
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>The sentence and the two buttons for a pairing in each state.</summary>
    private (string Status, RowAction Primary, RowAction Secondary) Describe(Pairing pairing)
    {
        var name = _label;
        bool asking = pairing.Direction is PairingDirection.Asking;

        return pairing.State switch
        {
            PairingState.Sending or PairingState.CrossedWaiting =>
                (Loc.Format("Share_Waiting", name), RowAction.None, RowAction.Cancel),
            PairingState.WaitingForCode =>
                (Loc.Format("Share_EnterCode", name), RowAction.Connect, RowAction.Cancel),
            PairingState.ShowingCode =>
                (Loc.Format("Share_AskedQuestion", name) + " " + Loc.Format("Share_ShowCodeHint", name),
                 RowAction.None, RowAction.Decline),

            PairingState.Accepted => (Loc.Format("Share_Accepted", name), RowAction.None, RowAction.Dismiss),
            PairingState.Declined => (Loc.Format("Share_Declined", name), RowAction.Ask, RowAction.Dismiss),
            PairingState.Expired => (Loc.Get("Share_Expired"), RowAction.Ask, RowAction.Dismiss),
            PairingState.TooManyTries => (Loc.Get("Share_TooManyTries"), RowAction.Ask, RowAction.Dismiss),
            PairingState.Withdrawn => (Loc.Format("Share_Withdrawn", name), RowAction.Ask, RowAction.Dismiss),
            PairingState.Lost => (Loc.Format("Share_Lost", name), RowAction.Ask, RowAction.Dismiss),
            PairingState.Unreachable => (Loc.Format("Share_Unreachable", name), RowAction.Ask, RowAction.Dismiss),
            PairingState.Resting => (Loc.Format("Share_Resting", name), RowAction.None, RowAction.Dismiss),
            PairingState.NeedsUpdateThere => (Loc.Format("Share_NeedsUpdateThere", name), RowAction.None, RowAction.Dismiss),

            // Full on the asking side means the other computer is at its limit. On the asked side,
            // this computer is at its limit.
            PairingState.Full => (asking ? Loc.Format("Share_Full", name) : Loc.Get("Share_LimitReached"),
                                  RowAction.None, RowAction.Dismiss),

            _ => ("", RowAction.None, RowAction.None),
        };
    }

    /// <summary>The list says the send finished; the board has the answer, this only has whether it
    /// arrived.</summary>
    public void Sent(bool delivered)
    {
        _sending = false;
        _sendFailed = !delivered;

        if (_item is { } item)
            Apply(item, _label);

        if (delivered && _item?.Pairing is { State: PairingState.WaitingForCode, WrongCode: true })
        {
            CodeBox.SelectAll();
            CodeBox.Focus(FocusState.Programmatic);
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => Do(_primary);

    private void Secondary_Click(object sender, RoutedEventArgs e) => Do(_secondary);

    private void Do(RowAction action)
    {
        if (_item is not { } item)
            return;

        switch (action)
        {
            case RowAction.Ask:
                AskRequested?.Invoke(this, item.DeviceId);
                break;
            case RowAction.Connect:
                Submit();
                break;
            case RowAction.Cancel:
                CancelRequested?.Invoke(this, item.DeviceId);
                break;
            case RowAction.Decline:
                DeclineRequested?.Invoke(this, item.DeviceId);
                break;
            case RowAction.Dismiss:
                DismissRequested?.Invoke(this, item.DeviceId);
                break;
        }
    }

    private void Submit()
    {
        var code = CodeBox.Text.Trim();
        if (_item is not { } item || code.Length != 6 || _sending)
            return;

        _sending = true;
        _sendFailed = false;
        PrimaryButton.IsEnabled = false;
        EntryError.Visibility = Visibility.Collapsed;

        CodeEntered?.Invoke(this, item.DeviceId, code);
    }

    private void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Digits only. A letter would make the code look wrong when it is not.
        var digits = new string([.. CodeBox.Text.Where(char.IsDigit)]);
        if (digits != CodeBox.Text)
        {
            CodeBox.Text = digits;
            CodeBox.SelectionStart = digits.Length;
            return;
        }

        if (_primary is RowAction.Connect)
            PrimaryButton.IsEnabled = digits.Length == 6 && !_sending;
    }

    private void CodeBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is not Windows.System.VirtualKey.Enter)
            return;

        e.Handled = true;
        Submit();
    }
}
