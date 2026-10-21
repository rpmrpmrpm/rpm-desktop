using RPM.Core.Cloud;
using RPM.Core.Lyrics;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Services.Lyrics;

/// <summary>
/// Sends the lyric sheet the user chose on this machine ("try another", or their own file) to the cloud,
/// so the TVs and phones show the same sheet instead of the one LRCLIB gives first.
///
/// <para>Only for a record in the user's own collection that has a cloud id. The cloud keeps the sheet
/// under the user's own account, and a reader finds it by the owner of the record. A sheet chosen for a
/// borrowed record would be kept where no reader looks for it.</para>
///
/// <para>This is best effort. If the user is signed out, offline or refused, the sheet stays on this
/// machine only, as it does for a record that was never uploaded.</para>
/// </summary>
public sealed class LyricSheetShare(RpmCloudClient cloud, LibraryService library, ILogger<LyricSheetShare> log)
{
    /// <summary>The sheet <paramref name="sheet"/> was chosen for the file at <paramref name="path"/>.
    /// Returns at once; the lookup of the record and the send run in the background.</summary>
    public void Chosen(string? path, LyricSheet sheet)
    {
        if (string.IsNullOrWhiteSpace(path) || !sheet.IsSynced || sheet.Source is not { Length: > 0 } lrc)
            return;

        _ = Task.Run(() => SendAsync(path, lrc));
    }

    private async Task SendAsync(string path, string lrc)
    {
        try
        {
            if (TrackHome.For(path) is not { } home
                || !string.Equals(home.Root, library.Root, StringComparison.Ordinal)
                || TrackHome.RowIn(home, path)?.RpmId is not { Length: > 0 } rpmId)
                return;

            await cloud.PutLyricSheetAsync(rpmId, lrc, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Signed out, offline, or a server that has no such route. The readers keep LRCLIB's sheet.
            log.LogDebug(ex, "[Cloud] Could not send the chosen lyric sheet");
        }
    }
}
