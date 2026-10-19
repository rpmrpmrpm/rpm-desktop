using System.ComponentModel;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// The tree's ticks. A box stands for everything under it, at every level: an artist's tick is its
/// records' tick and its tracks' tick, and each of those has a box on screen that has to say so.
/// </summary>
public class QueueTickTests
{
    private static ImportNode Artist(string name) =>
        new() { Name = name, Depth = 0, GroupArtist = name };

    private static ImportNode Album(ImportNode artist, string name, int tracks)
    {
        var node = new ImportNode
        {
            Name = name,
            Depth = 1,
            GroupArtist = artist.GroupArtist,
            GroupAlbum = name,
            VirtualTrackCount = tracks,
        };
        node.Parent = artist;
        artist.Children.Add(node);
        node.SetCheckState(0, tracks);
        return node;
    }

    /// <summary>The store's side of a group tick, which the source supplies. Here it only records that
    /// it was asked.</summary>
    private static (ImportNode Artist, List<ImportNode> Albums, List<bool> Asked) Tree()
    {
        var artist = Artist("Enya");
        var albums = new List<ImportNode>
        {
            Album(artist, "Watermark", 11),
            Album(artist, "Shepherd Moons", 12),
        };
        artist.SetCheckState(0, 23);

        var asked = new List<bool>();
        artist.FolderCheckAll = on => asked.Add(on);
        foreach (var album in albums)
            album.FolderCheckAll = on => asked.Add(on);

        return (artist, albums, asked);
    }

    private static List<string> Watch(ImportNode node)
    {
        var raised = new List<string>();
        ((INotifyPropertyChanged)node).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        return raised;
    }

    // ── downwards ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_artists_tick_is_its_records_tick()
    {
        var (artist, albums, _) = Tree();

        artist.IsChecked = true;

        Assert.True(albums[0].IsChecked);
        Assert.True(albums[1].IsChecked);
    }

    /// <summary>Each record has a box of its own on screen, so each of them has to be told to re-read
    /// it. A record left looking untouched while every track under it is ticked is the same bug as a
    /// tick that did not take.</summary>
    [Fact]
    public void Every_record_underneath_re_reads_its_own_box()
    {
        var (artist, albums, _) = Tree();
        var first = Watch(albums[0]);
        var second = Watch(albums[1]);

        artist.IsChecked = true;

        Assert.Contains(nameof(ImportNode.IsChecked), first);
        Assert.Contains(nameof(ImportNode.IsChecked), second);
    }

    [Fact]
    public void Unticking_an_artist_unticks_its_records()
    {
        var (artist, albums, _) = Tree();
        artist.IsChecked = true;

        artist.IsChecked = false;

        Assert.False(albums[0].IsChecked);
        Assert.False(albums[1].IsChecked);
    }

    /// <summary>The store is asked once, by the group that was ticked. Its records' rows are paged, so
    /// there is nothing in memory to walk and no reason for each of them to ask again.</summary>
    [Fact]
    public void The_store_is_asked_once()
    {
        var (artist, _, asked) = Tree();

        artist.IsChecked = true;

        Assert.Equal([true], asked);
    }

    // ── upwards ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_record_half_ticked_leaves_its_artist_part_way()
    {
        var (artist, albums, _) = Tree();

        albums[0].IsChecked = true;

        Assert.True(albums[0].IsChecked);
        Assert.False(albums[1].IsChecked);
        Assert.Null(artist.IsChecked);      // some, not all
    }

    [Fact]
    public void Every_record_ticked_ticks_the_artist()
    {
        var (artist, albums, _) = Tree();

        albums[0].IsChecked = true;
        albums[1].IsChecked = true;

        Assert.True(artist.IsChecked);
    }

    [Fact]
    public void A_track_ticked_reaches_all_the_way_up()
    {
        var (artist, albums, _) = Tree();
        var leaf = Leaf(albums[0]);

        leaf.IsChecked = true;

        Assert.Null(albums[0].IsChecked);
        Assert.Null(artist.IsChecked);
    }

    // ── the bulk untick ─────────────────────────────────────────────────────────────────────────

    /// <summary>"Select none" from the bar writes the store in one statement and then tells the tree. The
    /// tree is what the boxes are read off, so every level of it has to be told: an artist and a record
    /// left half-ticked over an empty queue is the same wrong answer twice.</summary>
    [Fact]
    public void A_queue_wide_untick_empties_every_group()
    {
        var (artist, albums, _) = Tree();
        albums[0].IsChecked = true;
        Assert.Null(artist.IsChecked);

        artist.SetCheckedThroughout(false);

        Assert.False(artist.IsChecked);
        Assert.False(albums[0].IsChecked);
        Assert.False(albums[1].IsChecked);
    }

    [Fact]
    public void A_queue_wide_tick_fills_every_group()
    {
        var (artist, albums, _) = Tree();

        artist.SetCheckedThroughout(true);

        Assert.True(artist.IsChecked);
        Assert.True(albums[0].IsChecked);
        Assert.True(albums[1].IsChecked);
    }

    /// <summary>And each of those boxes re-reads itself, or the counter is right in memory and wrong
    /// on screen: which is exactly what a stale record row looks like.</summary>
    [Fact]
    public void Every_group_re_reads_its_box_after_a_queue_wide_change()
    {
        var (artist, albums, _) = Tree();
        var above = Watch(artist);
        var below = Watch(albums[1]);

        artist.SetCheckedThroughout(false);

        Assert.Contains(nameof(ImportNode.IsChecked), above);
        Assert.Contains(nameof(ImportNode.IsChecked), below);
    }

    private static ImportNode Leaf(ImportNode album)
    {
        var info = new TrackInfo { FilePath = "/m/a.flac", Extension = ".flac", Title = "Orinoco Flow" };
        return new ImportNode
        {
            Name = "Orinoco Flow",
            Depth = album.Depth + 1,
            Row = TrackRow.FromStored(info, info, false),
            Parent = album,
            QueueId = 1,
        };
    }
}
