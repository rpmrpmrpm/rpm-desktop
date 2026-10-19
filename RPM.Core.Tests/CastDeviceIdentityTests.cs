using System.Net;
using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// These come from a real household, because an invented example would not show the problem: two
/// Nest Audios and the group they belong to, with the group running on one of the speakers' own
/// addresses.
/// </summary>
public class CastDeviceIdentityTests
{
    private static readonly IPAddress Keuken = IPAddress.Parse("192.168.1.106");   // Nest Audio
    private static readonly IPAddress Keuken2 = IPAddress.Parse("192.168.1.165");  // Nest Audio …

    /// <summary>
    /// A speaker group runs <i>on a member speaker</i>, at that speaker's address on another port.
    /// If identity is the address, the group and its host collapse into one entry and one of them is
    /// thrown away. It is the group that goes, and with it the only destination that actually works
    /// (Google refuses to cast to a speaker that has been grouped).
    /// </summary>
    [Fact]
    public void A_Group_Is_Not_The_Speaker_It_Runs_On()
    {
        // "Keuken 2" the speaker, and the two groups hosted on it, all on 192.168.1.165.
        var speaker = CastDeviceIdentity.For("2d1b6997946dade175faa197c85ee380", Keuken2, 8009);
        var pair = CastDeviceIdentity.For("D41E1942-6004-4283-A31E-123C6F5C973F", Keuken2, 32000);
        var mixed = CastDeviceIdentity.For("91ADAB4F-4DA1-4DAE-86F8-7D97EAD88A54", Keuken2, 32223);

        Assert.Equal(3, new[] { speaker, pair, mixed }.Distinct().Count());
    }

    [Fact]
    public void The_Same_Device_Keeps_The_Same_Key()
    {
        var first = CastDeviceIdentity.For("e90dd2c895b0d33cad76cf574aee8dec", Keuken, 8009);
        var again = CastDeviceIdentity.For("e90dd2c895b0d33cad76cf574aee8dec", Keuken, 8009);

        Assert.Equal(first, again);
    }

    /// <summary>Two different speakers are two speakers, however alike their names.</summary>
    [Fact]
    public void Different_Devices_Get_Different_Keys()
    {
        var keuken = CastDeviceIdentity.For("e90dd2c895b0d33cad76cf574aee8dec", Keuken, 8009);
        var keuken2 = CastDeviceIdentity.For("2d1b6997946dade175faa197c85ee380", Keuken2, 8009);

        Assert.NotEqual(keuken, keuken2);
    }

    /// <summary>Without an advertised id we fall back to address <i>and port</i>, never the address
    /// alone, which is the mistake this class exists to prevent.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Falling_Back_Without_An_Id_Still_Separates_A_Group_From_Its_Host(string? missing)
    {
        var speaker = CastDeviceIdentity.For(missing, Keuken2, 8009);
        var group = CastDeviceIdentity.For(missing, Keuken2, 32000);

        Assert.NotEqual(speaker, group);
        Assert.Contains("192.168.1.165", speaker);
    }

    [Fact]
    public void An_Advertised_Id_Beats_The_Address()
    {
        // Same device, seen on a new address after a DHCP lease change: still the same device.
        var before = CastDeviceIdentity.For("abc123", IPAddress.Parse("192.168.1.10"), 8009);
        var after = CastDeviceIdentity.For("abc123", IPAddress.Parse("192.168.1.77"), 8009);

        Assert.Equal(before, after);
    }
}
