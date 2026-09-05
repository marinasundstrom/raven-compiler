using System.Collections.Immutable;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class MatchExhaustivenessCodeFixProvider : CodeFixProvider
{
    private const string AddMissingArmsEquivalenceKey = nameof(MatchExhaustivenessCodeFixProvider) + ".AddMissingArms";
    private const string ReplaceCatchAllEquivalenceKey = nameof(MatchExhaustivenessCodeFixProvider) + ".ReplaceCatchAll";
    private const string RemoveCatchAllEquivalenceKey = nameof(MatchExhaustivenessCodeFixProvider) + ".RemoveCatchAll";
    private const string ThrowPlaceholderExpression = "throw System.NotImplementedException()";

    private static readonly ImmutableArray<string> FixableIds =
    [
        CompilerDiagnostics.MatchExpressionNotExhaustive.Id,
        CompilerDiagnostics.MatchExpressionCatchAllRedundant.Id
    ];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!diagnostic.Location.IsInSource)
            return;

        if (string.Equals(diagnostic.Id, CompilerDiagnostics.MatchExpressionNotExhaustive.Id, StringComparison.OrdinalIgnoreCase))
        {
            RegisterAddMissingArmFix(context, diagnostic);
            return;
        }

        if (string.Equals(diagnostic.Id, CompilerDiagnostics.MatchExpressionCatchAllRedundant.Id, StringComparison.OrdinalIgnoreCase))
            RegisterReplaceCatchAllFix(context, diagnostic);
    }

    private static void RegisterAddMissingArmFix(CodeFixContext context, Diagnostic diagnostic)
    {
        if (!IsPrimaryMissingCaseDiagnostic(context, diagnostic))
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var matchSyntax = FindMatch(root, diagnostic.Location.SourceSpan);
        if (matchSyntax is null)
            return;

        var semanticModel = context.Document
            .GetSemanticModelAsync(diagnostic.Location.SourceSpan.Start, context.CancellationToken)
            .GetAwaiter()
            .GetResult();
        if (semanticModel is null)
            return;

        var semanticMatch = FindMatch(
            semanticModel.SyntaxTree.GetRoot(context.CancellationToken),
            diagnostic.Location.SourceSpan);
        if (semanticMatch is null)
            return;

        var missingCases = GetMatchExhaustiveness(
            semanticModel,
            semanticMatch,
            new MatchExhaustivenessOptions(ignoreCatchAllPatterns: false)).MissingCases;
        if (missingCases.IsDefaultOrEmpty)
        {
            if (!TryGetMissingCase(diagnostic, out var missingCase))
                return;

            missingCases = [missingCase];
        }

        var patternTexts = missingCases
            .Select(missingCase => FormatPatternText(missingCase, semanticModel, semanticMatch))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        if (patternTexts.IsDefaultOrEmpty)
            return;

        var sourceText = context.Document.GetTextAsync(context.CancellationToken).GetAwaiter().GetResult();
        var text = sourceText.ToString();
        var armText = string.Concat(patternTexts.Select(patternText =>
            CreateMissingArmText(text, matchSyntax, patternText)));
        var insertionPosition = GetLineStart(text, GetMatchCloseBraceToken(matchSyntax).Span.Start);
        var change = new TextChange(new TextSpan(insertionPosition, 0), armText);
        var title = patternTexts.Length == 1
            ? $"Add missing match arm for '{patternTexts[0]}'"
            : "Add all missing match arms";

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                title,
                context.Document.Id,
                change,
                AddMissingArmsEquivalenceKey));
    }

    private static bool IsPrimaryMissingCaseDiagnostic(CodeFixContext context, Diagnostic diagnostic)
    {
        var first = context.Diagnostics.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Id,
                CompilerDiagnostics.MatchExpressionNotExhaustive.Id,
                StringComparison.OrdinalIgnoreCase) &&
            candidate.Location.SourceSpan.Equals(diagnostic.Location.SourceSpan) &&
            ReferenceEquals(candidate.Location.SourceTree, diagnostic.Location.SourceTree));

        return first is null || ReferenceEquals(first, diagnostic);
    }

    private static void RegisterReplaceCatchAllFix(CodeFixContext context, Diagnostic diagnostic)
    {
        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var token = root.FindToken(diagnostic.Location.SourceSpan.Start);
        var arm = token.Parent?.FirstAncestorOrSelf<MatchArmSyntax>();
        var matchSyntax = FindContainingMatch(arm);
        if (arm is null || matchSyntax is null)
            return;

        var semanticModel = context.Document
            .GetSemanticModelAsync(diagnostic.Location.SourceSpan.Start, context.CancellationToken)
            .GetAwaiter()
            .GetResult();
        if (semanticModel is null)
            return;

        var semanticMatch = FindMatch(
            semanticModel.SyntaxTree.GetRoot(context.CancellationToken),
            diagnostic.Location.SourceSpan);
        if (semanticMatch is null)
            return;

        var ignoreCatchAllInfo = GetMatchExhaustiveness(
            semanticModel,
            semanticMatch,
            new MatchExhaustivenessOptions(ignoreCatchAllPatterns: true));

        if (!TryGetSingleMissingCase(ignoreCatchAllInfo.MissingCases, out var missingCase))
        {
            RegisterRemoveCatchAllFix(context, arm);
            return;
        }

        if (missingCase == "_")
        {
            RegisterRemoveCatchAllFix(context, arm);
            return;
        }

        var patternText = FormatPatternText(missingCase, semanticModel, semanticMatch);
        var change = new TextChange(arm.Pattern.Span, patternText);

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                $"Replace catch-all with '{patternText}'",
                context.Document.Id,
                change,
                ReplaceCatchAllEquivalenceKey));
    }

    private static void RegisterRemoveCatchAllFix(CodeFixContext context, MatchArmSyntax arm)
    {
        var sourceText = context.Document.GetTextAsync(context.CancellationToken).GetAwaiter().GetResult();
        var text = sourceText.ToString();
        var span = GetLineRemovalSpan(text, arm.Span);
        if (span.Length == 0)
            return;

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Remove redundant catch-all arm",
                context.Document.Id,
                new TextChange(span, string.Empty),
                RemoveCatchAllEquivalenceKey));
    }

    private static SyntaxNode? FindMatch(SyntaxNode root, TextSpan diagnosticSpan)
    {
        var token = root.FindToken(diagnosticSpan.Start);
        return FindContainingMatch(token.Parent);
    }

    private static bool TryGetMissingCase(Diagnostic diagnostic, out string missingCase)
    {
        missingCase = string.Empty;

        var args = diagnostic.GetMessageArgs();
        if (args.Length == 0 || args[0] is not string value || string.IsNullOrWhiteSpace(value))
            return false;

        missingCase = NormalizeMissingCase(value);
        return true;
    }

    private static string NormalizeMissingCase(string value)
    {
        const string prefix = "Missing match case:";

        value = value.Trim();
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            return value;

        var firstQuote = value.IndexOf('\'');
        var lastQuote = value.LastIndexOf('\'');
        if (firstQuote >= 0 && lastQuote > firstQuote)
            return value.Substring(firstQuote + 1, lastQuote - firstQuote - 1);

        return value.Substring(prefix.Length).Trim().TrimEnd('.');
    }

    private static bool TryGetSingleMissingCase(ImmutableArray<string> missingCases, out string missingCase)
    {
        missingCase = string.Empty;

        if (missingCases.Length != 1)
            return false;

        missingCase = missingCases[0];
        return true;
    }

    private static string FormatPatternText(
        string missingCase,
        SemanticModel semanticModel,
        SyntaxNode matchSyntax)
    {
        if (missingCase is "_" or "null" or "true" or "false" or "()" ||
            missingCase.StartsWith(".", StringComparison.Ordinal))
        {
            return missingCase;
        }

        var scrutineeType = semanticModel.GetTypeInfo(GetMatchScrutinee(matchSyntax)).Type;
        var patternDomainType = scrutineeType.GetNonNullableType();
        var union = patternDomainType.TryGetUnion() ?? patternDomainType.TryGetUnionCase()?.Union;
        if (union is not null)
        {
            var (caseName, payloadDisplay) = SplitCaseDisplay(missingCase);
            var caseType = union.DeclaredCaseTypes.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, caseName, StringComparison.Ordinal));
            if (caseType is not null)
            {
                if (payloadDisplay is not null)
                    return $".{caseName}({FormatNestedPayloadPattern(payloadDisplay)})";

                return caseType.ConstructorParameters.Length == 0
                    ? "." + caseName
                    : $".{caseName}({string.Join(", ", caseType.ConstructorParameters.Select(parameter => $"let {CreateBindingName(parameter.Name, "value")}"))})";
            }

            if (union.DeclaredCaseTypes.IsDefaultOrEmpty && !union.MemberTypes.IsDefaultOrEmpty)
                return FormatTypedDeclarationPattern(missingCase);
        }

        if (patternDomainType is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType &&
            enumType.GetMembers().OfType<IFieldSymbol>().Any(field =>
                field.IsConst &&
                field.ContainingType?.TypeKind == TypeKind.Enum &&
                string.Equals(field.Name, missingCase, StringComparison.Ordinal)))
        {
            return "." + missingCase;
        }

        if (TypeCoverageHelper.TryGetSealedHierarchy(patternDomainType, out var sealedRoot))
        {
            var projectedHierarchy = patternDomainType as INamedTypeSymbol ?? sealedRoot;
            var caseType = TypeCoverageHelper
                .GetSealedHierarchyCoverageTypes(sealedRoot, projectedHierarchy)
                .FirstOrDefault(candidate => MatchesTypeDisplay(candidate, missingCase));
            if (caseType is not null)
                return FormatSealedHierarchyPattern(caseType, missingCase);
        }

        return missingCase;
    }

    private static string FormatSealedHierarchyPattern(INamedTypeSymbol caseType, string typeDisplay)
    {
        var definition = caseType.OriginalDefinition as INamedTypeSymbol ?? caseType;
        if (definition is SourceNamedTypeSymbol { IsRecord: true } or
            ConstructedNamedTypeSymbol { OriginalDefinition: SourceNamedTypeSymbol { IsRecord: true } })
        {
            var parameters = definition.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax())
                .OfType<RecordDeclarationSyntax>()
                .Select(declaration => declaration.ParameterList)
                .FirstOrDefault(parameterList => parameterList is not null);
            if (parameters is { Parameters.Count: > 0 })
            {
                return $"{typeDisplay}({string.Join(", ", parameters.Parameters.Select(parameter =>
                    $"let {CreateBindingName(parameter.Identifier.ValueText, "value")}"))})";
            }

            return typeDisplay;
        }

        return $"{typeDisplay} {CreateTypeBindingName(caseType.Name)}";
    }

    private static bool MatchesTypeDisplay(INamedTypeSymbol type, string display)
        => string.Equals(type.Name, display, StringComparison.Ordinal) ||
           string.Equals(
               type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
               display,
               StringComparison.Ordinal);

    private static string FormatTypedDeclarationPattern(string typeDisplay)
        => typeDisplay is "null" or "true" or "false" or "()"
            ? typeDisplay
            : $"{typeDisplay} v";

    private static string FormatNestedPayloadPattern(string payloadDisplay)
        => payloadDisplay.StartsWith(".", StringComparison.Ordinal) ||
           payloadDisplay is "null" or "true" or "false" or "()"
            ? payloadDisplay
            : FormatTypedDeclarationPattern(payloadDisplay);

    private static (string CaseName, string? PayloadDisplay) SplitCaseDisplay(string missingCase)
    {
        var openParen = missingCase.IndexOf('(');
        if (openParen <= 0 || !missingCase.EndsWith(')'))
            return (missingCase, null);

        return (
            missingCase.Substring(0, openParen),
            missingCase.Substring(openParen + 1, missingCase.Length - openParen - 2));
    }

    private static string CreateBindingName(string sourceName, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(sourceName)
            ? fallback
            : char.ToLowerInvariant(sourceName[0]) + sourceName.Substring(1);
        return SyntaxFacts.TryParseKeyword(name, out _) ? "@" + name : name;
    }

    private static string CreateTypeBindingName(string typeName)
    {
        const string syntaxSuffix = "Syntax";
        if (typeName.EndsWith(syntaxSuffix, StringComparison.Ordinal) &&
            typeName.Length > syntaxSuffix.Length)
        {
            typeName = typeName.Substring(0, typeName.Length - syntaxSuffix.Length);
        }

        return CreateBindingName(typeName, "value");
    }

    private static string CreateMissingArmText(string sourceText, SyntaxNode matchSyntax, string patternText)
    {
        var newLine = sourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var armIndent = GetArmIndent(sourceText, matchSyntax);

        return $"{armIndent}{patternText} => {ThrowPlaceholderExpression}{newLine}";
    }

    private static string GetArmIndent(string sourceText, SyntaxNode matchSyntax)
    {
        var arms = GetMatchArms(matchSyntax);
        if (arms.Count > 0)
            return GetLineIndent(sourceText, arms[0].Span.Start);

        return GetLineIndent(sourceText, GetMatchCloseBraceToken(matchSyntax).Span.Start) + "    ";
    }

    private static SyntaxNode? FindContainingMatch(SyntaxNode? node)
        => node?.FirstAncestorOrSelf<MatchExpressionSyntax>()
            ?? (SyntaxNode?)node?.FirstAncestorOrSelf<PostfixMatchExpressionSyntax>()
            ?? node?.FirstAncestorOrSelf<MatchStatementSyntax>();

    private static MatchExhaustivenessInfo GetMatchExhaustiveness(
        SemanticModel semanticModel,
        SyntaxNode matchSyntax,
        MatchExhaustivenessOptions options)
        => matchSyntax switch
        {
            MatchExpressionSyntax keywordFirst => semanticModel.GetMatchExhaustiveness(keywordFirst, options),
            PostfixMatchExpressionSyntax postfix => semanticModel.GetMatchExhaustiveness(postfix, options),
            MatchStatementSyntax statement => semanticModel.GetMatchExhaustiveness(statement, options),
            _ => new MatchExhaustivenessInfo(isExhaustive: true, ImmutableArray<string>.Empty, hasCatchAll: false),
        };

    private static ExpressionSyntax GetMatchScrutinee(SyntaxNode matchSyntax)
        => matchSyntax switch
        {
            MatchExpressionSyntax keywordFirst => keywordFirst.Expression,
            PostfixMatchExpressionSyntax postfix => postfix.Expression,
            MatchStatementSyntax statement => statement.Expression,
            _ => throw new ArgumentException("Expected match syntax.", nameof(matchSyntax)),
        };

    private static SyntaxList<MatchArmSyntax> GetMatchArms(SyntaxNode matchSyntax)
        => matchSyntax switch
        {
            MatchExpressionSyntax keywordFirst => keywordFirst.Arms,
            PostfixMatchExpressionSyntax postfix => postfix.Arms,
            MatchStatementSyntax statement => statement.Arms,
            _ => default,
        };

    private static SyntaxToken GetMatchCloseBraceToken(SyntaxNode matchSyntax)
        => matchSyntax switch
        {
            MatchExpressionSyntax keywordFirst => keywordFirst.CloseBraceToken,
            PostfixMatchExpressionSyntax postfix => postfix.CloseBraceToken,
            MatchStatementSyntax statement => statement.CloseBraceToken,
            _ => default,
        };

    private static string GetLineIndent(string sourceText, int position)
    {
        var lineStart = sourceText.LastIndexOf('\n', Math.Max(0, position - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        var index = lineStart;
        while (index < sourceText.Length && (sourceText[index] == ' ' || sourceText[index] == '\t'))
            index++;

        return sourceText.Substring(lineStart, index - lineStart);
    }

    private static int GetLineStart(string sourceText, int position)
    {
        var lineStart = sourceText.LastIndexOf('\n', Math.Max(0, position - 1));
        return lineStart < 0 ? 0 : lineStart + 1;
    }

    private static TextSpan GetLineRemovalSpan(string sourceText, TextSpan span)
    {
        var start = sourceText.LastIndexOf('\n', Math.Max(0, span.Start - 1));
        start = start < 0 ? 0 : start + 1;

        var end = sourceText.IndexOf('\n', span.End);
        if (end >= 0)
            end++;
        else
            end = span.End;

        return new TextSpan(start, Math.Max(0, end - start));
    }
}
