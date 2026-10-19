using RPM.Desktop.Services.Sharing;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The part of <see cref="SharingService.ArtFor"/> that needs no running server, library or peer. It
/// turns the key a borrower asked for (<c>pl:12</c>) into the node id that
/// <see cref="RPM.Desktop.Presentation.Common.PlaylistCoverStore.Read"/> expects. Any other key gives
/// nothing.
///
/// <para>The rest of <c>ArtFor</c> is tested elsewhere. It reads the master, and only from this library's
/// own segment. The routing is tested by the OnArt and ArtRoute test in <c>LibraryServerTests</c>, and the
/// store's own-segment behaviour is tested in <c>PlaylistCoverStoreTests</c>. Only this parsing was still
/// untested.</para>
/// </summary>
public class PlaylistArtKeyTests
{
    [Fact]
    public void A_playlist_key_names_its_node()
        => Assert.Equal(12L, SharingService.PlaylistNodeOf("pl:12"));

    [Theory]
    [InlineData("sleeve")]
    [InlineData("pl:")]
    [InlineData("pl:notanumber")]
    [InlineData("PL:12")]
    [InlineData("album:12")]
    [InlineData("")]
    public void Anything_that_is_not_a_well_formed_playlist_key_names_nothing(string key)
        => Assert.Null(SharingService.PlaylistNodeOf(key));
}
