namespace RPM.Core.Library;

/// <summary>Numbers a library's albums in the order that the folder tree lays them out.
///
/// An album is a folder with tracks directly in it. That is not a guess: the library's shape is
/// Artist/Album/track (see <see cref="Import.NamingTemplate"/>), so a folder that holds audio is an album,
/// and a folder that holds only other folders is a container, such as an artist or a box set.
///
/// The walk is generic over the node type. The tree must match the one the presentation draws, so the rule
/// lives here in one place and can be tested directly. Each caller only says how to read a node.
/// </summary>
public static class AlbumOrder
{
    /// <summary>The albums in tree order.
    ///
    /// Pre-order: a folder's own row comes before the rows of its subfolders, as the rows appear in the
    /// view.
    ///
    /// <paramref name="rootTrackCount"/> is the number of loose tracks at the library root. They have no
    /// folder node of their own. In the tree they come last, after every folder, because a folder's tracks
    /// follow its subfolders. So the root is the last album and uses the empty string as its key.
    /// </summary>
    public static List<string> Build<TNode>(
        IEnumerable<TNode> roots,
        Func<TNode, string?> folderOf,
        Func<TNode, int> directTrackCountOf,
        Func<TNode, IEnumerable<TNode>> childrenOf,
        int rootTrackCount)
    {
        var albums = new List<string>();

        void Walk(TNode node)
        {
            if (folderOf(node) is { } folder && directTrackCountOf(node) > 0)
                albums.Add(folder);

            foreach (var child in childrenOf(node))
                Walk(child);
        }

        foreach (var root in roots)
            Walk(root);

        if (rootTrackCount > 0)
            albums.Add("");

        return albums;
    }
}
