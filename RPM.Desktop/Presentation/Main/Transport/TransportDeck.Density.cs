namespace RPM.Desktop.Presentation.Main.Transport;

/// <summary>
/// How much of the deck fits. The bar holds three groups at fixed sizes: the pane pill on the left, the
/// transport in the exact centre, and the state chrome on the right. On a narrow window there is not room
/// for all three. The middle stays centred, and the two edges run under it or off it.
///
/// <para>Things are removed in order of how little they are worth on a small window. Cast goes first. It
/// sits where the volume readout ends, so those two meet before anything else does. Then the volume slider
/// gives up a third of its length and leaves altogether. The two keys on either side still change the volume
/// without it, five at a time. Then the view picker goes. Then the pane pill and the menu, which can be
/// reached elsewhere in the window. Last, the whole volume group goes: the mark, the keys and the readout.
/// That leaves the four keys the bar exists for: stop, back, play and forward.</para>
///
/// <para>Every width below is a fixed value from the deck's markup. So the space the deck needs at a given
/// step does not depend on how it looks right now. Hiding a pill changes the demand, never the width on
/// offer. This stops a resize from flipping between two steps.</para>
/// </summary>
internal sealed partial class TransportDeck
{
    /// <summary>The transport bar's own spacing between keys.</summary>
    private const double DeckSpacing = 8;

    /// <summary>The spacing the two edge stacks set between their pills.</summary>
    private const double DeckPillSpacing = 10;

    /// <summary>Space kept between the centred transport and each edge group, so the deck reads as three groups,
    /// not one long row.</summary>
    private const double DeckClearance = 16;

    /// <summary>A transport key (34), the play orb (50), and the volume keys' narrower box (26).</summary>
    private const double TransportKeyWidth = 34;
    private const double TransportPlayWidth = 50;
    private const double VolumeKeyWidth = 26;

    /// <summary>The thin rule between the transport and the volume group: 1 point of line, with 4 points of space
    /// on either side.</summary>
    private const double VolumeRuleWidth = 9;

    /// <summary>The speaker, Bluetooth and cast mark: 16 points, in a box with 2 points of space on either side.</summary>
    private const double VolumeMarkWidth = 20;

    /// <summary>The volume slider at rest, and the length it gives up before it is removed.</summary>
    private const double VolumeSliderWidth = 92;
    private const double VolumeSliderShortWidth = 60;

    /// <summary>The percentage readout, fixed so a drag cannot drag the bar sideways with it.</summary>
    private const double VolumeReadoutWidth = 30;

    /// <summary>A deck pill: a 16-wide mark, 14 of padding either side, and the hairline.</summary>
    private const double DeckPillWidth = 46;

    /// <summary>The pane pill is wider. Its mark is the little window, 26 across.</summary>
    private const double PanePillWidth = 56;

    /// <summary>The smallest step: only the four transport keys.</summary>
    private const int DeckStageFloor = 6;

    /// <summary>Which step the deck is on. -1 until the first layout has been measured.</summary>
    private int _deckStage = -1;

    private void DeckRow_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyDeckDensity();

    /// <summary>The chips appear and disappear with a rip, a share, and a cloud upload. Each one that appears takes
    /// width from the same row, so the deck checks again what it can hold when they change.</summary>
    private void DeckChrome_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyDeckDensity();

    /// <summary>Puts the deck on the widest step this window can hold.</summary>
    private void ApplyDeckDensity()
    {
        var available = DeckRow.ActualWidth - DeckRow.Padding.Left - DeckRow.Padding.Right;
        if (available <= 0)
            return;

        var stage = 0;
        while (stage < DeckStageFloor && DeckRowWidth(stage) > available)
            stage++;

        if (stage == _deckStage)
            return;
        _deckStage = stage;

        // Cast goes first, before the slider is shortened. It is the one pill that sits right where the volume
        // readout ends. The two touch each other before anything else gets tight.
        DeckCastButton.Visibility = Vis(stage < 1);

        VolumeSlider.Width = stage >= 2 ? VolumeSliderShortWidth : VolumeSliderWidth;
        VolumeSlider.Visibility = Vis(stage < 3);

        ViewButton.Visibility = Vis(stage < 4);

        PanePill.Visibility = Vis(stage < 5);
        MenuPill.Visibility = Vis(stage < 5);

        // The mark, the two keys and the readout form one group, with the rule in front of it. On the last step the
        // whole volume half of the bar goes, and only the transport is left.
        var volume = Vis(stage < 6);
        VolumeRule.Visibility = volume;
        VolumeMark.Visibility = volume;
        VolumeDown.Visibility = volume;
        VolumeUp.Visibility = volume;
        VolumeReadout.Visibility = volume;

        static Visibility Vis(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The space the deck needs at a given step: the transport, the two edge groups, and the space between
    /// them. The chips are measured, not assumed. Their width depends on the rip or upload, and it is the same at
    /// every step.</summary>
    private double DeckRowWidth(int stage)
    {
        // Stop, back, play, forward: the four that never leave.
        var transport = TransportKeyWidth * 3 + TransportPlayWidth + DeckSpacing * 3;

        if (stage < 6)
        {
            transport += VolumeRuleWidth + VolumeMarkWidth + VolumeKeyWidth * 2 + VolumeReadoutWidth
                       + DeckSpacing * 4;
            if (stage < 3)
                transport += (stage >= 2 ? VolumeSliderShortWidth : VolumeSliderWidth) + DeckSpacing;
        }

        var left = stage < 5 ? PanePillWidth : 0;

        var right = ChipsWidth();
        if (stage < 1)
            right += DeckPillWidth + DeckPillSpacing;   // cast
        if (stage < 4)
            right += DeckPillWidth + DeckPillSpacing;   // the view picker
        if (stage < 5)
            right += DeckPillWidth + DeckPillSpacing;   // the menu

        // The transport is centred by a Grid whose two edge columns are stars. Whatever is left after the middle
        // is split evenly between them, however unequal the two groups are. The right edge has three pills to
        // the left edge's one, so it runs out of room first. A row that adds up to less than the deck does not
        // show whether it fits. The wider group must fit in half of the leftover. The other side's spare width is
        // not available to it.
        var edge = Math.Max(left, right);
        return transport + (edge > 0 ? (edge + DeckClearance) * 2 : 0);
    }

    /// <summary>The width the rip, share and cloud chips take from the right edge right now. A chip that has nothing
    /// to show takes zero, which is most of the time.</summary>
    private double ChipsWidth()
    {
        var width = 0d;
        foreach (var chip in new FrameworkElement[] { DeckRipChip, DeckShareChip, DeckCloudChip })
        {
            if (chip.Visibility == Visibility.Visible && chip.ActualWidth > 0)
                width += chip.ActualWidth + DeckPillSpacing;
        }
        return width;
    }
}
