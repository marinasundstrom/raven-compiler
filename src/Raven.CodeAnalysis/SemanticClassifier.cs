using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

public static class SemanticClassifier
{
    public static SemanticClassificationResult Classify(SyntaxNode node, SemanticModel model, bool allowBinding = true)
        => ClassifyCore(node, model, allowBinding);

    public static SemanticClassificationResult Classify(SyntaxNode node, bool allowBinding = false)
        => ClassifyCore(node, model: null, allowBinding);

    private static SemanticClassificationResult ClassifyCore(SyntaxNode node, SemanticModel? model, bool allowBinding)
    {
        var tokenMap = new Dictionary<SyntaxToken, SemanticClassification>();
        var triviaMap = new Dictionary<SyntaxTrivia, SemanticClassification>();
        var declaredTypeNames = allowBinding ? null : CollectDeclaredTypeNames(node);
        var declaredValueNames = allowBinding ? null : CollectDeclaredValueNames(node);

        foreach (var descendant in node.DescendantTokens(descendIntoTrivia: true))
        {
            var kind = descendant.Kind;

            if (kind is SyntaxKind.IsKeyword or SyntaxKind.AsKeyword)
            {
                tokenMap[descendant] = SemanticClassification.Keyword;
            }
            else if (descendant.Parent is YieldStatementSyntax yieldStatement && descendant == yieldStatement.FromKeyword ||
                     descendant.Parent is YieldExpressionSyntax yieldExpression && descendant == yieldExpression.FromKeyword)
            {
                tokenMap[descendant] = SemanticClassification.Keyword;
            }
            // Reserved words
            else if (descendant.IsKeyword())
            {
                tokenMap[descendant] = SemanticClassification.Keyword;
            }
            else if (descendant.Parent is MacroDeclarationSyntax macro &&
                     descendant == macro.MacroKeyword)
            {
                tokenMap[descendant] = SemanticClassification.Keyword;
            }
            else if (descendant.Parent is ParameterSyntax targetParameter &&
                     descendant == targetParameter.OnKeyword)
            {
                tokenMap[descendant] = SemanticClassification.Keyword;
            }
            else if (IsMacroContributionKeyword(descendant))
            {
                tokenMap[descendant] = SemanticClassification.Keyword;
            }
            else if (descendant.Parent is MacroCapabilityClauseSyntax capability &&
                     (descendant == capability.CapabilityKeyword || descendant == capability.ByKeyword))
            {
                tokenMap[descendant] = SemanticClassification.Keyword;
            }
            // Interpolated-string punctuation: color ${ and } as interpolation (but only when they belong to an Interpolation node).
            else if ((kind == SyntaxKind.DollarToken ||
                      kind == SyntaxKind.OpenBraceToken ||
                      kind == SyntaxKind.CloseBraceToken) &&
                     IsInterpolationPunctuation(descendant))
            {
                tokenMap[descendant] = SemanticClassification.Interpolation;
            }
            // Literals
            else if (kind == SyntaxKind.StringLiteralToken ||
                     kind == SyntaxKind.MultiLineStringLiteralToken ||
                     kind == SyntaxKind.StringStartToken ||
                     kind == SyntaxKind.StringEndToken)
            {
                tokenMap[descendant] = SemanticClassification.StringLiteral;
            }
            else if (kind == SyntaxKind.NumericLiteralToken)
            {
                tokenMap[descendant] = SemanticClassification.NumericLiteral;
            }
            else if (kind == SyntaxKind.QuestionToken && descendant.Parent is NullableTypeSyntax)
            {
                tokenMap[descendant] = SemanticClassification.NullableAnnotation;
            }
            // Identifiers (with symbol resolution)
            else if (kind == SyntaxKind.IdentifierToken)
            {
                if (model is not null && IsFreestandingMacroAlias(descendant, model))
                {
                    tokenMap[descendant] = SemanticClassification.Keyword;
                }
                else
                {
                    var bindNode = GetBindableParent(descendant);
                    if (bindNode is not null)
                    {
                        var symbol = ResolveSymbol(bindNode, model, allowBinding);

                        var classification = IsAliasTarget(bindNode)
                            ? SemanticClassification.Type
                            : symbol is null
                            ? ClassifyBySyntaxOrEventFallback(bindNode, model, allowBinding, declaredTypeNames, declaredValueNames)
                            : ClassifySymbol(symbol, bindNode);

                        classification = ClassifyDiscriminatedUnionCasePattern(descendant, bindNode, symbol, model, classification, allowBinding);
                        tokenMap[descendant] = classification;
                    }
                }
            }

            // Comments from trivia
            foreach (var trivia in descendant.LeadingTrivia.Concat(descendant.TrailingTrivia))
            {
                if (trivia.Kind == SyntaxKind.SingleLineCommentTrivia ||
                    trivia.Kind == SyntaxKind.MultiLineCommentTrivia ||
                    trivia.Kind == SyntaxKind.DirectiveTrivia ||
                    trivia.Kind == SyntaxKind.DocumentationCommentTrivia)
                {
                    triviaMap[trivia] = SemanticClassification.Comment;
                }
                else if (trivia.Kind == SyntaxKind.DisabledTextTrivia)
                {
                    triviaMap[trivia] = SemanticClassification.InactiveCode;
                }
            }
        }

        return new SemanticClassificationResult(tokenMap, triviaMap);
    }

