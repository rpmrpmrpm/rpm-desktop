using RPM.Desktop.Presentation.Import;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which of the import grid's optional columns are down, as it is written to settings and read back.
///
/// <para>What is stored is the list of hidden columns. The most important test is the last one: a column
/// that did not exist when the setting was written must be shown. Otherwise every column added in a
/// later version would be hidden from everyone who had opened the picker before.</para>
/// </summary>
public class ImportColumnsTests
{
    [Fact]
    public void Nothing_hidden_is_stored_as_no_opinion()
    {
        Assert.Null(ImportColumns.Format([]));
        Assert.Empty(ImportColumns.Parse(null));
        Assert.Empty(ImportColumns.Parse(""));
    }

    [Fact]
    public void Ids_survive_the_round_trip()
    {
        var stored = ImportColumns.Format(["Codec", "Bitrate", "Size"]);

        Assert.Equal(["Bitrate", "Codec", "Size"], ImportColumns.Parse(stored).Order());
    }

    [Fact]
    public void Blanks_and_repeats_are_dropped_before_they_are_written()
    {
        Assert.Equal("Codec,Size", ImportColumns.Format(["Codec", " ", "codec", "Size", ""]));
    }

    [Fact]
    public void Spacing_in_a_hand_edited_setting_is_forgiven()
    {
        Assert.Equal(["Codec", "Size"], ImportColumns.Parse(" Codec , Size ,, ").Order());
    }

    [Fact]
    public void A_column_the_setting_never_heard_of_is_shown()
    {
        var hidden = ImportColumns.Parse("Codec,Bitrate");

        Assert.DoesNotContain("Channels", hidden);
    }
}
