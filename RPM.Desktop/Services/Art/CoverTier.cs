namespace RPM.Desktop.Services.Art;

/// <summary>
/// Which of a record's two pictures may be written over the other.
///
/// <para>A tile has two pictures. The resident one is small and is kept for the whole collection. The
/// sharp one is cut to the size the tile is drawn at, and it is given back when the tile scrolls out
/// of view. The loader fills both, from two different jobs, on three workers, in an order nobody
/// controls. The question is not "is this picture newer?". The question is "is this picture at least
/// as big as what the wall is asking for?". The rule is written here, not inside the loader, because
/// both questions are one rule seen from two sides.</para>
/// </summary>
public static class CoverTier
{
    /// <summary>
    /// Whether a picture read at <paramref name="readEdge"/> must leave the sharp layer where it is.
    ///
    /// <para>The whole-collection pass fills the resident picture for every record that lacks one. A
    /// record on screen can already have a sharp picture and still lack a resident one. Without this
    /// rule, the pass read a 128 pixel picture and put it over the 512 pixel picture the user was
    /// looking at. The tile went soft, the loader saw that the size did not match, and it read the
    /// sharp picture again. This caused a blur and a re-read for each tile on screen.</para>
    /// </summary>
    public static bool KeepsSharp(int readEdge, int wantedEdge, bool hasSharp)
        => hasSharp && readEdge < wantedEdge;

    /// <summary>
    /// Whether the loader should ask for a picture at <paramref name="wantedEdge"/>.
    ///
    /// <para><paramref name="missedEdge"/> stops the retry from becoming a loop. A read that returns
    /// nothing is remembered with the size it was asked at. The wall asks again when the zoom changes,
    /// and not before.</para>
    /// </summary>
    public static bool WorthAsking(int coverEdge, int missedEdge, int wantedEdge, bool missing)
        => !missing && coverEdge != wantedEdge && missedEdge != wantedEdge;

    /// <summary>
    /// Whether "no picture came back" should be believed for ever.
    ///
    /// <para>Only when the record has never shown a picture. A record that already has a cover has art,
    /// so an empty read means the read failed, not that the art is missing. A read can fail when a file
    /// is being written, when a computer on the network does not answer, or when the cloud is briefly
    /// unreachable. If the failure were believed, the loader would drop the record for the rest of the
    /// session, and the record would keep its small picture whatever size the wall asks for. That is
    /// why the sharpening sometimes did not happen.</para>
    /// </summary>
    public static bool Latches(bool hasAnyPicture) => !hasAnyPicture;
}
