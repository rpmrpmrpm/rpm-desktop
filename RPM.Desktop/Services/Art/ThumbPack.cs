using System.Security.Cryptography;
using System.Text;
using Windows.UI;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// The whole collection's resident artwork in ONE file. The wall's first screen then costs one sequential
/// read, not a few hundred separate reads.
///
/// <para><see cref="ThumbCache"/> keeps one thumb per record per size. For a few records that is the right
/// design. The resident tier is different. Those pictures are wanted ALL AT ONCE, at app start, for every
/// record in the collection. A thousand records means a thousand directory lookups and a thousand file opens.
/// Each file is only a few kilobytes. The disk spends its time seeking rather than reading. On a cold cache,
/// that is most of the wait before a wall of covers appears.</para>
///
/// <para>Packed, the same thousand pictures are one open and one read of a few megabytes, in order. The tint is
/// stored with each picture. This matters more than its size suggests. Reading a cover's colour means decoding
/// the cover. Without the stored tint, every record would need decoding on the first pass over a new library.</para>
///
/// <para>Derived data throughout. A pack that cannot be read, or that was written by an older version, is ignored
/// and rebuilt from <see cref="ThumbCache"/>. There is nothing to invalidate, because nothing here is the only
/// copy of anything.</para>
/// </summary>
public static class ThumbPack
{
    /// <summary>One record's resident picture: the encoded bytes, and the colour read off them.</summary>
    public readonly record struct Entry(byte[] Bytes, Color? Tint);

    /// <summary>Increase this when the layout below changes, or when the pictures in it are cut differently. The
    /// pack holds encoded bytes. So an improvement to the cutter has no effect until the pack is thrown away (see
    /// ThumbCache.Cutter). An older pack is not read. It is replaced. Its pictures can all be derived again, so
    /// converting them is not worth it.</summary>
    private const int Version = 3;

    private const string Magic = "RPMPACK";

    /// <param name="scope">Which COLLECTION this pack is for. See <see cref="ScopeFor"/>.</param>
    private static string PathFor(int edge, string? scope) => Path.Combine(
        LocalData.Dir, "thumbs",
        string.IsNullOrEmpty(scope) ? $"resident-{edge}.pack" : $"resident-{scope}-{edge}.pack");

