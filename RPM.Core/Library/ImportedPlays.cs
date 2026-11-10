namespace RPM.Core.Library;

/// <summary>
/// Sends the play counts that imports from other apps brought in, one device per source library.
///
/// <para>An import of 20,000 tracks writes 20,000 rows at once. They go out in one sync pass, in requests
/// of <see cref="PerRequest"/> (the store's limit), and not one request per pass, which would take a
/// hundred passes to reach the phone.</para>
/// </summary>
public static class ImportedPlays
{
    /// <summary>The store's limit of rows per request.</summary>
    public const int PerRequest = 200;

    /// <summary>The most requests in one pass, so a pass stays short whatever was imported. 20,000 rows.</summary>
    private const int MaxRequests = 100;

    /// <summary>Sends until nothing is left, a request fails, or <see cref="MaxRequests"/> were sent; the
    /// next pass sends the rest. <paramref name="send"/> sends one device's rows and says whether they
    /// arrived; only rows that arrived are marked as sent. Returns how many rows were sent.</summary>
    public static async Task<int> SendAllAsync(
        LibraryDatabase db, Func<string, IReadOnlyList<LibraryDatabase.TrackPlayRow>, Task<bool>> send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(send);
        var sent = 0;
        for (var requests = 0; requests < MaxRequests && !ct.IsCancellationRequested;)
        {
            var due = db.ImportPlaysToSend(PerRequest);
            if (due.Count == 0)
                break;
            foreach (var device in due.GroupBy(r => r.Device))
            {
                IReadOnlyList<LibraryDatabase.TrackPlayRow> rows = [.. device];
                requests++;
                if (!await send(device.Key, rows))
                    return sent;
                db.NotePlaysSent(device.Key, rows);
                sent += rows.Count;
            }
        }
        return sent;
    }
}
