using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// One record wearing a sleeve on a shelf that is not the wall. Examples are an artist's page, a
/// genre page, the row of suggestions under an album, and the home screen's row.
///
/// <para><b>Why a second interface and not <see cref="ICoverCard"/>.</b> ICoverCard is what
/// <see cref="CoverStream"/> needs. It has two picture tiers, the encoded resident bytes, the pack, the
/// tint, and the edge each was read at. All of this exists because a wall is virtualised, zoomable and
/// scrolled. The wall reads the same record at three sizes, and it must not read the same size twice.
/// A shelf does none of that. It asks once, at one size, for cards it has already built. What a shelf
/// needs to be told is one thing that is easy to get wrong: not "here is a picture", but
/// "there is no picture, and there never will be".</para>
///
/// <para><b>That verdict is the whole point.</b> A card draws its made sleeve (see
/// <see cref="Converters.SleeveFigureConverter"/>) from a seed. The seed must stay null until the cover
/// has really been looked for and not found. If each shelf had to remember this itself, none of them
/// did. Every card on those pages drew a Skia paint and a blur when it was realised, and then covered
/// it with the real artwork a moment later. <see cref="CoverFill"/> is the only method that sets the
/// verdict, through <see cref="CoverMissing"/>.</para>
/// </summary>
public interface ISleeveCard
{
    /// <summary>Whether this card already has a picture. The fill reads this but never writes it. It
    /// tells a read that returned nothing apart from art that is really absent. See
    /// <see cref="CoverTier.Latches"/>.</summary>
    bool HasCover { get; }

    /// <summary>The picture, once it has landed.</summary>
    ImageSource? Cover { get; set; }

    /// <summary>No art anywhere for this record: not in its folder, not in its tags, not on the computer
    /// that holds it, and not in the cloud. Only <see cref="CoverFill"/> sets this. A card that sets it
    /// itself would claim a verdict on a read it did not make.</summary>
    bool CoverMissing { get; set; }
}
