using System.Security.Cryptography;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// The home banner's photographs as the models cut them, kept on disk between launches.
///
/// <para><b>Why.</b> The reveal waits for the banner's first two pictures. Each one needs a face detection
/// and a U²-Netp saliency run before it can be cut (<c>HomeFeatureArt.FrameAsync</c>). On an M4 Mac, each
/// card took about 0.3 s. The cards queue one after another on the saliency model's gate, and together
/// they took about 0.6 s of a 1.0 s dressing. The banner is chosen by the hour and the day, so a launch in
/// the same part of the day cuts the same photographs again. With this cache, the second time the answer
/// is a file read.</para>
///
/// <para><b>What is kept.</b> Only a cut that both models answered (<c>Framed.Settled</c>) is kept. A
/// fallback is fine to show, but it is not this machine's real answer. Writing it down would keep a worse
/// picture after the models recover. The key is the photograph's content, the plate's width in whole
/// points (the crop's obstruction geometry depends on it), and the app's version. So a new release that
/// changes the models or the crop cuts again, rather than reading an old answer.</para>
/// </summary>
public static class PlateCutCache
{
    private static readonly string Dir = Path.Combine(LocalData.Dir, "plate-cuts");

    /// <summary>A month. After that the banner has moved on to other records, so the cuts only take up
    /// disk.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static int _pruned;

    /// <summary>Where the cut of <paramref name="photo"/> is kept, for a plate <paramref name="plate"/> wide.
    /// This hashes the photograph, so call it once per card, off the UI thread.</summary>
    public static string KeyFor(byte[] photo, double plate)
        => Path.Combine(Dir,
            $"{Convert.ToHexString(SHA256.HashData(photo))[..16]}-{(int)Math.Round(plate)}{Suffix}");

    /// <summary>The kept cut, or null if there is none or it cannot be read. A failed read counts as a miss.
    /// The caller frames the photograph again, as it would anyway.</summary>
    public static byte[]? Find(string key)
    {
        try
        {
            PruneOnce();
            return File.Exists(key) ? File.ReadAllBytes(key) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Keeps a settled cut. It is written to a temporary name of its own and then moved into place.
    /// A reader never sees half a file. Two cards that keep the same photograph never write to one file.</summary>
    public static void Keep(string key, byte[] cut)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = $"{key}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, cut);
            File.Move(tmp, key, overwrite: true);
        }
        catch
        {
            // Not kept, so the next launch frames it again. Nothing on screen depends on this.
        }
    }

    /// <summary>The app version is in the file name, so cuts from another release are swept rather than read.</summary>
    private static readonly string Suffix =
        "." + string.Concat(App.VersionText.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".cut";

    private static void PruneOnce()
    {
        if (Interlocked.Exchange(ref _pruned, 1) == 1 || !Directory.Exists(Dir))
            return;
        try
        {
            var cutoff = DateTime.UtcNow - MaxAge;
            foreach (var file in Directory.EnumerateFiles(Dir))
            {
                if (!file.EndsWith(Suffix, StringComparison.Ordinal)
                    || File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* a sweep that fails leaves files for the next one */ }
    }
}
