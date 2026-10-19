using System.Collections;
using System.Collections.Generic;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The empty list that every repeater inside a recycled template falls back to. Its ItemsSource is never
/// null.
///
/// <para>Uno's ItemsRepeater cannot be given a null ItemsSource after it has been unloaded and loaded
/// again. OnUnloaded drops its collection-changed revoker, but it keeps the view that the revoker was made
/// from. The next OnLoaded subscribes again, through a lambda that reads the FIELD at dispose time
/// (ItemsRepeater.cs:653 in 6.6). Meanwhile OnDataSourcePropertyChanged sets that same field to null before
/// it disposes the old revoker. So a null comes in, and the unsubscribe dereferences null. The rest of the
/// property change is then skipped. That includes the reset that the layout needs. The previous collection
/// also stays wired to the repeater for good. The error is logged, not thrown, so it looks like one line in
/// the console and nothing more.</para>
///
/// <para>A repeater bound inside an item template is exactly the case that hits this. The container is
/// recycled, and the DataContext it inherits goes with it. A binding with nothing to resolve then pushes the
/// property's default, which is null. A fallback that is a real, empty list keeps that path closed. The
/// revoker made in OnDataSourcePropertyChanged captures its own local variable, so it never has the bug. So
/// this fallback only matters on the load path.</para>
///
/// <para><b>This class must stay partial, because native code reads it.</b> The repeater reads it through
/// WinRT's list interfaces. In a Native AOT build, CsWinRT only generates those for a partial class. If the
/// class were not partial, the fallback could not be assigned to ItemsSource. The first template that loaded
/// without data would then fail with "Failed to assign to property ItemsRepeater.ItemsSource", and the app
/// would crash.</para>
/// </summary>
public sealed partial class NoItems : IReadOnlyList<object>
{
    public int Count => 0;

    public object this[int index] => throw new System.IndexOutOfRangeException();

    public IEnumerator<object> GetEnumerator() { yield break; }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
