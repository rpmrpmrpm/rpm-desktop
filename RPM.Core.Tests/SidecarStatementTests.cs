using System.Reflection;
using RPM.Core.Cloud;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// A sidecar read with the names of the fields it states. A field it leaves out is "not known there"
/// and a field it states as empty is "empty there". The second is how an edit made in the web app
/// empties a field in a file on this disk.
/// </summary>
public class SidecarStatementTests
{
    private static TrackInfo File() => new()
    {
        FilePath = "a.flac", Extension = ".flac", Title = "Song", Conductor = "Karajan", Year = 1990, Bpm = 120,
    };

    [Fact]
    public void Every_field_of_the_sidecar_is_a_tag_or_named_as_not_one()
    {
        // A field added to the sidecar and left out of both lists would never reach a file from a pull.
        var properties = typeof(TagSidecar).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsStatic: false } && p.Name != "EqualityContract")
            .Select(p => p.Name)
            .ToHashSet();
        var tags = SidecarStatement.Tags.Select(t => t.Name).ToHashSet();

        Assert.Empty(properties.Except(tags).Except(SidecarStatement.NotTags));
        Assert.Empty(tags.Except(properties));
        Assert.Empty(tags.Intersect(SidecarStatement.NotTags));
    }

    [Fact]
    public void A_field_stated_as_empty_empties_it_and_one_left_out_keeps_it()
    {
        var said = SidecarStatement.Read("""{"RelativePath":"a.flac","Extension":".flac","Conductor":null,"Year":0}""")!;

        var over = said.Over(File());

        Assert.Null(over.Conductor);
        Assert.Equal(0u, over.Year);
        Assert.Equal("Song", over.Title);
        Assert.Equal(120u, over.Bpm);
    }

    [Fact]
    public void A_sidecar_written_by_this_app_states_its_numbers_and_leaves_out_empty_text()
    {
        var json = new TagSidecar { RelativePath = "a.flac", Extension = ".flac", Title = "Song" }.ToJson();

        var said = SidecarStatement.Read(json)!;

        Assert.Contains("Year", said.Stated);
        Assert.DoesNotContain("Conductor", said.Stated);
        Assert.Equal("Karajan", said.Over(File()).Conductor);
    }

    [Fact]
    public void Text_that_is_not_a_sidecar_is_no_statement()
    {
        Assert.Null(SidecarStatement.Read("[]"));
        Assert.Null(SidecarStatement.Read("not json"));
    }

    [Fact]
    public void The_picture_is_replaced_only_when_it_differs_and_removed_only_when_stated()
    {
        var replaced = SidecarStatement.Read("""{"RelativePath":"a","Extension":".flac","Art":"CQ=="}""")!;
        Assert.Equal(new byte[] { 9 }, replaced.ArtOver([1, 2]));
        Assert.Null(replaced.ArtOver([9]));

        var removed = SidecarStatement.Read("""{"RelativePath":"a","Extension":".flac","Art":null}""")!;
        Assert.Equal([], removed.ArtOver([1, 2])!);
        Assert.Null(removed.ArtOver(null));

        var silent = SidecarStatement.Read("""{"RelativePath":"a","Extension":".flac"}""")!;
        Assert.Null(silent.ArtOver([1, 2]));
    }
}
