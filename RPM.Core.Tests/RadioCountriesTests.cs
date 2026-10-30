using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>
/// The names the country list is shown under. Every "before" here is a string that Radio Browser
/// really publishes, taken from its own countries endpoint. The point is not that a rule is neat,
/// but that the eighty names a listener sees are names they would use.
/// </summary>
public class RadioCountriesTests
{
    /// <summary>The ones that tidying cannot reach: the common name is a different word from the
    /// registered one.</summary>
    [Theory]
    [InlineData("The United Kingdom Of Great Britain And Northern Ireland", "GB", "United Kingdom")]
    [InlineData("The United States Of America", "US", "United States")]
    [InlineData("The Russian Federation", "RU", "Russia")]
    [InlineData("The Republic Of Korea", "KR", "South Korea")]
    [InlineData("The Democratic Peoples Republic Of Korea", "KP", "North Korea")]
    [InlineData("Taiwan, Republic Of China", "TW", "Taiwan")]
    [InlineData("Bolivarian Republic Of Venezuela", "VE", "Venezuela")]
    [InlineData("Islamic Republic Of Iran", "IR", "Iran")]
    [InlineData("Syrian Arab Republic", "SY", "Syria")]
    [InlineData("United Republic Of Tanzania", "TZ", "Tanzania")]
    [InlineData("The Republic Of Moldova", "MD", "Moldova")]
    [InlineData("Republic Of North Macedonia", "MK", "North Macedonia")]
    [InlineData("The Lao Peoples Democratic Republic", "LA", "Laos")]
    [InlineData("The Democratic Republic Of The Congo", "CD", "DR Congo")]
    [InlineData("State Of Palestine", "PS", "Palestine")]
    [InlineData("The Holy See", "VA", "Vatican City")]
    public void A_country_called_something_else_entirely(string filed, string code, string shown)
        => Assert.Equal(shown, RadioCountries.Short(filed, code));

    /// <summary>The article that the register puts in front of a name and that nobody puts in a
    /// list.</summary>
    [Theory]
    [InlineData("The Netherlands", "NL", "Netherlands")]
    [InlineData("The Philippines", "PH", "Philippines")]
    [InlineData("The United Arab Emirates", "AE", "United Arab Emirates")]
    [InlineData("The Dominican Republic", "DO", "Dominican Republic")]
    [InlineData("The Bahamas", "BS", "Bahamas")]
    [InlineData("The Gambia", "GM", "Gambia")]
    [InlineData("The Central African Republic", "CF", "Central African Republic")]
    [InlineData("The Faroe Islands", "FO", "Faroe Islands")]
    public void The_article_comes_off(string filed, string code, string shown)
        => Assert.Equal(shown, RadioCountries.Short(filed, code));

    /// <summary>The directory title-cases every word, including the ones that only join a name
    /// together.</summary>
    [Theory]
    [InlineData("Trinidad And Tobago", "TT", "Trinidad and Tobago")]
    [InlineData("Bosnia And Herzegovina", "BA", "Bosnia and Herzegovina")]
    [InlineData("Isle Of Man", "IM", "Isle of Man")]
    [InlineData("Sao Tome And Principe", "ST", "Sao Tome and Principe")]
    [InlineData("Saint Vincent And The Grenadines", "VC", "Saint Vincent and the Grenadines")]
    public void The_joining_words_come_back_down(string filed, string code, string shown)
        => Assert.Equal(shown, RadioCountries.Short(filed, code));

    /// <summary>Two hundred of them are already called what everybody calls them, and those must come
    /// through unchanged.</summary>
    [Theory]
    [InlineData("Germany", "DE")]
    [InlineData("France", "FR")]
    [InlineData("Türkiye", "TR")]
    [InlineData("Czechia", "CZ")]
    [InlineData("New Zealand", "NZ")]
    [InlineData("South Africa", "ZA")]
    [InlineData("Saudi Arabia", "SA")]
    [InlineData("Papua New Guinea", "PG")]
    public void A_name_that_needs_nothing_is_left_alone(string name, string code)
        => Assert.Equal(name, RadioCountries.Short(name, code));

