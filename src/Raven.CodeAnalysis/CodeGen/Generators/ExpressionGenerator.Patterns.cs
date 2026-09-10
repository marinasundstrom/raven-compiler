using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;

namespace Raven.CodeAnalysis.CodeGen;

internal partial class ExpressionGenerator
{
    // ============================================
    // Entry points
    // ============================================

    private void EmitIsPatternExpression(BoundIsPatternExpression e, EmitContext context)
    {
        EmitExpression(e.Expression);

        var inputType =
            e.Expression.Type
            ?? Compilation.GetSpecialType(SpecialType.System_Object);

        if (inputType.TypeKind == TypeKind.Error)
            inputType = Compilation.GetSpecialType(SpecialType.System_Object);

        EmitPattern(e.Pattern, inputType, scope: null);
    }

    private void EmitMatchExpression(BoundMatchExpression matchExpression, EmitContext context)
    {
        var lowered = Lowerer.LowerExpression(MethodSymbol, matchExpression);
        if (lowered is BoundMatchExpression)
            throw new InvalidOperationException("BoundMatchExpression should be lowered before code generation.");

        EmitExpression(lowered, context);
    }

    // ============================================
    // Pattern emission (typed input)
    // ============================================

    private enum PatternInput
    {
        Typed = 0,
        Object = 1,
    }

    private PatternInput GetPatternInputRequirement(BoundPattern pattern)
    {
        switch (pattern)
        {
            case BoundGuardedPattern guardedPattern:
                {
                    var patternRequirement = GetPatternInputRequirement(guardedPattern.Pattern);
                    var guardRequirement = guardedPattern.GuardPattern is null
                        ? PatternInput.Typed
                        : GetPatternInputRequirement(guardedPattern.GuardPattern);
                    return (PatternInput)Math.Max((int)patternRequirement, (int)guardRequirement);
                }

            // constant patterns can be evaluated in typed form (for value-type scrutinees)
            case BoundConstantPattern:
                return PatternInput.Typed;

            case BoundComparisonPattern:
            case BoundRangePattern:
                return PatternInput.Typed;

            // combinations inherit the "worst" requirement
            case BoundUnaryPattern up:
                return GetPatternInputRequirement(up.Pattern);

            case BoundBinaryPattern bp:
                {
                    var l = GetPatternInputRequirement(bp.Left);
                    var r = GetPatternInputRequirement(bp.Right);
                    return (PatternInput)Math.Max((int)l, (int)r);
                }

            // These need object semantics in your implementation (isinst, ITuple, member lookup pipeline, etc.)
            case BoundDeclarationPattern:
            case BoundPositionalPattern:
            case BoundDictionaryPattern:
            case BoundDeconstructPattern:
            case BoundPropertyPattern:
            case BoundCasePattern:
            case BoundUnionMemberPattern:
                return PatternInput.Object;

            default:
                return PatternInput.Object;
        }
    }

    private void EmitPattern(BoundPattern pattern, ITypeSymbol inputType, Generator? scope = null, IILocal? scrutineeLocal2 = null)
    {
        scope ??= this;

        if (inputType is INamedTypeSymbol providerUnion && providerUnion.TryGetUnion() is not null &&
            UnionFacts.GetMemberProvider(providerUnion) is { } provider &&
            EmitProviderPattern(pattern, providerUnion, provider, scope, scrutineeLocal2))
            return;

        static bool TypesMatch(ITypeSymbol? left, ITypeSymbol? right)
        {
            if (left is null || right is null)
                return false;

            if (SymbolEqualityComparer.Default.Equals(left, right))
                return true;

            var leftPlain = left.GetNonNullableType();
            var rightPlain = right.GetNonNullableType();
            if (SymbolEqualityComparer.Default.Equals(leftPlain, rightPlain))
                return true;

            var leftDefinition = left.OriginalDefinition ?? left;
            var rightDefinition = right.OriginalDefinition ?? right;
            return SymbolEqualityComparer.Default.Equals(leftDefinition, rightDefinition);
        }

        // Spill the scrutinee into a local of its current IL stack type (avoids forcing object boxing)
        IILocal SpillScrutineeToLocal(ITypeSymbol curType)
        {
            var clr = ResolveClrType(curType);
            var loc = ILGenerator.DeclareLocal(clr);
            ILGenerator.Emit(OpCodes.Stloc, loc);
            return loc;
        }

        // Some patterns require an object reference on stack to work (isinst/ITuple/property pipeline, etc.)
        void EnsureObjectOnStack(ref ITypeSymbol curType)
        {
            if (RequiresValueTypeHandling(curType) && curType.TypeKind != TypeKind.Error)
                ILGenerator.Emit(OpCodes.Box, ResolveClrType(curType));

            curType = Compilation.GetSpecialType(SpecialType.System_Object);
        }

        if (pattern is BoundComparisonPattern relational)
        {
            EmitComparisonPattern(relational, inputType, scope, scrutineeLocal2);
            return;
        }

        if (pattern is BoundGuardedPattern guardedPattern)
        {
            IILocal scrutineeLocal;
            if (scrutineeLocal2 is not null)
            {
                ILGenerator.Emit(OpCodes.Pop);
                scrutineeLocal = scrutineeLocal2;
            }
            else
            {
                scrutineeLocal = SpillScrutineeToLocal(inputType);
            }

            var labelFail = ILGenerator.DefineLabel();
            var labelDone = ILGenerator.DefineLabel();

            ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
            EmitPattern(guardedPattern.Pattern, inputType, scope, scrutineeLocal);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);

            if (guardedPattern.GuardPattern is not null)
            {
                ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
                EmitPattern(guardedPattern.GuardPattern, inputType, scope, scrutineeLocal);
            }
            else if (guardedPattern.GuardExpression is not null)
            {
                EmitExpression(guardedPattern.GuardExpression);
            }
            else
            {
                ILGenerator.Emit(OpCodes.Ldc_I4_1);
            }

            ILGenerator.Emit(OpCodes.Br, labelDone);

            ILGenerator.MarkLabel(labelFail);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);

