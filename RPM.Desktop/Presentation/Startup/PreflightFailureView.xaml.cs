using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Startup;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Presentation.Startup;

/// <summary>One failed item as the screen shows it.</summary>
[RpmBindable]
public sealed partial class PreflightRow
{
    public PreflightRow(PreflightFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        Name = failure.Item.Name;
        Purpose = Strings.Loc.Get(failure.Item.PurposeKey);
        Fault = Strings.Loc.Get(failure.Fault switch
        {
            PreflightFault.Damaged => "Preflight_FaultDamaged",
            PreflightFault.Unreadable => "Preflight_FaultUnreadable",
            _ => "Preflight_FaultMissing",
        });

        // Where the app looked is the part the user can act on. For a bundled file it is the folder
        // the file belongs in. For a resolved tool it is the last place the resolver tried.
        Where = failure.Path ?? AppPayload.BaseDirectory;
        if (failure.Detail is { Length: > 0 } detail)
            Where = $"{Where}  ·  {detail}";

        // Red for what stops the app, amber for what only costs a feature. This is the only colour
        // on the screen, and it shows the one difference that matters here.
        Mark = new SolidColorBrush(failure.Item.Severity == PreflightSeverity.Required
            ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x5A, 0x52)
            : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF0, 0xB4, 0x32));
    }

    public string Name { get; }
    public string Purpose { get; }
    public string Fault { get; }
    public string Where { get; }
    public Brush Mark { get; }
}

/// <summary>What the failure screen binds to.</summary>
[RpmBindable]
public sealed partial class PreflightFailureModel
{
    public PreflightFailureModel(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
        Blocking = [.. report.Blocking.Select(f => new PreflightRow(f))];
        Degraded = [.. report.Degraded.Select(f => new PreflightRow(f))];
    }

    public PreflightReport Report { get; }
    // These use their own list type because both are bound to an ItemsSource. Under Native AOT that
    // needs a type for which CsWinRT generated WinRT interfaces; see Common.PreflightRowList (RPM007).
    public Common.PreflightRowList Blocking { get; }
    public Common.PreflightRowList Degraded { get; }
    public bool HasBlocking => Blocking.Count > 0;
    public bool HasDegraded => Degraded.Count > 0;
}

/// <summary>
/// The screen the app shows instead of itself when the install is incomplete. Closing it quits the
/// app, because there is nothing to continue to.
/// </summary>
public sealed partial class PreflightFailureView : UserControl
{
    public PreflightFailureView(PreflightReport report)
    {
        InitializeComponent();
        Model = new PreflightFailureModel(report);
        DataContext = Model;
    }

    public PreflightFailureModel Model { get; }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(Preflight.Describe(Model.Report, AppPayload.BaseDirectory));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            CopiedNote.Visibility = Visibility.Visible;
        }
        catch
        {
            // If the clipboard cannot be opened, a second error dialog on top of this one would not
            // help.
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();
}
