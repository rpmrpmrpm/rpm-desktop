using RPM.Core.Models;
using RPM.Desktop.Presentation.Dialogs;

namespace RPM.Desktop.Tests;

/// <summary>
/// The catalogue that every editing window is built from. The mass edit, the album editor and the per-file
/// editor all read this one list, rather than each keeping their own idea of what a tag is.
///
/// <para>So it is worth checking as a list. A duplicate kind, a field with no way to write itself into a
/// file, or a pairing that points at nothing would each be a window that draws wrong or an edit that
/// silently does not land.</para>
/// </summary>
public class TagFieldsTests
{
    [Fact]
    public void Every_field_is_its_own_kind()
    {
        var kinds = TagFields.All.Select(f => f.Kind).ToList();

        Assert.Equal(kinds.Count, kinds.Distinct().Count());
    }

    /// <summary>Every kind can be looked up, which is what a window does when it draws a field it was
    /// handed by kind alone.</summary>
    [Fact]
    public void Every_field_can_be_found_by_its_kind()
    {
        foreach (var field in TagFields.All)
            Assert.Same(field, TagFields.Get(field.Kind));
    }

    [Fact]
    public void Every_field_says_what_it_is_called_and_where_it_belongs()
    {
        foreach (var field in TagFields.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(field.LabelKey), $"{field.Kind} has no label");
            Assert.False(string.IsNullOrWhiteSpace(field.GroupKey), $"{field.Kind} belongs to no group");
        }
    }

    /// <summary>A field that cannot write itself into a file is a field the user edits and then
    /// loses.</summary>
    [Fact]
    public void Every_field_can_write_itself_into_a_track()
    {
        var track = new TrackInfo { FilePath = "/music/Band/Record/01 - Song.flac", Extension = ".flac" };

        foreach (var field in TagFields.All)
            Assert.NotNull(field.Apply(track, "value"));
    }

    /// <summary>"Track [3] of [12]": the partner is drawn by the field it is paired with and never
    /// gets a row of its own, so a pairing that names a field the catalogue does not have would leave
    /// the partner invisible and uneditable.</summary>
    [Fact]
    public void A_paired_field_names_one_that_exists()
    {
        foreach (var field in TagFields.All.Where(f => f.PairedWith is not null))
            Assert.Contains(TagFields.All, f => f.Kind == field.PairedWith);
    }

    [Fact]
    public void The_scoped_views_are_the_whole_catalogue_between_them()
    {
        var scoped = TagFields.TrackOnly().Concat(TagFields.AlbumAndDisc()).Select(f => f.Kind).ToList();

        Assert.Equal(TagFields.All.Count, scoped.Count);
        Assert.Equal(TagFields.All.Select(f => f.Kind).OrderBy(k => k), scoped.OrderBy(k => k));
    }

    /// <summary>
    /// A field with a fixed set of values is drawn as a list in every window: typing at an advisory
    /// flag could only ever produce a fourth state it does not have.
    ///
    /// <para>A blank value is allowed and is not an oversight: "not stated" is the honest answer for
    /// most records, and it is not the same as "clean". What must hold is that no two entries store the
    /// same thing, and that every one of them can be labelled.</para>
    /// </summary>
    [Fact]
    public void A_field_with_options_offers_distinct_labelled_values()
    {
        foreach (var field in TagFields.All.Where(f => f.Options is { Count: > 0 }))
        {
            var values = field.Options!.Select(o => o.Value).ToList();
            Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());

            foreach (var option in field.Options!)
                Assert.False(string.IsNullOrWhiteSpace(option.LabelKey), $"{field.Kind} offers an unlabelled value");
        }
    }

    /// <summary>
    /// A file tagged in French and read in German has to come back as the same flag, so the value is a
    /// fixed token and only the label is translated.
    /// </summary>
    [Theory]
    [InlineData(ContentAdvisory.Explicit)]
    [InlineData(ContentAdvisory.Clean)]
    [InlineData(ContentAdvisory.Unknown)]
    public void An_advisory_flag_survives_being_written_down_and_read_back(ContentAdvisory advisory)
        => Assert.Equal(advisory, TagFields.AdvisoryValues.Parse(TagFields.AdvisoryValues.ToValue(advisory)));

    [Fact]
    public void An_advisory_value_nobody_recognises_reads_as_not_stated()
    {
        Assert.Equal(ContentAdvisory.Unknown, TagFields.AdvisoryValues.Parse(null));
        Assert.Equal(ContentAdvisory.Unknown, TagFields.AdvisoryValues.Parse(""));
        Assert.Equal(ContentAdvisory.Unknown, TagFields.AdvisoryValues.Parse("¯\\_(ツ)_/¯"));
    }

    [Fact]
    public void Every_advisory_state_is_offered()
        => Assert.Equal(
            Enum.GetValues<ContentAdvisory>().Length,
            TagFields.AdvisoryValues.Options.Count);
}
