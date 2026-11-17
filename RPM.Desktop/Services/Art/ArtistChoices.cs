using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// Which photograph each artist wears, as <c>ArtistPortraits.ChooseAsync</c> decided it, kept on disk with
/// what it was decided from.
/// </summary>
/// <remarks>
/// <para><b>Why it is written down.</b> Deciding asks the artwork service for the artist's photographs (a
/// cached answer read and parsed, or a round trip once it has aged) and then measures up to four of them,
/// each a ranged read from the host that holds it, which the web cache never keeps. Held only in memory,
/// that was done again for every artist on every launch: 50 to 220 ms an artist, measured, and most of
/// what a wall of 965 boxes spent filling itself in as the reader scrolled.</para>
///
/// <para><b>What would make it wrong, and how that is caught.</b> The user's own say over an artist's
/// photographs is the saved set in the library: hiding one, or adding one. That set is read on every
/// launch (one row, quick) and the answer is reused only while it is the same SET. The order is left out on
/// purpose: the carousel rewrites it whenever somebody stops on a picture, and the choice exists to stay put
/// while they page. A catalogue that gains a photograph is not seen here, so an answer is also kept for a
/// limited time only (<see cref="PhotoAge"/>, <see cref="NoneAge"/>), and a photograph that has gone from
/// its host is forgotten as soon as a box finds that out (<see cref="Forget"/>).</para>
///
/// <para>A sidecar file beside the app's other small stores, read once and held. A choice is written a few
/// seconds after it is made, with every other choice made in those seconds, because a wall decides
/// hundreds in a row.</para>
/// </remarks>
internal static partial class ArtistChoices
{
    /// <summary>What the stored choices were made by. Increase this when the way a photograph is chosen changes
    /// in a way that should reach artists already decided. Everything older is decided again.</summary>
    private const int Version = 2;

    /// <summary>Where the file lives. It can be set, so that tests write somewhere of their own.</summary>
    internal static string Dir { get; set; } = LocalData.Dir;

    private static string FilePath => Path.Combine(Dir, "artist-choices.json");

    private static readonly object Gate = new();

    /// <summary>One artist's choice.</summary>
    /// <param name="From">The saved photographs it was chosen with, as <see cref="FromOf"/> states them.</param>
    /// <param name="Url">The photograph chosen, or null where the artist has none.</param>
    /// <param name="At">When it was chosen, in UTC ticks.</param>
    internal sealed record Choice(string From, string? Url, long At);

    /// <summary>How long a chosen photograph is kept. A catalogue that has since gained a better one is seen
    /// at most this late.</summary>
    internal static readonly TimeSpan PhotoAge = TimeSpan.FromDays(30);

    /// <summary>How long "no photograph" is kept. Shorter, because that is the answer a catalogue is most
    /// likely to change.</summary>
    internal static readonly TimeSpan NoneAge = TimeSpan.FromDays(7);

    private sealed record Stored(int Version, Dictionary<string, Choice> Artists);

    /// <summary>The choices, once read. Null until the first question.</summary>
    private static Dictionary<string, Choice>? _held;

    /// <summary>False when the file exists and could not be read: nothing is written over it then. See
    /// RPM.Core.Files.StoreFile.</summary>
    private static bool _mayWrite = true;

    /// <summary>A write is waiting to happen.</summary>
    private static bool _writing;

    /// <summary>How long after a choice the file is written, so a wall's hundreds of choices are one write.</summary>
    private static readonly TimeSpan WriteAfter = TimeSpan.FromSeconds(3);

    /// <summary>The choice kept for this artist with this same saved set, and still young enough to use;
    /// null where there is none. A kept choice can itself be "no photograph": see
    /// <see cref="Choice.Url"/>.</summary>
    public static Choice? For(string artist, string from)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return null;

        lock (Gate)
        {
            if (!Held().TryGetValue(Key(artist), out var kept) || kept.From != from)
                return null;

            var age = DateTime.UtcNow - new DateTime(kept.At, DateTimeKind.Utc);
            return age < (kept.Url is null ? NoneAge : PhotoAge) ? kept : null;
        }
    }

    /// <summary>Writes down a choice, or that the artist has no photograph (<paramref name="url"/> null).</summary>
    public static void Keep(string artist, string from, string? url)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return;

        lock (Gate)
        {
            var held = Held();
            if (!_mayWrite)
                return;

            var key = Key(artist);
            if (held.TryGetValue(key, out var was) && was.From == from && was.Url == url)
                return;   // the same answer again: its age stays where it was

            held[key] = new Choice(from, url, DateTime.UtcNow.Ticks);
            QueueWrite();
        }
    }

    /// <summary>The kept choice is wrong: its photograph is gone from where it was. The next launch decides
    /// again.</summary>
    public static void Forget(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return;

        lock (Gate)
        {
            if (Held().Remove(Key(artist)) && _mayWrite)
                QueueWrite();
        }
    }

    /// <summary>A write a few seconds from now, if one is not already waiting. Call it with
    /// <see cref="Gate"/> held.</summary>
    private static void QueueWrite()
    {
        if (_writing)
            return;

        _writing = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(WriteAfter).ConfigureAwait(false);
            Write();
        });
    }

    /// <summary>The saved photographs a choice was made with, as one short string. As a SET: see the
    /// remarks on the class for why the order is left out.</summary>
    public static string FromOf(IEnumerable<string> saved)
    {
        var joined = string.Join('\n', saved.Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
    }

    /// <summary>Artists are matched the way the rest of the app matches them. Case and surrounding spaces do
    /// not make two artists.</summary>
    private static string Key(string artist) => artist.Trim().ToLowerInvariant();

    /// <summary>The choices, read from disk the first time. Call it with <see cref="Gate"/> held.</summary>
    private static Dictionary<string, Choice> Held()
    {
        if (_held is { } held)
            return held;

        var read = RPM.Core.Files.StoreFile.Load(
            FilePath, text => JsonSerializer.Deserialize(text, ArtistChoicesJson.Default.Stored));

        _mayWrite = read.MayWrite;
        _held = read.Value is { Version: Version, Artists: { } artists }
            ? new Dictionary<string, Choice>(artists, StringComparer.Ordinal)
            : new Dictionary<string, Choice>(StringComparer.Ordinal);
        return _held;
    }

    /// <summary>Writes the whole file, to a name of its own first and then moved into place, so a reader
    /// never sees half of it.</summary>
    private static void Write()
    {
        Stored stored;
        lock (Gate)
        {
            _writing = false;
            if (_held is null || !_mayWrite)
                return;
            stored = new Stored(Version, new Dictionary<string, Choice>(_held, StringComparer.Ordinal));
        }

        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(stored, ArtistChoicesJson.Default.Stored));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A choice that cannot be written costs a measurement on the next launch, not a picture.
        }
    }

    /// <summary>The JSON serialiser for the stored choices. A published build is Native AOT, which serialises
    /// from this and never by reflection.</summary>
    [JsonSerializable(typeof(Stored))]
    private sealed partial class ArtistChoicesJson : JsonSerializerContext;
}
