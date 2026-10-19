namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Anything with a cloud mark that can be busy: a record in the album list, a track line inside it, or a
/// tile on the wall.
///
/// <para>This lets one breath drive a mixed set of them (see <see cref="CloudLineBreath"/>). A view shows a
/// record and its songs together, and both can be busy at once. A second clock for the lines would repaint
/// the window twice per beat for one breath. It would also put the record and its own track visibly out
/// of step.</para>
/// </summary>
internal interface ICloudBadged
{
    /// <summary>True while the mark is on its way up or on its way out. This is what breathes.</summary>
    bool IsCloudBusy { get; }

    /// <summary>The mark's opacity. It is written on every beat while the mark is busy. When the mark stops,
    /// it is set back to <see cref="RPM.Core.Cloud.CloudBadgePulse.Rest"/>.</summary>
    double CloudPulse { get; set; }
}
