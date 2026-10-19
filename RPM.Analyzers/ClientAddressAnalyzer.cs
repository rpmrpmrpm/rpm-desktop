using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RPM.Analyzers;

/// <summary>
/// RPM004: a client's base address set by hand instead of by a <c>Destination</c>.
///
/// <para>Where a client points and what it must carry to be answered there are one fact. The app's
/// own API refuses the reads that name no user unless the request carries the app key, and the key is
/// attached by <c>Destination.Aim</c>. A base address written straight onto a client therefore has no
/// key with it.</para>
///
/// <para>The assignment is what is checked, not the address, because the address is usually a
/// variable by then (for example a string parameter that accepts any address).</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ClientAddressAnalyzer : DiagnosticAnalyzer
{
    private const string Client = "HttpClient";
    private const string Property = "BaseAddress";

    /// <summary>The one type allowed to aim a client, by name. Matched on the declaring type rather
    /// than on a file name, so moving code to another file does not change what is allowed.</summary>
    private const string Allowed = "Destination";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.ClientAddress);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Inspect, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Inspect(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol
            is not IPropertySymbol { Name: Property } property)
            return;

        if (property.ContainingType?.Name != Client)
            return;

        if (DeclaredInAllowedType(assignment))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rules.ClientAddress, assignment.GetLocation()));
    }

    private static bool DeclaredInAllowedType(SyntaxNode node)
    {
        for (var walk = node.Parent; walk is not null; walk = walk.Parent)
            if (walk is TypeDeclarationSyntax declared && declared.Identifier.ValueText == Allowed)
                return true;

        return false;
    }
}
