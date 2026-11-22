using RPM.Core.Library;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Main.Models;

/// <summary>
/// The tag stage: the second step of adding music, after the scan. The queue shows the same animation as the
/// scan, with its own words, while the catalogue fill finds the genre, subgenre, year, explicit marks and disc
/// picture of each record just added. Then the queue opens for editing.
///
/// <para><b>Why it is a stage of its own.</b> The fill writes onto the records while they are on screen. A
/// person who starts editing straight away would see boxes change under them. The stage lets the fill finish
/// first, at full speed, and says which record it is on. The tag panel keeps what somebody typed in any case
/// (see ImportTagPanel.SetRows), so leaving the stage early is safe.</para>
///
/// <para><b>What it covers.</b> Only the records this add brought, and only their release fill (see
/// MainModel.CatalogueFill). The fill asks about the records it has not answered for yet, and the stage counts
/// the ones that came with this add. The explicit marks asked song by song come after every release, a few
/// seconds apart, and they go on in the background after the stage: they only set explicit flags.</para>
///
/// <para><b>When there is no stage.</b> The fill is switched off, nobody is signed in, the computer is offline,
/// or this add brought no record the fill has not answered for already.</para>
///
/// <para><b>How it ends.</b> The release fill has run for every record it covers; or the user leaves it with
/// its button, and the fill goes on in the background as it does without a stage; or the fill ends for any
/// reason (the catalogue stopped answering, the library closed); or nothing has moved for
/// <see cref="StageStall"/>, because a stage that waits on a catalogue that never answers is a hang.</para>
///
/// <para><b>The governor.</b> While the stage is on screen the user is waiting for the fill and doing nothing
/// else, so the fill is waited on (see <see cref="Services.Shell.Sweep.Next(CancellationToken, Func{bool})"/>):
/// the stage's own animation keeps the drawing thread busy, and must not hold the fill it is showing.</para>
/// </summary>
public sealed partial class MainModel
{
    /// <summary>How long the stage waits without any record finishing before it gives up and opens the queue. A
    /// release asks the rpm API once or twice and each request may take up to twenty seconds, so a slower
    /// record is a catalogue that is not answering.</summary>
    private static readonly TimeSpan StageStall = TimeSpan.FromSeconds(25);

    /// <summary>Guards the fields below. The fill reports from its own thread; the screen reads on the
    /// interface thread.</summary>
    private readonly object _stageGate = new();

    /// <summary>The records the stage still waits for, by the fill's group keys.</summary>
    private readonly HashSet<string> _stageLeft = new(StringComparer.OrdinalIgnoreCase);

    private int _stageTotal;
    private int _stageDone;
    private string _stageArtist = "";
    private string _stageAlbum = "";
    private long _stageMovedAt;

    /// <summary>How many times records were added to the stage. The fill reads it before it reads the queue, so a
    /// record added after that read is not taken for one that is missing from the queue.</summary>
    private int _stageAdds;

    private int TagStageAdds => Volatile.Read(ref _stageAdds);

    /// <summary>Whether the stage is on. Read by the fill's thread through <see cref="TagStageWaiting"/>.</summary>
    private volatile bool _stageOn;

    private DispatcherQueueTimer? _stageWatch;

    /// <summary>The stage's animation. Not while a scan or an import has the pane: each has its own.</summary>
    public Visibility TagStageVisibility => TagStageShown ? Visibility.Visible : Visibility.Collapsed;

    private bool TagStageShown => _stageOn && !_scanning && !_importing;

    /// <summary>The line under the bar: which record the fill is on, and how far through it is.</summary>
    public string TagStageCountText
    {
        get
        {
            lock (_stageGate)
            {
                if (_stageTotal == 0)
                    return "";
                var at = Math.Min(_stageDone + 1, _stageTotal);
                return _stageAlbum.Length == 0
                    ? Loc.Format("Queue_TagStageCount", at, _stageTotal)
                    : Loc.Format("Queue_TagStageAlbum", at, _stageTotal, _stageArtist, _stageAlbum);
            }
        }
    }

    public double TagStageProgressMax
    {
        get
        {
            lock (_stageGate)
                return Math.Max(1, _stageTotal);
        }
    }

    public double TagStageProgressValue
    {
        get
        {
            lock (_stageGate)
                return _stageDone;
        }
    }

    /// <summary>Whether the user is watching the fill right now. The fill's sweep asks this before each record
    /// (see <see cref="Services.Shell.Sweep.Next(CancellationToken, Func{bool})"/>).</summary>
    private bool TagStageWaiting() => _stageOn;

    /// <summary>Whether the stage waits for this record. The fill asks about these first.</summary>
    private bool TagStageCovers(string key)
    {
        if (!_stageOn)
            return false;
        lock (_stageGate)
            return _stageLeft.Contains(key);
    }

    /// <summary>
    /// Starts the stage for the records an add brought, or adds them to the stage already on screen. On the
    /// interface thread, before the scan's animation goes, so one animation follows the other.
    /// </summary>
    /// <param name="keys">The group keys of the records the add brought. A record the fill has already answered
    /// for is left out: the fill will not ask about it again.</param>
    private void BeginTagStage(IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0 || !_settings.ImportFillFromCatalogue || !_library.IsOpen)
            return;

        // Signed out or offline: the fill may still find something, or may not, and a stage that waits on it
        // would be a wait for nothing.
        if (!App.Services.GetRequiredService<Services.Account.AuthService>().IsLoggedIn
            || RPM.Core.Net.NetworkReach.Shared.Now != RPM.Core.Net.Reach.Online)
            return;

