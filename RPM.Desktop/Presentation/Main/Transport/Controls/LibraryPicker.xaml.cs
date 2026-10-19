using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>
/// How much room a <see cref="LibraryPicker"/>'s rows are given.
/// </summary>
/// <remarks>
/// <b>A property of the window the list is in, not of the list.</b> The same collections are offered
/// in a flyout on a full window and in a flyout on a 300-point player. A row that reads well in the
/// player can be lost in the full window.
/// </remarks>
public enum PickerLook
{
    /// <summary>No look was stated. This is not a real look. <see cref="LibraryPicker.Show"/> refuses it.</summary>
    Unstated,

    /// <summary>The roomy look: a 38-point tile lit with the app's gradient on the row you are standing in,
    /// the name at 13.5 semibold, and the state on its own line. Used by the main window.</summary>
    Roomy,

    /// <summary>The compact look: rows of the data grid, 24 points high, flush, banded, with the grid's
    /// selection wash and its cyan bar. Used by the player.</summary>
    Compact,
}

/// <summary>
/// The list of collections this computer can be standing in, as a control of its own.
///
/// <para>It is shown in two places: the deck's share button, and the player's library pane. The rows
/// are <see cref="SharedLibraryRow"/> and are built by <see cref="SharedLibraryRow.Build"/> in both.
/// This control draws them and reports which one was picked, and nothing else. What a pick means (the
/// sign-in a borrowed shelf needs, the reload that follows) belongs to the host, because each host
/// handles it differently.</para>
///
/// <para><b>The host states how big the rows are drawn</b>, through <see cref="Look"/>. The markup
/// contains both looks and this picks between them. See the note on the XAML for what each look is.</para>
/// </summary>
public sealed partial class LibraryPicker : UserControl
{
    public LibraryPicker() => InitializeComponent();

    /// <summary>A row was clicked, with the computer it names, or empty for the user's own.</summary>
    public event Action<string>? Picked;

    /// <summary>
    /// Which look this list is drawn in. There is no default. The host must state one.
    /// </summary>
    /// <remarks>
    /// <b>Stated by each host and never inferred.</b> A look that suits one window is easy to apply to
    /// another window by mistake, so there is no fallback. <see cref="Show"/> refuses
    /// <see cref="PickerLook.Unstated"/>, so a new host must answer the question itself.
    ///
    /// <para>This is a dependency property, so the hosts set it in their markup, next to the control. It is
    /// applied on every change, not only on load. It can be set in markup before the tree is up, so reading it
    /// only at load would miss the value.</para>
    /// </remarks>
    public static readonly DependencyProperty LookProperty = DependencyProperty.Register(
        nameof(Look), typeof(PickerLook), typeof(LibraryPicker),
        new PropertyMetadata(PickerLook.Unstated, OnLookChanged));

    public PickerLook Look
    {
        get => (PickerLook)GetValue(LookProperty);
        set => SetValue(LookProperty, value);
    }

    private static void OnLookChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is LibraryPicker picker)
            picker.Dress();
    }

    /// <summary>
    /// Applies the look: the row template, and the surface the rows stand on.
    /// </summary>
    /// <remarks>
    /// The surface is part of the look. The compact list is a table, so it uses the grid's own fill and edge,
    /// which makes it look like one. The roomy list is a stack of cards, so it has no surface of its own and
    /// stands on the flyout's own ground. A card list on a table's surface would be a panel inside a panel.
    ///
    /// <para>Both surfaces are read from this control's own resources by key. Anything that comes from the app,
    /// such as the grid's fill and edge, is resolved in the markup, where the parser looks up to the
    /// application dictionary. A <c>Resources[...]</c> lookup in code does not look that far. Reaching for an
    /// app-wide brush from this method throws the first time the compact list is styled.</para>
    /// </remarks>
    private void Dress()
    {
        switch (Look)
        {
            case PickerLook.Roomy:
                Rows.ItemTemplate = (DataTemplate)Resources["RoomyTemplate"];
                Surface.Style = (Style)Resources["RoomySurface"];
                break;

            case PickerLook.Compact:
                Rows.ItemTemplate = (DataTemplate)Resources["CompactTemplate"];
                Surface.Style = (Style)Resources["CompactSurface"];
                break;

            default:
                // Left unstyled on purpose. Show checks for this and reports it. Drawing a default here would let
                // this control choose the look for the host.
                Rows.ItemTemplate = null;
                break;
        }
    }

    /// <summary>
    /// Shows these collections, in this order.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Look"/> was never stated. The method throws rather than using a default. Both hosts open
    /// their flyout on every run of the app, so the fault shows up at once. A silent fallback could go
    /// unnoticed for a long time.
    /// </exception>
    public void Show(List<SharedLibraryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (Look is PickerLook.Unstated)
        {
            throw new InvalidOperationException(
                "LibraryPicker.Look was not stated. Set it where the control is declared: Roomy in a "
                + "full window, Compact in the player. See the note on the XAML.");
        }

        Rows.ItemsSource = rows;
    }

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string deviceId })
            Picked?.Invoke(deviceId);
    }
}