            ILGenerator.MarkLabel(labelDone);
            return;
        }

        if (pattern is BoundRangePattern rangePattern)
        {
            EmitRangePattern(rangePattern, inputType, scope, scrutineeLocal2);
            return;
        }

        if (pattern is BoundDiscardPattern)
        {
            ILGenerator.Emit(OpCodes.Pop);
            ILGenerator.Emit(OpCodes.Ldc_I4_1);
            return;
        }

        if (pattern is BoundConstantPattern constantPattern)
        {
            IILocal? matchedLocal = null;
            if (constantPattern.Designator is not null && scrutineeLocal2 is null)
            {
                matchedLocal = ILGenerator.DeclareLocal(ResolveClrType(inputType));
                ILGenerator.Emit(OpCodes.Stloc, matchedLocal);
                ILGenerator.Emit(OpCodes.Ldloc, matchedLocal);
            }

            matchedLocal ??= scrutineeLocal2;
            EmitConstantPattern(constantPattern, inputType, scrutineeLocal2);

            if (constantPattern.Designator is not null && matchedLocal is not null)
            {
                var labelDone = ILGenerator.DefineLabel();
                ILGenerator.Emit(OpCodes.Dup);
                ILGenerator.Emit(OpCodes.Brfalse, labelDone);
                EmitPatternDesignator(constantPattern.Designator, matchedLocal, scope);
                ILGenerator.MarkLabel(labelDone);
            }

            return;
        }

        if (pattern is BoundUnionMemberPattern unionMemberPattern)
        {
            EmitUnionMemberPattern(unionMemberPattern, inputType, scope, scrutineeLocal2);
            return;
        }

        if (pattern is BoundDeclarationPattern declarationPattern)
        {
            var typeSymbol = declarationPattern.Type;

            if (typeSymbol.TypeKind == TypeKind.Null)
            {
                EmitNullConstantPattern(inputType, ResolveClrType(inputType), scrutineeLocal2);
                return;
            }

            var clrType = ResolveClrType(typeSymbol);

            if (inputType.TryGetUnion() is INamedTypeSymbol unionType &&
                inputType is INamedTypeSymbol inputNamedType)
            {
                var tryGetSymbol = inputNamedType
                    .GetMembers("TryGetValue")
                    .OfType<IMethodSymbol>()
                    .FirstOrDefault(m =>
                        !m.IsStatic &&
                        m.Parameters.Length == 1 &&
                        m.Parameters[0].RefKind == RefKind.Out &&
                        TypesMatch(m.Parameters[0].GetByRefElementType(), typeSymbol));

                if (tryGetSymbol is not null)
                {
                    var unionClrType = Generator.InstantiateType(ResolveClrType(inputType));
                    var tryGetMethod = CloseMethodOnRuntimeCarrier(unionClrType, GetMethodInfo(tryGetSymbol));
                    var outParameter = tryGetMethod.GetParameters() is [{ ParameterType: var outType }] &&
                                       outType.IsByRef &&
                                       outType.GetElementType() is Type outElementType
                        ? CloseTypeFromMethodContext(outElementType, tryGetMethod.DeclaringType)
                        : Generator.InstantiateType(clrType);

                    if (inputType.TypeKind != TypeKind.Error)
                    {
                        var inputClr = Generator.InstantiateType(ResolveClrType(inputType));
                        if (unionClrType.IsValueType && ClrTypesMatch(inputClr, unionClrType))
                        {
                            IILocal unionLocal;
                            if (scrutineeLocal2 is not null)
                            {
                                unionLocal = scrutineeLocal2;
                                ILGenerator.Emit(OpCodes.Pop);
                            }
                            else
                            {
                                unionLocal = ILGenerator.DeclareLocal(unionClrType);
                                ILGenerator.Emit(OpCodes.Stloc, unionLocal);
                            }

                            var valueLocal = ILGenerator.DeclareLocal(outParameter);
                            var labelFail = ILGenerator.DefineLabel();
                            var labelDone = ILGenerator.DefineLabel();

                            ILGenerator.Emit(OpCodes.Ldloca, unionLocal);
                            ILGenerator.Emit(OpCodes.Ldloca, valueLocal);
                            ILGenerator.Emit(OpCodes.Call, tryGetMethod);
                            ILGenerator.Emit(OpCodes.Brfalse, labelFail);

                            EmitPatternDesignator(declarationPattern.Designator, valueLocal, scope);
                            ILGenerator.Emit(OpCodes.Ldc_I4_1);
                            ILGenerator.Emit(OpCodes.Br, labelDone);

                            ILGenerator.MarkLabel(labelFail);
                            ILGenerator.Emit(OpCodes.Ldc_I4_0);
                            ILGenerator.MarkLabel(labelDone);
                            return;
                        }
                    }

                    EnsureObjectOnStack(ref inputType);

                    var unionCarrierLocal = scrutineeLocal2 ?? ILGenerator.DeclareLocal(unionClrType);
                    var valueLocal2 = ILGenerator.DeclareLocal(outParameter);
                    var labelSuccess = ILGenerator.DefineLabel();
                    var labelTryGetFail = ILGenerator.DefineLabel();
                    var labelTryGetDone = ILGenerator.DefineLabel();

                    ILGenerator.Emit(OpCodes.Isinst, unionClrType);
                    ILGenerator.Emit(OpCodes.Dup);
                    ILGenerator.Emit(OpCodes.Brtrue, labelSuccess);
                    ILGenerator.Emit(OpCodes.Pop);
                    ILGenerator.Emit(OpCodes.Ldc_I4_0);
                    ILGenerator.Emit(OpCodes.Br, labelTryGetDone);

                    ILGenerator.MarkLabel(labelSuccess);
                    ILGenerator.Emit(unionClrType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, unionClrType);
                    ILGenerator.Emit(OpCodes.Stloc, unionCarrierLocal);

                    ILGenerator.Emit(unionClrType.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, unionCarrierLocal);
                    ILGenerator.Emit(OpCodes.Ldloca, valueLocal2);
                    ILGenerator.Emit(unionClrType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, tryGetMethod);
                    ILGenerator.Emit(OpCodes.Brfalse, labelTryGetFail);

                    EmitPatternDesignator(declarationPattern.Designator, valueLocal2, scope);
                    ILGenerator.Emit(OpCodes.Ldc_I4_1);
                    ILGenerator.Emit(OpCodes.Br, labelTryGetDone);

                    ILGenerator.MarkLabel(labelTryGetFail);
                    ILGenerator.Emit(OpCodes.Ldc_I4_0);
                    ILGenerator.MarkLabel(labelTryGetDone);
                    return;
                }

                if (unionType.TypeKind == TypeKind.Class)
                {
                    EmitClassUnionValuePattern(inputNamedType, ResolveClrType(inputType), scrutineeLocal2, () => EmitPattern(
                        declarationPattern,
                        Compilation.GetSpecialType(SpecialType.System_Object)!,
                        scope,
                        scrutineeLocal2: null));
                    return;
                }
            }

            // Fast path: if the scrutinee is already exactly the declared type, bind directly
            // without boxing or isinst/unbox.any.
            if (inputType.TypeKind != TypeKind.Error)
            {
                var inputClr = ResolveClrType(inputType);
                inputClr = Generator.InstantiateType(inputClr);
                var declaredClr = Generator.InstantiateType(clrType);

                if (ClrTypesMatch(inputClr, declaredClr) &&
                    (!inputType.IsNullable || typeSymbol.IsNullable))
                {
                    EmitDesignationFromStack(declarationPattern.Designator, scope);

                    ILGenerator.Emit(OpCodes.Ldc_I4_1);
                    return;
                }

                if (ClrTypesMatch(inputClr, declaredClr) &&
                    inputType.IsNullable &&
                    !typeSymbol.IsNullable &&
                    inputType.GetNonNullableType() is ITypeParameterSymbol &&
                    typeSymbol is ITypeParameterSymbol)
                {
                    var valueLocal = SpillScrutineeToLocal(inputType);
                    var labelNull = ILGenerator.DefineLabel();
                    var labelDone = ILGenerator.DefineLabel();

                    ILGenerator.Emit(OpCodes.Ldloc, valueLocal);
                    ILGenerator.Emit(OpCodes.Box, inputClr);
                    ILGenerator.Emit(OpCodes.Brfalse, labelNull);
                    ILGenerator.Emit(OpCodes.Ldloc, valueLocal);
                    EmitDesignationFromStack(declarationPattern.Designator, scope);
                    ILGenerator.Emit(OpCodes.Ldc_I4_1);
                    ILGenerator.Emit(OpCodes.Br, labelDone);

                    ILGenerator.MarkLabel(labelNull);
                    ILGenerator.Emit(OpCodes.Ldc_I4_0);
                    ILGenerator.MarkLabel(labelDone);
                    return;
                }
            }

            // General case: use object semantics
            EnsureObjectOnStack(ref inputType);

            var isReferencePattern = IsKnownReferenceType(typeSymbol);
            if (!isReferencePattern && clrType.IsGenericParameter)
            {
                var attributes = clrType.GenericParameterAttributes;
                if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                    isReferencePattern = true;
            }

            if (!isReferencePattern)
            {
                var labelSuccess = ILGenerator.DefineLabel();
                var labelDone = ILGenerator.DefineLabel();
                var requiresUnbox = clrType.IsValueType || (clrType.IsGenericParameter && !isReferencePattern);

                ILGenerator.Emit(OpCodes.Isinst, clrType);
                ILGenerator.Emit(OpCodes.Dup);
                ILGenerator.Emit(OpCodes.Brtrue, labelSuccess);
                ILGenerator.Emit(OpCodes.Pop);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Br, labelDone);

                ILGenerator.MarkLabel(labelSuccess);
                ILGenerator.Emit(requiresUnbox ? OpCodes.Unbox_Any : OpCodes.Castclass, clrType);

                EmitDesignationFromStack(declarationPattern.Designator, scope);

                ILGenerator.Emit(OpCodes.Ldc_I4_1);
                ILGenerator.MarkLabel(labelDone);
            }
            else
            {
                ILGenerator.Emit(OpCodes.Isinst, clrType); // cast or null
                if (declarationPattern.Designator is not BoundDiscardDesignator)
                {
                    ILGenerator.Emit(OpCodes.Dup);
                    EmitDesignationFromStack(declarationPattern.Designator, scope);
                }
                ILGenerator.Emit(OpCodes.Ldnull);
                ILGenerator.Emit(OpCodes.Cgt_Un); // not-null
            }

            return;
        }

        if (pattern is BoundCasePattern casePattern)
        {
            var tryGetMethod = ResolveCasePatternTryGetMethod(casePattern, out var unionClrType, out var caseClrType);

            // Fast path: if the scrutinee is already the DU value type, avoid boxing/isinst/unbox.any.
            // This is the common case for matching directly over a DU-typed value.
            if (inputType.TypeKind != TypeKind.Error)
            {
                var inputClr = Generator.InstantiateType(ResolveClrType(inputType));
                if (unionClrType.IsValueType && ClrTypesMatch(inputClr, unionClrType))
                {
                    IILocal unionLocal2;
                    if (scrutineeLocal2 is not null)
                    {
                        unionLocal2 = scrutineeLocal2;
                        ILGenerator.Emit(OpCodes.Pop);
                    }
                    else
                    {
                        unionLocal2 = ILGenerator.DeclareLocal(unionClrType);
                        ILGenerator.Emit(OpCodes.Stloc, unionLocal2);
                    }
                    var caseLocal2 = ILGenerator.DeclareLocal(caseClrType);

                    var labelFail2 = ILGenerator.DefineLabel();
                    var labelDone2 = ILGenerator.DefineLabel();

                    ILGenerator.Emit(OpCodes.Ldloca, caseLocal2);
                    ILGenerator.Emit(OpCodes.Initobj, caseClrType);

                    ILGenerator.Emit(OpCodes.Ldloca, unionLocal2);
                    ILGenerator.Emit(OpCodes.Ldloca, caseLocal2);
                    ILGenerator.Emit(OpCodes.Call, tryGetMethod);
                    ILGenerator.Emit(OpCodes.Brfalse, labelFail2);

                    var parameterCount2 = Math.Min(
                        casePattern.CaseSymbol.ConstructorParameters.Length,
                        casePattern.Arguments.Length);

                    for (var i = 0; i < parameterCount2; i++)
                    {
                        var parameter = casePattern.CaseSymbol.ConstructorParameters[i];
                        var propertyName = GetCasePropertyName(parameter.Name);

                        var propertySymbol = casePattern.CaseSymbol
                            .GetMembers(propertyName)
                            .OfType<IPropertySymbol>()
                            .FirstOrDefault();

                        if (propertySymbol?.GetMethod is null)
                        {
                            ILGenerator.Emit(OpCodes.Br, labelFail2);
                            break;
                        }

                        ILGenerator.Emit(OpCodes.Ldloca, caseLocal2);
                        ILGenerator.Emit(OpCodes.Call, GetMethodInfo(propertySymbol.GetMethod));

                        // IMPORTANT: do not pre-box; nested patterns decide.
                        EmitPatternTestBranchFalse(casePattern.Arguments[i], propertySymbol.Type, scope, labelFail2, null);
                    }

                    EmitPatternDesignator(casePattern.Designator, caseLocal2, scope);
                    ILGenerator.Emit(OpCodes.Ldc_I4_1);
                    ILGenerator.Emit(OpCodes.Br, labelDone2);

                    ILGenerator.MarkLabel(labelFail2);
                    ILGenerator.Emit(OpCodes.Ldc_I4_0);

                    ILGenerator.MarkLabel(labelDone2);
                    return;
                }
            }

            // Boxed pipeline for case patterns uses Isinst => object input
            EnsureObjectOnStack(ref inputType);

            var unionLocal = scrutineeLocal2 ?? ILGenerator.DeclareLocal(unionClrType);
            var caseLocal = ILGenerator.DeclareLocal(caseClrType);

            var labelSuccess = ILGenerator.DefineLabel();
            var labelFail = ILGenerator.DefineLabel();
            var labelDone = ILGenerator.DefineLabel();

            ILGenerator.Emit(OpCodes.Isinst, unionClrType);
            ILGenerator.Emit(OpCodes.Dup);
            ILGenerator.Emit(OpCodes.Brtrue, labelSuccess);
            ILGenerator.Emit(OpCodes.Pop);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            ILGenerator.Emit(OpCodes.Br, labelDone);

            ILGenerator.MarkLabel(labelSuccess);
            ILGenerator.Emit(unionClrType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, unionClrType);
            ILGenerator.Emit(OpCodes.Stloc, unionLocal);

            ILGenerator.Emit(OpCodes.Ldloca, caseLocal);
            ILGenerator.Emit(OpCodes.Initobj, caseClrType);

            ILGenerator.Emit(unionClrType.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, unionLocal);
            ILGenerator.Emit(OpCodes.Ldloca, caseLocal);
            ILGenerator.Emit(OpCodes.Call, tryGetMethod);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);

            var parameterCount = Math.Min(
                casePattern.CaseSymbol.ConstructorParameters.Length,
                casePattern.Arguments.Length);

            for (var i = 0; i < parameterCount; i++)
            {
                var parameter = casePattern.CaseSymbol.ConstructorParameters[i];
                var propertyName = GetCasePropertyName(parameter.Name);

                var propertySymbol = casePattern.CaseSymbol
                    .GetMembers(propertyName)
                    .OfType<IPropertySymbol>()
                    .FirstOrDefault();

                if (propertySymbol?.GetMethod is null)
                {
                    ILGenerator.Emit(OpCodes.Br, labelFail);
                    break;
                }

                ILGenerator.Emit(caseClrType.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, caseLocal);
                ILGenerator.Emit(OpCodes.Call, GetMethodInfo(propertySymbol.GetMethod));

                // IMPORTANT: do not pre-box; nested patterns decide.
                EmitPatternTestBranchFalse(casePattern.Arguments[i], propertySymbol.Type, scope, labelFail, null);
            }

            EmitPatternDesignator(casePattern.Designator, caseLocal, scope);
            ILGenerator.Emit(OpCodes.Ldc_I4_1);
            ILGenerator.Emit(OpCodes.Br, labelDone);

            ILGenerator.MarkLabel(labelFail);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);

            ILGenerator.MarkLabel(labelDone);
            return;
        }

        if (pattern is BoundUnaryPattern unaryPattern)
        {
            // Preserve scrutinee without forcing object boxing
            var scrutineeLocal = SpillScrutineeToLocal(inputType);

            ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
            EmitPattern(unaryPattern.Pattern, inputType, scope, scrutineeLocal2);

            if (unaryPattern.Kind == BoundUnaryPatternKind.Not)
            {
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Ceq);
                return;
            }

            throw new InvalidOperationException($"Unexpected bound unary pattern kind '{unaryPattern.Kind}'.");
        }

        if (pattern is BoundBinaryPattern binaryPattern)
        {
            var scrutineeLocal = SpillScrutineeToLocal(inputType);

            var labelFail = ILGenerator.DefineLabel();
            var labelDone = ILGenerator.DefineLabel();

            if (binaryPattern.Kind == BoundPatternKind.And)
            {
                ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
                EmitPattern(binaryPattern.Left, inputType, scope, scrutineeLocal2);
                ILGenerator.Emit(OpCodes.Brfalse, labelFail);

                ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
                EmitPattern(binaryPattern.Right, inputType, scope, scrutineeLocal2);
                ILGenerator.Emit(OpCodes.Brfalse, labelFail);

                ILGenerator.Emit(OpCodes.Ldc_I4_1);
                ILGenerator.Emit(OpCodes.Br, labelDone);

                ILGenerator.MarkLabel(labelFail);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);

                ILGenerator.MarkLabel(labelDone);
                return;
            }

            if (binaryPattern.Kind == BoundPatternKind.Or)
            {
                var labelTrue = ILGenerator.DefineLabel();

                ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
                EmitPattern(binaryPattern.Left, inputType, scope, scrutineeLocal2);
                ILGenerator.Emit(OpCodes.Brtrue, labelTrue);

                ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
                EmitPattern(binaryPattern.Right, inputType, scope, scrutineeLocal2);
                ILGenerator.Emit(OpCodes.Brtrue, labelTrue);

                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Br, labelDone);

                ILGenerator.MarkLabel(labelTrue);
                ILGenerator.Emit(OpCodes.Ldc_I4_1);

                ILGenerator.MarkLabel(labelDone);
                return;
            }

            throw new InvalidOperationException($"Unexpected bound binary pattern kind '{binaryPattern.Kind}'.");
        }

        if (pattern is BoundPositionalPattern tuplePattern)
        {
            var isSequencePattern = tuplePattern.IsSequence;
            if (isSequencePattern)
                inputType = inputType.GetNonNullableType();

            var positionalInputType = tuplePattern.Type;
            var recoversSequenceTypeFromObject =
                isSequencePattern &&
                inputType.SpecialType == SpecialType.System_Object &&
                positionalInputType.TypeKind != TypeKind.Error &&
                !TypesMatch(inputType, positionalInputType) &&
                (positionalInputType.SpecialType == SpecialType.System_String ||
                    positionalInputType is IArrayTypeSymbol ||
                    TryGetIndexableCollectionAccess(positionalInputType, out _));

            if (recoversSequenceTypeFromObject)
            {
                var positionalClrType = ResolveClrType(positionalInputType);
                var labelHasInput = ILGenerator.DefineLabel();
                var labelRecovered = ILGenerator.DefineLabel();

                ILGenerator.Emit(OpCodes.Isinst, positionalClrType);
                ILGenerator.Emit(OpCodes.Dup);
                ILGenerator.Emit(OpCodes.Brtrue, labelHasInput);
                ILGenerator.Emit(OpCodes.Pop);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Br, labelRecovered);

                ILGenerator.MarkLabel(labelHasInput);
                ILGenerator.Emit(
                    positionalClrType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass,
                    positionalClrType);
                EmitPattern(tuplePattern, positionalInputType, scope, scrutineeLocal2);

                ILGenerator.MarkLabel(labelRecovered);
                return;
            }

            IILocal? matchedInputLocal = null;
            if (tuplePattern.Designator is not null && inputType.TypeKind != TypeKind.Error)
            {
                matchedInputLocal = ILGenerator.DeclareLocal(ResolveClrType(inputType));
                ILGenerator.Emit(OpCodes.Stloc, matchedInputLocal);
                ILGenerator.Emit(OpCodes.Ldloc, matchedInputLocal);
            }

            if (isSequencePattern && inputType.SpecialType == SpecialType.System_String)
            {
                EmitStringCollectionPattern(tuplePattern, scope);
                return;
            }

            if (isSequencePattern && inputType is IArrayTypeSymbol arrayType)
            {
                EmitArrayCollectionPattern(tuplePattern, arrayType, scope);
                return;
            }

            if (isSequencePattern &&
                TryGetIndexableCollectionAccess(inputType, out var indexableAccess))
            {
                EmitIndexableCollectionPattern(tuplePattern, indexableAccess, scope);
                return;
            }

            if (tuplePattern.RestIndex >= 0)
            {
                ILGenerator.Emit(OpCodes.Pop);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                return;
            }

            EnsureObjectOnStack(ref inputType);

            var tupleInterfaceType = Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.ITuple")
                ?? throw new InvalidOperationException("Unable to resolve runtime type for System.Runtime.CompilerServices.ITuple.");

            var lengthGetter = tupleInterfaceType.GetProperty("Length")?.GetMethod;
            var itemGetter = tupleInterfaceType.GetProperty("Item")?.GetMethod;

            if (lengthGetter is null || itemGetter is null)
                throw new NotSupportedException("System.Runtime.CompilerServices.ITuple is required to match tuple patterns.");

            var tupleLocal = ILGenerator.DeclareLocal(tupleInterfaceType);
            var labelFail = ILGenerator.DefineLabel();
            var labelDone = ILGenerator.DefineLabel();

            ILGenerator.Emit(OpCodes.Isinst, tupleInterfaceType);
            ILGenerator.Emit(OpCodes.Stloc, tupleLocal);
            ILGenerator.Emit(OpCodes.Ldloc, tupleLocal);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);

            ILGenerator.Emit(OpCodes.Ldloc, tupleLocal);
            ILGenerator.Emit(OpCodes.Callvirt, lengthGetter);
            ILGenerator.Emit(OpCodes.Ldc_I4, tuplePattern.Elements.Length);
            ILGenerator.Emit(OpCodes.Bne_Un, labelFail);

            for (var i = 0; i < tuplePattern.Elements.Length; i++)
            {
                ILGenerator.Emit(OpCodes.Ldloc, tupleLocal);
                ILGenerator.Emit(OpCodes.Ldc_I4, i);
                ILGenerator.Emit(OpCodes.Callvirt, itemGetter); // object

                EmitPattern(tuplePattern.Elements[i], Compilation.GetSpecialType(SpecialType.System_Object), scope, scrutineeLocal2);
                ILGenerator.Emit(OpCodes.Brfalse, labelFail);
            }

            if (matchedInputLocal is not null)
                EmitPatternDesignator(tuplePattern.Designator, matchedInputLocal, scope);

            ILGenerator.Emit(OpCodes.Ldc_I4_1);
            ILGenerator.Emit(OpCodes.Br, labelDone);

            ILGenerator.MarkLabel(labelFail);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);

            ILGenerator.MarkLabel(labelDone);
            return;
        }

        if (pattern is BoundDeconstructPattern deconstructPattern)
        {
            EmitDeconstructPattern(deconstructPattern, inputType, scope);
            return;
        }

        if (pattern is BoundPropertyPattern propertyPattern)
        {
            EmitPropertyPattern(propertyPattern, inputType, scope, scrutineeLocal2);
            return;
        }

        if (pattern is BoundDictionaryPattern dictionaryPattern)
        {
            EmitDictionaryPattern(dictionaryPattern, inputType.GetNonNullableType(), scope, scrutineeLocal2);
            return;
        }

        throw new InvalidOperationException($"Unexpected bound pattern type '{pattern.GetType().Name}'.");
    }

    private void EmitArrayCollectionPattern(BoundPositionalPattern pattern, IArrayTypeSymbol arrayType, Generator scope)
    {
        var arrayClrType = ResolveClrType(arrayType);
        var arrayLocal = ILGenerator.DeclareLocal(arrayClrType);
        var lengthLocal = ILGenerator.DeclareLocal(typeof(int));
        var labelFail = ILGenerator.DefineLabel();
        var labelDone = ILGenerator.DefineLabel();
        var elementType = arrayType.ElementType;
        var restIndex = pattern.RestIndex;
        var hasRest = restIndex >= 0;

        ILGenerator.Emit(OpCodes.Stloc, arrayLocal);

        ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
        ILGenerator.Emit(OpCodes.Brfalse, labelFail);

        ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
        ILGenerator.Emit(OpCodes.Ldlen);
        ILGenerator.Emit(OpCodes.Conv_I4);
        ILGenerator.Emit(OpCodes.Stloc, lengthLocal);

        var fixedWidth = GetSequenceFixedWidth(pattern);
        if (!hasRest)
        {
            ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
            ILGenerator.Emit(OpCodes.Ldc_I4, fixedWidth);
            ILGenerator.Emit(OpCodes.Bne_Un, labelFail);
        }
        else
        {
            ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
            ILGenerator.Emit(OpCodes.Ldc_I4, fixedWidth);
            ILGenerator.Emit(OpCodes.Blt, labelFail);
        }

        for (var i = 0; i < pattern.Elements.Length; i++)
        {
            var elementPattern = pattern.Elements[i];
            if (elementPattern is BoundDiscardPattern)
                continue;

            if (pattern.ElementKinds[i] == BoundPositionalPattern.SequenceElementKind.RestSegment)
            {
                var restLengthLocal = ILGenerator.DeclareLocal(typeof(int));
                ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
                ILGenerator.Emit(OpCodes.Ldc_I4, fixedWidth);
                ILGenerator.Emit(OpCodes.Sub);
                ILGenerator.Emit(OpCodes.Stloc, restLengthLocal);

                var restArrayType = Compilation.CreateArrayTypeSymbol(elementType);
                var restArrayLocal = EmitArraySlice(arrayLocal, elementType, GetSequencePrefixWidth(pattern, i), restLengthLocal);
                var targetType = GetSequencePatternInputType(elementPattern, restArrayType);
                var inputLocal = MaterializeSequencePatternInput(restArrayLocal, restArrayType, targetType);
                ILGenerator.Emit(OpCodes.Ldloc, inputLocal);
                EmitPatternTestBranchFalse(elementPattern, targetType, scope, labelFail);
                continue;
            }

            if (pattern.ElementKinds[i] == BoundPositionalPattern.SequenceElementKind.FixedSegment)
            {
                var segmentArrayType = Compilation.CreateArrayTypeSymbol(elementType);
                var segmentArrayLocal = EmitArraySlice(
                    arrayLocal,
                    elementType,
                    GetSequenceElementStartIndex(pattern, i, lengthLocal),
                    pattern.ElementWidths[i]);
                var targetType = GetSequencePatternInputType(elementPattern, segmentArrayType);
                var inputLocal = MaterializeSequencePatternInput(segmentArrayLocal, segmentArrayType, targetType);
                ILGenerator.Emit(OpCodes.Ldloc, inputLocal);
                EmitPatternTestBranchFalse(elementPattern, targetType, scope, labelFail);
                continue;
            }

            ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
            EmitLoadIndex(GetSequenceElementStartIndex(pattern, i, lengthLocal));
            EmitLoadElement(elementType);
            EmitPatternTestBranchFalse(elementPattern, elementType, scope, labelFail);
        }

        EmitPatternDesignator(pattern.Designator, arrayLocal, scope);
        ILGenerator.Emit(OpCodes.Ldc_I4_1);
        ILGenerator.Emit(OpCodes.Br, labelDone);

        ILGenerator.MarkLabel(labelFail);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);

        ILGenerator.MarkLabel(labelDone);
    }

    private void EmitIndexableCollectionPattern(
        BoundPositionalPattern pattern,
        (INamedTypeSymbol InterfaceType, IMethodSymbol CountGetter, IMethodSymbol IndexerGetter, ITypeSymbol ElementType) access,
        Generator scope)
    {
        var interfaceClrType = ResolveClrType(access.InterfaceType);
        var collectionLocal = ILGenerator.DeclareLocal(interfaceClrType);
        var lengthLocal = ILGenerator.DeclareLocal(typeof(int));
        var indexLocal = ILGenerator.DeclareLocal(typeof(int));

        var elementType = access.ElementType;
        var arrayType = (IArrayTypeSymbol)Compilation.CreateArrayTypeSymbol(elementType);
        var arrayClrType = ResolveClrType(arrayType);
        var arrayLocal = ILGenerator.DeclareLocal(arrayClrType);
        var nullCollectionLabel = ILGenerator.DefineLabel();
        var doneLabel = ILGenerator.DefineLabel();

        ILGenerator.Emit(OpCodes.Stloc, collectionLocal);

        ILGenerator.Emit(OpCodes.Ldloc, collectionLocal);
        ILGenerator.Emit(OpCodes.Brfalse, nullCollectionLabel);

        ILGenerator.Emit(OpCodes.Ldloc, collectionLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(access.CountGetter));
        ILGenerator.Emit(OpCodes.Stloc, lengthLocal);

        ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
        ILGenerator.Emit(OpCodes.Newarr, ResolveClrType(elementType));
        ILGenerator.Emit(OpCodes.Stloc, arrayLocal);

        var loopStart = ILGenerator.DefineLabel();
        var loopDone = ILGenerator.DefineLabel();

        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Stloc, indexLocal);

        ILGenerator.MarkLabel(loopStart);
        ILGenerator.Emit(OpCodes.Ldloc, indexLocal);
        ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
        ILGenerator.Emit(OpCodes.Bge, loopDone);

        ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
        ILGenerator.Emit(OpCodes.Ldloc, indexLocal);
        ILGenerator.Emit(OpCodes.Ldloc, collectionLocal);
        ILGenerator.Emit(OpCodes.Ldloc, indexLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(access.IndexerGetter));
        EmitStoreElement(elementType);

        ILGenerator.Emit(OpCodes.Ldloc, indexLocal);
        ILGenerator.Emit(OpCodes.Ldc_I4_1);
        ILGenerator.Emit(OpCodes.Add);
        ILGenerator.Emit(OpCodes.Stloc, indexLocal);
        ILGenerator.Emit(OpCodes.Br, loopStart);

        ILGenerator.MarkLabel(loopDone);
        ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
        var arrayMatchPattern = pattern.Designator is null
            ? pattern
            : new BoundPositionalPattern(
                pattern.Type,
                pattern.Elements,
                designator: null,
                reason: pattern.Reason,
                restIndex: pattern.RestIndex,
                elementWidths: pattern.ElementWidths,
                elementKinds: pattern.ElementKinds,
                isSequence: pattern.IsSequence);
        EmitArrayCollectionPattern(arrayMatchPattern, arrayType, scope);

        if (pattern.Designator is not null)
        {
            ILGenerator.Emit(OpCodes.Dup);
            ILGenerator.Emit(OpCodes.Brfalse, doneLabel);
            ILGenerator.Emit(OpCodes.Pop);
            EmitPatternDesignator(pattern.Designator, collectionLocal, scope);
            ILGenerator.Emit(OpCodes.Ldc_I4_1);
        }

        ILGenerator.Emit(OpCodes.Br, doneLabel);

        ILGenerator.MarkLabel(nullCollectionLabel);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.MarkLabel(doneLabel);
    }

    private void EmitStringCollectionPattern(BoundPositionalPattern pattern, Generator scope)
    {
        var stringLocal = ILGenerator.DeclareLocal(typeof(string));
        var lengthLocal = ILGenerator.DeclareLocal(typeof(int));
        var labelFail = ILGenerator.DefineLabel();
        var labelDone = ILGenerator.DefineLabel();
        var stringType = Compilation.GetSpecialType(SpecialType.System_String);
        var charType = Compilation.GetSpecialType(SpecialType.System_Char);
        var restIndex = pattern.RestIndex;
        var hasRest = restIndex >= 0;
        var fixedWidth = GetSequenceFixedWidth(pattern);

        ILGenerator.Emit(OpCodes.Stloc, stringLocal);
        ILGenerator.Emit(OpCodes.Ldloc, stringLocal);
        ILGenerator.Emit(OpCodes.Brfalse, labelFail);
        ILGenerator.Emit(OpCodes.Ldloc, stringLocal);
        ILGenerator.Emit(OpCodes.Callvirt, typeof(string).GetProperty(nameof(string.Length))!.GetMethod!);
        ILGenerator.Emit(OpCodes.Stloc, lengthLocal);

        ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
        ILGenerator.Emit(OpCodes.Ldc_I4, fixedWidth);
        ILGenerator.Emit(hasRest ? OpCodes.Blt : OpCodes.Bne_Un, labelFail);

        for (var i = 0; i < pattern.Elements.Length; i++)
        {
            var elementPattern = pattern.Elements[i];
            if (elementPattern is BoundDiscardPattern)
                continue;

            if (pattern.ElementKinds[i] == BoundPositionalPattern.SequenceElementKind.RestSegment)
            {
                var restLengthLocal = ILGenerator.DeclareLocal(typeof(int));
                ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
                ILGenerator.Emit(OpCodes.Ldc_I4, fixedWidth);
                ILGenerator.Emit(OpCodes.Sub);
                ILGenerator.Emit(OpCodes.Stloc, restLengthLocal);

                var restLocal = EmitStringSlice(stringLocal, GetSequencePrefixWidth(pattern, i), restLengthLocal);
                ILGenerator.Emit(OpCodes.Ldloc, restLocal);
                EmitPatternTestBranchFalse(elementPattern, stringType, scope, labelFail);
                continue;
            }

            if (pattern.ElementKinds[i] == BoundPositionalPattern.SequenceElementKind.FixedSegment)
            {
                var sliceLocal = EmitStringSlice(
                    stringLocal,
                    GetSequenceElementStartIndex(pattern, i, lengthLocal),
                    pattern.ElementWidths[i]);
                ILGenerator.Emit(OpCodes.Ldloc, sliceLocal);
                EmitPatternTestBranchFalse(elementPattern, stringType, scope, labelFail);
                continue;
            }

            ILGenerator.Emit(OpCodes.Ldloc, stringLocal);
            EmitLoadIndex(GetSequenceElementStartIndex(pattern, i, lengthLocal));
            ILGenerator.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Chars")!.GetMethod!);
            EmitPatternTestBranchFalse(elementPattern, charType, scope, labelFail);
        }

        EmitPatternDesignator(pattern.Designator, stringLocal, scope);
        ILGenerator.Emit(OpCodes.Ldc_I4_1);
        ILGenerator.Emit(OpCodes.Br, labelDone);

        ILGenerator.MarkLabel(labelFail);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.MarkLabel(labelDone);
    }

    private int GetSequenceFixedWidth(BoundPositionalPattern pattern)
    {
        var total = 0;
        for (var i = 0; i < pattern.Elements.Length; i++)
        {
            if (pattern.ElementKinds[i] == BoundPositionalPattern.SequenceElementKind.RestSegment)
                continue;

            total += pattern.ElementWidths[i];
        }

        return total;
    }

    private int GetSequencePrefixWidth(BoundPositionalPattern pattern, int index)
    {
        var total = 0;
        for (var i = 0; i < index; i++)
        {
            if (pattern.ElementKinds[i] == BoundPositionalPattern.SequenceElementKind.RestSegment)
                continue;

            total += pattern.ElementWidths[i];
        }

        return total;
    }

    private int GetSequenceSuffixWidth(BoundPositionalPattern pattern, int index)
    {
        var total = 0;
        for (var i = index; i < pattern.Elements.Length; i++)
        {
            if (pattern.ElementKinds[i] == BoundPositionalPattern.SequenceElementKind.RestSegment)
                continue;

            total += pattern.ElementWidths[i];
        }

        return total;
    }

    private object GetSequenceElementStartIndex(BoundPositionalPattern pattern, int index, IILocal totalLengthLocal)
    {
        if (pattern.RestIndex < 0 || index < pattern.RestIndex)
            return GetSequencePrefixWidth(pattern, index);

        var suffixWidth = GetSequenceSuffixWidth(pattern, index);
        var startLocal = ILGenerator.DeclareLocal(typeof(int));
        ILGenerator.Emit(OpCodes.Ldloc, totalLengthLocal);
        ILGenerator.Emit(OpCodes.Ldc_I4, suffixWidth);
        ILGenerator.Emit(OpCodes.Sub);
        ILGenerator.Emit(OpCodes.Stloc, startLocal);
        return startLocal;
    }

    private void EmitLoadIndex(object index)
    {
        if (index is int constant)
        {
            ILGenerator.Emit(OpCodes.Ldc_I4, constant);
            return;
        }

        ILGenerator.Emit(OpCodes.Ldloc, (IILocal)index);
    }

    private IILocal EmitArraySlice(IILocal arrayLocal, ITypeSymbol elementType, object startIndex, int length)
    {
        var lengthLocal = ILGenerator.DeclareLocal(typeof(int));
        ILGenerator.Emit(OpCodes.Ldc_I4, length);
        ILGenerator.Emit(OpCodes.Stloc, lengthLocal);
        return EmitArraySlice(arrayLocal, elementType, startIndex, lengthLocal);
    }

    private IILocal EmitArraySlice(IILocal arrayLocal, ITypeSymbol elementType, object startIndex, IILocal lengthLocal)
    {
        var sliceType = Compilation.CreateArrayTypeSymbol(elementType);
        var sliceLocal = ILGenerator.DeclareLocal(ResolveClrType(sliceType));

        ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
        ILGenerator.Emit(OpCodes.Newarr, ResolveClrType(elementType));
        ILGenerator.Emit(OpCodes.Stloc, sliceLocal);

        var arrayCopyMethod = typeof(Array).GetMethod(
            "Copy",
            [typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int)])
            ?? throw new InvalidOperationException("Unable to resolve System.Array.Copy overload.");

        ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
        EmitLoadIndex(startIndex);
        ILGenerator.Emit(OpCodes.Ldloc, sliceLocal);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
        ILGenerator.Emit(OpCodes.Call, arrayCopyMethod);

        return sliceLocal;
    }

    private IILocal EmitStringSlice(IILocal stringLocal, object startIndex, int length)
    {
        var lengthLocal = ILGenerator.DeclareLocal(typeof(int));
        ILGenerator.Emit(OpCodes.Ldc_I4, length);
        ILGenerator.Emit(OpCodes.Stloc, lengthLocal);
        return EmitStringSlice(stringLocal, startIndex, lengthLocal);
    }

    private IILocal EmitStringSlice(IILocal stringLocal, object startIndex, IILocal lengthLocal)
    {
        var sliceLocal = ILGenerator.DeclareLocal(typeof(string));
        ILGenerator.Emit(OpCodes.Ldloc, stringLocal);
        EmitLoadIndex(startIndex);
        ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
        ILGenerator.Emit(OpCodes.Callvirt, typeof(string).GetMethod(nameof(string.Substring), [typeof(int), typeof(int)])!);
        ILGenerator.Emit(OpCodes.Stloc, sliceLocal);
        return sliceLocal;
    }

    private void EmitPatternTestBranchFalse(
        BoundPattern pattern,
        ITypeSymbol inputType,
        Generator scope,
        ILLabel labelFail,
        IILocal? scrutineeLocal2 = null)
    {
        // `_` is always true: consume the value and move on
        if (pattern is BoundDiscardPattern)
        {
            ILGenerator.Emit(OpCodes.Pop);
            return;
        }

        // `T x` where T exactly matches the input type is always true: just bind x
        if (pattern is BoundDeclarationPattern dp)
        {
            var declared = dp.Type;

            if (inputType.TypeKind != TypeKind.Error && declared.TypeKind != TypeKind.Error)
            {
                if (declared.TypeKind == TypeKind.Null)
                {
                    EmitNullConstantPattern(inputType, ResolveClrType(inputType), scrutineeLocal2);
                    ILGenerator.Emit(OpCodes.Brfalse, labelFail);
                    return;
                }

                var inputClr = Generator.InstantiateType(ResolveClrType(inputType));
                var declaredClr = Generator.InstantiateType(ResolveClrType(declared));

                if (ClrTypesMatch(inputClr, declaredClr))
                {
                    EmitDesignationFromStack(dp.Designator, scope);

                    return;
                }
            }
        }

        // Otherwise, do the normal bool test + fail branch
        EmitPattern(pattern, inputType, scope, scrutineeLocal2);
        ILGenerator.Emit(OpCodes.Brfalse, labelFail);
    }

    private void EmitComparisonPattern(BoundComparisonPattern pattern, ITypeSymbol inputType, Generator scope, IILocal? scrutineeLocal2 = null)
    {
        // Stack on entry: <scrutinee>  (typed if PatternInput.Typed worked correctly)

        // If binder already marked it as error-ish, just evaluate to false safely.
        if (inputType.TypeKind == TypeKind.Error || pattern.Value.Type.TypeKind == TypeKind.Error)
        {
            ILGenerator.Emit(OpCodes.Pop);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        if (pattern.Operator is BoundComparisonPatternOperator.Equals or BoundComparisonPatternOperator.NotEquals)
        {
            EmitRuntimeValueConstantCompare(pattern.Value, inputType, scrutineeLocal2);
            if (pattern.Operator == BoundComparisonPatternOperator.NotEquals)
            {
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Ceq);
            }

            return;
        }

        // Spill scrutinee so we can reuse it (and handle nullable without duplicating stack games)
        var scrutineeClr = ResolveClrType(inputType);
        var scrutineeLocal = scrutineeLocal2 ?? ILGenerator.DeclareLocal(scrutineeClr);
        ILGenerator.Emit(OpCodes.Stloc, scrutineeLocal);

        // Nullable<T> path
        if (inputType.IsNullable)
        {
            var nullableClr = scrutineeClr;
            var underlyingType = inputType.GetNullableUnderlyingType();

            var hasValueGetter = GetNullableHasValueGetter(nullableClr);
            var getValueOrDefault = GetNullableGetValueOrDefault(nullableClr);

            var labelFalse = ILGenerator.DefineLabel();
            var labelDone = ILGenerator.DefineLabel();

            // if (!loc.HasValue) -> false
            ILGenerator.Emit(OpCodes.Ldloca_S, scrutineeLocal);
            ILGenerator.Emit(OpCodes.Call, hasValueGetter);
            ILGenerator.Emit(OpCodes.Brfalse, labelFalse);

            // left = loc.GetValueOrDefault()
            ILGenerator.Emit(OpCodes.Ldloca_S, scrutineeLocal);
            ILGenerator.Emit(OpCodes.Call, getValueOrDefault); // underlying T on stack

            // right = emit relational RHS as underlying type
            EmitRelationalRhs(pattern.Value, underlyingType, scope);

            // compare => int
            EmitCompare(underlyingType);

            // apply operator vs 0
            EmitComparisonOperator(pattern.Operator);

            ILGenerator.Emit(OpCodes.Br, labelDone);

            ILGenerator.MarkLabel(labelFalse);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);

            ILGenerator.MarkLabel(labelDone);
            return;
        }

        // Non-nullable path
        ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
        EmitRelationalRhs(pattern.Value, inputType, scope);
        EmitCompare(inputType);
        EmitComparisonOperator(pattern.Operator);
    }

    private void EmitRangePattern(BoundRangePattern pattern, ITypeSymbol inputType, Generator scope, IILocal? scrutineeLocal2 = null)
    {
        // Desugar `lo..hi`  → `(scrutinee >= lo) && (scrutinee <= hi)`
        // Desugar `lo..<hi` → `(scrutinee >= lo) && (scrutinee <  hi)`
        // Desugar `lo..`   → `scrutinee >= lo`
        // Desugar `..hi`   → `scrutinee <= hi`
        // Desugar `..<hi`  → `scrutinee < hi`

        if (inputType.TypeKind == TypeKind.Error)
        {
            ILGenerator.Emit(OpCodes.Pop);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        // Spill the scrutinee so we can use it for both bound comparisons.
        var scrutineeClr = ResolveClrType(inputType);
        var scrutineeLocal = scrutineeLocal2 ?? ILGenerator.DeclareLocal(scrutineeClr);
        ILGenerator.Emit(OpCodes.Stloc, scrutineeLocal);

        bool hasBoth = pattern.LowerBound is not null && pattern.UpperBound is not null;

        if (hasBoth)
        {
            var labelFail = ILGenerator.DefineLabel();
            var labelDone = ILGenerator.DefineLabel();

            // lower bound: scrutinee >= lo
            ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
            EmitRelationalRhs(pattern.LowerBound!, inputType, scope);
            EmitCompare(inputType);
            EmitComparisonOperator(BoundComparisonPatternOperator.GreaterThanOrEqual);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);

            // upper bound: scrutinee <= hi
            ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
            EmitRelationalRhs(pattern.UpperBound!, inputType, scope);
            EmitCompare(inputType);
            EmitComparisonOperator(pattern.IsUpperExclusive
                ? BoundComparisonPatternOperator.LessThan
                : BoundComparisonPatternOperator.LessThanOrEqual);
            ILGenerator.Emit(OpCodes.Br, labelDone);

            ILGenerator.MarkLabel(labelFail);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);

            ILGenerator.MarkLabel(labelDone);
        }
        else if (pattern.LowerBound is not null)
        {
            ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
            EmitRelationalRhs(pattern.LowerBound, inputType, scope);
            EmitCompare(inputType);
            EmitComparisonOperator(BoundComparisonPatternOperator.GreaterThanOrEqual);
        }
        else if (pattern.UpperBound is not null)
        {
            ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
            EmitRelationalRhs(pattern.UpperBound, inputType, scope);
            EmitCompare(inputType);
            EmitComparisonOperator(pattern.IsUpperExclusive
                ? BoundComparisonPatternOperator.LessThan
                : BoundComparisonPatternOperator.LessThanOrEqual);
        }
        else
        {
            // Degenerate `..` with no bounds — always true.
            ILGenerator.Emit(OpCodes.Ldc_I4_1);
        }
    }

    private void EmitRelationalRhs(BoundExpression rhs, ITypeSymbol targetType, Generator scope)
    {
        // Comparison patterns are intended to be constant expressions, but be defensive:
        // - literals are fine
        // - const fields (or other compile-time constants) are fine
        // - otherwise we fall back to evaluating the expression normally

        if (rhs is BoundConstantPattern cp)
        {
            EmitConstantForRelational(cp, targetType);
            return;
        }

        if (rhs is BoundLiteralExpression litExpr)
        {
            var value = litExpr.Value;
            if (value is null)
            {
                EmitDefaultValue(targetType);
                return;
            }

            EmitLiteralInTargetType(value, targetType, litExpr.Type);
            return;
        }

        if (rhs is BoundFieldAccess fieldAccess)
        {
            // Support `const` fields / literal-like symbols in comparison patterns.
            var constantValue = fieldAccess.Field.GetConstantValue();
            if (constantValue is not null)
            {
                EmitLiteralInTargetType(constantValue, targetType);
                return;
            }
        }

        // Fallback: evaluate the RHS expression and rely on binder + conversions.
        new ExpressionGenerator(scope, rhs).Emit();
    }
    private void EmitLiteralInTargetType(object value, ITypeSymbol targetType)
    {
        // Best-effort emission for compile-time constants that are not represented as LiteralTypeSymbol.
        // This is primarily used for const fields in patterns.
        if (targetType is LiteralTypeSymbol lt)
            targetType = lt.UnderlyingType;

        if (targetType.TypeKind == TypeKind.Enum)
            targetType = ((INamedTypeSymbol)targetType).EnumUnderlyingType
                ?? Compilation.GetSpecialType(SpecialType.System_Int32);

        switch (targetType.SpecialType)
        {
            case SpecialType.System_Boolean:
                ILGenerator.Emit(Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                return;

            case SpecialType.System_Char:
                ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToChar(value, CultureInfo.InvariantCulture));
                return;

            case SpecialType.System_SByte:
                ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToSByte(value, CultureInfo.InvariantCulture));
                return;

            case SpecialType.System_Byte:
                ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToByte(value, CultureInfo.InvariantCulture));
                return;

            case SpecialType.System_Int16:
                ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToInt16(value, CultureInfo.InvariantCulture));
                return;

            case SpecialType.System_UInt16:
                ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToUInt16(value, CultureInfo.InvariantCulture));
                return;

            case SpecialType.System_Int32:
                ILGenerator.Emit(OpCodes.Ldc_I4, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                return;

            case SpecialType.System_UInt32:
                unchecked { ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToUInt32(value, CultureInfo.InvariantCulture)); }
                return;

            case SpecialType.System_Int64:
                ILGenerator.Emit(OpCodes.Ldc_I8, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;

            case SpecialType.System_UInt64:
                unchecked { ILGenerator.Emit(OpCodes.Ldc_I8, (long)Convert.ToUInt64(value, CultureInfo.InvariantCulture)); }
                return;

            case SpecialType.System_Single:
                ILGenerator.Emit(OpCodes.Ldc_R4, Convert.ToSingle(value, CultureInfo.InvariantCulture));
                return;

            case SpecialType.System_Double:
                ILGenerator.Emit(OpCodes.Ldc_R8, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return;

            case SpecialType.System_String:
                ILGenerator.Emit(OpCodes.Ldstr, (string)value);
                return;
        }

        // Fallback: boxed constant (caller must compare via Equals)
        EmitLiteral(value);
        if (targetType.IsValueType)
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(targetType));
    }

    // ============================================
    // Property patterns
    // ============================================

    private void EmitDeconstructPattern(BoundDeconstructPattern deconstructPattern, ITypeSymbol inputType, Generator scope)
    {
        // Deconstruct patterns need reference semantics for null-checking and optional narrowed type checks.
        // If the scrutinee is already a reference type, don't force it into an `object` local.
        // Only box when necessary (value types / unconstrained type parameters).

        var labelFail = ILGenerator.DefineLabel();
        var labelDone = ILGenerator.DefineLabel();

        IILocal? objLocal = null;
        IILocal inputLocal;

        var requiresBoxing = RequiresValueTypeHandling(inputType) && inputType.TypeKind != TypeKind.Error;

        if (requiresBoxing)
        {
            // Box value-type scrutinee into object local.
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(inputType));
            objLocal = ILGenerator.DeclareLocal(typeof(object));
            ILGenerator.Emit(OpCodes.Stloc, objLocal);
            inputLocal = objLocal;
        }
        else
        {
            // Keep the scrutinee in its native reference type.
            var inputClr = ResolveClrType(inputType);
            inputLocal = ILGenerator.DeclareLocal(inputClr);
            ILGenerator.Emit(OpCodes.Stloc, inputLocal);
        }

        // Null fails for deconstruct patterns
        ILGenerator.Emit(OpCodes.Ldloc, inputLocal);
        ILGenerator.Emit(OpCodes.Brfalse, labelFail);

        // Optional narrowed type-test
        if (deconstructPattern.NarrowedType is not null)
        {
            if (!requiresBoxing)
            {
                // Create object view lazily for isinst.
                objLocal = ILGenerator.DeclareLocal(typeof(object));
                ILGenerator.Emit(OpCodes.Ldloc, inputLocal);
                ILGenerator.Emit(OpCodes.Stloc, objLocal);
            }

            var narrowedClrType = ResolveClrType(deconstructPattern.NarrowedType);
            ILGenerator.Emit(OpCodes.Ldloc, objLocal);
            ILGenerator.Emit(OpCodes.Isinst, narrowedClrType);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);
        }

        var receiverType = deconstructPattern.ReceiverType;
        var receiverClrType = ResolveClrType(receiverType);
        IILocal receiverLocal;

        // We need a receiver local typed as the deconstruct receiver type.
        // Prefer reusing the typed input local when possible.
        if (receiverClrType == typeof(object))
        {
            objLocal ??= ILGenerator.DeclareLocal(typeof(object));

            if (requiresBoxing)
            {
                // inputLocal already is object.
                receiverLocal = objLocal;
                if (!ReferenceEquals(objLocal, inputLocal))
                {
                    ILGenerator.Emit(OpCodes.Ldloc, inputLocal);
                    ILGenerator.Emit(OpCodes.Stloc, objLocal);
                }
            }
            else
            {
                // Store reference-typed input into object view.
                ILGenerator.Emit(OpCodes.Ldloc, inputLocal);
                ILGenerator.Emit(OpCodes.Stloc, objLocal);
                receiverLocal = objLocal;
            }
        }
        else if (receiverType.IsValueType)
        {
            // Need object view for Unbox_Any.
            objLocal ??= ILGenerator.DeclareLocal(typeof(object));
            if (!requiresBoxing)
            {
                ILGenerator.Emit(OpCodes.Ldloc, inputLocal);
                ILGenerator.Emit(OpCodes.Stloc, objLocal);
            }

            receiverLocal = ILGenerator.DeclareLocal(receiverClrType);
            ILGenerator.Emit(OpCodes.Ldloc, objLocal);
            ILGenerator.Emit(OpCodes.Unbox_Any, receiverClrType);
            ILGenerator.Emit(OpCodes.Stloc, receiverLocal);
        }
        else
        {
            // Reference receiver
            var inputClrType = ResolveClrType(inputType);

            if (!requiresBoxing && inputClrType == receiverClrType)
            {
                receiverLocal = inputLocal;
            }
            else
            {
                objLocal ??= ILGenerator.DeclareLocal(typeof(object));
                if (!requiresBoxing)
                {
                    ILGenerator.Emit(OpCodes.Ldloc, inputLocal);
                    ILGenerator.Emit(OpCodes.Stloc, objLocal);
                }

                receiverLocal = ILGenerator.DeclareLocal(receiverClrType);
                ILGenerator.Emit(OpCodes.Ldloc, objLocal);
                ILGenerator.Emit(OpCodes.Castclass, receiverClrType);
                ILGenerator.Emit(OpCodes.Stloc, receiverLocal);
            }
        }

        var parameters = deconstructPattern.DeconstructMethod.Parameters;
        var parameterOffset = deconstructPattern.DeconstructMethod.IsExtensionMethod ? 1 : 0;
        var parameterCount = parameters.Length - parameterOffset;
        var argumentLocals = new IILocal[parameterCount];

        for (var i = 0; i < parameterCount; i++)
        {
            var parameterClrType = ResolveClrType(parameters[i + parameterOffset].Type);
            argumentLocals[i] = ILGenerator.DeclareLocal(parameterClrType);
        }

        if (!deconstructPattern.DeconstructMethod.IsExtensionMethod && receiverType.IsValueType)
            ILGenerator.Emit(OpCodes.Ldloca, receiverLocal);
        else
            ILGenerator.Emit(OpCodes.Ldloc, receiverLocal);

        for (var i = 0; i < argumentLocals.Length; i++)
            ILGenerator.Emit(OpCodes.Ldloca, argumentLocals[i]);

        var callOpCode = deconstructPattern.DeconstructMethod.IsExtensionMethod
            ? OpCodes.Call
            : receiverType.IsValueType ? OpCodes.Call : OpCodes.Callvirt;
        ILGenerator.Emit(callOpCode, GetMethodInfo(deconstructPattern.DeconstructMethod));

        for (var i = 0; i < parameterCount; i++)
        {
            ILGenerator.Emit(OpCodes.Ldloc, argumentLocals[i]);
            EmitPatternTestBranchFalse(
                deconstructPattern.Arguments[i],
                parameters[i + parameterOffset].Type,
                scope,
                labelFail,
                scrutineeLocal2: null);
        }

        EmitPatternDesignator(deconstructPattern.Designator, receiverLocal, scope);
        ILGenerator.Emit(OpCodes.Ldc_I4_1);
        ILGenerator.Emit(OpCodes.Br, labelDone);

        ILGenerator.MarkLabel(labelFail);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);

        ILGenerator.MarkLabel(labelDone);
    }

    private void EmitPropertyPattern(BoundPropertyPattern propertyPattern, ITypeSymbol inputType, Generator scope, IILocal? local)
    {
        // Property patterns need reference semantics for null-checking and optional narrowed type checks.
        // If the scrutinee is already a reference type, don't force it into an `object` local.
        // Only box when necessary (value types / unconstrained type parameters).

        var labelFail = ILGenerator.DefineLabel();
        var labelDone = ILGenerator.DefineLabel();

        IILocal? objLocal = null;
        IILocal inputLocal;

        var requiresBoxing = RequiresValueTypeHandling(inputType) && inputType.TypeKind != TypeKind.Error;

        if (requiresBoxing)
        {
            // Box value-type scrutinee into object local.
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(inputType));
            objLocal = local ?? ILGenerator.DeclareLocal(typeof(object));
            ILGenerator.Emit(OpCodes.Stloc, objLocal);
            inputLocal = objLocal;
        }
        else
        {
            // Keep the scrutinee in its native reference type.
            var inputClr = ResolveClrType(inputType);
            inputLocal = local ?? ILGenerator.DeclareLocal(inputClr);
            ILGenerator.Emit(OpCodes.Stloc, inputLocal);
        }

        // Null fails for property patterns
        ILGenerator.Emit(OpCodes.Ldloc, inputLocal);
        ILGenerator.Emit(OpCodes.Brfalse, labelFail);

        // Optional narrowed type-test
        if (propertyPattern.NarrowedType is not null)
        {
            if (!requiresBoxing)
            {
                // Also keep an object-typed view when we need Isinst against a narrowed type.
                objLocal = local ?? ILGenerator.DeclareLocal(typeof(object));
                ILGenerator.Emit(OpCodes.Ldloc, inputLocal);
                ILGenerator.Emit(OpCodes.Stloc, objLocal);
            }

            var narrowedClrType = ResolveClrType(propertyPattern.NarrowedType);

            // Use isinst on the object-typed view; this avoids any generic/reference quirks.
            ILGenerator.Emit(OpCodes.Ldloc, objLocal);
            ILGenerator.Emit(OpCodes.Isinst, narrowedClrType);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);
        }

        if (propertyPattern.Properties.Length == 0)
        {
            ILGenerator.Emit(OpCodes.Ldc_I4_1);
            ILGenerator.Emit(OpCodes.Br, labelDone);

            ILGenerator.MarkLabel(labelFail);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);

            ILGenerator.MarkLabel(labelDone);
            return;
        }

        var lookupType = propertyPattern.ReceiverType;
        var lookupClrType = ResolveClrType(lookupType);

        // Build a typed receiver local for member access (cast only when needed).
        IILocal typedLocal;

        if (lookupClrType == typeof(object))
        {
            typedLocal = objLocal;
        }
        else
        {
            // If we already have the exact reference type, avoid an extra cast local.
            var inputClrType = ResolveClrType(inputType);

            if (!requiresBoxing && inputClrType == lookupClrType)
            {
                typedLocal = inputLocal;
            }
            else
            {
                typedLocal = ILGenerator.DeclareLocal(lookupClrType);
                ILGenerator.Emit(OpCodes.Ldloc, objLocal);
                ILGenerator.Emit(OpCodes.Castclass, lookupClrType);
                ILGenerator.Emit(OpCodes.Stloc, typedLocal);
            }
        }

        // If the property pattern has a top-level designation (e.g. `Type { ... } name`),
        // bind it to the (possibly cast) receiver value.
        if (propertyPattern.Designator is not null)
            EmitPatternDesignator(propertyPattern.Designator, typedLocal, scope);

        foreach (var sub in propertyPattern.Properties)
        {
            ILGenerator.Emit(OpCodes.Ldloc, typedLocal);

            switch (sub.Member)
            {
                case IPropertySymbol prop:
                    {
                        if (prop.GetMethod is null)
                        {
                            ILGenerator.Emit(OpCodes.Br, labelFail);
                            break;
                        }

                        var getter = GetMethodInfo(prop.GetMethod);
                        ILGenerator.Emit(OpCodes.Callvirt, getter);
                        break;
                    }

                case IFieldSymbol field:
                    {
                        var fieldInfo = GetField(field);
                        ILGenerator.Emit(OpCodes.Ldfld, fieldInfo);
                        break;
                    }

                default:
                    ILGenerator.Emit(OpCodes.Br, labelFail);
                    break;
            }

            // IMPORTANT: do not pre-box; nested pattern decides.
            EmitPattern(sub.Pattern, sub.Type, scope);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);
        }

        ILGenerator.Emit(OpCodes.Ldc_I4_1);
        ILGenerator.Emit(OpCodes.Br, labelDone);

        ILGenerator.MarkLabel(labelFail);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);

        ILGenerator.MarkLabel(labelDone);
    }

    private void EmitDictionaryPattern(BoundDictionaryPattern dictionaryPattern, ITypeSymbol inputType, Generator scope, IILocal? local)
    {
        var labelFail = ILGenerator.DefineLabel();
        var labelDone = ILGenerator.DefineLabel();

        var requiresBoxing = RequiresValueTypeHandling(inputType) && inputType.TypeKind != TypeKind.Error;
        IILocal inputLocal;
        IILocal? objectLocal = null;

        if (requiresBoxing)
        {
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(inputType));
            objectLocal = local ?? ILGenerator.DeclareLocal(typeof(object));
            ILGenerator.Emit(OpCodes.Stloc, objectLocal);
            inputLocal = objectLocal;
        }
        else
        {
            var inputClr = ResolveClrType(inputType);
            inputLocal = local ?? ILGenerator.DeclareLocal(inputClr);
            ILGenerator.Emit(OpCodes.Stloc, inputLocal);
        }

        ILGenerator.Emit(OpCodes.Ldloc, inputLocal);
        ILGenerator.Emit(OpCodes.Brfalse, labelFail);

        var receiverClrType = ResolveClrType(dictionaryPattern.ReceiverType);
        IILocal receiverLocal;

        if (!requiresBoxing && ClrTypesMatch(ResolveClrType(inputType), receiverClrType))
        {
            receiverLocal = inputLocal;
        }
        else
        {
            receiverLocal = ILGenerator.DeclareLocal(receiverClrType);

            if (requiresBoxing)
                ILGenerator.Emit(OpCodes.Ldloc, objectLocal!);
            else
                ILGenerator.Emit(OpCodes.Ldloc, inputLocal);

            ILGenerator.Emit(OpCodes.Castclass, receiverClrType);
            ILGenerator.Emit(OpCodes.Stloc, receiverLocal);
        }

        var tryGetValueMethod = dictionaryPattern.ReceiverType
            .GetMembers("TryGetValue")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(static method =>
                !method.IsStatic &&
                method.Parameters.Length == 2 &&
                method.Parameters[1].RefKind == RefKind.Out);

        if (tryGetValueMethod is null)
            throw new InvalidOperationException($"Missing TryGetValue on '{dictionaryPattern.ReceiverType}'.");

        var tryGetValueMethodInfo = GetMethodInfo(tryGetValueMethod);
        var valueClrType = ResolveClrType(dictionaryPattern.ValueType);

        foreach (var entry in dictionaryPattern.Entries)
        {
            var valueLocal = ILGenerator.DeclareLocal(valueClrType);
            ILGenerator.Emit(OpCodes.Ldloc, receiverLocal);
            EmitExpression(entry.Key, EmitContext.Value);
            ILGenerator.Emit(OpCodes.Ldloca, valueLocal);
            ILGenerator.Emit(OpCodes.Callvirt, tryGetValueMethodInfo);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);

            ILGenerator.Emit(OpCodes.Ldloc, valueLocal);
            EmitPattern(entry.Pattern, dictionaryPattern.ValueType, scope);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);
        }

        if (dictionaryPattern.Designator is not null)
            EmitPatternDesignator(dictionaryPattern.Designator, inputLocal, scope);

        ILGenerator.Emit(OpCodes.Ldc_I4_1);
        ILGenerator.Emit(OpCodes.Br, labelDone);

        ILGenerator.MarkLabel(labelFail);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);

        ILGenerator.MarkLabel(labelDone);
    }

    // ============================================
    // Constant patterns (typed, avoid boxing)
    // ============================================

    private void EmitConstantPattern(BoundConstantPattern constantPattern, ITypeSymbol inputType, IILocal? scrutineeLocal2)
    {
        if (constantPattern.Expression is BoundUnitExpression &&
            inputType.SpecialType == SpecialType.System_Unit)
        {
            ILGenerator.Emit(OpCodes.Pop);
            ILGenerator.Emit(OpCodes.Ldc_I4_1);
            return;
        }

        if (!IsNullConstantPattern(constantPattern) &&
            inputType.TryGetUnion() is IUnionSymbol { TypeKind: TypeKind.Class } &&
            inputType is INamedTypeSymbol classUnionType)
        {
            EmitClassUnionValuePattern(classUnionType, ResolveClrType(inputType), scrutineeLocal2, () =>
                EmitConstantPattern(
                    constantPattern,
                    Compilation.GetSpecialType(SpecialType.System_Object)!,
                    scrutineeLocal2: null));
            return;
        }

        // Runtime "value pattern" (e.g. identifier/member access) – compare by object.Equals.
        if (constantPattern.Expression is not null
            && !IsNullConstantExpression(constantPattern.Expression))
        {
            if (TryEmitCompileTimeConstantPattern(constantPattern.Expression, inputType, scrutineeLocal2))
                return;

            EmitRuntimeValueConstantCompare(constantPattern.Expression, inputType, scrutineeLocal2);
            return;
        }

        // Literal-backed constant pattern (fast path)
        var literal = constantPattern.LiteralType;
        /*if (literal is null)
        {
            // Defensive: binder should always provide either Expression or LiteralType.
            ILGenerator.Emit(OpCodes.Pop);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            return;
        }*/

        var value = literal?.ConstantValue;

        var scrutineeType = inputType;
        var scrutineeClr = ResolveClrType(scrutineeType);

        // NULL literal
        if (value is null)
        {
            EmitNullConstantPattern(scrutineeType, scrutineeClr, scrutineeLocal2);
            return;
        }

        // String fast path: compare directly via string.op_Equality.
        // This avoids object temp/null/isinst scaffolding for common string cases.
        if (scrutineeType.SpecialType == SpecialType.System_String && value is string stringValue)
        {
            ILGenerator.Emit(OpCodes.Ldstr, stringValue);
            var opEq = typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) })!;
            ILGenerator.Emit(OpCodes.Call, opEq);
            return;
        }

        // Nullable<T> value types
        if (scrutineeType.IsNullable && scrutineeType.GetNullableUnderlyingType()!.IsValueType)
        {
            EmitNullableValueConstantCompare(scrutineeType, value, literal);
            return;
        }

        // Non-nullable value types: fast typed compare, no boxing
        if (scrutineeType.IsValueType)
        {
            var loc = scrutineeLocal2 ?? ILGenerator.DeclareLocal(scrutineeClr);
            ILGenerator.Emit(OpCodes.Stloc, loc);
            ILGenerator.Emit(OpCodes.Ldloc, loc);

            EmitLiteralInTargetType(value, scrutineeType, literal);
            ILGenerator.Emit(OpCodes.Ceq);
            return;
        }

        // Reference types
        EmitReferenceConstantCompare(value, literal, scrutineeLocal2);
    }

    private bool TryEmitCompileTimeConstantPattern(BoundExpression expression, ITypeSymbol inputType, IILocal? scrutineeLocal2)
    {
        if (!TryGetCompileTimeConstantValue(expression, out var value, out var sourceType))
            return false;

        var scrutineeType = inputType;
        var scrutineeClr = ResolveClrType(scrutineeType);

        if (value is null)
        {
            EmitNullConstantPattern(scrutineeType, scrutineeClr, scrutineeLocal2);
            return true;
        }

        if (scrutineeType.SpecialType == SpecialType.System_String && value is string stringValue)
        {
            ILGenerator.Emit(OpCodes.Ldstr, stringValue);
            var opEq = typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) })!;
            ILGenerator.Emit(OpCodes.Call, opEq);
            return true;
        }

        if (scrutineeType.IsNullable && scrutineeType.GetNullableUnderlyingType()!.IsValueType)
        {
            EmitNullableValueConstantCompare(scrutineeType, value, sourceType);
            return true;
        }

        if (scrutineeType.IsValueType)
        {
            var loc = scrutineeLocal2 ?? ILGenerator.DeclareLocal(scrutineeClr);
            ILGenerator.Emit(OpCodes.Stloc, loc);
            ILGenerator.Emit(OpCodes.Ldloc, loc);

            EmitLiteralInTargetType(value, scrutineeType, sourceType);
            ILGenerator.Emit(OpCodes.Ceq);
            return true;
        }

        EmitReferenceConstantCompare(value, sourceType, scrutineeLocal2);
        return true;
    }

    private static bool TryGetCompileTimeConstantValue(BoundExpression expression, out object? value, out ITypeSymbol sourceType)
    {
        switch (expression)
        {
            case BoundFieldAccess fieldAccess when IsCompileTimeConstantField(fieldAccess.Field):
                value = fieldAccess.Field.GetConstantValue();
                sourceType = fieldAccess.Field.Type;
                return true;

            case BoundMemberAccessExpression { Member: IFieldSymbol field } when IsCompileTimeConstantField(field):
                value = field.GetConstantValue();
                sourceType = field.Type;
                return true;

            default:
                value = null;
                sourceType = null!;
                return false;
        }
    }

    private static bool IsCompileTimeConstantField(IFieldSymbol field)
        => field.IsConst || field.ContainingType?.TypeKind == TypeKind.Enum;

    private void EmitNullConstantPattern(ITypeSymbol scrutineeType, Type scrutineeClr, IILocal? scrutineeLocal2)
    {
        if (scrutineeType.TryGetUnion() is IUnionSymbol { TypeKind: TypeKind.Struct } &&
            scrutineeType is INamedTypeSymbol scrutineeNamedType)
        {
            var hasValueGetter = scrutineeNamedType
                .GetMembers("HasValue")
                .OfType<IPropertySymbol>()
                .Where(static property => property.GetMethod is not null)
                .Select(property => property.GetMethod!)
                .FirstOrDefault(method => SymbolEqualityComparer.Default.Equals(method.ContainingType, scrutineeNamedType))
                ?? scrutineeNamedType
                    .GetMembers("HasValue")
                    .OfType<IPropertySymbol>()
                    .Where(static property => property.GetMethod is not null)
                    .Select(property => property.GetMethod!)
                    .First();

            var unionLocal = scrutineeLocal2;
            if (unionLocal is null)
            {
                unionLocal = ILGenerator.DeclareLocal(scrutineeClr);
                ILGenerator.Emit(OpCodes.Stloc, unionLocal);
            }

            ILGenerator.Emit(OpCodes.Ldloca_S, unionLocal);
            ILGenerator.Emit(OpCodes.Call, GetMethodInfo(hasValueGetter));
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            ILGenerator.Emit(OpCodes.Ceq);
            return;
        }

        if (scrutineeType.TryGetUnion() is IUnionSymbol { TypeKind: TypeKind.Class } &&
            scrutineeType is INamedTypeSymbol classUnionType)
        {
            var valueGetter = classUnionType
                .GetMembers("Value")
                .OfType<IPropertySymbol>()
                .Where(static property => property.GetMethod is not null)
                .Select(property => property.GetMethod!)
                .FirstOrDefault(method => SymbolEqualityComparer.Default.Equals(method.ContainingType, classUnionType))
                ?? classUnionType
                    .GetMembers("Value")
                    .OfType<IPropertySymbol>()
                    .Where(static property => property.GetMethod is not null)
                    .Select(property => property.GetMethod!)
                    .First();

            var unionLocal = scrutineeLocal2;
            if (unionLocal is null)
            {
                unionLocal = ILGenerator.DeclareLocal(scrutineeClr);
                ILGenerator.Emit(OpCodes.Stloc, unionLocal);
            }

            var isNullLabel = ILGenerator.DefineLabel();
            var doneLabel = ILGenerator.DefineLabel();

            ILGenerator.Emit(OpCodes.Ldloc, unionLocal);
            ILGenerator.Emit(OpCodes.Brfalse, isNullLabel);
            ILGenerator.Emit(OpCodes.Ldloc, unionLocal);
            ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(valueGetter));
            ILGenerator.Emit(OpCodes.Ldnull);
            ILGenerator.Emit(OpCodes.Ceq);
            ILGenerator.Emit(OpCodes.Br, doneLabel);
            ILGenerator.MarkLabel(isNullLabel);
            ILGenerator.Emit(OpCodes.Ldc_I4_1);
            ILGenerator.MarkLabel(doneLabel);
            return;
        }

        if (scrutineeType.IsValueType && !scrutineeType.IsNullable)
        {
            if (scrutineeLocal2 is null)
                ILGenerator.Emit(OpCodes.Pop);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        if (scrutineeType.IsReferenceType)
        {
            ILGenerator.Emit(OpCodes.Ldnull);
            ILGenerator.Emit(OpCodes.Ceq);
            return;
        }

        var loc = scrutineeLocal2 ?? ILGenerator.DeclareLocal(scrutineeClr);
        ILGenerator.Emit(OpCodes.Stloc, loc);

        ILGenerator.Emit(OpCodes.Ldloca_S, loc);
        ILGenerator.Emit(OpCodes.Call, GetNullableHasValueGetter(scrutineeClr));
        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Ceq);
    }

    private void EmitReferenceConstantCompare(object value, ITypeSymbol sourceType, IILocal? local)
    {
        var obj = local ?? ILGenerator.DeclareLocal(typeof(object));
        ILGenerator.Emit(OpCodes.Stloc, obj);

        var notNull = ILGenerator.DefineLabel();
        var done = ILGenerator.DefineLabel();

        ILGenerator.Emit(OpCodes.Ldloc, obj);
        ILGenerator.Emit(OpCodes.Brtrue, notNull);

        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Br, done);

        ILGenerator.MarkLabel(notNull);

        if (value is string s)
        {
            ILGenerator.Emit(OpCodes.Ldloc, obj);
            ILGenerator.Emit(OpCodes.Isinst, typeof(string));
            ILGenerator.Emit(OpCodes.Ldstr, s);

            var opEq = typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) })!;
            ILGenerator.Emit(OpCodes.Call, opEq);

            ILGenerator.Emit(OpCodes.Br, done);
            ILGenerator.MarkLabel(done);
            return;
        }

        ILGenerator.Emit(OpCodes.Ldloc, obj);
        var underlyingSource = sourceType is LiteralTypeSymbol ls ? ls.UnderlyingType : sourceType;
        EmitConstantAsObject(underlyingSource, value);

        var equals = typeof(object).GetMethod(nameof(object.Equals), new[] { typeof(object) })!;
        ILGenerator.Emit(OpCodes.Callvirt, equals);

        ILGenerator.MarkLabel(done);
    }

    private void EmitNullableValueConstantCompare(ITypeSymbol nullableType, object value, ITypeSymbol sourceType)
    {
        var nullableClr = ResolveClrType(nullableType);
        var underlyingType = nullableType.GetNullableUnderlyingType();

        var loc = ILGenerator.DeclareLocal(nullableClr);
        ILGenerator.Emit(OpCodes.Stloc, loc);

        var hasValue = GetNullableHasValueGetter(nullableClr);
        var getValueOrDefault = GetNullableGetValueOrDefault(nullableClr);

        var has = ILGenerator.DefineLabel();
        var done = ILGenerator.DefineLabel();

        ILGenerator.Emit(OpCodes.Ldloca_S, loc);
        ILGenerator.Emit(OpCodes.Call, hasValue);
        ILGenerator.Emit(OpCodes.Brtrue, has);

        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Br, done);

        ILGenerator.MarkLabel(has);

        ILGenerator.Emit(OpCodes.Ldloca_S, loc);
        ILGenerator.Emit(OpCodes.Call, getValueOrDefault); // underlying T

        EmitLiteralInTargetType(value, underlyingType, sourceType);
        ILGenerator.Emit(OpCodes.Ceq);

        ILGenerator.MarkLabel(done);
    }

    private void EmitLiteralInTargetType(object value, ITypeSymbol targetType, ITypeSymbol sourceType)
    {
        // Unwrap literal wrapper types
        if (targetType is LiteralTypeSymbol lt)
            targetType = lt.UnderlyingType;

        var litUnderlying = sourceType is LiteralTypeSymbol ls ? ls.UnderlyingType : sourceType;

        // Enums compare via underlying integral type
        if (targetType.TypeKind == TypeKind.Enum)
            targetType = ((INamedTypeSymbol)targetType).EnumUnderlyingType
                ?? Compilation.GetSpecialType(SpecialType.System_Int32);

        if (litUnderlying.TypeKind == TypeKind.Enum)
            litUnderlying = ((INamedTypeSymbol)litUnderlying).EnumUnderlyingType
                ?? Compilation.GetSpecialType(SpecialType.System_Int32);

        switch (targetType.SpecialType)
        {
            case SpecialType.System_Boolean:
                ILGenerator.Emit((bool)value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                return;

            case SpecialType.System_Char:
                ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToChar(value));
                return;

            case SpecialType.System_SByte:
                ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToSByte(value));
                return;

            case SpecialType.System_Byte:
                ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToByte(value));
                return;

            case SpecialType.System_Int16:
                ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToInt16(value));
                return;

            case SpecialType.System_UInt16:
                ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToUInt16(value));
                return;

            case SpecialType.System_Int32:
                ILGenerator.Emit(OpCodes.Ldc_I4, Convert.ToInt32(value));
                return;

            case SpecialType.System_UInt32:
                unchecked { ILGenerator.Emit(OpCodes.Ldc_I4, (int)Convert.ToUInt32(value)); }
                return;

            case SpecialType.System_Int64:
                ILGenerator.Emit(OpCodes.Ldc_I8, Convert.ToInt64(value));
                return;

            case SpecialType.System_UInt64:
                unchecked { ILGenerator.Emit(OpCodes.Ldc_I8, (long)Convert.ToUInt64(value)); }
                return;

            case SpecialType.System_Single:
                ILGenerator.Emit(OpCodes.Ldc_R4, Convert.ToSingle(value, CultureInfo.InvariantCulture));
                return;

            case SpecialType.System_Double:
                ILGenerator.Emit(OpCodes.Ldc_R8, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return;

            case SpecialType.System_String:
                ILGenerator.Emit(OpCodes.Ldstr, (string)value);
                return;
        }

        // Fallback: boxed constant (caller must compare via Equals)
        EmitConstantAsObject(litUnderlying, value);
    }

    private void EmitConstantAsObject(ITypeSymbol sourceType, object value)
    {
        switch (value)
        {
            case string s:
                ILGenerator.Emit(OpCodes.Ldstr, s);
                return;

            case char ch:
                ILGenerator.Emit(OpCodes.Ldc_I4, (int)ch);
                ILGenerator.Emit(OpCodes.Conv_U2);
                ILGenerator.Emit(OpCodes.Box, ResolveClrType(sourceType));
                return;

            case bool b:
                ILGenerator.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Box, typeof(bool));
                return;

            default:
                EmitLiteral(value);
                if (sourceType.IsValueType)
                    ILGenerator.Emit(OpCodes.Box, ResolveClrType(sourceType));
                return;
        }
    }

    // ============================================
    // Unboxed DU fast path helpers
    // ============================================

    private bool TryEmitPatternTest_UnboxedValueTypeDU(
        BoundPattern pattern,
        Generator scope,
        IILocal unionLocal,
        Type unionClrType)
    {
        if (pattern is BoundCasePattern cp)
        {
            EmitCasePatternUnboxed(cp, scope, unionLocal, unionClrType, unionLocal);
            return true;
        }

        if (pattern is BoundConstantPattern constantPattern && IsNullConstantPattern(constantPattern))
        {
            EmitStructUnionHasNoValue(unionLocal, unionClrType);
            return true;
        }

        // Binder may insert a redundant declaration/type pattern when the scrutinee is already
        // statically known to be the DU type (especially for generics). Treat it as always-true
        // and perform the binding without boxing/isinst.
        if (pattern is BoundDeclarationPattern dp)
        {
            if (dp.Type.TypeKind == TypeKind.Null)
            {
                EmitStructUnionHasNoValue(unionLocal, unionClrType);
                return true;
            }

            var dpClr = ResolveClrType(dp.Type);
            dpClr = Generator.InstantiateType(dpClr);

            // Only handle exact DU type matches; anything else must fall back.
            if (dpClr != unionClrType)
                return false;

            EmitPatternDesignator(dp.Designator, unionLocal, scope);

            ILGenerator.Emit(OpCodes.Ldc_I4_1);
            return true;
        }

        if (pattern is BoundUnaryPattern up && up.Kind == BoundUnaryPatternKind.Not)
        {
            if (!TryEmitPatternTest_UnboxedValueTypeDU(up.Pattern, scope, unionLocal, unionClrType))
                return false;

            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            ILGenerator.Emit(OpCodes.Ceq);
            return true;
        }

        if (pattern is BoundBinaryPattern bp)
        {
            if (bp.Kind == BoundPatternKind.And)
                return TryEmitAnd_UnboxedDU(bp.Left, bp.Right, scope, unionLocal, unionClrType);

            if (bp.Kind == BoundPatternKind.Or)
                return TryEmitOr_UnboxedDU(bp.Left, bp.Right, scope, unionLocal, unionClrType);

            return false;
        }

        return false;
    }

    private void EmitStructUnionHasNoValue(IILocal unionLocal, Type unionClrType)
    {
        EmitStructUnionHasValue(unionLocal, unionClrType);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Ceq);
    }

    private void EmitStructUnionHasValue(IILocal unionLocal, Type unionClrType)
    {
        var getter = unionClrType.GetProperty("HasValue")?.GetGetMethod()
            ?? throw new InvalidOperationException($"Missing HasValue getter on union type '{unionClrType}'.");

        ILGenerator.Emit(OpCodes.Ldloca_S, unionLocal);
        ILGenerator.Emit(OpCodes.Call, getter);
    }

    private static bool IsNullConstantPattern(BoundConstantPattern pattern)
        => pattern.LiteralType is null &&
           (pattern.Expression is null && pattern.ConstantValue is null ||
            pattern.Expression is not null && IsNullConstantExpression(pattern.Expression));

    private static bool IsNullConstantExpression(BoundExpression expression)
        => expression is BoundTypeExpression { Type: NullTypeSymbol }
            or BoundLiteralExpression { Kind: BoundLiteralExpressionKind.NullLiteral };

    private void EmitClassUnionValuePattern(INamedTypeSymbol classUnionType, Type unionClrType, IILocal? scrutineeLocal2, Action emitValuePattern)
    {
        var valueGetter = classUnionType
            .GetMembers("Value")
            .OfType<IPropertySymbol>()
            .Where(static property => property.GetMethod is not null)
            .Select(property => property.GetMethod!)
            .FirstOrDefault(method => SymbolEqualityComparer.Default.Equals(method.ContainingType, classUnionType))
            ?? classUnionType
                .GetMembers("Value")
                .OfType<IPropertySymbol>()
                .Where(static property => property.GetMethod is not null)
                .Select(property => property.GetMethod!)
                .First();

        unionClrType = Generator.InstantiateType(unionClrType);
        var unionCarrierLocal = scrutineeLocal2 ?? ILGenerator.DeclareLocal(unionClrType);
        var labelCarrierPresent = ILGenerator.DefineLabel();
        var labelDone = ILGenerator.DefineLabel();

        if (scrutineeLocal2 is null)
        {
            ILGenerator.Emit(OpCodes.Stloc, unionCarrierLocal);
        }
        else
        {
            ILGenerator.Emit(OpCodes.Pop);
        }

        ILGenerator.Emit(OpCodes.Ldloc, unionCarrierLocal);
        ILGenerator.Emit(OpCodes.Brtrue, labelCarrierPresent);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Br, labelDone);

        ILGenerator.MarkLabel(labelCarrierPresent);
        ILGenerator.Emit(OpCodes.Ldloc, unionCarrierLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(valueGetter));
        emitValuePattern();

        ILGenerator.MarkLabel(labelDone);
    }

    private bool TryEmitAnd_UnboxedDU(
        BoundPattern left,
        BoundPattern right,
        Generator scope,
        IILocal unionLocal,
        Type unionClrType)
    {
        if (!IsUnboxedDUCompatible(left) || !IsUnboxedDUCompatible(right))
            return false;

        var labelFail = ILGenerator.DefineLabel();
        var labelDone = ILGenerator.DefineLabel();

        TryEmitPatternTest_UnboxedValueTypeDU(left, scope, unionLocal, unionClrType);
        ILGenerator.Emit(OpCodes.Brfalse, labelFail);

        TryEmitPatternTest_UnboxedValueTypeDU(right, scope, unionLocal, unionClrType);
        ILGenerator.Emit(OpCodes.Brfalse, labelFail);

        ILGenerator.Emit(OpCodes.Ldc_I4_1);
        ILGenerator.Emit(OpCodes.Br, labelDone);

        ILGenerator.MarkLabel(labelFail);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);

        ILGenerator.MarkLabel(labelDone);
        return true;
    }

    private bool TryEmitOr_UnboxedDU(
        BoundPattern left,
        BoundPattern right,
        Generator scope,
        IILocal unionLocal,
        Type unionClrType)
    {
        if (!IsUnboxedDUCompatible(left) || !IsUnboxedDUCompatible(right))
            return false;

        var labelTrue = ILGenerator.DefineLabel();
        var labelDone = ILGenerator.DefineLabel();

        TryEmitPatternTest_UnboxedValueTypeDU(left, scope, unionLocal, unionClrType);
        ILGenerator.Emit(OpCodes.Brtrue, labelTrue);

        TryEmitPatternTest_UnboxedValueTypeDU(right, scope, unionLocal, unionClrType);
        ILGenerator.Emit(OpCodes.Brtrue, labelTrue);

        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Br, labelDone);

        ILGenerator.MarkLabel(labelTrue);
        ILGenerator.Emit(OpCodes.Ldc_I4_1);

        ILGenerator.MarkLabel(labelDone);
        return true;
    }

    private static bool IsUnboxedDUCompatible(BoundPattern pattern)
    {
        if (pattern is BoundCasePattern)
            return true;

        if (pattern is BoundUnaryPattern up && up.Kind == BoundUnaryPatternKind.Not)
            return IsUnboxedDUCompatible(up.Pattern);

        if (pattern is BoundBinaryPattern bp &&
            (bp.Kind == BoundPatternKind.And || bp.Kind == BoundPatternKind.Or))
            return IsUnboxedDUCompatible(bp.Left) && IsUnboxedDUCompatible(bp.Right);

        return false;
    }

    private void EmitCasePatternUnboxed(
        BoundCasePattern casePattern,
        Generator scope,
        IILocal unionLocal,
        Type unionClrType,
        IILocal? local)
    {
        var tryGetMethod = ResolveCasePatternTryGetMethod(casePattern, out _, out var caseClrType);
        var caseLocal = local ?? ILGenerator.DeclareLocal(caseClrType);

        var labelFail = ILGenerator.DefineLabel();
        var labelDone = ILGenerator.DefineLabel();

        ILGenerator.Emit(OpCodes.Ldloca, caseLocal);
        ILGenerator.Emit(OpCodes.Initobj, caseClrType);

        ILGenerator.Emit(OpCodes.Ldloca, unionLocal);
        ILGenerator.Emit(OpCodes.Ldloca, caseLocal);
        ILGenerator.Emit(OpCodes.Call, tryGetMethod);
        ILGenerator.Emit(OpCodes.Brfalse, labelFail);

        var parameterCount = Math.Min(
            casePattern.CaseSymbol.ConstructorParameters.Length,
            casePattern.Arguments.Length);

        for (var i = 0; i < parameterCount; i++)
        {
            var parameter = casePattern.CaseSymbol.ConstructorParameters[i];
            var propertyName = GetCasePropertyName(parameter.Name);

            var propertySymbol = casePattern.CaseSymbol
                .GetMembers(propertyName)
                .OfType<IPropertySymbol>()
                .FirstOrDefault();

            if (propertySymbol?.GetMethod is null)
            {
                ILGenerator.Emit(OpCodes.Br, labelFail);
                break;
            }

            ILGenerator.Emit(OpCodes.Ldloca, caseLocal);
            ILGenerator.Emit(OpCodes.Call, GetMethodInfo(propertySymbol.GetMethod));

            // Use helper to avoid unnecessary branching for trivially-true patterns.
            EmitPatternTestBranchFalse(casePattern.Arguments[i], propertySymbol.Type, scope, labelFail, null);
        }

        ILGenerator.Emit(OpCodes.Ldc_I4_1);
        ILGenerator.Emit(OpCodes.Br, labelDone);

        ILGenerator.MarkLabel(labelFail);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);

        ILGenerator.MarkLabel(labelDone);
    }

    // ============================================
    // Misc helpers (existing in your project)
    // ============================================

    private static bool IsUnionValueType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        if (!named.IsValueType)
            return false;

        // Constructed generic DU instances may not carry DU metadata directly.
        if (named.TryGetUnion() is not null)
            return true;

        var def = named.OriginalDefinition;
        return def is not null && def.TryGetUnion() is not null;
    }

    private static bool RequiresValueTypeHandling(ITypeSymbol typeSymbol)
    {
        return typeSymbol.IsValueType || typeSymbol is ITypeParameterSymbol { IsReferenceType: false };
    }

    private static bool IsKnownReferenceType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is ITypeParameterSymbol typeParameter)
            return (typeParameter.ConstraintKind & TypeParameterConstraintKind.ReferenceType) != 0;

        return typeSymbol.IsReferenceType == true;
    }

    private static bool ClrTypesMatch(Type? left, Type? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is null || right is null)
            return false;

        if (left == right)
            return true;

        if (left.IsArray || right.IsArray)
        {
            if (!left.IsArray || !right.IsArray)
                return false;
            if (left.GetArrayRank() != right.GetArrayRank())
                return false;
            return ClrTypesMatch(left.GetElementType(), right.GetElementType());
        }

        if (left.IsByRef || right.IsByRef)
        {
            if (!left.IsByRef || !right.IsByRef)
                return false;
            return ClrTypesMatch(left.GetElementType(), right.GetElementType());
        }

        if (left.IsPointer || right.IsPointer)
        {
            if (!left.IsPointer || !right.IsPointer)
                return false;
            return ClrTypesMatch(left.GetElementType(), right.GetElementType());
        }

        if (left.IsGenericType || right.IsGenericType)
        {
            if (!left.IsGenericType || !right.IsGenericType)
                return false;

            var leftDefinition = left.GetGenericTypeDefinition();
            var rightDefinition = right.GetGenericTypeDefinition();
            if (!ReferenceEquals(leftDefinition, rightDefinition) && leftDefinition != rightDefinition)
                return false;

            var leftArgs = left.GetGenericArguments();
            var rightArgs = right.GetGenericArguments();
            if (leftArgs.Length != rightArgs.Length)
                return false;

            for (var i = 0; i < leftArgs.Length; i++)
            {
                if (!ClrTypesMatch(leftArgs[i], rightArgs[i]))
                    return false;
            }

            return true;
        }

        if (left.IsGenericParameter || right.IsGenericParameter)
        {
            if (!left.IsGenericParameter || !right.IsGenericParameter)
                return false;
            return left.GenericParameterPosition == right.GenericParameterPosition &&
                   string.Equals(left.Name, right.Name, StringComparison.Ordinal);
        }

        return string.Equals(left.FullName, right.FullName, StringComparison.Ordinal) &&
               Equals(left.Assembly, right.Assembly);
    }

    private static string GetCasePropertyName(string parameterName)
    {
        if (string.IsNullOrEmpty(parameterName))
            return parameterName;

        if (char.IsUpper(parameterName[0]))
            return parameterName;

        Span<char> buffer = stackalloc char[parameterName.Length];
        parameterName.AsSpan().CopyTo(buffer);
        buffer[0] = char.ToUpperInvariant(buffer[0]);
        return new string(buffer);
    }

    private IILocal? EmitDesignation(BoundDesignator designation, Generator scope)
    {
        if (designation is BoundSingleVariableDesignator single)
        {
            var symbol = single.Local;
            var existingLocal = scope.GetLocal(symbol);
            if (existingLocal is not null)
                return existingLocal;

            var local = ILGenerator.DeclareLocal(ResolveClrType(symbol.Type));
            local.SetLocalSymInfo(single.Local.Name);

            scope.AddLocal(symbol, local);
            return local;
        }

        if (designation is BoundDiscardDesignator)
            return null;

        throw new InvalidOperationException($"Unexpected bound designation type '{designation.GetType().Name}'.");
    }

    private void EmitDesignationFromStack(BoundDesignator designation, Generator scope)
    {
        if (designation is BoundSingleVariableDesignator single &&
            MethodBodyGenerator.TryGetCapturedField(single.Local, out var field, out var fromStateMachine))
        {
            var valueLocal = ILGenerator.DeclareLocal(ResolveClrType(single.Local.Type));
            ILGenerator.Emit(OpCodes.Stloc, valueLocal);
            if (fromStateMachine)
                ILGenerator.Emit(OpCodes.Ldarg_0);
            else
                MethodBodyGenerator.EmitLoadClosure();
            ILGenerator.Emit(OpCodes.Ldloc, valueLocal);
            ILGenerator.Emit(OpCodes.Stfld, field);
            return;
        }

        var local = EmitDesignation(designation, scope);
        if (local is not null)
            ILGenerator.Emit(OpCodes.Stloc, local);
        else
            ILGenerator.Emit(OpCodes.Pop);
    }

    private void EmitPatternDesignator(BoundDesignator? designator, IILocal sourceLocal, Generator scope)
    {
        if (designator is null)
            return;

        if (designator is BoundSingleVariableDesignator single &&
            MethodBodyGenerator.TryGetCapturedField(single.Local, out var field, out var fromStateMachine))
        {
            if (fromStateMachine)
                ILGenerator.Emit(OpCodes.Ldarg_0);
            else
                MethodBodyGenerator.EmitLoadClosure();
            ILGenerator.Emit(OpCodes.Ldloc, sourceLocal);
            ILGenerator.Emit(OpCodes.Stfld, field);
            return;
        }

        var boundLocal = EmitDesignation(designator, scope);
        if (boundLocal is null)
            return;

        ILGenerator.Emit(OpCodes.Ldloc, sourceLocal);
        ILGenerator.Emit(OpCodes.Stloc, boundLocal);
    }

    // Helpers

    private void EmitConstantForRelational(BoundConstantPattern constant, ITypeSymbol targetType)
    {
        // Comparison patterns must be constant literals. Binder should enforce this.
        var literal = constant.LiteralType;
        if (literal is null)
        {
            // Be defensive: emit default(T) and let compare produce deterministic result.
            EmitDefaultValue(targetType);
            return;
        }

        var value = constant.ConstantValue; // from LiteralTypeSymbol
        if (value is null)
        {
            // binder should prevent this; be defensive
            EmitDefaultValue(targetType);
            return;
        }

        // Use your existing literal emitter, but ensure it's emitted as the target type.
        // For relational comparisons, the "targetType" should be the scrutinee/member type.
        EmitLiteralInTargetType(value, targetType, literal);
    }

    private void EmitDefaultValue(ITypeSymbol type, EmitContext context)
    {
        if (type.TypeKind == TypeKind.Error)
        {
            ILGenerator.Emit(OpCodes.Ldnull);
            return;
        }

        var clr = ResolveClrType(type);

        if (!clr.IsValueType)
        {
            ILGenerator.Emit(OpCodes.Ldnull);
            return;
        }

        var tmp = ILGenerator.DeclareLocal(clr);
        ILGenerator.Emit(OpCodes.Ldloca_S, tmp);
        ILGenerator.Emit(OpCodes.Initobj, clr);
        ILGenerator.Emit(OpCodes.Ldloc, tmp);
    }

    private void EmitRuntimeValueConstantCompare(BoundExpression valueExpression, ITypeSymbol scrutineeType, IILocal? scrutineeLocal2)
    {
        // Stack on entry: <scrutinee>
        // Spill the scrutinee, evaluate the value expression, box as needed, and call object.Equals(a,b).

        var scrutineeClr = ResolveClrType(scrutineeType);
        var scrutineeLocal = scrutineeLocal2 ?? ILGenerator.DeclareLocal(scrutineeClr);
        ILGenerator.Emit(OpCodes.Stloc, scrutineeLocal);

        // left: box(scrutinee)
        ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
        if (RequiresValueTypeHandling(scrutineeType) && scrutineeType.TypeKind != TypeKind.Error)
            ILGenerator.Emit(OpCodes.Box, Generator.InstantiateType(scrutineeClr));

        // right: evaluate and box
        new ExpressionGenerator(this, valueExpression).Emit();

        var valueType = valueExpression.Type;
        if (valueType is not null && RequiresValueTypeHandling(valueType) && valueType.TypeKind != TypeKind.Error)
            ILGenerator.Emit(OpCodes.Box, Generator.InstantiateType(ResolveClrType(valueType)));

        var equals2 = typeof(object).GetMethod(nameof(object.Equals), new[] { typeof(object), typeof(object) })
            ?? throw new InvalidOperationException("Failed to resolve object.Equals(object, object).");

        ILGenerator.Emit(OpCodes.Call, equals2);
    }

    private void EmitCompare(ITypeSymbol type)
    {
        var clr = Generator.InstantiateType(ResolveClrType(type));

        var rightLocal = ILGenerator.DeclareLocal(clr);
        ILGenerator.Emit(OpCodes.Stloc, rightLocal); // pop right

        var leftLocal = ILGenerator.DeclareLocal(clr);
        ILGenerator.Emit(OpCodes.Stloc, leftLocal); // pop left

        var comparerType = typeof(System.Collections.Generic.Comparer<>).MakeGenericType(clr);
        var defaultGetter = comparerType.GetProperty("Default")!.GetGetMethod()!;
        var compareMethod = comparerType.GetMethod("Compare", new[] { clr, clr })!;

        ILGenerator.Emit(OpCodes.Call, defaultGetter);
        ILGenerator.Emit(OpCodes.Ldloc, leftLocal);
        ILGenerator.Emit(OpCodes.Ldloc, rightLocal);
        ILGenerator.Emit(OpCodes.Callvirt, compareMethod); // int
    }

    private void EmitComparisonOperator(BoundComparisonPatternOperator op)
    {
        // Stack: <compareResult:int>
        ILGenerator.Emit(OpCodes.Ldc_I4_0);

        switch (op)
        {
            case BoundComparisonPatternOperator.Equals:
                ILGenerator.Emit(OpCodes.Ceq);
                break;

            case BoundComparisonPatternOperator.NotEquals:
                ILGenerator.Emit(OpCodes.Ceq);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Ceq);
                break;

            case BoundComparisonPatternOperator.LessThan:
                ILGenerator.Emit(OpCodes.Clt);
                break;

            case BoundComparisonPatternOperator.LessThanOrEqual:
                // !(result > 0)
                ILGenerator.Emit(OpCodes.Cgt);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Ceq);
                break;

            case BoundComparisonPatternOperator.GreaterThan:
                ILGenerator.Emit(OpCodes.Cgt);
                break;

            case BoundComparisonPatternOperator.GreaterThanOrEqual:
                // !(result < 0)
                ILGenerator.Emit(OpCodes.Clt);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Ceq);
                break;

            default:
                throw new NotSupportedException();
        }
    }

    private bool TryEmitPatternBranchFalse_UnboxedValueTypeDU(
        BoundPattern pattern,
        Generator scope,
        IILocal unionLocal,
        Type unionClrType,
        ILLabel labelFail)
    {
        if (pattern is BoundCasePattern cp)
        {
            EmitCasePatternUnboxed_BranchFalse(cp, scope, unionLocal, unionClrType, labelFail);
            return true;
        }

        if (pattern is BoundConstantPattern constantPattern && IsNullConstantPattern(constantPattern))
        {
            EmitStructUnionHasValue(unionLocal, unionClrType);
            ILGenerator.Emit(OpCodes.Brtrue, labelFail);
            return true;
        }

        // Treat redundant declaration/type pattern over DU as always-true and just bind.
        if (pattern is BoundDeclarationPattern dp)
        {
            if (dp.Type.TypeKind == TypeKind.Null)
            {
                EmitStructUnionHasValue(unionLocal, unionClrType);
                ILGenerator.Emit(OpCodes.Brtrue, labelFail);
                return true;
            }

            var dpClr = ResolveClrType(dp.Type);
            dpClr = Generator.InstantiateType(dpClr);

            if (!ClrTypesMatch(dpClr, unionClrType))
                return false;

            EmitPatternDesignator(dp.Designator, unionLocal, scope);

            return true;
        }

        if (pattern is BoundUnaryPattern up && up.Kind == BoundUnaryPatternKind.Not)
        {
            // NOT succeeds when inner fails.
            var labelInnerFailed = ILGenerator.DefineLabel();

            if (!TryEmitPatternBranchFalse_UnboxedValueTypeDU(up.Pattern, scope, unionLocal, unionClrType, labelInnerFailed))
                return false;

            // Inner succeeded => NOT fails.
            ILGenerator.Emit(OpCodes.Br, labelFail);

            // Inner failed => NOT succeeds.
            ILGenerator.MarkLabel(labelInnerFailed);
            return true;
        }

        if (pattern is BoundBinaryPattern bp)
        {
            if (bp.Kind == BoundPatternKind.And)
            {
                if (!IsUnboxedDUCompatible(bp.Left) || !IsUnboxedDUCompatible(bp.Right))
                    return false;

                TryEmitPatternBranchFalse_UnboxedValueTypeDU(bp.Left, scope, unionLocal, unionClrType, labelFail);
                TryEmitPatternBranchFalse_UnboxedValueTypeDU(bp.Right, scope, unionLocal, unionClrType, labelFail);
                return true;
            }

            if (bp.Kind == BoundPatternKind.Or)
            {
                if (!IsUnboxedDUCompatible(bp.Left) || !IsUnboxedDUCompatible(bp.Right))
                    return false;

                var labelTryRight = ILGenerator.DefineLabel();
                var labelSuccess = ILGenerator.DefineLabel();

                // If left fails, try right.
                TryEmitPatternBranchFalse_UnboxedValueTypeDU(bp.Left, scope, unionLocal, unionClrType, labelTryRight);

                // Left succeeded => success.
                ILGenerator.Emit(OpCodes.Br, labelSuccess);

                ILGenerator.MarkLabel(labelTryRight);
                TryEmitPatternBranchFalse_UnboxedValueTypeDU(bp.Right, scope, unionLocal, unionClrType, labelFail);

                ILGenerator.MarkLabel(labelSuccess);
                return true;
            }
        }

        return false;
    }

    private void EmitCasePatternUnboxed_BranchFalse(
        BoundCasePattern casePattern,
        Generator scope,
        IILocal unionLocal,
        Type unionClrType,
        ILLabel labelFail)
    {
        var tryGetMethod = ResolveCasePatternTryGetMethod(casePattern, out _, out var caseClrType);
        var caseLocal = ILGenerator.DeclareLocal(caseClrType);

        ILGenerator.Emit(OpCodes.Ldloca, caseLocal);
        ILGenerator.Emit(OpCodes.Initobj, caseClrType);

        ILGenerator.Emit(OpCodes.Ldloca, unionLocal);
        ILGenerator.Emit(OpCodes.Ldloca, caseLocal);
        ILGenerator.Emit(OpCodes.Call, tryGetMethod);
        ILGenerator.Emit(OpCodes.Brfalse, labelFail);

        var parameterCount = Math.Min(
            casePattern.CaseSymbol.ConstructorParameters.Length,
            casePattern.Arguments.Length);

        for (var i = 0; i < parameterCount; i++)
        {
            var parameter = casePattern.CaseSymbol.ConstructorParameters[i];
            var propertyName = GetCasePropertyName(parameter.Name);

            var propertySymbol = casePattern.CaseSymbol
                .GetMembers(propertyName)
                .OfType<IPropertySymbol>()
                .FirstOrDefault();

            if (propertySymbol?.GetMethod is null)
            {
                ILGenerator.Emit(OpCodes.Br, labelFail);
                break;
            }

            ILGenerator.Emit(OpCodes.Ldloca, caseLocal);
            ILGenerator.Emit(OpCodes.Call, GetMethodInfo(propertySymbol.GetMethod));

            EmitPatternBranchFalse(casePattern.Arguments[i], propertySymbol.Type, scope, labelFail, null);
        }

        EmitPatternDesignator(casePattern.Designator, caseLocal, scope);
    }

    // Preferred match-arm pattern emission: branch to labelFail when pattern does NOT match.
    // On success, fall through. Does NOT materialize a boolean result.
    private void EmitPatternBranchFalse(
        BoundPattern pattern,
        ITypeSymbol inputType,
        Generator? scope,
        ILLabel labelFail,
        IILocal? scrutineeLocal2 = null)
    {
        scope ??= this;

        // Some patterns require object semantics. Keep the same boxing rule as EmitPattern.
        void EnsureObjectOnStack(ref ITypeSymbol curType)
        {
            if (RequiresValueTypeHandling(curType) && curType.TypeKind != TypeKind.Error)
                ILGenerator.Emit(OpCodes.Box, ResolveClrType(curType));

            curType = Compilation.GetSpecialType(SpecialType.System_Object);
        }

        // `_` always matches: just consume scrutinee.
        if (pattern is BoundDiscardPattern)
        {
            ILGenerator.Emit(OpCodes.Pop);
            return;
        }

        // Constant and comparison patterns already produce a boolean efficiently.
        // We keep their existing implementation but immediately branch on false.
        if (pattern is BoundConstantPattern cp)
        {
            EmitConstantPattern(cp, inputType, scrutineeLocal2);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);
            return;
        }

        if (pattern is BoundComparisonPattern rp)
        {
            EmitComparisonPattern(rp, inputType, scope, scrutineeLocal2);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);
            return;
        }

        if (pattern is BoundRangePattern rpRange)
        {
            EmitRangePattern(rpRange, inputType, scope, scrutineeLocal2);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);
            return;
        }

        if (pattern is BoundDictionaryPattern dictionaryPattern)
        {
            EmitDictionaryPattern(dictionaryPattern, inputType, scope, scrutineeLocal2);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);
            return;
        }

        // Declaration/capture pattern: if the scrutinee is already exactly the declared type,
        // it cannot fail. Perform binding and fall through.
        if (pattern is BoundDeclarationPattern dp)
        {
            var declared = dp.Type;

            if (inputType.TypeKind != TypeKind.Error && declared.TypeKind != TypeKind.Error)
            {
                var inputClr = Generator.InstantiateType(ResolveClrType(inputType));
                var declaredClr = Generator.InstantiateType(ResolveClrType(declared));

                if (inputClr == declaredClr)
                {
                    EmitDesignationFromStack(dp.Designator, scope);

                    return;
                }
            }

            // General case: fall back to existing bool-producing implementation.
            EmitPattern(dp, inputType, scope, scrutineeLocal2);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);
            return;
        }

        // Case patterns: implement branching form for both unboxed DU value-type fast path and boxed pipeline.
        if (pattern is BoundCasePattern casePattern)
        {
            var tryGetMethod = ResolveCasePatternTryGetMethod(casePattern, out var unionClrType, out var caseClrType);

            // Unboxed DU value-type fast path (scrutinee is already DU value type)
            if (inputType.TypeKind != TypeKind.Error)
            {
                var inputClr = Generator.InstantiateType(ResolveClrType(inputType));
                if (unionClrType.IsValueType && ClrTypesMatch(inputClr, unionClrType))
                {
                    IILocal unionLocal2;
                    if (scrutineeLocal2 is not null)
                    {
                        unionLocal2 = scrutineeLocal2;
                        ILGenerator.Emit(OpCodes.Pop);
                    }
                    else
                    {
                        unionLocal2 = ILGenerator.DeclareLocal(unionClrType);
                        ILGenerator.Emit(OpCodes.Stloc, unionLocal2);
                    }
                    var caseLocal2 = ILGenerator.DeclareLocal(caseClrType);

                    ILGenerator.Emit(OpCodes.Ldloca, caseLocal2);
                    ILGenerator.Emit(OpCodes.Initobj, caseClrType);

                    ILGenerator.Emit(OpCodes.Ldloca, unionLocal2);
                    ILGenerator.Emit(OpCodes.Ldloca, caseLocal2);
                    ILGenerator.Emit(OpCodes.Call, tryGetMethod);
                    ILGenerator.Emit(OpCodes.Brfalse, labelFail);

                    var parameterCount2 = Math.Min(
                        casePattern.CaseSymbol.ConstructorParameters.Length,
                        casePattern.Arguments.Length);

                    for (var i = 0; i < parameterCount2; i++)
                    {
                        var parameter = casePattern.CaseSymbol.ConstructorParameters[i];
                        var propertyName = GetCasePropertyName(parameter.Name);

                        var propertySymbol = casePattern.CaseSymbol
                            .GetMembers(propertyName)
                            .OfType<IPropertySymbol>()
                            .FirstOrDefault();

                        if (propertySymbol?.GetMethod is null)
                        {
                            ILGenerator.Emit(OpCodes.Br, labelFail);
                            break;
                        }

                        ILGenerator.Emit(OpCodes.Ldloca, caseLocal2);
                        ILGenerator.Emit(OpCodes.Call, GetMethodInfo(propertySymbol.GetMethod));

                        // Branching nested pattern test.
                        EmitPatternBranchFalse(casePattern.Arguments[i], propertySymbol.Type, scope, labelFail, null);
                    }

                    return;
                }
            }

            // Boxed pipeline: needs object semantics for the union type-test.
            EnsureObjectOnStack(ref inputType);

            var unionLocal = scrutineeLocal2 ?? ILGenerator.DeclareLocal(unionClrType);
            var caseLocal = ILGenerator.DeclareLocal(caseClrType);

            // isinst unionClrType
            var labelHasUnion = ILGenerator.DefineLabel();
            ILGenerator.Emit(OpCodes.Isinst, unionClrType);
            ILGenerator.Emit(OpCodes.Dup);
            ILGenerator.Emit(OpCodes.Brtrue, labelHasUnion);
            ILGenerator.Emit(OpCodes.Pop);
            ILGenerator.Emit(OpCodes.Br, labelFail);

            ILGenerator.MarkLabel(labelHasUnion);
            ILGenerator.Emit(unionClrType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, unionClrType);
            ILGenerator.Emit(OpCodes.Stloc, unionLocal);

            ILGenerator.Emit(OpCodes.Ldloca, caseLocal);
            ILGenerator.Emit(OpCodes.Initobj, caseClrType);

            ILGenerator.Emit(unionClrType.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, unionLocal);
            ILGenerator.Emit(OpCodes.Ldloca, caseLocal);
            ILGenerator.Emit(OpCodes.Call, tryGetMethod);
            ILGenerator.Emit(OpCodes.Brfalse, labelFail);

            var parameterCount = Math.Min(
                casePattern.CaseSymbol.ConstructorParameters.Length,
                casePattern.Arguments.Length);

            for (var i = 0; i < parameterCount; i++)
            {
                var parameter = casePattern.CaseSymbol.ConstructorParameters[i];
                var propertyName = GetCasePropertyName(parameter.Name);

                var propertySymbol = casePattern.CaseSymbol
                    .GetMembers(propertyName)
                    .OfType<IPropertySymbol>()
                    .FirstOrDefault();

                if (propertySymbol?.GetMethod is null)
                {
                    ILGenerator.Emit(OpCodes.Br, labelFail);
                    break;
                }

                ILGenerator.Emit(caseClrType.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, caseLocal);
                ILGenerator.Emit(OpCodes.Call, GetMethodInfo(propertySymbol.GetMethod));

                EmitPatternBranchFalse(casePattern.Arguments[i], propertySymbol.Type, scope, labelFail, null);
            }

            return;
        }

        // Unary/binary logical patterns can be emitted in branch form directly.
        if (pattern is BoundUnaryPattern unaryPattern)
        {
            if (unaryPattern.Kind == BoundUnaryPatternKind.Not)
            {
                var scrutineeClr = ResolveClrType(inputType);
                var scrutineeLocal = scrutineeLocal2 ?? ILGenerator.DeclareLocal(scrutineeClr);
                ILGenerator.Emit(OpCodes.Stloc, scrutineeLocal);

                var labelInnerFailed = ILGenerator.DefineLabel();
                ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
                EmitPatternBranchFalse(unaryPattern.Pattern, inputType, scope, labelInnerFailed, scrutineeLocal);

                // Inner pattern matched => `not` fails.
                ILGenerator.Emit(OpCodes.Br, labelFail);
                ILGenerator.MarkLabel(labelInnerFailed);
                return;
            }

            throw new InvalidOperationException($"Unexpected bound unary pattern kind '{unaryPattern.Kind}'.");
        }

        if (pattern is BoundBinaryPattern binaryPattern)
        {
            var scrutineeClr = ResolveClrType(inputType);
            var scrutineeLocal = scrutineeLocal2 ?? ILGenerator.DeclareLocal(scrutineeClr);
            ILGenerator.Emit(OpCodes.Stloc, scrutineeLocal);

            if (binaryPattern.Kind == BoundPatternKind.And)
            {
                ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
                EmitPatternBranchFalse(binaryPattern.Left, inputType, scope, labelFail, scrutineeLocal);

                ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
                EmitPatternBranchFalse(binaryPattern.Right, inputType, scope, labelFail, scrutineeLocal);
                return;
            }

            if (binaryPattern.Kind == BoundPatternKind.Or)
            {
                var labelTryRight = ILGenerator.DefineLabel();
                var labelSuccess = ILGenerator.DefineLabel();

                ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
                EmitPatternBranchFalse(binaryPattern.Left, inputType, scope, labelTryRight, scrutineeLocal);
                ILGenerator.Emit(OpCodes.Br, labelSuccess);

                ILGenerator.MarkLabel(labelTryRight);
                ILGenerator.Emit(OpCodes.Ldloc, scrutineeLocal);
                EmitPatternBranchFalse(binaryPattern.Right, inputType, scope, labelFail, scrutineeLocal);

                ILGenerator.MarkLabel(labelSuccess);
                return;
            }

            throw new InvalidOperationException($"Unexpected bound binary pattern kind '{binaryPattern.Kind}'.");
        }

        // Unary/binary/tuple/property/deconstruct: fall back to bool form for now.
        // (You can migrate these later for fully structured decompile.)
        EmitPattern(pattern, inputType, scope, scrutineeLocal2);
        ILGenerator.Emit(OpCodes.Brfalse, labelFail);
    }

    private MethodInfo ResolveCasePatternTryGetMethod(
        BoundCasePattern casePattern,
        out Type unionClrType,
        out Type caseClrType)
    {
        unionClrType = Generator.InstantiateType(ResolveClrType(casePattern.CaseSymbol.Union));
        caseClrType = Generator.InstantiateType(ResolveClrType(casePattern.CaseSymbol));

        var tryGetMethod = GetMethodInfo(casePattern.TryGetMethod);

        if (tryGetMethod.DeclaringType is not null)
            unionClrType = tryGetMethod.DeclaringType;

        var outElementType = TryGetOutLocalElementType(tryGetMethod);
        if (outElementType is not null)
            caseClrType = CloseNestedCarrierCaseType(outElementType, unionClrType);

        return tryGetMethod;
    }

    private void EmitUnionMemberPattern(
        BoundUnionMemberPattern unionMemberPattern,
        ITypeSymbol inputType,
        Generator scope,
        IILocal? scrutineeLocal2)
    {
        var unionClrType = Generator.InstantiateType(ResolveClrType(unionMemberPattern.UnionType));
        var tryGetMethod = CloseMethodOnRuntimeCarrier(unionClrType, GetMethodInfo(unionMemberPattern.TryGetMethod));
        var memberClrType = TryGetOutLocalElementType(tryGetMethod) is { } outElementType
            ? CloseTypeFromMethodContext(outElementType, tryGetMethod.DeclaringType)
            : Generator.InstantiateType(ResolveClrType(unionMemberPattern.MemberType));

        if (inputType.TypeKind != TypeKind.Error)
        {
            var inputClr = Generator.InstantiateType(ResolveClrType(inputType));
            if (unionClrType.IsValueType && ClrTypesMatch(inputClr, unionClrType))
            {
                IILocal unionLocal;
                if (scrutineeLocal2 is not null)
                {
                    unionLocal = scrutineeLocal2;
                    ILGenerator.Emit(OpCodes.Pop);
                }
                else
                {
                    unionLocal = ILGenerator.DeclareLocal(unionClrType);
                    ILGenerator.Emit(OpCodes.Stloc, unionLocal);
                }

                var valueLocal = ILGenerator.DeclareLocal(memberClrType);
                var labelFail = ILGenerator.DefineLabel();
                var labelDone = ILGenerator.DefineLabel();

                ILGenerator.Emit(OpCodes.Ldloca, unionLocal);
                ILGenerator.Emit(OpCodes.Ldloca, valueLocal);
                ILGenerator.Emit(OpCodes.Call, tryGetMethod);
                ILGenerator.Emit(OpCodes.Brfalse, labelFail);

                ILGenerator.Emit(OpCodes.Ldloc, valueLocal);
                EmitPatternTestBranchFalse(unionMemberPattern.Pattern, unionMemberPattern.MemberType, scope, labelFail, valueLocal);
                ILGenerator.Emit(OpCodes.Ldc_I4_1);
                ILGenerator.Emit(OpCodes.Br, labelDone);

                ILGenerator.MarkLabel(labelFail);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.MarkLabel(labelDone);
                return;
            }
        }

        if (RequiresValueTypeHandling(inputType) && inputType.TypeKind != TypeKind.Error)
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(inputType));

        inputType = Compilation.GetSpecialType(SpecialType.System_Object);

        var unionCarrierLocal = scrutineeLocal2 ?? ILGenerator.DeclareLocal(unionClrType);
        var valueLocal2 = ILGenerator.DeclareLocal(memberClrType);
        var labelSuccess = ILGenerator.DefineLabel();
        var labelTryGetFail = ILGenerator.DefineLabel();
        var labelTryGetDone = ILGenerator.DefineLabel();

        ILGenerator.Emit(OpCodes.Isinst, unionClrType);
        ILGenerator.Emit(OpCodes.Dup);
        ILGenerator.Emit(OpCodes.Brtrue, labelSuccess);
        ILGenerator.Emit(OpCodes.Pop);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Br, labelTryGetDone);

        ILGenerator.MarkLabel(labelSuccess);
        ILGenerator.Emit(unionClrType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, unionClrType);
        ILGenerator.Emit(OpCodes.Stloc, unionCarrierLocal);

        ILGenerator.Emit(unionClrType.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, unionCarrierLocal);
        ILGenerator.Emit(OpCodes.Ldloca, valueLocal2);
        ILGenerator.Emit(unionClrType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, tryGetMethod);
        ILGenerator.Emit(OpCodes.Brfalse, labelTryGetFail);

        ILGenerator.Emit(OpCodes.Ldloc, valueLocal2);
        EmitPatternTestBranchFalse(unionMemberPattern.Pattern, unionMemberPattern.MemberType, scope, labelTryGetFail, valueLocal2);
        ILGenerator.Emit(OpCodes.Ldc_I4_1);
        ILGenerator.Emit(OpCodes.Br, labelTryGetDone);

        ILGenerator.MarkLabel(labelTryGetFail);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.MarkLabel(labelTryGetDone);
    }
}
