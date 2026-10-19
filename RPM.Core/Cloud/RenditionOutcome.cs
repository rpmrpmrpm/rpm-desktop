namespace RPM.Core.Cloud;

/// <summary>What came of making a track's renditions: the head clip and the free rate.</summary>
public enum RenditionOutcome
{
    /// <summary>Both renditions were uploaded. This is written down, so the track is never encoded
    /// again.</summary>
    Made,

    /// <summary>Something went wrong, and may not go wrong next time: a network that dropped, a file
    /// ffmpeg could not read, or a signed URL that expired during the upload. Nothing is written down,
    /// and the track is offered again on the next run.
    /// </summary>
    Failed,

    /// <summary>
    /// The account has no track under this id, so there is nothing to attach a rendition to.
    ///
    /// <para>This has its own outcome because retrying cannot fix it. The id is the track's name in the
    /// store. A row that says Synced while the store has never heard of it lies about where the music
    /// is. It is picked for renditions on every run, encodes a file, gets a 404, and writes nothing
    /// down. Then it comes back on the next run, and on every run after that.
    /// <c>CloudSyncService.CatchUpRenditionsAsync</c> puts the row back in the queue, so the track is
    /// actually uploaded.</para>
    /// </summary>
    NotInTheCloud,
}

/// <summary>What came of making a track's renditions, and, when something refused, what it said.
///
/// <para>The words matter as much as the outcome. <see cref="RenditionOutcome.Failed"/> covers both a
/// network that dropped and a file that ffmpeg will never encode. Only the message tells them apart
/// (<c>CloudFailure.Classify</c>). Without the message, the caller would have to treat every failure as
/// worth another try. One damaged file would then be read again on every launch, for as long as the
/// app stayed installed.</para>
/// </summary>
/// <param name="Said">The tool's or the server's own words. They are not translated and are not meant
/// for showing to users. Null when nothing went wrong.</param>
/// <param name="FullError">Everything known about the failure (<see
/// cref="CloudFailure.InFull(Exception)"/>), for the log and for a report. Null when nothing went
/// wrong.</param>
public readonly record struct RenditionResult(
    RenditionOutcome Outcome, string? Said = null, string? FullError = null);

/// <summary>
/// What a library should do about a row the store does not recognise.
///
/// <para>The rule is narrow on purpose. The store is trusted about one track at a time, and only from
/// the endpoint that names that track. A listing that came back short, a request that timed out, or a
/// token that expired must never be read as "the cloud has lost the collection". The action taken here
/// is to upload everything again, so a false reading would be costly.</para>
/// </summary>
public static class CloudRowRepair
{
    /// <summary>How many rows one run may put right before it stops and reports a problem instead.
    ///
    /// <para>A handful of rows the store has never heard of means a library with a few bad ids. Putting
    /// those rows back in the queue is the repair. A whole run of them is different. It usually means
    /// the account is signed in as somebody else, a listing came from the wrong pool, or the API answers
    /// 404 to everything. The repair would then demote every row in the collection and upload it all
    /// again. From inside the loop these cases look the same, so the loop must not decide between
    /// them.</para>
    /// </summary>
    public const int MostPerRun = 5;

    /// <summary>Whether a row that claims to be in the cloud has just been contradicted by the store
    /// itself, so it has to be put back in the queue.
    /// </summary>
    /// <param name="state">What the row says about itself.</param>
    /// <param name="outcome">What the store said when asked about this exact track.</param>
    /// <param name="alreadyPutBack">How many rows this run has already put right. Past
    /// <see cref="MostPerRun"/>, nothing more is changed. See there.</param>
    public static bool BelongsBackInTheQueue(
        CloudState state, RenditionOutcome outcome, int alreadyPutBack = 0)
        => outcome == RenditionOutcome.NotInTheCloud
           && state == CloudState.Synced
           && alreadyPutBack < MostPerRun;

    /// <summary>Whether a run has seen so many denials that the account, not the rows, is the likely
    /// cause. In that case the user must be told, and the library must not be uploaded again.</summary>
    /// <param name="deniedThisRun">Denials the run could not explain. A denial that the catalogue accounts
    /// for is not counted. See <see cref="ExplainedByTheCatalog"/>.</param>
    public static bool SomethingIsWrongWithTheAccount(int deniedThisRun) => deniedThisRun > MostPerRun;

    /// <summary>Whether the run's catalogue listing explains the denial. The account holds this exact
    /// audio, but under a different name from the one the row asked for.
    ///
    /// <para><see cref="MostPerRun"/> exists because the loop cannot tell a few bad rows from a wrong
    /// account. The catalogue's hash index can tell them apart. An account that holds the audio being
    /// denied is working. The row is simply filed under a name it has lost. This is proof, not a guess.
    /// Such a row is put right whatever <see cref="MostPerRun"/> says, and the denial is never counted
    /// against the account.
    /// </para>
    /// </summary>
    /// <param name="rowId">The id the store was asked about and denied.</param>
    /// <param name="idTheCatalogHolds">The id that the catalogue's hash index files this audio under, or
    /// null when it says nothing.</param>
    public static bool ExplainedByTheCatalog(string? rowId, string? idTheCatalogHolds)
        => CloudAdoption.WorthVerifying(idTheCatalogHolds, rowId);

    /// <summary>The state that such a row is put into: Local, not Queued.
    ///
    /// <para>Local is the state the row had before anybody offered it to the cloud. <c>QueueForUpload</c>
    /// promotes Local rows. So the track goes up again if it still belongs (the user picked it, or the
    /// whole library is synced), and it stays put if it does not. Writing Queued here would put back in
    /// the queue a track that its owner may have removed from the cloud on purpose.</para>
    /// </summary>
    public static CloudState PutBackAs => CloudState.Local;
}
