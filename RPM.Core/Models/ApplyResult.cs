namespace RPM.Core.Models;

/// <summary>Whether files are moved into the organized layout or copied, leaving originals.</summary>
public enum OrganizeMode
{
    Move,
    Copy,
}

/// <summary>Outcome of applying one suggestion.</summary>
public sealed record ApplyResult
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
