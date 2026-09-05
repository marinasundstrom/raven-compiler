using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class ConstructorParameterNamingCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(ConstructorParameterNamingCodeFixProvider);

    private static readonly ImmutableArray<string> FixableIds = [ConstructorParameterNamingAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, ConstructorParameterNamingAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        if (!diagnostic.Properties.TryGetValue(ConstructorParameterNamingAnalyzer.SuggestedNameProperty, out var suggestedName) ||
            string.IsNullOrWhiteSpace(suggestedName))
        {
            return;
        }

        var symbolKindDisplay = "parameter";
        if (diagnostic.Properties.TryGetValue(ConstructorParameterNamingAnalyzer.SymbolKindProperty, out var kindFromDiagnostic) &&
            !string.IsNullOrWhiteSpace(kindFromDiagnostic))
        {
            symbolKindDisplay = kindFromDiagnostic;
        }

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var parameter = node?.FirstAncestorOrSelf<ParameterSyntax>();
        if (parameter is null)
            return;

        var semanticModel = context.Document.GetSemanticModelAsync(context.CancellationToken).GetAwaiter().GetResult();
        if (semanticModel is null)
            return;

        var parameterSymbol = semanticModel.GetDeclaredSymbol(parameter) as IParameterSymbol;
        if (parameterSymbol is null)
            return;

        var scope = GetRenameScope(parameter);
        if (scope is null)
            return;

        var declarationSpan = parameter.Identifier.Span;
        var replacements = new List<TextSpan> { declarationSpan };

        foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var referenced = semanticModel.GetSymbolInfo(identifier).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(referenced, parameterSymbol))
                continue;

            replacements.Add(identifier.Identifier.Span);
        }

        if (replacements.Count == 0)
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                $"Rename {symbolKindDisplay} to '{suggestedName}'",
                (solution, _) =>
                {
                    var document = solution.GetDocument(context.Document.Id);
                    if (document is null)
                        return solution;

                    var updated = document.GetTextAsync(context.CancellationToken).GetAwaiter().GetResult();
                    foreach (var span in replacements
                                 .Distinct()
                                 .OrderByDescending(static s => s.Start))
                    {
                        updated = updated.WithChange(new TextChange(span, suggestedName));
                    }

                    return solution.WithDocumentText(context.Document.Id, updated);
                },
                EquivalenceKey));
    }

    private static SyntaxNode? GetRenameScope(ParameterSyntax parameter)
    {
        if (parameter.Parent is not ParameterListSyntax parameterList)
            return null;

        return parameterList.Parent switch
        {
            ConstructorDeclarationSyntax constructorDeclaration => constructorDeclaration,
            TypeDeclarationSyntax typeDeclaration => typeDeclaration,
            _ => null,
        };
    }
}
