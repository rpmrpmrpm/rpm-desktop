using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Migration;
using RPM.Core.Ripping;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services.Migration;
using RPM.Desktop.Services.Shell;
using RPM.Desktop.Strings;
using Windows.Foundation;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>
/// The deck's import chip: the import icon with a progress ring. It is the import window, minimised. It
/// reads everything it shows from <see cref="MigrationService"/>, so the chip and the window are two
/// views of one import.
/// </summary>
public sealed partial class ImportChip : UserControl
{
    private static readonly Color Cyan = Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF);
    private static readonly Color Gold = Color.FromArgb(0xFF, 0xEF, 0xAF, 0x12);

    private const double Centre = 12;
    private const double Radius = 9;
    private const int RingSegments = 48;
    private const double MinArc = 0.04;

    private readonly MigrationService _service;

    // At most one redraw waits on the UI thread; a fast import reports after every track.
    private int _redrawQueued;

    public ImportChip()
    {
        InitializeComponent();
        _service = App.Services.GetRequiredService<MigrationService>();
        FillRing(RingTrack, 1);

        Loaded += (_, _) =>
        {
            _service.Changed += OnServiceChanged;
            Sync();
        };
        Unloaded += (_, _) => _service.Changed -= OnServiceChanged;
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 1)
            return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _redrawQueued, 0);
            Sync();
        }))
            Interlocked.Exchange(ref _redrawQueued, 0);
    }

    private void Sync()
    {
        var end = _service.LastEnd;
        var stopped = !_service.IsRunning && end is not null && end != RunEnd.Finished;
        var unseenReport = !_service.IsRunning && end == RunEnd.Finished && !_service.ReportSeen;
        var show = _service.IsRunning || stopped || unseenReport;
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;

        var p = _service.Progress;
        var done = p is null ? 0 : p.Counts.Imported + p.Counts.AlreadyInRpm + p.Counts.NotImported;
        var fraction = unseenReport ? 1
            : p is { BytesTotal: > 0 } ? (double)p.BytesDone / p.BytesTotal
            : p is { Total: > 0 } ? (double)done / p.Total
            : 0;

        // Gold when it stands stopped and waits for the person; cyan while it runs or when it is done.
        var ink = new SolidColorBrush(stopped ? Gold : Cyan);
        RingArc.Stroke = ink;
        ImportGlyph.Foreground = stopped ? ink : new SolidColorBrush(Microsoft.UI.Colors.White);
        FillRing(RingArc, Math.Max(fraction, MinArc));

        var total = p?.Total ?? 0;
        IconName.Set(ChipButton, Loc.Format("Migration_Status", _service.CurrentLabel,
            done.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
    }

    private async void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is not { } root)
            return;
        await Dialogs.MigrationDialog.ShowAsync(root, _service, App.Services.GetRequiredService<FolderPickerService>());
    }

    /// <summary>Sets the ring to a fraction of a full circle, as plain points from the top, going clockwise.</summary>
    private static void FillRing(Microsoft.UI.Xaml.Shapes.Polyline line, double fraction)
    {
        var points = new PointCollection();
        foreach (var (x, y) in ProgressArc.Points(fraction, Centre, Centre, Radius, RingSegments))
            points.Add(new Point(x, y));
        line.Points = points;
    }
}