    private static bool IsFreestandingMacroAlias(SyntaxToken token, SemanticModel model)
    {
        var carrier = token.Parent?.AncestorsAndSelf().FirstOrDefault(static node =>
            node is FreestandingMacroExpressionSyntax or
                FreestandingMacroMemberDeclarationSyntax or
                FreestandingMacroDeclarationSyntax);
        if (carrier is null ||
            !FreestandingMacroInvocation.TryCreate(carrier, out var invocation) ||
            !invocation.Name.Span.Contains(token.Span) ||
            !invocation.TryGetMacroName(out var name) ||
            !model.Compilation.GetMacroRegistry().TryResolveFreestandingMacro(
                model.Compilation,
                carrier,
                name,
                out var loaded,
                out _))
        {
            return false;
        }

        return loaded.Aliases.Contains(name, StringComparer.Ordinal);
    }

    private static bool IsMacroContributionKeyword(SyntaxToken token)
    {
        return token.Parent switch
        {
            MacroExpansionStatementSyntax statement => token == statement.Keyword,
            MacroExpansionExpressionSyntax expression => token == expression.Keyword,
            _ => false
        };
    }

    private static ISymbol? ResolveSymbol(SyntaxNode node, SemanticModel? model, bool allowBinding)
    {
        if (model is null)
            return null;

        if (allowBinding)
        {
            var info = model.GetSymbolInfo(node);
            return info.Symbol
                   ?? info.CandidateSymbols.FirstOrDefault()
                   ?? model.GetDeclaredSymbol(node);
        }

        if (model.TryGetCachedSymbolInfo(node, out var cachedInfo))
            return cachedInfo.Symbol ?? cachedInfo.CandidateSymbols.FirstOrDefault();

        return CanResolveDeclaredSymbolWithoutBinding(node)
            ? model.GetDeclaredSymbol(node)
            : null;
    }

    private static bool CanResolveDeclaredSymbolWithoutBinding(SyntaxNode node)
        => node switch
        {
            NamespaceDeclarationSyntax => true,
            TypeDeclarationSyntax => true,
            UnionDeclarationSyntax => true,
            CaseDeclarationSyntax => true,
            DelegateDeclarationSyntax => true,
            BaseMethodDeclarationSyntax => true,
            FunctionStatementSyntax => true,
            MacroDeclarationSyntax => true,
            PropertyDeclarationSyntax => true,
            EventDeclarationSyntax => true,
            AccessorDeclarationSyntax => true,
            ParameterSyntax parameter => parameter.Parent?.Parent is TypeDeclarationSyntax
                or BaseMethodDeclarationSyntax
                or MacroDeclarationSyntax,
            _ => false
        };

