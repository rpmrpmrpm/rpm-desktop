using RPM.Core.Library;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// A mood playlist's cover: the frame every generated list wears (its name and the rpm mark,
/// <see cref="CoverOverlay"/>) over the picture the listener chose for it (<see cref="MoodCover"/>). The
/// frame is the same one all smart playlists wear, so the listener only chooses the image under it.
///
/// <para>The frame is inked from the mood's own room (<see cref="CoverRoom.Of(SmartPlaylistId)"/> of the whole-shelf
/// list), so a Calm list somebody made wears Calm's colours whatever picture is under it.</para>
/// </summary>
internal static class MoodCovers
{
    /// <summary>
    /// The framed square and the wash its page stands on, for <paramref name="cover"/> under
    /// <paramref name="name"/>. <see cref="PlaylistArt.Drawn.None"/> where nothing could be drawn.
    /// </summary>
    /// <param name="sleeves">The chosen records' covers, in the chosen order, for <see cref="MoodCoverSource.Records"/>;
    /// empty for the others.</param>
    /// <param name="picture">The listener's own picture, already squared, for <see cref="MoodCoverSource.Picture"/>;
    /// null for the others.</param>
    internal static async Task<PlaylistArt.Drawn> DrawAsync(
        SmartPlaylistKind mood, MoodCover cover, string name, IReadOnlyList<byte[]> sleeves, byte[]? picture,
        CancellationToken ct)
    {
        var id = SmartPlaylistId.WholeShelf(mood);

        // The mood's own picture is the generated list's whole cover with this name in its band, roomed or
        // photographed exactly as that list is.
        if (cover.Source is MoodCoverSource.Mood)
            return await GeneratedCover.NamedAsync(id, name, ct);

        var under = cover.Source switch
        {
            MoodCoverSource.Records => sleeves.Count > 0 ? await Task.Run(() => PlaylistArt.Made(sleeves, name).Cover, ct) : null,
            MoodCoverSource.Picture => picture,
            MoodCoverSource.Figure => await Task.Run(
                () => PlaylistArt.Figure(cover.Style, cover.Seed, CoverMark.None, mood: null).Cover, ct),
            MoodCoverSource.Mood => null,
        };
        if (under is not { Length: > 0 })
            return await GeneratedCover.NamedAsync(id, name, ct);

        var room = CoverRoom.Of(id);
        return await Task.Run(() =>
        {
            // The wash is the picture's own, as on the generated lists: a blur of the frame is a dark field with the
            // name smeared across it.
            var framed = CoverOverlay.Over(under, name, room);
            return new PlaylistArt.Drawn(framed is { Length: > 0 } ? framed : under, Services.Art.ArtScaler.Wash(under));
        }, ct);
    }
}
