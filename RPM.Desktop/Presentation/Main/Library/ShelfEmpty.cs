using RPM.Desktop.Strings;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// Why an album view has nothing to show. The view cannot tell the cases apart from the data: an
/// empty collection and a search with no matches both have no records, but they need different
/// messages. Whoever applies the filter sets the reason on every feed.
/// </summary>
public enum ShelfEmpty
{
    /// <summary>Nothing has been added yet, so the empty shelf is an invitation.</summary>
    Collection,

    /// <summary>There is a search in the box and no record answers it.</summary>
    Search,

    /// <summary>The shelf has not been read from the open collection yet (the first read, or after the
    /// collection was reopened or switched). Nothing is said: the screen's loader is shown instead.
    /// An empty message here would claim that the collection is empty when nobody has read it yet.</summary>
    Reading,
}

/// <summary>The face an empty shelf wears. Kept in one place because the wall and the album list show
/// the same shelf in two ways, and only one of them is on screen at a time.</summary>
internal static class ShelfEmptyHint
{
    /// <summary>The glyph and the string key an empty shelf shows for this reason. Kept apart from the
    /// controls so the rule can be read on its own. An empty collection gets a struck-through note. A
    /// search with no results gets a plain note, because nothing is missing in that case.</summary>
    public static (string Glyph, string Key) Face(ShelfEmpty empty) => empty is ShelfEmpty.Search
        ? ("\uf001", "Library_NoSearchResults")
        : ("\uf87d", "Library_Empty");

    public static void Dress(ShelfEmpty empty, FontIcon icon, TextBlock line)
    {
        var (glyph, key) = Face(empty);
        icon.Glyph = glyph;
        line.Text = Loc.Get(key);
    }
}
