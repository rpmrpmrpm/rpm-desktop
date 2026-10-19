using RPM.Desktop.Presentation.Main.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// What an album page offers about a record's pressing link, on the reader's own shelf and on one borrowed
/// from somebody else.
///
/// <para>A borrowed shelf is read-only in both directions a link can run. The reader cannot make a link, so
/// an unlinked borrowed record has no groove to invite them in. A linked borrowed record is different. Its
/// groove is RPM's own wall for the album, and the reader writes on it with their own account, whoever's
/// disk holds the copy.</para>
/// </summary>
public class LinkOfferTests
{
    [Fact]
    public void An_own_record_that_is_linked_gets_the_groove_and_the_key()
    {
        var offer = LinkOffer.For(linked: true, borrowed: false);

        Assert.True(offer.Groove);
        Assert.True(offer.LinkKey);
        Assert.False(offer.NameTheLender);
    }

    /// <summary>An unlinked record on the reader's own shelf keeps both. The empty groove explains how
    /// linking works, and the key beside the pressing is how a link is made.</summary>
    [Fact]
    public void An_own_record_that_is_not_linked_keeps_both()
    {
        var offer = LinkOffer.For(linked: false, borrowed: false);

        Assert.True(offer.Groove);
        Assert.True(offer.LinkKey);
        Assert.False(offer.NameTheLender);
    }

    [Fact]
    public void A_borrowed_record_that_is_linked_keeps_the_groove_and_loses_the_key()
    {
        var offer = LinkOffer.For(linked: true, borrowed: true);

        Assert.True(offer.Groove);
        Assert.False(offer.LinkKey);
    }

    /// <summary>This is the case the type exists for: no groove to write on, no key to write with, and a
    /// line that says whose record it is.</summary>
    [Fact]
    public void A_borrowed_record_that_is_not_linked_has_no_groove_and_no_key()
    {
        var offer = LinkOffer.For(linked: false, borrowed: true);

        Assert.False(offer.Groove);
        Assert.False(offer.LinkKey);
        Assert.True(offer.NameTheLender);
    }

    /// <summary>Whether the key is offered depends only on the shelf. A borrowed record never offers it,
    /// and an own record always does, linked or not. Linking only changes what the key says.</summary>
    [Fact]
    public void The_key_follows_the_shelf_and_nothing_else()
    {
        foreach (var linked in new[] { true, false })
        {
            Assert.True(LinkOffer.For(linked, borrowed: false).LinkKey);
            Assert.False(LinkOffer.For(linked, borrowed: true).LinkKey);
        }
    }

    /// <summary>The groove is removed in only one case. It is stated as a property, so that any new case
    /// added later must be thought about, not inherited.</summary>
    [Fact]
    public void The_groove_goes_only_where_it_cannot_be_reached()
    {
        foreach (var linked in new[] { true, false })
            foreach (var borrowed in new[] { true, false })
                Assert.Equal(
                    !(borrowed && !linked),
                    LinkOffer.For(linked, borrowed).Groove);
    }
}
