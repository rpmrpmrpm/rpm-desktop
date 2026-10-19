using Amazon.S3;
using Amazon.S3.Model;
using SkiaSharp;

namespace StationLogos;

/// <summary>
/// Converts a fetched picture into the one copy the app needs, and uploads it to the bucket.
///
/// <para><b>Nothing is kept on this machine.</b> The bytes are fetched, re-encoded and uploaded in
/// one pass and then released. Saving them to disk first needs a lot of space: the chosen pictures
/// average 108KB and there are about 21,000 of them, which is about 2.3GB.</para>
///
/// <para><b>Re-encoded, not stored as fetched.</b> The average file is 108KB, some are up to eight
/// megabytes, and a phone should not download a multi-megabyte PNG to draw a 51-point row. (The
/// <c>Content-Length</c> of the 2KB RANGED probes used for measuring is not the file size, so do not
/// estimate sizes from it.) At 512px WebP the same catalogue is about 590MB and every large file is
/// limited.</para>
/// </summary>
public sealed class Bucket(IAmazonS3 s3, string name, bool pretend)
{
    /// <summary>The largest size the app draws a logo at: <c>RadioModel.SquareLogoEdge</c>, the
    /// now playing square. Anything larger is detail nobody sees. Anything smaller is not changed,
    /// because scaling a 64px favicon up to 512 makes the file bigger and the picture no better.
    /// </summary>
    public const int Edge = 512;

