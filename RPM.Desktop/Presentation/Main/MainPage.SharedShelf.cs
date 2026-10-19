using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services.Audio;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Sharing;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main;

public sealed partial class MainPage
{
    /// <summary>The two things about a borrowed shelf that cannot be shown by drawing: whose it is,
    /// and whether that computer can be reached. Louder than either is a stop in the music, which
    /// happens when neither that computer nor the cloud can be reached.
    ///
    /// <para>A shelf taken from someone else is kept on this disk, so it stays readable after they shut
    /// their laptop. Every record on it that exists only on their disk stops being playable at that
    /// moment. Pressing play on one of them does nothing.</para>
    ///
    /// <para>So when their computer cannot be reached, the shelf narrows to what the cloud holds. That is
    /// the rule the sharing was agreed under. The blue band explains why the wall has fewer records on it
    /// than it did a minute ago.</para>
    ///
    /// <para>The red band is the other case. It is about the player rather than the shelf. A record
    /// that was playing from that computer stopped in the middle of a track, the cloud had no copy to
    /// fall back to, and the music is off. The red band wins when both are true, because it is the one
    /// the listener needs to see.</para>
    /// </summary>
    private void SyncLibraryBand()
    {
        var library = App.Services.GetService<LibraryService>();
        var sharing = App.Services.GetService<SharingService>();
        var lender = library is { IsPeerView: true, ViewingPeer: { } peer } && sharing is not null
            ? peer
            : null;

        var reading = RPM.Core.Sharing.LibraryBand.For(
            playbackLost: App.Services.GetService<PlaybackService>() is { SourceLost: true },
            reach: RPM.Core.Net.NetworkReach.Shared.Now,
            viewingPeer: lender is not null,
            peerReachable: lender is null || sharing!.IsReachable(lender));

        switch (reading)
        {
            case RPM.Core.Sharing.LibraryBandReading.PlaybackLost:
                ShowLibraryBand(Loc.Get("Share_PlaybackLost"), "BandError", mark: "\uf06a");
                break;

            case RPM.Core.Sharing.LibraryBandReading.PeerAway:
                ShowLibraryBand(Loc.Format("Share_PeerAway", PeerNames.Of(sharing!, lender!)), "BandInfo",
                                mark: "\uf05a");
                break;

            case RPM.Core.Sharing.LibraryBandReading.Offline:
                ShowLibraryBand(Loc.Get("Net_Offline"), "BandInfo", mark: "\uf05a");
                break;

            case RPM.Core.Sharing.LibraryBandReading.LocalOnly:
                ShowLibraryBand(Loc.Get("Net_LocalOnly"), "BandInfo", mark: "\uf05a");
                break;

            default:
                LibraryBand.Visibility = Visibility.Collapsed;
                break;
        }

        // The band takes the bottom edge of the card while it is up. So whatever is drawn above it
        // stops being the card's foot, and gives up the corner it had there. This is asked of the band
        // itself, after every branch above (including the one that hides it), so it reflects the state
        // the window is in now. The album page is the only view in this card that rounds its foot. The
        // wall, the list and the table all end square.
        AlbumDetail.StandOn(LibraryBand.Visibility == Visibility.Visible ? CardFoot.Band : CardFoot.CardEdge);
    }

    /// <summary>Shows the band in one of its styles. The colours are named in the markup and are looked
    /// up by name here, from the application's dictionary rather than the page's. See
    /// <see cref="BandBrush"/>, which is the same route the left pane's band takes.</summary>
    /// <param name="mark">The Font Awesome glyph, written as an escape rather than the character itself.
    /// A raw private-use code point does not survive every editor it passes through.</param>
    private void ShowLibraryBand([Shown] string message, string voice, string mark)
    {
        LibraryBandText.Text = message;
        LibraryBandMark.Text = mark;
        LibraryBand.Background = BandBrush(voice + "Fill");
        LibraryBand.BorderBrush = BandBrush(voice + "Edge");
        LibraryBandMark.Foreground = BandBrush(voice + "Mark");
        LibraryBandText.Foreground = BandBrush(voice + "Ink");
        LibraryBand.Visibility = Visibility.Visible;
    }
}
