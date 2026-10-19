using RPM.Core.Library;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The Delete submenu. This is the only place it is built.
///
/// <para>It always has three entries, in the order of what each copy costs the listener: the files on
/// this disk, the copy in the cloud, then both. A greyed entry is information. A greyed "Delete from
/// cloud" says the record is not in the cloud. A menu that simply left the line out would not tell the
/// listener that.</para>
///
/// <para>One builder serves the record's menu, the song's menu and the library table. The promise covers
/// all three: the same three words, in the same order, do the same thing to one file or to twelve.</para>
/// </summary>
public static class DeleteMenu
{
    /// <summary>The lid-and-bin icon used on every Delete in the app. It is on the submenu that opens the
    /// three entries.</summary>
    private const string TrashGlyph = "\uf2ed";

    /// <summary>A disk for the files on this computer, a cloud for the copy in the cloud, and the plain
    /// bin for the one that takes both. Each action has its own icon. If the entries shared one icon, the
    /// submenu would have to be read word by word.</summary>
    private const string DiskGlyph = "\uf0a0";
    private const string CloudGlyph = "\uf0c2";
    private const string BothGlyph = "\uf1f8";

    /// <summary>A new submenu, for the menus that are built in code.</summary>
    public static MenuFlyoutSubItem Build(
        DeleteOffer offer, string label, FontFamily solid,
        Action onLocal, Action onCloud, Action onEverywhere)
    {
        var item = new MenuFlyoutSubItem();
        Fill(item, offer, label, solid, onLocal, onCloud, onEverywhere);
        return item;
    }

    /// <summary>
    /// The same submenu, written into one declared in XAML. It is cleared and rebuilt each time the menu
    /// opens.
    ///
    /// <para>It is rebuilt, not just re-enabled, because the offer is read at the moment of the
    /// right-click. A record whose upload has just finished, or whose files were deleted from another
    /// view, must not show entries from an earlier state.</para>
    /// </summary>
    public static void Fill(
        MenuFlyoutSubItem item, DeleteOffer offer, string label, FontFamily solid,
        Action onLocal, Action onCloud, Action onEverywhere)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.Text = label;
        item.Icon = Icon(TrashGlyph, solid);
        item.FontSize = 12;
        item.MinHeight = 26;
        item.Padding = new Thickness(11, 3, 11, 4);

        item.Items.Clear();
        item.Items.Add(Entry(DeletePrompt.ActionKey(DeleteReach.Local), DiskGlyph, offer.Local, onLocal));
        item.Items.Add(Entry(DeletePrompt.ActionKey(DeleteReach.Cloud), CloudGlyph, offer.Cloud, onCloud));
        item.Items.Add(
            Entry(DeletePrompt.ActionKey(DeleteReach.Everywhere), BothGlyph, offer.Everywhere, onEverywhere));

        MenuFlyoutItem Entry(string locKey, string glyph, bool enabled, Action onClick)
        {
            var entry = new MenuFlyoutItem
            {
                Text = Loc.Get(locKey), Icon = Icon(glyph, solid), IsEnabled = enabled,
                FontSize = 12, MinHeight = 26, Padding = new Thickness(11, 3, 11, 4),
            };
            entry.Click += (_, _) => onClick();
            return entry;
        }
    }

    /// <summary>FaMenuIcon sets the glyph in a 16x16 box, so the template's Viewbox has nothing left to
    /// stretch. Every other menu icon in the app uses the same style.</summary>
    private static FontIcon Icon(string glyph, FontFamily solid) => new()
    {
        Glyph = glyph,
        Style = (Style)Application.Current.Resources["FaMenuIcon"],
        FontFamily = solid,
    };
}
