using System.Globalization;
using System.Text.Json.Serialization;

namespace RPM.Core.Sharing;

/// <summary>
/// The space and libraries part of a NAS's hello. A NAS library's own hello carries it too, with no
/// libraries listed.
/// </summary>
public sealed record NasHello
{
    /// <summary>The SMB address of the folder that holds the libraries, for example
    /// <c>smb://nas.local/music/rpm</c>.</summary>
    [JsonPropertyName("smb")] public string Smb { get; init; } = "";

    /// <summary>The space limit for all libraries together. Null when there is none.</summary>
    [JsonPropertyName("limitBytes")] public long? LimitBytes { get; init; }

    [JsonPropertyName("usedBytes")] public long UsedBytes { get; init; }

    /// <summary>Free space on the disk that holds the libraries.</summary>
    [JsonPropertyName("freeBytes")] public long FreeBytes { get; init; }

    [JsonPropertyName("libraries")] public IReadOnlyList<NasLibraryEntry> Libraries { get; init; } = [];

    /// <summary>The folder holds music and no library. A computer may make the folder itself a library,
    /// with its files as they are.</summary>
    [JsonPropertyName("existingMusic")] public bool ExistingMusic { get; init; }
}

/// <summary>One library folder on a NAS, as the NAS's hello lists it.</summary>
public sealed record NasLibraryEntry
{
    /// <summary>The folder's name under the folder that holds the libraries. Empty when the library is
    /// that folder itself.</summary>
    [JsonPropertyName("folder")] public string Folder { get; init; } = "";

    [JsonPropertyName("libraryId")] public string LibraryId { get; init; } = "";

    /// <summary>The name in the claim file, or the folder name before the library is claimed.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>The owner's account fingerprint from the claim file, or empty.</summary>
    [JsonPropertyName("account")] public string Account { get; init; } = "";

    [JsonPropertyName("deviceId")] public string DeviceId { get; init; } = "";

    /// <summary>The public key the library is announced with, base64url.</summary>
    [JsonPropertyName("publicKey")] public string PublicKey { get; init; } = "";
}

/// <summary>A NAS machine that also serves a computer's library. See <see cref="PeerCatalog.Hosts"/>.</summary>
public sealed record PeerHost
{
    [JsonPropertyName("deviceId")] public required string DeviceId { get; init; }

    /// <summary>Its public key, base64url. A reader pins this key.</summary>
    [JsonPropertyName("publicKey")] public required string PublicKey { get; init; }

    [JsonPropertyName("name")] public string Name { get; init; } = "";

    [JsonPropertyName("libraryId")] public required string LibraryId { get; init; }
}

/// <summary>What the owner's computer uploads to the NAS: the catalogue for its own devices, the
/// catalogue for guests, and every key it trusts.</summary>
public sealed record NasPublish
{
    [JsonPropertyName("owner")] public required PeerCatalog Owner { get; init; }

    [JsonPropertyName("guest")] public required PeerCatalog Guest { get; init; }

    [JsonPropertyName("trust")] public required IReadOnlyList<NasTrusted> Trust { get; init; }
}

/// <summary>One key the owner's computer trusts, and how it knows it.</summary>
public sealed record NasTrusted
{
    [JsonPropertyName("publicKey")] public required string PublicKey { get; init; }

    [JsonPropertyName("kind")] public required PeerKind Kind { get; init; }
}

/// <summary>What a NAS announcement is.</summary>
public enum NasRole
{
    /// <summary>The NAS as a whole. Its hello lists the libraries and the space.</summary>
    Nas,

    /// <summary>One library folder on the NAS.</summary>
    Library,
}

/// <summary>A NAS announcement, as read from the network.</summary>
public sealed record DiscoveredNas
{
    public required NasRole Role { get; init; }
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required byte[] PublicKey { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public int Version { get; init; }

    /// <summary>The library id. Libraries only.</summary>
    public string? LibraryId { get; init; }

    /// <summary>The SMB address of the folder that holds the libraries. The NAS only.</summary>
    public string? Smb { get; init; }

    /// <summary>Whether this NAS speaks the protocol version this build speaks.</summary>
    public bool SameVersion => Version == SharingProtocol.Version;
}

/// <summary>Reads a NAS announcement's TXT record.</summary>
public static class NasBeacon
{
    /// <summary>The announcement, or null when the record is incomplete or its id is not the id its key
    /// gives. Nothing in a record is trusted beyond that: it gives an address and a key to check.</summary>
    public static DiscoveredNas? Read(IReadOnlyDictionary<string, string> txt, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(txt);
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
            return null;

        if (Value(txt, SharingProtocol.Txt.PublicKey) is not { } encoded ||
            SharingProtocol.FromBase64Url(encoded) is not { Length: > 0 } key ||
            Value(txt, SharingProtocol.Txt.DeviceId) is not { } id ||
            !string.Equals(id, PeerIdentity.IdFor(key), StringComparison.OrdinalIgnoreCase))
            return null;

        NasRole role;
        switch (Value(txt, SharingProtocol.Txt.Role))
        {
            case SharingProtocol.Txt.RoleNas:
                role = NasRole.Nas;
                break;
            case SharingProtocol.Txt.RoleLibrary:
                role = NasRole.Library;
                break;
            default:
                return null;
        }

        var libraryId = Value(txt, SharingProtocol.Txt.LibraryId);
        if (role is NasRole.Library && libraryId is null)
            return null;

        return new DiscoveredNas
        {
            Role = role,
            DeviceId = id,
            Name = Value(txt, SharingProtocol.Txt.Name) ?? id,
            PublicKey = key,
            Host = host,
            Port = port,
            Version = int.TryParse(Value(txt, SharingProtocol.Txt.Version), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var version) ? version : 0,
            LibraryId = libraryId,
            Smb = Value(txt, SharingProtocol.Txt.Smb),
        };
    }

    private static string? Value(IReadOnlyDictionary<string, string> txt, string key)
    {
        foreach (var (name, value) in txt)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}
