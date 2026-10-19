using NuGet.Versioning;

using RPM.Desktop.Services.Updates;

using Velopack;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which delta packages an installed copy takes. A delta names the version it produces, not the version it
/// was made from. So the only safe chain has no gap: the local version must be listed, and there must be a
/// delta for every full release after it. These tests check this rule.
/// </summary>
public class DeltaChainTests
{
    private static VelopackAsset Full(string version) => Asset(version, VelopackAssetType.Full);

    private static VelopackAsset Delta(string version) => Asset(version, VelopackAssetType.Delta);

    private static VelopackAsset Asset(string version, VelopackAssetType type) => new()
    {
        PackageId = "com.rpm.desktop",
        Version = SemanticVersion.Parse(version),
        Type = type,
        FileName = $"com.rpm.desktop-{version}-osx-arm64-{(type == VelopackAssetType.Full ? "full" : "delta")}.nupkg",
    };

    [Fact]
    public void One_release_ahead_takes_its_delta()
    {
        var feed = new[] { Full("0.1.9"), Full("0.1.10"), Delta("0.1.10") };

        var chain = DeltaChain.From(feed, Full("0.1.9"), Full("0.1.10"));

        Assert.NotNull(chain);
        Assert.Equal(["0.1.10"], chain.Select(d => d.Version.ToString()));
    }

    [Fact]
    public void Several_releases_ahead_take_every_delta_oldest_first()
    {
        var feed = new[]
        {
            Delta("0.1.12"), Full("0.1.12"), Full("0.1.9"), Delta("0.1.11"), Full("0.1.11"), Full("0.1.10"), Delta("0.1.10"),
        };

        var chain = DeltaChain.From(feed, Full("0.1.9"), Full("0.1.12"));

        Assert.NotNull(chain);
        Assert.Equal(["0.1.10", "0.1.11", "0.1.12"], chain.Select(d => d.Version.ToString()));
        Assert.All(chain, d => Assert.Equal(VelopackAssetType.Delta, d.Type));
    }

    /// <summary>Here 0.1.10 has no delta, so the delta for 0.1.11 was made from 0.1.10. It cannot be
    /// applied to 0.1.9. Velopack's own rule would apply it anyway, so this code must reject it.</summary>
    [Fact]
    public void A_release_without_its_delta_breaks_the_chain()
    {
        var feed = new[] { Full("0.1.9"), Full("0.1.10"), Full("0.1.11"), Delta("0.1.11") };

        Assert.Null(DeltaChain.From(feed, Full("0.1.9"), Full("0.1.11")));
    }

    /// <summary>A local package that the feed does not list, for example a version published only to
    /// another channel or removed from the feed. Nothing says which delta was made from it.</summary>
    [Fact]
    public void A_local_version_the_feed_does_not_list_takes_the_full_package()
    {
        var feed = new[] { Full("0.1.10"), Delta("0.1.10") };

        Assert.Null(DeltaChain.From(feed, Full("0.1.6"), Full("0.1.10")));
    }

    [Fact]
    public void The_latest_release_without_a_delta_takes_the_full_package()
    {
        var feed = new[] { Full("0.1.9"), Full("0.1.10") };

        Assert.Null(DeltaChain.From(feed, Full("0.1.9"), Full("0.1.10")));
    }

    /// <summary>A delta newer than the target is not part of the path to it.</summary>
    [Fact]
    public void Deltas_past_the_target_are_left_out()
    {
        var feed = new[] { Full("0.1.9"), Full("0.1.10"), Delta("0.1.10"), Full("0.1.11"), Delta("0.1.11") };

        var chain = DeltaChain.From(feed, Full("0.1.9"), Full("0.1.10"));

        Assert.NotNull(chain);
        Assert.Equal(["0.1.10"], chain.Select(d => d.Version.ToString()));
    }

    [Fact]
    public void Nothing_newer_than_the_local_package_is_no_chain()
    {
        var feed = new[] { Full("0.1.9") };

        Assert.Null(DeltaChain.From(feed, Full("0.1.9"), Full("0.1.9")));
    }
}
