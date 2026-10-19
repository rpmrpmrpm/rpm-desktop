using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Checks the key model's output order against the model's own list.
///
/// <para>The names below are copied from S-KEY's <c>key_map</c>, in its order and with its spelling.
/// If this test and the model disagree, every track in the library gets a confident but wrong key.
/// That is worse than no key, because the mix uses it.</para>
/// </summary>
public class KeyModelLabelsTests
{
    /// <summary>S-KEY's key_map, unchanged, index 0 to 23.</summary>
    private static readonly string[] ModelNames =
    [
        "A Major", "Bb Major", "B Major", "C Major", "C# Major", "D Major",
        "D# Major", "E Major", "F Major", "F# Major", "G Major", "G# Major",
        "B minor", "C minor", "C# minor", "D minor", "D# minor", "E minor",
        "F minor", "F# minor", "G minor", "G# minor", "A minor", "Bb minor",
    ];

    /// <summary>The app writes the black notes as sharps and the model writes some of them as
    /// flats: the same key with a different name.</summary>
    private static string Normalise(string name) =>
        name.Replace("Bb", "A#").Replace(" Major", " major").Replace(" minor", " minor");

    [Fact]
    public void Every_output_names_the_key_the_model_says_it_does()
    {
        Assert.Equal(ModelNames.Length, KeyModelLabels.Order.Count);
        Assert.Equal(KeyModelLabels.Count, KeyModelLabels.Order.Count);

        for (int i = 0; i < ModelNames.Length; i++)
            Assert.Equal(Normalise(ModelNames[i]), KeyModelLabels.Order[i].Name);
    }

    [Fact]
    public void All_twenty_four_keys_appear_once()
    {
        Assert.Equal(24, KeyModelLabels.Order.Distinct().Count());
        Assert.Equal(12, KeyModelLabels.Order.Count(k => k.IsMinor));
        Assert.All(KeyModelLabels.Order, k => Assert.True(k.IsKnown));
    }

    [Fact]
    public void Both_halves_climb_by_a_semitone_from_where_they_start()
    {
        // Catches a rotation of either half. The list above would also catch it, but this states
        // the rule: the majors go up from A, the minors from B, and each of the twenty-four is a
        // semitone above the one before it in its half.
        Assert.Equal("A major", KeyModelLabels.Order[0].Name);
        Assert.Equal("B minor", KeyModelLabels.Order[12].Name);

        for (int i = 1; i < 12; i++)
        {
            Assert.Equal((KeyModelLabels.Order[i - 1].Tonic + 1) % 12, KeyModelLabels.Order[i].Tonic);
            Assert.Equal((KeyModelLabels.Order[i + 11].Tonic + 1) % 12, KeyModelLabels.Order[i + 12].Tonic);
        }
    }

    [Fact]
    public void The_two_halves_are_a_tone_apart_and_not_relatives()
    {
        // Stated because the obvious guess is wrong. Output n+12 is a tone above output n, not its
        // relative minor. The two halves of the model's list are not related, and a table that
        // assumed they were would be wrong by five semitones.
        for (int i = 0; i < 12; i++)
        {
            int major = KeyModelLabels.Order[i].Tonic;
            int minor = KeyModelLabels.Order[i + 12].Tonic;
            Assert.Equal(2, (minor - major + 12) % 12);
            Assert.NotEqual(KeyModelLabels.Order[i].CamelotHour, KeyModelLabels.Order[i + 12].CamelotHour);
        }
    }
}
