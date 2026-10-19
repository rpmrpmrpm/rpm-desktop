using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Import;

/// <summary>The key to the marks in the queue's first two columns. Content only: it is shown from the
/// toolbar flyout and knows nothing about the pane it is opened over.</summary>
public sealed partial class QueueLegendPanel : UserControl
{
    public QueueLegendPanel() => InitializeComponent();
}
