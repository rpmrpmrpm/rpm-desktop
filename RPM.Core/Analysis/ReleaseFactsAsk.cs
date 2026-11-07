using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Net;

namespace RPM.Core.Analysis;

/// <summary>
/// Asks a catalogue what kind of music one linked release is, and writes the answer against the release.
///
/// <para><b>Only for a catalogue with one list of words.</b> The app's own catalogue gives a single list ranked
/// by its voters, so the list is passed as both the genres and the styles, which is what
/// <see cref="LibraryDatabase.SetReleaseMeta"/> asks of such a provider. Discogs keeps the two apart and has a
/// pass of its own.</para>
///
/// <para><b>Nothing is written when nobody answered.</b> A catalogue that could not be reached says nothing
/// about the record, and writing an empty answer would stop the release ever being asked again. A catalogue
/// that answered and has no words for it, or has no such release, has said something, and that row is what
/// stops the same release being asked on every pass.</para>
/// </summary>
public static class ReleaseFactsAsk
{
    /// <summary>Asks about one album and writes what came back.</summary>
    /// <returns>Whether the catalogue answered. It is not the same as whether the release named any
    /// words.</returns>
    public static async Task<bool> AskAsync(
        LibraryDatabase db,
        IMetadataProvider catalogue,
        LibraryDatabase.ReleaseToAsk album,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(album);

        if (!(await catalogue.GetReleaseAsync(album.ReleaseId, ct)).Answers(out var release, out _))
            return false;

        var words = WordsOf(release);
        db.SetReleaseMeta(album.Provider, album.ReleaseId, words, words);
        return true;
    }

    /// <summary>The catalogue's words for a release: the voted list, most votes first, or the pair an older
    /// server sends in its place.</summary>
    private static IReadOnlyList<string> WordsOf(ReleaseDetail? release)
    {
        if (release is null)
            return [];
        if (release.Genres is { Count: > 0 } voted)
            return voted;

        return [.. new[] { release.Subgenre, release.Genre }.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!)];
    }
}