    /// <summary>
    /// The pack name for the collection at <paramref name="root"/>.
    ///
    /// <para>The pack is keyed by the collection, not by the wall that draws it. So the two walls can share one
    /// pack. They usually show the same records. The window's shelf and the visualizer's both show the user's own
    /// library, unless a borrowed one is open. If each wall had its own pack, each would pay the full first-run
    /// cost. Opening the collection over the room would then be slow, even on a machine that had just drawn every
    /// one of those covers. One root gives one file and one cost. A borrowed shelf has a different root, so it gets
    /// a pack of its own. That is the purpose of the split. A friend's folder must never answer for a folder of
    /// the same name at home.</para>
    /// </summary>
    public static string ScopeFor(string? root) =>
        string.IsNullOrEmpty(root)
            ? ""
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..12];

    /// <summary>
    /// Every record in the pack for this edge, or an empty map if there is no pack worth reading.
    /// Synchronous, and a single read. Call it off the UI thread.
    /// </summary>
    public static Dictionary<string, Entry> Load(int edge, string? scope = null)
    {
        var found = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = PathFor(edge, scope);
            if (!File.Exists(path))
                return found;

            // Read the whole file at once, not as a stream. The point of the pack is ONE read. Holding a few
            // megabytes for the length of this call costs less than the seeks it saves.
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadString() != Magic || reader.ReadInt32() != Version || reader.ReadInt32() != edge)
                return found;   // Not this app's file, or an older layout. It is rebuilt, not parsed.

            var count = reader.ReadInt32();
            if (count < 0)
                return found;

            var folders = new string[count];
            var tints = new int[count];
            var lengths = new int[count];
            for (var i = 0; i < count; i++)
            {
                folders[i] = reader.ReadString();
                tints[i] = reader.ReadInt32();
                lengths[i] = reader.ReadInt32();
            }

            for (var i = 0; i < count; i++)
            {
                var bytes = reader.ReadBytes(lengths[i]);
                if (bytes.Length != lengths[i])
                    break;   // Truncated. Keep what was whole, and let the rest be derived again.
                found[folders[i]] = new Entry(bytes, Untint(tints[i]));
            }
        }
        catch
        {
            found.Clear();   // Unreadable is the same as absent.
        }
        return found;
    }

    /// <summary>
    /// Writes the pack. It is written to a temporary file and then renamed. A crash in the middle of a write
    /// leaves the previous pack intact. It never leaves a half-written pack that would be read as correct.
    /// Synchronous, so call it off the UI thread.
    /// </summary>
    public static void Save(int edge, IReadOnlyDictionary<string, Entry> entries, string? scope = null)
    {
        try
        {
            var path = PathFor(edge, scope);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";

            using (var stream = File.Create(tmp))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(edge);
                writer.Write(entries.Count);

                // The index is written first, as a whole, and then the pictures. The reader needs to know what is
                // in the file before it reads any of it. An index spread between the pictures would mean seeking
                // through the file that exists to avoid seeks.
                foreach (var (folder, entry) in entries)
                {
                    writer.Write(folder);
                    writer.Write(Tint(entry.Tint));
                    writer.Write(entry.Bytes.Length);
                }
                foreach (var entry in entries.Values)
                    writer.Write(entry.Bytes);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // A pack that cannot be written makes the next start slower. It is not a fault.
        }
    }

    /// <summary>
    /// Deletes this collection's packs for every edge except the ones still in use.
    ///
    /// <para>A pack is written per edge. So when a wall changes the size of its pictures, it starts reading a
    /// different file. It leaves the old file on disk, whole, for a size that nothing asks for any more. One pack
    /// for a large collection is tens of megabytes. The edges a wall passes through are steps, not a smooth range.
    /// Left alone, the old packs build up, one for each step the window has been resized across.</para>
    ///
    /// <para><paramref name="keep"/> lists every edge that some wall is still reading. That includes BOTH walls,
    /// not just the caller's. The two walls share a pack when they show the same collection. A wall that pruned
    /// only its own edge would delete the other wall's pack on every start. Synchronous, with a directory walk.
    /// Call it off the UI thread.</para>
    /// </summary>
    public static void Prune(string? scope, IReadOnlyCollection<int> keep)
    {
        try
        {
            var dir = Path.Combine(LocalData.Dir, "thumbs");
            if (!Directory.Exists(dir))
                return;

            var prefix = string.IsNullOrEmpty(scope) ? "resident-" : $"resident-{scope}-";
            foreach (var path in Directory.EnumerateFiles(dir, prefix + "*.pack"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var tail = name[prefix.Length..];

                // A scoped pack's tail is only the edge. An UNSCOPED pack's tail is everything after "resident-".
                // For a scoped file, that is "<scope>-<edge>". So a numeric tail is what tells the two kinds apart.
                // A prune of the unscoped packs must not remove scoped ones that belong to collections it was not
                // asked about.
                if (!int.TryParse(tail, out var edge) || keep.Contains(edge))
                    continue;

                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Held open by the other wall's read. It is leftover either way, and the next start tries again.
                }
            }
        }
        catch
        {
            // A directory that cannot be walked leaves disk in use. It is not a fault.
        }
    }

    // Colour as one int. Zero means "no colour was read". A fully transparent tint is not a value any cover
    // produces, so zero is safe to use as the marker.
    private static int Tint(Color? colour) =>
        colour is not { } c ? 0 : (0xFF << 24) | (c.R << 16) | (c.G << 8) | c.B;

    private static Color? Untint(int packed) =>
        packed == 0 ? null
        : Color.FromArgb(0xFF, (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
}
