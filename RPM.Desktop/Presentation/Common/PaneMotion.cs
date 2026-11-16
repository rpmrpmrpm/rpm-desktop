namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Whether a pane is moving, for work that can wait until it has landed.
/// </summary>
/// <remarks>
/// A pane move lays the library out again on every frame, and it needs every frame it can get. Work that
/// arrives during a move and is not part of it (an answer from the network that builds a column of cards,
/// for example) is best done the moment the move is over. The pane conductor sets this for the length of
/// every pane move and view switch. UI thread only.
/// </remarks>
public static class PaneMotion
{
    /// <summary>True while a pane is moving.</summary>
    public static bool Moving => _holds > 0;

    /// <summary>
    /// Completes at once when no pane is moving, otherwise when the move has landed.
    /// </summary>
    public static Task SettledAsync()
    {
        if (!Moving)
            return Task.CompletedTask;
        _settled ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _settled.Task;
    }

    /// <summary>A move starts (<paramref name="on"/> true) or lands (false). Called in pairs by the pane
    /// conductor.</summary>
    internal static void Hold(bool on)
    {
        _holds = Math.Max(0, _holds + (on ? 1 : -1));
        if (_holds > 0)
            return;
        var settled = _settled;
        _settled = null;
        settled?.TrySetResult();
    }

    private static int _holds;
    private static TaskCompletionSource? _settled;
}
