namespace RPM.Core.Library;

/// <summary>
/// Whether a made cover shows the parental advisory mark in its corner.
///
/// <para>This is a type, not a bool, and every call must state it. It is never defaulted. The mark is a
/// claim about the music. A mark that appears by accident says that a record is explicit when nobody
/// said so. A mark that goes missing when a cover is redrawn removes a label that the record needs.
/// Neither mistake is visible in a diff of a call site that simply leaves the argument out.</para>
/// </summary>
public enum CoverMark
{
    /// <summary>No mark. Most covers have no mark.</summary>
    None,

    /// <summary>The parental advisory lockup, stamped in the corner.</summary>
    Explicit,
}

/// <summary>
/// How a playlist's picture was arrived at.
///
/// <para><b>Provenance, not a contract.</b> The picture itself is bytes. It is drawn once on the desktop
/// and kept as a file in the cover store. This type records only which method made the picture. It is
/// kept for two jobs. One is reopening the picker on the tab the user last used, with their choice still
/// ticked. The other is knowing that a <see cref="Records"/> cover must be redrawn when one of its albums
/// is retagged into a different folder.</para>
///
/// <para>It is never sent to another machine. Other machines receive the picture, not the recipe. That
/// is why there is no version field, and no rule for what an older build does with a newer kind.
/// <see cref="Generated"/> is written by the borrowing machine itself, from fields that the catalogue
/// already holds. So it is not sent either.</para>
/// </summary>
public abstract record PlaylistCover
{
    /// <summary>Drawn from the list itself: the first four distinct records on it, inset on a ground of
    /// their colours. This is the default cover.</summary>
    public sealed record Automatic : PlaylistCover;

    /// <summary>Four records the user chose, in the order they were chosen. That order is the layout:
    /// top-left, top-right, bottom-left, bottom-right.</summary>
    /// <param name="Folders">The album folders, as the tracks table spells them.</param>
    public sealed record Records(IReadOnlyList<string> Folders) : PlaylistCover;

    /// <summary>A picture from the user's own computer. Only the choice is stored here. The file has
    /// already been copied, and its original path is a fact about the user's disk, not about this
    /// playlist.</summary>
    public sealed record Picture : PlaylistCover;

    /// <summary>One of the generated covers: which of the eight styles, the seed that sets its colours,
    /// and whether it shows the advisory mark. The first two define the picture. The mark is a claim about
    /// the picture, so it is stored here as well as in the drawn bytes. A cover redrawn from its recipe,
    /// after a lost file or a rebuilt store, then still carries the same mark.</summary>
    public sealed record Figure(int Style, uint Seed, CoverMark Mark) : PlaylistCover;

    /// <summary>
    /// A borrowed shelf's copy of one of the owner's generated lists. It shows the same picture that the
    /// list shows on the owner's computer: the same photograph or drawn figure, in the same frame.
    ///
    /// <para><b>Set only by the local copy of a borrowed shelf.</b> The owner's generated lists arrive as
    /// fixed lists of records (see <c>BorrowedMoodLists</c>). So on a borrowed shelf they are stored as
    /// ordinary playlists. Without this cover they would be drawn as a quilt of four sleeves, while the
    /// owner's shelf shows the list's photograph. The picker never offers this cover, and the user's own
    /// tree never holds it.</para>
    ///
    /// <para><b>Kept on the node's own row, in the same insert.</b> The shelf's tree is rewritten whole on
    /// every change (<c>LibraryDatabase.MirrorPlaylistTree</c>). So a fact about a list must be stored on
    /// its own row. Stored anywhere else, it could be read without the row, or the row without it.</para>
    /// </summary>
    /// <param name="Id">Which generated list it is, as the owner's catalogue named it.</param>
    public sealed record Generated(SmartPlaylistId Id) : PlaylistCover;

