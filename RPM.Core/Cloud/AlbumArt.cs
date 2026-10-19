using System.Security.Cryptography;
using System.Text;
using RPM.Core.Import;
using RPM.Core.Metadata;

namespace RPM.Core.Cloud;

/// <summary>An album's cover as the library shows it, and where it came from.</summary>
public sealed record AlbumCover(byte[] Bytes, string Mime, string Source)
{
    public const string SourceEmbedded = "embedded";
    public const string SourceFolder = "folder";

    /// <summary>Not from this disk at all: from the computer that holds the record, or from the store. This
    /// is what a borrowed or offloaded album's sleeve comes back as.</summary>
    public const string SourceElsewhere = "elsewhere";
}

/// <summary>One album's cover in the cloud: the key it is stored under, and how to find the picture.
///
/// <para>This exists because the sidecar cannot carry it. A sidecar holds the art <em>embedded in the
/// file</em>. That is the right thing for a restore, because it records what the user's file contained.
/// Putting a folder image into it would restore a file with a picture the original never had. But the
/// library does not show only embedded art. It shows embedded art <b>or the album folder's image</b>, and
/// most ripped albums have only the folder image. So an album whose cover the desktop shows correctly
/// arrives in the cloud with no cover.
/// </para>
///
/// <para>The cover is stored once per album, not once per track. Sixteen tracks share one sleeve. Storing
/// it sixteen times to solve a display problem would be worse than the gap.
/// </para>
/// </summary>
public static class AlbumArt
{
    /// <summary>The album's key: a hash of its artist and title. It is safe as a path component whatever
    /// the album is called, including slashes, colons, emoji and right-to-left text.
    ///
    /// <para>The key is derived, not assigned. So the desktop that uploads a cover and the phone that asks
    /// for one get the same string, without either being told it. This makes the normalisation part of the
    /// contract: trimmed, lower-cased, and invariant. Changing it would orphan every stored cover, so it
    /// must not change.
    /// </para>
    /// </summary>
    /// <param name="albumArtist">The album artist where there is one, otherwise the track artist. An album
    /// belongs to its artist, not to a featured guest.</param>
    public static string KeyFor(string? albumArtist, string? album)
    {
        var normalized = $"{Normalize(albumArtist)}|{Normalize(album)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32].ToLowerInvariant();
    }

    /// <summary>The key for a track's album. The fallback is applied here, so each caller does not have to
    /// apply it: an album belongs to its album artist, and to the track artist only when there is no album
    /// artist.
    ///
    /// <para>The fallback is written here, not as <c>??</c> at the call sites. A tag that is present but
    /// blank is not an album artist. A caller that used <c>??</c> would key the album under the empty
    /// string, while one that trimmed first would key it under the artist. Two keys for one album means the
    /// sleeve is stored where nothing will look for it.
    /// </para>
    /// </summary>
    public static string KeyForTrack(string? albumArtist, string? artist, string? album)
        => KeyFor(string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist, album);

    /// <summary>The key that an album's <em>disc label</em> is stored under. The disc label is the
    /// transparent picture of the record itself. The turntable shows it in place of the drawn disc.
    ///
    /// <para>This is the same hash over the same two names, with one word ("disc") in front of them. So it
    /// is a second object in the same per-user art prefix, and it needs nothing new from the server. A key
    /// is a key, and the store only checks that it is 32 hex characters. The benefit: a label put on a
    /// record at one desk is on that record at every other desk, as the cover beside it always has
    /// been.</para>
    /// </summary>
    public static string DiscKeyFor(string? albumArtist, string? album)
    {
        var normalized = $"disc|{Normalize(albumArtist)}|{Normalize(album)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32].ToLowerInvariant();
    }

    /// <summary>The disc-label key for a track's album. It uses the same album-artist-then-artist fallback
    /// as <see cref="KeyForTrack"/>. The two pictures of one record must agree about whose record it
    /// is.</summary>
    public static string DiscKeyForTrack(string? albumArtist, string? artist, string? album)
        => DiscKeyFor(string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist, album);

    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant();