    /// <summary>
    /// Re-encodes and uploads, and returns the result.
    /// </summary>
    /// <returns>The stored size in bytes, or null when the bytes are not a picture Skia can read.</returns>
    /// <param name="drawing">An SVG, which is stored exactly as fetched. There is no rasteriser on
    /// this path, on purpose: a drawing has a transparent background, never blurs, is a few
    /// kilobytes, and the app draws it at any size. Rendering it to a 512px webp would lose all of
    /// that.</param>
    public async Task<long?> PutAsync(string key, byte[] body, bool drawing, CancellationToken ct)
    {
        var made = drawing ? body : Reshape(body);
        if (made is null || made.Length == 0)
            return null;

        if (pretend)
            return made.Length;

        using var stream = new MemoryStream(made);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = name,
            Key = key,
            InputStream = stream,
            ContentType = drawing ? "image/svg+xml" : "image/webp",
            // Thirty days, the same as the app's own picture cache. Stations rarely change their
            // logo.
            Headers = { CacheControl = "public, max-age=2592000" },
            DisablePayloadSigning = true,
        }, ct);

        return made.Length;
    }

    /// <summary>
    /// One picture, no larger than <see cref="Edge"/> on its long side, as WebP.
    ///
    /// <para>Its own proportions are kept. A logo is not square just because the places that draw it
    /// are, and stretching a wide wordmark into a square looks worse than the letterboxing the app
    /// already does.</para>
    ///
    /// <para>Lossless when there is transparency, lossy at quality 82 when there is not. A mark on a
    /// transparent background is flat colour, and lossless is both smaller and exact for it. A
    /// photographic logo compresses much better lossy, and at 82 the difference is not visible at
    /// this size.</para>
    /// </summary>
    private static byte[]? Reshape(byte[] body)
    {
        try
        {
            using var original = SKBitmap.Decode(body);
            if (original is null || original.Width <= 0 || original.Height <= 0)
                return null;

            var scale = Math.Min(1.0, (double)Edge / Math.Max(original.Width, original.Height));
            var wide = Math.Max(1, (int)Math.Round(original.Width * scale));
            var tall = Math.Max(1, (int)Math.Round(original.Height * scale));

            using var fitted = scale < 1.0
                ? original.Resize(new SKImageInfo(wide, tall), ResizeQuality)
                : original;

            if (fitted is null)
                return null;

            var clear = HasAlpha(fitted);
            using var image = SKImage.FromBitmap(fitted);
            using var data = image.Encode(
                clear ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Webp,
                clear ? 100 : 82);

            return data?.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies one stored object to a second key, inside S3.
    ///
    /// <para>The store is keyed by the PICTURE, so about 43,000 stations share about 23,000 objects.
    /// The API is keyed by the STATION, so it can answer <c>/radio/logo/{uuid}</c> with one S3 read
    /// and no lookup table that must be created, filled and kept in sync with the bucket. This
    /// converts one layout into the other with a server-side copy: no station's server is contacted,
    /// and the bytes are not downloaded to this machine.</para>
    ///
    /// <para>The duplication is intended and cheap. Both layouts together are about 1.5GB, which
    /// costs very little per month, compared with a DynamoDB table that could get out of sync with
    /// the bucket.</para>
    /// </summary>
    public async Task CopyAsync(string from, string to, CancellationToken ct)
    {
        if (pretend || string.Equals(from, to, StringComparison.Ordinal))
            return;

        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = name,
            SourceKey = from,
            DestinationBucket = name,
            DestinationKey = to,
            // The content type is copied with the object. Do not set it here: forcing webp would
            // label every stored SVG as webp.
            MetadataDirective = S3MetadataDirective.COPY,
        }, ct);
    }

    /// <summary>
    /// Whether these bytes are a mark on a transparent background, not a tile with rounded corners.
    ///
    /// <para>The same test as <c>TransparentMark.StandsAlone</c> in the app, done the same way,
    /// because the result decides whether a background is drawn behind the picture, and the two must
    /// agree; change both. "Has an alpha channel" is NOT the test: an android-chrome icon has one and
    /// is still opaque, so testing for alpha would choose home-screen tiles over a station's real
    /// mark.</para>
    ///
    /// <para>A mark is transparent all around its outside; a rounded tile is transparent only at its
    /// corners, about a tenth of its border. There are no cases in between.</para>
    /// </summary>
    public static bool? StandsAlone(byte[] body)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(body);
            if (bitmap is null || bitmap.Width < 4 || bitmap.Height < 4)
                return null;

            if (bitmap.AlphaType == SKAlphaType.Opaque)
                return false;

            // Inset by a pixel: the outermost row of a resampled picture is often partly
            // transparent, and counting it would find transparency on a picture that has none.
            int left = 1, top = 1, right = bitmap.Width - 2, bottom = bitmap.Height - 2;
            int seen = 0, empty = 0;

            for (var i = 0; i < 64; i++)
            {
                var t = i / 64.0;
                foreach (var (x, y) in new[]
                {
                    ((int)(left + t * (right - left)), top),
                    ((int)(left + t * (right - left)), bottom),
                    (left,  (int)(top + t * (bottom - top))),
                    (right, (int)(top + t * (bottom - top))),
                })
                {
                    seen++;
                    if (bitmap.GetPixel(x, y).Alpha < 24)
                        empty++;
                }
            }

            return seen > 0 && (double)empty / seen >= 0.5;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The station uuids that have an object under a prefix. Used to find the logos that a
    /// person chose manually, which the crawl must not overwrite.</summary>
    public async Task<HashSet<string>> KeysUnderAsync(string prefix, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? more = null;

        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = name,
                Prefix = prefix,
                ContinuationToken = more,
                MaxKeys = 1000,
            }, ct);

            foreach (var row in page.S3Objects ?? [])
            {
                var station = row.Key[prefix.Length..];
                if (station.Length > 0)
                    found.Add(station);
            }

            more = (page.IsTruncated ?? false) ? page.NextContinuationToken : null;
        }
        while (more is not null);

        return found;
    }

    /// <summary>Mitchell, not the default. A logo has sharp edges and flat colour, and nearest or
    /// bilinear scaling from 1024 to 512 gives jagged lettering.</summary>
    private static readonly SKSamplingOptions ResizeQuality = new(SKCubicResampler.Mitchell);

    /// <summary>Whether any pixel is transparent. Decides lossless or lossy, and it is checked on the
    /// RESIZED copy because that is the copy being encoded.</summary>
    private static bool HasAlpha(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque)
            return false;

        // A sparse sample, not every pixel: a logo with transparency has it around the outside, so a
        // grid of a few hundred reads finds it, and this runs about 21,000 times.
        var stepX = Math.Max(1, bitmap.Width / 24);
        var stepY = Math.Max(1, bitmap.Height / 24);
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                if (bitmap.GetPixel(x, y).Alpha < 250)
                    return true;
            }
        }

        return false;
    }
}
