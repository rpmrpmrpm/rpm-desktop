using RPM.Core.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>The files an album is made of, for asking whether what plays is still that album.</summary>
internal sealed record RecordFiles(IReadOnlySet<string> Paths)
{
    public static RecordFiles Of(IEnumerable<TrackInfo> tracks) =>
        new(tracks.Select(t => t.FilePath).ToHashSet(StringComparer.Ordinal));

    public bool Holds(string? playing) => playing is not null && Paths.Contains(playing);

    /// <summary>
    /// Whether a handed-over cover for this album stays on the player's square: the header has no cover
    /// for what plays yet, and what plays is still this album. Asked each time the header republishes
    /// and when the empty-cover grace runs out. The answer is true whenever those conditions hold, not
    /// only for a short time window.
    /// </summary>
    public bool StandsIn(bool headerHasCover, string? playing) => !headerHasCover && Holds(playing);
}

/// <summary>
/// The cover that the screen an album was played from has already decoded. It is handed to the player
/// for the time between the press and the arrival of the header's own cover.
///
/// <para>Every press that puts music on from Pegasus states one (<see cref="PegasusChrome"/>'s
/// PlayFromDrawer requires it, with no default value): an album page hands its cover over, and a list
/// of songs from many albums has none to hand, so it passes <see cref="None"/>.</para>
/// </summary>
internal abstract record SleeveInHand
{
    private SleeveInHand() { }

    /// <summary>Nothing to hand over: the screen does not show the cover of one album for what it plays.</summary>
    public sealed record None : SleeveInHand;

    /// <summary>The album's cover as the screen shows it, and the files that cover belongs to.</summary>
    public sealed record Of(ImageSource Picture, RecordFiles Record) : SleeveInHand;

    /// <summary>The picture in <paramref name="art"/>, as the cover of <paramref name="tracks"/>, or
    /// <see cref="None"/> while the frame is still empty.</summary>
    public static SleeveInHand From(Image? art, IEnumerable<TrackInfo> tracks) =>
        art?.Source is { } picture ? new Of(picture, RecordFiles.Of(tracks)) : new None();
}

public sealed partial class PegasusChrome
{
    /// <summary>
    /// A cover handed over by the screen an album was played from. It stays on the player's square until
    /// the header has a cover of its own, or until that album stops being what plays. Null otherwise.
    ///
    /// <para>The player shows only the header's cover (see Apply). After a press the header finds and
    /// decodes that cover from scratch: the tags, the picture, a cache keyed by the picture's bytes, the
    /// palette, and two decodes. That can take about a second, and the opaque placeholder shows for that
    /// time, although the album page the press came from already has the same cover decoded.</para>
    /// </summary>
    private SleeveInHand.Of? _standIn;

    /// <summary>The file on the deck. A handed-over cover is checked against it.</summary>
    private string? PlayingPath => _amp?.Deck?.FilePath;

    /// <summary>
    /// Puts a handed-over cover on the player's square, at full opacity, before the composition appears.
    ///
    /// <para>Only over an empty square. A cover that is already shown stays until the header's next cover
    /// fades over it, as on any change of track. The header does not say which album a cover is from, so
    /// this class cannot tell an old album's cover from the new one's.</para>
    /// </summary>
    private void HandOver(SleeveInHand sleeve)
    {
        if (sleeve is not SleeveInHand.Of handed)
            return;
        if (_coverWanted is not null)
            return;

        // The press has put this album on (PlayAlbumFrom sets the deck before it returns). An empty list
        // puts nothing on, and then there is nothing to stand in for.
        if (!handed.Record.StandsIn(headerHasCover: false, PlayingPath))
            return;

        _standIn = handed;
        _coverGrace?.Stop();

        // An album cover fills the square, whatever was shown before (a station's mark is fitted inside
        // the square, on a ground of its own).
        FrameLayers(whole: false, width: 0, height: 0);
        CoverGround.Source = null;
        CoverGround.Opacity = 0;
        CrossTo(handed.Picture, fadeMs: 0);
    }

    /// <summary>Whether the handed-over cover still applies. Clears it when it does not.</summary>
    private bool StandInHolds(bool headerHasCover)
    {
        if (_standIn is { } standIn && standIn.Record.StandsIn(headerHasCover, PlayingPath))
            return true;
        _standIn = null;
        return false;
    }
}
