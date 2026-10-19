using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RPM.Analyzers;

/// <summary>
/// RPM002: a catalogue's address written into a project that is not the API.
///
/// <para>Reads string literals rather than types, because a call to a catalogue often has no catalogue
/// type in it at all. It builds a URL from a host constant and passes it to a shared fetcher, so a
/// type-based check would see nothing wrong.</para>
///
/// <para><b>Documentation comments are not literals and are not checked</b>, which is deliberate: the
/// reason a call is forbidden usually has to name the service it is forbidden from.</para>
///
/// <para>The hosts are the ones <c>CatalogueReach</c> refuses at run time. Not listed, because an app
/// may reach them: Wikipedia and Wikidata, whose article depends on the reader's own language; the
/// Cover Art Archive and the artwork CDNs, which carry image bytes rather than answers. fanart.tv's API
/// host is listed, because only the API calls it, with RPM's project key.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CatalogueAddressAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableArray<string> Catalogues = ImmutableArray.Create(
        "musicbrainz.org",
        "listenbrainz.org",
        "acoustid.org",
        "audd.io",
        "theaudiodb.com",
        "api.discogs.com",
        "sonovault.now",
        "webservice.fanart.tv");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.CatalogueAddress);

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
        if (text.Length == 0)
            return;

        foreach (var catalogue in Catalogues)
        {
            if (text.IndexOf(catalogue, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            context.ReportDiagnostic(
                Diagnostic.Create(Rules.CatalogueAddress, context.Node.GetLocation(), catalogue));
            return;
        }
    }
}
