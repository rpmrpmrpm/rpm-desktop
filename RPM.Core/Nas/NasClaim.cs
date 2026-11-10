using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Sharing;

namespace RPM.Core.Nas;

/// <summary>One owner computer in a library's claim file.</summary>
public sealed record NasClaimKey
{
    /// <summary>The computer's public key, base64url.</summary>
    [JsonPropertyName("publicKey")] public required string PublicKey { get; init; }

    /// <summary>What the owner calls the library. The NAS shows it in its list of libraries.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>The owner's account fingerprint (<see cref="NasClaim.AccountFingerprint"/>), or empty when
    /// the computer was signed out.</summary>
    [JsonPropertyName("account")] public string Account { get; init; } = "";
}

/// <summary>The claim file: the computers that own a library on a NAS.</summary>
public sealed record NasClaimFile
{
    [JsonPropertyName("keys")] public IReadOnlyList<NasClaimKey> Keys { get; init; } = [];

    /// <summary>Whether <paramref name="publicKey"/> is one of the owner computers.</summary>
    public bool Holds(byte[] publicKey) =>
        Keys.Any(k => SharingProtocol.FromBase64Url(k.PublicKey) is { } key && key.AsSpan().SequenceEqual(publicKey));
}

/// <summary>
/// The claim file in a library folder on a NAS (<c>.rpm/daemon-owner.json</c>). A NAS has no screen to
/// show a pairing code, so a computer claims a library by writing its key here over SMB. Only someone
/// whose NAS login can write the folder can do that, and the daemon accepts uploads only from a key in
/// this file.
/// </summary>
public static class NasClaim
{
    public const string FileName = "daemon-owner.json";

    public static string PathIn(string libraryRoot) => Path.Combine(libraryRoot, ".rpm", FileName);

    /// <summary>The claim file, or null when there is none or it cannot be read.</summary>
    public static NasClaimFile? Read(string libraryRoot)
    {
        try
        {
            var path = PathIn(libraryRoot);
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), NasJson.Default.NasClaimFile)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds <paramref name="key"/> to the claim file, or replaces the entry with the same public key. The
    /// other keys stay, so a second computer of the same owner joins without removing the first.
    /// </summary>
    /// <remarks>Written to a temporary name and renamed, so the daemon never reads half a file.</remarks>
    public static void AddKey(string libraryRoot, NasClaimKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var current = Read(libraryRoot) ?? new NasClaimFile();
        var keys = current.Keys.Where(k => !string.Equals(k.PublicKey, key.PublicKey, StringComparison.Ordinal))
            .Append(key)
            .ToList();
        var path = PathIn(libraryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(current with { Keys = keys }, NasJson.Default.NasClaimFile));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The account as the claim file and the NAS's hello carry it: base64url of the first 12 bytes of the
    /// SHA-256 of the user id. The folder is readable by anyone with the NAS login, so the id itself is
    /// not written there.
    /// </summary>
    public static string AccountFingerprint(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        return SharingProtocol.ToBase64Url(SHA256.HashData(Encoding.UTF8.GetBytes(userId))[..12]);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NasClaimFile))]
internal sealed partial class NasJson : JsonSerializerContext;
