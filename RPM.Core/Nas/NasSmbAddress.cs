namespace RPM.Core.Nas;

/// <summary>
/// An SMB address split into the parts a mount needs: <c>smb://host/share/path</c>. The share is what is
/// mounted; the path is the folder inside it that holds the libraries, and may be empty.
/// </summary>
public sealed record NasSmbAddress(string Host, string Share, string Path)
{
    /// <summary>Reads <c>smb://host/share</c> or <c>smb://host/share/a/b</c>. Null for anything else.</summary>
    public static NasSmbAddress? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "smb", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(uri.Host))
            return null;

        var parts = Uri.UnescapeDataString(uri.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        var host = uri.IsDefaultPort || uri.Port <= 0 ? uri.Host : $"{uri.Host}:{uri.Port}";
        return new NasSmbAddress(host, parts[0], string.Join('/', parts.Skip(1)));
    }

    /// <summary>The share alone, as macOS and Linux mount it: <c>smb://host/share</c>.</summary>
    public string ShareUrl => $"smb://{Host}/{Uri.EscapeDataString(Share)}";

    /// <summary>The share alone, as Windows reaches it: <c>\\host\share</c>.</summary>
    public string UncShare => $@"\\{HostWithoutPort}\{Share}";

    /// <summary>The host name without a port. Windows cannot name a port in a UNC path.</summary>
    public string HostWithoutPort => Host.Split(':')[0];

    /// <summary>The library folder <paramref name="folder"/> under the share's local root.</summary>
    public string LibraryPath(string shareRoot, string folder)
    {
        var path = shareRoot;
        foreach (var part in Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Append(folder))
            path = System.IO.Path.Combine(path, part);
        return path;
    }
}
