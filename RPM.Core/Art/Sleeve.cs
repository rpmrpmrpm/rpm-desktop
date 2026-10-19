namespace RPM.Core.Art;

/// <summary>A fetched picture. It holds the bytes, not a URL, so the work that runs off the UI thread
/// never needs to make a network call.</summary>
public sealed record AlbumArt(byte[] Bytes, string Mime);

/// <summary>
/// One release's cover at the two sizes the app keeps it in.
///
/// <para><see cref="Folder"/> is the archive's own scan. It is written to the album folder as
/// <c>cover.jpg</c> and never fetched again. <see cref="Embedded"/> is the copy that goes inside every
/// track file, scaled to <see cref="EmbeddedArt.MaxEdge"/> (see there for why the two differ).</para>
///
/// <para>They are often the same object: a cover that already fits needs no second copy. Having both
/// properties means code that uses a sleeve never has to check which case it is.</para>
/// </summary>
public sealed record Sleeve(AlbumArt Folder, AlbumArt Embedded)
{
    /// <summary>The same picture at both sizes. Use it for a cover that is already small (scaling
    /// could only lose quality) and for a source that gives only one size.</summary>
    public static Sleeve Of(AlbumArt art) => new(art, art);
}