    /// <summary>
    /// A list or folder on a borrowed shelf whose owner chose its picture. The owner's computer serves
    /// that picture (<c>SharingService.ArtFor</c>). The borrower asks for it and keeps a copy.
    ///
    /// <para><b>Set only by the local copy of a borrowed shelf, from the owner's listing</b>
    /// (<c>PeerPlaylist.Picture</c>). Without this cover, the borrower draws the automatic quilt for every
    /// list, whatever the owner chose. The owner's computer already serves the picture, but nothing on the
    /// borrowing side asks for it.</para>
    /// </summary>
    /// <param name="NodeId">The node's id in the owner's tree. The picture is requested by this id. The
    /// local copy stores the node under an id of its own, so the owner's id is kept here.</param>
    /// <param name="Stamp">A hash of the picture as the owner had it when the listing was built. The stored
    /// copy is filed under this hash, so a picture the owner has changed is fetched again.</param>
    public sealed record ChosenByOwner(long NodeId, string Stamp) : PlaylistCover;

    /// <summary>Separates the folders in a <see cref="Records"/> value. A vertical bar, which cannot
    /// occur in a path on any platform this app runs on.</summary>
    private const char Between = '|';

    public string ToWire() => this switch
    {
        Records r => "records:" + string.Join(Between, r.Folders),
        Picture => "pic",
        // The kind is stored by name, not by number, so the value still works if the enum is reordered.
        // The genre is last, so a genre that contains colons is read back whole.
        Generated g => $"gen:{g.Id.Kind}:{g.Id.Decade}:{g.Id.Genre ?? ""}",
        ChosenByOwner o => $"owner:{o.NodeId}:{o.Stamp}",
        // The mark is a fourth part, not a different prefix. It is left out when there is no mark, so
        // covers written before the mark existed are unchanged. An older build that does not know the
        // mark reads a marked cover as no cover at all, not as an unmarked one. That is the safer way to
        // fail. Showing "explicit" to a build that cannot draw the mark is worse than showing nothing.
        Figure { Mark: CoverMark.Explicit } f => $"fig:{f.Style}:{f.Seed}:x",
        Figure f => $"fig:{f.Style}:{f.Seed}",
        _ => "",
    };

    /// <summary>
    /// What was stored, or <see cref="Automatic"/> for anything that cannot be read.
    ///
    /// <para>An unreadable value becomes the automatic cover, instead of an exception. This runs on the
    /// playlist tree's load path. A value written by a build that knows one more kind should cost that
    /// playlist its chosen picture, not the whole tree.</para>
    /// </summary>
    public static PlaylistCover FromWire(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire))
            return new Automatic();

        if (wire == "pic")
            return new Picture();

        if (wire.StartsWith("records:", StringComparison.Ordinal))
        {
            var folders = wire["records:".Length..]
                .Split(Between, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            return folders.Count > 0 ? new Records(folders) : new Automatic();
        }

        if (wire.StartsWith("fig:", StringComparison.Ordinal))
        {
            var parts = wire.Split(':');
            if (parts.Length is not (3 or 4)
                || !int.TryParse(parts[1], out var style)
                || !uint.TryParse(parts[2], out var seed))
                return new Automatic();

            // A fourth part that is not the mark comes from a newer build that knows more kinds than this
            // one. The rule for those values is to fall back to the automatic cover rather than guess.
            if (parts.Length == 3)
                return new Figure(style, seed, CoverMark.None);

            return parts[3] == "x" ? new Figure(style, seed, CoverMark.Explicit) : new Automatic();
        }

        if (wire.StartsWith("gen:", StringComparison.Ordinal))
        {
            // Four parts at most, so a genre with a colon in it stays one genre.
            var parts = wire.Split(':', 4);
            if (parts.Length != 4
                || !Enum.TryParse<SmartPlaylistKind>(parts[1], ignoreCase: false, out var kind)
                || !Enum.IsDefined(kind)
                || !int.TryParse(parts[2], out var decade))
                return new Automatic();

            return new Generated(new SmartPlaylistId(kind, parts[3].Length > 0 ? parts[3] : null, decade));
        }

        if (wire.StartsWith("owner:", StringComparison.Ordinal))
        {
            var parts = wire.Split(':');
            return parts.Length == 3 && long.TryParse(parts[1], out var node) && parts[2].Length > 0
                ? new ChosenByOwner(node, parts[2])
                : new Automatic();
        }

        return new Automatic();
    }
}