    private static SemanticClassification ClassifySymbol(ISymbol symbol, SyntaxNode node)
    {
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } &&
            node is InvocationExpressionSyntax)
        {
            return SemanticClassification.Type;
        }

        return symbol switch
        {
            INamespaceSymbol => SemanticClassification.Namespace,
            ITypeSymbol => SemanticClassification.Type,
            IMethodSymbol => SemanticClassification.Method,
            IMacroDeclarationSymbol => SemanticClassification.Macro,
            IParameterSymbol => SemanticClassification.Parameter,
            ILocalSymbol => SemanticClassification.Local,
            ILabelSymbol => SemanticClassification.Label,
            IFieldSymbol => SemanticClassification.Field,
            IPropertySymbol => SemanticClassification.Property,
            IEventSymbol => SemanticClassification.Event,
            _ => SemanticClassification.Default
        };
    }

    private static bool IsAliasTarget(SyntaxNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is AliasDirectiveSyntax)
                return true;
        }

        return false;
    }

    private static SemanticClassification ClassifyBySyntax(SyntaxNode node)
    {
        return node switch
        {
            TypeDeclarationSyntax => SemanticClassification.Type,
            UnionDeclarationSyntax => SemanticClassification.Type,
            CaseDeclarationSyntax => SemanticClassification.Type,
            DelegateDeclarationSyntax => SemanticClassification.Type,
            BaseMethodDeclarationSyntax => SemanticClassification.Method,
            FunctionStatementSyntax => SemanticClassification.Method,
            MacroDeclarationSyntax => SemanticClassification.Macro,
            PropertyDeclarationSyntax => SemanticClassification.Property,
            EventDeclarationSyntax => SemanticClassification.Event,
            InvocationExpressionSyntax => SemanticClassification.Method,
            MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax } => SemanticClassification.Method,
            MemberBindingExpressionSyntax { Parent: InvocationExpressionSyntax } => SemanticClassification.Method,
            MemberPatternPathSyntax => SemanticClassification.Type,
            ParameterSyntax => SemanticClassification.Parameter,
            VariableDeclaratorSyntax => SemanticClassification.Local,
            SingleVariableDesignationSyntax => SemanticClassification.Local,
            TypeSyntax => SemanticClassification.Type,
            _ => SemanticClassification.Default
        };
    }

    private static SemanticClassification ClassifyBySyntaxOrEventFallback(
        SyntaxNode node,
        SemanticModel? model,
        bool allowBinding,
        IReadOnlySet<string>? declaredTypeNames,
        IReadOnlyDictionary<string, SemanticClassification>? declaredValueNames)
    {
        if (!allowBinding &&
            TryGetReferenceName(node, out var referenceName) &&
            declaredValueNames is not null &&
            declaredValueNames.TryGetValue(referenceName, out var valueClassification))
        {
            return valueClassification;
        }

        if (!allowBinding &&
            declaredTypeNames is not null &&
            TryGetReferenceName(node, out referenceName) &&
            declaredTypeNames.Contains(referenceName))
        {
            return SemanticClassification.Type;
        }

        var bySyntax = ClassifyBySyntax(node);
        if (bySyntax != SemanticClassification.Default)
            return bySyntax;

        if (!allowBinding)
            return SemanticClassification.Default;

        if (model is null)
            return SemanticClassification.Default;

        if (node is MemberAccessExpressionSyntax memberAccess)
        {
            var receiverType = model.GetTypeInfo(memberAccess.Expression).Type;
            if (receiverType is not null)
            {
                var memberName = memberAccess.Name.Identifier.ValueText;
                var member = receiverType.GetMembers(memberName).OfType<IEventSymbol>().FirstOrDefault();
                if (member is not null)
                    return SemanticClassification.Event;
            }
        }
        else if (node is IdentifierNameSyntax identifier)
        {
            var binder = model.GetBinder(identifier);
            var memberName = identifier.Identifier.ValueText;
            if (binder.LookupSymbols(memberName).OfType<IEventSymbol>().Any())
                return SemanticClassification.Event;
        }

        return SemanticClassification.Default;
    }

    private static IReadOnlySet<string> CollectDeclaredTypeNames(SyntaxNode root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in root.DescendantNodesAndSelf())
        {
            switch (declaration)
            {
                case TypeDeclarationSyntax typeDeclaration:
                    names.Add(typeDeclaration.Identifier.Text);
                    break;

                case UnionDeclarationSyntax unionDeclaration:
                    names.Add(unionDeclaration.Identifier.Text);
                    break;

                case CaseDeclarationSyntax caseDeclaration:
                    names.Add(caseDeclaration.Identifier.Text);
                    break;

                case DelegateDeclarationSyntax delegateDeclaration:
                    names.Add(delegateDeclaration.Identifier.Text);
                    break;
            }
        }

        return names;
    }

    private static IReadOnlyDictionary<string, SemanticClassification> CollectDeclaredValueNames(SyntaxNode root)
    {
        var names = new Dictionary<string, SemanticClassification>(StringComparer.Ordinal);
        foreach (var declaration in root.DescendantNodesAndSelf())
        {
            switch (declaration)
            {
                case ParameterSyntax parameter:
                    names[parameter.Identifier.Text] = SemanticClassification.Parameter;
                    break;

                case VariableDeclaratorSyntax variable:
                    names[variable.Identifier.Text] = SemanticClassification.Local;
                    break;

                case SingleVariableDesignationSyntax designation:
                    names[designation.Identifier.Text] = SemanticClassification.Local;
                    break;
            }
        }

        return names;
    }

    private static bool TryGetReferenceName(SyntaxNode node, out string name)
    {
        switch (node)
        {
            case IdentifierNameSyntax identifier:
                name = identifier.Identifier.Text;
                return !string.IsNullOrEmpty(name);

            case GenericNameSyntax genericName:
                name = genericName.Identifier.Text;
                return !string.IsNullOrEmpty(name);

            case InvocationExpressionSyntax { Expression: IdentifierNameSyntax identifier }:
                name = identifier.Identifier.Text;
                return !string.IsNullOrEmpty(name);

            case InvocationExpressionSyntax { Expression: GenericNameSyntax genericName }:
                name = genericName.Identifier.Text;
                return !string.IsNullOrEmpty(name);

            case MemberAccessExpressionSyntax memberAccess:
                name = memberAccess.Name.Identifier.Text;
                return !string.IsNullOrEmpty(name);

            case MemberBindingExpressionSyntax memberBinding:
                name = memberBinding.Name.Identifier.Text;
                return !string.IsNullOrEmpty(name);

            default:
                name = string.Empty;
                return false;
        }
    }

    private static SemanticClassification ClassifyDiscriminatedUnionCasePattern(
        SyntaxToken token,
        SyntaxNode bindNode,
        ISymbol? symbol,
        SemanticModel? model,
        SemanticClassification current,
        bool allowBinding)
    {
        if (IsUnionCasePatternNode(bindNode))
        {
            if (current == SemanticClassification.Type)
                return SemanticClassification.Type;

            if (symbol is ITypeSymbol typeSymbol && typeSymbol.IsUnionCase)
                return SemanticClassification.Type;

            if (symbol is IMethodSymbol methodSymbol && methodSymbol.ReturnType.IsUnionCase)
                return SemanticClassification.Type;

            if (symbol is IMethodSymbol methodOnDiscriminatedUnion &&
                (methodOnDiscriminatedUnion.ContainingType.IsUnionCase ||
                 methodOnDiscriminatedUnion.ContainingType.IsUnion))
            {
                return SemanticClassification.Type;
            }

            if (symbol is IPropertySymbol propertyOnDiscriminatedUnion &&
                (propertyOnDiscriminatedUnion.ContainingType?.IsUnionCase == true ||
                 propertyOnDiscriminatedUnion.ContainingType?.IsUnion == true ||
                 propertyOnDiscriminatedUnion.Type.IsUnionCase))
            {
                return SemanticClassification.Type;
            }

            if (symbol is IFieldSymbol fieldOnDiscriminatedUnion &&
                (fieldOnDiscriminatedUnion.ContainingType?.IsUnionCase == true ||
                 fieldOnDiscriminatedUnion.ContainingType?.IsUnion == true ||
                 fieldOnDiscriminatedUnion.Type.IsUnionCase))
            {
                return SemanticClassification.Type;
            }

            if (current == SemanticClassification.Method && bindNode is IdentifierNameSyntax { Parent: ConstantPatternSyntax })
                return SemanticClassification.Type;

            if (allowBinding && model is not null && bindNode is ExpressionSyntax expression)
            {
                var typeInfo = model.GetTypeInfo(expression).Type;
                if (typeInfo?.IsUnionCase == true)
                    return SemanticClassification.Type;
            }
        }

        if (current == SemanticClassification.Method && IsIdentifierInMatchArmPatternPosition(token))
            return SemanticClassification.Type;

        return current;
    }

    private static bool IsUnionCasePatternNode(SyntaxNode bindNode)
    {
        return bindNode switch
        {
            MemberPatternPathSyntax => true,
            IdentifierNameSyntax { Parent: ConstantPatternSyntax } => true,
            GenericNameSyntax { Parent: ConstantPatternSyntax } => true,
            _ => false
        };
    }

    private static bool IsIdentifierInMatchArmPatternPosition(SyntaxToken token)
    {
        if (token.Kind != SyntaxKind.IdentifierToken)
            return false;

        for (var current = token.Parent; current is not null; current = current.Parent)
        {
            if (current is MatchArmSyntax matchArm)
                return token.Span.End <= matchArm.ArrowToken.Span.Start;
        }

        return false;
    }

    private static bool IsInterpolationPunctuation(SyntaxToken token)
    {
        // We only want to color these tokens as interpolation when they are part of an InterpolationSyntax node.
        // This avoids coloring normal braces elsewhere.
        var parent = token.Parent;

        if (parent is InterpolationSyntax)
            return true;

        return parent?.Parent is InterpolationSyntax;
    }

    private static SyntaxNode? GetBindableParent(SyntaxToken token)
    {
        var node = token.Parent;

        // Namespace declarations expose their symbol from the declaration node
        if (node is IdentifierNameSyntax && node.Parent is NamespaceDeclarationSyntax ns && ns.Name == node)
            return ns;

        // Climb to the outermost bindable node that includes this identifier
        while (node != null)
        {
            if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node)
                node = ma;
            else if (node.Parent is MemberBindingExpressionSyntax mb && mb.Name == node)
                node = mb;
            else if (node.Parent is InvocationExpressionSyntax inv && inv.Expression == node)
                node = inv;
            else
                break;
        }

        return node;
    }
}

public sealed record SemanticClassificationResult(
    Dictionary<SyntaxToken, SemanticClassification> Tokens,
    Dictionary<SyntaxTrivia, SemanticClassification> Trivia
);

public enum SemanticClassification
{
    Default,
    Keyword,
    NumericLiteral,
    StringLiteral,
    Operator,
    Interpolation,
    Comment,
    Namespace,
    Type,
    Method,
    Macro,
    Parameter,
    Local,
    Label,
    Property,
    Field,
    Event,
    NullableAnnotation,
    InactiveCode
}
