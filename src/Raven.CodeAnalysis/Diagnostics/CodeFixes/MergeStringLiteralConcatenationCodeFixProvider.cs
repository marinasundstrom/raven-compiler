using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class MergeStringLiteralConcatenationCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(MergeStringLiteralConcatenationCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds =
        [StringConcatenationAnalyzer.MergeDiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, StringConcatenationAnalyzer.MergeDiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var root = context.Document.SyntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var node = root.FindNode(diagnostic.Location.SourceSpan);
        if (node is null)
            return;

        var concat = node as InfixOperatorExpressionSyntax
            ?? node.AncestorsAndSelf().OfType<InfixOperatorExpressionSyntax>().FirstOrDefault();
        if (concat is null || concat.Kind != SyntaxKind.AddExpression)
            return;

        var parts = new List<ExpressionSyntax>();
        FlattenConcat(concat, parts);
        if (parts.Count < 2)
            return;

        var semanticModel = context.Document.GetSemanticModelAsync(context.CancellationToken).GetAwaiter().GetResult();
        if (semanticModel is null)
            return;

        var mergedBuilder = new StringBuilder();
        foreach (var part in parts)
        {
            if (!TryGetTextPart(part, semanticModel, out var text))
                return;

            mergedBuilder.Append(text);
        }

        var replacement = "\"" + EscapeStringLiteral(mergedBuilder.ToString()) + "\"";

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Merge string concatenation",
                context.Document.Id,
                new TextChange(concat.Span, replacement),
                EquivalenceKey));
    }

    private static void FlattenConcat(ExpressionSyntax expression, List<ExpressionSyntax> parts)
    {
        if (expression is InfixOperatorExpressionSyntax add &&
            add.Kind == SyntaxKind.AddExpression)
        {
            FlattenConcat(add.Left, parts);
            FlattenConcat(add.Right, parts);
            return;
        }

        parts.Add(expression);
    }

    internal static bool TryGetTextPart(ExpressionSyntax expression, SemanticModel semanticModel, out string text)
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

    internal static string EscapeStringLiteral(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\0': builder.Append("\\0"); break;
                case '\a': builder.Append("\\a"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\v': builder.Append("\\v"); break;
                default:
                    if (char.IsControl(ch))
                        builder.Append("\\u").Append(((int)ch).ToString("X4"));
                    else
                        builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }
}
