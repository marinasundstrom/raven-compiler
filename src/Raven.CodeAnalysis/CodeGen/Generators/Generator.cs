using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.CodeGen;

internal abstract class Generator
{
    static readonly Dictionary<SyntaxTree, SemanticModel> _semanticModels = new Dictionary<SyntaxTree, SemanticModel>();

    public Generator(Generator? parent = null)
    {
        Parent = parent;
    }

    public Generator? Parent { get; }

    public Compilation Compilation => MethodBodyGenerator.Compilation;

    public MethodGenerator MethodGenerator => MethodBodyGenerator.MethodGenerator;

    public virtual MethodBodyGenerator MethodBodyGenerator => Parent!.MethodBodyGenerator;

    public IMethodSymbol MethodSymbol => MethodBodyGenerator.MethodSymbol;

    public IILBuilder ILGenerator => MethodBodyGenerator.ILGenerator;

    public virtual void Emit()
    {

    }

    public virtual void AddLocal(ILocalSymbol localSymbol, IILocal builder)
    {
        Parent?.AddLocal(localSymbol, builder);
    }

    public virtual IILocal? GetLocal(ILocalSymbol localSymbol)
    {
        return Parent?.GetLocal(localSymbol);
    }

    public virtual IEnumerable<ILocalSymbol> EnumerateLocalsToDispose()
    {
        return Parent?.EnumerateLocalsToDispose() ?? Enumerable.Empty<ILocalSymbol>();
    }

    public virtual bool IsInsideExceptionHandler => Parent?.IsInsideExceptionHandler ?? false;

    public virtual bool TryGetExceptionExitLabel(out ILLabel label)
    {
        if (Parent is not null)
            return Parent.TryGetExceptionExitLabel(out label);

        label = default;
        return false;
    }

    public void EmitDispose(ImmutableArray<ILocalSymbol> locals)
    {
        if (locals.IsDefaultOrEmpty || locals.Length == 0)
            return;

        var disposableType = Compilation.GetSpecialType(SpecialType.System_IDisposable);
        if (disposableType.TypeKind == TypeKind.Error)
            return;

        var disposableClr = ResolveClrType(disposableType);
        var disposeMethod = disposableClr.GetMethod(nameof(IDisposable.Dispose), Type.EmptyTypes)
            ?? throw new InvalidOperationException("Missing IDisposable.Dispose method.");

        for (int i = locals.Length - 1; i >= 0; i--)
        {
            EmitDispose(locals[i], disposableClr, disposeMethod);
        }
    }

    private void EmitDispose(ILocalSymbol local, Type disposableClr, MethodInfo disposeMethod)
    {
        if (local.Type is null || local.Type.TypeKind == TypeKind.Error)
            return;

        var localBuilder = GetLocal(local);
        if (localBuilder is null)
            return;

        if (local.Type.IsReferenceType || local.Type.TypeKind == TypeKind.Null)
        {
            var skipLabel = ILGenerator.DefineLabel();
            ILGenerator.Emit(OpCodes.Ldloc, localBuilder);
            ILGenerator.Emit(OpCodes.Brfalse, skipLabel);
            ILGenerator.Emit(OpCodes.Ldloc, localBuilder);
            ILGenerator.Emit(OpCodes.Callvirt, disposeMethod);
            ILGenerator.MarkLabel(skipLabel);
        }
        else
        {
            var clrType = ResolveClrType(local.Type);
            ILGenerator.Emit(OpCodes.Ldloca, localBuilder);
            ILGenerator.Emit(OpCodes.Constrained, clrType);
            ILGenerator.Emit(OpCodes.Callvirt, disposeMethod);
        }
    }

    internal static Type InstantiateType(Type type)
    {
        if (type is TypeBuilder typeBuilder && typeBuilder.ContainsGenericParameters)
        {
            var parameters = typeBuilder.GetGenericArguments();
            return parameters.Length == 0 ? typeBuilder : typeBuilder.MakeGenericType(parameters);
        }

        if (type.IsGenericTypeDefinition)
        {
            var parameters = type.GetGenericArguments();
            return parameters.Length == 0 ? type : type.MakeGenericType(parameters);
        }

        return type;
    }

    protected BoundNode GetBoundNode(SyntaxNode syntaxNode)
    {
        SemanticModel semanticModel = ResolveSemanticModel(syntaxNode);
        return semanticModel.GetBoundNode(syntaxNode);
    }

    protected BoundExpression GetBoundNode(ExpressionSyntax expression)
    {
        SemanticModel semanticModel = ResolveSemanticModel(expression);
        return semanticModel.GetBoundNode(expression) ?? throw new InvalidCastException("Cannot cast {0} to {2}.");
    }

    protected SymbolInfo GetSymbolInfo(SyntaxNode syntaxNode)
    {
        SemanticModel semanticModel = ResolveSemanticModel(syntaxNode);
        return semanticModel.GetSymbolInfo(syntaxNode);
    }

