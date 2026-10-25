using System.Net;
using System.Text;
using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>The admin's artist edits: how they change the related artists row, and how the API's answer is
/// read.</summary>
public class ArtistEditsTests
{
    private static ArtistWallItem Act(string name) => new(name, [name + "/record"], [], [], 10);

    private static readonly ArtistWallItem Me = Act("Me");
    private static readonly ArtistWallItem A = Act("A");
    private static readonly ArtistWallItem B = Act("B");
    private static readonly ArtistWallItem C = Act("C");
    private static readonly IReadOnlyList<ArtistWallItem> Wall = [Me, A, B, C];

    [Fact]
    public void No_edits_keep_the_collections_own_row()
    {
        IReadOnlyList<ArtistWallItem> computed = [A, B];

        Assert.Same(computed, ArtistEdit.None.Relate(computed, Me, Wall));
    }

    [Fact]
    public void The_admins_acts_come_first_and_the_unrelated_ones_go()
    {
        var edit = new ArtistEdit { Related = ["c", "Nobody here"], Unrelated = ["B"] };

        var row = edit.Relate([A, B], Me, Wall);

        Assert.Equal(["C", "A"], row.Select(a => a.Name));
    }

    [Fact]
    public void The_act_itself_is_never_related_even_when_the_admin_names_it()
    {
        var edit = new ArtistEdit { Related = ["me", "A"] };

        Assert.Equal(["A"], edit.Relate([], Me, Wall).Select(a => a.Name));
    }

    [Fact]
    public void An_empty_description_keeps_the_encyclopedias()
    {
        Assert.Null(new ArtistEdit { Description = "  " }.Text);
        Assert.Equal("Words.", new ArtistEdit { Description = " Words. " }.Text);
    }

    [Fact]
    public async Task The_answer_is_read_and_kept_and_its_other_fields_are_ignored()
    {
        var handler = new Answering(HttpStatusCode.OK,
            """{"description":"An act.","wide":["x"],"square":[],"hidden":[],"related":["A"],"unrelated":[]}""");
        var client = new ArtistEditsClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") });

        var edit = await client.OfAsync(" The Act ");
        var again = await client.OfAsync("the act");

        Assert.Equal("An act.", edit.Text);
        Assert.Equal(["A"], edit.Related!);
        Assert.Same(edit, again);
        Assert.Equal(1, handler.Asked);
        Assert.Equal("https://api.test/artist/edits?name=The%20Act", handler.LastUrl);
        Assert.Same(edit, client.Peek("THE ACT"));
    }

    [Fact]
    public async Task A_failed_ask_is_no_edits_and_is_asked_again()
    {
        var handler = new Answering(HttpStatusCode.InternalServerError, "");
        var client = new ArtistEditsClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") });

        Assert.Same(ArtistEdit.None, await client.OfAsync("Act"));
        Assert.Null(client.Peek("Act"));
        await client.OfAsync("Act");
        Assert.Equal(2, handler.Asked);
    }

    private sealed class Answering(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Asked { get; private set; }
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked++;
            LastUrl = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
