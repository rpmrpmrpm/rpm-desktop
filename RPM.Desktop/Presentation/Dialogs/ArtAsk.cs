namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// What a window wants done about a record's pictures when it sends the user off to pick a pressing.
///
/// <para>There are three answers rather than a flag. The release window offers a choice, and the choice
/// only makes sense with a third fact: whether the record already has a cover. A record with a cover is
/// one where fetching another replaces it. A record with no cover is one where nobody would open the
/// window and then decline the picture. So the offer is always made, and it starts where the record
/// is: off over a picture that exists, on when there is none.</para>
///
/// <para>A bool cannot express this. It would replace a picture the user chose by hand, whenever a
/// lookup ran.</para>
/// </summary>
public enum ArtAsk
{
    /// <summary>Leave the pictures alone: this lookup is about the words. No offer is made.</summary>
    None,

    /// <summary>Fetch them. The record has none, so the offer starts ticked.</summary>
    Missing,

    /// <summary>Fetch them, but the record is holding pictures already: the offer starts unticked, and
    /// what it has keeps unless somebody says otherwise.</summary>
    Held,
}
