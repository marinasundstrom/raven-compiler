using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class PreferOptionOverNullableCodeFixProvider : CodeFixProvider
{
    private static readonly ImmutableArray<string> FixableIds = [NonNullDeclarationsAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, NonNullDeclarationsAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var args = diagnostic.GetMessageArgs();
        if (args.Length < 1 || args[0] is not string suggestedType || string.IsNullOrWhiteSpace(suggestedType))
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        if (!TryFindTargetDeclarator(root, diagnostic.Location.SourceSpan, out var declarator))
            return;

        if (declarator.TypeAnnotation?.Type is { } explicitTypeSyntax &&
            Intersects(explicitTypeSyntax.Span, diagnostic.Location.SourceSpan))
        {
            var change = new TextChange(explicitTypeSyntax.Span, suggestedType);
            context.RegisterCodeFix(
                CodeAction.CreateTextChange(
                    $"Use '{suggestedType}'",
                    context.Document.Id,
                    change,
                    $"{nameof(PreferOptionOverNullableCodeFixProvider)}.UseType:{suggestedType}"));
        }

        if (!TryCreateRewriteToOptionAction(context.Document, root, diagnostic, suggestedType, out var rewriteAction))
            return;

        context.RegisterCodeFix(rewriteAction);
    }

    private static bool TryCreateRewriteToOptionAction(
        Document document,
        SyntaxNode root,
        Diagnostic diagnostic,
        string suggestedType,
        out CodeAction action)
    {
        action = null!;

        if (!TryFindTargetDeclarator(root, diagnostic.Location.SourceSpan, out var declarator))
            return false;

        if (declarator is null)
            return false;

        if (!TryCreateTypeRewriteChange(declarator, suggestedType, out var typeRewrite))
            return false;

        if (declarator.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault() is not { } localDeclaration)
            return false;

        if (localDeclaration.Declaration.Declarators.Count != 1)
            return false;

        if (localDeclaration.Declaration.BindingKeyword.Kind is not (SyntaxKind.ValKeyword or SyntaxKind.VarKeyword or SyntaxKind.LetKeyword))
            return false;

        var localName = declarator.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(localName))
        {
            return false;
        }

        if (localDeclaration.Parent is not BlockStatementSyntax block)
            return false;

        var statementIndex = IndexOfStatement(block, localDeclaration);
        if (statementIndex < 0)
            return false;

        var ifStatement = FindFirstGuardIfStatement(block, statementIndex + 1, localName);
        if (ifStatement is null)
            return false;

        if (!TryAnalyzeCondition(sourceText: document.GetTextAsync().GetAwaiter().GetResult(), ifStatement.Condition, localName, out var conditionRewrite))
            return false;

        var maybeName = CreateMaybeName(localName);
        if (string.IsNullOrWhiteSpace(maybeName) || string.Equals(maybeName, localName, StringComparison.Ordinal))
            return false;

        if (HasNameConflict(localDeclaration, block, maybeName))
            return false;

        if (!AreAllReferencesContained(block, localName, ifStatement))
            return false;

        var sourceText = document.GetTextAsync().GetAwaiter().GetResult();
        var changes = CreateRewriteChanges(sourceText, typeRewrite, declarator, ifStatement, maybeName, conditionRewrite);
        if (changes is null)
            return false;

        action = CodeAction.Create(
            "Rewrite nullable handling to Option pattern matching",
            (solution, cancellationToken) =>
            {
                var updatedDocument = solution.GetDocument(document.Id);
                if (updatedDocument is null)
                    return solution;

                var text = updatedDocument.GetTextAsync(cancellationToken).GetAwaiter().GetResult();
                foreach (var textChange in changes.OrderByDescending(change => change.Span.Start))
                    text = text.WithChange(textChange);

                return solution.WithDocumentText(document.Id, text);
            },
            nameof(PreferOptionOverNullableCodeFixProvider) + ".RewriteNullableHandling");

        return true;
    }

    private static TextChange[]? CreateRewriteChanges(
        SourceText sourceText,
        TextChange typeRewrite,
        VariableDeclaratorSyntax declarator,
        IfStatementSyntax ifStatement,
        string maybeName,
        ConditionRewrite conditionRewrite)
    {
        return
        [
            typeRewrite,
            new TextChange(declarator.Identifier.Span, maybeName),
            new TextChange(ifStatement.Condition.Span, $"{maybeName} is {conditionRewrite.SomePattern}")
        ];
    }

    private static bool TryFindTargetDeclarator(
        SyntaxNode root,
        TextSpan diagnosticSpan,
        out VariableDeclaratorSyntax declarator)
    {
        declarator = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(candidate =>
            {
                var typeSpan = candidate.TypeAnnotation?.Type.Span;
                if (typeSpan is not null &&
                    (typeSpan.Value == diagnosticSpan
                     || (typeSpan.Value.Start <= diagnosticSpan.Start && typeSpan.Value.End >= diagnosticSpan.End)
                     || (diagnosticSpan.Start <= typeSpan.Value.Start && diagnosticSpan.End >= typeSpan.Value.End)))
                {
                    return true;
                }

                var identifierSpan = candidate.Identifier.Span;
                return identifierSpan == diagnosticSpan
                    || (identifierSpan.Start <= diagnosticSpan.Start && identifierSpan.End >= diagnosticSpan.End)
                    || (diagnosticSpan.Start <= identifierSpan.Start && diagnosticSpan.End >= identifierSpan.End);
            })!;

        return declarator is not null;
    }

    private static bool TryCreateTypeRewriteChange(
        VariableDeclaratorSyntax declarator,
        string suggestedType,
        out TextChange change)
    {
        if (declarator.TypeAnnotation?.Type is { } typeSyntax)
        {
            change = new TextChange(typeSyntax.Span, suggestedType);
            return true;
        }

        var identifierSpan = declarator.Identifier.Span;
        change = new TextChange(new TextSpan(identifierSpan.End, 0), $": {suggestedType}");
        return true;
    }

    private static bool Intersects(TextSpan left, TextSpan right)
    {
        return left.Start < right.End && right.Start < left.End;
    }

    private static bool TryAnalyzeCondition(
        SourceText sourceText,
        ExpressionSyntax condition,
        string localName,
        out ConditionRewrite rewrite)
    {
        if (TryGetNullCheck(condition, localName))
        {
            rewrite = new ConditionRewrite($"Some(let {localName})");
            return true;
        }

        if (condition is not IsPatternExpressionSyntax isPattern)
        {
            rewrite = default;
            return false;
        }

        if (TryGetReferencedIdentifier(isPattern.Expression) is not { } identifier ||
            !string.Equals(identifier, localName, StringComparison.Ordinal))
        {
            rewrite = default;
            return false;
        }

        if (isPattern.Pattern is DeclarationPatternSyntax declarationPattern)
        {
            var patternText = sourceText.GetSubText(declarationPattern.Span);
            rewrite = new ConditionRewrite($"Some({patternText})");
            return true;
        }

        rewrite = default;
        return false;
    }

    private static bool TryGetNullCheck(
        ExpressionSyntax condition,
        string localName)
    {
        if (condition is IsPatternExpressionSyntax isPattern &&
            TryGetReferencedIdentifier(isPattern.Expression) is { } identifier &&
            string.Equals(identifier, localName, StringComparison.Ordinal) &&
            IsNotNullPattern(isPattern.Pattern))
        {
            return true;
        }

        if (condition is not InfixOperatorExpressionSyntax binary)
            return false;

        if (binary.OperatorToken.Kind != SyntaxKind.NotEqualsToken)
            return false;

        if (TryGetReferencedIdentifier(binary.Left) is { } leftIdentifier &&
            string.Equals(leftIdentifier, localName, StringComparison.Ordinal) &&
            IsNullLiteral(binary.Right))
        {
            return true;
        }

        if (TryGetReferencedIdentifier(binary.Right) is { } rightIdentifier &&
            string.Equals(rightIdentifier, localName, StringComparison.Ordinal) &&
            IsNullLiteral(binary.Left))
        {
            return true;
        }

        return false;
    }

    private static bool AreAllReferencesContained(
        BlockStatementSyntax block,
        string localName,
        IfStatementSyntax ifStatement)
    {
        foreach (var identifier in block.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!string.Equals(identifier.Identifier.ValueText, localName, StringComparison.Ordinal))
                continue;

            if (ifStatement.Condition.FullSpan.Contains(identifier.Span))
                continue;

            if (ifStatement.ThenStatement.FullSpan.Contains(identifier.Span))
                continue;

            return false;
        }

        return true;
    }

    private static bool HasNameConflict(
        LocalDeclarationStatementSyntax localDeclaration,
        BlockStatementSyntax block,
        string maybeName)
    {
        foreach (var declarator in block.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Span == localDeclaration.Declaration.Declarators[0].Span &&
                declarator.Kind == localDeclaration.Declaration.Declarators[0].Kind)
            {
                continue;
            }

            if (string.Equals(declarator.Identifier.ValueText, maybeName, StringComparison.Ordinal))
                return true;
        }

        foreach (var designation in block.DescendantNodes().OfType<SingleVariableDesignationSyntax>())
        {
            if (string.Equals(designation.Identifier.ValueText, maybeName, StringComparison.Ordinal))
                return true;
        }

        var callable = localDeclaration.Ancestors().FirstOrDefault(static ancestor =>
            ancestor is FunctionStatementSyntax or MethodDeclarationSyntax or ConstructorDeclarationSyntax or FunctionExpressionSyntax);
        if (callable is null)
            return false;

        foreach (var parameter in callable.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (string.Equals(parameter.Identifier.ValueText, maybeName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static int IndexOfStatement(BlockStatementSyntax block, StatementSyntax statement)
    {
        for (var i = 0; i < block.Statements.Count; i++)
        {
            if (block.Statements[i].Span == statement.Span &&
                block.Statements[i].Kind == statement.Kind)
            {
                return i;
            }
        }

        return -1;
    }

    private static IfStatementSyntax? FindFirstGuardIfStatement(BlockStatementSyntax block, int startIndex, string localName)
    {
        for (var i = startIndex; i < block.Statements.Count; i++)
        {
            var statement = block.Statements[i];
            if (statement is IfStatementSyntax ifStatement)
                return ifStatement;

            if (ContainsIdentifierReference(statement, localName))
                return null;
        }

        return null;
    }

    private static bool ContainsIdentifierReference(SyntaxNode node, string localName)
    {
        return node.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Any(identifier => string.Equals(identifier.Identifier.ValueText, localName, StringComparison.Ordinal));
    }

    private static string? TryGetReferencedIdentifier(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        if (expression is not IdentifierNameSyntax identifier)
            return null;

        return identifier.Identifier.ValueText;
    }

    private static bool IsNullLiteral(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression.Kind == SyntaxKind.NullLiteralExpression;
    }

    private static string CreateMaybeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.StartsWith("maybe", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var first = char.ToUpper(name[0], CultureInfo.InvariantCulture);
        return $"maybe{first}{name[1..]}";
    }

    private static bool IsNotNullPattern(PatternSyntax pattern)
    {
        return pattern is UnaryPatternSyntax
        {
            Kind: SyntaxKind.NotPattern,
            Pattern: ConstantPatternSyntax { Expression.Kind: SyntaxKind.NullLiteralExpression }
        };
    }

    private readonly record struct ConditionRewrite(string SomePattern);
}
