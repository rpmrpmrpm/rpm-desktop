using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>The ladder of letters beside Pegasus's artists list, as on the Android TV app.</summary>
public class ArtistLettersTests
{
    private static ArtistWallItem Act(string name) => new(name, [name + "/a"], [], [], 1);

    private static IReadOnlyList<ArtistLetter> LettersOf(params string[] names) =>
        ArtistLetters.Of(names.Select(Act).ToList(), a => a.Initial);

    [Fact]
    public void Each_letter_once_at_its_first_act()
    {
        var letters = LettersOf("ABBA", "Air", "Björk", "Blur", "Cher");

        Assert.Equal([new('A', 0), new('B', 2), new('C', 4)], letters);
    }

    /// <summary>Only the letters the list has: no rung leads nowhere.</summary>
    [Fact]
    public void A_letter_with_no_acts_has_no_rung()
    {
        var letters = LettersOf("ABBA", "Queen", "Zappa");

        Assert.Equal(['A', 'Q', 'Z'], letters.Select(l => l.Letter));
    }

    /// <summary>Digits, symbols and other scripts file under "#", which comes after Z.</summary>
    [Fact]
    public void The_hash_bucket_comes_last()
    {
        var letters = LettersOf("ABBA", "Zappa", "2Pac", "!!!", "坂本龍一");

        Assert.Equal([new('A', 0), new('Z', 1), new('#', 2)], letters);
    }

    /// <summary>The ladder reads A to Z whatever order it is handed, each letter at the first act filed
    /// under it.</summary>
    [Fact]
    public void Letters_read_alphabetically_whatever_the_order_handed()
    {
        var letters = LettersOf("2Pac", "Haim", "Air", "Hole");

        Assert.Equal([new('A', 2), new('H', 1), new('#', 0)], letters);
    }

    /// <summary>Accents fold and a leading bracket is skipped, as the list's own letters are.</summary>
    [Fact]
    public void A_name_files_under_the_letter_the_list_gives_it()
    {
        var letters = LettersOf("Ólafur Arnalds", "(hed) p.e.");

        Assert.Equal([new('H', 1), new('O', 0)], letters);
    }

    [Fact]
    public void An_empty_list_has_no_ladder()
    {
        Assert.Empty(LettersOf());
    }
}