    protected T? GetDeclaredSymbol<T>(SyntaxNode syntaxNode)
        where T : class, ISymbol
    {
        SemanticModel semanticModel = ResolveSemanticModel(syntaxNode);
        return semanticModel.GetDeclaredSymbol(syntaxNode) as T;
    }

    protected TypeInfo GetTypeInfo(ExpressionSyntax expression)
    {
        SemanticModel semanticModel = ResolveSemanticModel(expression);
        return semanticModel.GetTypeInfo(expression);
    }

    public Type ResolveClrType(ITypeSymbol typeSymbol)
    {
        return MethodBodyGenerator.ResolveClrType(typeSymbol);
    }

    public MemberInfo? GetMemberBuilder(SourceSymbol sourceSymbol) => MethodGenerator.TypeGenerator.CodeGen.GetMemberBuilder(sourceSymbol);

    protected static bool TypeMayRequireBoxing(ITypeSymbol type)
    {
        if (type.IsValueType)
            return true;

        if (type is ITypeParameterSymbol typeParameter)
            return (typeParameter.ConstraintKind & TypeParameterConstraintKind.ReferenceType) == 0;

        return false;
    }

    protected static bool IsDefinitelyReferenceTypeForBoxTarget(ITypeSymbol type)
    {
        if (type.IsValueType)
            return false;

        if (type is ITypeParameterSymbol typeParameter)
            return (typeParameter.ConstraintKind & TypeParameterConstraintKind.ReferenceType) != 0;

        return true;
    }

    protected static bool ShouldBoxForReferenceTarget(ITypeSymbol source, ITypeSymbol target)
        => TypeMayRequireBoxing(source) && IsDefinitelyReferenceTypeForBoxTarget(target);

    protected static ConstructorInfo GetNullableConstructor(Type nullableType, Type underlyingType)
    {
        if (nullableType.IsGenericType)
        {
            var definition = nullableType.GetGenericTypeDefinition();
            var isTypeBuilderInstantiation = string.Equals(
                nullableType.GetType().FullName,
                "System.Reflection.Emit.TypeBuilderInstantiation",
                StringComparison.Ordinal);
            if (nullableType.ContainsGenericParameters || definition is TypeBuilder || isTypeBuilderInstantiation)
            {
                var genericArgument = definition.GetGenericArguments()[0];
                var definitionCtor = definition.GetConstructor(new[] { genericArgument });
                if (definitionCtor is not null)
                    return TypeBuilder.GetConstructor(nullableType, definitionCtor);

                throw new InvalidOperationException($"Missing Nullable constructor for {nullableType}");
            }
        }

        var ctor = nullableType.GetConstructor(new[] { underlyingType });
        if (ctor is not null)
            return ctor;

        throw new InvalidOperationException($"Missing Nullable constructor for {nullableType}");
    }

    private SemanticModel ResolveSemanticModel(SyntaxNode syntaxNode)
    {
        var syntaxTree = syntaxNode.SyntaxTree!;

        if (!_semanticModels.TryGetValue(syntaxTree, out var semanticModel))
        {
            semanticModel = Compilation.GetSemanticModel(syntaxTree);
            _semanticModels[syntaxTree] = semanticModel;
        }

        return semanticModel;
    }

