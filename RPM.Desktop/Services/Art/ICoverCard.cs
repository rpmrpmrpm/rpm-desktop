using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// One record on a wall of covers, as <see cref="CoverStream"/> sees it.
///
/// <para>Two walls use this: the library window's wall and the visualizer's wall. They are different
/// controls with different layouts, chrome and purposes. Each wall had its own copy of the same stream
/// code, and the copies drifted apart, so a fix made on one wall was missing from the other. What the
/// walls share is this: a record, two places to put its pictures, and a note of what has already been
/// read. That is all the stream needs, and all it touches.</para>
///
/// <para><b>The stream writes these, and nothing else should.</b> A surface may read any of them. The
/// drawer borrows the sharp picture to open on, and the record label draws it. But the tiers belong to
/// the stream. A surface that assigns one will disagree with the loader about which picture the record
/// is showing.</para>
/// </summary>
public interface ICoverCard
{
    /// <summary>The album folder. It is the record's name everywhere in this app. It is the key the pack
    /// is written under, and it does not change when the tags are edited.</summary>
    string Folder { get; }

    /// <summary>The resident layer. The tile paints this on the frame it is realised.</summary>
    ImageSource? Cover { get; set; }

    /// <summary>The layer that fades in over the resident one. It is cut for the size the tile is drawn
    /// at.</summary>
    ImageSource? Sharp { get; set; }

    /// <summary>What the resident layer falls back to when a tile is rebound. It is the same picture as
    /// <see cref="Cover"/>, kept rather than read again.</summary>
    ImageSource? Tiny { get; set; }

    /// <summary>The resident picture, encoded. This is tens of kilobytes, not the megabyte its pixels
    /// take. The collection sweep gathers these, and the pack is written from them. This is why a record
    /// scrolled far away can release its pixels and still paint without a disk read.</summary>
    byte[]? ResidentBytes { get; set; }

    /// <summary>The colour read off the cover. It is read once per record and kept. It is null until it
    /// has been read. A cover that has really changed clears it.</summary>
    Color? Tint { get; set; }

    /// <summary>No art anywhere for this record. It is set once, so a wall of records without covers does
    /// not read each one on every pass for the rest of the session. See <see cref="CoverTier.Latches"/>
    /// for how this differs from a read that failed.</summary>
    bool CoverMissing { get; set; }

    /// <summary>The edge at which a read came back empty. The loader asks again at any other size, but
    /// not at this one. See <see cref="CoverTier.WorthAsking"/>.</summary>
    int MissedEdge { get; set; }

    /// <summary>The pixel edge the picture was DECODED at. It is not the edge it is drawn at, and not the
    /// edge of the file it came from. Set it to the edge that was read, and never just raise it towards
    /// that edge. A raised value could not come back down. A card that kept claiming a bigger picture
    /// than the wall now needs would be queued as a job on every pass, for ever.</summary>
    int CoverEdge { get; set; }
}
