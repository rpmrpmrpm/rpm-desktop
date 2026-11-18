using RPM.Core.Analysis;
using RPM.Core.Cloud;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The CLAP audio model, run in the cloud, for a computer that cannot run the beat model (see
/// <see cref="Audio.DjModelService"/>, whose verdict this follows).
///
/// <para>The split is the one <see cref="CloudBeatModel"/> makes. The features are built on this machine,
/// because that is an FFT and every machine can do it. Only the features cross the network, about a
/// megabyte for three clips, at single precision. The store runs the same model file the app ships and
/// answers with the model's own output, one embedding per clip, which <see cref="SoundEmbedding.Average"/>
/// turns into the record's embedding here, as it does for weights opened from disk.</para>
///
/// <para><b>A refusal does not end the session.</b> The beat model's cloud stand-in stops after one
/// refusal, because a set's look-ahead cannot afford to wait on a store that is not answering. The sound
/// pass has no look-ahead: a refusal means the store is busy or out of reach for now. <see cref="Embed"/>
/// returns null, the pass stops this round without writing anything (see
/// <see cref="SoundOutcome.ModelDidNotAnswer"/>), and the next round asks again.</para>
/// </summary>
public sealed class CloudSoundModel(RpmCloudClient cloud, AuthService auth) : ISoundModel
{
    /// <summary>True when somebody is signed in. The store answers only a signed-in caller.</summary>
    public bool IsAvailable => auth.IsLoggedIn;

    /// <inheritdoc />
    public float[][]? Embed(IReadOnlyList<float[]> features, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (!IsAvailable || features.Count == 0 || features.Any(f => f.Length != ClapFeatures.ValuesPerClip))
            return null;

        ct.ThrowIfCancellationRequested();

        var all = new float[features.Count * ClapFeatures.ValuesPerClip];
        for (int c = 0; c < features.Count; c++)
            features[c].CopyTo(all, c * ClapFeatures.ValuesPerClip);

        // Synchronous on purpose, as in CloudBeatModel: it runs inside a pass that is already on a
        // background thread, with nothing else to do. Not cancellable once sent, for the reason given at
        // CloudBeatModel.Ask: a send cut off the socket cannot be told apart from a refusal.
        var said = cloud.ReadSoundAsync(GridWire.Pack(all, GridPrecision.Single), features.Count, CancellationToken.None)
                        .GetAwaiter().GetResult();

        var values = GridWire.Unpack(said, features.Count * SoundEmbedding.Dimensions, GridPrecision.Single);
        if (values is null)
            return null;

        var embeddings = new float[features.Count][];
        for (int c = 0; c < features.Count; c++)
            embeddings[c] = values.AsSpan(c * SoundEmbedding.Dimensions, SoundEmbedding.Dimensions).ToArray();
        return embeddings;
    }
}
