using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class StringConcatenationToInterpolatedStringCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(StringConcatenationToInterpolatedStringCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds =
        [StringConcatenationAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, StringConcatenationAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        // 1) Find the concat expression to rewrite
        var root = context.Document.SyntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var span = diagnostic.Location.SourceSpan;

        // Find node that covers the diagnostic span; diagnostic is typically on the '+' token.
        var node = root.FindNode(span);
        if (node is null)
            return;

        // Walk up to a InfixOperatorExpressionSyntax (+) if needed.
        var concat = node as InfixOperatorExpressionSyntax
            ?? node.AncestorsAndSelf().OfType<InfixOperatorExpressionSyntax>().FirstOrDefault();

        if (concat is null || concat.Kind != SyntaxKind.AddExpression)
            return;

        // IMPORTANT: the analyzer should have reported only topmost, but we can still normalize:
        // climb while parent is also string concat.
        concat = GetTopmostConcat(concat);

        // 2) Flatten chain into parts (syntax only; analyzer already ensured string type)
        var parts = new List<ExpressionSyntax>();
        FlattenConcat(concat, parts);

        if (parts.Count < 2)
            return;

        var semanticModel = context.Document.GetSemanticModelAsync(context.CancellationToken).GetAwaiter().GetResult();
        if (semanticModel is null)
            return;

        // 3) Build interpolation text
        if (!TryBuildInterpolatedString(parts, semanticModel, out var replacementText))
            return;

        // 4) Replace whole expression span
        var change = new TextChange(concat.Span, replacementText);

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Convert to interpolated string",
                context.Document.Id,
                change,
                EquivalenceKey));
    }

    internal static InfixOperatorExpressionSyntax GetTopmostConcat(InfixOperatorExpressionSyntax expr)
    {
        // If Raven has Parent typed as SyntaxNode, this works.
        var current = expr;

        while (current.Parent is InfixOperatorExpressionSyntax parent &&
               parent.Kind == SyntaxKind.AddExpression)
        {
            current = parent;
        }

        return current;
    }

    internal static void FlattenConcat(ExpressionSyntax expr, List<ExpressionSyntax> parts)
    {
        if (expr is InfixOperatorExpressionSyntax add &&
            add.Kind == SyntaxKind.AddExpression)
        {
            FlattenConcat(add.Left, parts);
            FlattenConcat(add.Right, parts);
            return;
        }

        parts.Add(expr);
    }

    internal static bool TryBuildInterpolatedString(List<ExpressionSyntax> parts, SemanticModel semanticModel, out string replacement)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        var hasInterpolation = false;

        foreach (var part in parts)
        {
            if (TryGetTextPart(part, semanticModel, out var text))
            {
                sb.Append(EscapeInterpolatedText(text));
            }
            else
            {
                hasInterpolation = true;
                sb.Append("${");
                sb.Append(part.ToFullString().Trim());
                sb.Append("}");
            }
        }

        sb.Append("\"");
        replacement = sb.ToString();
        return hasInterpolation;
    }

    private static bool TryGetTextPart(ExpressionSyntax expression, SemanticModel semanticModel, out string text)
    {
        if (expression is ParenthesizedExpressionSyntax parenthesized)
            return TryGetTextPart(parenthesized.Expression, semanticModel, out text);

        if (expression is LiteralExpressionSyntax literal)
            return TryFormatConstant(literal.Token.Value, literal.Token.ValueText, out text);

        var symbol = semanticModel.GetSymbolInfo(expression).Symbol;
        symbol = symbol?.UnderlyingSymbol ?? symbol;

        switch (symbol)
        {
            case ILocalSymbol { IsConst: true } local:
                return TryFormatConstant(local.ConstantValue, null, out text);
            case IFieldSymbol { IsConst: true } field:
                return TryFormatConstant(field.GetConstantValue(), null, out text);
            default:
                text = string.Empty;
                return false;
        }
    }

    private static bool TryFormatConstant(object? value, string? stringValueText, out string text)
    {
        switch (value)
        {
            case null:
                if (stringValueText is not null)
                {
                    text = stringValueText;
                    return true;
                }

                text = string.Empty;
                return true;
            case string:
                text = stringValueText ?? string.Empty;
                return true;
            case char c:
                text = c.ToString();
                return true;
            case bool b:
                text = b.ToString();
                return true;
            case sbyte i8:
                text = i8.ToString(CultureInfo.InvariantCulture);
                return true;
            case byte u8:
                text = u8.ToString(CultureInfo.InvariantCulture);
                return true;
            case short i16:
                text = i16.ToString(CultureInfo.InvariantCulture);
                return true;
            case ushort u16:
                text = u16.ToString(CultureInfo.InvariantCulture);
                return true;
            case int i32:
                text = i32.ToString(CultureInfo.InvariantCulture);
                return true;
            case uint u32:
                text = u32.ToString(CultureInfo.InvariantCulture);
                return true;
            case long i64:
                text = i64.ToString(CultureInfo.InvariantCulture);
                return true;
            case ulong u64:
                text = u64.ToString(CultureInfo.InvariantCulture);
                return true;
            case nint ni:
                text = ni.ToString(CultureInfo.InvariantCulture);
                return true;
            case nuint nui:
                text = nui.ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                text = string.Empty;
                return false;
        }
    }

    private static string EscapeInterpolatedText(string text)
    {
        // In interpolated strings:
        // - " stays as " because we’re already inside quotes; we must escape it.
        // - { and } must be doubled.
        // - backslashes depend on whether you generate regular or verbatim interpolated strings (we do regular).
        var sb = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '{': sb.Append("{{"); break;
                case '}': sb.Append("}}"); break;
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }
}
