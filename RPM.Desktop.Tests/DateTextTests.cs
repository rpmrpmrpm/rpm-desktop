using System.Globalization;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Tests;

/// <summary>
/// A day written the way the reader's culture writes one. A fixed pattern such as <c>d MMM yyyy</c> must
/// not be used, because it puts the day first in every culture, including Chinese and American English.
/// </summary>
public class DateTextTests
{
    private static readonly DateTime Day = new(2026, 10, 8);

    /// <summary>No culture's date may carry its weekday, and every one must say the year, the month
    /// and the day.</summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("fr-FR")]
    [InlineData("es-ES")]
    [InlineData("de-DE")]
    [InlineData("zh-Hans")]
    public void A_day_has_no_weekday_and_says_year_month_and_day(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);

        foreach (var said in new[] { DateText.Short(Day, culture), DateText.Long(Day, culture) })
        {
            Assert.DoesNotContain(culture.DateTimeFormat.GetDayName(DayOfWeek.Thursday), said, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2026", said);
            Assert.Contains("8", said);
        }

        Assert.DoesNotContain("dddd", DateText.Pattern(culture, abbreviated: true));
        Assert.DoesNotContain("MMMM", DateText.Pattern(culture, abbreviated: true));
    }

    /// <summary>The order comes from the culture. Chinese starts with the year, American English with the
    /// month, and French with the day.</summary>
    [Fact]
    public void Each_culture_puts_its_parts_in_its_own_order()
    {
        Assert.StartsWith("2026", DateText.Short(Day, CultureInfo.GetCultureInfo("zh-Hans")));
        Assert.StartsWith("Oct", DateText.Short(Day, CultureInfo.GetCultureInfo("en-US")));
        Assert.StartsWith("8", DateText.Short(Day, CultureInfo.GetCultureInfo("fr-FR")));
    }

    /// <summary>The long form keeps the month whole; the short form abbreviates it.</summary>
    [Fact]
    public void The_long_form_names_the_month_and_the_short_form_abbreviates_it()
    {
        var culture = CultureInfo.GetCultureInfo("en-GB");

        Assert.Contains("October", DateText.Long(Day, culture));
        Assert.DoesNotContain("October", DateText.Short(Day, culture));
        Assert.Contains("Oct", DateText.Short(Day, culture));
    }
}
