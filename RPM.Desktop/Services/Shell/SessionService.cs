using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// The app's own saved state: where the interface was, as opposed to the choices the user made.
///
/// <para>This is kept apart from <see cref="SettingsService"/>, in its own <c>session.json</c> file. A
/// setting is something that somebody decided. This is a note that the app keeps for itself, so that
/// reopening the app brings back the screen that was left: the pane that was open, the shelf's scroll and
/// the queue that was waiting. The two also change at very different rates. A scroll offset moves every
/// frame. The file settings.json is read by code that reacts to it. A save there makes the shell reload its
/// backdrop, which is a full-screen image decode for a mouse wheel.</para>
///
/// <para>Nothing here sends notifications, and nothing reacts to it. It is written as the user works, and
/// read once at startup by whoever owns the thing being restored. Nothing in it is trusted on the way back
/// in. See <see cref="SessionRestore"/> for why a stored value is a claim about a world that
/// may have changed.</para>
///
/// <para>Writes are coalesced (<see cref="SaveDelay"/>) and flushed when the window closes. A drag across
/// the window, or a fling down a wall of covers, is one write, not two hundred.</para>
/// </summary>
public sealed partial class SessionService : IDisposable
{
    /// <summary>How long a change waits for the next one before it is written. This is long enough to fold
    /// a whole gesture into one write, and short enough that a crash loses only the last moment of
    /// it.</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    /// <summary>The longest a change may stay unwritten while more changes keep arriving.</summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    private sealed record Model(
        int WindowLeft = 0,
        int WindowTop = 0,
        int WindowWidth = 0,
        int WindowHeight = 0,
        bool WindowMaximized = false,
        string? View = null,
        bool LeftPaneOpen = false,
        string? LeftTab = null,
        string? RightTab = null,
        double LeftStars = 0,
        double RightStars = 0,
        string LibrarySearch = "",
        bool FavoritesOnly = false,
        double WallScroll = 0,
        double ListScroll = 0,
        double TableScroll = 0,
        string? TableSortColumn = null,
        bool TableSortDescending = false,
        IReadOnlyDictionary<string, double>? TableColumnWidths = null,
        IReadOnlyList<string>? TableHiddenColumns = null,
        IReadOnlyList<string>? Queue = null,
        int QueueIndex = -1,
        string? QueueTrack = null,
        double QueueSeconds = 0,
        bool Shuffle = false,
        bool Repeat = false);

    private Model _model = new();

    /// <summary>False when the session file is there but could not be read at startup. In that case the
    /// file is not written over. See RPM.Core.Files.StoreFile.</summary>
    private bool _mayWrite = true;
    private readonly Timer _write;
    private bool _disposed;

    /// <summary>When the note on disk stopped matching the note in memory. Null means that they
    /// agree.</summary>
    private DateTime? _dirtySince;

    public SessionService()
    {
        _write = new Timer(_ => Write(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // Quitting from the dock or with Cmd+Q ends the process without closing the window. So the window's
        // own flush never runs. The EQ writes its curve here for the same reason.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Flush(); } catch { } };
    }

    private static string StorePath => Path.Combine(LocalData.Dir, "session.json");

    // ── the window itself ──

    /// <summary>
    /// Where the window was and how big it was, in physical pixels. Windows only.
    ///
    /// <para>macOS does not use this. AppKit remembers a window's frame itself (see
    /// <see cref="WindowPlacement"/>). That handles the screen being unplugged, resolution changes and the
    /// menu bar, none of which this app can see. Uno implements neither <c>DisplayArea.WorkArea</c> nor
    /// <c>MoveAndResize</c>.</para>
    /// </summary>
    public (int Left, int Top, int Width, int Height) WindowFrame
    {
        get => (_model.WindowLeft, _model.WindowTop, _model.WindowWidth, _model.WindowHeight);
        set => Update(_model with
        {
            WindowLeft = value.Left,
            WindowTop = value.Top,
            WindowWidth = value.Width,
            WindowHeight = value.Height,
        });
    }

    /// <summary>Whether the window was left maximised. Windows only. It takes priority over the frame: a
    /// maximised window's stored frame is the one it had before it was maximised.</summary>
    public bool WindowMaximized
    {
        get => _model.WindowMaximized;
        set => Update(_model with { WindowMaximized = value });
    }

    // ── the window's three views ──

    /// <summary>Which view the app was left in, by name. It is read through <see
    /// cref="SessionRestore.Enum{T}"/>. A name that no longer resolves means no view at all, and the
    /// library is where the app opens.</summary>
    public string? View
    {
        get => _model.View;
        set => Update(_model with { View = value });
    }

    // ── the left pane ──

    public bool LeftPaneOpen
    {
        get => _model.LeftPaneOpen;
        set => Update(_model with { LeftPaneOpen = value });
    }

    /// <summary>Which function the pane was showing, by name.</summary>
    public string? LeftTab
    {
        get => _model.LeftTab;
        set => Update(_model with { LeftTab = value });
    }

    /// <summary>Which way the library pane was reading the collection, by name. Whether that pane was
    /// folded is not kept. It only folds for the import workbench, and a queue does not survive the process
    /// that built it.</summary>
    public string? RightTab
    {
        get => _model.RightTab;
        set => Update(_model with { RightTab = value });
    }

    /// <summary>The star split between the pane and the library, as the splitter left it. Import keeps its
    /// own pixel width in the settings. That is a choice about a working view, not a session
    /// note.</summary>
    public (double Left, double Right) Split
    {
        get => (_model.LeftStars, _model.RightStars);
        set => Update(_model with { LeftStars = value.Left, RightStars = value.Right });
    }

    // ── the library panel ──

    /// <summary>What was in the search box.</summary>
    public string LibrarySearch
    {
        get => _model.LibrarySearch;
        set => Update(_model with { LibrarySearch = value ?? "" });
    }

    /// <summary>Whether the shelf was narrowed to the favourites.</summary>
    public bool FavoritesOnly
    {
        get => _model.FavoritesOnly;
        set => Update(_model with { FavoritesOnly = value });
    }


    /// <summary>How far down each of the three views was, kept per view. They are three ways of reading one
    /// collection, and the offset of a wall means nothing to a table of tracks.</summary>
    public double WallScroll
    {
        get => _model.WallScroll;
        set => Update(_model with { WallScroll = value });
    }

    public double ListScroll
    {
        get => _model.ListScroll;
        set => Update(_model with { ListScroll = value });
    }

    public double TableScroll
    {
        get => _model.TableScroll;
        set => Update(_model with { TableScroll = value });
    }

    // ── the data table ──

    /// <summary>The column the table was sorted on, and which way. The column is the mirror's own column
    /// name, which is what the grid keys its sort by. Null means the table's default order.</summary>
    public (string? Column, bool Descending) TableSort
    {
        get => (_model.TableSortColumn, _model.TableSortDescending);
        set => Update(_model with { TableSortColumn = value.Column, TableSortDescending = value.Descending });
    }

    /// <summary>The width that each column was dragged to, by column name. A column that is not in the map
    /// keeps the width the markup gives it. That is what a column added since the file was written
    /// gets.</summary>
    public IReadOnlyDictionary<string, double> TableColumnWidths
    {
        get => _model.TableColumnWidths ?? new Dictionary<string, double>();
        set => Update(_model with { TableColumnWidths = value });
    }

    /// <summary>The columns taken off the table, by column name. A column that is not in the list is shown.
    /// That is what a column added since the file was written gets.</summary>
    public IReadOnlyList<string> TableHiddenColumns
    {
        get => _model.TableHiddenColumns ?? [];
        set => Update(_model with { TableHiddenColumns = value });
    }

    // ── the queue ──

    /// <summary>
    /// The queue that was waiting, as library-relative paths, and where the needle was in it.
    ///
    /// <para>The paths are relative to the library, not absolute. An offloaded track has no file on this
    /// disk, so it would fail every existence check. The mirror is what can still answer for it. A library
    /// that has been pointed somewhere else gives paths that resolve to nothing. That is the right answer,
    /// because those records are not here.</para>
    ///
    /// <para>The playing track is stored by path as well as by position, because the queue that comes back
    /// may be shorter than the one that was saved. See <see cref="SessionRestore.Cursor"/>.</para>
    /// </summary>
    public IReadOnlyList<string> Queue
    {
        get => _model.Queue ?? [];
        set => Update(_model with { Queue = value });
    }

    public int QueueIndex
    {
        get => _model.QueueIndex;
        set => Update(_model with { QueueIndex = value });
    }

    public string? QueueTrack
    {
        get => _model.QueueTrack;
        set => Update(_model with { QueueTrack = value });
    }

    /// <summary>How far into the playing track the needle was, in seconds.</summary>
    public double QueueSeconds
    {
        get => _model.QueueSeconds;
        set => Update(_model with { QueueSeconds = value });
    }

    /// <summary>Saves the queue and the needle together. They are one fact. Writing them one property at a
    /// time would leave a file on disk with a cursor into a queue that it does not hold.</summary>
    public void SetQueue(IReadOnlyList<string> queue, int index, string? track, double seconds)
        => Update(_model with
        {
            Queue = queue,
            QueueIndex = index,
            QueueTrack = track,
            QueueSeconds = seconds,
        });

    public bool Shuffle
    {
        get => _model.Shuffle;
        set => Update(_model with { Shuffle = value });
    }

    public bool Repeat
    {
        get => _model.Repeat;
        set => Update(_model with { Repeat = value });
    }

    private void Update(Model model)
    {
        _model = model;
        if (_disposed)
            return;

        // A quiet moment writes the file. A stream of changes that never has a quiet moment, such as the
        // needle moving through a record or a fling down a wall of covers, is written anyway once the
        // oldest unsaved change is MaxDelay old. Without that limit, each change would push the deadline
        // further out, and nothing would reach the disk until the app closed.
        var since = _dirtySince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - since >= MaxDelay)
        {
            Write();
            return;
        }

        try { _write.Change(SaveDelay, Timeout.InfiniteTimeSpan); } catch { }
    }

    private void Write()
    {
        _dirtySince = null;
        if (!_mayWrite)
            return;
        try { File.WriteAllText(StorePath, JsonSerializer.Serialize(_model, SessionServiceJson.Default.Model)); } catch { }
    }

    /// <summary>Writes now. The window is closing, and the last second of a session is the one that says
    /// where it was left.</summary>
    public void Flush()
    {
        try { _write.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch { }
        Write();
    }

    /// <summary>Reads the stored session at startup. A file that does not parse is moved aside, and the app
    /// opens on its defaults. This happens for a file that was only half written when power was lost, or
    /// that was written by a version that stored something else. A file that could not be read this time is
    /// left alone, and it is not written over. See RPM.Core.Files.StoreFile.</summary>
    public void Load()
    {
        var read = RPM.Core.Files.StoreFile.Load(
            StorePath, text => JsonSerializer.Deserialize(text, SessionServiceJson.Default.Model));
        _model = read.Value ?? new Model();
        _mayWrite = read.MayWrite;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Flush();
        _write.Dispose();
    }
    /// <summary>The serializer for the session file. A published build is Native AOT, so it serializes
    /// through this generated code and never by reflection.</summary>
    [JsonSerializable(typeof(Model))]
    private sealed partial class SessionServiceJson : JsonSerializerContext;

}
