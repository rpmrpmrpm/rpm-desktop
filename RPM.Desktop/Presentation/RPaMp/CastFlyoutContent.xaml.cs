using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.RPaMp;

/// <summary>The cast destination picker, shared by the player popout and the deck bar. Its DataContext
/// is the one <see cref="RPaMpModel"/>, inherited from the button that opens it as a flyout.</summary>
public sealed partial class CastFlyoutContent : UserControl
{
    public CastFlyoutContent()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private RPaMpModel? Model => DataContext as RPaMpModel;

    /// <summary>Speakers come and go, so the list is rebuilt each time the menu opens rather than kept.
    /// The scan takes a couple of seconds, and the menu says so while it runs. The host's flyout calls
    /// this on Opening, so every re-open scans again.</summary>
    public void Refresh()
    {
        CapHeight();
        if (Model is { } model)
        {
            _ = model.RefreshCastTargetsAsync();
            _ = model.RefreshFirewallAsync();
        }
    }

    // Cap the list to the window height minus room for the deck it opens from and a top gap.
    // A taller list scrolls inside the cap. The cap uses the live window size, so it updates on resize.
    // Until the XamlRoot is known, the list has no cap.
    private void CapHeight()
    {
        if (XamlRoot is { } root && root.Size.Height > 0)
            CastScroll.MaxHeight = System.Math.Max(240, root.Size.Height - 140);
    }

    private void CastLocal_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (Model is { } model)
            _ = model.CastToAsync(null);
    }

    private void CastDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CastTarget target) return;
        var root = XamlRoot;   // Taken before the menu closes, because the firewall dialog needs the XamlRoot.
        Close();
        if (Model is { } model)
            _ = CastAsync(model, target, root);
    }

    /// <summary>Casts after asking to let rpm through Windows Firewall, when the firewall blocks it.
    /// Without that, the speaker cannot fetch the stream.</summary>
    private static async Task CastAsync(RPaMpModel model, CastTarget target, XamlRoot? root)
    {
        if (await Dialogs.FirewallDialog.BeforeCastingAsync(root, target.Device.Name, model.LastCastNeverFetched))
            await model.CastToAsync(target);
    }

    private void CastRescan_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Firewall_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model)
            _ = model.FixFirewallAsync();
    }

    // The flyout content is hosted in a Popup. Walk up to the Popup and close it after a destination is
    // picked. This way the control does not need to know which flyout (popout or deck) hosts it.
    private void Close()
    {
        DependencyObject? d = this;
        while (d is not null and not Microsoft.UI.Xaml.Controls.Primitives.Popup)
            d = VisualTreeHelper.GetParent(d);
        if (d is Microsoft.UI.Xaml.Controls.Primitives.Popup popup)
            popup.IsOpen = false;
    }
}
