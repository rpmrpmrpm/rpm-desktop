using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RPM.Analyzers;

/// <summary>
/// RPM016: a string literal with a letter in it handed to something that puts it on screen.
///
/// <para>The places that show text: a text property of a XAML element (<c>Text</c>,
/// <c>Content</c>, <c>Header</c>, <c>PlaceholderText</c> and the others in
/// <see cref="TextProperties"/>, on any type that is a <c>DependencyObject</c>, and a window's
/// <c>Title</c>), <c>ToolTipService.SetToolTip</c>, <c>AutomationProperties.SetName</c> and
/// <c>SetHelpText</c>, a list's <c>Items.Add</c>, and any parameter or property the app marks with an
/// attribute named <c>ShownAttribute</c>.</para>
///
/// <para>What is read: a literal, an interpolated string's own text, both sides of a concatenation and
/// both branches of a conditional. A value that comes from anywhere else (a field, a call, a key) is
/// not judged, which is what <c>Loc.Get</c> returns. The app's own name and the unit symbols (dB, Hz,
/// kHz, kbps, LUFS, BPM) are left as written, since they read the same in every language.</para>
///
/// <para>Code inside <c>#if DEBUG</c> is not judged: it is the developer's screens (Mix Lab, the
/// gallery), which no Release build contains.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ShownLiteralAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> TextProperties = ImmutableHashSet.Create(
        "Text", "Content", "Header", "PlaceholderText", "Title", "Description", "Message", "Subtitle",
        "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText", "OnContent", "OffContent",
        "Label");

    /// <summary>Names and unit symbols that read the same in every language, removed before the
    /// letters are looked for.</summary>
    private static readonly Regex AsWritten = new(@"\b(?:rpm|RPaMp|dB|Hz|kHz|kbps|LUFS|BPM)\b", RegexOptions.Compiled);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.ShownLiteral);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(InspectAssignment, OperationKind.SimpleAssignment);
        context.RegisterOperationAction(InspectCall, OperationKind.Invocation, OperationKind.ObjectCreation);
    }

    private static void InspectAssignment(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        if (assignment.Target is not IPropertyReferenceOperation { Property: { } property })
            return;

        if (!Shows(property))
            return;

        Report(context, assignment.Value, $"{property.ContainingType.Name}.{property.Name}");
    }

    private static void InspectCall(OperationAnalysisContext context)
    {
        var (method, arguments) = context.Operation switch
        {
            IInvocationOperation call => (call.TargetMethod, call.Arguments),
            IObjectCreationOperation made when made.Constructor is { } ctor => (ctor, made.Arguments),
            _ => (null, ImmutableArray<IArgumentOperation>.Empty),
        };
        if (method is null)
            return;

        foreach (var argument in arguments)
        {
            if (argument.Parameter is not { } parameter)
                continue;

            if (Shows(method, parameter))
                Report(context, argument.Value, $"{method.ContainingType.Name}.{method.Name}");
        }
    }

    private static bool Shows(IPropertySymbol property)
    {
        if (HasShown(property.GetAttributes()))
            return true;

        if (!TextProperties.Contains(property.Name))
            return false;

        if (property.Name == "Title" && Is(property.ContainingType, "Microsoft.UI.Xaml", "Window"))
            return true;

        return DerivesFrom(property.ContainingType, "Microsoft.UI.Xaml", "DependencyObject");
    }

    private static bool Shows(IMethodSymbol method, IParameterSymbol parameter)
    {
        if (HasShown(parameter.GetAttributes()))
            return true;

        var owner = method.ContainingType;
        switch (method.Name)
        {
            case "SetToolTip" when Is(owner, "Microsoft.UI.Xaml.Controls", "ToolTipService"):
                return parameter.Ordinal == 1;
            case "SetName" or "SetHelpText" when Is(owner, "Microsoft.UI.Xaml.Automation", "AutomationProperties"):
                return parameter.Ordinal == 1;
            case "Add" or "Insert" when Is(owner, "Microsoft.UI.Xaml.Controls", "ItemCollection"):
                return parameter.Type.SpecialType == SpecialType.System_Object;
            default:
                return false;
        }
    }

    private static void Report(OperationAnalysisContext context, IOperation value, string where)
    {
        foreach (var (words, at) in Literals(value))
        {
            var rest = AsWritten.Replace(words, "");
            if (!rest.Any(char.IsLetter))
                continue;

            if (InDebugOnlyCode(at.Syntax))
                return;

            context.ReportDiagnostic(Diagnostic.Create(Rules.ShownLiteral, at.Syntax.GetLocation(), words.Trim(), where));
            return;
        }
    }

    /// <summary>Every piece of text in the value that is written in the source rather than read from
    /// somewhere: literals, an interpolation's own text, and both sides of a concatenation or a
    /// conditional.</summary>
    private static System.Collections.Generic.IEnumerable<(string Words, IOperation At)> Literals(IOperation value)
    {
        switch (value)
        {
            case IConversionOperation conversion:
                foreach (var found in Literals(conversion.Operand))
                    yield return found;
                break;
            case ILiteralOperation { ConstantValue: { HasValue: true, Value: string text } } literal:
                yield return (text, literal);
                break;
            case IInterpolatedStringOperation interpolated:
                var own = string.Concat(interpolated.Parts.OfType<IInterpolatedStringTextOperation>()
                    .Select(p => p.Text.ConstantValue.HasValue ? p.Text.ConstantValue.Value as string : ""));
                yield return (own, interpolated);
                break;
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } joined:
                foreach (var found in Literals(joined.LeftOperand))
                    yield return found;
                foreach (var found in Literals(joined.RightOperand))
                    yield return found;
                break;
            case IConditionalOperation either:
                foreach (var found in Literals(either.WhenTrue))
                    yield return found;
                if (either.WhenFalse is { } otherwise)
                    foreach (var found in Literals(otherwise))
                        yield return found;
                break;
            case ICoalesceOperation coalesce:
                foreach (var found in Literals(coalesce.Value))
                    yield return found;
                foreach (var found in Literals(coalesce.WhenNull))
                    yield return found;
                break;
        }
    }

    /// <summary>Whether the node sits in the first branch of an <c>#if DEBUG</c>: after that directive
    /// and before the <c>#elif</c>, <c>#else</c> or <c>#endif</c> that closes it at the same depth.</summary>
    private static bool InDebugOnlyCode(SyntaxNode node)
    {
        var directives = node.SyntaxTree.GetRoot()
            .DescendantTrivia(descendIntoTrivia: true)
            .Select(t => t.GetStructure())
            .OfType<DirectiveTriviaSyntax>()
            .ToList();

        for (var i = 0; i < directives.Count; i++)
        {
            if (directives[i] is not IfDirectiveTriviaSyntax { Condition: IdentifierNameSyntax { Identifier.ValueText: "DEBUG" } } check)
                continue;
            if (node.SpanStart < check.Span.End)
                continue;

            var depth = 0;
            for (var j = i + 1; j < directives.Count; j++)
            {
                switch (directives[j])
                {
                    case IfDirectiveTriviaSyntax:
                        depth++;
                        continue;
                    case EndIfDirectiveTriviaSyntax when depth > 0:
                        depth--;
                        continue;
                    case ElifDirectiveTriviaSyntax or ElseDirectiveTriviaSyntax or EndIfDirectiveTriviaSyntax when depth == 0:
                        if (node.SpanStart < directives[j].SpanStart)
                            return true;
                        j = directives.Count;
                        continue;
                }
            }
        }

        return false;
    }

    private static bool HasShown(ImmutableArray<AttributeData> attributes)
        => attributes.Any(a => a.AttributeClass?.Name == "ShownAttribute");

    private static bool Is(INamedTypeSymbol? type, string ns, string name)
        => type is not null && type.Name == name && type.ContainingNamespace?.ToDisplayString() == ns;

    /// <summary>Whether the type is, inherits or implements the named one. Implements matters: in Uno
    /// <c>DependencyObject</c> is an interface, not a base class.</summary>
    private static bool DerivesFrom(INamedTypeSymbol? type, string ns, string name)
    {
        for (var walk = type; walk is not null; walk = walk.BaseType)
            if (Is(walk, ns, name))
                return true;

        return type is not null && type.AllInterfaces.Any(i => Is(i, ns, name));
    }
}
