using RPM.Fingerprint;

namespace RPM.Fingerprint.Tests;

/// <summary>
/// How a key is spelled. It becomes a primary key in a table two functions write to, so the things
/// that matter are that one record cannot end up under two spellings of one name, and that nothing a
/// client sends reaches the table without being recognised first.
/// </summary>
public class RecordingKeyTests
{
    private const string Sha = "AA11BB22CC33DD44EE55FF6600778899AABBCCDDEEFF00112233445566778899";

    [Fact]
    public void A_hash_is_normalised_to_lower_case()
    {
        var key = RecordingKey.Canonical(Sha);

        Assert.NotNull(key);
        Assert.Equal("canon:" + Sha.ToLowerInvariant(), key!.Value.ToString());
    }

    [Fact]
    public void Surrounding_space_does_not_make_a_second_key()
    {
        Assert.Equal(RecordingKey.Samples(Sha), RecordingKey.Samples("  " + Sha + "  "));
    }

    [Fact]
    public void Two_kinds_never_collide_on_one_value()
    {
        Assert.NotEqual(RecordingKey.Canonical(Sha), RecordingKey.Samples(Sha));
    }

    [Fact]
    public void Something_that_is_not_a_hash_is_not_a_key()
    {
        Assert.Null(RecordingKey.Canonical("nope"));
        Assert.Null(RecordingKey.Canonical(Sha[..63]));
        Assert.Null(RecordingKey.Canonical(Sha[..63] + "z"));
        Assert.Null(RecordingKey.Canonical(null));
    }

    [Fact]
    public void A_track_id_is_normalised_through_its_own_type()
    {
        // Two libraries writing one id in two shapes must not become two keys.
        var braced = RecordingKey.Rpm("{6F9619FF-8B86-D011-B42D-00CF4FC964FF}");
        var plain = RecordingKey.Rpm("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

        Assert.NotNull(braced);
        Assert.Equal(plain, braced);
    }

    [Fact]
    public void An_empty_track_id_is_not_an_identity()
    {
        Assert.Null(RecordingKey.Rpm(Guid.Empty.ToString()));
        Assert.Null(RecordingKey.Rpm("not-a-guid"));
    }

    [Fact]
    public void A_key_survives_the_round_trip_it_is_written_for()
    {
        foreach (var key in new[]
                 {
                     RecordingKey.Canonical(Sha)!.Value,
                     RecordingKey.Samples(Sha)!.Value,
                     RecordingKey.Rpm("6f9619ff-8b86-d011-b42d-00cf4fc964ff")!.Value,
                     RecordingKey.SongRow(41)!.Value,
                 })
        {
            Assert.Equal(key, RecordingKey.Parse(key.ToString()));
        }
    }

    [Fact]
    public void A_kind_this_build_does_not_know_is_refused()
    {
        Assert.Null(RecordingKey.Parse("acoustid:" + Sha));
        Assert.Null(RecordingKey.Parse("canon"));
        Assert.Null(RecordingKey.Parse(":" + Sha));
        Assert.Null(RecordingKey.Parse("canon:"));
        Assert.Null(RecordingKey.Parse(null));
    }

    [Fact]
    public void A_bundle_keeps_what_it_recognises_and_drops_the_rest()
    {
        // A build that learns a fifth kind before this one does should still get its other keys stored,
        // rather than having the whole bundle refused for the one word this build cannot read.
        var keys = RecordingKey.ParseAll(
        [
            "canon:" + Sha,
            "acoustid:whatever",
            "samples:" + Sha,
            "canon:" + Sha.ToLowerInvariant(),   // the same key twice
        ]);

        Assert.Equal(2, keys.Count);
        Assert.Equal("canon:" + Sha.ToLowerInvariant(), keys[0].ToString());
        Assert.Equal("samples:" + Sha.ToLowerInvariant(), keys[1].ToString());
    }
}

/// <summary>
/// What a bundle of keys is allowed to write. The one place in the shared pool where being wrong is
/// permanent for everybody, so: adding is cheap and allowed, joining two records is not and is not.
/// </summary>
public class RecordingGraphTests
{
    private const string ShaA = "aa11bb22cc33dd44ee55ff6600778899aabbccddeeff00112233445566778899";
    private const string ShaB = "bb11bb22cc33dd44ee55ff6600778899aabbccddeeff00112233445566778899";
    private const string ShaC = "cc11bb22cc33dd44ee55ff6600778899aabbccddeeff00112233445566778899";

