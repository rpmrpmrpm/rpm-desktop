using System.Security.Cryptography;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// The pictures the person chose themselves: an artist's artwork picked from this computer, not found by
/// the art sources.
///
/// <para>The chosen file is COPIED into the app's own folder. It is not referenced where it sits. The
/// saved order lasts beyond the session, because that artist keeps this picture from now on. A path into
/// a Pictures folder does not last. The file can be moved, renamed or tidied into an album, or it can be
/// on a card that is not in the machine. Then the header quietly shows a photograph the user never chose,
/// with no reason given. A copy is a few hundred kilobytes, and it is still the user's. Nothing here
/// touches the original.</para>
///
/// <para>The name is taken from what is IN the file, not from what the file is called. So choosing the
/// same picture twice, for one artist or for five, writes it once and gives the same address both
/// times.</para>
/// </summary>
public static class OwnArt
{
    /// <summary>Where the chosen pictures live, beside the app's other art caches. This is not a cache.
    /// Nothing clears this folder, because deleting a file here deletes artwork that a person chose.</summary>
    private static string Dir => Path.Combine(LocalData.Dir, "chosen-art");

    /// <summary>
    /// The picture formats the picker offers and this class stores. The list is set by what the crop can
    /// read, which is Skia's set. The header runs the picture through a saliency pass and a composite, not
    /// just a decoder. HEIC is left out on purpose. It is the usual format of a Mac photo library, and Skia
    /// has no reader for it. If it were offered, the file would be copied, nothing would show, and the
    /// menu item would look broken.
    /// </summary>
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into the store and returns the address to save it under. The
    /// address is a <c>file://</c> URL. That URL marks the picture as the person's own everywhere else (see
    /// <see cref="RPM.Core.Library.ArtistArtSource"/>). Returns null if the file cannot be read or is not a
    /// picture. This runs on the caller's thread. It is a file copy, so call it off the UI thread.
    /// </summary>
    public static string? Store(string sourcePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return null;

            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (!Extensions.Contains(extension))
                return null;

            var bytes = File.ReadAllBytes(sourcePath);
            if (bytes.Length == 0)
                return null;

            Directory.CreateDirectory(Dir);
            var destination = Path.Combine(Dir, Name(bytes) + extension);

            // Already stored from an earlier choice. The name is the content, so there is nothing to write.
            // The address is the same as last time.
            if (!File.Exists(destination))
                File.WriteAllBytes(destination, bytes);

            return new Uri(destination).AbsoluteUri;
        }
        catch
        {
            return null;   // An unreadable file means the choice did not happen. It is not a crash.
        }
    }

    private static string Name(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
}
