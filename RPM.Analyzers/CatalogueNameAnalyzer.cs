using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RPM.Analyzers;

/// <summary>
/// RPM003: the name MusicBrainz written into a string.
///
/// <para>rpm names SonoVault. The catalogue's data reaches the app under SonoVault's licence, so
/// SonoVault is who the app credits and who its screens name, and a string saying otherwise is a
/// statement about a licence rather than a wording choice.</para>
///
/// <para><b>Strings, not identifiers.</b> <c>MusicBrainzReleaseId</c> is the name of a field in an
/// audio file, written there by other programs, and <c>MusicBrainzEncyclopedia</c> is the class that
/// calls the catalogue. Neither can be renamed by deciding to, and neither reaches a reader. What
/// reaches a reader is a string.</para>
///
/// <para><b>Documentation comments are not literals and are not checked</b>, the same as RPM002 and
/// for the same reason: explaining where a value comes from usually has to name its source.</para>
///
/// <para><b>Generated code is not checked</b>, the same as RPM002. The JSON source generator writes
/// every property's name as a literal, so checking generated code reports the tag fields in files
/// nobody wrote. Text shown on screen comes from scripts/strings.json, and gen_locales.py refuses the
/// name there, which also covers the phone app.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CatalogueNameAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// The strings that carry this name and cannot be changed by deciding to.
    ///
    /// <para>Every one is an identifier some other program chose: two field names on the wire to the
    /// rpm API, a tag name as it is spelled inside an audio file, and a provider key already written
    /// into every library database there is. Renaming any of them is a protocol change or a
    /// migration, not an edit.</para>
    ///
    /// <para>Listed rather than matched by a pattern, so that adding one is a deliberate change. A new
    /// string that seems to belong here should almost always say SonoVault instead.</para>
    /// </summary>
    private static readonly ImmutableHashSet<string> Fixed = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        // Wire: the field names RPM.Lambda.Api sends and reads.
        "musicBrainzReleaseId",
        "musicBrainzArtistId",
        // The tag as it is spelled inside a FLAC or an MP3, by whoever wrote the file.
        "MUSICBRAINZ_DISCID",
        // The release type, as Picard spells it in an APE tag and in older Xiph comments, and in an ID3 user text
        // frame and an MP4 freeform atom (ReleaseTypeTag).
        "MUSICBRAINZ_ALBUMTYPE",
        "MusicBrainz Album Type",
        // Stored: the provider key already in the release_meta rows of every existing library.
        "musicbrainz-release-group");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.CatalogueName);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Inspect, SyntaxKind.StringLiteralExpression, SyntaxKind.Utf8StringLiteralExpression);
    }

    private static void Inspect(SyntaxNodeAnalysisContext context)
    {
        var text = ((LiteralExpressionSyntax)context.Node).Token.ValueText;

        if (text.IndexOf("musicbrainz", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        // The fixed names are REMOVED from the literal rather than compared against it, because a
        // literal may contain one inside something larger: the provider key appears in the middle of a
        // page of SQL, and a whole-string comparison would report the query. Any name left after they
        // are removed is one that could have said SonoVault.
        foreach (var name in Fixed)
            text = text.Replace(name, string.Empty);

        // A comment inside the literal is still a comment. A page of SQL has its own, written with
        // "--", and they explain which catalogue an id comes from for the same reason a C# comment
        // would: the ids in these columns ARE MusicBrainz's, whatever name the app shows on screen. C#
        // comments are exempt from this rule, so SQL comments inside a string are exempt too.
        text = WithoutSqlComments(text);

        if (text.IndexOf("musicbrainz", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        context.ReportDiagnostic(
            Diagnostic.Create(Rules.CatalogueName, context.Node.GetLocation(),
                Shorten(((LiteralExpressionSyntax)context.Node).Token.ValueText)));
    }

    /// <summary>The literal with its SQL comment lines dropped.</summary>
    private static string WithoutSqlComments(string text)
    {
        if (text.IndexOf("--", StringComparison.Ordinal) < 0)
            return text;

        var kept = new System.Text.StringBuilder(text.Length);

        foreach (var line in text.Split('\n'))
        {
            var at = line.IndexOf("--", StringComparison.Ordinal);
            kept.Append(at < 0 ? line : line.Substring(0, at)).Append('\n');
        }

        return kept.ToString();
    }

    /// <summary>The literal as the message quotes it. A page of SQL in an error message hides the
    /// error, so a long one is cut to the words around the name.</summary>
    private static string Shorten(string text)
    {
        if (text.Length <= 60)
            return text;

        var at = text.IndexOf("musicbrainz", StringComparison.OrdinalIgnoreCase);
        var from = Math.Max(0, at - 20);
        var to = Math.Min(text.Length, at + 40);

        return (from > 0 ? "…" : "") + text.Substring(from, to - from).Replace("\n", " ")
             + (to < text.Length ? "…" : "");
    }
}
