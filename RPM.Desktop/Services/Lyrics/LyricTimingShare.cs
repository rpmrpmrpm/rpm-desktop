using RPM.Core.Cloud;
using RPM.Core.Lyrics;

namespace RPM.Desktop.Services.Lyrics;

/// <summary>
/// Sends the lines this machine measured (<see cref="LyricAlignment"/>) to the cloud. The TVs and
/// phones then light each word at the time the record sings it, instead of guessing.
///
/// <para>Lines are collected per track and sent a few at a time. A measured line is held. The held
/// lines are sent once <see cref="Wait"/> has passed with no new line, or at once when a line for a
/// different track arrives. Each line is sent with its index, its time and its text from the sheet it
/// was measured against. A reader whose sheet is different uses only the lines that match on both.
/// This is best effort. If the user is signed out, offline or refused, the lines are dropped. The
/// reader then keeps its own guess, as it does when this machine has not measured the lyrics.</para>
/// </summary>
public sealed class LyricTimingShare(RpmCloudClient cloud)
{
    /// <summary>How long a quiet spell lasts before the held lines are sent.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(6);

    private readonly object _gate = new();
    private string? _rpmId;
    private readonly Dictionary<int, LyricTimingLineBody> _held = [];
    private CancellationTokenSource? _timer;

    /// <summary>A line of <paramref name="sheet"/> measured for the track
    /// <paramref name="rpmId"/>.</summary>
    public void Note(string? rpmId, LyricSheet sheet, int line, IReadOnlyList<WordSpan> words)
    {
        if (string.IsNullOrWhiteSpace(rpmId) || line < 0 || line >= sheet.Lines.Count || words.Count == 0)
            return;

        var body = new LyricTimingLineBody(
            line, sheet.Lines[line].Seconds, sheet.Lines[line].Text,
            [.. words.Select(w => new LyricTimingWordBody(w.From, w.To))]);

        (string Id, LyricTimingLineBody[] Lines)? flush = null;
        lock (_gate)
        {
            if (_rpmId is not null && _rpmId != rpmId && _held.Count > 0)
                flush = (_rpmId, [.. _held.Values]);
            if (_rpmId != rpmId)
                _held.Clear();
            _rpmId = rpmId;
            _held[line] = body;

            _timer?.Cancel();
            _timer = new CancellationTokenSource();
            var token = _timer.Token;
            _ = Task.Delay(Wait, token).ContinueWith(_ => SendHeld(), token,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        if (flush is { } early)
            _ = SendAsync(early.Id, early.Lines);
    }

    private void SendHeld()
    {
        string? id;
        LyricTimingLineBody[] lines;
        lock (_gate)
        {
            id = _rpmId;
            lines = [.. _held.Values];
            _held.Clear();
        }
        if (id is not null && lines.Length > 0)
            _ = SendAsync(id, lines);
    }

    private async Task SendAsync(string rpmId, LyricTimingLineBody[] lines)
    {
        try
        {
            await cloud.PutLyricTimingAsync(rpmId, lines, CancellationToken.None);
        }
        catch
        {
            // Signed out, offline, or a server that has no such route. The readers keep their guess.
        }
    }
}
