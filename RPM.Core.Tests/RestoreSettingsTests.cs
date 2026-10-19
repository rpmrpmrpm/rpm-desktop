using RPM.Core.Audio;
using RPM.Core.Recording;

namespace RPM.Core.Tests;

public class RestoreSettingsTests
{
    private static string TempFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rpm-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void A_folder_with_no_file_reads_as_null()
    {
        Assert.Null(RestoreSettings.Read(TempFolder()));
    }

    [Fact]
    public void Off_is_a_bypass()
    {
        Assert.True(RestoreSettings.Off.IsBypass);
    }

    [Fact]
    public void An_equaliser_curve_is_not_a_bypass()
    {
        var settings = RestoreSettings.Off with
        {
            EqOn = true,
            Gains = new double[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        };
        Assert.False(settings.IsBypass);
    }

    [Fact]
    public void Noise_reduction_without_a_profile_is_still_a_bypass()
    {
        // Nothing to gate against, so the switch on its own changes nothing.
        Assert.True((RestoreSettings.Off with { NoiseOn = true }).IsBypass);
    }

    [Fact]
    public void A_shelf_moved_with_the_equaliser_off_does_not_apply()
    {
        var settings = RestoreSettings.Off with { Bass = 4 };
        Assert.False(settings.ToneApplies);
        Assert.True(settings.IsBypass);
    }

    [Fact]
    public void A_shelf_moved_with_the_equaliser_on_applies()
    {
        var settings = RestoreSettings.Off with { EqOn = true, Treble = -3 };
        Assert.True(settings.ToneApplies);
        Assert.False(settings.IsBypass);
    }

    [Fact]
    public void Noise_applies_only_with_both_the_switch_and_a_profile()
    {
        var profile = NoiseProfile.Measure(new float[8192], 2, 44100);
        Assert.False((RestoreSettings.Off with { NoiseOn = true }).NoiseApplies);
        Assert.False((RestoreSettings.Off with { Profile = profile }).NoiseApplies);
        Assert.True((RestoreSettings.Off with { NoiseOn = true, Profile = profile }).NoiseApplies);
    }

    [Fact]
    public void Round_trips_through_the_file()
    {
        string dir = TempFolder();
        var profile = NoiseProfile.Measure(new float[8192], 2, 44100);
        var settings = RestoreSettings.Off with
        {
            EqOn = true,
            Preamp = -2,
            Gains = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
            NoiseOn = true,
            Noise = new NoiseReductionSettings(ReductionDb: 18, Sensitivity: 4),
            Profile = profile,
        };

        settings.WriteTo(dir);
        var back = RestoreSettings.Read(dir)!;

        Assert.True(back.EqOn);
        Assert.Equal(-2, back.Preamp);
        Assert.Equal(settings.Gains, back.Gains);
        Assert.Equal(18, back.Noise.ReductionDb);
        Assert.Equal(profile.Magnitudes, back.Profile!.Magnitudes);
    }

    [Fact]
    public void An_unreadable_file_throws_rather_than_reading_as_off()
    {
        string dir = TempFolder();
        File.WriteAllText(Path.Combine(dir, RestoreSettings.FileName), "{ this is not json");

        // Silently treating it as off would cut the record without any of the settings the user
        // chose, and nothing on screen would say so.
        Assert.Throws<InvalidDataException>(() => RestoreSettings.Read(dir));
    }

    [Fact]
    public void Unknown_fields_from_a_later_build_are_ignored()
    {
        string dir = TempFolder();
        File.WriteAllText(Path.Combine(dir, RestoreSettings.FileName),
            """{ "EqOn": true, "Preamp": 1, "SomethingNew": 42 }""");

        var back = RestoreSettings.Read(dir)!;
        Assert.True(back.EqOn);
        Assert.Equal(1, back.Preamp);
    }
}
