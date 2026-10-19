using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// How a shelf dresses a record: ask for the cover, show it, and where nothing came back, say so.
///
/// <para><b>Written once, because the same mistake was made four times.</b> The artist page, the genre
/// page, the home row and the album page's row of suggestions each had their own copy of "fetch, decode,
/// assign". Each copy stopped at the assignment. None of them said "there is no picture", which is the
/// fact that the made sleeve depends on. So every card on those pages drew a Skia paint and a blur when
/// it was realised, and then hid it behind real artwork a moment later. The wall did not have this
/// fault, because its reads go through <see cref="CoverStream"/>, which does say it. This method says
/// the same thing for the shelves that are not walls.</para>
///
/// <para><b>What is shared is the verdict, not the fetching.</b> Each shelf looks for its bytes in its
/// own way, and the four ways differ. One uses a loader handed in from the host. One reads a folder and
/// then falls back to the network. One draws a cover for a generated list. That is
/// <paramref name="fetch"/>. No shelf may decide for itself what an empty answer means. With no picture
/// on the card, an empty answer means the art does not exist. With a picture on the card, it means the
/// read failed, and it must not latch. See <see cref="CoverTier.Latches"/>.</para>
///
/// <para><b>The cards are looked up after the answer lands, never before.</b> A fetch is a disk read or
/// a network round trip, and the page can be scrolled, rebuilt or left during it. So the caller passes a
/// way to FIND the cards, not the cards themselves. The cards are found against the shelf as it is when
/// there is finally something to write. A list captured before the await may hold cards that are no
/// longer on the page. This follows the house rule on order-independence over time windows.</para>
/// </summary>
public static class CoverFill
{
    /// <summary>
    /// Dresses every card that stands for one record.
    /// </summary>
    /// <param name="cards">Finds the cards, and is called once the read has answered. There can be more
    /// than one card, because the same record can stand on two shelves of one page and only one of them
    /// did the asking.</param>
    /// <param name="fetch">Gets this shelf's bytes. A null or empty result matters most, because it
    /// decides whether the record is marked as having no art.</param>
    /// <param name="decode">Turns bytes into a picture at the size and pace the shelf uses. A genre page
    /// limits its decodes against the frame rate. A row of six does not.</param>
    public static async Task DressAsync(
        Func<IEnumerable<ISleeveCard>> cards,
        Func<CancellationToken, Task<byte[]?>> fetch,
        Func<byte[], Task<ImageSource?>> decode,
        CancellationToken ct)
    {
        var bytes = await fetch(ct);

        // A cancelled read is not a verdict. The page was left, or the shelf was rebuilt during the
        // fetch. A card marked as having no cover on that basis would keep showing a made sleeve for the
        // rest of the session, even though its artwork was never looked for.
        if (ct.IsCancellationRequested)
            return;

        if (bytes is not { Length: > 0 })
        {
            foreach (var card in cards())
                if (CoverTier.Latches(card.HasCover))
                    card.CoverMissing = true;
            return;
        }

        var art = await decode(bytes);
        if (art is null || ct.IsCancellationRequested)
            return;

        foreach (var card in cards())
            card.Cover = art;
    }

    /// <summary>The same, for a shelf where each record is one card. Every shelf works this way except the
    /// genre page. There, a record stands under "newest" and in its decade at once.</summary>
    public static Task DressAsync(
        ISleeveCard card,
        Func<CancellationToken, Task<byte[]?>> fetch,
        Func<byte[], Task<ImageSource?>> decode,
        CancellationToken ct)
        => DressAsync(() => [card], fetch, decode, ct);
}
