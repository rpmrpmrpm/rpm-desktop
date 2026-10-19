using System.Globalization;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Tests;

/// <summary>
/// Sizes a person would say out loud.
///
/// <para>The decimal separator comes from the machine's culture, not from this file. A Dutch install shows
/// "12,4 GB", and that is correct. So the expected strings are built the same way the code builds them. The
/// asserts check the parts that are decisions: which unit is used, and how many digits follow it.</para>
/// </summary>
public class ByteSizeTests
{
    private const long Kilo = 1024;
    private const long Mega = 1024 * Kilo;
    private const long Giga = 1024 * Mega;
    private const long Tera = 1024 * Giga;

    private static string Number(double value, string format) =>
        value.ToString(format, CultureInfo.CurrentCulture);

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    public void Bytes_are_said_as_bytes(long bytes, string expected)
        => Assert.Equal(expected, ByteSize.Human(bytes));

    /// <summary>Kilobytes and bytes are shown as whole numbers, because a tenth of a kilobyte means little
    /// to a reader.</summary>
    [Fact]
    public void Kilobytes_and_bytes_get_no_decimal()
    {
        Assert.Equal("1 KB", ByteSize.Human(Kilo));
        Assert.Equal("2 KB", ByteSize.Human(Kilo * 3 / 2));    // 1.5 KB, rounded to the nearest
        Assert.Equal("1023 KB", ByteSize.Human(Mega - Kilo));
    }

    /// <summary>From megabytes up, one decimal is shown. A tenth of a gigabyte is a real part of a music
    /// collection.</summary>
    [Fact]
    public void Megabytes_and_up_carry_a_tenth()
    {
        Assert.Equal($"{Number(1.5, "0.#")} MB", ByteSize.Human(Mega * 3 / 2));
        Assert.Equal($"{Number(12.4, "0.#")} GB", ByteSize.Human(13_314_398_208));
        Assert.Equal($"{Number(2.5, "0.#")} TB", ByteSize.Human(Tera * 5 / 2));
    }

    /// <summary>A whole number of gigabytes is shown with no decimal part.</summary>
    [Fact]
    public void A_whole_number_of_gigabytes_is_said_without_a_decimal()
        => Assert.Equal("4 GB", ByteSize.Human(Giga * 4));

    [Theory]
    [InlineData(1023, "B")]
    [InlineData(1024, "KB")]
    [InlineData(1024L * 1024, "MB")]
    [InlineData(1024L * 1024 * 1024, "GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "TB")]
    public void The_unit_changes_at_every_thousand_and_twenty_four(long bytes, string unit)
        => Assert.EndsWith($" {unit}", ByteSize.Human(bytes));

    /// <summary>The largest unit is terabytes. A library of a petabyte or more is shown as a large number
    /// of TB, rather than running past the last unit.</summary>
    [Fact]
    public void A_size_past_the_last_unit_stays_in_that_unit()
        => Assert.EndsWith(" TB", ByteSize.Human(Tera * 4096));

    [Fact]
    public void A_negative_size_does_not_walk_the_units_backwards()
        => Assert.Equal("-500 B", ByteSize.Human(-500));
}
