using System.Globalization;
using RPM.Core.Library;

namespace RPM.Core.Cloud;

/// <summary>
/// Takes the listener's other devices' listens into a library's log, a page at a time (<c>GET cloud/listens</c>).
///
/// <para><b>A listing is read to its end, over as many passes as it takes.</b> One pass reads at most
/// <c>pagesPerPass</c> pages. Where it stops, the stamp it asked from and the token of the next page are kept, and the
/// next pass carries on from that page. It never starts the listing again, so a long history from another computer
/// is taken in a few passes, however quickly it was uploaded.</para>
///
/// <para><b>The next listing starts from what the store says is complete.</b> A batch is stamped when the store starts
/// writing it, and a large batch, or the store's index, can take a while to show all its rows. So a row stamped
/// before the newest one read may become readable only later. Each listing's first page says up to which stamp every
/// row was readable then (<see cref="ListensPage.SafeUntil"/>, the store's clock less a margin). This listing read
/// every row from its start onwards that was readable, so every row stamped before that point is in, and the next
/// listing asks from there. Rows read twice cost a little reading, and the listen ids make them harmless
/// (<see cref="LibraryDatabase.AcceptListens"/>).</para>
/// </summary>
public static class ListensPull
{
    /// <summary>Reads pages of listens from the store and takes them into <paramref name="db"/>.</summary>
    /// <param name="mine">This library's device id. Its own listens come back and are skipped.</param>
    /// <param name="read">One page: the stamp to ask from and the token of the page, or null for the first.</param>
    /// <param name="pagesPerPass">The most pages one pass reads.</param>
    /// <returns>How many listens were new.</returns>
    public static async Task<int> RunAsync(
        LibraryDatabase db, string mine, Func<long, string?, CancellationToken, Task<ListensPage>> read,
        int pagesPerPass, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);

        var state = State.Read(db.GetMeta(LibraryDatabase.ListensCursorKey));
        var taken = 0;

        for (int pages = 0; pages < pagesPerPass; pages++)
        {
            var found = await read(state.Since, state.Page, ct);
            taken += db.AcceptListens(mine, found.Listens);

            // The first page of a listing says what is complete. A store that does not say (zero) is taken at its
            // newest stamp.
            var safe = state.Page is null ? found.SafeUntil : state.Safe;
            var newest = Math.Max(state.Newest, found.Newest);

            state = found.Next is { } next
                ? state with { Page = next, Safe = safe, Newest = newest }
                : new State(Math.Max(state.Since, safe > 0 ? safe : newest), null, 0, 0);

            // Kept after every page, so a pass that stops (an error, the app closing) carries on from here.
            db.SetMeta(LibraryDatabase.ListensCursorKey, state.Write());

            if (state.Page is null)
                break;
        }

        return taken;
    }

    /// <summary>Where the reading is: the stamp the listing asks from, the token of its next page (null between
    /// listings), what its first page said is complete, and the newest stamp it has read.</summary>
    private sealed record State(long Since, string? Page, long Safe, long Newest)
    {
        public static State Read(string? stored)
        {
            var parts = (stored ?? "").Split('|');
            static long At(string[] parts, int i) =>
                parts.Length > i && long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            return new State(At(parts, 0), parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null, At(parts, 2), At(parts, 3));
        }

        /// <summary>One meta value. The token is URL-safe base64, which has no '|'.</summary>
        public string Write() => string.Join('|',
            Since.ToString(CultureInfo.InvariantCulture), Page ?? "",
            Safe.ToString(CultureInfo.InvariantCulture), Newest.ToString(CultureInfo.InvariantCulture));
    }
}
