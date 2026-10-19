namespace RPM.Core.Mixing;

/// <summary>
/// One stage of a roll: a slice of the record, played over and over for a while.
/// </summary>
///
/// <param name="SliceFrames">How much of the record goes round in one loop: half a bar, a beat, or half
/// a beat.</param>
/// <param name="LengthFrames">How long this stage lasts. It is normally a whole number of slices. The
/// stage then ends where a slice ends, and the next stage starts on a line rather than part way through
/// one.</param>
///
/// <remarks>
/// A schedule of these steps makes a roll shorten as it goes. A roll is two beats round and round, then
/// one beat, then half a beat. Each stage is shorter than the last, and each one pulls the room further
/// forward. Every slice is a prefix of the longest slice. So the deck captures once and reads less of
/// it as the schedule goes on. See <see cref="Deck.RollOut"/>.
/// </remarks>
public readonly record struct RollStep(long SliceFrames, long LengthFrames);
