using RPM.Core.Audio;

namespace RPM.Core.Tests;

public class EndpointNamesTests
{
    [Fact]
    public void TheSoundLibrarysHeadComesOff()
        => Assert.Equal("Speakers (Realtek High Definition Audio)",
                        EndpointNames.Bare("OpenAL Soft on Speakers (Realtek High Definition Audio)"));

    [Fact]
    public void APlainNameIsLeftAlone()
        // What macOS gives on both sides, including the accents.
        => Assert.Equal("Xtreme 2 — Gio", EndpointNames.Bare("Xtreme 2 — Gio"));

    [Fact]
    public void TheDuplicateMarkerStays()
        // " #2" is how the sound library tells two speakers of the same name apart. Dropping it would
        // match the wrong one, which is worse than not matching at all.
        => Assert.Equal("Headphones #2", EndpointNames.Bare("OpenAL Soft on Headphones #2"));

    [Fact]
    public void TheHeadIsOnlyTakenFromTheFront()
        => Assert.Equal("Dock (OpenAL Soft on tap)", EndpointNames.Bare("Dock (OpenAL Soft on tap)"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingIsNothing(string? name)
        => Assert.Equal("", EndpointNames.Bare(name));
}
