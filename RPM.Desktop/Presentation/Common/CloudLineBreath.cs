namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The breath (a pulsing opacity) on the cloud marks that a view shows. The marks are its records,
/// its track lines, or both. While any mark is busy, this joins the one shared
/// <see cref="BadgeBreath"/> clock. When none is busy, it leaves the clock and stops it at once.
///
/// <para>There is one of these per view. The models do not breathe on their own. A line is made and
/// dropped by whichever list shows it. A model that joined the shared clock itself would keep the
/// clock running after its band closed during an upload. That would repaint the window twenty times
/// a second for a row nobody can see. The view knows when its marks go away. The marks do not.</para>
///
/// <para>There is one for a view's records and their songs together, not one each. They are shown at
/// once and can be busy at once. Two clocks would cost two repaints per beat for one breath. They
/// would also let a record and its own track pulse out of step.</para>
/// </summary>
/// <param name="marks">The view's current marks, read fresh on every call. The set is rebuilt
/// whenever a record is opened, read or re-badged.</param>
internal sealed class CloudLineBreath(Func<IEnumerable<ICloudBadged>> marks)
{
    private bool _breathing;

    /// <summary>
    /// The marks that were breathing when the set was last read.
    ///
    /// <para>The set is held, not read again on every beat. <paramref name="marks"/> walks everything
    /// the view holds. In the album list that is every record ever scrolled past, and every line of
    /// each one. The beat runs twenty times a second. If the set were walked on each beat, an hour of
    /// browsing would make the breath the slowest thing on the screen. The set only changes when a
    /// mark changes, and that is when <see cref="Sync"/> is called.</para>
    /// </summary>
    private ICloudBadged[] _busy = [];

    /// <summary>Call after the marks change or are re-read. UI thread only.</summary>
    public void Sync()
    {
        _busy = [.. marks().Where(m => m.IsCloudBusy)];
        var wanted = _busy.Length > 0;
        if (wanted == _breathing)
            return;

        if (wanted)
        {
            _breathing = true;
            BadgeBreath.Join(OnTick);
        }
        else
        {
            Stop();
        }
    }

    /// <summary>Leaves the clock and sets every mark back to full. Call on unload too. A view that
    /// goes away while a track is uploading must not leave the timer running.</summary>
    public void Stop()
    {
        if (!_breathing)
            return;
        _breathing = false;
        BadgeBreath.Leave(OnTick);
        _busy = [];

        foreach (var mark in marks())
            mark.CloudPulse = RPM.Core.Cloud.CloudBadgePulse.Rest;
    }

    /// <summary>Checks again whether each mark is busy. A mark can stop being busy between two syncs.
    /// It must settle to rest, not keep breathing until the next sync.</summary>
    private void OnTick(double opacity)
    {
        foreach (var mark in _busy)
            if (mark.IsCloudBusy)
                mark.CloudPulse = opacity;
    }
}