        var added = 0;
        lock (_stageGate)
        {
            if (!_stageOn)
            {
                _stageLeft.Clear();
                _stageTotal = _stageDone = 0;
                _stageArtist = _stageAlbum = "";
            }

            foreach (var key in keys)
            {
                if (!FillAnswered(key) && _stageLeft.Add(key))
                    added++;
            }
            _stageTotal += added;
            if (added > 0)
                Interlocked.Increment(ref _stageAdds);
        }

        if (added == 0)
            return;

        Interlocked.Exchange(ref _stageMovedAt, Environment.TickCount64);
        RPM.Core.Diagnostics.Scope.Said("import", FormattableString.Invariant($"tag stage: {added} records"));
        if (!_stageOn)
        {
            _stageOn = true;
            WatchTagStage();
            RaiseQueueChrome();
        }
        RaiseTagStage();
    }

    /// <summary>The fill has started on a record. Called from the fill's thread.</summary>
    private void TagStageAt(string key, string artist, string album)
    {
        lock (_stageGate)
        {
            if (!_stageOn || !_stageLeft.Contains(key))
                return;
            _stageArtist = artist;
            _stageAlbum = album;
        }
        Interlocked.Exchange(ref _stageMovedAt, Environment.TickCount64);
        _dispatcher.TryEnqueue(RaiseTagStage);
    }

    /// <summary>The fill is done with a record, whatever it found. Called from the fill's thread. The last record
    /// ends the stage.</summary>
    private void TagStageDone(string key)
    {
        bool last;
        lock (_stageGate)
        {
            if (!_stageOn || !_stageLeft.Remove(key))
                return;
            _stageDone++;
            last = _stageLeft.Count == 0;
        }
        Interlocked.Exchange(ref _stageMovedAt, Environment.TickCount64);
        _dispatcher.TryEnqueue(() =>
        {
            if (last)
                EndTagStage("done");
            else
                RaiseTagStage();
        });
    }

    /// <summary>The fill has read the queue: a record the stage waits for that is not in it cannot be reached, and
    /// is no longer waited for. This happens when a file the add found was already queued under another record.
    /// Called from the fill's thread.</summary>
    /// <param name="queued">The keys of every record in the queue, as the fill read them.</param>
    /// <param name="staged"><see cref="TagStageAdds"/> as it was before that read. When records were added to the
    /// stage since, the read may be older than they are, and nothing is dropped.</param>
    private void TagStageKeepOnly(IReadOnlySet<string> queued, int staged)
    {
        bool empty;
        lock (_stageGate)
        {
            if (!_stageOn || _stageAdds != staged)
                return;
            var gone = _stageLeft.Where(k => !queued.Contains(k)).ToList();
            if (gone.Count == 0)
                return;
            foreach (var key in gone)
                _stageLeft.Remove(key);
            _stageTotal -= gone.Count;
            empty = _stageLeft.Count == 0;
        }
        _dispatcher.TryEnqueue(() =>
        {
            if (empty)
                EndTagStage("nothing to fill");
            else
                RaiseTagStage();
        });
    }

    /// <summary>The user leaves the stage with its button. The fill goes on in the background, as it does when
    /// there is no stage.</summary>
    public void SkipTagStage() => EndTagStage("skipped");

    /// <summary>Ends the stage and opens the queue for editing. On the interface thread. Does nothing when there is
    /// no stage.</summary>
    private void EndTagStage(string why)
    {
        if (!_stageOn)
            return;

        _stageOn = false;
        lock (_stageGate)
        {
            _stageLeft.Clear();
            _stageTotal = _stageDone = 0;
            _stageArtist = _stageAlbum = "";
        }
        _stageWatch?.Stop();
        RPM.Core.Diagnostics.Scope.Said("import", $"tag stage ended: {why}");
        RaiseQueueChrome();
        RaiseTagStage();
    }

    /// <summary>Checks every two seconds that the stage is still moving. See <see cref="StageStall"/>.</summary>
    private void WatchTagStage()
    {
        if (_stageWatch is null)
        {
            _stageWatch = _dispatcher.CreateTimer();
            _stageWatch.Interval = TimeSpan.FromSeconds(2);
            _stageWatch.Tick += (_, _) =>
            {
                if (!_stageOn)
                {
                    _stageWatch?.Stop();
                    return;
                }
                var still = Environment.TickCount64 - Interlocked.Read(ref _stageMovedAt);
                if (still > StageStall.TotalMilliseconds)
                    EndTagStage("the catalogue did not answer");
            };
        }
        _stageWatch.Start();
    }

    private void RaiseTagStage()
    {
        OnChanged(nameof(TagStageVisibility));
        OnChanged(nameof(TagStageCountText));
        OnChanged(nameof(TagStageProgressMax));
        OnChanged(nameof(TagStageProgressValue));
    }

    /// <summary>The fill's key for each record among the rows an add brought. Unknown artist and unknown album are
    /// buckets, not records, and the fill does not ask about them.</summary>
    private static HashSet<string> RecordKeysOf(IEnumerable<QueueTrack> rows)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var (artist, album) = LibraryDatabase.QueueGroupKeys(row.Wanted.AlbumArtist, row.Wanted.Artist, row.Wanted.Album);
            if (artist.Length > 0 && album.Length > 0)
                keys.Add(FillKey(artist, album));
        }
        return keys;
    }
}
