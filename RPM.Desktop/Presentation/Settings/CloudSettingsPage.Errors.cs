using System.ComponentModel;
using System.Runtime.InteropServices;
using RPM.Core.Cloud;
using RPM.Desktop.Strings;
using Microsoft.Extensions.DependencyInjection;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>
/// One thing that went wrong, in full, with a button to send it in.
///
/// <para>This row only observes, unlike <see cref="CloudHistoryRow"/>. A log line is a fact and never
/// changes. This row has a button, and a button that has been pressed must be able to show that.</para>
/// </summary>
[RpmBindable]
public sealed partial class CloudErrorRow : INotifyPropertyChanged
{
    private bool _sending;
    private bool _sent;
    private bool _refused;

    public CloudErrorRow(CloudActivityEntry entry, long nowUtcTicks)
    {
        Entry = entry;
        Title = entry.Title;
        Detail = entry.Detail is { Length: > 0 } d ? d : Loc.Get("Cloud_HistoryFailedSuffix");

        // The sentence shown here comes first. Then comes everything the log kept about the failure:
        // the whole output of ffmpeg, the exception chain and the stack. Rows logged before the full
        // text was kept have only the sentence.
        ReportDetail = entry.FullError is { Length: > 0 } full ? $"{Detail}\n\n{full}" : Detail;

        // Every row in this list is a refusal, because the page selects on that. So the verb names the
        // attempt, never an achievement. "Uploaded", with the reason in red underneath, would
        // contradict itself and read like two different tracks.
        var verb = Loc.Get(entry.Op switch
        {
            CloudOp.Remove => "CloudChip_StageFailedRemove",
            CloudOp.Restore => "CloudChip_StageFailedRestore",
            CloudOp.Offload => "CloudChip_StageFailedOffload",
            _ => "CloudChip_StageFailedUpload",
        });

        var age = CloudActivityAge.Of(entry.AtUtcTicks, nowUtcTicks);
        var when = age.Unit switch
        {
            AgeUnit.JustNow => Loc.Get("Cloud_HistoryJustNow"),
            AgeUnit.Minutes => Loc.Format("Cloud_HistoryMinutes", age.Value),
            AgeUnit.Hours => Loc.Format("Cloud_HistoryHours", age.Value),
            _ => Loc.Format("Cloud_HistoryDays", age.Value),
        };

        // What it was and when, on one line. The album name tells the user which track this is. The age
        // shows whether the problem is still happening.
        Line = string.IsNullOrWhiteSpace(entry.Subtitle)
            ? $"{verb} · {when}"
            : $"{verb} · {entry.Subtitle} · {when}";
    }

    public CloudActivityEntry Entry { get; }

    public string Title { get; }
    public string Line { get; }
    public string Detail { get; }

    /// <summary>What a report sends as its detail. Not shown on the card.</summary>
    public string ReportDetail { get; }

    /// <summary>Pressed and still in progress. The button goes quiet rather than repeating
    /// itself.</summary>
    public bool IsSending
    {
        get => _sending;
        set { _sending = value; Changed(); }
    }

    /// <summary>Sent, and the server took it. A report is sent once.</summary>
    public bool IsSent
    {
        get => _sent;
        set { _sent = value; Changed(); }
    }

    /// <summary>Sent, and it did not arrive. The button stays active, because the network may have
    /// failed and the user is allowed to try again.</summary>
    public bool WasRefused
    {
        get => _refused;
        set { _refused = value; Changed(); }
    }

    public bool CanReport => !IsSending && !IsSent;

    public string ReportLabel => IsSent
        ? Loc.Get("Cloud_ErrorsReported")
        : IsSending ? Loc.Get("Cloud_ErrorsReporting") : Loc.Get("Cloud_ErrorsReport");

    public string RefusedText => WasRefused ? Loc.Get("Cloud_ErrorsReportRefused") : "";

    public Visibility RefusedVisibility => WasRefused ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed()
    {
        foreach (var name in new[]
                 { nameof(CanReport), nameof(ReportLabel), nameof(RefusedText), nameof(RefusedVisibility) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed partial class CloudSettingsPage
{
    /// <summary>
    /// The failures out of the log book, with their reasons.
    ///
    /// <para>The failures are kept apart from the history. A failure's own words can be four lines of
    /// ffmpeg output. Four such lines under every entry for a month would make a page that nobody
    /// reads. Here the full text is the main point, so there is room for it.</para>
    /// </summary>
    private void LoadErrors()
    {
        if (!_library.IsOpen)
        {
            ErrorList.ItemsSource = null;
            ErrorEmptyText.Visibility = Visibility.Visible;
            return;
        }

        // Only the failures that nothing has fixed since. A track that failed and then uploaded is no
        // longer an error. A track that failed five times counts as one error.
        var now = DateTime.UtcNow.Ticks;
        var rows = CloudActivityFailures.Outstanding(_library.Db.QueryCloudActivity())
            .Select(e => new CloudErrorRow(e, now))
            .ToList();

        // Rows already sent stay sent across a reload. The log is re-read each time a track finishes,
        // and a button that reset itself under the pointer would be reported twice.
        foreach (var row in rows.Where(r => _reported.Contains(r.Entry.Id)))
            row.IsSent = true;

        ErrorList.ItemsSource = rows;
        ErrorEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Which log rows have been sent this session. This is not saved. A report is a one-off
    /// courtesy, and the queue on the other end is where a duplicate is cheap to drop.</summary>
    private readonly HashSet<long> _reported = [];

    private async void OnReportError(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CloudErrorRow row } || !row.CanReport)
            return;

        row.WasRefused = false;
        row.IsSending = true;

        try
        {
            var client = App.Services.GetRequiredService<RpmCloudClient>();
            var sent = await client.ReportErrorAsync(new ErrorReport(
                Area: "cloud",
                Operation: row.Entry.Op.ToString().ToLowerInvariant(),
                Title: row.Entry.Title,
                Subtitle: row.Entry.Subtitle,
                Detail: row.ReportDetail,
                AppVersion: App.VersionText,
                Platform: RuntimeInformation.OSDescription,
                AtUtcTicks: row.Entry.AtUtcTicks));

            if (sent is { Length: > 0 })
            {
                _reported.Add(row.Entry.Id);
                row.IsSent = true;
            }
            else
            {
                row.WasRefused = true;
            }
        }
        catch
        {
            // Signed out, offline, or an API failure: from here these all look the same. The
            // report did not go, and the button should still work.
            row.WasRefused = true;
        }
        finally
        {
            row.IsSending = false;
        }
    }
}
