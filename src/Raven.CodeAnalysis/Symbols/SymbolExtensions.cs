using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;

using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

public static partial class SymbolExtensions
{
    private static readonly Dictionary<string, string> s_specialTypeNames = new()
    {
        ["System.Object"] = "object",
        ["System.String"] = "string",
        ["System.Boolean"] = "bool",
        ["System.Byte"] = "byte",
        ["System.SByte"] = "sbyte",
        ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort",
        ["System.Int32"] = "int",
        ["System.UInt32"] = "uint",
        ["System.Int64"] = "long",
        ["System.UInt64"] = "ulong",
        ["System.Single"] = "float",
        ["System.Double"] = "double",
        ["System.Decimal"] = "decimal",
        ["System.Char"] = "char",
        ["System.Void"] = "void",
        ["System.Unit"] = "unit"
    };

    public static string ToFullyQualifiedMetadataName(this INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol is null)
            throw new ArgumentNullException(nameof(typeSymbol));

        return ((ITypeSymbol)typeSymbol).ToFullyQualifiedMetadataName();
    }

    public static bool MetadataIdentityEquals(this ITypeSymbol? left, ITypeSymbol? right)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right))
            return true;

        if (left is null || right is null)
            return false;

        if (HaveEquivalentMetadataIdentity(left, right))
            return true;

        var leftIdentity = GetStableMetadataIdentity(left);
        var rightIdentity = GetStableMetadataIdentity(right);
        return string.Equals(leftIdentity, rightIdentity, StringComparison.Ordinal);
    }

    private static bool HaveEquivalentMetadataIdentity(ITypeSymbol left, ITypeSymbol right)
    {
        if (left is ITupleTypeSymbol leftTuple)
            left = leftTuple.UnderlyingTupleType;

        if (right is ITupleTypeSymbol rightTuple)
            right = rightTuple.UnderlyingTupleType;

        if (left.TypeKind != right.TypeKind)
            return false;

        switch (left)
        {
            case ITypeParameterSymbol leftTypeParameter when right is ITypeParameterSymbol rightTypeParameter:
                return HaveEquivalentTypeParameterIdentity(leftTypeParameter, rightTypeParameter);

            case INamedTypeSymbol leftNamed when right is INamedTypeSymbol rightNamed:
                return HaveEquivalentNamedTypeIdentity(leftNamed, rightNamed);

            case IArrayTypeSymbol leftArray when right is IArrayTypeSymbol rightArray:
                return leftArray.Rank == rightArray.Rank &&
                       leftArray.IsFixedArray == rightArray.IsFixedArray &&
                       leftArray.FixedLength == rightArray.FixedLength &&
                       HaveEquivalentMetadataIdentity(leftArray.ElementType, rightArray.ElementType);

            case IPointerTypeSymbol leftPointer when right is IPointerTypeSymbol rightPointer:
                return HaveEquivalentMetadataIdentity(leftPointer.PointedAtType, rightPointer.PointedAtType);

            case IAddressTypeSymbol leftAddress when right is IAddressTypeSymbol rightAddress:
                return HaveEquivalentMetadataIdentity(leftAddress.ReferencedType, rightAddress.ReferencedType);

            case NullableTypeSymbol leftNullable when right is NullableTypeSymbol rightNullable:
                return HaveEquivalentMetadataIdentity(leftNullable.UnderlyingType, rightNullable.UnderlyingType);

            case LiteralTypeSymbol leftLiteral when right is LiteralTypeSymbol rightLiteral:
                return HaveEquivalentMetadataIdentity(leftLiteral.UnderlyingType, rightLiteral.UnderlyingType);

            default:
                return false;
        }
    }

    private static bool HaveEquivalentTypeParameterIdentity(ITypeParameterSymbol left, ITypeParameterSymbol right)
    {
        var leftDefinition = (ITypeParameterSymbol)(left.OriginalDefinition ?? left);
        var rightDefinition = (ITypeParameterSymbol)(right.OriginalDefinition ?? right);

        if (ReferenceEquals(leftDefinition, rightDefinition))
            return true;

        if (leftDefinition.OwnerKind != rightDefinition.OwnerKind ||
            leftDefinition.Ordinal != rightDefinition.Ordinal ||
            !string.Equals(leftDefinition.Name, rightDefinition.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (leftDefinition.OwnerKind == TypeParameterOwnerKind.Method)
        {
            var leftOwner = (IMethodSymbol?)(leftDefinition.DeclaringMethodParameterOwner?.OriginalDefinition ?? leftDefinition.DeclaringMethodParameterOwner);
            var rightOwner = (IMethodSymbol?)(rightDefinition.DeclaringMethodParameterOwner?.OriginalDefinition ?? rightDefinition.DeclaringMethodParameterOwner);
            return SymbolEqualityComparer.Default.Equals(leftOwner, rightOwner);
        }

        var leftTypeOwner = (INamedTypeSymbol?)(leftDefinition.DeclaringTypeParameterOwner?.OriginalDefinition ?? leftDefinition.DeclaringTypeParameterOwner);
        var rightTypeOwner = (INamedTypeSymbol?)(rightDefinition.DeclaringTypeParameterOwner?.OriginalDefinition ?? rightDefinition.DeclaringTypeParameterOwner);
        return SymbolEqualityComparer.Default.Equals(leftTypeOwner, rightTypeOwner);
    }

    private static bool HaveEquivalentNamedTypeIdentity(INamedTypeSymbol left, INamedTypeSymbol right)
    {
        var leftDefinition = (INamedTypeSymbol)(left.OriginalDefinition ?? left);
        var rightDefinition = (INamedTypeSymbol)(right.OriginalDefinition ?? right);

        if (!string.Equals(leftDefinition.ToFullyQualifiedMetadataName(), rightDefinition.ToFullyQualifiedMetadataName(), StringComparison.Ordinal))
            return false;

        if (left.TypeArguments.Length != right.TypeArguments.Length)
            return false;

        for (var i = 0; i < left.TypeArguments.Length; i++)
        {
            if (!HaveEquivalentMetadataIdentity(left.TypeArguments[i], right.TypeArguments[i]))
                return false;
        }

        return true;
    }

    private static string GetStableMetadataIdentity(ITypeSymbol typeSymbol)
    {
        switch (typeSymbol)
        {
            case ITypeParameterSymbol typeParameter:
                var definition = (ITypeParameterSymbol)(typeParameter.OriginalDefinition ?? typeParameter);
                var ownerIdentity = definition.OwnerKind switch
                {
                    TypeParameterOwnerKind.Type => (definition.DeclaringTypeParameterOwner?.OriginalDefinition as INamedTypeSymbol
                                                    ?? definition.DeclaringTypeParameterOwner)
                                                    ?.ToFullyQualifiedMetadataName()
                                                    ?? "<type>",
                    TypeParameterOwnerKind.Method => (definition.DeclaringMethodParameterOwner?.OriginalDefinition as IMethodSymbol
                                                      ?? definition.DeclaringMethodParameterOwner) is { } method
                        ? $"{(method.ContainingType?.OriginalDefinition as INamedTypeSymbol ?? method.ContainingType)?.ToFullyQualifiedMetadataName() ?? "<global>"}::{method.MetadataName}"
                        : "<method>",
                    _ => "<unknown>"
                };
                return $"!{definition.OwnerKind}:{ownerIdentity}:{definition.Ordinal}:{definition.Name}";

            case IArrayTypeSymbol arrayType:
                return $"[{arrayType.Rank},{arrayType.IsFixedArray},{arrayType.FixedLength?.ToString() ?? string.Empty}]{GetStableMetadataIdentity(arrayType.ElementType)}";

            case IPointerTypeSymbol pointerType:
                return $"*{GetStableMetadataIdentity(pointerType.PointedAtType)}";

            case IAddressTypeSymbol addressType:
                return $"&{GetStableMetadataIdentity(addressType.ReferencedType)}";

            case NullableTypeSymbol nullableType:
                return $"?{GetStableMetadataIdentity(nullableType.UnderlyingType)}";

            case LiteralTypeSymbol literalType:
                return $"literal({GetStableMetadataIdentity(literalType.UnderlyingType)})";

            case INamedTypeSymbol namedType:
                var definitionIdentity = namedType.OriginalDefinition is INamedTypeSymbol originalDefinition
                    ? originalDefinition.ToFullyQualifiedMetadataName()
                    : namedType.ToFullyQualifiedMetadataName();

                if (namedType.TypeArguments.IsDefaultOrEmpty || namedType.TypeArguments.Length == 0)
                    return definitionIdentity;

                return $"{definitionIdentity}<{string.Join(",", namedType.TypeArguments.Select(GetStableMetadataIdentity))}>";

            default:
                return typeSymbol.MetadataName ?? typeSymbol.Name ?? string.Empty;
        }
    }

    /// <summary>
    /// Legacy name: now just delegates to the central type formatter.
    /// </summary>
    public static string ToDisplayStringKeywordAware(this ITypeSymbol typeSymbol, SymbolDisplayFormat format)
    {
        return FormatType(typeSymbol, format);
    }

    public static string ToDisplayStringForDiagnostics(this ITypeSymbol typeSymbol, SymbolDisplayFormat format)
    {
        return FormatType(typeSymbol, format);
    }

    public static bool ContainsErrorType(this ITypeSymbol? typeSymbol)
        => ContainsErrorType(typeSymbol, new HashSet<ITypeSymbol>(ReferenceEqualityComparer.Instance));

    private static bool ContainsErrorType(ITypeSymbol? typeSymbol, HashSet<ITypeSymbol> visited)
    {
        if (typeSymbol is null)
            return false;

        if (!visited.Add(typeSymbol))
            return false;

        if (typeSymbol is IErrorTypeSymbol)
            return true;

        if (typeSymbol is LiteralTypeSymbol literal)
            return ContainsErrorType(literal.UnderlyingType, visited);

        if (typeSymbol is NullableTypeSymbol nullable)
            return ContainsErrorType(nullable.UnderlyingType, visited);

        switch (typeSymbol)
        {
            case IArrayTypeSymbol array:
                return ContainsErrorType(array.ElementType, visited);

            case RefTypeSymbol refType:
                return ContainsErrorType(refType.ElementType, visited);

            case IPointerTypeSymbol pointer:
                return ContainsErrorType(pointer.PointedAtType, visited);

            case IAddressTypeSymbol address:
                return ContainsErrorType(address.ReferencedType, visited);

            case INamedTypeSymbol named:
                if (!named.TypeArguments.IsDefaultOrEmpty)
                {
                    foreach (var argument in named.TypeArguments)
                    {
                        if (ContainsErrorType(argument, visited))
                            return true;
                    }
                }

                if (named.TypeKind == TypeKind.Delegate &&
                    named.GetDelegateInvokeMethod() is { } invoke)
                {
                    if (ContainsErrorType(invoke.ReturnType, visited))
                        return true;

                    foreach (var parameter in invoke.Parameters)
                    {
                        if (ContainsErrorType(parameter.Type, visited))
                            return true;
                    }
                }

                return false;

            case ITypeParameterSymbol typeParameter:
                if (!typeParameter.ConstraintTypes.IsDefaultOrEmpty)
                {
                    foreach (var constraint in typeParameter.ConstraintTypes)
                    {
                        if (ContainsErrorType(constraint, visited))
                            return true;
                    }
                }

                return false;
        }

        return false;
    }

    public static ITypeSymbol? GetNullableUnderlyingType(this ITypeSymbol typeSymbol)
    {
        if (typeSymbol is null)
            throw new ArgumentNullException(nameof(typeSymbol));

        return typeSymbol is NullableTypeSymbol nullable
            ? nullable.UnderlyingType
            : null;
    }

    public static string ToDisplayStringForTypeMismatchDiagnostic(this ITypeSymbol typeSymbol, SymbolDisplayFormat format)
    {
        return FormatType(typeSymbol, format);
    }

    public static string ToDisplayString(this ISymbol symbol, SymbolDisplayFormat? format = default!)
    {
        format ??= SymbolDisplayFormat.RavenSignatureFormat;

        if (symbol is IAliasSymbol { Kind: SymbolKind.Type } alias &&
            format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.ExpandAliases))
        {
            symbol = alias.UnderlyingSymbol;
        }

        // Single entry point for top-level types – we can prepend type keyword here
        if (symbol is ITypeSymbol typeSymbol)
        {
            // TryFormatFunctionType must be checked first: System.Func/Action and synthesized
            // delegates always render as arrow-notation (e.g. T -> bool), even when
            // DelegateStyle is NameAndSignature.
            var isNamedDelegateDeclarationDisplay =
                typeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Delegate } namedDelegateType &&
                format.DelegateStyle == SymbolDisplayDelegateStyle.NameAndSignature &&
                !TryFormatFunctionType(typeSymbol, format, out _);
            string text;
            if (isNamedDelegateDeclarationDisplay)
            {
                text = FormatNamedDelegateDeclaration((INamedTypeSymbol)typeSymbol, format);
            }
            else if (typeSymbol is IUnionCaseTypeSymbol { IsUnionCase: true } &&
                     format.KindOptions.HasFlag(SymbolDisplayKindOptions.IncludeMemberKeyword))
            {
                // A union case declaration inherits its generic parameters from the
                // containing union. Display those parameters in type-use positions,
                // but do not repeat them on the case declaration itself.
                text = FormatType(
                    typeSymbol,
                    format.WithGenericsOptions(
                        format.GenericsOptions & ~SymbolDisplayGenericsOptions.IncludeTypeParameters));
            }
            else
            {
                text = FormatType(typeSymbol, format);
            }

            if (typeSymbol is IUnionCaseTypeSymbol { IsUnionCase: true } unionCase &&
                format.KindOptions.HasFlag(SymbolDisplayKindOptions.IncludeMemberKeyword) &&
                format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeParameters))
            {
                text += "(" + string.Join(", ", unionCase.ConstructorParameters.Select(parameter => FormatParameter(parameter, format))) + ")";
            }

            if (format.KindOptions.HasFlag(SymbolDisplayKindOptions.IncludeMemberKeyword) &&
                symbol is INamedTypeSymbol namedMemberType &&
                GetMemberKindKeyword(namedMemberType) is { Length: > 0 } memberKeyword)
            {
                text = memberKeyword + " " + text;
            }

            if (typeSymbol is INamedTypeSymbol namedUnionType &&
                namedUnionType.IsUnion &&
                namedUnionType is IUnionSymbol unionSymbol &&
                format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.IncludeUnionMemberTypes) &&
                !unionSymbol.MemberTypes.IsDefaultOrEmpty)
            {
                var memberFormat = format.WithKindOptions(format.KindOptions & ~SymbolDisplayKindOptions.IncludeTypeKeyword);
                var memberTypes = new List<ITypeSymbol>();
                var includeNullMember = false;

                foreach (var member in unionSymbol.MemberTypes)
                {
                    var nonNullMember = UnionContentNullability.GetNonNullContentType(member, out var memberMayBeNull);
                    includeNullMember |= memberMayBeNull;

                    if (nonNullMember.TypeKind == TypeKind.Null)
                        continue;

                    if (memberTypes.Any(existing => SymbolEqualityComparer.Default.Equals(existing, nonNullMember)))
                        continue;

                    memberTypes.Add(nonNullMember);
                }

                var memberDisplayParts = memberTypes
                    .Select(member => FormatType(member, memberFormat))
                    .ToList();

                if (includeNullMember || unionSymbol.ContentMayBeNull)
                    memberDisplayParts.Add("null");

                var members = string.Join(" | ", memberDisplayParts);
                text += $"({members})";
            }

            if (format.KindOptions.HasFlag(SymbolDisplayKindOptions.IncludeTypeKeyword) &&
                            symbol is INamedTypeSymbol namedTypeSymbol &&
                            !namedTypeSymbol.IsUnionCase &&
                            !isNamedDelegateDeclarationDisplay)
            {
                var modifierPrefix = GetTypeDeclarationModifierPrefix(namedTypeSymbol);
                var keyword = GetTypeKeyword(namedTypeSymbol);
                if (!string.IsNullOrEmpty(keyword))
                {
                    text = string.IsNullOrEmpty(modifierPrefix)
                        ? keyword + " " + text
                        : modifierPrefix + " " + keyword + " " + text;
                }
                else if (!string.IsNullOrEmpty(modifierPrefix))
                {
                    text = modifierPrefix + " " + text;
                }
            }

            if (format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.EscapeIdentifiers))
            {
                return EscapeIdentifier(text);
            }

            return text;
        }

        var result = new StringBuilder();

        if (symbol is ILocalSymbol localSymbol)
        {
            if (format.LocalOptions.HasFlag(SymbolDisplayLocalOptions.IncludeBinding))
            {
                if (localSymbol.IsConst)
                {
                    result.Append("const ");
                }
                else
                {
                    if (localSymbol.IsMutable)
                    {
                        result.Append("var ");
                    }
                    else
                    {
                        result.Append("val ");
                    }
                }
            }

            var display = FormatNamedSymbol(
                localSymbol.Name,
                localSymbol.Type,
                format.LocalOptions.HasFlag(SymbolDisplayLocalOptions.IncludeType),
                format,
                useNameOption: true);
            result.Append(display);
            // Append constant value for const locals
            if (localSymbol.IsConst)
            {
                result.Append(" = ");
                result.Append(FormatConstant(localSymbol.ConstantValue, localSymbol.Type, format));
            }

            var text = result.ToString();
            if (format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.EscapeIdentifiers))
            {
                return EscapeIdentifier(text);
            }

            return text;
        }

        if (symbol is ILabelSymbol labelSymbol)
        {
            result.Append(EscapeIdentifierIfNeeded(labelSymbol.Name, format));

            var text = result.ToString();
            if (format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.EscapeIdentifiers))
            {
                return EscapeIdentifier(text);
            }

            return text;
        }

        if (symbol is IParameterSymbol parameterSymbol)
        {
            var display = FormatParameter(parameterSymbol, format);
            result.Append(display);

            var text = result.ToString();
            if (format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.EscapeIdentifiers))
            {
                return EscapeIdentifier(text);
            }

            return text;
        }

        if (symbol is IMethodSymbol methodSymbol2
            && !methodSymbol2.ExplicitInterfaceImplementations.IsEmpty)
        {
            if (format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeExplicitInterface))
            {
                var t = methodSymbol2.ExplicitInterfaceImplementations[0];
                var type = GetFullType(t, format);
                if (!string.IsNullOrEmpty(type))
                {
                    result.Append(type).Append('.');
                }
            }
        }
        else if (symbol is IPropertySymbol propertySymbol2
            && !propertySymbol2.ExplicitInterfaceImplementations.IsEmpty)
        {
            if (format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeExplicitInterface))
            {
                var t = propertySymbol2.ExplicitInterfaceImplementations[0];
                var type = GetFullType(t, format);
                if (!string.IsNullOrEmpty(type))
                {
                    result.Append(type).Append('.');
                }
            }
        }
        else
        {
            // Type qualification for non-type symbols (namespaces/types used as containers)
            if (format.TypeQualificationStyle == SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces)
            {
                if (symbol.ContainingNamespace is { } containingNamespace && !containingNamespace.IsGlobalNamespace)
                {
                    var ns = GetFullNamespace(symbol, format);
                    if (!string.IsNullOrEmpty(ns))
                    {
                        result.Append(ns).Append('.');
                    }
                }

                if (symbol.ContainingType is not null)
                {
                    var type = GetFullType(symbol, format);
                    if (!string.IsNullOrEmpty(type))
                    {
                        result.Append(type).Append('.');
                    }
                }
            }
            else if (format.TypeQualificationStyle == SymbolDisplayTypeQualificationStyle.NameAndContainingTypes)
            {
                if (symbol.ContainingType is not null)
                {
                    var type = GetFullType(symbol, format);
                    if (!string.IsNullOrEmpty(type))
                    {
                        result.Append(type).Append('.');
                    }
                }
            }
        }

        // Symbol name
        var symbolName = GetDisplayName(symbol);
        var shouldEscapeSymbolName = symbol switch
        {
            IMethodSymbol
            {
                IsConstructor: true,
                MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor
            } => false,
            IPropertySymbol { IsIndexer: true } => false,
            IMethodSymbol { Name: "Invoke" } => false,
            _ => true
        };

        result.Append(shouldEscapeSymbolName
            ? EscapeIdentifierIfNeeded(symbolName, format)
            : symbolName);

        if (symbol is INamedTypeSymbol typeSymbol2)
        {
            if (format.GenericsOptions.HasFlag(SymbolDisplayGenericsOptions.IncludeTypeParameters) &&
                typeSymbol2.TypeParameters is { IsDefaultOrEmpty: false })
            {
                IEnumerable<string> arguments;

                if (!typeSymbol2.TypeArguments.IsDefaultOrEmpty &&
                    typeSymbol2.TypeArguments.Length == typeSymbol2.TypeParameters.Length)
                {
                    arguments = typeSymbol2.TypeArguments
                        .Select(arg => FormatType(arg, format));
                }
                else
                {
                    arguments = typeSymbol2.TypeParameters
                        .Select(p => EscapeIdentifierIfNeeded(p.Name, format));
                }

                result.Append('<');
                result.Append(string.Join(", ", arguments));
                result.Append('>');
            }

        }

        if (symbol is IMacroDeclarationSymbol macroSymbol)
        {
            if (format.GenericsOptions.HasFlag(SymbolDisplayGenericsOptions.IncludeTypeParameters) &&
                !macroSymbol.TypeParameters.IsDefaultOrEmpty)
            {
                var arguments = !macroSymbol.TypeArguments.IsDefaultOrEmpty &&
                    macroSymbol.TypeArguments.Length == macroSymbol.TypeParameters.Length
                        ? macroSymbol.TypeArguments.Select(argument => FormatType(argument, format))
                        : macroSymbol.TypeParameters.Select(parameter =>
                            EscapeIdentifierIfNeeded(parameter.Name, format));
                result.Append('<');
                result.Append(string.Join(", ", arguments));
                result.Append('>');
            }

            if (format.DelegateStyle == SymbolDisplayDelegateStyle.NameAndSignature ||
                format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeParameters))
            {
                result.Append('(');
                result.Append(string.Join(
                    ", ",
                    macroSymbol.Parameters.Select(parameter => FormatParameter(parameter, format))));
                result.Append(')');
            }

            if (macroSymbol.MacroKind == MacroKind.AttachedDeclaration)
            {
                result.Append(" on ");
                if (!string.Equals(macroSymbol.TargetName, "target", StringComparison.Ordinal))
                {
                    result.Append(EscapeIdentifierIfNeeded(macroSymbol.TargetName ?? "target", format));
                    result.Append(": ");
                }
                result.Append(macroSymbol.Targets);
            }

            if (format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeType))
            {
                var returnDisplay = FormatType(macroSymbol.ReturnType, format);
                if (!string.IsNullOrEmpty(returnDisplay))
                {
                    result.Append(" -> ");
                    result.Append(returnDisplay);
                }
            }
        }
        else if (symbol is IMethodSymbol methodSymbol)
        {
            // Prepend "func" for lambda methods
            if (methodSymbol.IsLambda)
            {
                result.Insert(0, "func ");
            }
            if (format.GenericsOptions.HasFlag(SymbolDisplayGenericsOptions.IncludeTypeParameters)
                && (!methodSymbol.TypeParameters.IsDefaultOrEmpty || !methodSymbol.TypeArguments.IsDefaultOrEmpty))
            {

                IEnumerable<string> arguments;

                if (!methodSymbol.TypeArguments.IsDefaultOrEmpty &&
                    methodSymbol.TypeArguments.Length == methodSymbol.TypeParameters.Length)
                {
                    arguments = methodSymbol.TypeArguments.Select(a => FormatType(a, format));
                }
                else
                {
                    arguments = methodSymbol.TypeParameters
                        .Select(p => EscapeIdentifierIfNeeded(p.Name, format));
                }

                result.Append('<');
                result.Append(string.Join(", ", arguments));
                result.Append('>');
            }

            if (format.DelegateStyle == SymbolDisplayDelegateStyle.NameAndSignature ||
                format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeParameters))
            {
                result.Append('(');
                result.Append(string.Join(", ", methodSymbol.Parameters.Select(p => FormatParameter(p, format))));
                result.Append(')');
            }

            if (format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeType))
            {
                var returnType = methodSymbol.ReturnType;
                if (returnType is not null)
                {
                    var returnDisplay = FormatType(returnType, format);
                    if (!string.IsNullOrEmpty(returnDisplay))
                    {
                        result.Append(" -> ");
                        result.Append(returnDisplay);
                    }
                }
            }
        }
        else if (symbol is IPropertySymbol propertySymbol)
        {
            if (propertySymbol.IsIndexer &&
                (format.DelegateStyle == SymbolDisplayDelegateStyle.NameAndSignature ||
                 format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeParameters)))
            {
                // Prefer property parameters if your model has them; fallback to accessor signature.
                var parameters =
                    propertySymbol.Parameters.IsDefaultOrEmpty
                        ? propertySymbol.GetMethod?.Parameters ?? default
                        : propertySymbol.Parameters;

                result.Append('[');
                result.Append(string.Join(", ", parameters.Select(p => FormatParameter(p, format))));
                result.Append(']');
            }

            if (format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeType))
            {
                var typeDisplay = FormatType(propertySymbol.Type, format);
                result.Append(": ");
                result.Append(typeDisplay);
            }

            if (format.PropertyStyle == SymbolDisplayPropertyStyle.ShowReadWriteDescriptor)
            {
                var accessorDisplay = FormatPropertyAccessors(propertySymbol, format);
                if (!string.IsNullOrEmpty(accessorDisplay))
                {
                    result.Append(' ');
                    result.Append(accessorDisplay);
                }
            }
            else if (propertySymbol.SetMethod is { MethodKind: MethodKind.InitOnly })
            {
                result.Append(" { init; }");
            }
        }
        else if (symbol is IFieldSymbol fieldSymbol)
        {
            if (format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeType))
            {
                var typeDisplay = FormatType(fieldSymbol.Type, format);
                result.Append(": ");
                result.Append(typeDisplay);
            }
            // Append constant value for const fields
            if (fieldSymbol.IsConst)
            {
                result.Append(" = ");
                result.Append(FormatConstant(
                    fieldSymbol.GetConstantValue(),
                    fieldSymbol.Type,
                    format,
                    formatEnumMember: fieldSymbol.ContainingType?.TypeKind != TypeKind.Enum));
            }
        }
        else if (symbol is IEventSymbol eventSymbol)
        {
            if (format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeType))
            {
                var typeDisplay = FormatType(eventSymbol.Type, format);
                result.Append(": ");
                result.Append(typeDisplay);
            }
        }

        // Prefix: accessibility + modifiers + kind keyword (top-level only)
        string? accessibilityPrefix = null;
        if (format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeAccessibility) &&
            symbol.DeclaredAccessibility is not Accessibility.NotApplicable)
        {
            accessibilityPrefix = ShouldDisplayAccessibility(symbol)
                ? AccessibilityUtilities.GetDisplayText(symbol.DeclaredAccessibility)
                : null;
        }

        string? modifiersPrefix = null;
        if (format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeModifiers))
        {
            modifiersPrefix = GetMemberModifiers(symbol);
        }

        string? kindPrefix = null;

        // namespace keyword
        if (format.KindOptions.HasFlag(SymbolDisplayKindOptions.IncludeNamespaceKeyword) &&
            symbol is INamespaceSymbol namespaceSymbol &&
            !namespaceSymbol.IsGlobalNamespace)
        {
            kindPrefix = "namespace";
        }
        // member keyword (currently disabled; GetMemberKindKeyword returns null)
        else if (format.KindOptions.HasFlag(SymbolDisplayKindOptions.IncludeMemberKeyword))
        {
            kindPrefix = GetMemberKindKeyword(symbol);
        }

        if (!string.IsNullOrEmpty(accessibilityPrefix) ||
            !string.IsNullOrEmpty(modifiersPrefix) ||
            !string.IsNullOrEmpty(kindPrefix))
        {
            var prefixBuilder = new StringBuilder();

            if (!string.IsNullOrEmpty(accessibilityPrefix))
            {
                prefixBuilder.Append(accessibilityPrefix);
            }

            if (!string.IsNullOrEmpty(modifiersPrefix))
            {
                if (prefixBuilder.Length > 0)
                    prefixBuilder.Append(' ');

                prefixBuilder.Append(modifiersPrefix);
            }

            if (!string.IsNullOrEmpty(kindPrefix))
            {
                if (prefixBuilder.Length > 0)
                    prefixBuilder.Append(' ');

                prefixBuilder.Append(kindPrefix);
            }

            if (prefixBuilder.Length > 0)
            {
                prefixBuilder.Append(' ');
                result.Insert(0, prefixBuilder.ToString());
            }
        }

        var finalText = result.ToString();
        if (format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.EscapeIdentifiers))
        {
            return EscapeIdentifier(finalText);
        }

        return finalText;
    }

    // =========================
    //  Type formatting helpers
    // =========================

    private static string GetDisplayName(ISymbol symbol) => symbol switch
    {
        // Constructors are rendered as `init`
        IMethodSymbol { IsConstructor: true, MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }
            => "init",

        // User-defined operators: `func +`, `func ==`, etc.
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } op
            => "func " + GetOperatorToken(op),

        // User-defined conversions: `func implicit`, `func explicit`
        IMethodSymbol { MethodKind: MethodKind.Conversion } conv
            => GetConversionDisplayName(conv),

        // Indexers are rendered as `self`
        IPropertySymbol { IsIndexer: true }
            => "self",

        // Callable objects / delegates
        IMethodSymbol { Name: "Invoke" }
            => "self",

        _ => symbol.Name
    };

    private static string GetConversionDisplayName(IMethodSymbol method)
    {
        // Roslyn naming convention for conversions:
        // op_Implicit / op_Explicit
        return method.Name switch
        {
            "op_Implicit" => "func implicit",
            "op_Explicit" => "func explicit",
            _ => "func"
        };
    }

    private static string GetOperatorToken(IMethodSymbol method)
    {
        // Roslyn naming convention for operators (examples):
        // op_Addition, op_Equality, op_UnaryPlus, op_LogicalNot, ...
        // Also, newer checked operators may appear as op_CheckedAddition, etc.

        var name = method.Name;

        var checkedPrefix = false;
        const string checkedOpPrefix = "op_Checked";
        if (name.StartsWith(checkedOpPrefix, StringComparison.Ordinal))
        {
            checkedPrefix = true;
            name = "op_" + name.Substring(checkedOpPrefix.Length);
        }

        var token = name switch
        {
            "op_Addition" => "+",
            "op_Subtraction" => "-",
            "op_Multiply" => "*",
            "op_Division" => "/",
            "op_Modulus" => "%",

            "op_BitwiseAnd" => "&",
            "op_BitwiseOr" => "|",
            "op_ExclusiveOr" => "^",

            "op_LeftShift" => "<<",
            "op_RightShift" => ">>",

            "op_LogicalNot" => "!",
            "op_OnesComplement" => "~",
            "op_UnaryPlus" => "+",
            "op_UnaryNegation" => "-",

            "op_Increment" => "++",
            "op_Decrement" => "--",

            "op_Equality" => "==",
            "op_Inequality" => "!=",
            "op_LessThan" => "<",
            "op_LessThanOrEqual" => "<=",
            "op_GreaterThan" => ">",
            "op_GreaterThanOrEqual" => ">=",

            "op_True" => "true",
            "op_False" => "false",

            _ => name.StartsWith("op_", StringComparison.Ordinal) ? name.Substring(3) : name
        };

        return checkedPrefix ? $"checked {token}" : token;
    }

    private static string FormatType(ITypeSymbol typeSymbol, SymbolDisplayFormat format)
    {
        if (typeSymbol is IAliasSymbol { Kind: SymbolKind.Type } alias &&
            format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.ExpandAliases))
        {
            typeSymbol = (ITypeSymbol)alias.UnderlyingSymbol;
        }

        // Format literal pseudo-types using their constant value
        if (typeSymbol is LiteralTypeSymbol literal)
            return literal.Name;

        // Nullable<T> => T?
        if (typeSymbol is NullableTypeSymbol nullable)
        {
            var underlying = nullable.UnderlyingType;

            // Nullable of function type => (A -> B)?
            if (underlying is INamedTypeSymbol { TypeKind: TypeKind.Delegate } &&
                TryFormatFunctionType(underlying, format, out var funcDisplay))
            {
                return $"({funcDisplay})?";
            }

            var underlyingDisplay = FormatType(underlying, format);
            if (IsStandardUnionType(underlying))
                underlyingDisplay = $"({underlyingDisplay})";

            return underlyingDisplay + "?";
        }

        // Delegate function sugar (synthesized + System.Func/Action)
        if (TryFormatFunctionType(typeSymbol, format, out var functionDisplay))
            return functionDisplay;

        // Type parameter: just the (escaped) name
        if (typeSymbol is ITypeParameterSymbol typeParameter)
            return EscapeIdentifierIfNeeded(typeParameter.Name, format);

        // Arrays
        if (typeSymbol is IArrayTypeSymbol arrayType)
        {
            var elementDisplay = FormatType(arrayType.ElementType, format);

            if (arrayType.Rank == 1)
                return arrayType.FixedLength is int fixedLength
                    ? elementDisplay + $"[{fixedLength}]"
                    : elementDisplay + "[]";

            var elementType = arrayType.ElementType;

            // Array of a function or standard union type requires grouping.
            if (elementType is INamedTypeSymbol { TypeKind: TypeKind.Delegate } ||
                IsStandardUnionType(elementType))
            {
                elementDisplay = $"({elementDisplay})";
            }

            return elementDisplay + "[" + new string(',', arrayType.Rank - 1) + "]";
        }

        // Pointers
        if (typeSymbol is IPointerTypeSymbol pointerType)
        {
            var pointedAt = FormatType(pointerType.PointedAtType, format);
            return "*" + pointedAt;
        }

        // ByRef
        if (typeSymbol is RefTypeSymbol refTypeType)
        {
            var addressTo = FormatType(refTypeType.ElementType, format);
            return "&" + addressTo;
        }

        // Tuples
        if (typeSymbol is ITupleTypeSymbol tupleType)
        {
            var elementTypes = tupleType.TupleElements
                .Select((e, index) =>
                {
                    var t = FormatType(e.Type, format);

                    if (!format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.IncludeTupleElementNames))
                        return t;

                    if (string.IsNullOrWhiteSpace(e.Name) || IsImplicitTupleElementName(e.Name, index))
                        return t;

                    var escapedElementName = EscapeIdentifierIfNeeded(e.Name, format);
                    return $"{escapedElementName}: {t}";
                });

            return "(" + string.Join(", ", elementTypes) + ")";
        }

        static bool IsImplicitTupleElementName(string name, int elementIndex)
        {
            if (!name.StartsWith("Item", StringComparison.Ordinal))
                return false;

            var suffix = name["Item".Length..];
            return int.TryParse(suffix, out var ordinal) && ordinal == elementIndex + 1;
        }

        if (IsStandardUnionType(typeSymbol) &&
            typeSymbol is INamedTypeSymbol standardUnion &&
            !standardUnion.TypeArguments.IsDefaultOrEmpty)
        {
            return string.Join(" | ", standardUnion.TypeArguments.Select(t => FormatType(t, format)));
        }

        // Special types => keywords
        if (format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.UseSpecialTypes))
        {
            if (typeSymbol.SpecialType == SpecialType.System_Unit)
                return "()";

            var fullName = typeSymbol.ToFullyQualifiedMetadataName();
            if (fullName is not null && s_specialTypeNames.TryGetValue(fullName, out var keyword))
                return keyword;
        }

        // Named types and everything else
        if (typeSymbol is INamedTypeSymbol namedType)
            return FormatNamedType(namedType, format);

        // Fallback: just the name
        return EscapeIdentifierIfNeeded(typeSymbol.Name, format);
    }

    private static bool IsStandardUnionType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol namedType)
            return false;

        var definition = (namedType.OriginalDefinition as INamedTypeSymbol) ??
                         (namedType.ConstructedFrom as INamedTypeSymbol) ??
                         namedType;

        return definition.Arity is >= 2 and <= 5 &&
               string.Equals(definition.Name, "Union", StringComparison.Ordinal) &&
               string.Equals(definition.ContainingNamespace?.ToDisplayString(), "System", StringComparison.Ordinal);
    }

    private static string FormatSimpleNamedType(INamedTypeSymbol typeSymbol, SymbolDisplayFormat format)
    {
        var sb = new StringBuilder();

        // Simple name
        sb.Append(EscapeIdentifierIfNeeded(typeSymbol.Name, format));

        // Generic arguments / parameters
        if (format.GenericsOptions.HasFlag(SymbolDisplayGenericsOptions.IncludeTypeParameters) &&
            typeSymbol.Arity > 0)
        {
            var declaredArity = typeSymbol.Arity;
            IEnumerable<string> arguments;

            if (typeSymbol is ConstructedNamedTypeSymbol constructedNamed)
            {
                var explicitArguments = constructedNamed.GetExplicitTypeArgumentsForInference();
                if (!explicitArguments.IsDefaultOrEmpty &&
                    explicitArguments.Length == typeSymbol.TypeParameters.Length)
                {
                    var offset = explicitArguments.Length - declaredArity;
                    arguments = explicitArguments
                        .Skip(offset)
                        .Take(declaredArity)
                        .Select(a => FormatType(a, format));
                }
                else
                {
                    var declaringType = typeSymbol.OriginalDefinition;
                    arguments = typeSymbol.TypeParameters
                        .Where(p => SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, declaringType))
                        .Select(p => EscapeIdentifierIfNeeded(p.Name, format));
                }
            }
            else if (!typeSymbol.TypeArguments.IsDefaultOrEmpty &&
                typeSymbol.TypeArguments.Length == typeSymbol.TypeParameters.Length)
            {
                // Constructed type: use actual type arguments
                var offset = typeSymbol.TypeArguments.Length - declaredArity;
                arguments = typeSymbol.TypeArguments
                    .Skip(offset)
                    .Take(declaredArity)
                    .Select(a => FormatType(a, format));
            }
            else
            {
                // Unconstructed type: fall back to parameter names declared on this type
                var declaringType = typeSymbol.OriginalDefinition;

                arguments = typeSymbol.TypeParameters
                    .Where(p => SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, declaringType))
                    .Select(p => EscapeIdentifierIfNeeded(p.Name, format));
            }

            sb.Append('<');
            sb.Append(string.Join(", ", arguments));
            sb.Append('>');
        }

        return sb.ToString();
    }

    private static string FormatNamedType(INamedTypeSymbol typeSymbol, SymbolDisplayFormat format)
    {
        var sb = new StringBuilder();

        // Qualification
        if (format.TypeQualificationStyle == SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces)
        {
            if (typeSymbol.ContainingNamespace is { IsGlobalNamespace: false })
            {
                var ns = GetFullNamespace(typeSymbol, format);
                if (!string.IsNullOrEmpty(ns))
                {
                    sb.Append(ns).Append('.');
                }
            }

            if (typeSymbol.ContainingType is not null)
            {
                var containingTypes = GetFullType(typeSymbol, format);
                if (!string.IsNullOrEmpty(containingTypes))
                {
                    sb.Append(containingTypes).Append('.');
                }
            }
        }
        else if (format.TypeQualificationStyle == SymbolDisplayTypeQualificationStyle.NameAndContainingTypes)
        {
            if (typeSymbol.ContainingType is not null)
            {
                var containingTypes = GetFullType(typeSymbol, format);
                if (!string.IsNullOrEmpty(containingTypes))
                {
                    sb.Append(containingTypes).Append('.');
                }
            }
        }

        // Simple name + generic params/args
        sb.Append(FormatSimpleNamedType(typeSymbol, format));

        // NOTE: no KindOptions here – nested type usage must not get type keyword.

        return sb.ToString();
    }

    private static string FormatNamedDelegateDeclaration(INamedTypeSymbol delegateType, SymbolDisplayFormat format)
    {
        var displayName = FormatNamedType(delegateType, format);
        var invoke = delegateType.GetDelegateInvokeMethod();
        if (invoke is null)
            return $"delegate {displayName}";

        var parameterFormat = format.WithParameterOptions(
            format.ParameterOptions | SymbolDisplayParameterOptions.IncludeParamsRefOut);
        var parameters = string.Join(", ", invoke.Parameters.Select(p => FormatParameter(p, parameterFormat)));
        var returnType = FormatType(invoke.ReturnType, format);
        return $"delegate {displayName}({parameters}) -> {returnType}";
    }

    // =========================
    //  Function type helpers
    // =========================

    private static bool TryFormatFunctionType(ITypeSymbol typeSymbol, SymbolDisplayFormat format, out string display)
    {
        if (typeSymbol is INamedTypeSymbol named && named.TypeKind == TypeKind.Delegate)
        {
            if (named is SynthesizedDelegateTypeSymbol synthesized)
            {
                display = FormatFunctionSignature(
                    synthesized.ParameterTypes,
                    synthesized.ParameterRefKinds,
                    synthesized.ReturnType,
                    format, true);
                return true;
            }

            if (IsSystemFuncOrAction(named))
            {
                if (TryFormatSystemFuncOrActionFromTypeArguments(named, format, out display))
                    return true;

                var invoke = named.GetDelegateInvokeMethod();
                if (invoke is null)
                {
                    display = null!;
                    return false;
                }

                var parameterTypes = invoke.Parameters.Select(p => p.Type).ToImmutableArray();
                var refKinds = invoke.Parameters.Select(p => p.RefKind).ToImmutableArray();

                display = FormatFunctionSignature(parameterTypes, refKinds, invoke.ReturnType, format);
                return true;
            }
        }

        display = null!;
        return false;
    }

    private static bool TryFormatSystemFuncOrActionFromTypeArguments(
        INamedTypeSymbol named,
        SymbolDisplayFormat format,
        out string display)
    {
        display = null!;

        if (named.Name == "Action")
        {
            var parameterDisplays = named.TypeArguments.IsDefaultOrEmpty
                ? []
                : named.TypeArguments.Select(type => FormatFunctionParameter(type, RefKind.None, format)).ToArray();
            display = FormatFunctionSignatureText(parameterDisplays, "()", isLambda: false);
            return true;
        }

        if (named.Name == "Func" &&
            !named.TypeArguments.IsDefaultOrEmpty)
        {
            var parameterDisplays = named.TypeArguments
                .Take(named.TypeArguments.Length - 1)
                .Select(type => FormatFunctionParameter(type, RefKind.None, format))
                .ToArray();
            var returnDisplay = FormatType(named.TypeArguments[^1], format);
            display = FormatFunctionSignatureText(parameterDisplays, returnDisplay, isLambda: false);
            return true;
        }

        return false;
    }

    private static string FormatFunctionSignature(
        ImmutableArray<ITypeSymbol> parameterTypes,
        ImmutableArray<RefKind> refKinds,
        ITypeSymbol returnType,
        SymbolDisplayFormat format,
        bool isLambda = false)
    {
        var parameterDisplays = ImmutableArray.CreateBuilder<string>(parameterTypes.IsDefault ? 0 : parameterTypes.Length);

        if (!parameterTypes.IsDefaultOrEmpty)
        {
            for (var i = 0; i < parameterTypes.Length; i++)
            {
                var parameterType = parameterTypes[i];
                var refKind = !refKinds.IsDefaultOrEmpty && i < refKinds.Length
                    ? refKinds[i]
                    : RefKind.None;

                parameterDisplays.Add(FormatFunctionParameter(parameterType, refKind, format));
            }
        }

        var shouldParenthesizeSingleParameter = parameterTypes.Length == 1 &&
                                                parameterTypes[0] is not ITupleTypeSymbol and not UnitTypeSymbol;

        var returnDisplay = FormatType(returnType, format);

        if (returnType.TypeKind == TypeKind.Delegate)
        {
            returnDisplay = $"({returnDisplay})";
        }

        return FormatFunctionSignatureText(
            parameterDisplays.ToArray(),
            returnDisplay,
            isLambda,
            shouldParenthesizeSingleParameter);
    }

    private static string FormatFunctionSignatureText(
        IReadOnlyList<string> parameterDisplays,
        string returnDisplay,
        bool isLambda,
        bool shouldParenthesizeSingleParameter = true)
    {
        string parameterText = parameterDisplays.Count switch
        {
            0 => "()",
            1 when shouldParenthesizeSingleParameter => isLambda ? $"({parameterDisplays[0]})" : parameterDisplays[0],
            _ => $"({string.Join(", ", parameterDisplays)})"
        };

        return $"{parameterText} -> {returnDisplay}";
    }

    private static string FormatFunctionParameter(ITypeSymbol type, RefKind refKind, SymbolDisplayFormat format)
    {
        var typeDisplay = FormatType(type, format);

        return refKind switch
        {
            RefKind.In => $"in {typeDisplay}",
            RefKind.Ref => $"ref {typeDisplay}",
            RefKind.Out => $"out {typeDisplay}",
            RefKind.RefReadOnly => $"ref readonly {typeDisplay}",
            _ => typeDisplay
        };
    }

    private static bool IsSystemFuncOrAction(INamedTypeSymbol named)
    {
        if (named.Name is not ("Func" or "Action"))
            return false;

        var ns = named.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
            return false;

        return ns.Name == "System" && (ns.ContainingNamespace is null || ns.ContainingNamespace.IsGlobalNamespace);
    }

    // =========================
    //  Misc helpers
    // =========================

    private static SymbolDisplayFormat WithoutTypeAccessibility(SymbolDisplayFormat format)
    {
        return format.WithMemberOptions(format.MemberOptions & ~SymbolDisplayMemberOptions.IncludeAccessibility);
    }

    private static string EscapeIdentifierIfNeeded(string identifier, SymbolDisplayFormat format)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return identifier;
        }

        var result = identifier;

        if (format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers) &&
            SyntaxFacts.TryParseKeyword(identifier, out _))
        {
            result = "@" + result;
        }

        if (format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.EscapeIdentifiers))
        {
            result = EscapeIdentifier(result);
        }

        return result;
    }

    private static string FormatParameter(IParameterSymbol parameter, SymbolDisplayFormat format)
    {
        var includeType = format.ParameterOptions.HasFlag(SymbolDisplayParameterOptions.IncludeType);
        var includeName = format.ParameterOptions.HasFlag(SymbolDisplayParameterOptions.IncludeName) &&
            (!parameter.HasImplicitName || format.ParameterOptions.HasFlag(SymbolDisplayParameterOptions.IncludeGeneratedNames));
        var includeRefKind = format.ParameterOptions.HasFlag(SymbolDisplayParameterOptions.IncludeParamsRefOut);

        var builder = new StringBuilder();

        // Core "name: type" (or just type / just name depending on options)
        var parameterType = parameter.Type;

        var core = FormatNamedSymbol(
            parameter.Name,
            parameterType,
            includeType,
            format,
            includeName,
            escapeName: !IsSelfReceiverParameter(parameter));

        if (parameter.IsVarParams)
            core = $"params {core}";

        if (includeRefKind)
        {
            var modifiers = GetParameterModifiers(parameter);
            if (!string.IsNullOrEmpty(modifiers))
            {
                builder.Append(modifiers);
                builder.Append(' ');
            }
        }

        // Append the core parameter text
        builder.Append(core);

        if (format.ParameterOptions.HasFlag(SymbolDisplayParameterOptions.IncludeDefaultValue) &&
            parameter.HasExplicitDefaultValue)
        {
            builder.Append(" = ");
            builder.Append(FormatParameterDefaultValue(parameter, format));
        }

        return builder.ToString();
    }

    private static string FormatParameterDefaultValue(IParameterSymbol parameter, SymbolDisplayFormat format)
    {
        if (parameter.ExplicitDefaultValue is OptionNoneParameterDefaultValue)
            return ".None";

        if (parameter is PEParameterSymbol { ExplicitDefaultValueIsTypeDefault: true } ||
            HasDefaultExpressionSyntax(parameter))
            return "default";

        return FormatConstant(parameter.ExplicitDefaultValue, parameter.Type, format);
    }

    private static bool HasDefaultExpressionSyntax(IParameterSymbol parameter)
    {
        foreach (var syntax in parameter.DeclaringSyntaxReferences.Select(static reference => reference.GetSyntax()))
        {
            if (syntax is ParameterSyntax { DefaultValue.Value: DefaultExpressionSyntax })
                return true;
        }

        return false;
    }

    private static string GetParameterModifiers(IParameterSymbol parameter)
    {
        return parameter.RefKind switch
        {
            RefKind.Out => "out",
            RefKind.Ref => "ref",
            RefKind.In => "in",
            RefKind.RefReadOnly => "ref readonly",
            RefKind.RefReadOnlyParameter => "ref readonly",
            _ => string.Empty
        };
    }

    private static string FormatConstant(
        object? value,
        ITypeSymbol type,
        SymbolDisplayFormat format,
        bool formatEnumMember = true)
    {
        if (type.IsValueType && value is null)
            return "default";

        if (value is null)
            return "null";

        if (formatEnumMember && TryFormatEnumConstant(value, type, format, out var enumConstant))
            return enumConstant;

        // Strings
        if (value is string s)
        {
            // Basic escaping for backslash and quote
            var escaped = s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
            return $"\"{escaped}\"";
        }

        // Chars
        if (value is char c)
        {
            var text = c.ToString()
                .Replace("\\", "\\\\")
                .Replace("'", "\\'");
            return $"'{text}'";
        }

        // Booleans
        if (value is bool b)
            return b ? "true" : "false";

        // Runtime enum values can reach metadata symbols as boxed Enum instances.
        if (value is Enum e)
        {
            if (formatEnumMember)
                return e.ToString();

            value = Convert.ChangeType(e, e.GetTypeCode(), CultureInfo.InvariantCulture);
        }

        // Numeric and other IFormattable values
        if (value is IFormattable f)
            return f.ToString(null, CultureInfo.InvariantCulture) ?? "0";

        // Fallback
        return value.ToString() ?? "null";
    }

    private static bool TryFormatEnumConstant(
        object value,
        ITypeSymbol type,
        SymbolDisplayFormat format,
        out string display)
    {
        display = string.Empty;

        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            return false;

        if (!TryConvertEnumConstantValue(value, out var numericValue))
            return false;

        foreach (var field in enumType.GetMembers().OfType<IFieldSymbol>())
        {
            if (!field.IsConst || field.ContainingType?.TypeKind != TypeKind.Enum)
                continue;

            var fieldValue = field.GetConstantValue();
            if (fieldValue is null ||
                !TryConvertEnumConstantValue(fieldValue, out var fieldNumericValue) ||
                fieldNumericValue != numericValue)
            {
                continue;
            }

            var memberDisplay = EscapeIdentifierIfNeeded(field.Name, format);
            display = format.MiscellaneousOptions.HasFlag(SymbolDisplayMiscellaneousOptions.UseTargetTypedMemberBinding)
                ? "." + memberDisplay
                : FormatType(enumType, format) + "." + memberDisplay;
            return true;
        }

        return false;
    }

    private static bool TryConvertEnumConstantValue(object value, out decimal numericValue)
    {
        try
        {
            if (value is Enum enumValue)
                value = Convert.ChangeType(enumValue, enumValue.GetTypeCode(), CultureInfo.InvariantCulture);

            numericValue = value switch
            {
                sbyte v => v,
                byte v => v,
                short v => v,
                ushort v => v,
                int v => v,
                uint v => v,
                long v => v,
                ulong v => v,
                _ => default
            };

            return value is sbyte or byte or short or ushort or int or uint or long or ulong;
        }
        catch (InvalidCastException)
        {
            numericValue = default;
            return false;
        }
        catch (OverflowException)
        {
            numericValue = default;
            return false;
        }
    }

    private static string FormatPropertyAccessors(IPropertySymbol propertySymbol, SymbolDisplayFormat format)
    {
        var accessors = new List<string>();

        var getter = FormatPropertyAccessor(propertySymbol.GetMethod, propertySymbol, format, "get");
        if (getter is not null)
            accessors.Add(getter);

        var setterKeyword = propertySymbol.SetMethod?.MethodKind == MethodKind.InitOnly ? "init" : "set";
        var setter = FormatPropertyAccessor(propertySymbol.SetMethod, propertySymbol, format, setterKeyword);
        if (setter is not null)
            accessors.Add(setter);

        if (accessors.Count == 0)
            return string.Empty;

        return "{ " + string.Join(" ", accessors) + " }";
    }

    private static string? FormatPropertyAccessor(
        IMethodSymbol? accessor,
        IPropertySymbol propertySymbol,
        SymbolDisplayFormat format,
        string keyword)
    {
        if (accessor is null)
            return null;

        var parts = new List<string>();

        var includeAccessibility = format.MemberOptions.HasFlag(SymbolDisplayMemberOptions.IncludeAccessibility);
        if (includeAccessibility &&
            accessor.DeclaredAccessibility != Accessibility.NotApplicable &&
            accessor.DeclaredAccessibility != propertySymbol.DeclaredAccessibility &&
            ShouldDisplayAccessibility(accessor))
        {
            parts.Add(AccessibilityUtilities.GetDisplayText(accessor.DeclaredAccessibility));
        }

        parts.Add(keyword);
        return string.Join(" ", parts) + ";";
    }

    private static string EscapeIdentifier(string identifier)
    {
        // Could HTML-escape here if needed
        return identifier;
    }

    private static bool ShouldDisplayAccessibility(ISymbol symbol)
    {
        if (symbol.DeclaredAccessibility is not Accessibility.Public)
            return true;

        return symbol switch
        {
            IMethodSymbol => false,
            IPropertySymbol => false,
            IFieldSymbol => false,
            IEventSymbol => false,
            _ => true
        };
    }

    private static string FormatNamedSymbol(
        string name,
        ITypeSymbol type,
        bool includeType,
        SymbolDisplayFormat format,
        bool useNameOption,
        bool escapeName = true)
    {
        var sb = new StringBuilder();

        if (useNameOption)
        {
            sb.Append(escapeName ? EscapeIdentifierIfNeeded(name, format) : name);

            if (includeType)
            {
                sb.Append(": ");
                var typeFormat = WithoutTypeAccessibility(format);
                var typeDisplay = FormatType(type, typeFormat);
                sb.Append(typeDisplay);
            }

            return sb.ToString();
        }

        if (includeType)
        {
            var typeFormat = WithoutTypeAccessibility(format);
            var typeDisplay = FormatType(type, typeFormat);
            sb.Append(typeDisplay);
        }

        return sb.ToString();
    }

    private static bool IsSelfReceiverParameter(IParameterSymbol parameter)
    {
        return string.Equals(parameter.Name, "self", StringComparison.Ordinal) &&
               parameter.ContainingSymbol is IMethodSymbol { IsExtensionMethod: true };
    }

    private static string GetFullNamespace(ISymbol symbol, SymbolDisplayFormat format)
    {
        var namespaces = new List<string>();
        var currentNamespace = symbol.ContainingNamespace;

        while (currentNamespace is not null && !currentNamespace.IsGlobalNamespace)
        {
            namespaces.Insert(0, EscapeIdentifierIfNeeded(currentNamespace.Name, format));
            currentNamespace = currentNamespace.ContainingNamespace;
        }

        return string.Join(".", namespaces);
    }

    private static string GetFullType(ISymbol symbol, SymbolDisplayFormat format)
    {
        var types = new List<string>();

        // Build containing type chain (outermost -> innermost containing type)
        var chain = new List<INamedTypeSymbol>();
        var current = symbol.ContainingType;
        while (current is not null)
        {
            if (current is INamedTypeSymbol ct)
                chain.Add(ct);

            current = current.ContainingType;
        }

        chain.Reverse();

        // Some symbol models may carry all generic arguments for nested types on the nested symbol.
        // Use that as a fallback slicing source when containing types don't expose their own arguments.
        ImmutableArray<ITypeSymbol> combinedArgs = default;
        if (symbol is INamedTypeSymbol named && !named.TypeArguments.IsDefaultOrEmpty)
            combinedArgs = named.TypeArguments;

        var totalArity = 0;
        foreach (var ct in chain)
            totalArity += ct.Arity;

        var canSliceCombined = !combinedArgs.IsDefaultOrEmpty && combinedArgs.Length >= totalArity;
        var argIndex = 0;

        foreach (var ct in chain)
        {
            // Prefer the containing type symbol's own type arguments when available.
            if (ct.Arity > 0 &&
                !ct.TypeArguments.IsDefaultOrEmpty &&
                ct.TypeArguments.Length == ct.TypeParameters.Length)
            {
                var args = ct.TypeArguments
                    .Skip(ct.TypeArguments.Length - ct.Arity)
                    .Take(ct.Arity)
                    .Select(a => FormatType(a, format));

                types.Add($"{EscapeIdentifierIfNeeded(ct.Name, format)}<{string.Join(", ", args)}>");
                argIndex += ct.Arity;
                continue;
            }

            // Fallback: if we can slice from combined args (outermost-to-innermost), do so.
            if (ct.Arity > 0 && canSliceCombined && combinedArgs.Length >= argIndex + ct.Arity)
            {
                var args = combinedArgs
                    .Skip(argIndex)
                    .Take(ct.Arity)
                    .Select(a => FormatType(a, format));

                types.Add($"{EscapeIdentifierIfNeeded(ct.Name, format)}<{string.Join(", ", args)}>");
                argIndex += ct.Arity;
                continue;
            }

            // Unconstructed type: use the type parameters declared on this type.
            if (ct.Arity > 0 && ct.TypeParameters is { IsDefaultOrEmpty: false })
            {
                var declaringType = ct.OriginalDefinition;
                var args = ct.TypeParameters
                    .Where(p => SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, declaringType))
                    .Select(p => EscapeIdentifierIfNeeded(p.Name, format));

                types.Add($"{EscapeIdentifierIfNeeded(ct.Name, format)}<{string.Join(", ", args)}>");
                // Do not advance argIndex here (we didn't consume combined args).
                continue;
            }

            // Non-generic containing type
            types.Add(EscapeIdentifierIfNeeded(ct.Name, format));
        }

        return string.Join(".", types);
    }

    private static string GetMemberModifiers(ISymbol symbol)
    {
        var parts = new List<string>();

        switch (symbol)
        {
            case IParameterSymbol @param:
                if (param.RefKind == RefKind.Out)
                {
                    parts.Add("out");
                }
                break;

            case IFieldSymbol field:
                if (IsFieldDeclaredExtern(field))
                    parts.Add("extern");

                if (field.IsConst)
                {
                    parts.Add("const");
                }
                else
                {
                    if (field.IsStatic)
                        parts.Add("static");

                    if (field.IsReadOnly)
                    {
                        parts.Add("readonly");
                    }
                }

                break;

            case IMethodSymbol method:
                // Local functions will also show these correctly.
                if (method.IsStatic)
                    parts.Add("static");

                if (method.IsAbstract)
                    parts.Add("abstract");

                // C#-style: final override
                if (method.IsFinal && method.IsOverride)
                    parts.Add("final");

                if (method.IsVirtual)
                    parts.Add("virtual");

                if (method.IsOverride)
                    parts.Add("override");

                if (method.IsAsync)
                    parts.Add("async");

                //if (method.IsPartialDefinition || method.IsPartialImplementation)
                //    parts.Add("partial");

                break;

            case IPropertySymbol property:
                if (property.IsStatic)
                    parts.Add("static");

                /*
                if (property.IsAbstract)
                    parts.Add("abstract");

                if (property.IsSealed && property.IsOverride)
                    parts.Add("sealed");

                if (property.IsVirtual)
                    parts.Add("virtual");

                if (property.IsOverride)
                    parts.Add("override");

                // If Raven has required / readonly properties:
                if (property.IsRequired)
                    parts.Add("required");

                if (property.IsReadOnly)
                    parts.Add("readonly");
                */

                break;

            case IEventSymbol @event:
                if (@event.IsStatic)
                    parts.Add("static");

                /*
                if (@event.IsAbstract)
                    parts.Add("abstract");

                if (@event.IsSealed && @event.IsOverride)
                    parts.Add("sealed");

                if (@event.IsVirtual)
                    parts.Add("virtual");

                if (@event.IsOverride)
                    parts.Add("override");
                */
                break;

            case ILocalSymbol localSymbol:

                break;

            case INamedTypeSymbol type:
                // Class / struct / interface / delegate modifiers
                switch (type.TypeKind)
                {
                    case TypeKind.Class:
                    case TypeKind.Struct:
                    case TypeKind.Interface:
                    case TypeKind.Delegate:
                        if (type.IsStatic)
                        {
                            parts.Add("static");
                        }
                        else
                        {
                            if (type.IsAbstract)
                                parts.Add("abstract");

                            if (!type.IsClosed)
                                parts.Add("open");
                        }

                        break;
                }

                break;
        }

        return string.Join(" ", parts);
    }

    private static bool IsFieldDeclaredExtern(IFieldSymbol field)
        => field.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .Any(static syntax => syntax.AncestorsAndSelf()
                .OfType<ConstDeclarationSyntax>()
                .Any(static declaration => declaration.Modifiers.Any(
                    static modifier => modifier.Kind == SyntaxKind.ExternKeyword)));

    private static string? GetTypeDeclarationModifierPrefix(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.IsSealedHierarchy)
            return "sealed";

        return null;
    }

    public static IMethodSymbol? GetDelegateInvokeMethod(this INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.TypeKind != TypeKind.Delegate)
            return null;

        return typeSymbol
            .GetMembers("Invoke")
            .OfType<IMethodSymbol>()
            .FirstOrDefault();
    }

    private static string? GetTypeKeyword(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.TypeKind switch
        {
            TypeKind.Struct when typeSymbol.IsUnion => "union struct",
            TypeKind.Class when typeSymbol.IsUnion => "union class",
            TypeKind.Class => "class",
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => null
        };
    }

    private static string? GetMemberKindKeyword(ISymbol symbol)
    {
        return symbol switch
        {
            ITypeSymbol { IsUnionCase: true } => "case",
            IFieldSymbol { IsConst: true } => null,
            IFieldSymbol => "field",
            IPropertySymbol property => property.IsMutable ? "var" : "val",
            IEventSymbol => "event",
            IMethodSymbol { IsConstructor: true } => null,
            IMethodSymbol { MethodKind: MethodKind.StaticConstructor } => null,
            IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise } => null,
            IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => null,
            IMacroDeclarationSymbol => "macro",
            IMethodSymbol => "func",
            _ => null
        };
    }
}
