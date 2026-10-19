using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The rule for recording that a recording is safely in the cloud: the store must have just said so,
/// about this exact recording.
///
/// <para>Every test here is a case that refuses. A row that claims a track is backed up when the
/// store holds nothing under that id is never corrected afterwards.</para>
/// </summary>
public class CloudVouchTests
{
    private const string Id = "6f1c0d3e-6f1c-4d3e-8f1c-0d3e6f1c0d3e";
    private const string Hash = "0f8a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8";

    private static CloudVerification Ok(string? sha = Hash) => new(Id, Ok: true, Sha256: sha);

    [Fact]
    public void The_store_saying_yes_about_this_exact_recording_is_the_one_way_to_pass()
        => Assert.True(CloudVouch.Vouches(Ok(), Hash));

    /// <summary>An empty answer means a machine that went offline, or an API too old to support the
    /// request. It confirms nothing.</summary>
    [Fact]
    public void No_answer_at_all_vouches_for_nothing()
        => Assert.False(CloudVouch.Vouches(null, Hash));

    [Fact]
    public void An_answer_that_says_no_is_a_no()
        => Assert.False(CloudVouch.Vouches(new CloudVerification(Id, Ok: false, Reason: "no sidecar"), Hash));

    /// <summary>
    /// The subtle case. If the checksum stored under this id is not the checksum this library holds,
    /// the two disagree about which recording the id names. The file on this disk is the one the user
    /// can actually hear.
    /// </summary>
    [Fact]
    public void A_store_holding_a_different_recording_under_the_same_id_does_not_vouch()
        => Assert.False(CloudVouch.Vouches(Ok("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), Hash));

    [Fact]
    public void A_checksum_that_differs_only_in_case_is_the_same_checksum()
        => Assert.True(CloudVouch.Vouches(Ok(Hash.ToUpperInvariant()), Hash));

    /// <summary>A row with no recorded hash has nothing to compare, so the store's own "yes" is the
    /// answer.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_track_whose_hash_this_library_never_recorded_rests_on_the_stores_word(string? mine)
        => Assert.True(CloudVouch.Vouches(Ok(), mine));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_store_that_reports_no_checksum_is_taken_at_its_word(string? theirs)
        => Assert.True(CloudVouch.Vouches(Ok(theirs), Hash));
}
