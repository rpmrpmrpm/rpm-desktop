using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The one place where the cover of a generated playlist is chosen, and the order of the choice.
///
/// <para><b>Why it is shared.</b> Four surfaces show one of these lists as a picture: the playlists
/// tree, the head of the list's own page, the home screen's row and a genre page's shelf. They must
/// all show the same picture for the same list, so the order is written once, here.
/// <see cref="PlaylistCovers"/> does the same for a list that a person made and kept.</para>
///
/// <para><b>Two questions.</b> First, what goes behind the window. Second, the frame over it. Every
/// generated list has the frame, so the order below chooses a picture, and <see cref="CoverOverlay"/>
/// draws the app's own frame over whichever picture it is.</para>
///
/// <para><b>What goes behind the window, best first.</b>
/// <list type="number">
/// <item>The flat room, on the five lists that stand on one (<see cref="CoverOverlay.Roomed"/>): a
/// picture that the app makes for this cover and shows nowhere else.</item>
/// <item>The photograph that ships for this list, if it has one
/// (<see cref="Services.Art.GeneratedCoverService"/>, <see cref="PlaylistPhotos"/>). It costs a file
/// read and two draws. Most lists have none and use the figure below.</item>
/// <item>The figure drawn from the list's own two numbers (<see cref="PlaylistArt.Generated"/>). It
/// always works and is the same picture on every machine.</item>
/// </list></para>
///
/// <para><b>The frame is a layer, not a replacement.</b> If it cannot be drawn (no asset, no
/// surface), the result is the picture underneath it.</para>
/// </summary>
internal static class GeneratedCover
{
    /// <summary>The square, for a surface that shows one and nothing else.</summary>
    internal static async Task<byte[]?> SquareAsync(SmartPlaylistId id, CancellationToken ct)
        => (await DrawnAsync(id, SmartPlaylistFace.Name(id), CoverGround.NotWanted, ct)).Cover;

    /// <summary>The square and the wash the page behind it stands on, for the list's own page.</summary>
    internal static Task<PlaylistArt.Drawn> PageAsync(SmartPlaylistId id, CancellationToken ct)
        => DrawnAsync(id, SmartPlaylistFace.Name(id), CoverGround.Wanted, ct);

    /// <summary>The square and its wash with <paramref name="name"/> in the frame instead of the list's
    /// own name. A mood playlist that a person made uses its mood's picture under its own name
    /// (<see cref="MoodCovers"/>).</summary>
    internal static Task<PlaylistArt.Drawn> NamedAsync(SmartPlaylistId id, string name, CancellationToken ct)
        => DrawnAsync(id, name, CoverGround.Wanted, ct);

    /// <param name="name">The name set into the frame's band, spelled for this locale. The frame ships with an empty
    /// band.</param>
    private static async Task<PlaylistArt.Drawn> DrawnAsync(
        SmartPlaylistId id, string name, CoverGround ground, CancellationToken ct)
    {
        var tint = SmartPlaylistFace.Tint(id);
        var room = CoverRoom.Of(id);

        // The five lists that stand on the room are composed whole. The room is drawn at the size of
        // the cover, so there is no separate picture to lay the frame over.
        if (CoverOverlay.Roomed(id.Kind))
        {
            var framed = await Task.Run(() => CoverOverlay.Draw(name, room, ground), ct);

            // If the frame cannot be drawn (no shader on this machine, no surface), the code falls
            // through to the two answers below, so the list always has a picture.
            if (framed.Cover is { Length: > 0 })
                return framed;
        }

        var picture = await BehindAsync(id, tint, ground, ct);

        // Then the frame over it. The wash comes from the picture alone: a blur of the framed cover
        // would be a dark field with the list's name smeared across it. See CoverOverlay.Over.
        if (picture.Cover is { Length: > 0 } behind
            && await Task.Run(() => CoverOverlay.Over(behind, name, room), ct)
                is { Length: > 0 } framedOver)
            return picture with { Cover = framedOver };

        return picture;
    }

    /// <summary>The picture that the frame goes over: the photograph kept for this list, or the figure
    /// drawn from its own two numbers, which always works.</summary>
    private static async Task<PlaylistArt.Drawn> BehindAsync(
        SmartPlaylistId id, Windows.UI.Color tint, CoverGround ground, CancellationToken ct)
    {
        if (await Services.Art.GeneratedCoverService.CoverAsync(id, tint, ct) is { Length: > 0 } found)
            return new PlaylistArt.Drawn(
                found,
                ground is CoverGround.Wanted ? Services.Art.ArtScaler.Wash(found) : null);

        return await Task.Run(
            () =>
            {
                var (style, seed) = SmartPlaylistFace.Cover(id);
                return PlaylistArt.Generated(style, seed, tint);
            },
            ct);
    }
}
