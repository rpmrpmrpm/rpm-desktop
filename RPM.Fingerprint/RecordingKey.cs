using System.Globalization;

namespace RPM.Fingerprint;

/// <summary>
/// One way of identifying a record, in a single format that all components share.
///
/// <para>
/// There is no single name for a piece of music. There is a hash of one encoding of it, a hash of
/// the decoded audio, the id in a user's own copy, and the row id in the recognition database: four
/// names for one record, each found in a different way, and each one useless to code that has only
/// one of the others. This type lets them share a table: a key is its <see cref="Kind"/> and its
/// <see cref="Value"/>, and keys of two kinds never collide, however similar their values are.
/// </para>
///
/// <para>
/// Defined here, not in either function, because both ends must write the same string. The API
/// resolves these keys, and the desktop and the transcoder produce them. A different spelling of
/// <c>canon:</c> in one of them would create keys that are never looked up, with no error: the store
/// would seem to learn nothing. <see cref="CloudLayout"/> is shared for the same reason.
/// </para>
/// </summary>
/// <param name="Kind">Which of the four this is: one of the constants on this type.</param>
/// <param name="Value">The identifier itself, already normalised to the form the kind requires.</param>
public readonly record struct RecordingKey(string Kind, string Value)
{
    /// <summary>SHA-256 of the file with tags removed (<c>RPM.Core.Cloud.CanonicalAudio</c>), and the
    /// blob key in the store. Cheap to compute: a stream copy, no decode.</summary>
    public const string CanonicalSha = "canon";

    /// <summary>SHA-256 of the decoded audio (<c>RPM.Core.Analysis.AudioKey</c>). Needs a decode, but
    /// gives the same key for two lossless encodings of one master, which the canonical hash does
    /// not.</summary>
    public const string SampleHash = "samples";

    /// <summary>The GUID in a file's own tags. It identifies one user's copy of a record, not the
    /// record itself, but it is the only name available on a machine that has no audio.</summary>
    public const string RpmId = "rpm";

    /// <summary>A row in the fingerprint database: the recognition side's id for a
    /// recording.</summary>
    public const string Song = "song";

    /// <summary>The key's text form, used everywhere: in the table, on the network, in a log.</summary>
    public override string ToString() => $"{Kind}:{Value}";

    /// <summary>A hex hash key, or null when the value is not one. Both hash kinds are lower-case hex
    /// SHA-256 and are normalised here, not trusted, because they become primary keys and one capital
    /// letter would create a second row for one record.</summary>
    private static RecordingKey? Hash(string kind, string? value)
    {
        if (value is null)
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length != 64 || !trimmed.All(Uri.IsHexDigit))
            return null;

        return new RecordingKey(kind, trimmed.ToLowerInvariant());
    }

    public static RecordingKey? Canonical(string? sha256) => Hash(CanonicalSha, sha256);

    public static RecordingKey? Samples(string? hash) => Hash(SampleHash, hash);

    /// <summary>A track's own id. Normalised through <see cref="Guid"/> so that different braces or
    /// letter case written by different libraries give the same key.</summary>
    public static RecordingKey? Rpm(string? rpmId) =>
        Guid.TryParse(rpmId, out var id) && id != Guid.Empty
            ? new RecordingKey(RpmId, id.ToString("D"))
            : null;

    public static RecordingKey? SongRow(long songId) =>
        songId == 0 ? null : new RecordingKey(Song, songId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Parses a key received from the network, and rejects anything that is not one of the
    /// four kinds in the form that kind requires. Every key from a client must pass through here
    /// before it reaches the table.</summary>
    public static RecordingKey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        int at = text.IndexOf(':');
        if (at <= 0 || at == text.Length - 1)
            return null;

        var kind = text[..at];
        var value = text[(at + 1)..];

        return kind switch
        {
            CanonicalSha => Canonical(value),
            SampleHash => Samples(value),
            RpmId => Rpm(value),
            Song => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? SongRow(id)
                : null,
            _ => null,
        };
    }

    /// <summary>
    /// Every valid key in a list, in the given order and without repeats.
    ///
    /// <para>A bundle comes from a client and is not verified. Invalid entries are dropped instead of
    /// refusing the whole bundle on purpose: a newer client that knows a fifth kind should still have
    /// its other four keys stored.</para>
    /// </summary>
    public static IReadOnlyList<RecordingKey> ParseAll(IEnumerable<string>? texts)
    {
        if (texts is null)
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keys = new List<RecordingKey>();
        foreach (var text in texts)
            if (Parse(text) is { } key && seen.Add(key.ToString()))
                keys.Add(key);
        return keys;
    }
}
