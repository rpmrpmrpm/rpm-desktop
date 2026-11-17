using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>One line of the cloud's log book, ready to show: what was done, to what, when, and in what
/// colour. It is built once from a stored entry. A log line never changes.</summary>
[RpmBindable]
public sealed partial class CloudHistoryRow
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));

    public CloudHistoryRow(CloudActivityEntry entry, long nowUtcTicks)
    {
        var failed = entry.Outcome == CloudOutcome.Failed;

        Title = entry.Title;
        Ink = failed ? Bad : Ok;

        Glyph = entry.Op switch
        {
            CloudOp.Remove => "\uf1f8",
            CloudOp.Restore => "\uf063",
            CloudOp.Offload => "\uf0c2",
            _ => "\uf062",
        };

        var verb = Loc.Get(entry.Op switch
        {
            CloudOp.Remove => "Cloud_HistoryRemoved",
            CloudOp.Restore => "Cloud_HistoryDownloaded",
            CloudOp.Offload => "Cloud_HistoryOffloaded",
            _ => "Cloud_HistoryUploaded",
        });

        // The album under the verb, and a plain statement of failure in place of the reason. The reason
        // is not here. It can run to four lines of whatever refused, which would bury a month of
        // ordinary activity. The errors section carries the full reasons.
        var trailer = failed ? Loc.Get("Cloud_HistoryFailedSuffix") : entry.Subtitle;

        Line = string.IsNullOrWhiteSpace(trailer)
            ? failed ? $"{verb}, {Loc.Get("Cloud_HistoryFailedSuffix")}" : verb
            : failed ? $"{verb}, {trailer}" : $"{verb} · {trailer}";

        var age = CloudActivityAge.Of(entry.AtUtcTicks, nowUtcTicks);
        When = age.Unit switch
        {
            AgeUnit.JustNow => Loc.Get("Cloud_HistoryJustNow"),
            AgeUnit.Minutes => Loc.Format("Cloud_HistoryMinutes", age.Value),
            AgeUnit.Hours => Loc.Format("Cloud_HistoryHours", age.Value),
            _ => Loc.Format("Cloud_HistoryDays", age.Value),
        };
    }

    public string Title { get; }
    public string Line { get; }
    public string When { get; }
    public string Glyph { get; }
    public Brush Ink { get; }
}

public sealed partial class CloudSettingsPage
{
    /// <summary>Reads the log for the history and the errors, and only when lines were added or removed.</summary>
    private readonly CloudActivityFollower _log = new();

    /// <summary>
    /// Fills the history and the errors from the log. It is called on load, and whenever the engine
    /// reports a change, since a new line can exist then.
    ///
    /// <para>The engine reports a change many times per track (each stage, each count), and writes a log
    /// line only when a track ends. <see cref="CloudActivityFollower"/> skips the reports that wrote
    /// nothing, so a long upload does not rebuild both lists several times a second.</para>
    /// </summary>
    private void LoadLog()
    {
        if (!_library.IsOpen)
        {
            _log.Forget();
            ShowHistory([], 0);
            ShowErrors([], 0);
            return;
        }

        if (_log.ReadIfChanged(_library.Db) is not { } lines)
            return;

        var now = DateTime.UtcNow.Ticks;
        ShowHistory(lines, now);
        ShowErrors(lines, now);
    }

    private void ShowHistory(IReadOnlyList<CloudActivityEntry> lines, long nowUtcTicks)
    {
        HistoryHint.Text = Loc.Format("Cloud_HistoryHint", CloudActivityRetention.Days);

        var rows = lines.Select(e => new CloudHistoryRow(e, nowUtcTicks)).ToList();

        HistoryList.ItemsSource = rows;
        HistoryEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.IsEnabled = rows.Count > 0;
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (_library.IsOpen)
            _library.Db.ClearCloudActivity();
        LoadLog();
    }
}
