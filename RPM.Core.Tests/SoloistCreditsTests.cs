using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// The soloists tag read as performers and vocalists, and written back as one tag.
/// </summary>
public class SoloistCreditsTests
{
    private const string Requiem =
        "Barbara Bonney (soprano); Anne Sofie von Otter (mezzo-soprano); Hans Peter Blochwitz (tenor); "
        + "Willard White (bass); Ralph Kirshbaum (cello); Tweet (lead vocals)";

    [Fact]
    public void Singers_are_told_apart_from_players_by_their_role()
    {
        Assert.Equal(
            ["Barbara Bonney (soprano)", "Anne Sofie von Otter (mezzo-soprano)", "Hans Peter Blochwitz (tenor)",
             "Tweet (lead vocals)"],
            SoloistCredits.Vocalists(Requiem));

        // Bass on its own is the instrument; a sleeve credits the singer "bass vocals" or "bass-baritone".
        Assert.Equal(["Willard White (bass)", "Ralph Kirshbaum (cello)"], SoloistCredits.Performers(Requiem));
    }

    [Theory]
    [InlineData("Branford Marsalis (tenor saxophone)", false)]
    [InlineData("Missy Elliott (lead vocals)", true)]
    [InlineData("Sting (bass, vocals)", true)]
    [InlineData("Dietrich Fischer-Dieskau (Baritone)", true)]
    [InlineData("Kathleen Ferrier (Voice)", true)]
    [InlineData("Tweet", false)]
    public void A_role_that_names_a_voice_makes_a_vocalist(string entry, bool vocalist)
        => Assert.Equal(vocalist, SoloistCredits.IsVocalist(entry));

    [Fact]
    public void A_name_typed_under_vocalists_is_given_a_voice_so_it_reads_back_there()
    {
        var tag = SoloistCredits.Join(["András Schiff (piano)"], ["Cecilia Bartoli"]);

        Assert.Equal("András Schiff (piano); Cecilia Bartoli (vocals)", tag);
        Assert.Equal(["Cecilia Bartoli (vocals)"], SoloistCredits.Vocalists(tag));
    }

    [Fact]
    public void Each_list_is_replaced_without_touching_the_other()
    {
        var tag = SoloistCredits.WithPerformers(Requiem, "Yo-Yo Ma (cello)");

        Assert.Equal(["Yo-Yo Ma (cello)"], SoloistCredits.Performers(tag));
        Assert.Equal(4, SoloistCredits.Vocalists(tag).Count);

        var sung = SoloistCredits.WithVocalists(tag, "Jessye Norman (soprano)");
        Assert.Equal("Yo-Yo Ma (cello); Jessye Norman (soprano)", sung);
    }

    [Fact]
    public void Two_empty_lists_clear_the_tag()
        => Assert.Null(SoloistCredits.Join([" ", ""], []));

    [Fact]
    public void A_list_written_with_commas_after_each_role_is_read_as_one_entry_a_person()
    {
        const string Commas = "Theo Adam (bass vocals), Edith Mathis (soprano vocals), Peter Schreier (tenor)";

        Assert.Equal(["Theo Adam (bass vocals)", "Edith Mathis (soprano vocals)", "Peter Schreier (tenor)"],
            SoloistCredits.Vocalists(Commas));

        // A comma that does not close a role is part of the name.
        Assert.Equal(["Bach, Johann Sebastian (organ)"], SoloistCredits.Performers("Bach, Johann Sebastian (organ)"));
    }
}
