using System.Collections.Generic;
using System.Linq;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// One row of the tree down the left of the library pane: a list of songs, or a heading that holds
/// some.
/// </summary>
/// <remarks>
/// A heading is a node with children and nothing else: it opens and closes, and picking it picks
/// nothing. Whether a node is a heading is decided by <see cref="Children"/>, not by a flag. A node
/// with no children can therefore never be treated as a heading.
/// </remarks>
public sealed class AmpNode
{
    /// <summary>The row's text: a list's name in the user's language, or a playlist's name as it is.
    /// A phrase rather than plain text, so the tree can be built where the string table
    /// cannot be read.</summary>
    public required Phrase Label { get; init; }

    /// <summary>Its Font Awesome code point.</summary>
    public required string Glyph { get; init; }

    /// <summary>Which list of the collection it opens. Meaningless on a heading.</summary>
    public AmpScope Scope { get; init; }

    /// <summary>Which playlist it opens, for the rows under Playlists. Zero otherwise.</summary>
    public long PlaylistId { get; init; }

    /// <summary>The nesting level: 0 for a heading, 1 for a row under one.</summary>
    public int Depth { get; init; }

    /// <summary>The rows under it. Empty for a list.</summary>
    public IReadOnlyList<AmpNode> Children { get; init; } = [];

    /// <summary>Whether its children are showing. Headings start open, so the tree shows what it
    /// holds.</summary>
    public bool IsExpanded { get; set; } = true;

    /// <summary>Whether this row is a heading rather than a list.</summary>
    public bool IsGroup => Children.Count > 0;
}

/// <summary>
/// The library pane's tree: which lists it offers, and which rows are showing.
///
/// <para>The tree follows Winamp's media library tree, but offers only the lists this collection can
/// answer. Video, Online Services, Devices, Podcast Directory, Subscriptions, Bookmarks, History and
/// the disc drive are left out. RPM holds no data for them, and a row that always opens an empty list
/// is worse than no row.</para>
/// </summary>
public static class AmpNav
{
    /// <summary>The tree. The collection's playlists are listed under the Playlists heading.</summary>
    public static IReadOnlyList<AmpNode> Tree(IEnumerable<(string Name, long Id)> playlists)
    {
        ArgumentNullException.ThrowIfNull(playlists);

        return
        [
            new AmpNode
            {
                Label = new Phrase("Amp_NavLocal", ""),
                Glyph = "",
                Children =
                [
                    Leaf("Amp_NavAudio", "", AmpScope.Audio),
                    Leaf("Amp_NavMostPlayed", "", AmpScope.MostPlayed),
                    Leaf("Amp_NavRecentlyAdded", "", AmpScope.RecentlyAdded),
                    Leaf("Amp_NavRecentlyModified", "", AmpScope.RecentlyModified),
                    Leaf("Amp_NavRecentlyPlayed", "", AmpScope.RecentlyPlayed),
                    Leaf("Amp_NavNeverPlayed", "", AmpScope.NeverPlayed),
                    Leaf("Amp_NavFavorites", "", AmpScope.Favorites),
                ],
            },
            new AmpNode
            {
                Label = new Phrase("Amp_NavPlaylists", ""),
                Glyph = "",
                Children =
                [
                    .. playlists.Select(p => new AmpNode
                    {
                        Label = Phrase.AsIs(p.Name),
                        Glyph = "",
                        Scope = AmpScope.Playlist,
                        PlaylistId = p.Id,
                        Depth = 1,
                    }),
                ],
            },
        ];
    }

    /// <summary>The rows the tree is currently showing: every heading, and the children of the ones
    /// that are open.</summary>
    public static List<object> Rows(IReadOnlyList<AmpNode> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var rows = new List<object>();
        foreach (var node in tree)
        {
            rows.Add(node);
            if (!node.IsExpanded)
                continue;

            foreach (var child in node.Children)
                rows.Add(child);
        }

        return rows;
    }

    /// <summary>The first list in the tree, which is what the pane opens on.</summary>
    public static AmpNode? First(IReadOnlyList<AmpNode> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.SelectMany(n => n.Children).FirstOrDefault();
    }

    private static AmpNode Leaf(string key, string glyph, AmpScope scope) =>
        new() { Label = new Phrase(key, ""), Glyph = glyph, Scope = scope, Depth = 1 };
}
