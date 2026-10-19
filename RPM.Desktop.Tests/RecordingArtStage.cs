using RPM.Core.Art;
using RPM.Desktop.Presentation.NowPlaying;

namespace RPM.Desktop.Tests;

/// <summary>A stage that shows nothing and remembers the order it was asked in. Every picture comes
/// back empty on purpose: the covers and discs are XAML bitmaps, and this host has no application to
/// build one in.</summary>
internal sealed class RecordingArtStage : IArtStage
{
    public List<string> Calls { get; } = [];

    public ArtScene Scene => ArtScene.Default;

    public void SetText(string? title, string? artist, string? album, string? work = null,
                        string? movement = null) => Calls.Add("SetText");
    public void SetLoading(bool on) { }
    public void SetCarousel(int index, int count) { }
    public void EnterTrack() => Calls.Add("EnterTrack");
    public void EnterIdle() => Calls.Add("EnterIdle");

    public Task<byte[]?> LoadLocalCoverAsync(string filePath, CancellationToken ct)
    {
        Calls.Add("LoadLocalCoverAsync");
        return Task.FromResult<byte[]?>(null);
    }

    public Task<Asked<byte[]?>> LoadRemoteCoverAsync(string url, CancellationToken ct)
        => Task.FromResult(Asked.Answer<byte[]?>(null));

    /// <summary>No tuner in a test, so no station is on and the manager falls back to the address the
    /// source named: which is what every test here already expects of it.</summary>
    public Task<Asked<byte[]?>> TunedStationMarkAsync(CancellationToken ct)
        => Task.FromResult(Asked.Answer<byte[]?>(null));

    public Task<LoadedCover?> DecodeCoverBytesAsync(byte[] bytes, bool mark, CancellationToken ct)
        => Task.FromResult<LoadedCover?>(null);

    public Task<LoadedDisc?> LoadLocalDiscAsync(string filePath, CancellationToken ct)
    {
        Calls.Add("LoadLocalDiscAsync");
        return Task.FromResult<LoadedDisc?>(new LoadedDisc(null));
    }

    public Task<LoadedDisc?> DecodeDiscBytesAsync(byte[]? bytes, CancellationToken ct)
    {
        Calls.Add("DecodeDiscBytesAsync");
        return Task.FromResult<LoadedDisc?>(new LoadedDisc(null));
    }

    public Task<Asked<LoadedBackdrop?>> LoadBackdropAsync(string? url, ArtFraming? framing, CancellationToken ct)
    {
        Calls.Add("LoadBackdropAsync");
        return Task.FromResult(Asked.Answer<LoadedBackdrop?>(null));
    }

    public ArtFraming? AutomaticFraming(string? url) => null;
    public void OfferPlacement() { }

    public void ShowCover(LoadedCover? cover) => Calls.Add("ShowCover");
    public void ShowDisc(LoadedDisc disc) => Calls.Add("ShowDisc");
    public void ShowBackdrop(LoadedBackdrop art) => Calls.Add("ShowBackdrop");
    public void ClearBackdrop() => Calls.Add("ClearBackdrop");
}