    protected internal void EmitConversion(ITypeSymbol from, ITypeSymbol to, Conversion conversion)
    {
        if (conversion.IsIdentity && !RequiresNullableProjectionConversion(from, to))
            return;

        if (to is RefTypeSymbol && from is IAddressTypeSymbol)
            return;

        // A direct enum numeric conversion only depends on the enum's metadata
        // underlying type. Resolve it before asking Reflection.Emit for runtime
        // types so target-only enums can be converted without being loadable in
        // the compiler host.
        if (conversion.IsNumeric &&
            (from.TypeKind == TypeKind.Enum || to.TypeKind == TypeKind.Enum))
        {
            EmitNumericConversion(from, to);
            return;
        }

        var fromClrType = ResolveClrType(from);
        var toClrType = ResolveClrType(to);

        if (fromClrType == toClrType)
            return;

        if (from.TypeKind == TypeKind.Null &&
            to is NullableTypeSymbol nullableValueType &&
            nullableValueType.GetNullableAbiProjection() == NullableAbiProjection.NullableValueType)
        {
            ILGenerator.Emit(OpCodes.Pop);
            EmitDefaultValue(nullableValueType);
            return;
        }

        if (from.TypeKind == TypeKind.Null && conversion.IsReference)
            return;

        if (conversion.IsUserDefined && !conversion.IsLifted && conversion.MethodSymbol is IMethodSymbol userDefinedMethod)
        {
            var parameterType = userDefinedMethod.Parameters[0].Type;
            if (!SymbolEqualityComparer.Default.Equals(from, parameterType))
            {
                var parameterConversion = Compilation.ClassifyConversion(
                    from,
                    parameterType,
                    includeUserDefined: IsReadOnlySpanCastUpMethod(userDefinedMethod));
                if (parameterConversion.Exists && parameterConversion.IsImplicit)
                    EmitConversion(from, parameterType, parameterConversion);
            }

            ILGenerator.Emit(OpCodes.Call, GetMethodInfo(userDefinedMethod));
            return;
        }

        if (to is NullableTypeSymbol nullableReference &&
            nullableReference.GetNullableAbiProjection() == NullableAbiProjection.AnnotatedUnderlyingType)
        {
            EmitConversion(from, nullableReference.UnderlyingType, conversion);
            return;
        }

        if (to is NullableTypeSymbol nullableTo &&
            nullableTo.GetNullableAbiProjection() == NullableAbiProjection.NullableValueType)
        {
            if (conversion.IsLifted &&
                from is NullableTypeSymbol fromNullable &&
                fromNullable.GetNullableAbiProjection() == NullableAbiProjection.NullableValueType)
            {
                EmitLiftedNullableConversion(fromNullable, nullableTo, conversion);
                return;
            }

            EmitNullableConversion(from, nullableTo);
            return;
        }

        if (from.SpecialType == SpecialType.System_Object &&
            to is INamedTypeSymbol destinationCarrierFromObject &&
            destinationCarrierFromObject.TryGetUnion() is IUnionSymbol destinationDiscriminatedUnion &&
            (conversion.IsUnboxing || conversion.IsReference) &&
            EmitObjectToDiscriminatedUnionConversion(destinationCarrierFromObject, destinationDiscriminatedUnion))
        {
            return;
        }

        if (conversion.IsUnion)
        {
            if (!conversion.IsImplicit &&
                from is INamedTypeSymbol sourceNamedUnion &&
                sourceNamedUnion.TryGetUnion() is not null &&
                EmitExplicitUnionExtraction(sourceNamedUnion, to))
            {
                return;
            }

            if (conversion.MethodSymbol is { } factory)
            {
                var parameter = factory.Parameters[0];
                var argumentType = parameter.GetByRefElementType();
                PrepareUnionConstructorArgument(from, argumentType);
                if (parameter.RefKind == RefKind.In)
                {
                    var argument = ILGenerator.DeclareLocal(ResolveClrType(argumentType));
                    ILGenerator.Emit(OpCodes.Stloc, argument);
                    ILGenerator.Emit(OpCodes.Ldloca, argument);
                }
                if (factory.IsAbstract)
                    ILGenerator.Emit(OpCodes.Constrained, toClrType);
                ILGenerator.Emit(OpCodes.Call, GetMethodInfo(factory));
                return;
            }

            ConstructorInfo? runtimeConstructor = null;
            try
            {
                runtimeConstructor = toClrType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { fromClrType },
                    modifiers: null);
            }
            catch (NotSupportedException)
            {
                runtimeConstructor = null;
            }

            if (runtimeConstructor is not null)
            {
                ILGenerator.Emit(OpCodes.Newobj, runtimeConstructor);
                return;
            }

            if (conversion.ConstructorSymbol is not null)
            {
                PrepareUnionConstructorArgument(from, conversion.ConstructorSymbol.Parameters[0].Type);
                ILGenerator.Emit(OpCodes.Newobj, GetConstructorInfo(conversion.ConstructorSymbol));
                return;
            }

            if (IsDynamicBuilderType(toClrType) || IsDynamicBuilderType(fromClrType))
            {
                if (fromClrType == toClrType)
                    return;

                throw new NotSupportedException("Discriminated-union constructor not found.");
            }

            if (fromClrType == toClrType)
                return;
        }

        if (conversion.IsNumeric)
        {
            EmitNumericConversion(from, to);
            return;
        }

        if (conversion.IsUnboxing)
        {
            // Some bound conversions are classified as unboxing even when the emitted
            // source is already a value type (for example duplicated union arms such as
            // bool|bool lowering to bool). In that case there is nothing to unbox.
            if (fromClrType.IsValueType)
            {
                if (toClrType == fromClrType)
                    return;

                if (conversion.IsNumeric)
                {
                    EmitNumericConversion(from, to);
                    return;
                }
            }

            ILGenerator.Emit(OpCodes.Unbox_Any, toClrType);
            return;
        }

        /*
                if (conversion.IsUnboxing)
                {
                    if (fromClrType.IsValueType)
                    {
                        if (toClrType == fromClrType)
                            return;

                        if (conversion.IsNumeric)
                        {

                            EmitNumericConversion(from, to);
                            return;
                        }
                    }

                    ILGenerator.Emit(OpCodes.Unbox_Any, toClrType);
                    return;
                }*/

