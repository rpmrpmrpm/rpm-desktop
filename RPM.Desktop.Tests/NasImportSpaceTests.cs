using RPM.Core.Nas;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Nas;

namespace RPM.Desktop.Tests;

public class NasImportSpaceTests
{
    private static NasHello Space(long used, long free, long? limit) => new() { UsedBytes = used, FreeBytes = free, LimitBytes = limit };

    [Fact]
    public void Import_that_would_pass_the_limit_is_refused_before_copying() =>
        Assert.Equal(NasSpaceAnswer.Full, NasImportSpace.Decide(Space(1_900, 100_000, 2_000), 200));

    [Fact]
    public void Import_is_allowed_when_the_nas_space_cannot_be_read() =>
        Assert.Null(NasImportSpace.Decide(null, long.MaxValue));

    [Fact]
    public void Almost_full_is_reported_and_allowed() =>
        Assert.Equal(NasSpaceAnswer.AlmostFull, NasImportSpace.Decide(Space(1_700, 100_000, 2_000), 200));
}