    /// <summary>The directory has a few entries with no code at all. They get the tidying, which needs
    /// only the string.</summary>
    [Fact]
    public void A_country_with_no_code_is_still_tidied()
    {
        Assert.Equal("Netherlands", RadioCountries.Short("The Netherlands", null));
        Assert.Equal("Isle of Man", RadioCountries.Short("Isle Of Man", ""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_named_is_nothing_shown(string? name)
        => Assert.Equal("", RadioCountries.Short(name, "XX"));

    // ForPicker. The phone's RadioCountriesTests.swift holds the same cases; keep the two in step.

    /// <summary>The countries with the most stations are offered, and no more than
    /// <see cref="RadioCountries.Offered"/> of them.</summary>
    [Fact]
    public void The_picker_offers_the_countries_with_the_most_stations()
    {
        var many = Enumerable.Range(0, RadioCountries.Offered + 5)
            .Select(i => new RadioFacet($"Country {i:D3}", $"C{i:D3}", 1000 - i))
            .ToList();

        var offered = RadioCountries.ForPicker(many);

        Assert.Equal(RadioCountries.Offered, offered.Count);
        Assert.DoesNotContain(offered, c => c.Code == $"C{RadioCountries.Offered:D3}");
        Assert.Contains(offered, c => c.Code == $"C{RadioCountries.Offered - 1:D3}");
    }

    /// <summary>The listener's own country leads, ahead of the A to Z, even when it has few stations.
    /// The list is still no longer than <see cref="RadioCountries.Offered"/>.</summary>
    [Fact]
    public void The_listeners_country_comes_first()
    {
        var many = Enumerable.Range(0, RadioCountries.Offered + 5)
            .Select(i => new RadioFacet($"Country {i:D3}", $"C{i:D3}", 1000 - i))
            .Append(new RadioFacet("The Netherlands", "nl", 1))
            .ToList();

        var offered = RadioCountries.ForPicker(many, home: "NL");

        Assert.Equal(RadioCountries.Offered, offered.Count);
        Assert.Equal(("NL", "Netherlands"), (offered[0].Code, offered[0].Name));
        Assert.Equal("Country 000", offered[1].Name);
    }

    /// <summary>A home country the directory does not have changes nothing.</summary>
    [Fact]
    public void An_unknown_home_country_leaves_the_list_alone()
    {
        IReadOnlyList<RadioFacet> countries = [new("Germany", "DE", 2000), new("Austria", "AT", 500)];

        Assert.Equal(
            RadioCountries.ForPicker(countries).Select(c => c.Code),
            RadioCountries.ForPicker(countries, home: "ZZ").Select(c => c.Code));
    }

    /// <summary>A to Z by the shown name, so "The Netherlands" is under N and "The United Kingdom Of
    /// Great Britain And Northern Ireland" under U.</summary>
    [Fact]
    public void The_picker_sorts_by_the_short_name()
    {
        var offered = RadioCountries.ForPicker(
        [
            new("The United Kingdom Of Great Britain And Northern Ireland", "GB", 3000),
            new("Germany", "DE", 2000),
            new("The Netherlands", "NL", 1000),
            new("Austria", "AT", 500),
        ]);

        Assert.Equal(["Austria", "Germany", "Netherlands", "United Kingdom"], offered.Select(c => c.Name));
    }

    /// <summary>Sorted as names, not as code points: "Türkiye" between "Tunisia" and "Uganda".</summary>
    [Fact]
    public void The_picker_sorts_accented_names_with_their_letter()
    {
        var offered = RadioCountries.ForPicker(
        [
            new("Uganda", "UG", 30),
            new("Türkiye", "TR", 20),
            new("Tunisia", "TN", 10),
        ]);

        Assert.Equal(["Tunisia", "Türkiye", "Uganda"], offered.Select(c => c.Name));
    }

    /// <summary>The directory files Brazil a second time under "br" with one station. The client
    /// uppercases the code, and the picker shows one Brazil with both counts.</summary>
    [Fact]
    public void The_picker_shows_one_entry_per_code()
    {
        var offered = RadioCountries.ForPicker(
        [
            new("Brazil", "BR", 1759),
            new("Brazil", "br", 1),
            new("Italy", "IT", 1861),
        ]);

        Assert.Equal(["Brazil", "Italy"], offered.Select(c => c.Name));
        Assert.Equal(1760, offered[0].StationCount);
        Assert.Equal("BR", offered[0].Code);
    }

    /// <summary>A tie at the cut is decided by code, not by the order the directory answered in.</summary>
    [Fact]
    public void A_tie_at_the_cut_is_decided_by_code()
    {
        var full = Enumerable.Range(0, RadioCountries.Offered - 1)
            .Select(i => new RadioFacet($"Country {i:D3}", $"C{i:D3}", 1000 - i))
            .ToList();

        var one = RadioCountries.ForPicker([.. full, new("Zeta", "ZZ", 5), new("Alpha", "AA", 5)]);
        var other = RadioCountries.ForPicker([.. full, new("Alpha", "AA", 5), new("Zeta", "ZZ", 5)]);

        Assert.Contains(one, c => c.Code == "AA");
        Assert.DoesNotContain(one, c => c.Code == "ZZ");
        Assert.Equal(one.Select(c => c.Code), other.Select(c => c.Code));
    }
}
