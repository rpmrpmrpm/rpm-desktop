namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The text sizes of the Feed and the pages it opens. They are the sizes the rest of the app uses for the
/// same kind of text (the track pane beside the Feed, the artist page and its wall), so the Feed reads at
/// the same size as everything around it. Every text on the Feed's pages takes its size from here, by what
/// the text is. Two things are not page text and keep the app's own sizes: the menus
/// (<see cref="FeedCards.MenuItem"/>, the app's menu size) and the report dialog (the artist page's
/// report dialog).
/// </summary>
internal static class FeedType
{
    /// <summary>The name a page is about: the name on a person's page. The track pane's title.</summary>
    public const double PageTitle = 18;

    /// <summary>What a card or a page is about: the record on a review, the title over the notifications and
    /// over the edit page. The track pane's artist and album names.</summary>
    public const double Subject = 14;

    /// <summary>The heading of a card on the edit page (Profile photo, Banner), semibold. The track pane's
    /// and the artist page's block headings.</summary>
    public const double Section = 12;

    /// <summary>A person's name over what they wrote, semibold. The artist page's wall.</summary>
    public const double Name = 12;

    /// <summary>Running text: a post, a reply, a review, a notification, a field, a key's words and a line
    /// that explains. The artist page's prose and wall, and the app's text fields.</summary>
    public const double Body = 12;

    /// <summary>The height of a line of running text where the Feed sets it (a review run round its cover).
    /// The artist page's prose.</summary>
    public const double BodyLine = 18;

    /// <summary>When something was written, a count, the song under a post, what unlocks a badge. The times
    /// and counts on the artist page's wall.</summary>
    public const double Meta = 11;

    /// <summary>A glyph that stands beside a count or a line of text: the vote marks, the reply mark.</summary>
    public const double Glyph = 11;

    /// <summary>The stars of a review. The album page's stars.</summary>
    public const double Stars = 12;
}
