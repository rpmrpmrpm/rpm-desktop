using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Keys that only the mouse needs. They are hidden while the remote or the keyboard drives Pegasus, and
/// they come back as soon as the mouse moves.
///
/// <para>Some keys in Pegasus exist only because a mouse cannot press Back. The player's back key is one:
/// it takes a mouse user to the collection, which the remote's Back key already does. For a listener on a
/// sofa with a remote, such a key is one more stop for the focus to pass on its way along the row, and one
/// more thing to read. So a key marked with <see cref="OnlyProperty"/> is collapsed while the remote is in
/// use, and shown again when the pointer is used.</para>
///
/// <para>Mark a key in XAML with <c>local:MouseKeys.Only="True"</c>. Nothing else is needed. The host
/// (PegasusPlayer.Remote) says when the remote takes over and when the pointer takes it back, through
/// <see cref="RemoteInUse"/>. A key that is marked while the remote is already in use is hidden at
/// once.</para>
///
/// <para><b>This writes the key's Visibility.</b> A key whose Visibility is also set by other code would
/// have two owners, and the last writer would win. Put such a key inside a panel, and mark the panel
/// instead.</para>
/// </summary>
public static class MouseKeys
{
    public static readonly DependencyProperty OnlyProperty = DependencyProperty.RegisterAttached(
        "Only", typeof(bool), typeof(MouseKeys), new PropertyMetadata(false, OnOnlyChanged));

    public static bool GetOnly(DependencyObject element) => (bool)element.GetValue(OnlyProperty);

    public static void SetOnly(DependencyObject element, bool value) => element.SetValue(OnlyProperty, value);

    /// <summary>Every marked key. They are held weakly, so a key that leaves the tree for good is not kept
    /// alive by this list.</summary>
    private static readonly List<WeakReference<UIElement>> Keys = [];

    /// <summary>Whether the remote or the keyboard is driving the view now. False until the first key.</summary>
    public static bool RemoteDriving { get; private set; }

    /// <summary>
    /// The remote took over (true), or the pointer took it back (false). Every marked key is hidden or
    /// shown. Called on the UI thread. Calling it again with the same answer does nothing.
    /// </summary>
    public static void RemoteInUse(bool remote)
    {
        if (RemoteDriving == remote)
            return;
        RemoteDriving = remote;

        // The list is walked backwards, so a key that has been collected can be taken out as it is met.
        for (var i = Keys.Count - 1; i >= 0; i--)
        {
            if (Keys[i].TryGetTarget(out var key))
                Apply(key);
            else
                Keys.RemoveAt(i);
        }
    }

    private static void OnOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement key)
            return;

        if (e.NewValue is true)
        {
            Keys.Add(new WeakReference<UIElement>(key));
            Apply(key);
            return;
        }

        // No longer marked: the key is shown and left alone from now on.
        Keys.RemoveAll(held => !held.TryGetTarget(out var other) || ReferenceEquals(other, key));
        key.Visibility = Visibility.Visible;
    }

    private static void Apply(UIElement key) =>
        key.Visibility = RemoteDriving ? Visibility.Collapsed : Visibility.Visible;
}
