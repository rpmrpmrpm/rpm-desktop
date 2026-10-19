using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RPM.Analyzers;

/// <summary>
/// RPM009: <c>AppWindow.Presenter</c> read anywhere but <c>WindowPresenter</c>.
///
/// <para>Under Native AOT on Windows, <c>appWindow.Presenter as OverlappedPresenter</c> can return null,
/// and then every window feature that needs the presenter does nothing. WindowPresenter.Overlapped wraps
/// the native object again when the cast fails. A caller that reads the property itself does not get
/// that.</para>
///
/// <para>Matched on the declaring type rather than on a file name, so moving code to another file does
/// not change what is allowed.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AppWindowPresenterAnalyzer : DiagnosticAnalyzer
{
    private const string Allowed = "WindowPresenter";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.AppWindowPresenter);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Inspect, OperationKind.PropertyReference);
    }

    private static void Inspect(OperationAnalysisContext context)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        if (reference.Property is not { Name: "Presenter", ContainingType: { Name: "AppWindow" } owner })
            return;
        if (owner.ContainingNamespace?.ToDisplayString() != "Microsoft.UI.Windowing")
            return;
        if (DeclaredInAllowedType(reference.Syntax))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rules.AppWindowPresenter, reference.Syntax.GetLocation()));
    }

    private static bool DeclaredInAllowedType(SyntaxNode node)
    {
        for (var walk = node.Parent; walk is not null; walk = walk.Parent)
            if (walk is TypeDeclarationSyntax declared && declared.Identifier.ValueText == Allowed)
                return true;

        return false;
    }
}
