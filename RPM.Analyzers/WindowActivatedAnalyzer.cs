using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RPM.Analyzers;

/// <summary>
/// RPM006: a handler added to <c>Window.Activated</c> anywhere but <c>AppActivation</c>.
///
/// <para>The event is raised on Windows and, after launch, never on macOS, because Uno's mac host
/// registers no focus callback. A handler added to it compiles, works on one head and does nothing on
/// the other, with nothing to say so.</para>
///
/// <para>Matched on the declaring type rather than on a file name, so moving code to another file does
/// not change what is allowed.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WindowActivatedAnalyzer : DiagnosticAnalyzer
{
    private const string Allowed = "AppActivation";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.WindowActivated);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Inspect, SyntaxKind.AddAssignmentExpression);
    }

    private static void Inspect(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol
            is not IEventSymbol { Name: "Activated" } added)
            return;

        if (added.ContainingType?.Name != "Window")
            return;

        if (DeclaredInAllowedType(assignment))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rules.WindowActivated, assignment.GetLocation()));
    }

    private static bool DeclaredInAllowedType(SyntaxNode node)
    {
        for (var walk = node.Parent; walk is not null; walk = walk.Parent)
            if (walk is TypeDeclarationSyntax declared && declared.Identifier.ValueText == Allowed)
                return true;

        return false;
    }
}
