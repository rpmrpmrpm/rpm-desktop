using RPM.Core.Models;

namespace RPM.Core.Library;

/// <summary>
/// The filter that narrows the import queue. It is one object, not a long list of parameters. Every
/// read of the queue takes the same filter. A caller that rebuilds after an edit passes one value, so
/// it cannot quietly forget one of several parameters.
/// </summary>
public sealed record QueueFilter
{
    /// <summary>Nothing narrowed. The queue as it stands.</summary>
    public static readonly QueueFilter None = new();

    /// <summary>Free text. Each word matches the start of a word, using the full-text search
    /// index.</summary>
    public string? Search { get; init; }

    public bool TickedOnly { get; init; }
    public bool NeedsRecognition { get; init; }

    /// <summary>Hides the losing copies: exactly the rows that
    /// <see cref="LibraryDatabase.QueueRemoveDuplicates"/> would delete. These are rows already in the
    /// queue or on the shelf at equal or better quality. This is the only chip that hides rows instead of
    /// showing them. A queue of ten thousand files is mostly copies the user does not keep, so the user
    /// wants to see the rest.</summary>
    public bool HideDuplicates { get; init; }

    public bool Unreadable { get; init; }
    public bool SplitCandidates { get; init; }

    /// <summary>Cloud placeholders: the file is a row here but its bytes are not on this disk.</summary>
    public bool NoData { get; init; }

    /// <summary>Where the row's metadata came from, or null for any.</summary>
    public MetadataSource? Source { get; init; }

    /// <summary>A file extension with its dot (".flac"), or null for any.</summary>
    public string? Extension { get; init; }

    /// <summary>True when the filter narrows nothing, so the queue shows every row.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Search) && !AnyChip;

    /// <summary>True when any chip is on. The search text does not count. The bar's clear button clears
    /// the chips, not the search text.</summary>
    public bool AnyChip =>
        TickedOnly || NeedsRecognition || HideDuplicates || Unreadable || SplitCandidates || NoData
        || Source is not null || Extension is not null;
}

/// <summary>How many rows each chip covers. These counts come from one read, so a bar of six counts
/// costs one scan of the queue, not six.</summary>
/// <param name="Duplicates">Rows that a copy already covers: another queued file, or a track the library
/// holds a file for. Cloud records are not counted, because there is no file to copy.</param>
/// <param name="InCloud">Rows the collection already lists as cloud records, with no file on this
/// computer. They are counted apart from <paramref name="Duplicates"/>, because the advice is the
/// opposite: these are the files worth importing, since importing puts them on this computer.</param>
public readonly record struct QueueChipCounts(
    int Total, int Ticked, int NeedsRecognition, int Duplicates, int Unreadable,
    int SplitCandidates, int NoData, int InCloud, long Bytes = 0, double Seconds = 0);
