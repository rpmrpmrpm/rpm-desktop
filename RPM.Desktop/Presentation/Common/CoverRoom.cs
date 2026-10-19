using System.Numerics;
using RPM.Core.Library;
using RPM.Core.Viz;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The colours that a picture paints a room in.
///
/// <para>This has one text, because two surfaces use it for the same list. They are the playlist page
/// header (<c>PlaylistView.OpenRoom</c>) and the home banner card (<c>HomeView.LoadListFaceAsync</c>).
/// They show the same list. If the header were blue and the card green, the list would look like two
/// different lists.</para>
///
/// <para><b>None of the three colours can be used as it stands.</b> That is the job of
/// <see cref="RoomPalette.From"/>, not of this file. Covers have muted, uneven tones. A room painted in
/// them as they are would look muddy or disappear. This file only chooses which three readings to take,
/// and in which order.</para>
///
/// <para>It decodes the picture three times, once per reading. <see cref="CoverColor"/> warns against
/// this for a list that reads a record for each row. It is fine here. A header and a card each ask once
/// per list, not once per frame.</para>
/// </summary>
internal static class CoverRoom
{
    /// <summary>
    /// The room this cover paints. It is <see cref="RoomPalette.Fallback"/>, the app's own blue, where
    /// there is no picture or it cannot be decoded.
    /// </summary>
    /// <remarks>
    /// It decodes and resizes the picture, so it must run on a worker thread. Neither caller is on the
    /// drawing thread.
    /// </remarks>
    internal static RoomPalette Of(byte[]? cover)
    {
        if (cover is not { Length: > 0 })
            return RoomPalette.Fallback;

        try
        {
            var third = CoverColor.Palette(cover, 3);
            return RoomPalette.From(
                Vec(CoverColor.Dominant(cover)),
                Vec(CoverColor.Highlight(cover)),
                third.Count > 2 ? Vec(third[2]) : null);
        }
        catch
        {
            // A picture that fails to decode is not a reason for a card to have no ground colour.
            return RoomPalette.Fallback;
        }
    }

    /// <summary>
    /// What a generated list's room is: its colours, and where in the drift it starts.
    /// </summary>
    /// <remarks>
    /// <b>Two facts are kept, not one, because the colours are not unique.</b> A list's colourway comes
    /// from its seed, and two lists can have the same colourway. For example, two lists with different
    /// names can both be Crimson at seed 9,778. They differ only in which FAMILY their figure is drawn
    /// as. A room has no families, so the same colours would give the same picture. The drift start is
    /// read from the colours, so the two would also share a pose. The identity of the list has to be part
    /// of this value. Otherwise those two cards are one card with two names.
    /// </remarks>
    /// <param name="Colours">What the room is painted in.</param>
    /// <param name="Apart">How many places along the drift this list starts from, beyond what its colours
    /// give. See <see cref="CardRoomArt.Still(RoomPalette, int)"/>.</param>
    /// <param name="Ink">The three palette colours this list was rolled with, exactly as the asset lists
    /// them. <paramref name="Colours"/> is these three after <see cref="RoomPalette.From"/> has saturated
    /// and lifted them. So the palette colours cannot be read back from it. A cover's lettering and border
    /// must BE palette colours, so the untouched three are kept alongside. Null where the palette has not
    /// loaded.</param>
    internal readonly record struct ListRoom(
        RoomPalette Colours, int Apart, PlaylistFigure.Colourway? Ink);

    /// <summary>
    /// The room a generated list paints: the three colours its own cover is drawn in.
    /// </summary>
    /// <remarks>
    /// <b>The colourway the card already holds is used,</b> not a hue turned from one colour.
    /// <see cref="RoomPalette.From"/> given only a tint turns a hue for the second family. Its own remarks
    /// call that a last resort. The list already has three real colours. <c>PlaylistFigure.Colourway</c>
    /// picks three neighbours from the app's colour ring for the figure that this room stands in for.
    ///
    /// <para>The colours are read from the SEED, not from the drawn picture. Using <see cref="Of(byte[])"/>
    /// would mean drawing the figure and decoding it three times, only to recover the three numbers that
    /// went into it. The colours are the same either way, and no picture is needed in between.</para>
    ///
    /// <para>The app's blue is used where the palette has not loaded. Every other surface falls back to it
    /// too, rather than inventing a colour.</para>
    /// </remarks>
    internal static ListRoom Of(SmartPlaylistId id)
    {
        var apart = (int)id.Kind;

        var ring = RpmPalette.Colours
                             .Select(colour => new SKColor(colour.R, colour.G, colour.B))
                             .ToList();

        if (PlaylistFigure.Colourway.From(ring, SmartPlaylistFace.Cover(id).Seed) is not { } ink)
            return new ListRoom(RoomPalette.Fallback, apart, Ink: null);

        // Mid is most of what is seen, Hot is what the light is made of, and Deep is the shadow. That is
        // the order in which RoomPalette.From expects its three colours.
        return new ListRoom(
            RoomPalette.From(Vec(ink.Mid), Vec(ink.Hot), Vec(ink.Deep)),
            apart,
            ink);
    }

    private static Vector3? Vec(Windows.UI.Color? c)
        => c is { } v ? new Vector3(v.R / 255f, v.G / 255f, v.B / 255f) : null;

    private static Vector3 Vec(SKColor c) => new(c.Red / 255f, c.Green / 255f, c.Blue / 255f);
}
