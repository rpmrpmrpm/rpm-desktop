using RPM.DataGrid.Model;
using RPM.Desktop.Presentation.Main.Library.Models;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Grids;

/// <summary>
/// The library table's columns, as <see cref="RpmGrid"/> wants them.
///
/// <para>Twenty-seven columns, in order, at the widths the grid uses. Each one reads a property of
/// <see cref="LibraryTableRow"/>.</para>
///
/// <para>A column's <see cref="GridColumn.Id"/> is its name in the MIRROR, not the name of the row's
/// property. The sort, the filter and the saved session all use this one name. Whether a name may
/// reach any SQL at all is decided by <c>TrackTableColumns</c>, not here.</para>
/// </summary>
internal static class LibraryGrid
{
    public static GridColumns Columns() => new(
    [
        // The playing mark. No field of its own, so nothing to sort or filter it by.
        GridColumn.PlayingBars("playing", 30, row => ((LibraryTableRow)row).IsPlaying),

        // What the record says it is, with the explicit badge behind the name.
        GridColumn.TitleBadge("title", 190,
            row => new TitleCell(((LibraryTableRow)row).Title, ((LibraryTableRow)row).IsExplicit),
            header: Loc.Get("Review_ColTitle"), canSort: true, canFilter: true),

        Text("artist", 150, "Review_ColArtist", r => r.Artist, filter: true),
        Text("album", 150, "Review_ColAlbum", r => r.Album, filter: true),
        Text("album_artist", 150, "Review_ColAlbumArtist", r => r.AlbumArtist, filter: true),
        Text("track_number", 56, "Review_ColTrack", r => r.TrackNumber, align: CellAlign.Right),
        Text("disc_number", 48, "Review_ColDisc", r => r.DiscNumber, align: CellAlign.Right),
        Text("disc_subtitle", 130, "Review_ColDiscTitle", r => r.DiscSubtitle, filter: true),
        Text("year", 64, "Review_ColYear", r => r.Year, align: CellAlign.Right, filter: true),
        Text("genre", 120, "Review_ColGenre", r => r.Genre, filter: true),
        Text("work", 200, "Review_ColWork", r => r.Work, filter: true),
        Text("composer", 150, "Review_ColComposer", r => r.Composer, filter: true),

        // No filter on these columns. The cell shows a formatted value, not the stored one: "3:45"
        // for 225 seconds, or a date for a tick count. The filter is a LIKE against the stored column,
        // so a search for the shown text would match nothing. Plays is stored as a number and would
        // match, but a text search on a play count is no use, since sorting by it answers better.
        Text("duration_seconds", 70, "Review_ColLength", r => r.Duration, align: CellAlign.Right),
        Text("plays", 60, "Review_ColPlays", r => r.Plays, align: CellAlign.Right),
        Text("last_played_utc_ticks", 130, "Review_ColLastPlayed", r => r.LastPlayed),
        Text("added_utc_ticks", 130, "Review_ColAdded", r => r.Added),

        Text("extension", 70, "Review_ColKind", r => r.Kind, filter: true),
        Text("codec_description", 120, "Review_ColCodec", r => r.Codec, filter: true),
        Text("bitrate_kbps", 86, "Review_ColBitrate", r => r.Bitrate, align: CellAlign.Right),
        Text("sample_rate", 90, "Review_ColSampleRate", r => r.SampleRate, align: CellAlign.Right),
        Text("bits_per_sample", 76, "Review_ColBitDepth", r => r.BitDepth, align: CellAlign.Right),
        Text("channels", 80, "Review_ColChannels", r => r.Channels, align: CellAlign.Right),
        Text("size_bytes", 80, "Review_ColSize", r => r.Size, align: CellAlign.Right),
        Text("cloud_state", 96, "Review_ColCloud", r => r.Cloud, filter: true),
        Text("folder", 200, "Review_ColFolder", r => r.Folder, filter: true),
        Text("file_name", 200, "Review_ColFile", r => r.FileName, filter: true),
        Text("rpm_id", 150, "Review_ColRpmId", r => r.RpmId, filter: true),
    ]);

    /// <summary>Whether a column can be turned off. The playing mark cannot: it is how the table says
    /// which track is playing, and it has no header to offer in a menu.</summary>
    public static bool IsOptional(GridColumn column) => column.Id != "playing";

    private static GridColumn Text(
        string id, double width, string headerKey, Func<LibraryTableRow, string?> read,
        CellAlign align = CellAlign.Left, bool filter = false) =>
        GridColumn.Text(
            id, width, row => read((LibraryTableRow)row),
            header: Loc.Get(headerKey), canSort: true, canFilter: filter, align: align);
}