        if (conversion.IsBoxing)
        {
            // Generic type parameters can require boxing even when reflection does not
            // report IsValueType=true on the parameter type itself.
            if (!fromClrType.IsValueType && !fromClrType.IsGenericParameter)
                return;

            ILGenerator.Emit(OpCodes.Box, fromClrType);
            if (!SymbolEqualityComparer.Default.Equals(from, to) &&
                to.SpecialType != SpecialType.System_Object)
                ILGenerator.Emit(OpCodes.Castclass, toClrType);
            return;
        }

        if (conversion.IsReference && ShouldBoxForReferenceTarget(from, to))
        {
            ILGenerator.Emit(OpCodes.Box, fromClrType);
            if (!fromClrType.Equals(toClrType) &&
                to.SpecialType != SpecialType.System_Object)
                ILGenerator.Emit(OpCodes.Castclass, toClrType);
            return;
        }

        if (conversion.IsReference)
        {
            if (conversion.IsImplicit)
                return;

            if (toClrType.IsGenericParameter)
            {
                ILGenerator.Emit(OpCodes.Unbox_Any, toClrType);
                return;
            }

            ILGenerator.Emit(OpCodes.Castclass, toClrType);
            return;
        }

        if (conversion.IsPointer)
        {
            ILGenerator.Emit(OpCodes.Conv_U);
            return;
        }

