namespace RPM.Core.Models;

/// <summary>One proposed rename or move. It maps the source file to a path relative to the destination
/// root.</summary>
public sealed record TrackSuggestion
{
    public required TrackInfo Track { get; init; }

    /// <summary>Path relative to the destination root, always with '/' separators.</summary>
    public required string ProposedRelativePath { get; init; }

    /// <summary>True when the proposal equals the file's current location under the destination
    /// root.</summary>
    public bool IsAlreadyInPlace { get; init; }
}