    private static readonly RecordingKey Canon = RecordingKey.Canonical(ShaA)!.Value;
    private static readonly RecordingKey Samples = RecordingKey.Samples(ShaB)!.Value;
    private static readonly RecordingKey Other = RecordingKey.Samples(ShaC)!.Value;
    private static readonly RecordingKey Track = RecordingKey.Rpm("6f9619ff-8b86-d011-b42d-00cf4fc964ff")!.Value;

    private static Dictionary<string, KeyBinding> Known(params KeyBinding[] bindings) =>
        bindings.ToDictionary(b => b.Key.ToString(), b => b, StringComparer.Ordinal);

    private static BundleOutcome Admit(IReadOnlyList<RecordingKey> bundle, Dictionary<string, KeyBinding> known) =>
        RecordingGraph.Admit(bundle, known, () => "rec-new");

    [Fact]
    public void Audio_nobody_has_met_starts_a_record()
    {
        var outcome = Admit([Canon, Samples], Known());

        Assert.True(outcome.IsNew);
        Assert.Equal("rec-new", outcome.RecordingId);
        Assert.Equal([Canon, Samples], outcome.Bind);
        Assert.Empty(outcome.Affirm);
        Assert.False(outcome.IsConflict);
    }

    [Fact]
    public void A_new_encoding_of_a_known_record_joins_it()
    {
        // The case the whole graph exists for: somebody's FLAC and somebody else's ALAC of one master,
        // met on different machines, ending on one row.
        var outcome = Admit([Canon, Samples], Known(new KeyBinding(Samples, "rec-1", 3)));

        Assert.False(outcome.IsNew);
        Assert.Equal("rec-1", outcome.RecordingId);
        Assert.Equal([Canon], outcome.Bind);
        Assert.Equal([Samples], outcome.Affirm);
    }

    [Fact]
    public void Seeing_a_record_again_says_so_and_writes_nothing_new()
    {
        var outcome = Admit(
            [Canon, Samples],
            Known(new KeyBinding(Canon, "rec-1", 1), new KeyBinding(Samples, "rec-1", 1)));

        Assert.Equal("rec-1", outcome.RecordingId);
        Assert.Empty(outcome.Bind);
        Assert.Equal([Canon, Samples], outcome.Affirm);
    }

    [Fact]
    public void A_bundle_that_would_weld_two_records_together_writes_nothing()
    {
        // The dangerous edge. One mis-tagged file must not be able to make two pieces of music into
        // one for everybody who comes afterwards.
        var outcome = Admit(
            [Canon, Samples, Track],
            Known(new KeyBinding(Canon, "rec-1", 5), new KeyBinding(Samples, "rec-2", 4)));

        Assert.True(outcome.IsConflict);
        Assert.Null(outcome.RecordingId);
        Assert.Empty(outcome.Bind);
        Assert.Empty(outcome.Affirm);
        Assert.Equal([Canon, Samples], outcome.Disputed);
    }

    [Fact]
    public void A_disputed_bundle_does_not_get_to_attach_its_new_keys_either()
    {
        // Trusting part of a bundle that is proven wrong is still trusting it.
        var outcome = Admit(
            [Canon, Samples, Other],
            Known(new KeyBinding(Canon, "rec-1", 1), new KeyBinding(Samples, "rec-2", 1)));

        Assert.DoesNotContain(Other, outcome.Bind);
    }

    [Fact]
    public void One_key_on_its_own_is_not_a_bundle()
    {
        // Nothing was observed together, so nothing was learnt. A lone key can only repeat what the
        // store already knows or name something it can never resolve.
        Assert.Same(BundleOutcome.Nothing, Admit([Canon], Known()));
        Assert.Same(BundleOutcome.Nothing, Admit([], Known()));
        Assert.Same(BundleOutcome.Nothing, Admit([Canon], Known(new KeyBinding(Canon, "rec-1", 9))));
    }

    [Fact]
    public void A_record_met_by_three_names_at_once_takes_all_three()
    {
        var outcome = Admit([Canon, Samples, Track], Known(new KeyBinding(Track, "rec-7", 2)));

        Assert.Equal("rec-7", outcome.RecordingId);
        Assert.Equal([Canon, Samples], outcome.Bind);
        Assert.Equal([Track], outcome.Affirm);
    }
}
