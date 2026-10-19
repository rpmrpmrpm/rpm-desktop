namespace RPM.DataGrid.Tree;

/// <summary>
/// Contract for items displayed hierarchically by the grid: <see cref="VirtualTreeModel"/> holds the
/// structure and <see cref="TreeGridSource"/> presents it as rows.
///
/// The grid itself stays flat. The model projects the tree into a row list (depth-first, respecting
/// <see cref="IsExpanded"/>), and the tree column draws the indent and the expander chevron.
/// </summary>
public interface ITreeGridRow
{
    /// <summary>Zero-based depth in the tree (roots are 0). Drives the indent.</summary>
    int Depth { get; }

    /// <summary>True when the row has children (shows the expander chevron).</summary>
    bool HasChildren { get; }

    /// <summary>
    /// Expand state. Setting it must raise INotifyPropertyChanged so the
    /// flattener (and the chevron glyph) react.
    /// </summary>
    bool IsExpanded { get; set; }
}
