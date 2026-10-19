using RPM.Desktop.Presentation.Common;
using Windows.UI;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// Every test class that holds a <see cref="PaletteStandIn"/> runs in one shared collection.
///
/// <para><see cref="RpmPalette.Override"/> is one static value shared by the whole process. This project
/// has no <c>xunit.runner.json</c> and no <c>[CollectionBehavior]</c>. Without <c>[Collection]</c>, each
/// test class is its own collection, and xunit runs collections in parallel. If two classes each hold the
/// stand-in during their own tests, one class's <see cref="PaletteStandIn.Dispose"/> can set
/// <c>Override</c> to null in the middle of the other class's draw. Every seed that class asks about then
/// gets an empty <c>RingColours</c>, and the drawing looks broken, although the fault is a scheduling clash
/// in a file the test never touches.</para>
///
/// <para>Every test class built on <see cref="PaletteStandIn"/> belongs in this one collection. The
/// collection is named for the seam, not for any one class, so xunit runs them one after another.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class PaletteStandInCollection
{
    /// <summary>The collection's name. Each class writes
    /// <c>[Collection(PaletteStandInCollection.Name)]</c>,
    /// so no class has to retype a string literal that must match exactly.</summary>
    public const string Name = "RpmPalette stand-in";
}

/// <summary>
/// The app's ten accent colours, handed to <see cref="RpmPalette"/> for the length of one test.
///
/// <para><see cref="RpmPalette.Colours"/> is read from <c>Assets/RpmPalette.xaml</c> through a
/// <see cref="Microsoft.UI.Xaml.ResourceDictionary"/>. That needs a dispatcher, and no test in this
/// project has one. See <see cref="RpmPalette.Override"/> for the details. Anything that draws in the
/// app's palette (a made sleeve, a generated cover, a playlist figure) needs a real answer from
/// <c>Colours</c> to be worth testing. So this stand-in provides the ten colours for the test that asks,
/// and gives them back on <see cref="Dispose"/>. An empty palette is the correct state for every other
/// test.</para>
///
/// <para>The ten values are copied from <see cref="RpmPaletteTests"/>, not shared with it. That file
/// tests the snapping rule against numbers it owns. Reaching into it for a constant would tie this
/// fixture to a file that has no reason to know about it.</para>
///
/// <para>One test at a time holding the stand-in is not enough on its own. See
/// <see cref="PaletteStandInCollection"/>, which every class using this must belong to.</para>
/// </summary>
public sealed class PaletteStandIn : IDisposable
{
    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private static readonly Color[] Ten =
    [
        Rgb(0xD2, 0x31, 0x0E), // Vermilion
        Rgb(0xCC, 0x6E, 0x11), // Rust
        Rgb(0xEF, 0xAF, 0x12), // Gold
        Rgb(0xAD, 0xD0, 0x38), // Lime
        Rgb(0x13, 0x4C, 0x27), // Forest
        Rgb(0x03, 0x71, 0xD0), // Azure
        Rgb(0x47, 0x50, 0xAE), // Indigo
        Rgb(0xAC, 0x52, 0x8D), // Magenta
        Rgb(0xEA, 0x15, 0x4E), // Crimson
        Rgb(0xEE, 0x07, 0x09), // Red
    ];

    public PaletteStandIn() => RpmPalette.Override = Ten;

    public void Dispose()
    {
        RpmPalette.Override = null;
        GC.SuppressFinalize(this);
    }
}
