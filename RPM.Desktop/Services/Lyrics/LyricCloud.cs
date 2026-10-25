using RPM.Core.Cloud;
using RPM.Core.Lyrics;

namespace RPM.Desktop.Services.Lyrics;

/// <summary>
/// Reads back what the cloud keeps of a track's lyrics: the sheet its owner chose on a desktop, the word
/// timing that the owner's desktops and the transcoder measured off the record, and how far the sheet is
/// off the record. It also asks the transcoder to measure a track. <see cref="LyricTimingShare"/> and
/// <see cref="LyricSheetShare"/> are the sending side.
///
/// <para>Only this account's own records are read. The cloud keeps these under the record's owner, and a
/// record borrowed from somebody else's computer has no owner here to ask for.</para>
///
/// <para>Every call is best effort and quick. Signed out, offline, refused, or a server without the route:
/// each gives "none", the same answer as a track nobody has measured. The words are then shown as they
/// would be without the cloud. A read that takes longer than <see cref="Patience"/> is given up, because
/// a sheet on screen must not wait for it.</para>
/// </summary>
public sealed class LyricCloud(RpmCloudClient cloud, ILogger<LyricCloud> log)
{
    /// <summary>How long a read may take before it is given up.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    /// <summary>The sheet the owner chose for <paramref name="rpmId"/> on a desktop ("try another", or
    /// their own file), or null where they chose none or it has no times.</summary>
    public async Task<LyricSheet?> ChosenSheetAsync(string? rpmId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rpmId))
            return null;

        try
        {
            using var quick = Quick(ct);
            var lrc = await cloud.GetLyricSheetAsync(CloudShelf.Own, rpmId, quick.Token);
            return LyricSheet.Parse(lrc) is { IsSynced: true } sheet ? sheet : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "[Cloud] Could not read the chosen lyric sheet");
            return null;
        }
    }

    /// <summary>The measured lines of <paramref name="rpmId"/> and its sheet's offset, or <see
    /// cref="MeasuredAnswer.None"/>. A listener who does not support rpm is given the offset alone.</summary>
    public async Task<MeasuredAnswer> TimingAsync(string? rpmId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rpmId))
            return MeasuredAnswer.None;

        try
        {
            using var quick = Quick(ct);
            return await cloud.GetLyricTimingAsync(CloudShelf.Own, rpmId, quick.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "[Cloud] Could not read the lyric timing");
            return MeasuredAnswer.None;
        }
    }

    /// <summary>
    /// Asks the cloud to time <paramref name="sheet"/>'s words off <paramref name="rpmId"/>'s recording.
    /// The transcoder takes about half a minute for a song, and <see cref="TimingAsync"/> reads the answer
    /// later. False where the request failed or was refused (the work is for supporters).
    /// </summary>
    /// <param name="sheet">The sheet as it was looked up, not as it is shown. The lines are named by their
    /// own times, which is how every device matches them.</param>
    public async Task<bool> AskAsync(string? rpmId, LyricSheet sheet, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rpmId) || !sheet.IsSynced)
            return false;

        try
        {
            using var quick = Quick(ct);
            await cloud.AskLyricTimingAsync(
                CloudShelf.Own, rpmId,
                [.. sheet.Lines.Select((line, i) => new SheetLine(i, line.Seconds, line.Text))], quick.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "[Cloud] Could not ask for the lyrics to be timed");
            return false;
        }
    }

    private static CancellationTokenSource Quick(CancellationToken ct)
    {
        var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(Patience);
        return quick;
    }
}
