using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The three pictures on the sleeve stage: the sleeve, the disc scan, and the pressing behind them.
/// Each one is cut to the exact size it is drawn at, using the smooth bicubic filter, and cut again
/// when that size changes. See <see cref="RemoteImage.DecodeArtAsync"/> for how a cut is made.
/// </summary>
public sealed partial class AlbumDetailView
{
    /// <summary>One picture as it is on screen: the record it belongs to, the bytes it was cut from,
    /// and the pixel edge it was cut at. The bytes are kept so the picture can be cut again at a new
    /// size. The edge lets a cut that finishes late check whether it is still the picture on
    /// screen.</summary>
    private sealed record StageCut(string Folder, byte[] Bytes, int Edge);

    /// <summary>Says which of the three pictures a cut is for. Each one has its own element and its own
    /// remembered cut.</summary>
    private enum StagePicture { Sleeve, DiscScan, Pressing }

    private static readonly StagePicture[] StagePictures =
        [StagePicture.Sleeve, StagePicture.DiscScan, StagePicture.Pressing];

    private StageCut? _sleeveCut;
    private StageCut? _discScanCut;
    private StageCut? _pressingCut;

    /// <summary>Delays the re-cut until a resize has settled. A drag changes the sleeve's edge on
    /// nearly every pixel, and each cut costs three decodes and three encodes.</summary>
    private DispatcherTimer? _stageRecut;

    /// <summary>How long the sleeve's size must stay the same before its pictures are cut again. Until
    /// then the last cut is drawn scaled, so a drag stays smooth.</summary>
    private const int StageRecutMs = 150;

    /// <summary>
    /// The edge, in physical pixels, that the three pictures are drawn at now: the sleeve's box width
    /// times the display's scale. Until the first size pass has set a width, this is the widest the
    /// page ever draws the sleeve (see <see cref="SleeveDecodeEdge"/>), and the size pass that follows
    /// cuts again.
    /// </summary>
    private int StageEdge()
    {
        var width = CoverBox.Width;
        if (double.IsNaN(width) || width <= 0)
            return SleeveDecodeEdge();

        return Math.Max(1, (int)Math.Round(width * (XamlRoot?.RasterizationScale ?? 1)));
    }

    private ref StageCut? CutOf(StagePicture which)
    {
        switch (which)
        {
            case StagePicture.Sleeve: return ref _sleeveCut;
            case StagePicture.DiscScan: return ref _discScanCut;
            default: return ref _pressingCut;
        }
    }

    private Image ElementOf(StagePicture which) => which switch
    {
        StagePicture.Sleeve => Cover,
        StagePicture.DiscScan => CoverDiscScan,
        _ => CoverDisc,
    };

    /// <summary>
    /// Records that <paramref name="which"/> now shows <paramref name="cut"/>. Called by the loads, in
    /// the same turn as they set the element's source. A load that started before a resize lands at the
    /// size it read at its start, so it asks for a re-cut when that is no longer the size.
    /// </summary>
    private void ShownOnStage(StagePicture which, StageCut cut)
    {
        CutOf(which) = cut;
        SayStageCut(which, cut.Edge, recut: false);
        if (cut.Edge != StageEdge())
            ScheduleStageRecut();
    }

    /// <summary>Forgets what a picture was cut from. Used when the record is replaced. A re-cut still
    /// in flight for it then finds nothing to match and is dropped.</summary>
    private void ForgetStage(StagePicture which) => CutOf(which) = null;

    /// <summary>Asks for the three pictures to be cut again once the sleeve's size has stayed the same
    /// for <see cref="StageRecutMs"/>. Called by FitCover whenever it writes a new edge.</summary>
    private void ScheduleStageRecut()
    {
        if (_stageRecut is null)
        {
            _stageRecut = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StageRecutMs) };
            _stageRecut.Tick += (_, _) =>
            {
                _stageRecut.Stop();
                RecutStage();
            };
        }

        _stageRecut.Stop();
        _stageRecut.Start();
    }

    /// <summary>Cuts each picture that is on screen at a size other than the one it is drawn
    /// at.</summary>
    private void RecutStage()
    {
        var edge = StageEdge();
        foreach (var which in StagePictures)
        {
            if (CutOf(which) is { } cut && cut.Folder == _folder && cut.Edge != edge)
                _ = RecutAsync(which, cut, edge);
        }
    }

    /// <summary>
    /// One picture cut again at <paramref name="edge"/>.
    /// </summary>
    /// <remarks>
    /// The new picture is put on screen only if three things still hold when the cut finishes: the page
    /// still shows the same record, the picture on screen is still the one this cut was made from (the
    /// same remembered cut, not a newer load), and the edge is still the one asked for. If a later
    /// resize starts its own re-cut, this one is dropped, whichever of the two finishes first.
    /// </remarks>
    private async Task RecutAsync(StagePicture which, StageCut shown, int edge)
    {
        try
        {
            var picture = await RemoteImage.DecodeArtAsync(shown.Bytes, edge);

            if (_folder != shown.Folder || !ReferenceEquals(CutOf(which), shown) || StageEdge() != edge)
                return;

            ElementOf(which).Source = picture;
            CutOf(which) = shown with { Edge = edge };
            SayStageCut(which, edge, recut: true);
        }
        catch
        {
            // The cut is only an improvement. If it fails, the picture on screen stays, drawn at the
            // old size.
        }
    }

    /// <summary>One line on the <c>art</c> channel for each picture put on the stage. It gives the
    /// picture, the pixel edge it was cut at, and the edge the box is drawn at now. The two numbers
    /// match when the picture reaches the screen unscaled. Read with <c>rpmscope log --channel
    /// art</c>.</summary>
    private void SayStageCut(StagePicture which, int edge, bool recut)
        => RPM.Core.Diagnostics.Scope.Said(
            "art",
            $"stage {which} {(recut ? "re-cut" : "cut")} at {edge}px, box draws {StageEdge()}px");
}
