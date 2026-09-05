using System.Collections.Immutable;
using System.Text;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class ImplementInterfaceMembersCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(ImplementInterfaceMembersCodeFixProvider);
    private const string PlaceholderExpression = "throw System.NotImplementedException()";

    private static readonly ImmutableArray<string> FixableIds =
        [CompilerDiagnostics.TypeDoesNotImplementAbstractMember.Id];

    private static readonly SymbolDisplayFormat MemberDisplayFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat.WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeParameters);

    private static readonly SymbolDisplayFormat GeneratedTypeDisplayFormat =
        SymbolDisplayFormat.RavenSignatureFormat.WithTypeQualificationStyle(
            SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!diagnostic.Location.IsInSource ||
            !string.Equals(
                diagnostic.Id,
                CompilerDiagnostics.TypeDoesNotImplementAbstractMember.Id,
                StringComparison.OrdinalIgnoreCase) ||
            !IsPrimaryDiagnostic(context, diagnostic))
        {
            return;
        }

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var declaration = FindTypeDeclaration(root, diagnostic.Location.SourceSpan);
        if (declaration is null)
            return;

        var semanticModel = context.Document
            .GetSemanticModelAsync(diagnostic.Location.SourceSpan.Start, context.CancellationToken)
            .GetAwaiter()
            .GetResult();
        if (semanticModel is null)
            return;

        var semanticDeclaration = FindTypeDeclaration(
            semanticModel.SyntaxTree.GetRoot(context.CancellationToken),
            diagnostic.Location.SourceSpan);
        if (semanticDeclaration is null ||
            semanticModel.GetDeclaredSymbol(semanticDeclaration) is not INamedTypeSymbol typeSymbol)
        {
            return;
        }

        var missingMembers = GetMissingInterfaceMembers(context, typeSymbol, declaration.Identifier.Span);
        if (missingMembers.IsDefaultOrEmpty)
            return;

        var sourceText = context.Document.GetTextAsync(context.CancellationToken).GetAwaiter().GetResult();
        var insertion = CreateInsertion(sourceText.ToString(), declaration, missingMembers);
        if (insertion.Text.Length == 0)
            return;

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Implement missing interface members",
                context.Document.Id,
                new TextChange(new TextSpan(insertion.Position, 0), insertion.Text),
                EquivalenceKey));
    }

    private static bool IsPrimaryDiagnostic(CodeFixContext context, Diagnostic diagnostic)
    {
        var first = context.Diagnostics.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Id,
                CompilerDiagnostics.TypeDoesNotImplementAbstractMember.Id,
                StringComparison.OrdinalIgnoreCase) &&
            candidate.Location.SourceSpan.Equals(diagnostic.Location.SourceSpan) &&
            ReferenceEquals(candidate.Location.SourceTree, diagnostic.Location.SourceTree));

        return first is null || ReferenceEquals(first, diagnostic);
    }

    private static BaseTypeDeclarationSyntax? FindTypeDeclaration(SyntaxNode root, TextSpan diagnosticSpan)
    {
        var token = root.FindToken(diagnosticSpan.Start);
        return token.Parent?.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>();
    }

    private static ImmutableArray<MissingInterfaceMember> GetMissingInterfaceMembers(
        CodeFixContext context,
        INamedTypeSymbol typeSymbol,
        TextSpan identifierSpan)
    {
        var builder = ImmutableArray.CreateBuilder<MissingInterfaceMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var diagnostic in context.Diagnostics)
        {
            if (!string.Equals(
                    diagnostic.Id,
                    CompilerDiagnostics.TypeDoesNotImplementAbstractMember.Id,
                    StringComparison.OrdinalIgnoreCase) ||
                !ReferenceEquals(diagnostic.Location.SourceTree, context.Diagnostic.Location.SourceTree) ||
                !diagnostic.Location.SourceSpan.Equals(identifierSpan))
            {
                continue;
            }

            var arguments = diagnostic.GetMessageArgs();
            if (arguments.Length < 3 ||
                arguments[0] is not string diagnosedTypeName ||
                arguments[1] is not string memberDisplay ||
                arguments[2] is not string declaringTypeDisplay ||
                !string.Equals(diagnosedTypeName, typeSymbol.Name, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var interfaceType in typeSymbol.AllInterfaces)
            {
                if (!string.Equals(
                        GetTypeDisplay(interfaceType),
                        declaringTypeDisplay,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var member = FindMember(interfaceType, memberDisplay);
                if (member is null)
                    continue;

                var key = string.Concat(
                    interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    "|",
                    member.Kind,
                    "|",
                    memberDisplay);
                if (seen.Add(key))
                    builder.Add(new MissingInterfaceMember(member));

                break;
            }
        }

        return builder
            .OrderBy(static missing => missing.Member.Locations
                .FirstOrDefault(static location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
            .ToImmutableArray();
    }

    private static ISymbol? FindMember(INamedTypeSymbol interfaceType, string memberDisplay)
    {
        foreach (var method in interfaceType.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.AssociatedSymbol is not null ||
                method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
            {
                continue;
            }

            if (string.Equals(GetMethodDiagnosticDisplay(method), memberDisplay, StringComparison.Ordinal))
                return method;
        }

        foreach (var property in interfaceType.GetMembers().OfType<IPropertySymbol>())
        {
            if (string.Equals(GetPropertyDiagnosticDisplay(property), memberDisplay, StringComparison.Ordinal) ||
                IsPropertyDiagnosticDisplay(property, memberDisplay))
            {
                return property;
            }
        }

        return null;
    }

    private static string GetMethodDiagnosticDisplay(IMethodSymbol method)
        => method.ToDisplayString(MemberDisplayFormat);

    private static string GetPropertyDiagnosticDisplay(IPropertySymbol property)
        => property.ToDisplayString(MemberDisplayFormat.WithMemberOptions(SymbolDisplayMemberOptions.None));

    private static bool IsPropertyDiagnosticDisplay(IPropertySymbol property, string memberDisplay)
    {
        var propertyName = property.Name;
        return string.Equals(memberDisplay, propertyName, StringComparison.Ordinal) ||
            memberDisplay.StartsWith(propertyName + " ", StringComparison.Ordinal) ||
            memberDisplay.StartsWith(propertyName + "[", StringComparison.Ordinal);
    }

    private static string GetTypeDisplay(INamedTypeSymbol type)
        => type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static (int Position, string Text) CreateInsertion(
        string source,
        BaseTypeDeclarationSyntax declaration,
        ImmutableArray<MissingInterfaceMember> missingMembers)
    {
        var closeBracePosition = declaration.CloseBraceToken.Span.Start;
        var closeBraceLineStart = GetLineStart(source, closeBracePosition);
        var closeBraceOnOwnLine = source.AsSpan(closeBraceLineStart, closeBracePosition - closeBraceLineStart)
            .Trim()
            .IsEmpty;
        var insertionPosition = closeBraceOnOwnLine ? closeBraceLineStart : closeBracePosition;
        var closingIndent = closeBraceOnOwnLine
            ? source.Substring(closeBraceLineStart, closeBracePosition - closeBraceLineStart)
            : GetLineIndent(source, declaration.OpenBraceToken.Span.Start);
        var memberIndent = closingIndent + "    ";

        var generatedMembers = missingMembers
            .Select(missing => IndentMember(GenerateMember(missing), memberIndent))
            .Where(static member => member.Length > 0)
            .ToArray();
        if (generatedMembers.Length == 0)
            return (insertionPosition, string.Empty);

        var hasExistingMember = declaration.OpenBraceToken.Span.End < insertionPosition &&
            source.AsSpan(declaration.OpenBraceToken.Span.End, insertionPosition - declaration.OpenBraceToken.Span.End)
                .IndexOfAnyExcept(" \t\r\n".AsSpan()) >= 0;
        var prefix = closeBraceOnOwnLine
            ? hasExistingMember ? "\n" : string.Empty
            : "\n";
        var suffix = closeBraceOnOwnLine ? string.Empty : "\n" + closingIndent;

        return (
            insertionPosition,
            prefix + string.Join("\n\n", generatedMembers) + "\n" + suffix);
    }

    private static string GenerateMember(MissingInterfaceMember missing)
        => missing.Member switch
        {
            IMethodSymbol method => GenerateMethod(method),
            IPropertySymbol property => GenerateProperty(property),
            _ => string.Empty
        };

    private static string GenerateMethod(IMethodSymbol method)
    {
        var modifiers = method.IsStatic ? "static " : string.Empty;
        var name = EscapeIdentifier(method.Name);
        var typeParameters = method.TypeParameters.IsDefaultOrEmpty
            ? string.Empty
            : "<" + string.Join(", ", method.TypeParameters.Select(
                static typeParameter => EscapeIdentifier(typeParameter.Name))) + ">";
        var parameters = string.Join(", ", method.Parameters.Select(FormatParameter));
        var returnType = method.ReturnType.ToDisplayStringKeywordAware(GeneratedTypeDisplayFormat);
        var signature = $"{modifiers}func {name}{typeParameters}({parameters}) -> {returnType}";

        var constraints = FormatConstraints(method.TypeParameters);
        return signature + constraints + " => " + PlaceholderExpression;
    }

    private static string GenerateProperty(IPropertySymbol property)
    {
        var bindingKeyword = property.SetMethod is null ? "val" : "var";
        var name = property.IsIndexer ? "self" : EscapeIdentifier(property.Name);
        var parameters = property.IsIndexer
            ? "[" + string.Join(", ", property.Parameters.Select(FormatParameter)) + "]"
            : string.Empty;
        var type = property.Type.ToDisplayStringKeywordAware(GeneratedTypeDisplayFormat);
        var declaration = $"{bindingKeyword} {name}{parameters}: {type}";

        if (property.GetMethod is not null && property.SetMethod is null)
            return declaration + " => " + PlaceholderExpression;

        var accessors = new List<string>();
        if (property.GetMethod is not null)
            accessors.Add("get => " + PlaceholderExpression);
        if (property.SetMethod is not null)
        {
            var setterKeyword = property.SetMethod.MethodKind == MethodKind.InitOnly ? "init" : "set";
            accessors.Add(setterKeyword + " => " + PlaceholderExpression);
        }

        return declaration + " {\n    " + string.Join("\n    ", accessors) + "\n}";
    }

    private static string FormatParameter(IParameterSymbol parameter)
    {
        var prefix = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter => "in ",
            _ => string.Empty
        };
        if (parameter.IsVarParams)
            prefix += "params ";

        var name = EscapeIdentifier(parameter.Name);
        var type = parameter.Type.ToDisplayStringKeywordAware(GeneratedTypeDisplayFormat);
        return $"{prefix}{name}: {type}";
    }

    private static string FormatConstraints(ImmutableArray<ITypeParameterSymbol> typeParameters)
    {
        var builder = new StringBuilder();
        foreach (var typeParameter in typeParameters)
        {
            var constraints = new List<string>();
            var kind = typeParameter.ConstraintKind;
            if ((kind & TypeParameterConstraintKind.ReferenceType) != 0)
                constraints.Add("class");
            if ((kind & TypeParameterConstraintKind.ValueType) != 0)
                constraints.Add("struct");
            if ((kind & TypeParameterConstraintKind.NotNull) != 0)
                constraints.Add("notnull");

            constraints.AddRange(typeParameter.ConstraintTypes.Select(type =>
                type.ToDisplayStringKeywordAware(GeneratedTypeDisplayFormat)));

            if ((kind & TypeParameterConstraintKind.Constructor) != 0)
                constraints.Add("new()");
            if ((kind & TypeParameterConstraintKind.AllowByRefLike) != 0)
                constraints.Add("allows ref struct");

            if (constraints.Count > 0)
            {
                builder.Append(" where ");
                builder.Append(EscapeIdentifier(typeParameter.Name));
                builder.Append(": ");
                builder.Append(string.Join(", ", constraints.Distinct(StringComparer.Ordinal)));
            }
        }

        return builder.ToString();
    }

    private static string EscapeIdentifier(string identifier)
        => SyntaxFacts.TryParseKeyword(identifier, out _) ? "@" + identifier : identifier;

    private static string IndentMember(string member, string indent)
        => indent + member.Replace("\n", "\n" + indent, StringComparison.Ordinal);

    private static int GetLineStart(string text, int position)
    {
        var lineBreak = text.LastIndexOf('\n', Math.Max(0, position - 1));
        return lineBreak < 0 ? 0 : lineBreak + 1;
    }

    private static string GetLineIndent(string text, int position)
    {
        var lineStart = GetLineStart(text, position);
        var index = lineStart;
        while (index < position && text[index] is ' ' or '\t')
            index++;

        return text.Substring(lineStart, index - lineStart);
    }

    private readonly record struct MissingInterfaceMember(ISymbol Member);
}