    /// <summary>The cover the library would show for a file: its embedded art, or failing that, the album
    /// folder's image.
    ///
    /// <para>This is the same rule that the art display uses. It is kept in one place on purpose, so the
    /// two cannot drift apart. A cloud cover that disagreed with the one on screen would be worse than
    /// none.
    /// </para>
    /// </summary>
    public static AlbumCover? ForFile(string filePath)
    {
        try
        {
            // Normalised here, not passed through. What a file's tag calls its picture is the tagger's
            // choice, and the store only signs for three types.
            if (TagReader.LoadArt(filePath) is { } embedded)
                return new AlbumCover(embedded.Bytes, NormalizeMime(embedded.Mime), AlbumCover.SourceEmbedded);

            return Path.GetDirectoryName(filePath) is { } folder ? ForFolder(folder) : null;
        }
        catch
        {
            return null;   // a cover that cannot be read is no cover, never an error
        }
    }

    /// <summary>The cover that an album folder keeps beside its tracks. This is the second half of
    /// <see cref="ForFile"/>, on its own.
    ///
    /// <para>A record whose bytes are all in the cloud has no file to read art from. It can still have a
    /// picture. The editor writes the sleeve it was given into the album's folder, and this machine shows
    /// the record with that file. Without a way to read it, a cover put on an offloaded album stayed on
    /// this desk and never reached the phone.</para>
    /// </summary>
    public static AlbumCover? ForFolder(string folder)
    {
        try
        {
            if (FolderArtWriter.FindFolderImage(folder) is not { } image)
                return null;

            var bytes = File.ReadAllBytes(image);
            return bytes.Length == 0 ? null : new AlbumCover(bytes, MimeFor(image), AlbumCover.SourceFolder);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The disc label that an album folder keeps (disc.png), or null. A disc label is only ever in
    /// a folder, because the picture of a record is never a tag. So, unlike the cover, there is no second
    /// place to look.</summary>
    public static AlbumCover? DiscForFolder(string folder)
    {
        try
        {
            if (FolderArtWriter.FindDiscImage(folder) is not { } image)
                return null;

            var bytes = File.ReadAllBytes(image);
            return bytes.Length == 0 ? null : new AlbumCover(bytes, MimeFor(image), AlbumCover.SourceFolder);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>What a picture actually is, read from its first bytes rather than from the name of the
    /// file.
    ///
    /// <para>A cover picked in the editor is written to the folder as <c>cover.jpg</c>, whatever its real
    /// type is. Every decoder in the app detects the content. But the type is part of the presigned
    /// signature that an upload is signed with. So the type that the store is told must be the true one. A
    /// disc label is the case that matters. It is a transparent PNG. If it were called a JPEG, it would
    /// come back with its transparency removed.</para>
    /// </summary>
    public static string SniffMime(byte[] bytes) => bytes switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..]
            => "image/webp",
        _ => "image/jpeg",
    };

    /// <summary>A larger file is a scan, not a sleeve, and the phone would decode it down anyway. This
    /// limit stops a 40 MB TIFF from becoming an upload for each album.</summary>
    public const int MaxCoverBytes = 8 * 1024 * 1024;

    /// <summary>The content type a cover is stored under, reduced to the three that the store accepts.
    ///
    /// <para>This must be applied before the upload is requested, because the type is part of the presigned
    /// signature. The server allow-lists the types. It will not sign for whatever a caller sends. Otherwise
    /// a client could store <c>text/html</c> under its own prefix and be handed a signed URL that serves
    /// it. So anything outside the list comes back signed as <c>image/jpeg</c>. If the original type were
    /// sent in the PUT after that, the header would not match what was signed, and S3 would refuse the
    /// whole upload with a 403.
    /// </para>
    ///
    /// <para>This happens in practice. An ID3 APIC frame written by many taggers says <c>image/jpg</c>, not
    /// <c>image/jpeg</c>. Those albums reached the cloud with no sleeve at all. The only trace was an
    /// exception that nobody saw.
    /// </para>
    ///
    /// <para>This mirrors the allow-list in <c>CloudController.AlbumArtUpload</c>. The two are in
    /// different assemblies and cannot share the code, so they are tested from both ends instead.
    /// </para>
    /// </summary>
    public static string NormalizeMime(string? mime) => mime?.Trim().ToLowerInvariant() switch
    {
        "image/png" => "image/png",
        "image/webp" => "image/webp",
        _ => "image/jpeg",
    };

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
}
