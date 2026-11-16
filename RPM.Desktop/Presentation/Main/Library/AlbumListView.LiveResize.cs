using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The list while a pane resizes it live, one frame at a time.
/// </summary>
public sealed partial class AlbumListView
{
    /// <summary>
    /// A pane is about to resize the list live. The records the list keeps ready off screen keep the width
    /// they have now until <see cref="EndLiveResize"/>.
    /// </summary>
    /// <remarks>
    /// The list re-flows on every frame of the move, because its width changes. The stock panel keeps
    /// about twenty records realized, four or five of them in view, and every realized record is measured
    /// again at the new width on each frame: its heading, its keys and its chips. The ones out of view were
    /// most of that work, for records nobody could see. Giving them a width of their own makes each one a
    /// measure that answers from what it already knows.
    ///
    /// <para>Asking the panel for a smaller cache does not help. It gives the records back only when the
    /// thread is idle, and the thread is not idle during a move.</para>
    ///
    /// <para>The records in view are left alone, so they re-flow with the move. The held ones are given
    /// back at the landing, laid out once at the width the list keeps. Nothing scrolls in a quarter of a
    /// second, and a record's height does not depend on its width, so the set of records in view does not
    /// change during the move.</para>
    ///
    /// <para>WinUI only. On the Skia head the list is left as it is.</para>
    /// </remarks>
    public void BeginLiveResize()
    {
#if RPM_WINUI
        if (_heldRecords.Count > 0 || Visibility != Visibility.Visible)
            return;
        if (EntriesList.ItemsPanelRoot is not ItemsStackPanel panel || panel.FirstVisibleIndex < 0)
            return;

        for (var i = panel.FirstCacheIndex; i <= panel.LastCacheIndex; i++)
        {
            if (i >= panel.FirstVisibleIndex && i <= panel.LastVisibleIndex)
                continue;
            if (EntriesList.ContainerFromIndex(i) is not ListViewItem { ContentTemplateRoot: FrameworkElement record }
                || record.ActualWidth <= 0)
                continue;
            record.Width = record.ActualWidth;
            record.HorizontalAlignment = HorizontalAlignment.Left;
            _heldRecords.Add(record);
        }
#endif
    }

    /// <summary>The move has landed. The held records take the list's width again.</summary>
    public void EndLiveResize()
    {
#if RPM_WINUI
        foreach (var record in _heldRecords)
        {
            record.Width = double.NaN;
            record.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        _heldRecords.Clear();
#endif
    }

    /// <summary>The records held at their width for the length of a live resize. See
    /// <see cref="BeginLiveResize"/>.</summary>
    private readonly List<FrameworkElement> _heldRecords = [];
}
