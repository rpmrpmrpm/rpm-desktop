using RPM.Core.Import;

namespace RPM.Core.Tests;

/// <summary>
/// Building one tag out of the others. The pattern language is the app's own, the same tokens that
/// file a track and read one back out of a file name. So what is tested is that a value is built
/// the way a path is, minus everything that is about being a path.
/// </summary>
public class TagTemplateTests
{
    private static string Render(string pattern, params (string Token, string Value)[] fields)
    {
        var map = fields.ToDictionary(f => f.Token, f => f.Value, StringComparer.OrdinalIgnoreCase);
        return TagTemplate.Render(pattern, name => map.TryGetValue(name, out var v) ? v : null);
    }

    [Fact]
    public void A_pattern_is_its_fields_with_the_literals_between_them()
    {
        Assert.Equal(
            "Enya - Orinoco Flow",
            Render("{artist} - {title}", ("artist", "Enya"), ("title", "Orinoco Flow")));
    }

    [Fact]
    public void A_number_takes_the_format_the_token_asked_for()
    {
        Assert.Equal("03 - Storms", Render("{track:00} - {title}", ("track", "3"), ("title", "Storms")));
        Assert.Equal("3 - Storms", Render("{track} - {title}", ("track", "3"), ("title", "Storms")));
    }

    [Fact]
    public void An_empty_field_takes_its_punctuation_with_it()
    {
        // The whole reason this is not a string format: "{track:00} - {title}" on an unnumbered track
        // is the title, not " - " and the title.
        Assert.Equal("Storms", Render("{track:00} - {title}", ("track", ""), ("title", "Storms")));
        Assert.Equal("Enya", Render("{artist} - {title}", ("artist", "Enya"), ("title", "")));
        Assert.Equal("Watermark", Render("{album} ({year})", ("album", "Watermark"), ("year", "")));
    }

    [Fact]
    public void A_zero_is_an_absent_number_not_a_zero()
    {
        Assert.Equal("Storms", Render("{track:00} - {title}", ("track", "0"), ("title", "Storms")));
    }

    [Fact]
    public void A_token_no_field_answers_to_renders_as_nothing()
    {
        Assert.Equal("Storms", Render("{nosuchthing} {title}", ("title", "Storms")));
    }

    [Fact]
    public void Nothing_is_sanitized_because_this_is_a_value_and_not_a_path()
    {
        // A slash in a comment is a slash. The naming template's rules are about file systems, and a
        // tag lives in neither a folder nor a file name.
        Assert.Equal(
            "AC/DC: Back in Black",
            Render("{artist}: {album}", ("artist", "AC/DC"), ("album", "Back in Black")));
    }

    [Fact]
    public void A_pattern_with_no_tokens_is_the_literal_it_reads_as()
    {
        Assert.Equal("Ripped from vinyl", Render("Ripped from vinyl"));
        Assert.Equal("", Render(""));
    }

    [Fact]
    public void A_format_on_a_word_is_left_alone()
    {
        // "{title:00}" is a pattern somebody typed by accident, not an error worth throwing for.
        Assert.Equal("Storms", Render("{title:00}", ("title", "Storms")));
    }

    [Fact]
    public void Every_preset_renders_against_an_ordinary_track()
    {
        foreach (var preset in TagTemplate.Presets)
        {
            var value = Render(
                preset,
                ("artist", "Enya"), ("albumartist", "Enya"), ("album", "Watermark"),
                ("title", "Orinoco Flow"), ("track", "2"), ("year", "1988"));
            Assert.False(string.IsNullOrWhiteSpace(value), preset);
        }
    }

    [Fact]
    public void The_tokens_a_pattern_names_can_be_read_back_out_of_it()
    {
        Assert.Equal(["track", "title"], TagTemplate.TokensIn("{track:00} - {title}"));
    }
}
