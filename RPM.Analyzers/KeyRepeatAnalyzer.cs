using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RPM.Analyzers;

/// <summary>
/// RPM015: <c>CorePhysicalKeyStatus.WasKeyDown</c> read anywhere but <c>HeldKeys</c>.
///
/// <para>On the macOS head the flag is always false, so a check written against it works on Windows and
/// silently does nothing on a Mac. <c>HeldKeys.IsRepeat</c> works on both.</para>
///
/// <para>Matched on the declaring type rather than on a file name, as RPM005 is.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class KeyRepeatAnalyzer : DiagnosticAnalyzer
{
    private const string Allowed = "HeldKeys";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.KeyRepeat);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Inspect, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void Inspect(SyntaxNodeAnalysisContext context)
    {
        var access = (MemberAccessExpressionSyntax)context.Node;
        if (access.Name.Identifier.ValueText != "WasKeyDown")
            return;

        if (context.SemanticModel.GetSymbolInfo(access, context.CancellationToken).Symbol
            is not IPropertySymbol { ContainingType.Name: "CorePhysicalKeyStatus" } property)
            return;

        if (DeclaredInAllowedType(access))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rules.KeyRepeat, access.GetLocation(), $"{property.ContainingType.Name}.{property.Name}"));
    }

    private static bool DeclaredInAllowedType(SyntaxNode node)
    {
        for (var walk = node.Parent; walk is not null; walk = walk.Parent)
            if (walk is TypeDeclarationSyntax declared && declared.Identifier.ValueText == Allowed)
                return true;

        return false;
    }
}
