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

        var said = Said(album.Provider, album.ReleaseId, release?.Genres, release?.Genre, release?.Subgenre);
        db.SetReleaseMeta(said.Provider, said.ReleaseId, said.Genres, said.Styles);
        return true;
    }

    /// <summary>
    /// What the catalogue said about a release, as a library keeps it: the voted list, most votes first, or the pair
    /// an older server sends in its place, given as both the genres and the styles.
    /// </summary>
    /// <remarks>The server's list builds use this too, for the albums it fills from its own copy of the catalogue's
    /// answers, so an album reads the same facts there as here.</remarks>
    /// <param name="voted">Every word the catalogue's voters gave, most votes first. Null or empty from an older
    /// server.</param>
    /// <param name="genre">The single most voted word, from an older server.</param>
    /// <param name="subgenre">The narrower word beside it, from an older server.</param>
    public static CatalogueRelease Said(
        string provider, string releaseId, IReadOnlyList<string>? voted, string? genre, string? subgenre)
    {
        IReadOnlyList<string> words = voted is { Count: > 0 }
            ? voted
            : [.. new[] { subgenre, genre }.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!)];
        return new CatalogueRelease(provider, releaseId, words, words, null);
    }
}
