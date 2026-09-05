using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class ExpressionBodyToBlockBodyCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(ExpressionBodyToBlockBodyCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [ExpressionBodyToBlockBodyAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, ExpressionBodyToBlockBodyAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null || syntaxTree is null)
            return;

        var semanticModel = context.Document.GetSemanticModelAsync(context.CancellationToken).GetAwaiter().GetResult();
        if (semanticModel is null)
            return;

        var anchorNode = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var declaration = FindDeclaration(anchorNode);
        if (declaration is null || !TryGetExpressionBody(declaration, out var expressionBody))
            return;

        var expression = expressionBody.Expression;
        var sourceText = syntaxTree.GetText();
        var replacement = BuildBlockBodyText(declaration, expression, semanticModel, sourceText);
        var replacementSpan = GetExpressionBodyReplacementSpan(declaration, expressionBody);
        var change = new TextChange(replacementSpan, replacement);

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Convert to block body",
                context.Document.Id,
                change,
                EquivalenceKey));
    }

    internal static SyntaxNode? FindDeclaration(SyntaxNode node)
    {
        if (node is MethodDeclarationSyntax or
            FunctionStatementSyntax or
            AccessorDeclarationSyntax or
            ConstructorDeclarationSyntax or
            OperatorDeclarationSyntax or
            ConversionOperatorDeclarationSyntax)
        {
            return node;
        }

        if (node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method)
            return method;
        if (node.FirstAncestorOrSelf<FunctionStatementSyntax>() is { } function)
            return function;
        if (node.FirstAncestorOrSelf<AccessorDeclarationSyntax>() is { } accessor)
            return accessor;
        if (node.FirstAncestorOrSelf<ConstructorDeclarationSyntax>() is { } constructor)
            return constructor;
        if (node.FirstAncestorOrSelf<OperatorDeclarationSyntax>() is { } op)
            return op;

        return node.FirstAncestorOrSelf<ConversionOperatorDeclarationSyntax>();
    }

    internal static bool TryGetExpressionBody(SyntaxNode declaration, out ArrowExpressionClauseSyntax expressionBody)
    {
        expressionBody = declaration switch
        {
            MethodDeclarationSyntax method => method.ExpressionBody,
            FunctionStatementSyntax function => function.ExpressionBody,
            AccessorDeclarationSyntax accessor => accessor.ExpressionBody,
            ConstructorDeclarationSyntax constructor => constructor.ExpressionBody,
            OperatorDeclarationSyntax op => op.ExpressionBody,
            ConversionOperatorDeclarationSyntax conversion => conversion.ExpressionBody,
            _ => null
        };

        return expressionBody is not null;
    }

    internal static TextSpan GetExpressionBodyReplacementSpan(SyntaxNode declaration, ArrowExpressionClauseSyntax expressionBody)
    {
        var spanStart = expressionBody.Span.Start;
        var terminator = GetTerminatorToken(declaration);

        if (terminator.Kind is not SyntaxKind.None)
            return new TextSpan(spanStart, terminator.Span.End - spanStart);

        return expressionBody.Span;
    }

    private static SyntaxToken GetTerminatorToken(SyntaxNode declaration)
    {
        return declaration switch
        {
            MethodDeclarationSyntax method => method.TerminatorToken,
            FunctionStatementSyntax function => function.TerminatorToken,
            AccessorDeclarationSyntax accessor => accessor.TerminatorToken,
            ConstructorDeclarationSyntax constructor => constructor.TerminatorToken,
            OperatorDeclarationSyntax op => op.TerminatorToken,
            ConversionOperatorDeclarationSyntax conversion => conversion.TerminatorToken,
            _ => default
        };
    }

    internal static string BuildBlockBodyText(
        SyntaxNode declaration,
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        SourceText sourceText)
    {
        var statementText = BuildStatementText(declaration, expression, semanticModel);
        var declarationIndent = GetIndentation(sourceText, declaration.Span.Start);
        var statementIndent = declarationIndent + "    ";
        return "{\n" + statementIndent + statementText + "\n" + declarationIndent + "}";
    }

    private static string BuildStatementText(SyntaxNode declaration, ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (declaration is AccessorDeclarationSyntax accessor &&
            accessor.Kind == SyntaxKind.GetAccessorDeclaration)
        {
            return $"return {expression}";
        }

        if (declaration is ConstructorDeclarationSyntax)
            return expression.ToString();

        if (declaration is MethodDeclarationSyntax method)
            return ShouldUseReturnStatement((semanticModel.GetDeclaredSymbol(method) as IMethodSymbol)?.ReturnType) ? $"return {expression}" : expression.ToString();

        if (declaration is FunctionStatementSyntax function)
            return ShouldUseReturnStatement((semanticModel.GetDeclaredSymbol(function) as IMethodSymbol)?.ReturnType) ? $"return {expression}" : expression.ToString();

        if (declaration is OperatorDeclarationSyntax operatorDeclaration)
            return ShouldUseReturnStatement((semanticModel.GetDeclaredSymbol(operatorDeclaration) as IMethodSymbol)?.ReturnType) ? $"return {expression}" : expression.ToString();

        if (declaration is ConversionOperatorDeclarationSyntax conversion)
            return ShouldUseReturnStatement((semanticModel.GetDeclaredSymbol(conversion) as IMethodSymbol)?.ReturnType) ? $"return {expression}" : expression.ToString();

        return expression.ToString();
    }

    private static bool ShouldUseReturnStatement(ITypeSymbol? returnType)
    {
        if (returnType is null)
            return false;

        return returnType.SpecialType is not SpecialType.System_Void and not SpecialType.System_Unit;
    }

    private static string GetIndentation(SourceText text, int position)
    {
        if (position < 0 || position > text.Length)
            return string.Empty;

        var fullText = text.ToString();
        var lineStart = position;
        while (lineStart > 0 && fullText[lineStart - 1] is not ('\n' or '\r'))
            lineStart--;

        var lineText = fullText.Substring(lineStart, position - lineStart);
        var i = 0;

        while (i < lineText.Length && (lineText[i] == ' ' || lineText[i] == '\t'))
            i++;

        return i == 0 ? string.Empty : lineText[..i];
    }
}
