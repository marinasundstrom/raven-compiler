using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

/// <summary>
/// Reference code fix that applies the inferred return type suggested by
/// <see cref="MissingReturnTypeAnnotationAnalyzer"/>.
/// </summary>
public sealed class MissingReturnTypeAnnotationCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(MissingReturnTypeAnnotationCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [MissingReturnTypeAnnotationAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, MissingReturnTypeAnnotationAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var args = diagnostic.GetMessageArgs();
        if (args.Length < 2 || args[1] is not string returnType || string.IsNullOrWhiteSpace(returnType))
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var declarationNode = FindDeclarationNode(root, diagnostic.Location.SourceSpan);
        if (declarationNode is null)
            return;

        var insertionPosition = declarationNode.ParameterList.Span.End;
        var change = new TextChange(new TextSpan(insertionPosition, 0), $" -> {returnType}");

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                $"Add return type annotation '-> {returnType}'",
                context.Document.Id,
                change,
                EquivalenceKey));
    }

    private static IBaseMethodOrFunctionDeclarationSyntax? FindDeclarationNode(SyntaxNode root, TextSpan diagnosticSpan)
    {
        var token = root.FindToken(diagnosticSpan.Start);
        if (token.Parent is null)
            return null;

        var method = token.Parent.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method is not null && method.ReturnType is null)
            return new BaseMethodOrFunctionDeclarationSyntaxAdapter(method);

        var function = token.Parent.FirstAncestorOrSelf<FunctionStatementSyntax>();
        if (function is not null && function.ReturnType is null)
            return new BaseMethodOrFunctionDeclarationSyntaxAdapter(function);

        return null;
    }

    private interface IBaseMethodOrFunctionDeclarationSyntax
    {
        ParameterListSyntax ParameterList { get; }
    }

    private readonly struct BaseMethodOrFunctionDeclarationSyntaxAdapter : IBaseMethodOrFunctionDeclarationSyntax
    {
        private readonly ParameterListSyntax _parameterList;

        public BaseMethodOrFunctionDeclarationSyntaxAdapter(MethodDeclarationSyntax method)
        {
            _parameterList = method.ParameterList;
        }

        public BaseMethodOrFunctionDeclarationSyntaxAdapter(FunctionStatementSyntax function)
        {
            _parameterList = function.ParameterList;
        }

        public ParameterListSyntax ParameterList => _parameterList;
    }
}
