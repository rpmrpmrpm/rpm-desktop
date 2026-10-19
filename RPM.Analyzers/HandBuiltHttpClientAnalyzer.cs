using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RPM.Analyzers;

/// <summary>
/// RPM001: an HTTP client built with no handler, or a handler built outside <c>Wire</c>.
///
/// <para><b>What is wrong is the missing handler, not the <c>new</c>.</b> A client given a handler from
/// <c>Wire</c> is the correct way to build one that needs its own timeout (the radio stream holds a
/// response open for hours, and a peer on this LAN must not be gated at all). A client given NO handler
/// has no gate, no connection budget and no catalogue guard.</para>
///
/// <para>Both spellings are caught, because the second is easy for a reader to miss:
/// <c>new HttpClient()</c> is obvious and <c>private static readonly HttpClient Http = new();</c> is
/// not.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HandBuiltHttpClientAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Built with no handler, this client has no gate, budget or guard. Always wrong outside
    /// the allowed types.</summary>
    private const string Client = "HttpClient";

    /// <summary>The handlers themselves. Wire is where every one of these is configured (the socket
    /// budget, the gate, the redirect policy), so one built anywhere else is not configured.</summary>
    private static readonly ImmutableHashSet<string> Handlers =
        ImmutableHashSet.Create("SocketsHttpHandler", "HttpClientHandler");

    /// <summary>
    /// The two types allowed to build one, by name.
    ///
    /// <para><c>Wire</c> is where the sanctioned client is built. <c>NetworkReach</c> is the probe that
    /// decides whether there is a network at all, and it is deliberately plain: no cache, no retries
    /// and no gate of its own, because the gate itself depends on it.</para>
    ///
    /// <para>Matched on the declaring type rather than on a file name, so moving code to another file
    /// does not change what is allowed.</para>
    /// </summary>
    private static readonly ImmutableHashSet<string> Allowed =
        ImmutableHashSet.Create("Wire", "NetworkReach");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.HandBuiltHttpClient);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Inspect, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Inspect(SyntaxNodeAnalysisContext context)
    {
        var created = context.SemanticModel.GetTypeInfo(context.Node, context.CancellationToken).Type;
        if (created is null)
            return;

        var handler = Handlers.Contains(created.Name);
        if (!handler && created.Name != Client)
            return;

        // A client given a handler was given one on purpose, and the handler is itself checked by the
        // other half of this rule, so the pipeline is checked either way.
        if (!handler && Arguments(context.Node) > 0)
            return;

        if (DeclaredInAllowedType(context.Node))
            return;

        context.ReportDiagnostic(
            Diagnostic.Create(Rules.HandBuiltHttpClient, context.Node.GetLocation(), created.Name));
    }

    /// <summary>How many arguments the construction was given. An object initializer is not one of
    /// them: <c>new HttpClient { Timeout = t }</c> sets a property on a client with no handler.</summary>
    private static int Arguments(SyntaxNode node) => node switch
    {
        ObjectCreationExpressionSyntax created => created.ArgumentList?.Arguments.Count ?? 0,
        ImplicitObjectCreationExpressionSyntax implied => implied.ArgumentList.Arguments.Count,
        _ => 0,
    };

    /// <summary>Whether the expression sits inside one of the allowed types, however deeply nested.</summary>
    private static bool DeclaredInAllowedType(SyntaxNode node)
    {
        for (var walk = node.Parent; walk is not null; walk = walk.Parent)
            if (walk is TypeDeclarationSyntax declared && Allowed.Contains(declared.Identifier.ValueText))
                return true;

        return false;
    }
}
