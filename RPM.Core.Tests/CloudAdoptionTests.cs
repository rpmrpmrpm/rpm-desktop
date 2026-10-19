using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// Taking over an identity the account already stores the same audio under, instead of sending a
/// second copy.
///
/// <para>The saving is real and so is the danger: adopting marks a track as being in the cloud without
/// sending a byte, on the strength of a hash index built from a listing that can be ten minutes old.
/// Every test here is about the moment that index is wrong.</para>
/// </summary>
public class CloudAdoptionTests
{
    private const string Mine = "11111111-1111-4111-8111-111111111111";
    private const string Theirs = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public void The_same_recording_already_in_the_account_is_adopted_rather_than_sent_twice()
        => Assert.True(CloudAdoption.MayAdopt(Theirs, Mine, storeVouches: true));

    /// <summary>
    /// The failure this exists for. A listing ten minutes old can name a track the store no longer
    /// holds, and adopting on that basis writes a row saying "backed up" about music that is nowhere,
    /// which nothing afterwards corrects.
    /// </summary>
    [Fact]
    public void An_identity_the_store_will_not_vouch_for_is_never_adopted()
        => Assert.False(CloudAdoption.MayAdopt(Theirs, Mine, storeVouches: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_index_with_nothing_to_say_is_not_an_identity(string? knownId)
    {
        Assert.False(CloudAdoption.MayAdopt(knownId, Mine, storeVouches: true));
        Assert.False(CloudAdoption.WorthVerifying(knownId, Mine));
    }

    /// <summary>The account holding this audio under the id this very file already carries is not an
    /// adoption: it is this machine's own earlier upload of it, and there is nothing to take over.</summary>
    [Fact]
    public void A_track_does_not_adopt_the_identity_it_already_has()
    {
        Assert.False(CloudAdoption.MayAdopt(Mine, Mine, storeVouches: true));
        Assert.False(CloudAdoption.WorthVerifying(Mine, Mine));
    }

    /// <summary>Asking the store costs a request, so it is only asked where the answer could change
    /// anything.</summary>
    [Fact]
    public void The_store_is_only_asked_about_an_identity_worth_taking()
    {
        Assert.True(CloudAdoption.WorthVerifying(Theirs, Mine));
        Assert.True(CloudAdoption.WorthVerifying(Theirs, currentId: null));
    }

    /// <summary>A file that has never been stamped can adopt: it has no identity of its own to
    /// compare, which is exactly the case of a fresh import of a record another install uploaded.</summary>
    [Fact]
    public void A_file_with_no_identity_yet_can_take_one()
        => Assert.True(CloudAdoption.MayAdopt(Theirs, currentId: null, storeVouches: true));

    /// <summary>
    /// The property that keeps the two halves correct: nothing is ever adopted that the store was not
    /// asked about in that moment, whatever the index said.
    /// </summary>
    [Fact]
    public void Nothing_is_adopted_on_the_index_alone()
    {
        foreach (var knownId in new[] { Theirs, Mine, null, "" })
        foreach (var currentId in new[] { Mine, null })
            Assert.False(CloudAdoption.MayAdopt(knownId, currentId, storeVouches: false));
    }
}