        throw new NotSupportedException("Unsupported conversion");
    }

    internal static bool RequiresNullableProjectionConversion(ITypeSymbol from, ITypeSymbol to)
        => from is NullableTypeSymbol fromNullable &&
           to is NullableTypeSymbol toNullable &&
           fromNullable.GetNullableAbiProjection() != toNullable.GetNullableAbiProjection() &&
           SymbolEqualityComparer.Default.Equals(fromNullable.UnderlyingType, toNullable.UnderlyingType);

    private static bool IsReadOnlySpanCastUpMethod(IMethodSymbol method)
        => method.Name == "CastUp" &&
           method.IsStatic &&
           method.Arity == 1 &&
           method.ContainingType is
           {
               Name: "ReadOnlySpan",
               ContainingNamespace: { } containingNamespace
           } &&
           containingNamespace.ToDisplayString() == "System";

    private void PrepareUnionConstructorArgument(ITypeSymbol from, ITypeSymbol parameterType)
    {
        if (from.TypeKind == TypeKind.Null &&
            parameterType is NullableTypeSymbol nullableParameter &&
            nullableParameter.GetNullableAbiProjection() == NullableAbiProjection.NullableValueType)
        {
            ILGenerator.Emit(OpCodes.Pop);
            EmitDefaultValue(parameterType);
            return;
        }

        if (SymbolEqualityComparer.Default.Equals(from, parameterType))
            return;

        var parameterConversion = Compilation.ClassifyConversion(from, parameterType, includeUserDefined: false);
        if (parameterConversion.Exists && !parameterConversion.IsIdentity)
            EmitConversion(from, parameterType, parameterConversion);
    }

    private bool EmitExplicitUnionExtraction(INamedTypeSymbol unionType, ITypeSymbol memberType)
    {
        var provider = UnionFacts.GetMemberProvider(unionType);
        var tryGetMethod = (provider ?? unionType)
            .GetMembers("TryGetValue")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method =>
                !method.IsStatic &&
                method.Parameters.Length == 1 &&
                method.Parameters[0].RefKind == RefKind.Out &&
                (SymbolEqualityComparer.Default.Equals(method.Parameters[0].GetByRefElementType(), memberType) ||
                 SymbolEqualityComparer.Default.Equals(
                     method.Parameters[0].GetByRefElementType().OriginalDefinition ?? method.Parameters[0].GetByRefElementType(),
                     memberType.OriginalDefinition ?? memberType)));

        if (tryGetMethod is null)
            return false;

        var unionClrType = ResolveClrType(unionType);
        var unionLocal = ILGenerator.DeclareLocal(unionClrType);
        var memberClrType = ResolveClrType(memberType);
        var valueLocal = ILGenerator.DeclareLocal(memberClrType);
        var successLabel = ILGenerator.DefineLabel();

        ILGenerator.Emit(OpCodes.Stloc, unionLocal);
        ILGenerator.Emit(unionClrType.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, unionLocal);
        ILGenerator.Emit(OpCodes.Ldloca, valueLocal);
        if (provider is not null && unionClrType.IsValueType)
            ILGenerator.Emit(OpCodes.Constrained, unionClrType);
        ILGenerator.Emit(provider is not null ? OpCodes.Callvirt : OpCodes.Call, GetMethodInfo(tryGetMethod));
        ILGenerator.Emit(OpCodes.Brtrue, successLabel);

        var invalidCastCtor = typeof(InvalidCastException).GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException("Missing InvalidCastException constructor.");
        ILGenerator.Emit(OpCodes.Newobj, invalidCastCtor);
        ILGenerator.Emit(OpCodes.Throw);

        ILGenerator.MarkLabel(successLabel);
        ILGenerator.Emit(OpCodes.Ldloc, valueLocal);
        return true;
    }

    private bool EmitObjectToDiscriminatedUnionConversion(INamedTypeSymbol destinationNamedUnion, IUnionSymbol destinationUnion)
    {
        var variantConversions = new List<(ITypeSymbol VariantType, Conversion Conversion)>(destinationUnion.Variants.Length);
        foreach (var variant in destinationUnion.Variants)
        {
            var variantType = (ITypeSymbol)variant;
            var conversion = Compilation.ClassifyConversion(variantType, destinationNamedUnion);
            if (!conversion.Exists || !conversion.IsImplicit)
                continue;

            variantConversions.Add((variantType, conversion));
        }

        if (variantConversions.Count == 0)
            return false;

        var destinationClrType = ResolveClrType(destinationNamedUnion);
        var boxedSourceLocal = ILGenerator.DeclareLocal(typeof(object));
        var endLabel = ILGenerator.DefineLabel();
        var failedLabel = ILGenerator.DefineLabel();
        ILGenerator.Emit(OpCodes.Stloc, boxedSourceLocal);

        var objectGetType = typeof(object).GetMethod(nameof(object.GetType), Type.EmptyTypes)
            ?? throw new InvalidOperationException("Missing System.Object.GetType method.");
        var typeFromHandle = typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), BindingFlags.Public | BindingFlags.Static, binder: null, types: [typeof(RuntimeTypeHandle)], modifiers: null)
            ?? throw new InvalidOperationException("Missing System.Type.GetTypeFromHandle method.");
        var typeEquality = typeof(Type).GetMethod("op_Equality", BindingFlags.Public | BindingFlags.Static, binder: null, types: [typeof(Type), typeof(Type)], modifiers: null)
            ?? throw new InvalidOperationException("Missing System.Type.op_Equality method.");

        var afterDestinationLabel = ILGenerator.DefineLabel();
        ILGenerator.Emit(OpCodes.Ldloc, boxedSourceLocal);
        ILGenerator.Emit(OpCodes.Brfalse, afterDestinationLabel);
        ILGenerator.Emit(OpCodes.Ldloc, boxedSourceLocal);
        ILGenerator.Emit(OpCodes.Callvirt, objectGetType);
        ILGenerator.Emit(OpCodes.Ldtoken, destinationClrType);
        ILGenerator.Emit(OpCodes.Call, typeFromHandle);
        ILGenerator.Emit(OpCodes.Call, typeEquality);
        ILGenerator.Emit(OpCodes.Brfalse, afterDestinationLabel);
        ILGenerator.Emit(OpCodes.Ldloc, boxedSourceLocal);
        ILGenerator.Emit(destinationClrType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, destinationClrType);
        ILGenerator.Emit(OpCodes.Br, endLabel);
        ILGenerator.MarkLabel(afterDestinationLabel);

        foreach (var (variantType, conversion) in variantConversions)
        {
            var variantClrType = ResolveClrType(variantType);
            var nextVariantLabel = ILGenerator.DefineLabel();

            ILGenerator.Emit(OpCodes.Ldloc, boxedSourceLocal);
            ILGenerator.Emit(OpCodes.Brfalse, nextVariantLabel);
            ILGenerator.Emit(OpCodes.Ldloc, boxedSourceLocal);
            ILGenerator.Emit(OpCodes.Callvirt, objectGetType);
            ILGenerator.Emit(OpCodes.Ldtoken, variantClrType);
            ILGenerator.Emit(OpCodes.Call, typeFromHandle);
            ILGenerator.Emit(OpCodes.Call, typeEquality);
            ILGenerator.Emit(OpCodes.Brfalse, nextVariantLabel);

            ILGenerator.Emit(OpCodes.Ldloc, boxedSourceLocal);
            ILGenerator.Emit(variantClrType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, variantClrType);
            EmitConversion(variantType, destinationNamedUnion, conversion);
            ILGenerator.Emit(OpCodes.Br, endLabel);
            ILGenerator.MarkLabel(nextVariantLabel);
        }

        ILGenerator.Emit(OpCodes.Br, failedLabel);
        ILGenerator.MarkLabel(failedLabel);

        var invalidCastCtor = typeof(InvalidCastException).GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException("Missing InvalidCastException default constructor.");
        ILGenerator.Emit(OpCodes.Newobj, invalidCastCtor);
        ILGenerator.Emit(OpCodes.Throw);

        ILGenerator.MarkLabel(endLabel);
        return true;
    }

    private void EmitNullableConversion(ITypeSymbol from, NullableTypeSymbol nullableTo)
    {
        if (from.TypeKind == TypeKind.Null)
        {
            EmitDefaultValue(nullableTo);
            return;
        }

        if (from is NullableTypeSymbol fromNullable)
        {
            if (fromNullable.GetNullableAbiProjection() == nullableTo.GetNullableAbiProjection() &&
                SymbolEqualityComparer.Default.Equals(fromNullable.UnderlyingType, nullableTo.UnderlyingType))
            {
                return;
            }

            if (fromNullable.GetNullableAbiProjection() == NullableAbiProjection.AnnotatedUnderlyingType &&
                SymbolEqualityComparer.Default.Equals(fromNullable.UnderlyingType, nullableTo.UnderlyingType))
            {
                from = fromNullable.UnderlyingType;
            }
            else
            {
                throw new NotSupportedException("Unsupported nullable conversion");
            }
        }

        var underlying = nullableTo.UnderlyingType;

        var underlyingClr = ResolveClrType(underlying);
        var nullableClr = ResolveClrType(nullableTo);

        var valueLocal = ILGenerator.DeclareLocal(underlyingClr);
        var nullableLocal = ILGenerator.DeclareLocal(nullableClr);

        if (!SymbolEqualityComparer.Default.Equals(from, underlying))
        {
            var underlyingConversion = Compilation.ClassifyConversion(from, underlying);
            EmitConversion(from, underlying, underlyingConversion);
        }

        ILGenerator.Emit(OpCodes.Stloc, valueLocal);
        ILGenerator.Emit(OpCodes.Ldloca, nullableLocal);
        ILGenerator.Emit(OpCodes.Ldloc, valueLocal);

        var ctor = GetNullableConstructor(nullableClr, underlyingClr);

        ILGenerator.Emit(OpCodes.Call, ctor);
        ILGenerator.Emit(OpCodes.Ldloc, nullableLocal);
    }

    private void EmitLiftedNullableConversion(
        NullableTypeSymbol fromNullable,
        NullableTypeSymbol toNullable,
        Conversion conversion)
    {
        if (SymbolEqualityComparer.Default.Equals(fromNullable.UnderlyingType, toNullable.UnderlyingType) &&
            conversion.IsIdentity)
        {
            return;
        }

        var fromClr = ResolveClrType(fromNullable);
        var toClr = ResolveClrType(toNullable);

        var fromUnderlying = fromNullable.UnderlyingType;
        var toUnderlying = toNullable.UnderlyingType;
        var fromUnderlyingClr = ResolveClrType(fromUnderlying);
        var toUnderlyingClr = ResolveClrType(toUnderlying);

        var fromLocal = ILGenerator.DeclareLocal(fromClr);
        var toLocal = ILGenerator.DeclareLocal(toClr);
        var valueLocal = ILGenerator.DeclareLocal(toUnderlyingClr);

        var hasValueLabel = ILGenerator.DefineLabel();
        var doneLabel = ILGenerator.DefineLabel();

        ILGenerator.Emit(OpCodes.Stloc, fromLocal);
        ILGenerator.Emit(OpCodes.Ldloca, fromLocal);
        var hasValue = fromClr.GetProperty("HasValue")!.GetGetMethod()!;
        ILGenerator.Emit(OpCodes.Call, hasValue);
        ILGenerator.Emit(OpCodes.Brtrue, hasValueLabel);

        ILGenerator.Emit(OpCodes.Ldloca, toLocal);
        ILGenerator.Emit(OpCodes.Initobj, toClr);
        ILGenerator.Emit(OpCodes.Ldloc, toLocal);
        ILGenerator.Emit(OpCodes.Br, doneLabel);

        ILGenerator.MarkLabel(hasValueLabel);
        ILGenerator.Emit(OpCodes.Ldloca, fromLocal);
        var getValueOrDefault = fromClr.GetMethod("GetValueOrDefault", Type.EmptyTypes)!;
        ILGenerator.Emit(OpCodes.Call, getValueOrDefault);

        if (!SymbolEqualityComparer.Default.Equals(fromUnderlying, toUnderlying))
        {
            var underlyingConversion = Compilation.ClassifyConversion(fromUnderlying, toUnderlying);
            EmitConversion(fromUnderlying, toUnderlying, underlyingConversion);
        }

        ILGenerator.Emit(OpCodes.Stloc, valueLocal);
        ILGenerator.Emit(OpCodes.Ldloca, toLocal);
        ILGenerator.Emit(OpCodes.Ldloc, valueLocal);

        var ctor = GetNullableConstructor(toClr, toUnderlyingClr);

        ILGenerator.Emit(OpCodes.Call, ctor);
        ILGenerator.Emit(OpCodes.Ldloc, toLocal);
        ILGenerator.MarkLabel(doneLabel);
    }

    protected void EmitNumericConversion(ITypeSymbol from, ITypeSymbol to)
    {
        // If you have literal types (e.g. int literal) normalize them.
        from = from.UnwrapLiteralType() ?? from;
        to = to.UnwrapLiteralType() ?? to;

        from = NormalizeEnumNumericConversionType(from);
        to = NormalizeEnumNumericConversionType(to);

        // Numeric conversion should not be called for nullable.
        // (You already handle nullable earlier, but keep this as a sanity check.)
        if (from is NullableTypeSymbol || to is NullableTypeSymbol)
            throw new InvalidOperationException("Numeric conversion called for nullable types; expected nullable lowering earlier.");

        // decimal involved? -> call helper methods
        if (from.SpecialType == SpecialType.System_Decimal ||
            to.SpecialType == SpecialType.System_Decimal)
        {
            EmitDecimalNumericConversion(from, to);
            return;
        }

        // existing conv.* path
        EmitPrimitiveNumericConversion(to);
    }

    private static ITypeSymbol NormalizeEnumNumericConversionType(ITypeSymbol type)
        => type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlyingType }
            ? underlyingType
            : type;

    private void EmitPrimitiveNumericConversion(ITypeSymbol to)
    {
        switch (to.SpecialType)
        {
            case SpecialType.System_Int32: ILGenerator.Emit(OpCodes.Conv_I4); break;
            case SpecialType.System_Int64: ILGenerator.Emit(OpCodes.Conv_I8); break;
            case SpecialType.System_Single: ILGenerator.Emit(OpCodes.Conv_R4); break;
            case SpecialType.System_Double: ILGenerator.Emit(OpCodes.Conv_R8); break;
            case SpecialType.System_Int16: ILGenerator.Emit(OpCodes.Conv_I2); break;
            case SpecialType.System_UInt16:
            case SpecialType.System_Char: ILGenerator.Emit(OpCodes.Conv_U2); break;
            case SpecialType.System_UInt32: ILGenerator.Emit(OpCodes.Conv_U4); break;
            case SpecialType.System_UInt64: ILGenerator.Emit(OpCodes.Conv_U8); break;
            case SpecialType.System_SByte: ILGenerator.Emit(OpCodes.Conv_I1); break;
            case SpecialType.System_Byte: ILGenerator.Emit(OpCodes.Conv_U1); break;
            case SpecialType.System_IntPtr: ILGenerator.Emit(OpCodes.Conv_I); break;
            case SpecialType.System_UIntPtr: ILGenerator.Emit(OpCodes.Conv_U); break;
            default:
                throw new NotSupportedException($"Unsupported numeric conversion to {to.ToDisplayString()}");
        }
    }

    private void EmitDecimalNumericConversion(ITypeSymbol from, ITypeSymbol to)
    {
        // Stack already contains a value of type `from`.
        // We must emit a CALL to the right Decimal operator.

        if (from.SpecialType == SpecialType.System_Decimal &&
            to.SpecialType == SpecialType.System_Decimal)
        {
            return;
        }

        if (to.SpecialType == SpecialType.System_Decimal)
        {
            // <primitive> -> decimal
            var mi = GetDecimalToDecimalOperator(from);
            ILGenerator.Emit(OpCodes.Call, mi);
            return;
        }

        if (from.SpecialType == SpecialType.System_Decimal)
        {
            // decimal -> <primitive>
            var mi = GetDecimalFromDecimalOperator(to);
            ILGenerator.Emit(OpCodes.Call, mi);
            return;
        }

        throw new InvalidOperationException("EmitDecimalNumericConversion called without decimal involvement.");
    }

    private static MethodInfo GetDecimalToDecimalOperator(ITypeSymbol from)
    {
        var dec = typeof(decimal);

        // implicit for integral types, explicit for float/double
        return from.SpecialType switch
        {
            SpecialType.System_SByte => dec.GetMethod("op_Implicit", new[] { typeof(sbyte) })!,
            SpecialType.System_Byte => dec.GetMethod("op_Implicit", new[] { typeof(byte) })!,
            SpecialType.System_Int16 => dec.GetMethod("op_Implicit", new[] { typeof(short) })!,
            SpecialType.System_UInt16 => dec.GetMethod("op_Implicit", new[] { typeof(ushort) })!,
            SpecialType.System_Int32 => dec.GetMethod("op_Implicit", new[] { typeof(int) })!,
            SpecialType.System_UInt32 => dec.GetMethod("op_Implicit", new[] { typeof(uint) })!,
            SpecialType.System_Int64 => dec.GetMethod("op_Implicit", new[] { typeof(long) })!,
            SpecialType.System_UInt64 => dec.GetMethod("op_Implicit", new[] { typeof(ulong) })!,
            SpecialType.System_Char => dec.GetMethod("op_Implicit", new[] { typeof(char) })!,

            SpecialType.System_Single => dec.GetMethod("op_Explicit", new[] { typeof(float) })!,
            SpecialType.System_Double => dec.GetMethod("op_Explicit", new[] { typeof(double) })!,

            _ => throw new NotSupportedException($"No numeric conversion from {from.ToDisplayString()} to decimal.")
        };
    }

    private static MethodInfo GetDecimalFromDecimalOperator(ITypeSymbol to)
    {
        // We need: op_Explicit(decimal) -> <target primitive>
        // Decimal has MANY op_Explicit overloads, so pick by return type.

        Type? returnType = to.SpecialType switch
        {
            SpecialType.System_SByte => typeof(sbyte),
            SpecialType.System_Byte => typeof(byte),
            SpecialType.System_Int16 => typeof(short),
            SpecialType.System_UInt16 => typeof(ushort),
            SpecialType.System_Int32 => typeof(int),
            SpecialType.System_UInt32 => typeof(uint),
            SpecialType.System_Int64 => typeof(long),
            SpecialType.System_UInt64 => typeof(ulong),
            SpecialType.System_Char => typeof(char),
            SpecialType.System_Single => typeof(float),
            SpecialType.System_Double => typeof(double),
            _ => null
        };

        if (returnType is null)
            throw new NotSupportedException($"No numeric conversion from decimal to {to.ToDisplayString()}.");

        var dec = typeof(decimal);

        var mi = dec.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Explicit")
            .Where(m =>
            {
                var ps = m.GetParameters();
                return ps.Length == 1 &&
                       ps[0].ParameterType == typeof(decimal) &&
                       m.ReturnType == returnType;
            })
            .FirstOrDefault();

        return mi ?? throw new InvalidOperationException($"Missing decimal.op_Explicit(decimal) -> {returnType}.");
    }

    protected void EmitDefaultValue(ITypeSymbol type)
    {
        if (type.GetNonNullableType() is ITypeParameterSymbol)
        {
            EmitDefaultValueWithInitObj(type);
            return;
        }

        if (type.IsValueType)
        {
            EmitDefaultValueWithInitObj(type);
            return;
        }

        ILGenerator.Emit(OpCodes.Ldnull);
    }

    protected void EmitDefaultValueWithInitObj(ITypeSymbol type)
    {
        var clr = ResolveClrType(type);
        var local = ILGenerator.DeclareLocal(clr);
        ILGenerator.Emit(OpCodes.Ldloca, local);
        ILGenerator.Emit(OpCodes.Initobj, clr);
        ILGenerator.Emit(OpCodes.Ldloc, local);
    }

    public MethodInfo GetMethodInfo(IMethodSymbol methodSymbol)
    {
        methodSymbol = SubstituteAsyncStateMachineMethodTypeParameters(methodSymbol);
        return MethodGenerator.TypeGenerator.CodeGen.GetMethodInfoOrMetadataProxy(methodSymbol);
    }

    public ConstructorInfo GetConstructorInfo(IMethodSymbol constructorSymbol)
    {
        constructorSymbol = SubstituteAsyncStateMachineMethodTypeParameters(constructorSymbol);
        return MethodGenerator.TypeGenerator.CodeGen.RuntimeSymbolResolver.GetConstructorInfo(constructorSymbol);
    }

    private IMethodSymbol SubstituteAsyncStateMachineMethodTypeParameters(IMethodSymbol methodSymbol)
    {
        if (MethodGenerator.MethodSymbol.ContainingType is SynthesizedAsyncStateMachineTypeSymbol asyncStateMachine)
            return asyncStateMachine.SubstituteAsyncMethodTypeParameters(methodSymbol);

        if (MethodGenerator.MethodSymbol.ContainingType is ConstructedNamedTypeSymbol { ConstructedFrom: SynthesizedAsyncStateMachineTypeSymbol constructedAsyncStateMachine })
            return constructedAsyncStateMachine.SubstituteAsyncMethodTypeParameters(methodSymbol);

        return methodSymbol;
    }

    private static bool IsDynamicBuilderType(Type type)
    {
        var fullName = type.FullName;
        return fullName is not null &&
               fullName.StartsWith("System.Reflection.Emit.TypeBuilder", StringComparison.Ordinal);
    }

    private static bool ParameterMatchesSource(ITypeSymbol parameterType, ITypeSymbol sourceType)
    {
        if (SymbolEqualityComparer.Default.Equals(parameterType, sourceType))
            return true;

        if (parameterType.MetadataIdentityEquals(sourceType))
            return true;

        if (parameterType is INamedTypeSymbol parameterNamed &&
            sourceType is INamedTypeSymbol sourceNamed &&
            SymbolEqualityComparer.Default.Equals(parameterNamed.OriginalDefinition, sourceNamed.OriginalDefinition))
        {
            return true;
        }

        return false;
    }
}
