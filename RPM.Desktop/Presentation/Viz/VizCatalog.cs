namespace RPM.Desktop.Presentation.Viz;

/// <summary>One visualizer as the gallery shows it. It has a still picture of what the visualizer looks like,
/// its name, a line about what it does, and the id that the full-screen host uses to start it. Each visualizer
/// is its own stage class, so the id is all the host needs. There is no shader text to carry.</summary>
// This is partial because the Uno.Extensions key-equality generator adds its IKeyEquatable half. The generator
// treats a record with an Id as keyed.
[RpmBindable]
public sealed partial record VizEntry(
    string Id,
    string Name,
    string Description,
    string Preview);

/// <summary>
/// The visualizers the app can run. The Visualizations pane lists these. The full-screen host is given a
/// <see cref="VizEntry.Id"/>, and it builds the matching stage. Localized text is read on every access, so a
/// language change is picked up the next time the pane is shown.
///
/// <para>Pegasus is not listed here. It is one of the app's three views, not one visualizer among several. It
/// is opened from the deck's view menu, not from a gallery card, and it has its own host
/// (<see cref="Presentation.Pegasus.PegasusPlayer"/>).</para>
/// </summary>
public static class VizCatalog
{
    /// <summary>The built-in OpenGL ribbon sea (<see cref="RibbonStage"/>).</summary>
    public const string RibbonSea = "ribbon-sea";

    /// <summary>The instanced flight through the block field (<see cref="BlockFlowStage"/>).</summary>
    public const string LowFrequencyFlow = "low-frequency-flow";

    /// <summary>The polar spectrogram drawn as a flower (<see cref="BloomStage"/>).</summary>
    public const string Bloom = "bloom";

    /// <summary>Chladni's plate of sand (<see cref="ChladniStage"/>).</summary>
    public const string Chladni = "chladni";

    /// <summary>The spectrum mirrored into four (<see cref="ButterflyStage"/>).</summary>
    public const string Butterfly = "butterfly";

    /// <summary>The folded tunnel, cut to the record's own beat grid (<see cref="TunnelStage"/>).</summary>
    public const string Tunnel = "tunnel";

    /// <summary>Two mirrors and one strut (<see cref="KaleidoscopeStage"/>).</summary>
    public const string Kaleidoscope = "kaleidoscope";

    /// <summary>The slice through a four-dimensional lattice (<see cref="LatticeStage"/>).</summary>
    public const string Lattice = "lattice";

    /// <summary>
    /// Every visualizer, for the gallery's ItemsSource.
    ///
    /// <para><b>A List, not an IReadOnlyList.</b> A collection expression that is returned as
    /// IReadOnlyList becomes a type that the compiler creates. Under Native AOT, CsWinRT gives the WinRT
    /// list interfaces only to collection types that it can see being passed to a WinRT property. With the
    /// generated type, the repeater rejected the source with "Argument 'source' is not a supported vector".
    /// That crashed the Windows release when a restored session opened the Visualizations pane. Keep this
    /// a List.</para>
    /// </summary>
    public static List<VizEntry> All =>
    [
        new(RibbonSea,
            Strings.Loc.Get("Viz_RibbonSeaName"),
            Strings.Loc.Get("Viz_RibbonSeaDesc"),
            "ms-appx:///Assets/Viz/ribbon-sea.jpg"),

        new(LowFrequencyFlow,
            Strings.Loc.Get("Viz_LowFlowName"),
            Strings.Loc.Get("Viz_LowFlowDesc"),
            "ms-appx:///Assets/Viz/low-frequency-flow.jpg"),

        new(Bloom,
            Strings.Loc.Get("Viz_BloomName"),
            Strings.Loc.Get("Viz_BloomDesc"),
            "ms-appx:///Assets/Viz/bloom.jpg"),

        new(Chladni,
            Strings.Loc.Get("Viz_ChladniName"),
            Strings.Loc.Get("Viz_ChladniDesc"),
            "ms-appx:///Assets/Viz/chladni.jpg"),

        new(Butterfly,
            Strings.Loc.Get("Viz_ButterflyName"),
            Strings.Loc.Get("Viz_ButterflyDesc"),
            "ms-appx:///Assets/Viz/butterfly.jpg"),

        new(Tunnel,
            Strings.Loc.Get("Viz_TunnelName"),
            Strings.Loc.Get("Viz_TunnelDesc"),
            "ms-appx:///Assets/Viz/tunnel.jpg"),

        new(Kaleidoscope,
            Strings.Loc.Get("Viz_KaleidoscopeName"),
            Strings.Loc.Get("Viz_KaleidoscopeDesc"),
            "ms-appx:///Assets/Viz/kaleidoscope.jpg"),

        new(Lattice,
            Strings.Loc.Get("Viz_LatticeName"),
            Strings.Loc.Get("Viz_LatticeDesc"),
            "ms-appx:///Assets/Viz/lattice.jpg"),
    ];

    /// <summary>The entry with this id, or the first entry when there is no match. The gallery is the only
    /// caller. So an unknown id means that the catalogue has changed. It is not a user error.</summary>
    public static VizEntry Find(string? id)
    {
        var all = All;
        foreach (var entry in all)
            if (entry.Id == id)
                return entry;
        return all[0];
    }
}
