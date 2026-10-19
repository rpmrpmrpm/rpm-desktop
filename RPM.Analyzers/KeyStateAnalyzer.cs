using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RPM.Analyzers;

/// <summary>
/// RPM005: key state read anywhere but <c>HeldKeys</c>.
///
/// <para>Both of the framework's ways of asking are caught: <c>InputKeyboardSource.GetKeyStateForCurrentThread</c>
/// and <c>CoreWindow.GetKeyState</c> / <c>GetAsyncKeyState</c>. On the Uno heads all three read the same
/// table, built from the key events this app received, and on macOS nothing clears it when the app loses
/// focus.</para>
///
/// <para>Matched on the declaring type rather than on a file name, so moving code to another file does
/// not change what is allowed.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class KeyStateAnalyzer : DiagnosticAnalyzer
{
    private const string Allowed = "HeldKeys";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.KeyState);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Inspect, SyntaxKind.InvocationExpression);
    }

    private static void Inspect(SyntaxNodeAnalysisContext context)
    {
        if (context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol
            is not IMethodSymbol method)
            return;

        if (!ReadsKeyState(method))
            return;

        if (DeclaredInAllowedType(context.Node))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rules.KeyState, context.Node.GetLocation(), $"{method.ContainingType.Name}.{method.Name}"));
    }

    private static bool ReadsKeyState(IMethodSymbol method) => method.ContainingType?.Name switch
    {
        "InputKeyboardSource" => method.Name == "GetKeyStateForCurrentThread",
        "CoreWindow" => method.Name is "GetKeyState" or "GetAsyncKeyState",
        _ => false,
    };

    private static bool DeclaredInAllowedType(SyntaxNode node)
    {
        for (var walk = node.Parent; walk is not null; walk = walk.Parent)
            if (walk is TypeDeclarationSyntax declared && declared.Identifier.ValueText == Allowed)
                return true;

        return false;
    }
}
