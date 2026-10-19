using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Tests;

/// <summary>
/// When a record's papers name two different people, which one to believe.
///
/// <para>For example, a copy of <i>Maid in England</i> by Divine can come back with the right record
/// and an artist card for DiVine, a Dutch act that has nothing to do with it, while the record in the
/// same answer points directly to the right MusicBrainz id.</para>
/// </summary>
public class PapersAgreeTests
{
    /// <summary>The drag performer, Harris Glenn Milstead.</summary>
    private const string Divine = "9572db59-da29-4f9d-bc00-0287ee3386e9";

    /// <summary>A Dutch act of nearly the same name.</summary>
    private const string DiVineNl = "e684a31e-7167-46bb-a626-119043b3d33a";

    private static CatalogPapers Papers(string? artistId, string? albumArtistId, string? albumArtist = "Divine")
        => new(
            Artist: artistId is null ? null : new CatalogArtist("DiVine", Id: artistId),
            Album: new CatalogAlbum("Maid in England", Artist: albumArtist, ArtistId: albumArtistId));

    [Fact]
    public void The_record_wins_where_the_two_disagree()
        => Assert.Equal(Divine, PapersAgree.ArtistTheRecordNames(Papers(DiVineNl, Divine)));

    [Fact]
    public void They_agree_and_nothing_is_asked()
        => Assert.Null(PapersAgree.ArtistTheRecordNames(Papers(Divine, Divine)));

    /// <summary>The ids are from one catalogue and are not case-sensitive.</summary>
    [Fact]
    public void Agreement_does_not_turn_on_case()
        => Assert.Null(PapersAgree.ArtistTheRecordNames(Papers(Divine.ToUpperInvariant(), Divine)));

    /// <summary>An act recognised by name alone has no id. The record's id is then not a second
    /// opinion, it is the only one.</summary>
    [Fact]
    public void An_act_with_no_id_takes_the_records()
    {
        Assert.Equal(Divine, PapersAgree.ArtistTheRecordNames(Papers(null, Divine)));
        Assert.Equal(
            Divine,
            PapersAgree.ArtistTheRecordNames(
                new CatalogPapers(
                    Artist: new CatalogArtist("Divine"),
                    Album: new CatalogAlbum("Maid in England", Artist: "Divine", ArtistId: Divine))));
    }

    /// <summary>The one case where the record's own credit is certainly not the act being looked up.
    /// Believing it would put a card about nobody on every track of every compilation.</summary>
    /// <remarks>The spellings are <see cref="ArtistCredit.VariousSpellings"/>'s, normalised for case
    /// and punctuation like every other name in this app. "VA" is deliberately not among them and is
    /// not tested here: adding to that list affects every compilation in the library, not only this
    /// one lookup.</remarks>
    [Theory]
    [InlineData("Various Artists")]
    [InlineData("various artists")]
    [InlineData("Various")]
    public void A_compilation_credit_is_refused(string credited)
        => Assert.Null(PapersAgree.ArtistTheRecordNames(
            Papers(DiVineNl, "89ad4ac3-39f7-470e-963a-56509c546377", credited)));

    [Fact]
    public void A_record_that_names_nobody_changes_nothing()
    {
        Assert.Null(PapersAgree.ArtistTheRecordNames(Papers(DiVineNl, null)));
        Assert.Null(PapersAgree.ArtistTheRecordNames(Papers(DiVineNl, "")));
        Assert.Null(PapersAgree.ArtistTheRecordNames(Papers(DiVineNl, "   ")));
    }

    [Fact]
    public void No_record_at_all_changes_nothing()
    {
        Assert.Null(PapersAgree.ArtistTheRecordNames(
            new CatalogPapers(Artist: new CatalogArtist("DiVine", Id: DiVineNl))));
        Assert.Null(PapersAgree.ArtistTheRecordNames(CatalogPapers.Empty));
        Assert.Null(PapersAgree.ArtistTheRecordNames(null));
    }

    // ── putting the more reliable act in ──

    [Fact]
    public void The_truer_act_replaces_the_one_that_came_with_the_papers()
    {
        var papers = Papers(DiVineNl, Divine);
        var truer = new CatalogArtist("Divine", Id: Divine, Kind: "Person");

        var settled = PapersAgree.WithArtist(papers, truer);

        Assert.Equal(Divine, settled.Artist?.Id);
        Assert.Equal("Divine", settled.Artist?.Name);
    }

    /// <summary>The record itself is not in doubt: it is the half that was matched on more than a
    /// name, and it is what named the act that replaces the other one.</summary>
    [Fact]
    public void The_record_is_left_exactly_as_it_was()
    {
        var papers = Papers(DiVineNl, Divine);

        var settled = PapersAgree.WithArtist(papers, new CatalogArtist("Divine", Id: Divine));

        Assert.Same(papers.Album, settled.Album);
    }

    /// <summary>The lookup can fail. A card about the wrong act is bad; a card about nobody, where
    /// there was a card a moment ago, is worse.</summary>
    [Fact]
    public void A_lookup_that_answered_nothing_leaves_the_papers_alone()
    {
        var papers = Papers(DiVineNl, Divine);

        Assert.Same(papers, PapersAgree.WithArtist(papers, null));
        Assert.Same(papers, PapersAgree.WithArtist(papers, new CatalogArtist("")));
    }
}
