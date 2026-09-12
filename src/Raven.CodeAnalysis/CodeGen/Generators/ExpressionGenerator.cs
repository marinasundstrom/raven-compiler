using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;

using static Raven.CodeAnalysis.CodeGen.DebugUtils;

namespace Raven.CodeAnalysis.CodeGen;

internal partial class ExpressionGenerator : Generator
{
    private static readonly DelegateConstructorCacheKeyComparer s_delegateConstructorComparer = new();
    private static int s_delegateBridgeOrdinal;

    private readonly BoundExpression _expression;
    private bool _preserveResult;
    private readonly EmitContext _rootContext;
    private readonly Dictionary<DelegateConstructorCacheKey, ConstructorInfo> _delegateConstructorCache = new(s_delegateConstructorComparer);
    private Type[]? _delegateConstructorSignature;

    private static readonly MethodInfo GetTypeFromHandleMethod = typeof(Type)
        .GetMethod(nameof(Type.GetTypeFromHandle), BindingFlags.Public | BindingFlags.Static, binder: null, new[] { typeof(RuntimeTypeHandle) }, modifiers: null)
        ?? throw new InvalidOperationException("Failed to resolve Type.GetTypeFromHandle(RuntimeTypeHandle).");

    private static readonly MethodInfo ConsoleWriteLineString = typeof(Console)
        .GetMethod(nameof(Console.WriteLine), BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) })
        ?? throw new InvalidOperationException("Failed to resolve Console.WriteLine(string).");

    private static readonly MethodInfo ConsoleWriteLineStringObject = typeof(Console)
        .GetMethod(nameof(Console.WriteLine), BindingFlags.Public | BindingFlags.Static, new[] { typeof(string), typeof(object) })
        ?? throw new InvalidOperationException("Failed to resolve Console.WriteLine(string, object).");

    private static readonly MethodInfo IndexGetOffsetMethod = typeof(Index)
        .GetMethod(nameof(Index.GetOffset), BindingFlags.Public | BindingFlags.Instance, binder: null, new[] { typeof(int) }, modifiers: null)
        ?? throw new InvalidOperationException("Failed to resolve Index.GetOffset(int).");

    private static readonly MethodInfo StringFormatStringObject = typeof(string)
        .GetMethod(nameof(string.Format), BindingFlags.Public | BindingFlags.Static, new[] { typeof(string), typeof(object) })
        ?? throw new InvalidOperationException("Failed to resolve string.Format(string, object).");

    private static readonly MethodInfo ArrayEmptyGenericMethod = typeof(Array)
        .GetMethod(nameof(Array.Empty), BindingFlags.Public | BindingFlags.Static, binder: null, Type.EmptyTypes, modifiers: null)
        ?? throw new InvalidOperationException("Failed to resolve Array.Empty<T>().");

    private static readonly MethodInfo ArrayCopyMethod = typeof(Array)
        .GetMethod(nameof(Array.Copy), BindingFlags.Public | BindingFlags.Static, binder: null, [typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int)], modifiers: null)
        ?? throw new InvalidOperationException("Failed to resolve Array.Copy(Array, int, Array, int, int).");

    public ExpressionGenerator(Generator parent, BoundExpression expression, bool preserveResult = true)
        : this(parent, expression, preserveResult ? EmitContext.Value : EmitContext.None)
    {
        _expression = expression;
        _preserveResult = preserveResult;
    }


    // New preferred ctor
    public ExpressionGenerator(Generator parent, BoundExpression expression, EmitContext context) : base(parent)
    {
        _expression = expression;
        _rootContext = context;
        _preserveResult = context.ResultKind == EmitResultKind.Value;
    }

    public override void Emit()
    {
        Emit2();
    }

    public EmitInfo Emit2()
    {
        if (!_preserveResult && _expression is BoundAssignmentExpression assignmentExpression)
        {
            EmitAssignmentExpression(assignmentExpression, preserveResult: false);
            return EmitInfo.None;
        }

        return EmitExpression(_expression);
    }

    private void EmitNullableValueExpression(BoundNullableValueExpression expr)
    {
        // For Nullable<T> instance methods it's best to call on the address to avoid
        // unnecessary copies and to work correctly for open generic Nullable<T>.
        var nullableType = expr.Operand.Type;
        var receiverClrType = ResolveClrType(nullableType);

        // Load an address for the operand (or spill once if it isn't directly addressable).
        EmitExpression(expr.Operand, emitAddress: true);
        ILGenerator.Emit(OpCodes.Call, GetNullableValueGetter(receiverClrType));
    }

    private EmitInfo EmitExpression(BoundExpression expression, bool emitAddress = false)
    {
        return EmitExpression(expression, emitAddress ? EmitContext.Address : EmitContext.Value);
    }

    private EmitInfo EmitExpression(BoundExpression expression, EmitContext context)
    {
        if (CodeGenFlags.PrintDebug && CodeGenFlags.PrintEmittedBoundNodes)
            PrintDebug($"Emitting bound expression: {expression}");

        if (context.ResultKind == EmitResultKind.Address)
        {
            var addressInfo = TryEmitAddress(expression);
            if (addressInfo.Kind != EmitValueKind.None)
                return addressInfo;

            if (expression is BoundTypeExpression)
            {
                throw new InvalidOperationException(
                    $"Cannot take the address of type expression '{expression.Type?.ToDisplayString() ?? "<type>"}'.");
            }

            // Fallback: evaluate the value once, spill to a temp, then load its managed address.
            var tmpType = ResolveClrType(expression.Type);
            var tmp = ILGenerator.DeclareLocal(tmpType);
            EmitExpression(expression, emitAddress: false);
            ILGenerator.Emit(OpCodes.Stloc, tmp);
            ILGenerator.Emit(OpCodes.Ldloca_S, tmp);
            return EmitInfo.ForAddress(local: tmp, wasSpilledToLocal: true);
        }

        var info = EmitInfo.ForValue();

        switch (expression)
        {
            case BoundPropagateExpression propagateExpression:
                EmitPropagateExpression(propagateExpression);
                break;

            case BoundBinaryExpression binaryExpression:
                EmitBinaryExpression(binaryExpression);
                break;

            case BoundUnaryExpression unaryExpression:
                EmitUnaryExpression(unaryExpression);
                break;

            case BoundIndexExpression indexExpression:
                EmitIndexExpression(indexExpression);
                break;

            case BoundRangeExpression rangeExpression:
                EmitRangeExpression(rangeExpression);
                break;

            case BoundAddressOfExpression addressOfExpression:
                EmitAddressOfExpression(addressOfExpression);
                break;

            case BoundStackAllocExpression stackAllocExpression:
                EmitStackAllocExpression(stackAllocExpression);
                break;

            case BoundDereferenceExpression dereferenceExpression:
                EmitDereferenceExpression(dereferenceExpression);
                break;

            case BoundParameterAccess parameterAccess:
                info = EmitParameterAccess(parameterAccess);
                break;

            case BoundLocalAccess localAccess:
                info = EmitLocalAccess(localAccess, context);
                break;

            case BoundPropertyAccess propertyAccess:
                EmitPropertyAccess(propertyAccess, context);
                break;

            case BoundFieldAccess fieldAccess:
                info = EmitFieldAccess(fieldAccess, context);
                break;

            case BoundMemberAccessExpression memberAccessExpression:
                EmitMemberAccessExpression(memberAccessExpression);
                break;

            case BoundPointerMemberAccessExpression pointerMemberAccess:
                info = EmitPointerMemberAccessExpression(pointerMemberAccess, context);
                break;


            case BoundConditionalAccessExpression conditionalAccess:
                EmitConditionalAccessExpression(conditionalAccess, context);
                break;

            case BoundCarrierConditionalAccessExpression carrier:
                EmitCarrierConditionalAccessExpression(carrier, context);
                break;

            case BoundInvocationExpression invocationExpression:
                EmitInvocationExpression(invocationExpression);
                break;

            case BoundLiteralExpression literalExpression:
                EmitLiteralExpression(literalExpression);
                break;

            case BoundDefaultValueExpression defaultValueExpression:
                EmitDefaultValue(defaultValueExpression.Type);
                break;

            case BoundParenthesizedExpression parenthesized:
                info = EmitExpression(parenthesized.Expression);
                break;

            case BoundConversionExpression conversionExpression:
                EmitConversionExpression(conversionExpression);
                break;

            case BoundAsExpression asExpression:
                EmitAsExpression(asExpression);
                break;

            case BoundIfExpression ifStatement:
                EmitIfExpression(ifStatement);
                break;

            case BoundBlockExpression block:
                EmitBlock(block);
                break;

            case BoundTupleExpression tupleExpression:
                EmitTupleExpression(tupleExpression);
                break;

            case BoundAssignmentExpression assignmentExpression:
                EmitAssignmentExpression(assignmentExpression);
                break;

            case BoundObjectCreationExpression objectCreationExpression:
                EmitObjectCreationExpression(objectCreationExpression);
                break;
            case BoundMatchExpression matchExpression:
                EmitMatchExpression(matchExpression, context);
                break;

            case BoundCollectionExpression collectionExpression:
                EmitCollectionExpression(collectionExpression);
                break;

            case BoundDictionaryExpression dictionaryExpression:
                EmitDictionaryExpression(dictionaryExpression);
                break;

            case BoundEmptyCollectionExpression emptyCollectionExpression:
                EmitEmptyCollectionExpression(emptyCollectionExpression);
                break;

            case BoundCollectionComprehensionExpression comprehensionExpression:
                EmitCollectionComprehensionExpression(comprehensionExpression);
                break;

            case BoundTryExpression tryExpression:
                EmitTryExpression(tryExpression);
                break;

            case BoundArrayAccessExpression boundArrayAccessExpression:
                EmitArrayAccessExpression(boundArrayAccessExpression);
                break;

            case BoundIndexerAccessExpression boundIndexerAccessExpression:
                EmitIndexerAccessExpression(boundIndexerAccessExpression);
                break;

            case BoundIsPatternExpression isPatternExpression:
                EmitIsPatternExpression(isPatternExpression, context);
                break;

            case BoundSelfExpression selfExpression:
                EmitSelfExpression(selfExpression, context);
                break;

            case BoundBaseExpression baseExpression:
                EmitBaseExpression(baseExpression, context);
                break;

            case BoundTypeOfExpression typeOfExpression:
                EmitTypeOfExpression(typeOfExpression);
                break;

            case BoundNameOfExpression nameOfExpression:
                EmitNameOfExpression(nameOfExpression);
                break;

            case BoundTypeExpression typeExpression:
                if (!TryEmitDiscriminatedUnionCaseCreation(typeExpression.Type))
                    break;

                break;

            case BoundUnionCaseExpression unionCaseExpression:
                // Should be lowered before reaching codegen, but guard for robustness.
                EmitUnionCaseExpression(unionCaseExpression);
                break;

            case BoundUnitExpression unitExpression:
                EmitUnitExpression(unitExpression);
                if (!_preserveResult)
                    info = EmitInfo.None;
                break;

            case BoundFunctionExpression lambdaExpression:
                EmitLambdaExpression(lambdaExpression);
                break;

            case BoundDelegateCreationExpression delegateCreation:
                EmitDelegateCreationExpression(delegateCreation);
                break;

            case BoundMethodGroupExpression methodGroupExpression:
                EmitMethodGroupExpression(methodGroupExpression);
                break;

            case BoundNullCoalesceExpression nullCoalesceExpression:
                EmitNullCoalesceExpression(nullCoalesceExpression);
                break;

            case BoundNullableValueExpression nullableValueExpression:
                EmitNullableValueExpression(nullableValueExpression);
                break;

            case BoundRequiredResultExpression requiredResultExpression:
                EmitRequiredResultExpression(requiredResultExpression);
                break;

            case BoundThrowExpression throwExpression:
                EmitThrowExpression(throwExpression);
                break;

            case BoundAwaitExpression awaitExpression:
                EmitAwaitExpression(awaitExpression);
                break;

            case BoundReturnExpression returnExpression:
                EmitReturnExpression(returnExpression);
                break;

            case BoundErrorExpression errorExpression:
                EmitErrorExpression(errorExpression);
                break;

            default:
                throw new NotSupportedException($"Unsupported expression type: {expression.GetType()}");
        }

        return info;
    }

    private void EmitRequiredResultExpression(BoundRequiredResultExpression e)
    {
        // RequiredResult means: this expression must materialize a value,
        // regardless of the surrounding discard context.
        var saved = _preserveResult;
        _preserveResult = true;
        try
        {
            EmitExpression(e.Operand);
        }
        finally
        {
            _preserveResult = saved;
        }
    }

    private void EmitAwaitExpression(BoundAwaitExpression awaitExpression)
    {
        if (Compilation.IsRuntimeAsyncEnabled && TryEmitRuntimeAsyncAwait(awaitExpression, out var runtimeAsyncProducesValue))
        {
            if (_preserveResult &&
                !runtimeAsyncProducesValue &&
                (awaitExpression.ResultType.SpecialType == SpecialType.System_Unit ||
                 awaitExpression.Type.SpecialType == SpecialType.System_Unit ||
                 awaitExpression.GetResultMethod.ReturnType.SpecialType == SpecialType.System_Unit))
            {
                EmitUnitValue();
            }

            return;
        }

        // Runtime-async mode keeps awaits in method bodies instead of rewriting to
        // synthesized state-machine types. Emit the await pattern directly:
        // expression.GetAwaiter().GetResult()
        EmitExpression(awaitExpression.Expression);

        var getAwaiterMethod = awaitExpression.GetAwaiterMethod;
        var getAwaiterInfo = GetMethodInfo(getAwaiterMethod);

        if (getAwaiterMethod.IsStatic)
        {
            ILGenerator.Emit(OpCodes.Call, getAwaiterInfo);
        }
        else
        {
            var getAwaiterCall = awaitExpression.Expression.Type.IsValueType ? OpCodes.Call : OpCodes.Callvirt;
            ILGenerator.Emit(getAwaiterCall, getAwaiterInfo);
        }

        var awaiterClrType = ResolveClrType(awaitExpression.AwaiterType);
        var awaiterLocal = ILGenerator.DeclareLocal(awaiterClrType);
        ILGenerator.Emit(OpCodes.Stloc, awaiterLocal);

        var getResultMethod = awaitExpression.GetResultMethod;
        var getResultInfo = GetMethodInfo(getResultMethod);

        if (getResultMethod.IsStatic)
        {
            ILGenerator.Emit(OpCodes.Ldloc, awaiterLocal);
            ILGenerator.Emit(OpCodes.Call, getResultInfo);
            return;
        }

        if (awaitExpression.AwaiterType.IsValueType)
        {
            ILGenerator.Emit(OpCodes.Ldloca, awaiterLocal);
            ILGenerator.Emit(OpCodes.Call, getResultInfo);
            return;
        }

        ILGenerator.Emit(OpCodes.Ldloc, awaiterLocal);
        ILGenerator.Emit(OpCodes.Callvirt, getResultInfo);
    }

    private bool TryEmitRuntimeAsyncAwait(BoundAwaitExpression awaitExpression, out bool producesValue)
    {
        producesValue = false;

        if (Compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.AsyncHelpers") is not INamedTypeSymbol
            {
                TypeKind: not TypeKind.Error
            })
        {
            return false;
        }

        var asyncHelpersType = typeof(AsyncTaskMethodBuilder).Assembly.GetType("System.Runtime.CompilerServices.AsyncHelpers", throwOnError: false)
            ?? Type.GetType("System.Runtime.CompilerServices.AsyncHelpers, System.Private.CoreLib", throwOnError: false);
        if (asyncHelpersType is null)
            return false;

        var awaitedTypeSymbol = awaitExpression.Expression.Type.GetNonNullableType();
        var helper = ResolveRuntimeAsyncAwaitHelper(asyncHelpersType, awaitedTypeSymbol, awaitExpression.ResultType);
        if (helper is null)
            return false;

        EmitExpression(awaitExpression.Expression);
        ILGenerator.Emit(OpCodes.Call, helper);
        producesValue = helper.ReturnType != typeof(void);
        return true;
    }

    private MethodInfo? ResolveRuntimeAsyncAwaitHelper(
        Type asyncHelpersType,
        ITypeSymbol awaitedTypeSymbol,
        ITypeSymbol resultType)
    {
        var methods = asyncHelpersType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(static m => string.Equals(m.Name, "Await", StringComparison.Ordinal))
            .ToArray();

        if (methods.Length == 0)
            return null;

        static bool IsParameterMetadataName(MethodInfo method, string metadataName)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                return false;

            var parameterType = parameters[0].ParameterType;
            return string.Equals(parameterType.FullName, metadataName, StringComparison.Ordinal);
        }

        static bool IsGenericParameterDefinitionMetadataName(MethodInfo method, string metadataName)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                return false;

            var parameterType = parameters[0].ParameterType;
            if (!parameterType.IsGenericType)
                return false;

            var definition = parameterType.GetGenericTypeDefinition();
            return string.Equals(definition.FullName, metadataName, StringComparison.Ordinal);
        }

        var awaitedNamedType = awaitedTypeSymbol as INamedTypeSymbol;
        if (awaitedNamedType is null)
            return null;

        var awaitedDefinition = awaitedNamedType.OriginalDefinition as INamedTypeSymbol ?? awaitedNamedType;
        var awaitedMetadataName = awaitedDefinition.ToFullyQualifiedMetadataName();

        // Prefer symbol identity for BCL types that have SpecialType entries.
        if (awaitedDefinition.SpecialType == SpecialType.System_Threading_Tasks_Task)
        {
            return methods.FirstOrDefault(m =>
                !m.IsGenericMethodDefinition &&
                IsParameterMetadataName(m, "System.Threading.Tasks.Task"));
        }

        if (awaitedDefinition.SpecialType == SpecialType.System_Threading_Tasks_Task_T)
        {
            var resultClrType = ResolveClrType(resultType.GetNonNullableType());
            var genericTaskAwait = methods.FirstOrDefault(m =>
                m.IsGenericMethodDefinition &&
                IsGenericParameterDefinitionMetadataName(m, "System.Threading.Tasks.Task`1"));

            return genericTaskAwait?.MakeGenericMethod(resultClrType);
        }

        if (string.Equals(awaitedMetadataName, "System.Threading.Tasks.ValueTask", StringComparison.Ordinal))
        {
            return methods.FirstOrDefault(m =>
                !m.IsGenericMethodDefinition &&
                IsParameterMetadataName(m, "System.Threading.Tasks.ValueTask"));
        }

        if (string.Equals(awaitedMetadataName, "System.Threading.Tasks.ValueTask`1", StringComparison.Ordinal))
        {
            var resultClrType = ResolveClrType(resultType.GetNonNullableType());
            var genericValueTaskAwait = methods.FirstOrDefault(m =>
                m.IsGenericMethodDefinition &&
                IsGenericParameterDefinitionMetadataName(m, "System.Threading.Tasks.ValueTask`1"));

            return genericValueTaskAwait?.MakeGenericMethod(resultClrType);
        }

        // .ConfigureAwait(...) paths in net11 runtime async.
        if (string.Equals(awaitedMetadataName, "System.Runtime.CompilerServices.ConfiguredTaskAwaitable", StringComparison.Ordinal))
        {
            return methods.FirstOrDefault(m =>
                !m.IsGenericMethodDefinition &&
                IsParameterMetadataName(m, "System.Runtime.CompilerServices.ConfiguredTaskAwaitable"));
        }

        if (string.Equals(awaitedMetadataName, "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1", StringComparison.Ordinal))
        {
            var resultClrType = ResolveClrType(resultType.GetNonNullableType());
            var genericConfiguredTaskAwait = methods.FirstOrDefault(m =>
                m.IsGenericMethodDefinition &&
                IsGenericParameterDefinitionMetadataName(m, "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1"));

            return genericConfiguredTaskAwait?.MakeGenericMethod(resultClrType);
        }

        if (string.Equals(awaitedMetadataName, "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable", StringComparison.Ordinal))
        {
            return methods.FirstOrDefault(m =>
                !m.IsGenericMethodDefinition &&
                IsParameterMetadataName(m, "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable"));
        }

        if (string.Equals(awaitedMetadataName, "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1", StringComparison.Ordinal))
        {
            var resultClrType = ResolveClrType(resultType.GetNonNullableType());
            var genericConfiguredValueTaskAwait = methods.FirstOrDefault(m =>
                m.IsGenericMethodDefinition &&
                IsGenericParameterDefinitionMetadataName(m, "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1"));

            return genericConfiguredValueTaskAwait?.MakeGenericMethod(resultClrType);
        }

        return null;
    }

    private EmitInfo TryEmitAddress(BoundExpression expression)
    {
        switch (expression)
        {
            /*
            case BoundDereferenceExpression dereference:
                // Address-of for a dereference: the reference expression already produces the address.
                // Critical for pointer-member assignments so we don't materialize a copy via ldobj.
                EmitExpression(dereference.Reference);
                return EmitInfo.ForAddress();*/

            case BoundPointerMemberAccessExpression pointerMemberAccess:
                // Address-of for `x->A` when A is a field: load the pointer (address) and take field address.
                if (pointerMemberAccess.Member is not IFieldSymbol pField)
                    return EmitInfo.None;

                EmitExpression(pointerMemberAccess.PointerReceiver);
                ILGenerator.Emit(OpCodes.Ldflda, GetField(pField));

                if (TryGetAsyncInvestigationFieldLabel(pField, out var pFieldLabel))
                    EmitAsyncInvestigationAddressLogPreservingPointer(pFieldLabel);

                return EmitInfo.ForAddress(pField);

            case BoundLocalAccess localAccess:
                if (TryEmitCapturedVariableLoad(localAccess.Local))
                    return EmitInfo.None;

                var localBuilder = GetLocal(localAccess.Local);
                if (localBuilder is null)
                    throw new InvalidOperationException($"Missing local builder for '{localAccess.Local.Name}'");

                ILGenerator.Emit(OpCodes.Ldloca_S, localBuilder);
                return EmitInfo.ForAddress(localAccess.Local, localBuilder);

            case BoundParameterAccess parameterAccess:
                if (TryEmitCapturedVariableLoad(parameterAccess.Parameter))
                    return EmitInfo.None;

                EmitParameterForByRefUse(parameterAccess.Parameter);
                return EmitInfo.ForAddress(parameterAccess.Parameter);

            case BoundSelfExpression when !MethodSymbol.IsStatic && MethodSymbol.ContainingType?.IsValueType == true:
                ILGenerator.Emit(OpCodes.Ldarg_0);
                return EmitInfo.ForAddress();

            case BoundFieldAccess fieldAccess:
                // Static fields are always directly addressable.
                if (fieldAccess.Field.IsStatic)
                {
                    ILGenerator.Emit(OpCodes.Ldsflda, GetField(fieldAccess.Field));
                    if (TryGetAsyncInvestigationFieldLabel(fieldAccess.Field, out var staticFieldLabel))
                        EmitAsyncInvestigationAddressLogPreservingPointer(staticFieldLabel);
                    return EmitInfo.ForAddress(fieldAccess.Field);
                }

                // Instance field: need an addressable receiver for ldflda.
                if (fieldAccess.Receiver is not null)
                {
                    // Prefer address for value-type receivers to avoid copies.
                    if (!TryEmitValueTypeReceiverAddress(fieldAccess.Receiver, fieldAccess.Receiver.Type, fieldAccess.Field.ContainingType))
                    {
                        EmitExpression(fieldAccess.Receiver, emitAddress: fieldAccess.Field.ContainingType?.IsValueType == true);

                        if (fieldAccess.Field.ContainingType?.IsValueType == true)
                            EmitValueTypeAddressIfNeeded(fieldAccess.Receiver.Type, fieldAccess.Field.ContainingType);
                    }
                }
                else
                {
                    if (MethodSymbol.IsStatic)
                        throw new NotSupportedException($"Cannot take address of instance field '{fieldAccess.Field.Name}' in a static context.");

                    ILGenerator.Emit(OpCodes.Ldarg_0);
                }

                ILGenerator.Emit(OpCodes.Ldflda, GetField(fieldAccess.Field));
                if (TryGetAsyncInvestigationFieldLabel(fieldAccess.Field, out var fieldLabel))
                    EmitAsyncInvestigationAddressLogPreservingPointer(fieldLabel);
                return EmitInfo.ForAddress(fieldAccess.Field);

            case BoundArrayAccessExpression arrayAccess:
                // Address of array element.
                EmitArrayElementAddress(arrayAccess);
                return EmitInfo.ForAddress();

            // If you later introduce ref-return properties/indexers, handle them here.

            default:
                return EmitInfo.None;
        }
    }

    private bool TryEmitDiscriminatedUnionCaseCreation(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol namedType)
            return false;

        if (namedType.TryGetUnionCase() is null &&
            namedType.ContainingType?.TryGetUnion() is null)
        {
            return false;
        }

        var parameterlessCtor = namedType.Constructors.FirstOrDefault(static ctor => ctor.Parameters.Length == 0);
        if (parameterlessCtor is not null)
        {
            EmitObjectCreationExpression(new BoundObjectCreationExpression(parameterlessCtor, ImmutableArray<BoundExpression>.Empty));
            return true;
        }

        // Support unit-payload elision: `.Ok` should construct `Ok(())`.
        var unitPayloadCtor = namedType.Constructors.FirstOrDefault(ctor =>
            ctor.Parameters.Length == 1 &&
            ctor.Parameters[0].Type.SpecialType == SpecialType.System_Unit);
        if (unitPayloadCtor is null)
            return false;

        var unitType = Compilation.GetSpecialType(SpecialType.System_Unit);
        EmitObjectCreationExpression(
            new BoundObjectCreationExpression(
                unitPayloadCtor,
                ImmutableArray.Create<BoundExpression>(new BoundUnitExpression(unitType))));
        return true;
    }

    /// <summary>
    /// Fallback emitter for <see cref="BoundUnionCaseExpression"/> nodes that were not lowered.
    /// Constructs the case instance and then emits the DU tag+payload assignment inline.
    /// </summary>
    private void EmitUnionCaseExpression(BoundUnionCaseExpression node)
    {
        // Resolve the constructor.
        var ctor = node.CaseConstructor
            ?? node.CaseType.Constructors.FirstOrDefault(static c => c.Parameters.Length == 0)
            ?? node.CaseType.Constructors.FirstOrDefault(static c =>
                c.Parameters.Length == 1 &&
                c.Parameters[0].Type.SpecialType == SpecialType.System_Unit);

        if (ctor is null)
            throw new InvalidOperationException(
                $"EmitUnionCaseExpression: no constructor found on '{node.CaseType.Name}'.");

        var args = node.Arguments;
        if (ctor.Parameters.Length == 1 && args.IsDefaultOrEmpty)
        {
            var unitType = ctor.Parameters[0].Type;
            args = ImmutableArray.Create<BoundExpression>(new BoundUnitExpression(unitType));
        }

        var caseCreation = new BoundObjectCreationExpression(ctor, args);

        // A case can be named explicitly as the target type in Raven source. In that
        // form the bound expression represents the case value itself and must not be
        // wrapped in its carrier union.
        if (node.UnionType.TryGetUnionCase() is not null)
        {
            EmitObjectCreationExpression(caseCreation);
            return;
        }

        if (!node.UnionType.TryGetUnionCarrierConstructor(node.CaseType, out var unionCtor))
        {
            throw new InvalidOperationException(
                $"EmitUnionCaseExpression: no conversion found from '{node.CaseType}' to '{node.UnionType}'.");
        }

        EmitObjectCreationExpression(
            new BoundObjectCreationExpression(
                unionCtor,
                ImmutableArray.Create<BoundExpression>(caseCreation)));
    }

    private void EmitSelfExpression(BoundSelfExpression selfExpression, EmitContext context)
    {
        if (TryEmitCapturedVariableLoad(selfExpression.Symbol ?? selfExpression.Type))
            return;

        ILGenerator.Emit(OpCodes.Ldarg_0);

        if (!MethodSymbol.IsStatic && MethodSymbol.ContainingType?.IsValueType == true)
            ILGenerator.Emit(OpCodes.Ldobj, InstantiateType(ResolveClrType(selfExpression.Type)));
    }

    private void EmitBaseExpression(BoundBaseExpression baseExpression, EmitContext context)
    {
        if (TryEmitCapturedVariableLoad(baseExpression.Symbol ?? baseExpression.Type))
            return;

        ILGenerator.Emit(OpCodes.Ldarg_0);
    }

    private void EmitTypeOfExpression(BoundTypeOfExpression typeOfExpression)
    {
        if (typeOfExpression.SystemType.SpecialType == SpecialType.System_Int32)
        {
            ILGenerator.Emit(OpCodes.Sizeof, ResolveClrType(typeOfExpression.OperandType));
            return;
        }

        var operandClrType = ResolveClrType(typeOfExpression.OperandType);
        if (!IsOpenGenericTypeOfOperand(typeOfExpression.OperandType, operandClrType))
            operandClrType = Generator.InstantiateType(operandClrType);

        ILGenerator.Emit(OpCodes.Ldtoken, operandClrType);
        ILGenerator.Emit(OpCodes.Call, GetTypeFromHandleMethod);
    }

    private static bool IsOpenGenericTypeOfOperand(ITypeSymbol operandType, Type operandClrType)
    {
        if (operandType is not INamedTypeSymbol namedType)
            return false;

        return namedType.IsUnboundGenericType ||
               operandClrType.IsGenericTypeDefinition;
    }

    private void EmitNameOfExpression(BoundNameOfExpression nameOfExpression)
    {
        ILGenerator.Emit(OpCodes.Ldstr, nameOfExpression.ToString());
    }

    private void EmitDelegateCreationExpression(BoundDelegateCreationExpression delegateCreation)
    {
        var method = delegateCreation.Method
            ?? throw new InvalidOperationException("Delegate creation requires a resolved target method.");

        if (delegateCreation.DelegateType is not INamedTypeSymbol delegateTypeSymbol)
            throw new InvalidOperationException("Delegate creation requires a delegate type.");

        EmitDelegateCreation(delegateCreation.Receiver, method, delegateTypeSymbol);
    }

    private void EmitMethodGroupExpression(BoundMethodGroupExpression methodGroup)
    {
        var method = methodGroup.SelectedMethod;
        var delegateType = methodGroup.DelegateType ?? methodGroup.DelegateTypeFactory?.Invoke();

        if (method is null ||
            delegateType is not INamedTypeSymbol delegateTypeSymbol ||
            delegateTypeSymbol.TypeKind != TypeKind.Delegate)
        {
            EmitDefaultValue(methodGroup.MethodGroupType);
            return;
        }

        EmitDelegateCreation(methodGroup.Receiver, method, delegateTypeSymbol);
    }

    private void EmitErrorExpression(BoundErrorExpression errorExpression)
    {
        if (!_preserveResult)
            return;

        if (errorExpression.Type.SpecialType == SpecialType.System_Void)
            return;

        EmitDefaultValue(errorExpression.Type);
    }

    private void EmitThrowExpression(BoundThrowExpression throwExpression)
    {
        var localsToDispose = EnumerateLocalsToDispose().ToImmutableArray();
        var expression = throwExpression.Expression;
        var expressionType = expression.Type;
        IILocal? resultTemp = null;
        var hasValueOnStack = true;

        var info = EmitExpression(expression);

        if (localsToDispose.Length > 0 && expressionType is { TypeKind: not TypeKind.Error })
        {
            var clrType = ResolveClrType(expressionType);
            resultTemp = SpillValueToLocalIfNeeded(clrType, info, keepValueOnStack: false);
            hasValueOnStack = false;
        }
        else if (localsToDispose.Length > 0)
        {
            ILGenerator.Emit(OpCodes.Pop);
            hasValueOnStack = false;
        }

        EmitDispose(localsToDispose);

        if (resultTemp is not null)
        {
            ILGenerator.Emit(OpCodes.Ldloc, resultTemp);
            hasValueOnStack = true;
        }
        else if (!hasValueOnStack)
        {
            ILGenerator.Emit(OpCodes.Ldnull);
            hasValueOnStack = true;
        }

        if (expressionType is { TypeKind: not TypeKind.Error })
        {
            if (expressionType.IsValueType)
                ILGenerator.Emit(OpCodes.Box, ResolveClrType(expressionType));

            var exceptionType = Compilation.GetTypeByMetadataName("System.Exception");
            if (exceptionType is { TypeKind: not TypeKind.Error })
                ILGenerator.Emit(OpCodes.Castclass, ResolveClrType(exceptionType));
        }
        else if (hasValueOnStack)
        {
            ILGenerator.Emit(OpCodes.Pop);
            ILGenerator.Emit(OpCodes.Ldnull);
        }

        ILGenerator.Emit(OpCodes.Throw);
    }

    private void EmitReturnExpression(BoundReturnExpression returnExpression)
    {
        var returnStatement = new BoundReturnStatement(returnExpression.Expression);
        new StatementGenerator(this, returnStatement).Emit();
    }

    private void EmitDelegateCreation(BoundExpression? receiver, IMethodSymbol method, INamedTypeSymbol delegateType)
    {
        if (delegateType is SynthesizedDelegateTypeSymbol synthesized)
            MethodGenerator.TypeGenerator.CodeGen.GetTypeBuilder(synthesized);

        var delegateClrType = ResolveClrType(delegateType);
        var ctor = GetDelegateConstructor(delegateType, delegateClrType);

        var methodInfo = GetMethodInfo(method);
        if (TryEmitDelegateBridge(receiver, method, methodInfo, delegateType) is { } bridgeMethod)
        {
            ILGenerator.Emit(OpCodes.Ldnull);
            ILGenerator.Emit(OpCodes.Ldftn, bridgeMethod);
            ILGenerator.Emit(OpCodes.Newobj, ctor);
            return;
        }

        if (method.IsStatic)
        {
            ILGenerator.Emit(OpCodes.Ldnull);
            ILGenerator.Emit(OpCodes.Ldftn, methodInfo);
        }
        else
        {
            if (receiver is null)
                throw new InvalidOperationException("Instance delegate creation requires a receiver.");

            EmitExpression(receiver);

            if (receiver.Type is { IsValueType: true } receiverType)
                ILGenerator.Emit(OpCodes.Box, ResolveClrType(receiverType));

            if (method.IsVirtual && method.ContainingType is { IsValueType: false })
            {
                ILGenerator.Emit(OpCodes.Dup);
                ILGenerator.Emit(OpCodes.Ldvirtftn, methodInfo);
            }
            else
            {
                ILGenerator.Emit(OpCodes.Ldftn, methodInfo);
            }
        }

        ILGenerator.Emit(OpCodes.Newobj, ctor);
    }

    private MethodInfo? TryEmitDelegateBridge(
        BoundExpression? receiver,
        IMethodSymbol method,
        MethodInfo methodInfo,
        INamedTypeSymbol delegateType)
    {
        if (!method.IsStatic || receiver is not null)
            return null;

        var invoke = delegateType.GetDelegateInvokeMethod();
        if (invoke is null)
            return null;

        if (invoke.Parameters.Length != method.Parameters.Length)
            return null;

        if (!RequiresDelegateBridge(method, invoke))
            return null;

        if (MethodGenerator.TypeGenerator.TypeBuilder is not TypeBuilder bridgeOwner)
            return null;

        var parameterTypes = invoke.Parameters
            .Select(p => ResolveClrType(p.Type))
            .ToArray();
        var returnType = invoke.ReturnType.SpecialType == SpecialType.System_Unit
            ? ResolveClrType(Compilation.GetSpecialType(SpecialType.System_Void))
            : ResolveClrType(invoke.ReturnType);

        var name = $"<>__methodGroupBridge{Interlocked.Increment(ref s_delegateBridgeOrdinal)}";
        var bridgeBuilder = bridgeOwner.DefineMethod(
            name,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            CallingConventions.Standard,
            returnType,
            parameterTypes);
        var compilerGeneratedAttr = MethodGenerator.TypeGenerator.CodeGen.CreateCompilerGeneratedAttributeBuilder();
        if (compilerGeneratedAttr is not null)
            bridgeBuilder.SetCustomAttribute(compilerGeneratedAttr);

        var il = bridgeBuilder.GetILGenerator();

        for (var i = 0; i < invoke.Parameters.Length; i++)
        {
            var sourceType = invoke.Parameters[i].Type;
            var targetType = method.Parameters[i].Type;

            il.Emit(OpCodes.Ldarg, i);
            EmitBridgeConversion(il, sourceType, targetType);
        }

        il.Emit(OpCodes.Call, methodInfo);
        EmitBridgeReturnConversion(il, method.ReturnType, invoke.ReturnType);
        il.Emit(OpCodes.Ret);

        return bridgeBuilder;
    }

    private bool RequiresDelegateBridge(IMethodSymbol method, IMethodSymbol invoke)
    {
        if (method.Parameters.Length != invoke.Parameters.Length)
            return false;

        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var methodParameter = method.Parameters[i];
            var delegateParameter = invoke.Parameters[i];

            if (methodParameter.RefKind != delegateParameter.RefKind)
                return false;

            if (SymbolEqualityComparer.Default.Equals(delegateParameter.Type, methodParameter.Type))
                continue;

            var conversion = Compilation.ClassifyConversion(delegateParameter.Type, methodParameter.Type);
            if (!conversion.Exists || !conversion.IsImplicit || conversion.IsNumeric || conversion.IsUserDefined)
                return false;

            return true;
        }

        if (SymbolEqualityComparer.Default.Equals(method.ReturnType, invoke.ReturnType))
            return false;

        if (invoke.ReturnType.SpecialType == SpecialType.System_Void &&
            method.ReturnType.SpecialType is SpecialType.System_Void or SpecialType.System_Unit)
            return method.ReturnType.SpecialType == SpecialType.System_Unit;

        var returnConversion = Compilation.ClassifyConversion(method.ReturnType, invoke.ReturnType);
        if (!returnConversion.Exists || !returnConversion.IsImplicit || returnConversion.IsNumeric || returnConversion.IsUserDefined)
            return false;

        return true;
    }

    private void EmitBridgeConversion(ILGenerator il, ITypeSymbol sourceType, ITypeSymbol targetType)
    {
        if (SymbolEqualityComparer.Default.Equals(sourceType, targetType))
            return;

        var conversion = Compilation.ClassifyConversion(sourceType, targetType);
        if (!conversion.Exists || !conversion.IsImplicit)
            throw new InvalidOperationException($"Cannot bridge delegate parameter conversion from '{sourceType}' to '{targetType}'.");

        var sourceClr = ResolveClrType(sourceType);
        var targetClr = ResolveClrType(targetType);

        if (conversion.IsBoxing || (sourceType.IsValueType && !targetType.IsValueType))
        {
            il.Emit(OpCodes.Box, sourceClr);
            if (!SymbolEqualityComparer.Default.Equals(sourceType, targetType))
                il.Emit(OpCodes.Castclass, targetClr);
            return;
        }

        if (conversion.IsReference)
        {
            il.Emit(OpCodes.Castclass, targetClr);
            return;
        }
    }

    private void EmitBridgeReturnConversion(ILGenerator il, ITypeSymbol methodReturnType, ITypeSymbol delegateReturnType)
    {
        if (delegateReturnType.SpecialType == SpecialType.System_Void)
        {
            if (methodReturnType.SpecialType == SpecialType.System_Unit)
                il.Emit(OpCodes.Pop);

            return;
        }

        if (SymbolEqualityComparer.Default.Equals(methodReturnType, delegateReturnType))
            return;

        var conversion = Compilation.ClassifyConversion(methodReturnType, delegateReturnType);
        if (!conversion.Exists || !conversion.IsImplicit)
            throw new InvalidOperationException($"Cannot bridge delegate return conversion from '{methodReturnType}' to '{delegateReturnType}'.");

        var methodReturnClr = ResolveClrType(methodReturnType);
        var delegateReturnClr = ResolveClrType(delegateReturnType);

        if (conversion.IsBoxing || (methodReturnType.IsValueType && !delegateReturnType.IsValueType))
        {
            il.Emit(OpCodes.Box, methodReturnClr);
            if (!SymbolEqualityComparer.Default.Equals(methodReturnType, delegateReturnType))
                il.Emit(OpCodes.Castclass, delegateReturnClr);
            return;
        }

        if (conversion.IsReference)
            il.Emit(OpCodes.Castclass, delegateReturnClr);
    }

    private void EmitCapturedLambda(
        BoundFunctionExpression lambdaExpression,
        TypeGenerator.LambdaClosure closure,
        MethodInfo methodInfo,
        ITypeSymbol delegateTypeSymbol,
        Type delegateType)
    {
        var delegateCtor = GetDelegateConstructor(delegateTypeSymbol, delegateType);
        var runtimeClosureType = closure.GetRuntimeType(MethodGenerator.TypeGenerator.CodeGen);
        var runtimeClosureCtor = runtimeClosureType == closure.TypeBuilder
            ? closure.Constructor
            : TypeBuilder.GetConstructor(runtimeClosureType, closure.Constructor);
        var runtimeLambdaMethod = runtimeClosureType == closure.TypeBuilder || methodInfo.DeclaringType != closure.TypeBuilder
            ? methodInfo
            : TypeBuilder.GetMethod(runtimeClosureType, methodInfo);

        // If the closure is the shared outer-method closure (reference-based capture), reuse the
        // already-allocated local instead of creating a new instance and snapshotting values.
        if (MethodBodyGenerator.OuterMethodClosure is { } outerClosure &&
            ReferenceEquals(outerClosure, closure) &&
            MethodBodyGenerator.OuterMethodClosureLocal is { } outerLocal)
        {
            ILGenerator.Emit(OpCodes.Ldloc, outerLocal);
            ILGenerator.Emit(OpCodes.Ldftn, runtimeLambdaMethod);
            ILGenerator.Emit(OpCodes.Newobj, delegateCtor);
            return;
        }

        if (MethodBodyGenerator.LambdaClosure is { } currentLambdaClosure &&
            ReferenceEquals(currentLambdaClosure, closure))
        {
            MethodBodyGenerator.EmitLoadClosure();
            ILGenerator.Emit(OpCodes.Ldftn, runtimeLambdaMethod);
            ILGenerator.Emit(OpCodes.Newobj, delegateCtor);
            return;
        }

        // Fallback (value-based capture): create a new closure instance and copy captured values.
        var closureLocal = ILGenerator.DeclareLocal(runtimeClosureType);
        ILGenerator.Emit(OpCodes.Newobj, runtimeClosureCtor);
        ILGenerator.Emit(OpCodes.Stloc, closureLocal);

        var capturedSymbols = closure.CapturedSymbols
            .Where(captured => !IsDeclaredByLambda(captured, lambdaExpression))
            .ToArray();
        for (var i = 0; i < capturedSymbols.Length; i++)
        {
            var captured = capturedSymbols[i];

            ILGenerator.Emit(OpCodes.Ldloc, closureLocal);
            EmitCapturedValue(captured);
            var closureField = closure.GetField(captured);
            var runtimeField = runtimeClosureType == closure.TypeBuilder
                ? closureField
                : TypeBuilder.GetField(runtimeClosureType, closureField);
            ILGenerator.Emit(OpCodes.Stfld, runtimeField);
        }

        ILGenerator.Emit(OpCodes.Ldloc, closureLocal);
        ILGenerator.Emit(OpCodes.Ldftn, runtimeLambdaMethod);
        ILGenerator.Emit(OpCodes.Newobj, delegateCtor);
    }

    private static bool IsDeclaredByLambda(ISymbol? symbol, BoundFunctionExpression lambdaExpression)
    {
        if (symbol is null)
            return false;

        foreach (var parameter in lambdaExpression.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(symbol, parameter) ||
                HaveSameDeclaration(symbol, parameter))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HaveSameDeclaration(ISymbol left, ISymbol right)
    {
        foreach (var leftReference in left.DeclaringSyntaxReferences)
        {
            foreach (var rightReference in right.DeclaringSyntaxReferences)
            {
                if (leftReference.SyntaxTree == rightReference.SyntaxTree &&
                    leftReference.Span == rightReference.Span)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void EmitCapturedValue(ISymbol symbol)
    {
        if (TryEmitCapturedClosureField(symbol))
            return;

        switch (symbol)
        {
            case ILocalSymbol localSymbol:
                {
                    var localBuilder = GetLocal(localSymbol)
                        ?? throw new InvalidOperationException($"Missing local builder for captured local '{localSymbol.Name}'.");
                    ILGenerator.Emit(OpCodes.Ldloc, localBuilder);
                    break;
                }
            case IParameterSymbol parameterSymbol:
                {
                    if (string.Equals(parameterSymbol.Name, "self", StringComparison.Ordinal))
                    {
                        if (MethodSymbol.IsStatic)
                            ILGenerator.Emit(OpCodes.Ldnull);
                        else
                            ILGenerator.Emit(OpCodes.Ldarg_0);
                        break;
                    }

                    var declaredParameters = MethodSymbol.Parameters
                        .Where(parameter =>
                            SymbolEqualityComparer.Default.Equals(parameter, parameterSymbol) ||
                            string.Equals(parameter.Name, parameterSymbol.Name, StringComparison.Ordinal))
                        .ToArray();
                    if (declaredParameters.Length != 1)
                    {
                        if (MethodSymbol.IsStatic)
                            ILGenerator.Emit(OpCodes.Ldnull);
                        else
                            ILGenerator.Emit(OpCodes.Ldarg_0);
                        break;
                    }

                    var parameterBuilder = MethodGenerator.GetParameterBuilder(declaredParameters[0]);
                    var position = parameterBuilder.Position;
                    if (MethodSymbol.IsStatic)
                        position -= 1;
                    ILGenerator.Emit(OpCodes.Ldarg, position);
                    break;
                }
            case ITypeSymbol:
                {
                    if (MethodSymbol.IsStatic)
                        throw new InvalidOperationException("Cannot capture 'self' in a static context.");

                    ILGenerator.Emit(OpCodes.Ldarg_0);
                    break;
                }
            default:
                throw new NotSupportedException($"Capturing symbol '{symbol}' is not supported.");
        }
    }

    private bool TryEmitCapturedClosureField(ISymbol symbol)
    {
        if (!MethodBodyGenerator.TryGetCapturedField(symbol, out var fieldBuilder, out var fromStateMachine))
            return false;

        if (fromStateMachine)
            ILGenerator.Emit(OpCodes.Ldarg_0);
        else
            MethodBodyGenerator.EmitLoadClosure();
        ILGenerator.Emit(OpCodes.Ldfld, fieldBuilder);
        return true;
    }

    private bool TryEmitCapturedVariableLoad(ISymbol? symbol)
    {
        if (symbol is null)
            return false;

        if (!MethodBodyGenerator.TryGetCapturedField(symbol, out var fieldBuilder, out var fromStateMachine))
            return false;

        if (fromStateMachine)
            ILGenerator.Emit(OpCodes.Ldarg_0);
        else
            MethodBodyGenerator.EmitLoadClosure();
        ILGenerator.Emit(OpCodes.Ldfld, fieldBuilder);
        return true;
    }

    private bool TryEmitCapturedAssignment(ISymbol symbol, BoundExpression value)
    {
        if (!MethodBodyGenerator.TryGetCapturedField(symbol, out var fieldBuilder, out var fromStateMachine))
            return false;

        if (fromStateMachine)
            ILGenerator.Emit(OpCodes.Ldarg_0);
        else
            MethodBodyGenerator.EmitLoadClosure();
        EmitExpression(value);

        if (symbol is ILocalSymbol localSymbol &&
            value.Type is { IsValueType: true } &&
            localSymbol.Type is not null &&
            localSymbol.Type.SpecialType is SpecialType.System_Object)
        {
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(value.Type));
        }

        ILGenerator.Emit(OpCodes.Stfld, fieldBuilder);
        return true;
    }

    private void EmitLambdaExpression(BoundFunctionExpression lambdaExpression)
    {
        if (lambdaExpression.Symbol is not ILambdaSymbol lambdaSymbol)
            throw new InvalidOperationException("Lambda symbol missing.");
        if (lambdaSymbol is SourceLambdaSymbol expressionTreeLambda && expressionTreeLambda.IsExpressionTreeLambda)
        {
            throw new InvalidOperationException("Expression-tree lambda reached delegate emission. Expected lowering to Expression.* APIs.");
        }

        var typeGenerator = MethodGenerator.TypeGenerator;
        var lambdaGenerator = typeGenerator.GetMethodGenerator(lambdaSymbol);
        TypeGenerator.LambdaClosure? closure = null;
        if (lambdaSymbol is SourceLambdaSymbol sourceLambda)
            closure = typeGenerator.EnsureLambdaClosure(sourceLambda);

        if (lambdaGenerator is null)
        {
            lambdaGenerator = new MethodGenerator(typeGenerator, lambdaSymbol, typeGenerator.CodeGen.ILBuilderFactory);
            if (closure is not null)
                lambdaGenerator.SetLambdaClosure(closure);

            typeGenerator.Add(lambdaSymbol, lambdaGenerator);
            lambdaGenerator.DefineMethodBuilder();

            if (lambdaSymbol is SourceLambdaSymbol sourceLambdaSymbol)
                typeGenerator.CodeGen.AddMemberBuilder(sourceLambdaSymbol, lambdaGenerator.MethodBase);
        }
        else if (closure is not null)
        {
            lambdaGenerator.SetLambdaClosure(closure);
        }

        if (!lambdaGenerator.HasEmittedBody)
            lambdaGenerator.EmitLambdaBody(lambdaExpression, closure);

        if (!_preserveResult)
            return;

        if (lambdaGenerator.MethodBase is not MethodInfo methodInfo)
            throw new InvalidOperationException("Expected a method-backed lambda.");

        var delegateTypeSymbol = lambdaExpression.DelegateType
            ?? throw new InvalidOperationException("Lambda delegate type missing.");
        var delegateType = ResolveClrType(delegateTypeSymbol);
        delegateType = CloseOpenDelegateType(delegateType, methodInfo);

        if (closure is null && !typeGenerator.TryGetLambdaClosure(lambdaSymbol, out closure))
            throw new InvalidOperationException("Missing closure information for lambda.");

        EmitCapturedLambda(lambdaExpression, closure!, methodInfo, delegateTypeSymbol, delegateType);
    }

    private static Type CloseOpenDelegateType(Type delegateType, MethodInfo lambdaMethod)
    {
        if (!delegateType.ContainsGenericParameters)
            return delegateType;

        var parameterTypes = lambdaMethod.GetParameters().Select(p => p.ParameterType).ToArray();
        if (parameterTypes.Any(p => p.IsByRef))
            return delegateType;

        if (lambdaMethod.ReturnType == typeof(void))
        {
            if (parameterTypes.Length == 0)
                return typeof(Action);

            if (parameterTypes.Length > 16)
                return delegateType;

            var actionDefinition = Type.GetType($"System.Action`{parameterTypes.Length}", throwOnError: false);
            if (actionDefinition is null)
                return delegateType;

            return actionDefinition.MakeGenericType(parameterTypes);
        }

        if (parameterTypes.Length > 15)
            return delegateType;

        var funcArity = parameterTypes.Length + 1;
        var funcDefinition = Type.GetType($"System.Func`{funcArity}", throwOnError: false);
        if (funcDefinition is null)
            return delegateType;

        var typeArguments = new Type[funcArity];
        for (var i = 0; i < parameterTypes.Length; i++)
            typeArguments[i] = parameterTypes[i];
        typeArguments[funcArity - 1] = lambdaMethod.ReturnType;

        return funcDefinition.MakeGenericType(typeArguments);
    }

    private ConstructorInfo GetDelegateConstructor(ITypeSymbol delegateType, Type delegateClrType)
    {
        var cacheKey = CreateDelegateConstructorCacheKey(delegateType, delegateClrType);

        if (_delegateConstructorCache.TryGetValue(cacheKey, out var ctor))
            return ctor;

        if (delegateType is INamedTypeSymbol namedDelegate)
        {
            ctor = TryResolveDelegateConstructor(namedDelegate);
        }

        if (ctor is null)
        {
            _delegateConstructorSignature ??=
            [
                ResolveClrType(Compilation.GetSpecialType(SpecialType.System_Object)),
                ResolveClrType(Compilation.GetSpecialType(SpecialType.System_IntPtr))
            ];

            ctor = ResolveDelegateConstructorOnType(delegateClrType)
                ?? throw new InvalidOperationException($"Delegate '{delegateClrType}' lacks the expected constructor.");
        }

        ctor = NormalizeDelegateConstructor(ctor, delegateClrType);

        _delegateConstructorCache[cacheKey] = ctor;
        return ctor;
    }

    private ConstructorInfo NormalizeDelegateConstructor(ConstructorInfo constructor, Type delegateClrType)
    {
        if (constructor.DeclaringType == delegateClrType &&
            !constructor.ContainsGenericParameters &&
            !(constructor.DeclaringType?.ContainsGenericParameters ?? false))
        {
            return constructor;
        }

        _delegateConstructorSignature ??=
        [
            ResolveClrType(Compilation.GetSpecialType(SpecialType.System_Object)),
            ResolveClrType(Compilation.GetSpecialType(SpecialType.System_IntPtr))
        ];

        if (delegateClrType.Assembly.IsDynamic &&
            constructor.DeclaringType is { IsGenericTypeDefinition: true })
        {
            try
            {
                var projected = TypeBuilder.GetConstructor(delegateClrType, constructor);
                if (projected is not null)
                    return projected;
            }
            catch (ArgumentException)
            {
            }
        }

        return ResolveDelegateConstructorOnType(delegateClrType)
               ?? throw new InvalidOperationException($"Delegate '{delegateClrType}' lacks the expected constructor.");
    }

    private ConstructorInfo? ResolveDelegateConstructorOnType(Type delegateClrType)
    {
        _delegateConstructorSignature ??=
        [
            ResolveClrType(Compilation.GetSpecialType(SpecialType.System_Object)),
            ResolveClrType(Compilation.GetSpecialType(SpecialType.System_IntPtr))
        ];

        try
        {
            var direct = delegateClrType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: _delegateConstructorSignature,
                modifiers: null);
            if (direct is not null)
                return direct;
        }
        catch (NotSupportedException)
        {
            // TypeBuilderInstantiation cannot be queried directly; resolve via definition below.
        }

        if (delegateClrType.IsGenericType)
        {
            var definition = delegateClrType.IsGenericTypeDefinition
                ? delegateClrType
                : delegateClrType.GetGenericTypeDefinition();

            var definitionCtor = definition.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: _delegateConstructorSignature,
                modifiers: null);

            if (definitionCtor is not null)
            {
                try
                {
                    return TypeBuilder.GetConstructor(delegateClrType, definitionCtor);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private DelegateConstructorCacheKey CreateDelegateConstructorCacheKey(ITypeSymbol delegateType, Type delegateClrType)
    {
        if (delegateType is SynthesizedDelegateTypeSymbol synthesized)
        {
            var parameterTypes = synthesized.ParameterTypes.IsDefaultOrEmpty
                ? ImmutableArray<ITypeSymbol>.Empty
                : synthesized.ParameterTypes;
            var refKinds = synthesized.ParameterRefKinds.IsDefaultOrEmpty
                ? ImmutableArray<RefKind>.Empty
                : synthesized.ParameterRefKinds;

            return new DelegateConstructorCacheKey(delegateClrType, synthesized.ReturnType, parameterTypes, refKinds);
        }

        if (delegateType is INamedTypeSymbol namedDelegate && namedDelegate.GetDelegateInvokeMethod() is { } invoke)
        {
            var parameterTypesBuilder = ImmutableArray.CreateBuilder<ITypeSymbol>(invoke.Parameters.Length);
            var refKindsBuilder = ImmutableArray.CreateBuilder<RefKind>(invoke.Parameters.Length);

            foreach (var parameter in invoke.Parameters)
            {
                parameterTypesBuilder.Add(parameter.Type);
                refKindsBuilder.Add(parameter.RefKind);
            }

            return new DelegateConstructorCacheKey(
                delegateClrType,
                invoke.ReturnType,
                parameterTypesBuilder.MoveToImmutable(),
                refKindsBuilder.MoveToImmutable());
        }

        return new DelegateConstructorCacheKey(
            delegateClrType,
            delegateType,
            ImmutableArray<ITypeSymbol>.Empty,
            ImmutableArray<RefKind>.Empty);
    }

    private ConstructorInfo? TryResolveDelegateConstructor(INamedTypeSymbol delegateType)
    {
        foreach (var constructor in delegateType.Constructors)
        {
            if (constructor.MethodKind is not MethodKind.Constructor)
                continue;

            if (!IsDelegateConstructorSignature(constructor))
                continue;

            var ctorInfo = TryGetConstructorInfo(constructor);
            if (ctorInfo is not null)
                return ctorInfo;
        }

        return null;
    }

    private bool IsDelegateConstructorSignature(IMethodSymbol constructor)
    {
        if (constructor.Parameters.Length != 2)
            return false;

        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        var intPtrType = Compilation.GetSpecialType(SpecialType.System_IntPtr);

        return SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, objectType)
            && SymbolEqualityComparer.Default.Equals(constructor.Parameters[1].Type, intPtrType);
    }

    private ConstructorInfo? TryGetConstructorInfo(IMethodSymbol constructor)
    {
        try
        {
            return GetConstructorInfo(constructor);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private ConstructorInfo? TryGetConstructedConstructorInfo(ConstructedMethodSymbol constructed)
    {
        var definitionCtor = TryGetConstructorInfo(constructed.Definition);
        if (definitionCtor is null)
            return null;

        var declaringType = definitionCtor.DeclaringType;
        if (declaringType is null)
            return definitionCtor;

        if (constructed.TypeArguments.IsDefaultOrEmpty || constructed.TypeArguments.Length == 0)
            return definitionCtor;

        if (!declaringType.IsGenericTypeDefinition && !declaringType.ContainsGenericParameters)
            return definitionCtor;

        var runtimeArguments = constructed.TypeArguments.Select(ResolveClrType).ToArray();
        var constructedType = declaringType.MakeGenericType(runtimeArguments);

        _delegateConstructorSignature ??=
        [
            ResolveClrType(Compilation.GetSpecialType(SpecialType.System_Object)),
            ResolveClrType(Compilation.GetSpecialType(SpecialType.System_IntPtr))
        ];

        return constructedType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: _delegateConstructorSignature,
            modifiers: null);
    }

    private void EmitUnitExpression(BoundUnitExpression unitExpression)
    {
        if (_preserveResult)
            EmitUnitValue();
    }

    private void EmitPropagateExpression(BoundPropagateExpression expr)
    {
        // Evaluate the operand exactly once.
        var operandClrType = ResolveClrType(expr.Operand.Type);
        var operandInfo = EmitExpression(expr.Operand);
        var tmp = SpillValueToLocalIfNeeded(operandClrType, operandInfo, keepValueOnStack: false);

        // Prefer lowering via `TryGetValue(out Result<T,E>.Ok okCase)` so we can later read `okCase.Value`.
        // Fall back to `TryGetValue(out T value)` if we don't have OkCaseType.
        var okLabel = ILGenerator.DefineLabel();

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

            if (leftPlain.MetadataIdentityEquals(rightPlain))
                return true;

            var leftCase = leftPlain.TryGetUnionCase();
            var rightCase = rightPlain.TryGetUnionCase();
            if (leftCase is not null && rightCase is not null)
            {
                if (leftCase.Ordinal == rightCase.Ordinal)
                {
                    var leftUnion = leftCase.Union.GetNonNullableType();
                    var rightUnion = rightCase.Union.GetNonNullableType();
                    if (SymbolEqualityComparer.Default.Equals(leftUnion, rightUnion))
                        return true;

                    var leftUnionDefinition = leftUnion.OriginalDefinition ?? leftUnion;
                    var rightUnionDefinition = rightUnion.OriginalDefinition ?? rightUnion;
                    if (SymbolEqualityComparer.Default.Equals(leftUnionDefinition, rightUnionDefinition))
                        return true;
                }
            }

            var leftDefinition = left.OriginalDefinition ?? left;
            var rightDefinition = right.OriginalDefinition ?? right;
            return SymbolEqualityComparer.Default.Equals(leftDefinition, rightDefinition);
        }

        if (expr.TryGetOutputMethod is { } tryGetOutputSymbol)
        {
            var outputClrType = ResolveClrType(expr.OkType);
            var outputLocal = ILGenerator.DeclareLocal(outputClrType);
            var tryGetOutput = CloseMethodOnRuntimeCarrier(operandClrType, GetMethodInfo(tryGetOutputSymbol));

            if (operandClrType.IsValueType)
                ILGenerator.Emit(OpCodes.Ldloca_S, tmp);
            else
                ILGenerator.Emit(OpCodes.Ldloc, tmp);
            ILGenerator.Emit(OpCodes.Ldloca_S, outputLocal);
            ILGenerator.Emit(operandClrType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, tryGetOutput);
            ILGenerator.Emit(OpCodes.Brtrue, okLabel);

            EmitContractPropagateErrorReturn(expr, operandClrType, tmp);

            ILGenerator.MarkLabel(okLabel);
            ILGenerator.Emit(OpCodes.Ldloc, outputLocal);

            if (!_preserveResult)
                ILGenerator.Emit(OpCodes.Pop);

            return;
        }

        if (expr.OkCaseType is not null)
        {
            // --- Begin semantic-symbol-based method lookup for TryGetValue(out OkCaseType) ---
            if (expr.Operand.Type is not INamedTypeSymbol operandTypeSymbol)
                throw new InvalidOperationException("Propagate operand must be a named type.");
            var tryGetCandidates = operandTypeSymbol
                .GetMembers("TryGetValue")
                .OfType<IMethodSymbol>()
                .Where(m =>
                    !m.IsStatic &&
                    m.Parameters.Length == 1 &&
                    m.Parameters[0].RefKind == RefKind.Out)
                .ToArray();

            var tryGetOkSymbol = tryGetCandidates.FirstOrDefault(m =>
                expr.OkCaseType is not null &&
                TypesMatch(m.Parameters[0].GetByRefElementType(), expr.OkCaseType));

            if (tryGetOkSymbol is null)
            {
                tryGetOkSymbol = tryGetCandidates.FirstOrDefault(m =>
                    string.Equals(
                        m.Parameters[0].GetByRefElementType().TryGetUnionCase()?.Name,
                        expr.OkCaseName,
                        StringComparison.Ordinal));
            }

            if (tryGetOkSymbol is null)
            {
                tryGetOkSymbol = tryGetCandidates.FirstOrDefault(m =>
                    TypesMatch(m.Parameters[0].GetByRefElementType(), expr.OkType));
            }

            if (tryGetOkSymbol is null)
                throw new InvalidOperationException($"Missing compatible TryGetValue(out ...) on '{expr.Operand.Type}' for Ok case '{expr.OkCaseName}'.");

            var tryGetOkOkCase = CloseMethodOnRuntimeCarrier(operandClrType, GetMethodInfo(tryGetOkSymbol));
            // --- End semantic-symbol-based method lookup ---

            var okCaseClrType = tryGetOkOkCase.GetParameters() is [{ ParameterType: var okOutType }] &&
                                okOutType.IsByRef &&
                                okOutType.GetElementType() is Type okElementType
                ? CloseTypeFromMethodContext(okElementType, tryGetOkOkCase.DeclaringType)
                : ResolveClrType(expr.OkCaseType);
            okCaseClrType = CloseNestedCarrierCaseType(okCaseClrType, tryGetOkOkCase.DeclaringType ?? operandClrType);
            var okCaseLocal = ILGenerator.DeclareLocal(okCaseClrType);

            // if (tmp.TryGetValue(out okCaseLocal)) goto okLabel;
            if (operandClrType.IsValueType)
                ILGenerator.Emit(OpCodes.Ldloca_S, tmp);
            else
                ILGenerator.Emit(OpCodes.Ldloc, tmp);
            ILGenerator.Emit(OpCodes.Ldloca_S, okCaseLocal);
            ILGenerator.Emit(operandClrType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, tryGetOkOkCase);
            ILGenerator.Emit(OpCodes.Brtrue, okLabel);

            // Error path: extract error payload and early-return.
            EmitPropagateErrorReturn(expr, operandClrType, tmp);

            // Ok path: load okCaseLocal.Value (or best-effort payload) onto the stack.
            ILGenerator.MarkLabel(okLabel);

            var selectedOutType = tryGetOkSymbol.Parameters[0].GetByRefElementType();
            var outCarriesPayloadDirectly = TypesMatch(selectedOutType, expr.OkType);
            if (outCarriesPayloadDirectly)
            {
                ILGenerator.Emit(OpCodes.Ldloc, okCaseLocal);
            }
            else if (expr.OkValueProperty?.GetMethod is not null)
            {
                var valueGetter = GetMethodInfo(expr.OkValueProperty.GetMethod);
                if (okCaseClrType.IsValueType)
                {
                    // Value-type case: call the getter on the managed address to avoid copies.
                    ILGenerator.Emit(OpCodes.Ldloca_S, okCaseLocal);
                    ILGenerator.Emit(OpCodes.Call, valueGetter);
                }
                else
                {
                    ILGenerator.Emit(OpCodes.Ldloc, okCaseLocal);
                    ILGenerator.Emit(OpCodes.Callvirt, valueGetter);
                }
            }
            else
            {
                // Best-effort: if there is a `Value` property on the CLR type.
                var valueProp = okCaseClrType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (valueProp?.GetMethod is null)
                    throw new InvalidOperationException($"Missing Ok Value getter on '{okCaseClrType}'.");

                if (okCaseClrType.IsValueType)
                {
                    ILGenerator.Emit(OpCodes.Ldloca_S, okCaseLocal);
                    ILGenerator.Emit(OpCodes.Call, valueProp.GetMethod);
                }
                else
                {
                    ILGenerator.Emit(OpCodes.Ldloc, okCaseLocal);
                    ILGenerator.Emit(OpCodes.Callvirt, valueProp.GetMethod);
                }
            }

            if (!_preserveResult)
                ILGenerator.Emit(OpCodes.Pop);

            return;
        }

        // Fallback: `TryGetValue(out T)`
        {
            var okClrType = ResolveClrType(expr.OkType);
            var okLocal = ILGenerator.DeclareLocal(okClrType);

            // --- Begin semantic-symbol-based method lookup for TryGetValue(out OkType) ---
            if (expr.Operand.Type is not INamedTypeSymbol operandTypeSymbol)
                throw new InvalidOperationException("Propagate operand must be a named type.");
            var tryGetOkSymbol = operandTypeSymbol
                .GetMembers("TryGetValue")
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m =>
                !m.IsStatic &&
                m.Parameters.Length == 1 &&
                m.Parameters[0].RefKind == RefKind.Out &&
                TypesMatch(m.Parameters[0].GetByRefElementType(), expr.OkType));

            if (tryGetOkSymbol is null)
                throw new InvalidOperationException($"Missing TryGetValue(out {expr.OkType}) on '{expr.Operand.Type}'.");

            var tryGetOk = CloseMethodOnRuntimeCarrier(operandClrType, GetMethodInfo(tryGetOkSymbol));
            // --- End semantic-symbol-based method lookup ---

            if (operandClrType.IsValueType)
                ILGenerator.Emit(OpCodes.Ldloca_S, tmp);
            else
                ILGenerator.Emit(OpCodes.Ldloc, tmp);
            ILGenerator.Emit(OpCodes.Ldloca_S, okLocal);
            ILGenerator.Emit(operandClrType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, tryGetOk);
            ILGenerator.Emit(OpCodes.Brtrue, okLabel);

            // Error path
            EmitPropagateErrorReturn(expr, operandClrType, tmp);

            // Ok path: leave ok value on the stack.
            ILGenerator.MarkLabel(okLabel);
            ILGenerator.Emit(OpCodes.Ldloc, okLocal);

            if (!_preserveResult)
                ILGenerator.Emit(OpCodes.Pop);
        }
    }

    private IILocal SpillValueToLocalIfNeeded(Type clrType, EmitInfo info, bool keepValueOnStack)
    {
        // If the expression already came directly from an existing local (and we didn't spill),
        // reuse that local instead of introducing an intermediate temp.
        if (info.Local is not null && !info.WasCaptured && !info.WasSpilledToLocal)
        {
            if (!keepValueOnStack)
            {
                // Caller wants the stack cleared; we will reload from the local as needed.
                ILGenerator.Emit(OpCodes.Pop);
            }

            return info.Local;
        }

        var tmp = ILGenerator.DeclareLocal(clrType);

        if (keepValueOnStack)
        {
            // Preserve a copy on the stack for immediate use, but also cache for later.
            ILGenerator.Emit(OpCodes.Dup);
            ILGenerator.Emit(OpCodes.Stloc, tmp);
        }
        else
        {
            // Consume the value and store it for later use.
            ILGenerator.Emit(OpCodes.Stloc, tmp);
        }

        return tmp;
    }

    private static MethodInfo CloseMethodOnRuntimeCarrier(Type carrierType, MethodInfo method)
    {
        if (method.DeclaringType is null)
            return method;

        if (ReferenceEquals(method.DeclaringType, carrierType))
            return method;

        if (!carrierType.IsGenericType)
            return method;

        try
        {
            return TypeBuilder.GetMethod(carrierType, method);
        }
        catch (ArgumentException)
        {
            return method;
        }
        catch (NotSupportedException)
        {
            return method;
        }
    }

    private void EmitPropagateErrorReturn(BoundPropagateExpression expr, Type operandClrType, IILocal tmp)
    {
        var enclosingResultClrType = ResolveClrType(expr.EnclosingResultType);
        var enclosingErrorCtor = expr.EnclosingErrorConstructor;

        if (enclosingErrorCtor.Parameters.Length == 0)
        {
            var errorCtorInfo = GetConstructorInfo(enclosingErrorCtor);
            ILGenerator.Emit(OpCodes.Newobj, errorCtorInfo);

            // Updated: Use symbol-based EmitPropagateErrorCaseConversion
            EmitPropagateErrorCaseConversion(expr.EnclosingResultType, enclosingErrorCtor.ContainingType);
            EmitPropagateReturn(enclosingResultClrType);
            return;
        }

        if (enclosingErrorCtor.Parameters.Length != 1)
            throw new InvalidOperationException("Expected enclosing Error constructor to have exactly one parameter.");

        var targetErrorType = enclosingErrorCtor.Parameters[0].Type;
        var sourceErrorType = expr.ErrorType ?? Compilation.ErrorTypeSymbol;

        // Extract the error case structurally instead of depending on a carrier
        // helper method. Propagation semantics are defined by the union cases.
        if (expr.Operand.Type is not INamedTypeSymbol operandTypeSymbol)
            throw new InvalidOperationException($"Propagate operand must be a named type: '{expr.Operand.Type}'.");

        var tryGetErrorSymbol = operandTypeSymbol
            .GetMembers("TryGetValue")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m =>
                !m.IsStatic &&
                m.Parameters.Length == 1 &&
                m.Parameters[0].RefKind == RefKind.Out &&
                IsErrorCaseType(m.Parameters[0].GetByRefElementType()));

        bool IsErrorCaseType(ITypeSymbol candidate)
        {
            if (expr.ErrorCaseType is { } expected)
            {
                if (SymbolEqualityComparer.Default.Equals(candidate, expected) ||
                    SymbolEqualityComparer.Default.Equals(candidate.GetNonNullableType(), expected.GetNonNullableType()) ||
                    candidate.GetNonNullableType().MetadataIdentityEquals(expected.GetNonNullableType()))
                {
                    return true;
                }
            }

            return string.Equals(
                candidate.TryGetUnionCase()?.Name,
                expr.ErrorCaseName,
                StringComparison.Ordinal);
        }

        if (tryGetErrorSymbol is null)
            throw new InvalidOperationException($"Missing TryGetValue(out {expr.ErrorCaseName}...) on '{expr.Operand.Type}'.");

        var errorOutSymbolType = tryGetErrorSymbol.Parameters[0].GetByRefElementType();
        var errorOutType = ResolveClrType(errorOutSymbolType);
        var tryGetError = CloseMethodOnRuntimeCarrier(operandClrType, GetMethodInfo(tryGetErrorSymbol));

        var errorCaseLocal = ILGenerator.DeclareLocal(errorOutType);
        if (operandClrType.IsValueType)
            ILGenerator.Emit(OpCodes.Ldloca_S, tmp);
        else
            ILGenerator.Emit(OpCodes.Ldloc, tmp);

        ILGenerator.Emit(OpCodes.Ldloca_S, errorCaseLocal);
        ILGenerator.Emit(operandClrType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, tryGetError);

        var haveErrorCase = ILGenerator.DefineLabel();
        var invalidOpCtor = typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })
            ?? throw new InvalidOperationException("Missing InvalidOperationException(string) constructor.");

        ILGenerator.Emit(OpCodes.Brtrue, haveErrorCase);
        ILGenerator.Emit(OpCodes.Ldstr, $"Failed to extract '{expr.ErrorCaseName}' case from '{expr.Operand.Type}'.");
        ILGenerator.Emit(OpCodes.Newobj, invalidOpCtor);
        ILGenerator.Emit(OpCodes.Throw);
        ILGenerator.MarkLabel(haveErrorCase);

        if (SymbolEqualityComparer.Default.Equals(errorOutSymbolType, targetErrorType))
        {
            // Payload is directly the out value type.
            ILGenerator.Emit(OpCodes.Ldloc, errorCaseLocal);
        }
        else
        {
            var payloadGetter = (errorOutSymbolType as INamedTypeSymbol)?
                .GetMembers("Value").OfType<IPropertySymbol>().FirstOrDefault()?.GetMethod
                ?? (errorOutSymbolType as INamedTypeSymbol)?
                    .GetMembers("Error").OfType<IPropertySymbol>().FirstOrDefault()?.GetMethod
                ?? (errorOutSymbolType as INamedTypeSymbol)?
                    .GetMembers("Data").OfType<IPropertySymbol>().FirstOrDefault()?.GetMethod;

            if (payloadGetter is null)
                throw new InvalidOperationException($"Missing error payload getter on '{errorOutSymbolType}'.");

            var payloadGetterInfo = GetMethodInfo(payloadGetter);
            if (errorOutSymbolType.IsValueType)
            {
                ILGenerator.Emit(OpCodes.Ldloca_S, errorCaseLocal);
                ILGenerator.Emit(OpCodes.Call, payloadGetterInfo);
            }
            else
            {
                ILGenerator.Emit(OpCodes.Ldloc, errorCaseLocal);
                ILGenerator.Emit(OpCodes.Callvirt, payloadGetterInfo);
            }
        }

        // Convert payload to the parameter type of the enclosing Error constructor if needed.
        var conversion = expr.ErrorConversion.Exists
            ? expr.ErrorConversion
            : Compilation.ClassifyConversion(sourceErrorType, targetErrorType);

        if (conversion.Exists)
        {
            EmitConversion(sourceErrorType, targetErrorType, conversion);
        }
        else
        {
            // Best-effort: allow reference cast at runtime.
            var targetClr = ResolveClrType(targetErrorType);
            ILGenerator.Emit(targetClr.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, targetClr);
        }

        // Construct the enclosing Error case.
        var ctorInfo = GetConstructorInfo(enclosingErrorCtor);
        ILGenerator.Emit(OpCodes.Newobj, ctorInfo);

        // Updated: Use symbol-based EmitPropagateErrorCaseConversion
        EmitPropagateErrorCaseConversion(expr.EnclosingResultType, enclosingErrorCtor.ContainingType);
        EmitPropagateReturn(enclosingResultClrType);
    }

    private void EmitContractPropagateErrorReturn(BoundPropagateExpression expr, Type operandClrType, IILocal tmp)
    {
        var tryGetResidualSymbol = expr.TryGetResidualMethod
            ?? throw new InvalidOperationException("Missing TryGetResidual method for contract propagation.");
        var residualType = tryGetResidualSymbol.Parameters[0].GetByRefElementType();
        var residualLocal = ILGenerator.DeclareLocal(ResolveClrType(residualType));
        var tryGetResidual = CloseMethodOnRuntimeCarrier(operandClrType, GetMethodInfo(tryGetResidualSymbol));

        if (operandClrType.IsValueType)
            ILGenerator.Emit(OpCodes.Ldloca_S, tmp);
        else
            ILGenerator.Emit(OpCodes.Ldloc, tmp);
        ILGenerator.Emit(OpCodes.Ldloca_S, residualLocal);
        ILGenerator.Emit(operandClrType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, tryGetResidual);

        var haveResidual = ILGenerator.DefineLabel();
        ILGenerator.Emit(OpCodes.Brtrue, haveResidual);
        ILGenerator.Emit(OpCodes.Ldnull);
        ILGenerator.Emit(OpCodes.Throw);
        ILGenerator.MarkLabel(haveResidual);

        var fromResidual = expr.EnclosingErrorConstructor;
        if (!fromResidual.IsStatic || fromResidual.Parameters.Length != 1)
            throw new InvalidOperationException("Contract propagation requires a static FromResidual method with one parameter.");

        var targetResidualType = fromResidual.Parameters[0].Type;
        ILGenerator.Emit(OpCodes.Ldloc, residualLocal);

        var conversion = expr.ErrorConversion.Exists
            ? expr.ErrorConversion
            : Compilation.ClassifyConversion(residualType, targetResidualType);
        if (conversion.Exists && !conversion.IsIdentity)
            EmitConversion(residualType, targetResidualType, conversion);

        ILGenerator.Emit(OpCodes.Call, GetMethodInfo(fromResidual));
        EmitPropagateReturn(ResolveClrType(expr.EnclosingResultType));
    }

    private void EmitPropagateErrorCaseConversion(ITypeSymbol enclosingResultType, ITypeSymbol errorCaseType)
    {
        // The enclosing error-case type may be a nested case struct (e.g. Result<T,E>.Error)
        // that is not IL-assignable to the enclosing result type. Materialize the carrier
        // directly through its case-typed constructor.
        if (SymbolEqualityComparer.Default.Equals(enclosingResultType, errorCaseType))
            return;

        if (enclosingResultType is not INamedTypeSymbol enclosingCarrier)
            throw new InvalidOperationException($"Missing union carrier type for '{enclosingResultType}'.");

        if (!enclosingCarrier.TryGetUnionCarrierConstructor(errorCaseType, out var carrierCtor))
            throw new InvalidOperationException($"Missing union constructor from '{errorCaseType}' to '{enclosingResultType}'.");

        ILGenerator.Emit(OpCodes.Newobj, GetConstructorInfo(carrierCtor));
    }

    private void EmitPropagateReturn(Type resultClrType)
    {
        if (IsAsyncStateMachineMoveNext(out var stateField, out var builderField, out var setResultMethod))
        {
            var resultLocal = ILGenerator.DeclareLocal(resultClrType);
            ILGenerator.Emit(OpCodes.Stloc, resultLocal);

            var stateFieldInfo = stateField.GetFieldInfo(MethodGenerator.TypeGenerator.CodeGen);
            ILGenerator.Emit(OpCodes.Ldarg_0);
            ILGenerator.Emit(OpCodes.Ldc_I4, -2);
            ILGenerator.Emit(OpCodes.Stfld, stateFieldInfo);

            var builderFieldInfo = builderField.GetFieldInfo(MethodGenerator.TypeGenerator.CodeGen);
            ILGenerator.Emit(OpCodes.Ldarg_0);
            ILGenerator.Emit(OpCodes.Ldflda, builderFieldInfo);
            ILGenerator.Emit(OpCodes.Ldloc, resultLocal);
            ILGenerator.Emit(OpCodes.Call, GetMethodInfo(setResultMethod));

            // IMPORTANT: if we are inside an exception-protected region (async MoveNext is typically wrapped
            // in a try/catch), we must use `leave` to branch to the epilogue label. Using `br` here produces
            // invalid IL and can trigger InvalidProgramException.
            var returnLabel = MethodBodyGenerator.GetOrCreateReturnLabel();
            if (TryGetExceptionExitLabel(out _))
                ILGenerator.Emit(OpCodes.Leave, returnLabel);
            else
                ILGenerator.Emit(OpCodes.Br, returnLabel);
            return;
        }

        if (TryGetExceptionExitLabel(out var exitLabel))
        {
            // The epilogue must read this value even when this is the first return
            // emitted inside a protected region. A temporary spill is not a return slot.
            var returnValueLocal = MethodBodyGenerator.EnsureReturnValueLocal();
            ILGenerator.Emit(OpCodes.Stloc, returnValueLocal);
            ILGenerator.Emit(OpCodes.Leave, exitLabel);
            return;
        }

        ILGenerator.Emit(OpCodes.Ret);
    }

    private bool IsAsyncStateMachineMoveNext(
        out IFieldSymbol stateField,
        out IFieldSymbol builderField,
        out IMethodSymbol setResultMethod)
    {
        stateField = null!;
        builderField = null!;
        setResultMethod = null!;

        if (MethodSymbol.Name != "MoveNext" ||
            MethodSymbol.ContainingType is not INamedTypeSymbol containingType)
        {
            return false;
        }

        var state = containingType.GetMembers("_state").OfType<IFieldSymbol>().FirstOrDefault();
        var builder = containingType.GetMembers("_builder").OfType<IFieldSymbol>().FirstOrDefault();
        var setResult = builder?.Type
            .GetMembers("SetResult")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.Length == 1);

        if (state is null || builder is null || setResult is null)
            return false;

        stateField = state;
        builderField = builder;
        setResultMethod = setResult;
        return true;
    }

    private void EmitTupleExpression(BoundTupleExpression tupleExpression)
    {
        var elements = tupleExpression.Elements;
        var elementTypes = elements.Select(e => e.Type!).ToArray();

        for (int i = 0; i < elements.Count(); i++)
        {
            EmitExpression(elements.ElementAt(i));

            if (elementTypes[i].IsValueType == false)
                continue;

            var clrType = ResolveClrType(elementTypes[i]);

            // Box if needed (e.g. to match generic type constraints in ValueTuple.Create<T>)
            /*if (clrType.IsValueType && clrType.IsGenericType == false)
            {
                ILGenerator.Emit(OpCodes.Box, clrType);
            }*/
        }

        var valueTupleType = Compilation.GetTypeByMetadataName($"System.ValueTuple");

        var createMethod = valueTupleType
            .GetMembers("Create")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m =>
                m.IsStatic &&
                m.Parameters.Length == elements.Count());

        if (createMethod is null)
            throw new InvalidOperationException($"Could not resolve ValueTuple.Create({elements.Count()} args)");

        ILGenerator.Emit(OpCodes.Call, GetMethodInfo(createMethod).MakeGenericMethod(elements.Select(x => ResolveClrType(x.Type)).ToArray()));
    }
    private EmitInfo EmitLocalAccess(BoundLocalAccess localAccess, EmitContext context)
    {
        if (SubmissionCodeGenerator.TryEmitVariableLoad(this, localAccess.Local, out var submissionEmitInfo))
            return submissionEmitInfo;

        if (_carrierPayloadSymbol is not null &&
            _carrierPayloadLocal is not null &&
            SymbolEqualityComparer.Default.Equals(localAccess.Local, _carrierPayloadSymbol))
        {
            ILGenerator.Emit(OpCodes.Ldloc, _carrierPayloadLocal);
            return EmitInfo.ForValue(local: _carrierPayloadLocal);
        }

        if (TryEmitCapturedVariableLoad(localAccess.Local))
            return EmitInfo.ForValue(localAccess.Local, wasCaptured: true);

        var localBuilder = GetLocal(localAccess.Local);
        if (localBuilder is null)
            throw new InvalidOperationException($"Missing local builder for '{localAccess.Local.Name}'");

        ILGenerator.Emit(OpCodes.Ldloc, localBuilder);
        return EmitInfo.ForValue(localAccess.Local, localBuilder);
    }

    private EmitInfo EmitParameterAccess(BoundParameterAccess parameterAccess)
    {
        if (TryEmitCapturedVariableLoad(parameterAccess.Parameter))
            return EmitInfo.ForValue(parameterAccess.Parameter, wasCaptured: true);

        if (MethodBodyGenerator.LambdaClosure is { } lambdaClosure &&
            !MethodSymbol.Parameters.Any(parameter =>
                SymbolEqualityComparer.Default.Equals(parameter, parameterAccess.Parameter) ||
                string.Equals(parameter.Name, parameterAccess.Parameter.Name, StringComparison.Ordinal)))
        {
            lambdaClosure.EnsureFields(ImmutableArray.Create<ISymbol>(parameterAccess.Parameter));
            if (TryEmitCapturedVariableLoad(parameterAccess.Parameter))
                return EmitInfo.ForValue(parameterAccess.Parameter, wasCaptured: true);
        }

        int position = MethodGenerator.GetParameterBuilder(parameterAccess.Parameter).Position;
        if (MethodSymbol.IsStatic)
            position -= 1;

        ILGenerator.Emit(OpCodes.Ldarg, position);
        if (parameterAccess.Parameter.IsByRefParameter)
            EmitLoadIndirect(parameterAccess.Parameter.GetByRefElementType());
        return EmitInfo.ForValue(parameterAccess.Parameter);
    }

    private void EmitConversionExpression(BoundConversionExpression conversionExpression)
    {
        if (conversionExpression.Expression is BoundTypeExpression typeExpression)
        {
            ITypeSymbol caseTypeToConstruct = typeExpression.Type;

            if (conversionExpression.Conversion.IsUserDefined &&
                conversionExpression.Conversion.MethodSymbol is IMethodSymbol conversionMethod &&
                conversionMethod.Parameters.Length == 1)
            {
                caseTypeToConstruct = conversionMethod.Parameters[0].Type;
            }

            if (TryEmitDiscriminatedUnionCaseCreation(caseTypeToConstruct))
            {
                EmitConversion(caseTypeToConstruct, conversionExpression.Type, conversionExpression.Conversion);
                return;
            }
        }

        new ExpressionGenerator(this, conversionExpression.Expression).Emit();
        EmitConversion(conversionExpression.Expression.Type!, conversionExpression.Type, conversionExpression.Conversion);
    }

    private void EmitAsExpression(BoundAsExpression asExpression)
    {
        new ExpressionGenerator(this, asExpression.Expression).Emit();
        ILGenerator.Emit(OpCodes.Isinst, ResolveClrType(asExpression.Type));
    }

    private void EmitIndexExpression(BoundIndexExpression indexExpression)
    {
        if (indexExpression.Type.TypeKind == TypeKind.Error)
        {
            EmitDefaultValue(indexExpression.Type);
            return;
        }

        EmitExpression(indexExpression.Value);

        var indexClrType = ResolveClrType(indexExpression.Type);
        var ctor = indexClrType.GetConstructor(new[] { typeof(int), typeof(bool) })
            ?? throw new InvalidOperationException("Missing System.Index constructor.");

        ILGenerator.Emit(indexExpression.IsFromEnd ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Newobj, ctor);
    }

    private void EmitRangeExpression(BoundRangeExpression rangeExpression)
    {
        if (rangeExpression.Type.TypeKind == TypeKind.Error)
        {
            EmitDefaultValue(rangeExpression.Type);
            return;
        }

        var rangeClrType = ResolveClrType(rangeExpression.Type);

        if (rangeExpression.Left is null && rangeExpression.Right is null)
        {
            var allProperty = rangeClrType.GetProperty(nameof(Range.All), BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Missing System.Range.All property.");
            var getter = allProperty.GetMethod ?? throw new InvalidOperationException("Missing System.Range.All getter.");
            ILGenerator.Emit(OpCodes.Call, getter);
            return;
        }

        if (rangeExpression.Left is null && rangeExpression.Right is { } end)
        {
            EmitIndexExpression(end);

            var endAt = rangeClrType.GetMethod(
                nameof(Range.EndAt),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                new[] { ResolveClrType(end.Type) },
                modifiers: null)
                ?? throw new InvalidOperationException("Missing System.Range.EndAt method.");

            ILGenerator.Emit(OpCodes.Call, endAt);
            return;
        }

        if (rangeExpression.Left is { } start && rangeExpression.Right is null)
        {
            EmitIndexExpression(start);

            var startAt = rangeClrType.GetMethod(
                nameof(Range.StartAt),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                new[] { ResolveClrType(start.Type) },
                modifiers: null)
                ?? throw new InvalidOperationException("Missing System.Range.StartAt method.");

            ILGenerator.Emit(OpCodes.Call, startAt);
            return;
        }

        if (rangeExpression.Left is null || rangeExpression.Right is null)
        {
            EmitDefaultValue(rangeExpression.Type);
            return;
        }

        EmitIndexExpression(rangeExpression.Left);
        EmitIndexExpression(rangeExpression.Right);

        var ctor = rangeClrType.GetConstructor(new[]
        {
            ResolveClrType(rangeExpression.Left.Type),
            ResolveClrType(rangeExpression.Right.Type)
        }) ?? throw new InvalidOperationException("Missing System.Range constructor.");

        ILGenerator.Emit(OpCodes.Newobj, ctor);
    }

    private void EmitUnaryExpression(BoundUnaryExpression node)
    {
        var operand = node.Operand;
        var op = node.Operator;

        switch (op.OperatorKind)
        {
            case BoundUnaryOperatorKind.UnaryMinus: // -x
                EmitExpression(operand);
                ILGenerator.Emit(OpCodes.Neg);
                break;

            case BoundUnaryOperatorKind.UnaryPlus: // +x
                EmitExpression(operand); // no-op
                break;

            case BoundUnaryOperatorKind.LogicalNot: // !x
                EmitExpression(operand);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Ceq);
                break;

            case BoundUnaryOperatorKind.BitwiseNot: // ~x
                EmitExpression(operand);
                ILGenerator.Emit(OpCodes.Not);
                break;

            default:
                throw new NotSupportedException($"Unsupported unary operator");
        }
    }

    private void EmitAddressOfExpression(BoundAddressOfExpression addressOf)
    {
        switch (addressOf.Symbol)
        {
            case ILocalSymbol local:
                var localBuilder = GetLocal(local);
                if (localBuilder is null)
                {
                    var localClrType = ResolveClrType(local.Type);
                    localBuilder = ILGenerator.DeclareLocal(localClrType);
                    localBuilder.SetLocalSymInfo(local.Name);
                    AddLocal(local, localBuilder);
                }

                ILGenerator.Emit(OpCodes.Ldloca, localBuilder);

                break;

            case IParameterSymbol param:
                EmitParameterForByRefUse(param);
                break;

            case IFieldSymbol field when field.IsStatic:
                ILGenerator.Emit(OpCodes.Ldsflda, GetField(field));
                if (TryGetAsyncInvestigationFieldLabel(field, out var staticFieldLabel))
                    EmitAsyncInvestigationAddressLogPreservingPointer(staticFieldLabel);
                break;

            case IFieldSymbol field:
                if (addressOf.Receiver is not null)
                {
                    if (field.ContainingType?.IsValueType == true && addressOf.Receiver is BoundSelfExpression or BoundBaseExpression)
                    {
                        if (MethodSymbol.IsStatic)
                            throw new NotSupportedException($"Cannot take address of instance field '{field.Name}' in a static context.");

                        ILGenerator.Emit(OpCodes.Ldarg_0);
                    }
                    else if (!TryEmitValueTypeReceiverAddress(addressOf.Receiver, addressOf.Receiver.Type, field.ContainingType))
                    {
                        EmitExpression(addressOf.Receiver);

                        if (field.ContainingType?.IsValueType == true)
                            EmitValueTypeAddressIfNeeded(addressOf.Receiver.Type, field.ContainingType);
                    }
                }
                else
                {
                    if (MethodSymbol.IsStatic)
                        throw new NotSupportedException($"Cannot take address of instance field '{field.Name}' in a static context.");

                    ILGenerator.Emit(OpCodes.Ldarg_0);
                }

                ILGenerator.Emit(OpCodes.Ldflda, GetField(field));
                if (TryGetAsyncInvestigationFieldLabel(field, out var fieldLabel))
                    EmitAsyncInvestigationAddressLogPreservingPointer(fieldLabel);
                break;

            case ITypeSymbol:
                if (MethodSymbol.IsStatic)
                    throw new NotSupportedException("Cannot take the address of 'self' in a static context.");

                ILGenerator.Emit(OpCodes.Ldarg_0);
                break;

            case null when addressOf.Storage is BoundArrayAccessExpression arrayAccess:
                EmitArrayElementAddress(arrayAccess);
                break;

            default:
                throw new NotSupportedException($"Cannot take address of: {addressOf.Symbol}");
        }
    }

    private void EmitStackAllocExpression(BoundStackAllocExpression stackAlloc)
    {
        if (stackAlloc.Type is IPointerTypeSymbol)
        {
            EmitStackAllocationPointer(stackAlloc);
            return;
        }

        var countLocal = ILGenerator.DeclareLocal(typeof(int));
        EmitExpression(stackAlloc.Count);
        ILGenerator.Emit(OpCodes.Stloc, countLocal);
        ILGenerator.Emit(OpCodes.Ldloc, countLocal);
        ILGenerator.Emit(OpCodes.Conv_U);
        ILGenerator.Emit(OpCodes.Sizeof, ResolveClrType(stackAlloc.ElementType));
        ILGenerator.Emit(OpCodes.Mul_Ovf_Un);
        ILGenerator.Emit(OpCodes.Localloc);
        ILGenerator.Emit(OpCodes.Ldloc, countLocal);

        var spanClrType = ResolveClrType(stackAlloc.Type);
        var pointerClrType = typeof(void).MakePointerType();
        var constructor = spanClrType.GetConstructor([pointerClrType, typeof(int)])
            ?? throw new InvalidOperationException(
                $"Missing pointer/count constructor on stack allocation target '{stackAlloc.Type.ToDisplayString()}'.");
        ILGenerator.Emit(OpCodes.Newobj, constructor);
    }

    private void EmitStackAllocationPointer(BoundStackAllocExpression stackAlloc)
    {
        EmitExpression(stackAlloc.Count);
        ILGenerator.Emit(OpCodes.Conv_U);
        ILGenerator.Emit(OpCodes.Sizeof, ResolveClrType(stackAlloc.ElementType));
        ILGenerator.Emit(OpCodes.Mul_Ovf_Un);
        ILGenerator.Emit(OpCodes.Localloc);
    }

    private void EmitDereferenceExpression(BoundDereferenceExpression dereference)
    {
        EmitExpression(dereference.Reference);
        EmitLoadIndirect(dereference.ElementType);
    }

    private void EmitCollectionExpression(BoundCollectionExpression collectionExpression)
    {
        var target = collectionExpression.Type.GetNonNullableType();

        if (TryGetSpanCollectionElementType(target, out var spanElementType))
        {
            var arrayType = Compilation.CreateArrayTypeSymbol(spanElementType);
            var arrayCollection = new BoundCollectionExpression(arrayType, collectionExpression.Elements);
            EmitCollectionExpression(arrayCollection);

            var conversion = Compilation.ClassifyConversion(arrayType, target);
            if (!conversion.Exists || !conversion.IsImplicit)
                throw new InvalidOperationException(
                    $"Missing implicit array-to-span conversion from '{arrayType}' to '{target}'.");

            EmitConversion(arrayType, target, conversion);
            return;
        }

        if (target is IArrayTypeSymbol arrayTypeSymbol)
        {
            if (arrayTypeSymbol.Rank != 1)
                throw new NotSupportedException("Collection expressions do not support multidimensional array emission.");

            if (!collectionExpression.Elements.OfType<BoundSpreadElement>().Any())
            {
                ILGenerator.Emit(OpCodes.Ldc_I4, collectionExpression.Elements.Count());
                ILGenerator.Emit(OpCodes.Newarr, ResolveClrType(arrayTypeSymbol.ElementType));

                int index = 0;
                foreach (var element in collectionExpression.Elements)
                {
                    ILGenerator.Emit(OpCodes.Dup);
                    ILGenerator.Emit(OpCodes.Ldc_I4, index);

                    EmitExpression(element);

                    EmitStoreElement(arrayTypeSymbol.ElementType);

                    index++;
                }
            }
            else
            {
                if (TryEmitArrayWithArraySpreadFastPath(arrayTypeSymbol, collectionExpression))
                    return;

                EmitArrayWithSpreads(arrayTypeSymbol, collectionExpression);
            }
        }
        else if (target is INamedTypeSymbol namedType)
        {
            if (TryEmitCollectionExpressionViaImmutableList(namedType, collectionExpression))
                return;

            if (TryEmitCollectionExpressionViaBuilderAttribute(namedType, collectionExpression))
                return;

            // Special-case: target is IEnumerable<T>. We can materialize an array T[] (arrays implement IEnumerable<T>)
            // and leave it on the stack.
            var ienumerableDef = (INamedTypeSymbol?)Compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
            if (ienumerableDef is not null &&
                SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, ienumerableDef))
            {
                var elementType = namedType.TypeArguments[0];

                // If no spreads, emit a simple array literal.
                if (!collectionExpression.Elements.OfType<BoundSpreadElement>().Any())
                {
                    ILGenerator.Emit(OpCodes.Ldc_I4, collectionExpression.Elements.Count());
                    ILGenerator.Emit(OpCodes.Newarr, ResolveClrType(elementType));

                    int index = 0;
                    foreach (var element in collectionExpression.Elements)
                    {
                        ILGenerator.Emit(OpCodes.Dup);
                        ILGenerator.Emit(OpCodes.Ldc_I4, index);

                        EmitExpression(element);

                        // Store element into the array using the existing element-store helper.
                        EmitStoreElement(elementType);

                        index++;
                    }

                    return;
                }

                if (TryEmitArrayWithArraySpreadFastPath(elementType, collectionExpression))
                    return;

                // With spreads: reuse List<T> + ToArray lowering and return the array as IEnumerable<T>.
                var listTypeDef = (INamedTypeSymbol)Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!;
                var listType = (INamedTypeSymbol)listTypeDef.Construct(elementType);

                var minimumCapacity = collectionExpression.Elements.Count(static element => element is not BoundSpreadElement);
                var ctor2 = listType.Constructors.FirstOrDefault(c => !c.IsStatic && c.Parameters.Length == 1 && c.Parameters[0].Type.SpecialType == SpecialType.System_Int32)
                    ?? listType.Constructors.First(c => !c.IsStatic && c.Parameters.Length == 0);
                var ctorInfo2 = GetConstructorInfo(ctor2);

                if (ctor2.Parameters.Length == 1)
                    ILGenerator.Emit(OpCodes.Ldc_I4, minimumCapacity);

                ILGenerator.Emit(OpCodes.Newobj, ctorInfo2);
                var listLocal = ILGenerator.DeclareLocal(ResolveClrType(listType));
                ILGenerator.Emit(OpCodes.Stloc, listLocal);

                var addMethod2 = listType.GetMembers("Add").OfType<IMethodSymbol>().First();
                var addMethodInfo = GetMethodInfo(addMethod2);

                foreach (var element in collectionExpression.Elements)
                {
                    if (element is BoundSpreadElement spread)
                    {
                        EmitSpreadElement(listLocal, spread, elementType, addMethodInfo);
                    }
                    else
                    {
                        ILGenerator.Emit(OpCodes.Ldloc, listLocal);
                        EmitExpression(element);

                        if (element.Type is { } elementRuntimeType &&
                            ShouldBoxForReferenceTarget(elementRuntimeType, elementType))
                        {
                            ILGenerator.Emit(OpCodes.Box, ResolveClrType(elementRuntimeType));
                        }

                        ILGenerator.Emit(OpCodes.Callvirt, addMethodInfo);
                    }
                }

                ILGenerator.Emit(OpCodes.Ldloc, listLocal);
                var toArrayMethod = listType.GetMembers("ToArray").OfType<IMethodSymbol>().First();
                var toArrayInfo = GetMethodInfo(toArrayMethod);
                ILGenerator.Emit(OpCodes.Callvirt, toArrayInfo);

                return;
            }

            if (namedType.TypeKind == TypeKind.Interface &&
                TryEmitCollectionExpressionViaList(namedType, collectionExpression))
            {
                return;
            }

            var ctor = namedType.Constructors.FirstOrDefault(c => !c.IsStatic && c.Parameters.Length == 0);
            if (ctor is null)
                throw new NotSupportedException("Collection type requires a parameterless constructor");

            var ctorInfo = GetConstructorInfo(ctor);

            ILGenerator.Emit(OpCodes.Newobj, ctorInfo);
            var collectionLocal = ILGenerator.DeclareLocal(ResolveClrType(namedType));
            ILGenerator.Emit(OpCodes.Stloc, collectionLocal);

            var addMethod = collectionExpression.CollectionSymbol as IMethodSymbol;

            if (addMethod is not null)
            {
                var paramType = addMethod.Parameters[0].Type;
                foreach (var element in collectionExpression.Elements)
                {
                    if (element is BoundSpreadElement spread)
                    {
                        EmitSpreadElement(collectionLocal, spread, paramType, addMethod, namedType);
                    }
                    else
                    {
                        ILGenerator.Emit(OpCodes.Ldloc, collectionLocal);
                        EmitExpression(element);

                        if (element.Type is { } elementRuntimeType &&
                            ShouldBoxForReferenceTarget(elementRuntimeType, paramType))
                        {
                            ILGenerator.Emit(OpCodes.Box, ResolveClrType(elementRuntimeType));
                        }

                        var isInterfaceCall = addMethod.ContainingType?.TypeKind == TypeKind.Interface;

                        if (!addMethod.ContainingType!.IsValueType && (addMethod.IsVirtual || isInterfaceCall))
                            ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(addMethod));
                        else
                            ILGenerator.Emit(OpCodes.Call, GetMethodInfo(addMethod));

                        EmitCollectionAddResult(collectionLocal, namedType, addMethod.ReturnType);
                    }
                }
            }

            ILGenerator.Emit(OpCodes.Ldloc, collectionLocal);
        }
    }

    private void EmitDictionaryExpression(BoundDictionaryExpression dictionaryExpression)
    {
        if (dictionaryExpression.Type is not INamedTypeSymbol namedType)
            throw new NotSupportedException("Dictionary expressions require a named target type.");

        if (TryEmitDictionaryExpressionViaImmutableDictionary(namedType, dictionaryExpression))
            return;

        var constructor = namedType.Constructors.FirstOrDefault(static ctor => !ctor.IsStatic && ctor.Parameters.Length == 0);
        if (constructor is null)
            throw new NotSupportedException("Dictionary expression target requires a parameterless constructor.");

        var addMethod = dictionaryExpression.CollectionSymbol as IMethodSymbol
            ?? namedType.GetMembers("Add")
                .OfType<IMethodSymbol>()
                .FirstOrDefault(static method => !method.IsStatic && method.Parameters.Length == 2);

        if (addMethod is null)
            throw new NotSupportedException("Dictionary expression target requires an Add(key, value) method.");

        ILGenerator.Emit(OpCodes.Newobj, GetConstructorInfo(constructor));
        var dictionaryLocal = ILGenerator.DeclareLocal(ResolveClrType(namedType));
        ILGenerator.Emit(OpCodes.Stloc, dictionaryLocal);

        foreach (var element in dictionaryExpression.Elements)
        {
            switch (element)
            {
                case DictionaryEntryBinding entry:
                    EmitDictionaryEntryAdd(dictionaryLocal, entry, addMethod);
                    break;
                case DictionarySpreadBinding spread:
                    EmitDictionarySpreadAdd(dictionaryLocal, spread.Expression, addMethod.Parameters[0].Type, addMethod.Parameters[1].Type, addMethod);
                    break;
                case DictionaryComprehensionBinding comprehension:
                    EmitDictionaryComprehensionAdd(dictionaryLocal, comprehension, addMethod);
                    break;
            }
        }

        ILGenerator.Emit(OpCodes.Ldloc, dictionaryLocal);
    }

    private bool TryEmitDictionaryExpressionViaImmutableDictionary(INamedTypeSymbol targetType, BoundDictionaryExpression dictionaryExpression)
    {
        if (!string.Equals(targetType.OriginalDefinition.ToFullyQualifiedMetadataName(), "System.Collections.Immutable.ImmutableDictionary`2", StringComparison.Ordinal))
            return false;

        var immutableDictionaryHelpers = (INamedTypeSymbol?)Compilation.GetTypeByMetadataName("System.Collections.Immutable.ImmutableDictionary");
        if (immutableDictionaryHelpers is null)
            return false;

        var keyType = targetType.TypeArguments[0];
        var valueType = targetType.TypeArguments[1];

        var createRangeDefinition = immutableDictionaryHelpers
            .GetMembers("CreateRange")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(static method => method.IsStatic && method.TypeParameters.Length == 2 && method.Parameters.Length == 1);

        if (createRangeDefinition is null)
            return false;

        var createRange = createRangeDefinition.Construct(keyType, valueType);
        var dictionaryDefinition = (INamedTypeSymbol?)Compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
        if (dictionaryDefinition is null)
            return false;

        var mutableDictionaryType = (INamedTypeSymbol)dictionaryDefinition.Construct(keyType, valueType);
        var constructor = mutableDictionaryType.Constructors.FirstOrDefault(static ctor => !ctor.IsStatic && ctor.Parameters.Length == 0);
        var addMethod = mutableDictionaryType.GetMembers("Add").OfType<IMethodSymbol>().FirstOrDefault(static method => !method.IsStatic && method.Parameters.Length == 2);
        if (constructor is null || addMethod is null)
            return false;

        ILGenerator.Emit(OpCodes.Newobj, GetConstructorInfo(constructor));
        var dictionaryLocal = ILGenerator.DeclareLocal(ResolveClrType(mutableDictionaryType));
        ILGenerator.Emit(OpCodes.Stloc, dictionaryLocal);

        foreach (var element in dictionaryExpression.Elements)
        {
            switch (element)
            {
                case DictionaryEntryBinding entry:
                    EmitDictionaryEntryAdd(dictionaryLocal, entry, addMethod);
                    break;
                case DictionarySpreadBinding spread:
                    EmitDictionarySpreadAdd(dictionaryLocal, spread.Expression, keyType, valueType, addMethod);
                    break;
                case DictionaryComprehensionBinding comprehension:
                    EmitDictionaryComprehensionAdd(dictionaryLocal, comprehension, addMethod);
                    break;
            }
        }

        ILGenerator.Emit(OpCodes.Ldloc, dictionaryLocal);
        ILGenerator.Emit(OpCodes.Call, GetMethodInfo(createRange));
        return true;
    }

    private static bool TryGetSpanCollectionElementType(
        ITypeSymbol type,
        out ITypeSymbol elementType)
    {
        elementType = null!;

        if (type is not INamedTypeSymbol named ||
            named.TypeArguments.Length != 1 ||
            named.ContainingNamespace?.ToDisplayString() != "System" ||
            named.Name is not ("Span" or "ReadOnlySpan"))
        {
            return false;
        }

        elementType = named.TypeArguments[0];
        return true;
    }

    private bool TryEmitCollectionExpressionViaImmutableList(INamedTypeSymbol targetType, BoundCollectionExpression collectionExpression)
    {
        if (!string.Equals(targetType.OriginalDefinition.ToFullyQualifiedMetadataName(), "System.Collections.Immutable.ImmutableList`1", StringComparison.Ordinal))
            return false;

        var immutableListHelpers = (INamedTypeSymbol?)Compilation.GetTypeByMetadataName("System.Collections.Immutable.ImmutableList");
        if (immutableListHelpers is null)
            return false;

        var elementType = targetType.TypeArguments[0];
        var createRangeDefinition = immutableListHelpers
            .GetMembers("CreateRange")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(static method => method.IsStatic && method.TypeParameters.Length == 1 && method.Parameters.Length == 1);

        if (createRangeDefinition is null)
            return false;

        var createRange = createRangeDefinition.Construct(elementType);
        EmitCollectionExpressionAsArray(elementType, collectionExpression);
        ILGenerator.Emit(OpCodes.Call, GetMethodInfo(createRange));
        return true;
    }

    private bool TryEmitCollectionExpressionViaBuilderAttribute(INamedTypeSymbol targetType, BoundCollectionExpression collectionExpression)
    {
        Type targetClrType;
        try
        {
            targetClrType = ResolveClrType(targetType);
        }
        catch
        {
            return false;
        }

        System.Reflection.CustomAttributeData? builderAttribute = null;
        try
        {
            builderAttribute = targetClrType
                .GetCustomAttributesData()
                .FirstOrDefault(static attribute =>
                    string.Equals(
                        attribute.AttributeType.FullName,
                        "System.Runtime.CompilerServices.CollectionBuilderAttribute",
                        StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }

        if (builderAttribute is null || builderAttribute.ConstructorArguments.Count < 2)
            return false;

        var builderType = builderAttribute.ConstructorArguments[0].Value as Type;
        var methodName = builderAttribute.ConstructorArguments[1].Value as string;
        if (builderType is null || string.IsNullOrEmpty(methodName))
            return false;

        var methods = builderType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToArray();

        if (methods.Length == 0)
            return false;

        var elements = collectionExpression.Elements.ToArray();
        if (elements.Any(static element => element is BoundSpreadElement))
            return TryEmitCollectionExpressionViaBuilderAttributeWithSpreads(targetType, targetClrType, methods, elements);

        MethodInfo? selectedMethod = null;
        Type? paramsElementType = null;
        int fixedArgumentCount = 0;

        foreach (var candidate in methods)
        {
            var method = candidate;
            if (method.IsGenericMethodDefinition)
            {
                if (!targetClrType.IsGenericType ||
                    method.GetGenericArguments().Length != targetClrType.GetGenericArguments().Length)
                {
                    continue;
                }

                try
                {
                    method = method.MakeGenericMethod(targetClrType.GetGenericArguments());
                }
                catch
                {
                    continue;
                }
            }

            if (!targetClrType.IsAssignableFrom(method.ReturnType))
                continue;

            var parameters = method.GetParameters();
            var hasParamsArray = parameters.Length > 0 &&
                                 parameters[^1].GetCustomAttributes(typeof(ParamArrayAttribute), inherit: false).Length > 0 &&
                                 parameters[^1].ParameterType.IsArray;

            if (hasParamsArray)
            {
                var requiredCount = parameters.Length - 1;
                if (elements.Length < requiredCount)
                    continue;

                selectedMethod = method;
                fixedArgumentCount = requiredCount;
                paramsElementType = parameters[^1].ParameterType.GetElementType();
                break;
            }

            if (parameters.Length == elements.Length)
            {
                selectedMethod = method;
                fixedArgumentCount = parameters.Length;
                paramsElementType = null;
                break;
            }
        }

        if (selectedMethod is null)
            return false;

        var selectedParameters = selectedMethod.GetParameters();
        for (var i = 0; i < fixedArgumentCount; i++)
            EmitBuilderArgument(elements[i], selectedParameters[i].ParameterType);

        if (paramsElementType is not null)
        {
            var paramsCount = elements.Length - fixedArgumentCount;
            ILGenerator.Emit(OpCodes.Ldc_I4, paramsCount);
            ILGenerator.Emit(OpCodes.Newarr, paramsElementType);

            for (var i = 0; i < paramsCount; i++)
            {
                ILGenerator.Emit(OpCodes.Dup);
                ILGenerator.Emit(OpCodes.Ldc_I4, i);
                EmitBuilderArgument(elements[fixedArgumentCount + i], paramsElementType);
                EmitStoreElementByRuntimeType(paramsElementType);
            }
        }

        ILGenerator.Emit(OpCodes.Call, selectedMethod);
        return true;
    }

    private bool TryEmitCollectionExpressionViaBuilderAttributeWithSpreads(
        INamedTypeSymbol targetType,
        Type targetClrType,
        MethodInfo[] methods,
        BoundExpression[] elements)
    {
        if (!TryGetCollectionBuilderElementType(targetType, out var elementType))
            return false;

        Type elementClrType;
        try
        {
            elementClrType = ResolveClrType(elementType);
        }
        catch
        {
            return false;
        }

        if (elementClrType.IsByRefLike)
            return false;

        var arrayClrType = elementClrType.MakeArrayType();

        MethodInfo? selectedMethod = null;
        Type? selectedParameterType = null;

        foreach (var candidate in methods)
        {
            var method = CloseBuilderMethod(candidate, targetClrType);
            if (method is null || !targetClrType.IsAssignableFrom(method.ReturnType))
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                continue;

            var parameterType = parameters[0].ParameterType;
            if (!CanBuilderParameterConsumeArray(parameterType, arrayClrType, elementClrType))
                continue;

            selectedMethod = method;
            selectedParameterType = parameterType;
            break;
        }

        if (selectedMethod is null || selectedParameterType is null)
            return false;

        var listTypeDef = (INamedTypeSymbol?)Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        if (listTypeDef is null)
            return false;

        var listType = (INamedTypeSymbol)listTypeDef.Construct(elementType);
        var minimumCapacity = elements.Count(static element => element is not BoundSpreadElement);
        var ctor = listType.Constructors.FirstOrDefault(c => !c.IsStatic && c.Parameters.Length == 1 && c.Parameters[0].Type.SpecialType == SpecialType.System_Int32)
            ?? listType.Constructors.FirstOrDefault(c => !c.IsStatic && c.Parameters.Length == 0);
        if (ctor is null)
            return false;

        if (ctor.Parameters.Length == 1)
            ILGenerator.Emit(OpCodes.Ldc_I4, minimumCapacity);

        ILGenerator.Emit(OpCodes.Newobj, GetConstructorInfo(ctor));
        var listLocal = ILGenerator.DeclareLocal(ResolveClrType(listType));
        ILGenerator.Emit(OpCodes.Stloc, listLocal);

        var addMethod = listType.GetMembers("Add").OfType<IMethodSymbol>().FirstOrDefault(static method => method.Parameters.Length == 1);
        if (addMethod is null)
            return false;

        var addMethodInfo = GetMethodInfo(addMethod);

        foreach (var element in elements)
        {
            if (element is BoundSpreadElement spread)
            {
                EmitSpreadElement(listLocal, spread, elementType, addMethodInfo);
                continue;
            }

            ILGenerator.Emit(OpCodes.Ldloc, listLocal);
            EmitExpression(element);

            if (element.Type is { } elementRuntimeType &&
                ShouldBoxForReferenceTarget(elementRuntimeType, elementType))
            {
                ILGenerator.Emit(OpCodes.Box, ResolveClrType(elementRuntimeType));
            }

            ILGenerator.Emit(OpCodes.Callvirt, addMethodInfo);
        }

        ILGenerator.Emit(OpCodes.Ldloc, listLocal);
        var toArrayMethod = listType.GetMembers("ToArray").OfType<IMethodSymbol>().FirstOrDefault();
        if (toArrayMethod is null)
            return false;

        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(toArrayMethod));
        var arrayLocal = ILGenerator.DeclareLocal(arrayClrType);
        ILGenerator.Emit(OpCodes.Stloc, arrayLocal);

        if (!EmitBuilderArrayArgument(selectedParameterType, arrayClrType, elementClrType, arrayLocal))
            return false;

        ILGenerator.Emit(OpCodes.Call, selectedMethod);
        return true;
    }

    private static MethodInfo? CloseBuilderMethod(MethodInfo candidate, Type targetClrType)
    {
        if (!candidate.IsGenericMethodDefinition)
            return candidate;

        if (!targetClrType.IsGenericType ||
            candidate.GetGenericArguments().Length != targetClrType.GetGenericArguments().Length)
        {
            return null;
        }

        try
        {
            return candidate.MakeGenericMethod(targetClrType.GetGenericArguments());
        }
        catch
        {
            return null;
        }
    }

    private static bool CanBuilderParameterConsumeArray(Type parameterType, Type arrayType, Type elementType)
    {
        if (parameterType == arrayType || parameterType.IsAssignableFrom(arrayType))
            return true;

        if (!parameterType.IsGenericType)
            return false;

        var definition = parameterType.GetGenericTypeDefinition();
        if (definition != typeof(ReadOnlySpan<>))
            return false;

        return parameterType.GetGenericArguments()[0] == elementType;
    }

    private bool EmitBuilderArrayArgument(Type parameterType, Type arrayType, Type elementType, IILocal arrayLocal)
    {
        if (parameterType == arrayType || parameterType.IsAssignableFrom(arrayType))
        {
            ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
            return true;
        }

        if (!parameterType.IsGenericType || parameterType.GetGenericTypeDefinition() != typeof(ReadOnlySpan<>))
            return false;

        if (parameterType.GetGenericArguments()[0] != elementType)
            return false;

        var spanCtor = parameterType.GetConstructor([arrayType]);
        if (spanCtor is null)
            return false;

        ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
        ILGenerator.Emit(OpCodes.Newobj, spanCtor);
        return true;
    }

    private static bool TryGetCollectionBuilderElementType(INamedTypeSymbol targetType, out ITypeSymbol elementType)
    {
        if (targetType.Arity == 1 && targetType.TypeArguments.Length == 1)
        {
            elementType = targetType.TypeArguments[0];
            return true;
        }

        elementType = null!;
        return false;
    }

    private void EmitBuilderArgument(BoundExpression expression, Type parameterType)
    {
        EmitExpression(expression);

        Type? expressionType = null;
        if (expression.Type is not null)
            expressionType = ResolveClrType(expression.Type);

        if (expressionType is null || expressionType == parameterType)
            return;

        if (expressionType.IsValueType && !parameterType.IsValueType)
        {
            ILGenerator.Emit(OpCodes.Box, expressionType);
            if (parameterType != typeof(object))
                ILGenerator.Emit(OpCodes.Castclass, parameterType);
            return;
        }

        if (!expressionType.IsValueType && !parameterType.IsValueType)
        {
            ILGenerator.Emit(OpCodes.Castclass, parameterType);
            return;
        }
    }

    private void EmitStoreElementByRuntimeType(Type elementType)
    {
        if (!elementType.IsValueType)
        {
            ILGenerator.Emit(OpCodes.Stelem_Ref);
            return;
        }

        if (elementType == typeof(int))
        {
            ILGenerator.Emit(OpCodes.Stelem_I4);
            return;
        }

        if (elementType == typeof(long))
        {
            ILGenerator.Emit(OpCodes.Stelem_I8);
            return;
        }

        if (elementType == typeof(short))
        {
            ILGenerator.Emit(OpCodes.Stelem_I2);
            return;
        }

        if (elementType == typeof(byte))
        {
            ILGenerator.Emit(OpCodes.Stelem_I1);
            return;
        }

        if (elementType == typeof(float))
        {
            ILGenerator.Emit(OpCodes.Stelem_R4);
            return;
        }

        if (elementType == typeof(double))
        {
            ILGenerator.Emit(OpCodes.Stelem_R8);
            return;
        }

        ILGenerator.Emit(OpCodes.Stelem, elementType);
    }

    private bool TryEmitCollectionExpressionViaList(INamedTypeSymbol targetType, BoundCollectionExpression collectionExpression)
    {
        if (targetType.Arity != 1)
            return false;

        var listTypeDef = (INamedTypeSymbol?)Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        if (listTypeDef is null)
            return false;

        var elementType = targetType.TypeArguments[0];
        var listType = (INamedTypeSymbol)listTypeDef.Construct(elementType);
        var conversion = Compilation.ClassifyConversion(listType, targetType, includeUserDefined: false);
        if (!conversion.Exists)
            return false;

        var minimumCapacity = collectionExpression.Elements.Count(static element => element is not BoundSpreadElement);
        var ctor = listType.Constructors.FirstOrDefault(c => !c.IsStatic && c.Parameters.Length == 1 && c.Parameters[0].Type.SpecialType == SpecialType.System_Int32)
            ?? listType.Constructors.First(c => !c.IsStatic && c.Parameters.Length == 0);
        var ctorInfo = GetConstructorInfo(ctor);

        if (ctor.Parameters.Length == 1)
            ILGenerator.Emit(OpCodes.Ldc_I4, minimumCapacity);

        ILGenerator.Emit(OpCodes.Newobj, ctorInfo);
        var listLocal = ILGenerator.DeclareLocal(ResolveClrType(listType));
        ILGenerator.Emit(OpCodes.Stloc, listLocal);

        var addMethod = listType.GetMembers("Add").OfType<IMethodSymbol>().First();
        var addMethodInfo = GetMethodInfo(addMethod);

        foreach (var element in collectionExpression.Elements)
        {
            if (element is BoundSpreadElement spread)
            {
                EmitSpreadElement(listLocal, spread, elementType, addMethodInfo);
            }
            else
            {
                ILGenerator.Emit(OpCodes.Ldloc, listLocal);
                EmitExpression(element);

                if (element.Type is { } elementRuntimeType &&
                    ShouldBoxForReferenceTarget(elementRuntimeType, elementType))
                {
                    ILGenerator.Emit(OpCodes.Box, ResolveClrType(elementRuntimeType));
                }

                ILGenerator.Emit(OpCodes.Callvirt, addMethodInfo);
            }
        }

        ILGenerator.Emit(OpCodes.Ldloc, listLocal);
        return true;
    }

    private void EmitCollectionExpressionAsArray(ITypeSymbol elementType, BoundCollectionExpression collectionExpression)
    {
        var arrayType = (IArrayTypeSymbol)Compilation.CreateArrayTypeSymbol(elementType);

        if (!collectionExpression.Elements.OfType<BoundSpreadElement>().Any())
        {
            ILGenerator.Emit(OpCodes.Ldc_I4, collectionExpression.Elements.Count());
            ILGenerator.Emit(OpCodes.Newarr, ResolveClrType(elementType));

            var index = 0;
            foreach (var element in collectionExpression.Elements)
            {
                ILGenerator.Emit(OpCodes.Dup);
                ILGenerator.Emit(OpCodes.Ldc_I4, index);
                EmitExpression(element);
                EmitStoreElement(elementType);
                index++;
            }

            return;
        }

        if (TryEmitArrayWithArraySpreadFastPath(elementType, collectionExpression))
            return;

        EmitArrayWithSpreads(arrayType, collectionExpression);
    }

    private void EmitDictionaryExpressionAsKeyValuePairSequence(
        ITypeSymbol keyType,
        ITypeSymbol valueType,
        BoundDictionaryExpression dictionaryExpression)
    {
        var keyValuePairDefinition = Compilation.GetTypeByMetadataName("System.Collections.Generic.KeyValuePair`2") as INamedTypeSymbol
            ?? throw new NotSupportedException("Dictionary expressions require System.Collections.Generic.KeyValuePair<TKey, TValue>.");
        var keyValuePairType = (INamedTypeSymbol)keyValuePairDefinition.Construct(keyType, valueType);
        var constructor = keyValuePairType.Constructors.FirstOrDefault(static ctor => !ctor.IsStatic && ctor.Parameters.Length == 2)
            ?? throw new NotSupportedException("KeyValuePair<TKey, TValue> constructor could not be resolved.");

        var listTypeDefinition = (INamedTypeSymbol?)Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")
            ?? throw new NotSupportedException("Dictionary expressions require System.Collections.Generic.List<T>.");
        var listType = (INamedTypeSymbol)listTypeDefinition.Construct(keyValuePairType);
        var listCtor = listType.Constructors.FirstOrDefault(static ctor => !ctor.IsStatic && ctor.Parameters.Length == 0)
            ?? throw new NotSupportedException("List<T> must expose a parameterless constructor.");
        var addMethod = listType.GetMembers("Add").OfType<IMethodSymbol>().First(static method => !method.IsStatic && method.Parameters.Length == 1);

        ILGenerator.Emit(OpCodes.Newobj, GetConstructorInfo(listCtor));
        var listLocal = ILGenerator.DeclareLocal(ResolveClrType(listType));
        ILGenerator.Emit(OpCodes.Stloc, listLocal);

        foreach (var element in dictionaryExpression.Elements)
        {
            switch (element)
            {
                case DictionaryEntryBinding entry:
                    ILGenerator.Emit(OpCodes.Ldloc, listLocal);
                    EmitExpression(entry.Key);
                    EmitExpression(entry.Value);
                    ILGenerator.Emit(OpCodes.Newobj, GetConstructorInfo(constructor));
                    ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(addMethod));
                    break;
                case DictionarySpreadBinding spread:
                    EmitDictionaryKeyValuePairSpreadAdd(listLocal, spread.Expression, keyValuePairType, addMethod);
                    break;
                case DictionaryComprehensionBinding comprehension:
                    EmitDictionaryComprehensionKeyValuePairAdd(listLocal, comprehension, keyValuePairType, constructor, addMethod);
                    break;
            }
        }

        ILGenerator.Emit(OpCodes.Ldloc, listLocal);
    }

    private void EmitDictionaryEntryAdd(IILocal dictionaryLocal, DictionaryEntryBinding entry, IMethodSymbol addMethod)
    {
        ILGenerator.Emit(OpCodes.Ldloc, dictionaryLocal);
        EmitExpression(entry.Key);
        EmitExpression(entry.Value);

        var isInterfaceCall = addMethod.ContainingType?.TypeKind == TypeKind.Interface;
        if (!addMethod.ContainingType!.IsValueType && (addMethod.IsVirtual || isInterfaceCall))
            ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(addMethod));
        else
            ILGenerator.Emit(OpCodes.Call, GetMethodInfo(addMethod));
    }

    private void EmitDictionarySpreadAdd(
        IILocal dictionaryLocal,
        BoundExpression spreadExpression,
        ITypeSymbol targetKeyType,
        ITypeSymbol targetValueType,
        IMethodSymbol addMethod)
    {
        var spreadItemType = GetDictionarySpreadItemType(spreadExpression.Type!);
        var keyGetter = spreadItemType.GetMembers("Key").OfType<IPropertySymbol>().First().GetMethod!;
        var valueGetter = spreadItemType.GetMembers("Value").OfType<IPropertySymbol>().First().GetMethod!;

        EmitEnumerableLoop(
            spreadExpression,
            spreadItemType,
            currentLocal =>
            {
                ILGenerator.Emit(OpCodes.Ldloc, dictionaryLocal);
                ILGenerator.Emit(OpCodes.Ldloca, currentLocal);
                ILGenerator.Emit(OpCodes.Call, GetMethodInfo(keyGetter));
                EmitDictionarySpreadValueConversion(keyGetter.ReturnType, targetKeyType);
                ILGenerator.Emit(OpCodes.Ldloca, currentLocal);
                ILGenerator.Emit(OpCodes.Call, GetMethodInfo(valueGetter));
                EmitDictionarySpreadValueConversion(valueGetter.ReturnType, targetValueType);

                var isInterfaceCall = addMethod.ContainingType?.TypeKind == TypeKind.Interface;
                if (!addMethod.ContainingType!.IsValueType && (addMethod.IsVirtual || isInterfaceCall))
                    ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(addMethod));
                else
                    ILGenerator.Emit(OpCodes.Call, GetMethodInfo(addMethod));
            });
    }

    private void EmitDictionaryKeyValuePairSpreadAdd(
        IILocal listLocal,
        BoundExpression spreadExpression,
        INamedTypeSymbol keyValuePairType,
        IMethodSymbol listAddMethod)
    {
        EmitEnumerableLoop(
            spreadExpression,
            keyValuePairType,
            currentLocal =>
            {
                ILGenerator.Emit(OpCodes.Ldloc, listLocal);
                ILGenerator.Emit(OpCodes.Ldloc, currentLocal);
                ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(listAddMethod));
            });
    }

    private void EmitDictionaryComprehensionAdd(
        IILocal dictionaryLocal,
        DictionaryComprehensionBinding comprehension,
        IMethodSymbol addMethod)
    {
        EmitDictionaryComprehensionLoop(
            comprehension,
            currentScope =>
            {
                ILGenerator.Emit(OpCodes.Ldloc, dictionaryLocal);
                new ExpressionGenerator(currentScope, comprehension.KeySelector, EmitContext.Value).Emit2();
                if (comprehension.KeySelector.Type is { } keySelectorType &&
                    ShouldBoxForReferenceTarget(keySelectorType, addMethod.Parameters[0].Type))
                {
                    ILGenerator.Emit(OpCodes.Box, ResolveClrType(keySelectorType));
                }

                new ExpressionGenerator(currentScope, comprehension.ValueSelector, EmitContext.Value).Emit2();
                if (comprehension.ValueSelector.Type is { } valueSelectorType &&
                    ShouldBoxForReferenceTarget(valueSelectorType, addMethod.Parameters[1].Type))
                {
                    ILGenerator.Emit(OpCodes.Box, ResolveClrType(valueSelectorType));
                }

                var isInterfaceCall = addMethod.ContainingType?.TypeKind == TypeKind.Interface;
                if (!addMethod.ContainingType!.IsValueType && (addMethod.IsVirtual || isInterfaceCall))
                    ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(addMethod));
                else
                    ILGenerator.Emit(OpCodes.Call, GetMethodInfo(addMethod));
            });
    }

    private void EmitDictionaryComprehensionKeyValuePairAdd(
        IILocal listLocal,
        DictionaryComprehensionBinding comprehension,
        INamedTypeSymbol keyValuePairType,
        IMethodSymbol constructor,
        IMethodSymbol listAddMethod)
    {
        EmitDictionaryComprehensionLoop(
            comprehension,
            currentScope =>
            {
                ILGenerator.Emit(OpCodes.Ldloc, listLocal);
                new ExpressionGenerator(currentScope, comprehension.KeySelector, EmitContext.Value).Emit2();
                new ExpressionGenerator(currentScope, comprehension.ValueSelector, EmitContext.Value).Emit2();
                ILGenerator.Emit(OpCodes.Newobj, GetConstructorInfo(constructor));
                ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(listAddMethod));
            });
    }

    private void EmitDictionaryComprehensionLoop(
        DictionaryComprehensionBinding comprehension,
        Action<Scope> emitBody)
    {
        var iterationLocalBuilder = ILGenerator.DeclareLocal(ResolveClrType(comprehension.IterationLocal.Type));
        var scope = new Scope(this);
        scope.AddLocal(comprehension.IterationLocal, iterationLocalBuilder);

        if (comprehension.Source is BoundRangeExpression rangeSource)
        {
            EmitDictionaryComprehensionRangeLoop(comprehension, rangeSource, scope, iterationLocalBuilder, emitBody);
            return;
        }

        EmitNonGenericEnumerable(comprehension.Source);

        var enumerable = (INamedTypeSymbol)Compilation.GetTypeByMetadataName("System.Collections.IEnumerable")!;
        var getEnumerator = enumerable.GetMembers(nameof(IEnumerable.GetEnumerator)).OfType<IMethodSymbol>().First();
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(getEnumerator));

        var enumeratorType = getEnumerator.ReturnType;
        var enumeratorLocal = ILGenerator.DeclareLocal(ResolveClrType(enumeratorType));
        ILGenerator.Emit(OpCodes.Stloc, enumeratorLocal);

        var loopStart = ILGenerator.DefineLabel();
        var loopNext = ILGenerator.DefineLabel();
        var loopEnd = ILGenerator.DefineLabel();

        ILGenerator.MarkLabel(loopStart);
        var moveNext = enumeratorType.GetMembers(nameof(IEnumerator.MoveNext)).OfType<IMethodSymbol>().First();
        ILGenerator.Emit(OpCodes.Ldloc, enumeratorLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(moveNext));
        ILGenerator.Emit(OpCodes.Brfalse, loopEnd);

        var currentGetter = enumeratorType
            .GetMembers(nameof(IEnumerator.Current))
            .OfType<IPropertySymbol>()
            .First()
            .GetMethod!;
        ILGenerator.Emit(OpCodes.Ldloc, enumeratorLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(currentGetter));

        var iterationClrType = ResolveClrType(comprehension.IterationLocal.Type);
        if (comprehension.IterationLocal.Type.IsValueType)
            ILGenerator.Emit(OpCodes.Unbox_Any, iterationClrType);
        else
            ILGenerator.Emit(OpCodes.Castclass, iterationClrType);

        ILGenerator.Emit(OpCodes.Stloc, iterationLocalBuilder);

        if (comprehension.Condition is not null)
        {
            new ExpressionGenerator(scope, comprehension.Condition, EmitContext.Value).Emit2();
            ILGenerator.Emit(OpCodes.Brfalse, loopNext);
        }

        emitBody(scope);

        ILGenerator.MarkLabel(loopNext);
        ILGenerator.Emit(OpCodes.Br, loopStart);
        ILGenerator.MarkLabel(loopEnd);
    }

    private void EmitDictionaryComprehensionRangeLoop(
        DictionaryComprehensionBinding comprehension,
        BoundRangeExpression rangeSource,
        Scope scope,
        IILocal iterationLocalBuilder,
        Action<Scope> emitBody)
    {
        var elementType = comprehension.IterationLocal.Type.UnwrapLiteralType() ?? comprehension.IterationLocal.Type;
        var elementClrType = ResolveClrType(elementType);

        var startExpr = rangeSource.Left?.Value ?? throw new InvalidOperationException("Range comprehension requires a start value.");
        var endExpr = rangeSource.Right?.Value ?? throw new InvalidOperationException("Range comprehension requires an end value.");

        var startLocal = ILGenerator.DeclareLocal(elementClrType);
        new ExpressionGenerator(scope, startExpr, EmitContext.None).Emit2();
        ILGenerator.Emit(OpCodes.Stloc, startLocal);

        var endLocal = ILGenerator.DeclareLocal(elementClrType);
        new ExpressionGenerator(scope, endExpr, EmitContext.None).Emit2();
        ILGenerator.Emit(OpCodes.Stloc, endLocal);

        var stepLocal = ILGenerator.DeclareLocal(elementClrType);
        EmitNumericOne(elementType);
        ILGenerator.Emit(OpCodes.Stloc, stepLocal);

        var loopStart = ILGenerator.DefineLabel();
        var loopNext = ILGenerator.DefineLabel();
        var loopEnd = ILGenerator.DefineLabel();
        var positiveStepLabel = ILGenerator.DefineLabel();
        var negativeStepLabel = ILGenerator.DefineLabel();
        var bodyLabel = ILGenerator.DefineLabel();

        ILGenerator.MarkLabel(loopStart);
        EmitBranchIfGreaterThanZero(elementType, stepLocal, positiveStepLabel);
        EmitBranchIfLessThanZero(elementType, stepLocal, negativeStepLabel);
        ILGenerator.Emit(OpCodes.Br, loopEnd);

        ILGenerator.MarkLabel(positiveStepLabel);
        var upperExclusive = rangeSource.IsUpperExclusive;
        EmitRangeLoopBreakCondition(elementType, startLocal, endLocal, loopEnd, positiveStep: true, upperExclusive);
        ILGenerator.Emit(OpCodes.Br, bodyLabel);

        ILGenerator.MarkLabel(negativeStepLabel);
        EmitRangeLoopBreakCondition(elementType, startLocal, endLocal, loopEnd, positiveStep: false, upperExclusive);

        ILGenerator.MarkLabel(bodyLabel);
        ILGenerator.Emit(OpCodes.Ldloc, startLocal);
        ILGenerator.Emit(OpCodes.Stloc, iterationLocalBuilder);

        if (comprehension.Condition is not null)
        {
            new ExpressionGenerator(scope, comprehension.Condition, EmitContext.Value).Emit2();
            ILGenerator.Emit(OpCodes.Brfalse, loopNext);
        }

        emitBody(scope);

        ILGenerator.MarkLabel(loopNext);
        EmitRangeLoopIncrement(elementType, startLocal, stepLocal);
        ILGenerator.Emit(OpCodes.Br, loopStart);
        ILGenerator.MarkLabel(loopEnd);
    }

    private void EmitEnumerableLoop(
        BoundExpression sourceExpression,
        ITypeSymbol itemType,
        Action<IILocal> emitBody)
    {
        EmitNonGenericEnumerable(sourceExpression);

        var enumerable = (INamedTypeSymbol)Compilation.GetTypeByMetadataName("System.Collections.IEnumerable")!;
        var getEnumerator = enumerable.GetMembers(nameof(IEnumerable.GetEnumerator)).OfType<IMethodSymbol>().First();
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(getEnumerator));

        var enumeratorType = getEnumerator.ReturnType;
        var enumeratorLocal = ILGenerator.DeclareLocal(ResolveClrType(enumeratorType));
        ILGenerator.Emit(OpCodes.Stloc, enumeratorLocal);

        var itemLocal = ILGenerator.DeclareLocal(ResolveClrType(itemType));
        var loopStart = ILGenerator.DefineLabel();
        var loopEnd = ILGenerator.DefineLabel();

        ILGenerator.MarkLabel(loopStart);
        var moveNext = enumeratorType.GetMembers(nameof(IEnumerator.MoveNext)).OfType<IMethodSymbol>().First();
        ILGenerator.Emit(OpCodes.Ldloc, enumeratorLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(moveNext));
        ILGenerator.Emit(OpCodes.Brfalse, loopEnd);

        var currentGetter = enumeratorType
            .GetMembers(nameof(IEnumerator.Current))
            .OfType<IPropertySymbol>()
            .First()
            .GetMethod!;
        ILGenerator.Emit(OpCodes.Ldloc, enumeratorLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(currentGetter));

        var itemClrType = ResolveClrType(itemType);
        if (itemType.IsValueType)
            ILGenerator.Emit(OpCodes.Unbox_Any, itemClrType);
        else
            ILGenerator.Emit(OpCodes.Castclass, itemClrType);

        ILGenerator.Emit(OpCodes.Stloc, itemLocal);
        emitBody(itemLocal);
        ILGenerator.Emit(OpCodes.Br, loopStart);
        ILGenerator.MarkLabel(loopEnd);
    }

    private INamedTypeSymbol GetDictionarySpreadItemType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol arrayType && arrayType.ElementType is INamedTypeSymbol arrayElementType)
            return arrayElementType;

        if (type is INamedTypeSymbol namedType)
        {
            foreach (var candidate in new[] { namedType }.Concat(namedType.AllInterfaces))
            {
                if (candidate.OriginalDefinition is INamedTypeSymbol definition &&
                    string.Equals(definition.ToFullyQualifiedMetadataName(), "System.Collections.Generic.IEnumerable`1", StringComparison.Ordinal) &&
                    candidate.TypeArguments[0] is INamedTypeSymbol itemType)
                {
                    return itemType;
                }
            }
        }

        throw new NotSupportedException("Dictionary spread expressions require IEnumerable<KeyValuePair<TKey, TValue>>.");
    }

    private void EmitDictionarySpreadValueConversion(ITypeSymbol sourceType, ITypeSymbol targetType)
    {
        if (ShouldBoxForReferenceTarget(sourceType, targetType))
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(sourceType));
    }

    private void EmitArrayWithSpreads(IArrayTypeSymbol arrayTypeSymbol, BoundCollectionExpression collectionExpression)
    {
        var elementType = arrayTypeSymbol.ElementType;
        var listTypeDef = (INamedTypeSymbol)Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!;
        var listType = (INamedTypeSymbol)listTypeDef.Construct(elementType);

        var minimumCapacity = collectionExpression.Elements.Count(static element => element is not BoundSpreadElement);
        var ctor = listType.Constructors.FirstOrDefault(c => !c.IsStatic && c.Parameters.Length == 1 && c.Parameters[0].Type.SpecialType == SpecialType.System_Int32)
            ?? listType.Constructors.First(c => !c.IsStatic && c.Parameters.Length == 0);
        var ctorInfo = GetConstructorInfo(ctor);

        if (ctor.Parameters.Length == 1)
            ILGenerator.Emit(OpCodes.Ldc_I4, minimumCapacity);

        ILGenerator.Emit(OpCodes.Newobj, ctorInfo);
        var listLocal = ILGenerator.DeclareLocal(ResolveClrType(listType));
        ILGenerator.Emit(OpCodes.Stloc, listLocal);

        var addMethod = listType.GetMembers("Add").OfType<IMethodSymbol>().First();
        var addMethodInfo = GetMethodInfo(addMethod);

        foreach (var element in collectionExpression.Elements)
        {
            if (element is BoundSpreadElement spread)
            {
                EmitSpreadElement(listLocal, spread, elementType, addMethodInfo);
            }
            else
            {
                ILGenerator.Emit(OpCodes.Ldloc, listLocal);
                EmitExpression(element);

                if (element.Type is { } elementRuntimeType &&
                    ShouldBoxForReferenceTarget(elementRuntimeType, elementType))
                {
                    ILGenerator.Emit(OpCodes.Box, ResolveClrType(elementRuntimeType));
                }

                ILGenerator.Emit(OpCodes.Callvirt, addMethodInfo);
            }
        }

        ILGenerator.Emit(OpCodes.Ldloc, listLocal);
        var toArrayMethod = listType.GetMembers("ToArray").OfType<IMethodSymbol>().First();
        var toArrayInfo = GetMethodInfo(toArrayMethod);
        ILGenerator.Emit(OpCodes.Callvirt, toArrayInfo);
    }

    private bool TryEmitArrayWithArraySpreadFastPath(
        IArrayTypeSymbol targetArrayType,
        BoundCollectionExpression collectionExpression)
        => TryEmitArrayWithArraySpreadFastPath(targetArrayType.ElementType, collectionExpression);

    private bool TryEmitArrayWithArraySpreadFastPath(
        ITypeSymbol targetElementType,
        BoundCollectionExpression collectionExpression)
    {
        if (collectionExpression.Elements.Count() == 1 &&
            collectionExpression.Elements.First() is BoundSpreadElement singleSpread &&
            singleSpread.Expression.Type is IArrayTypeSymbol { Rank: 1 } singleSourceArrayType &&
            SymbolEqualityComparer.Default.Equals(singleSourceArrayType.ElementType, targetElementType))
        {
            EmitExpression(singleSpread.Expression);
            return true;
        }

        var spreads = new List<(BoundSpreadElement Spread, IArrayTypeSymbol SourceType)>();
        foreach (var element in collectionExpression.Elements)
        {
            if (element is not BoundSpreadElement spread)
                continue;

            if (spread.Expression.Type is not IArrayTypeSymbol { Rank: 1 } sourceArrayType)
                return false;

            if (!SymbolEqualityComparer.Default.Equals(sourceArrayType.ElementType, targetElementType))
                return false;

            spreads.Add((spread, sourceArrayType));
        }

        if (spreads.Count == 0)
            return false;

        var sourceLocals = new List<IILocal>(spreads.Count);
        var totalLengthLocal = ILGenerator.DeclareLocal(typeof(int));
        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Stloc, totalLengthLocal);

        var spreadIndex = 0;
        foreach (var element in collectionExpression.Elements)
        {
            if (element is BoundSpreadElement spread)
            {
                var sourceArrayType = spreads[spreadIndex].SourceType;
                var sourceLocal = ILGenerator.DeclareLocal(ResolveClrType(sourceArrayType));
                EmitExpression(spread.Expression);
                ILGenerator.Emit(OpCodes.Stloc, sourceLocal);
                sourceLocals.Add(sourceLocal);

                ILGenerator.Emit(OpCodes.Ldloc, totalLengthLocal);
                ILGenerator.Emit(OpCodes.Ldloc, sourceLocal);
                ILGenerator.Emit(OpCodes.Ldlen);
                ILGenerator.Emit(OpCodes.Conv_I4);
                ILGenerator.Emit(OpCodes.Add);
                ILGenerator.Emit(OpCodes.Stloc, totalLengthLocal);
                spreadIndex++;
                continue;
            }

            ILGenerator.Emit(OpCodes.Ldloc, totalLengthLocal);
            ILGenerator.Emit(OpCodes.Ldc_I4_1);
            ILGenerator.Emit(OpCodes.Add);
            ILGenerator.Emit(OpCodes.Stloc, totalLengthLocal);
        }

        var resultLocal = ILGenerator.DeclareLocal(ResolveClrType(Compilation.CreateArrayTypeSymbol(targetElementType)));
        ILGenerator.Emit(OpCodes.Ldloc, totalLengthLocal);
        ILGenerator.Emit(OpCodes.Newarr, ResolveClrType(targetElementType));
        ILGenerator.Emit(OpCodes.Stloc, resultLocal);

        var offsetLocal = ILGenerator.DeclareLocal(typeof(int));
        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Stloc, offsetLocal);

        spreadIndex = 0;
        foreach (var element in collectionExpression.Elements)
        {
            if (element is BoundSpreadElement)
            {
                var sourceLocal = sourceLocals[spreadIndex];

                ILGenerator.Emit(OpCodes.Ldloc, sourceLocal);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Ldloc, resultLocal);
                ILGenerator.Emit(OpCodes.Ldloc, offsetLocal);
                ILGenerator.Emit(OpCodes.Ldloc, sourceLocal);
                ILGenerator.Emit(OpCodes.Ldlen);
                ILGenerator.Emit(OpCodes.Conv_I4);
                ILGenerator.Emit(OpCodes.Call, ArrayCopyMethod);

                ILGenerator.Emit(OpCodes.Ldloc, offsetLocal);
                ILGenerator.Emit(OpCodes.Ldloc, sourceLocal);
                ILGenerator.Emit(OpCodes.Ldlen);
                ILGenerator.Emit(OpCodes.Conv_I4);
                ILGenerator.Emit(OpCodes.Add);
                ILGenerator.Emit(OpCodes.Stloc, offsetLocal);
                spreadIndex++;
                continue;
            }

            ILGenerator.Emit(OpCodes.Ldloc, resultLocal);
            ILGenerator.Emit(OpCodes.Ldloc, offsetLocal);
            EmitExpression(element);
            EmitStoreElement(targetElementType);

            ILGenerator.Emit(OpCodes.Ldloc, offsetLocal);
            ILGenerator.Emit(OpCodes.Ldc_I4_1);
            ILGenerator.Emit(OpCodes.Add);
            ILGenerator.Emit(OpCodes.Stloc, offsetLocal);
        }

        ILGenerator.Emit(OpCodes.Ldloc, resultLocal);
        return true;
    }

    private void EmitSpreadElement(
        IILocal collectionLocal,
        BoundSpreadElement spread,
        ITypeSymbol elementType,
        IMethodSymbol addMethod,
        ITypeSymbol collectionType)
    {
        EmitExpression(spread.Expression);

        if (spread.Expression.Type is { IsValueType: true } spreadType)
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(spreadType));

        var enumerable = (INamedTypeSymbol)Compilation.GetTypeByMetadataName("System.Collections.IEnumerable")!;
        ILGenerator.Emit(OpCodes.Castclass, ResolveClrType(enumerable));
        var getEnumerator = enumerable
            .GetMembers(nameof(IEnumerable.GetEnumerator))
            .OfType<IMethodSymbol>()
            .First();
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(getEnumerator));
        var enumeratorType = getEnumerator.ReturnType;
        var enumeratorLocal = ILGenerator.DeclareLocal(ResolveClrType(enumeratorType));
        ILGenerator.Emit(OpCodes.Stloc, enumeratorLocal);

        var loopStart = ILGenerator.DefineLabel();
        var loopEnd = ILGenerator.DefineLabel();

        ILGenerator.MarkLabel(loopStart);
        var moveNext = enumeratorType.GetMembers(nameof(IEnumerator.MoveNext))!.OfType<IMethodSymbol>().First();
        ILGenerator.Emit(OpCodes.Ldloc, enumeratorLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(moveNext));
        ILGenerator.Emit(OpCodes.Brfalse, loopEnd);

        ILGenerator.Emit(OpCodes.Ldloc, collectionLocal);
        var currentProp = enumeratorType
            .GetMembers(nameof(IEnumerator.Current))
            .OfType<IPropertySymbol>()
            .First()
            .GetMethod!;
        ILGenerator.Emit(OpCodes.Ldloc, enumeratorLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(currentProp));

        var clrElement = ResolveClrType(elementType);
        if (elementType.IsValueType)
            ILGenerator.Emit(OpCodes.Unbox_Any, clrElement);
        else
            ILGenerator.Emit(OpCodes.Castclass, clrElement);

        var paramType = addMethod.Parameters[0].Type;
        if (ShouldBoxForReferenceTarget(elementType, paramType))
            ILGenerator.Emit(OpCodes.Box, clrElement);

        var isInterfaceCall = addMethod.ContainingType?.TypeKind == TypeKind.Interface;

        if (!addMethod.ContainingType!.IsValueType && (addMethod.IsVirtual || isInterfaceCall))
            ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(addMethod));
        else
            ILGenerator.Emit(OpCodes.Call, GetMethodInfo(addMethod));

        EmitCollectionAddResult(collectionLocal, collectionType, addMethod.ReturnType);

        ILGenerator.Emit(OpCodes.Br, loopStart);
        ILGenerator.MarkLabel(loopEnd);
    }

    private void EmitCollectionAddResult(IILocal collectionLocal, ITypeSymbol collectionType, ITypeSymbol returnType)
    {
        if (returnType.SpecialType is SpecialType.System_Void or SpecialType.System_Unit)
            return;

        if (SymbolEqualityComparer.Default.Equals(returnType, collectionType))
        {
            ILGenerator.Emit(OpCodes.Stloc, collectionLocal);
            return;
        }

        var conversion = Compilation.ClassifyConversion(returnType, collectionType, includeUserDefined: false);
        if (conversion.Exists && conversion.IsReference)
        {
            EmitConversion(returnType, collectionType, conversion);
            ILGenerator.Emit(OpCodes.Stloc, collectionLocal);
            return;
        }

        ILGenerator.Emit(OpCodes.Pop);
    }

    private void EmitSpreadElement(IILocal collectionLocal, BoundSpreadElement spread, ITypeSymbol elementType, MethodInfo addMethodInfo)
    {
        EmitExpression(spread.Expression);

        if (spread.Expression.Type is { IsValueType: true } spreadType)
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(spreadType));

        var enumerable = (INamedTypeSymbol)Compilation.GetTypeByMetadataName("System.Collections.IEnumerable")!;
        ILGenerator.Emit(OpCodes.Castclass, ResolveClrType(enumerable));
        var getEnumerator = enumerable
            .GetMembers(nameof(IEnumerable.GetEnumerator))
            .OfType<IMethodSymbol>()
            .First();
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(getEnumerator));
        var enumeratorType = getEnumerator.ReturnType;
        var enumeratorLocal = ILGenerator.DeclareLocal(ResolveClrType(enumeratorType));
        ILGenerator.Emit(OpCodes.Stloc, enumeratorLocal);

        var loopStart = ILGenerator.DefineLabel();
        var loopEnd = ILGenerator.DefineLabel();

        ILGenerator.MarkLabel(loopStart);
        var moveNext = enumeratorType.GetMembers(nameof(IEnumerator.MoveNext))!.OfType<IMethodSymbol>().First();
        ILGenerator.Emit(OpCodes.Ldloc, enumeratorLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(moveNext));
        ILGenerator.Emit(OpCodes.Brfalse, loopEnd);

        ILGenerator.Emit(OpCodes.Ldloc, collectionLocal);
        var currentProp = enumeratorType
            .GetMembers(nameof(IEnumerator.Current))
            .OfType<IPropertySymbol>()
            .First()
            .GetMethod!;
        ILGenerator.Emit(OpCodes.Ldloc, enumeratorLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(currentProp));

        var clrElement = ResolveClrType(elementType);
        if (elementType.IsValueType)
            ILGenerator.Emit(OpCodes.Unbox_Any, clrElement);
        else
            ILGenerator.Emit(OpCodes.Castclass, clrElement);

        ILGenerator.Emit(OpCodes.Callvirt, addMethodInfo);

        ILGenerator.Emit(OpCodes.Br, loopStart);
        ILGenerator.MarkLabel(loopEnd);
    }

    private void EmitCollectionComprehensionExpression(BoundCollectionComprehensionExpression comprehension)
    {
        var elementType = comprehension.ElementType;
        var listTypeDef = (INamedTypeSymbol)Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!;
        var listType = (INamedTypeSymbol)listTypeDef.Construct(elementType);
        var listCtor = listType.Constructors.FirstOrDefault(c => !c.IsStatic && c.Parameters.Length == 0)
            ?? throw new InvalidOperationException("List<T> must expose a parameterless constructor.");

        ILGenerator.Emit(OpCodes.Newobj, GetConstructorInfo(listCtor));
        var listLocal = ILGenerator.DeclareLocal(ResolveClrType(listType));
        ILGenerator.Emit(OpCodes.Stloc, listLocal);

        var addMethod = listType.GetMembers("Add").OfType<IMethodSymbol>().First();
        var addMethodInfo = GetMethodInfo(addMethod);

        var iterationLocalBuilder = ILGenerator.DeclareLocal(ResolveClrType(comprehension.IterationLocal.Type));
        var scope = new Scope(this);
        scope.AddLocal(comprehension.IterationLocal, iterationLocalBuilder);

        if (comprehension.Source is BoundRangeExpression rangeSource)
        {
            EmitCollectionComprehensionRangeLoop(comprehension, rangeSource, scope, listLocal, addMethodInfo, iterationLocalBuilder);
            ILGenerator.Emit(OpCodes.Ldloc, listLocal);
            var toArrayRangeMethod = listType.GetMembers("ToArray").OfType<IMethodSymbol>().First();
            ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(toArrayRangeMethod));
            return;
        }

        EmitNonGenericEnumerable(comprehension.Source);

        var enumerable = (INamedTypeSymbol)Compilation.GetTypeByMetadataName("System.Collections.IEnumerable")!;
        var getEnumerator = enumerable.GetMembers(nameof(IEnumerable.GetEnumerator)).OfType<IMethodSymbol>().First();
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(getEnumerator));

        var enumeratorType = getEnumerator.ReturnType;
        var enumeratorLocal = ILGenerator.DeclareLocal(ResolveClrType(enumeratorType));
        ILGenerator.Emit(OpCodes.Stloc, enumeratorLocal);

        var loopStart = ILGenerator.DefineLabel();
        var loopNext = ILGenerator.DefineLabel();
        var loopEnd = ILGenerator.DefineLabel();

        ILGenerator.MarkLabel(loopStart);
        var moveNext = enumeratorType.GetMembers(nameof(IEnumerator.MoveNext)).OfType<IMethodSymbol>().First();
        ILGenerator.Emit(OpCodes.Ldloc, enumeratorLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(moveNext));
        ILGenerator.Emit(OpCodes.Brfalse, loopEnd);

        var currentGetter = enumeratorType
            .GetMembers(nameof(IEnumerator.Current))
            .OfType<IPropertySymbol>()
            .First()
            .GetMethod!;
        ILGenerator.Emit(OpCodes.Ldloc, enumeratorLocal);
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(currentGetter));

        var iterationClrType = ResolveClrType(comprehension.IterationLocal.Type);
        if (comprehension.IterationLocal.Type.IsValueType)
            ILGenerator.Emit(OpCodes.Unbox_Any, iterationClrType);
        else
            ILGenerator.Emit(OpCodes.Castclass, iterationClrType);

        ILGenerator.Emit(OpCodes.Stloc, iterationLocalBuilder);

        if (comprehension.Condition is not null)
        {
            new ExpressionGenerator(scope, comprehension.Condition, EmitContext.Value).Emit2();
            ILGenerator.Emit(OpCodes.Brfalse, loopNext);
        }

        ILGenerator.Emit(OpCodes.Ldloc, listLocal);
        new ExpressionGenerator(scope, comprehension.Selector, EmitContext.Value).Emit2();

        if (comprehension.Selector.Type is { } selectorType &&
            ShouldBoxForReferenceTarget(selectorType, elementType))
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(selectorType));

        ILGenerator.Emit(OpCodes.Callvirt, addMethodInfo);

        ILGenerator.MarkLabel(loopNext);
        ILGenerator.Emit(OpCodes.Br, loopStart);
        ILGenerator.MarkLabel(loopEnd);

        ILGenerator.Emit(OpCodes.Ldloc, listLocal);
        var toArrayMethod = listType.GetMembers("ToArray").OfType<IMethodSymbol>().First();
        ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(toArrayMethod));
    }

    private void EmitNonGenericEnumerable(BoundExpression sourceExpression)
    {
        EmitExpression(sourceExpression);

        if (sourceExpression.Type is { IsValueType: true } sourceType)
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(sourceType));

        var enumerable = (INamedTypeSymbol)Compilation.GetTypeByMetadataName("System.Collections.IEnumerable")!;
        ILGenerator.Emit(OpCodes.Castclass, ResolveClrType(enumerable));
    }

    private void EmitCollectionComprehensionRangeLoop(
        BoundCollectionComprehensionExpression comprehension,
        BoundRangeExpression rangeSource,
        Scope scope,
        IILocal listLocal,
        MethodInfo addMethodInfo,
        IILocal iterationLocalBuilder)
    {
        var elementType = comprehension.IterationLocal.Type.UnwrapLiteralType() ?? comprehension.IterationLocal.Type;
        var elementClrType = ResolveClrType(elementType);

        var startExpr = rangeSource.Left?.Value ?? throw new InvalidOperationException("Range comprehension requires a start value.");
        var endExpr = rangeSource.Right?.Value ?? throw new InvalidOperationException("Range comprehension requires an end value.");

        var startLocal = ILGenerator.DeclareLocal(elementClrType);
        new ExpressionGenerator(scope, startExpr, EmitContext.Value).Emit2();
        ILGenerator.Emit(OpCodes.Stloc, startLocal);

        var endLocal = ILGenerator.DeclareLocal(elementClrType);
        new ExpressionGenerator(scope, endExpr, EmitContext.Value).Emit2();
        ILGenerator.Emit(OpCodes.Stloc, endLocal);

        var stepLocal = ILGenerator.DeclareLocal(elementClrType);
        EmitNumericOne(elementType);
        ILGenerator.Emit(OpCodes.Stloc, stepLocal);

        var loopStart = ILGenerator.DefineLabel();
        var loopNext = ILGenerator.DefineLabel();
        var loopEnd = ILGenerator.DefineLabel();
        var positiveStepLabel = ILGenerator.DefineLabel();
        var negativeStepLabel = ILGenerator.DefineLabel();
        var bodyLabel = ILGenerator.DefineLabel();

        ILGenerator.MarkLabel(loopStart);
        EmitBranchIfGreaterThanZero(elementType, stepLocal, positiveStepLabel);
        EmitBranchIfLessThanZero(elementType, stepLocal, negativeStepLabel);
        ILGenerator.Emit(OpCodes.Br, loopEnd);

        ILGenerator.MarkLabel(positiveStepLabel);
        var upperExclusive = rangeSource.IsUpperExclusive;

        EmitRangeLoopBreakCondition(elementType, startLocal, endLocal, loopEnd, positiveStep: true, upperExclusive);
        ILGenerator.Emit(OpCodes.Br, bodyLabel);

        ILGenerator.MarkLabel(negativeStepLabel);
        EmitRangeLoopBreakCondition(elementType, startLocal, endLocal, loopEnd, positiveStep: false, upperExclusive);

        ILGenerator.MarkLabel(bodyLabel);
        ILGenerator.Emit(OpCodes.Ldloc, startLocal);
        ILGenerator.Emit(OpCodes.Stloc, iterationLocalBuilder);

        if (comprehension.Condition is not null)
        {
            new ExpressionGenerator(scope, comprehension.Condition, EmitContext.Value).Emit2();
            ILGenerator.Emit(OpCodes.Brfalse, loopNext);
        }

        ILGenerator.Emit(OpCodes.Ldloc, listLocal);
        new ExpressionGenerator(scope, comprehension.Selector, EmitContext.Value).Emit2();

        if (comprehension.Selector.Type is { } selectorType &&
            ShouldBoxForReferenceTarget(selectorType, comprehension.ElementType))
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(selectorType));

        ILGenerator.Emit(OpCodes.Callvirt, addMethodInfo);

        ILGenerator.MarkLabel(loopNext);
        EmitRangeLoopIncrement(elementType, startLocal, stepLocal);
        ILGenerator.Emit(OpCodes.Br, loopStart);
        ILGenerator.MarkLabel(loopEnd);
    }

    private void EmitRangeLoopBreakCondition(ITypeSymbol elementType, IILocal currentLocal, IILocal endLocal, ILLabel endLabel, bool positiveStep, bool upperExclusive = false)
    {
        var specialType = (elementType.UnwrapLiteralType() ?? elementType).SpecialType;

        if (specialType == SpecialType.System_Decimal)
        {
            ILGenerator.Emit(OpCodes.Ldloc, currentLocal);
            ILGenerator.Emit(OpCodes.Ldloc, endLocal);

            var compareMethod = typeof(decimal).GetMethod(
                nameof(decimal.Compare),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                [typeof(decimal), typeof(decimal)],
                modifiers: null)
                ?? throw new InvalidOperationException("Failed to resolve decimal.Compare(decimal, decimal).");

            ILGenerator.Emit(OpCodes.Call, compareMethod);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            ILGenerator.Emit(
                positiveStep
                    ? (upperExclusive ? OpCodes.Bge : OpCodes.Bgt)
                    : (upperExclusive ? OpCodes.Ble : OpCodes.Blt),
                endLabel);
            return;
        }

        ILGenerator.Emit(OpCodes.Ldloc, currentLocal);
        ILGenerator.Emit(OpCodes.Ldloc, endLocal);
        if (positiveStep)
        {
            ILGenerator.Emit(
                IsUnsignedIntegralType(specialType)
                    ? (upperExclusive ? OpCodes.Bge_Un : OpCodes.Bgt_Un)
                    : (upperExclusive ? OpCodes.Bge : OpCodes.Bgt),
                endLabel);
        }
        else
        {
            ILGenerator.Emit(
                IsUnsignedIntegralType(specialType)
                    ? (upperExclusive ? OpCodes.Ble_Un : OpCodes.Blt_Un)
                    : (upperExclusive ? OpCodes.Ble : OpCodes.Blt),
                endLabel);
        }
    }

    private void EmitBranchIfGreaterThanZero(ITypeSymbol elementType, IILocal valueLocal, ILLabel target)
    {
        var specialType = (elementType.UnwrapLiteralType() ?? elementType).SpecialType;

        if (specialType == SpecialType.System_Decimal)
        {
            EmitDecimalCompareWithZero(valueLocal);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            ILGenerator.Emit(OpCodes.Bgt, target);
            return;
        }

        ILGenerator.Emit(OpCodes.Ldloc, valueLocal);
        EmitNumericZero(elementType);
        ILGenerator.Emit(IsUnsignedIntegralType(specialType) ? OpCodes.Bgt_Un : OpCodes.Bgt, target);
    }

    private void EmitBranchIfLessThanZero(ITypeSymbol elementType, IILocal valueLocal, ILLabel target)
    {
        var specialType = (elementType.UnwrapLiteralType() ?? elementType).SpecialType;

        if (specialType == SpecialType.System_Decimal)
        {
            EmitDecimalCompareWithZero(valueLocal);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            ILGenerator.Emit(OpCodes.Blt, target);
            return;
        }

        ILGenerator.Emit(OpCodes.Ldloc, valueLocal);
        EmitNumericZero(elementType);
        ILGenerator.Emit(IsUnsignedIntegralType(specialType) ? OpCodes.Blt_Un : OpCodes.Blt, target);
    }

    private void EmitDecimalCompareWithZero(IILocal valueLocal)
    {
        ILGenerator.Emit(OpCodes.Ldloc, valueLocal);

        var zeroField = typeof(decimal).GetField(nameof(decimal.Zero), BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Failed to resolve decimal.Zero.");
        ILGenerator.Emit(OpCodes.Ldsfld, zeroField);

        var compareMethod = typeof(decimal).GetMethod(
            nameof(decimal.Compare),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            [typeof(decimal), typeof(decimal)],
            modifiers: null)
            ?? throw new InvalidOperationException("Failed to resolve decimal.Compare(decimal, decimal).");

        ILGenerator.Emit(OpCodes.Call, compareMethod);
    }

    private void EmitRangeLoopIncrement(ITypeSymbol elementType, IILocal currentLocal, IILocal stepLocal)
    {
        var specialType = (elementType.UnwrapLiteralType() ?? elementType).SpecialType;

        if (specialType == SpecialType.System_Decimal)
        {
            ILGenerator.Emit(OpCodes.Ldloc, currentLocal);
            ILGenerator.Emit(OpCodes.Ldloc, stepLocal);

            var addMethod = typeof(decimal).GetMethod(
                nameof(decimal.Add),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                [typeof(decimal), typeof(decimal)],
                modifiers: null)
                ?? throw new InvalidOperationException("Failed to resolve decimal.Add(decimal, decimal).");

            ILGenerator.Emit(OpCodes.Call, addMethod);
            ILGenerator.Emit(OpCodes.Stloc, currentLocal);
            return;
        }

        ILGenerator.Emit(OpCodes.Ldloc, currentLocal);
        ILGenerator.Emit(OpCodes.Ldloc, stepLocal);
        ILGenerator.Emit(OpCodes.Add);

        switch (specialType)
        {
            case SpecialType.System_Byte:
                ILGenerator.Emit(OpCodes.Conv_U1);
                break;
            case SpecialType.System_SByte:
                ILGenerator.Emit(OpCodes.Conv_I1);
                break;
            case SpecialType.System_Int16:
                ILGenerator.Emit(OpCodes.Conv_I2);
                break;
            case SpecialType.System_UInt16:
            case SpecialType.System_Char:
                ILGenerator.Emit(OpCodes.Conv_U2);
                break;
            case SpecialType.System_UInt32:
                ILGenerator.Emit(OpCodes.Conv_U4);
                break;
            case SpecialType.System_Int64:
                ILGenerator.Emit(OpCodes.Conv_I8);
                break;
            case SpecialType.System_UInt64:
                ILGenerator.Emit(OpCodes.Conv_U8);
                break;
        }

        ILGenerator.Emit(OpCodes.Stloc, currentLocal);
    }

    private void EmitNumericZero(ITypeSymbol elementType)
    {
        var specialType = (elementType.UnwrapLiteralType() ?? elementType).SpecialType;
        switch (specialType)
        {
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                ILGenerator.Emit(OpCodes.Ldc_I8, 0L);
                break;
            case SpecialType.System_Single:
                ILGenerator.Emit(OpCodes.Ldc_R4, 0f);
                break;
            case SpecialType.System_Double:
                ILGenerator.Emit(OpCodes.Ldc_R8, 0d);
                break;
            case SpecialType.System_Decimal:
                var zeroField = typeof(decimal).GetField(nameof(decimal.Zero), BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("Failed to resolve decimal.Zero.");
                ILGenerator.Emit(OpCodes.Ldsfld, zeroField);
                break;
            default:
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                break;
        }
    }

    private void EmitNumericOne(ITypeSymbol elementType)
    {
        var specialType = (elementType.UnwrapLiteralType() ?? elementType).SpecialType;
        switch (specialType)
        {
            case SpecialType.System_Int64:
                ILGenerator.Emit(OpCodes.Ldc_I8, 1L);
                break;
            case SpecialType.System_UInt64:
                ILGenerator.Emit(OpCodes.Ldc_I8, 1L);
                ILGenerator.Emit(OpCodes.Conv_U8);
                break;
            case SpecialType.System_Single:
                ILGenerator.Emit(OpCodes.Ldc_R4, 1f);
                break;
            case SpecialType.System_Double:
                ILGenerator.Emit(OpCodes.Ldc_R8, 1d);
                break;
            case SpecialType.System_Decimal:
                ILGenerator.Emit(OpCodes.Ldc_I4_1);
                var decimalCtor = typeof(decimal).GetConstructor([typeof(int)])
                    ?? throw new InvalidOperationException("Failed to resolve decimal(int) constructor.");
                ILGenerator.Emit(OpCodes.Newobj, decimalCtor);
                break;
            default:
                ILGenerator.Emit(OpCodes.Ldc_I4_1);
                break;
        }
    }

    private static bool IsUnsignedIntegralType(SpecialType specialType)
    {
        return specialType is
            SpecialType.System_Byte or
            SpecialType.System_UInt16 or
            SpecialType.System_Char or
            SpecialType.System_UInt32 or
            SpecialType.System_UInt64;
    }

    private void EmitEmptyCollectionExpression(BoundEmptyCollectionExpression emptyCollectionExpression)
    {
        var target = emptyCollectionExpression.Type;

        if (TryGetSpanCollectionElementType(target, out _))
        {
            EmitDefaultValue(target);
            return;
        }

        // If target is IEnumerable<T>, default to emitting an empty T[] since arrays implement IEnumerable<T>
        var ienumerableDef = (INamedTypeSymbol?)Compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
        if (ienumerableDef is not null &&
            target is INamedTypeSymbol named &&
            SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, ienumerableDef))
        {
            var elementType = named.TypeArguments[0];
            var elementClrType = ResolveClrType(elementType);
            ILGenerator.Emit(OpCodes.Call, ArrayEmptyGenericMethod.MakeGenericMethod(elementClrType));
            return;
        }

        if (target is IArrayTypeSymbol arrayTypeSymbol)
        {
            var elementClrType = ResolveClrType(arrayTypeSymbol.ElementType);
            ILGenerator.Emit(OpCodes.Call, ArrayEmptyGenericMethod.MakeGenericMethod(elementClrType));
        }
        else if (target is INamedTypeSymbol namedType)
        {
            if (TryEmitEmptyCollectionExpressionViaImmutableList(namedType))
                return;

            if (TryEmitEmptyCollectionExpressionViaBuilderAttribute(namedType))
                return;

            if (namedType.TypeKind == TypeKind.Interface &&
                TryEmitEmptyCollectionExpressionViaList(namedType))
            {
                return;
            }

            var ctor = namedType.Constructors.FirstOrDefault(c => !c.IsStatic && c.Parameters.Length == 0);
            if (ctor is null)
                throw new NotSupportedException("Collection type requires a parameterless constructor");

            var ctorInfo = GetConstructorInfo(ctor);

            ILGenerator.Emit(OpCodes.Newobj, ctorInfo);
        }
    }

    private bool TryEmitEmptyCollectionExpressionViaImmutableList(INamedTypeSymbol targetType)
    {
        if (!string.Equals(targetType.OriginalDefinition.ToFullyQualifiedMetadataName(), "System.Collections.Immutable.ImmutableList`1", StringComparison.Ordinal))
            return false;

        var immutableListHelpers = (INamedTypeSymbol?)Compilation.GetTypeByMetadataName("System.Collections.Immutable.ImmutableList");
        if (immutableListHelpers is null)
            return false;

        var elementType = targetType.TypeArguments[0];
        var createDefinition = immutableListHelpers
            .GetMembers("Create")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(static method => method.IsStatic && method.TypeParameters.Length == 1 && method.Parameters.Length == 0);

        if (createDefinition is null)
            return false;

        var createMethod = createDefinition.Construct(elementType);
        ILGenerator.Emit(OpCodes.Call, GetMethodInfo(createMethod));
        return true;
    }

    private bool TryEmitEmptyCollectionExpressionViaBuilderAttribute(INamedTypeSymbol targetType)
    {
        Type targetClrType;
        try
        {
            targetClrType = ResolveClrType(targetType);
        }
        catch
        {
            return false;
        }

        System.Reflection.CustomAttributeData? builderAttribute = null;
        try
        {
            builderAttribute = targetClrType
                .GetCustomAttributesData()
                .FirstOrDefault(static attribute =>
                    string.Equals(
                        attribute.AttributeType.FullName,
                        "System.Runtime.CompilerServices.CollectionBuilderAttribute",
                        StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }

        if (builderAttribute is null || builderAttribute.ConstructorArguments.Count < 2)
            return false;

        var builderType = builderAttribute.ConstructorArguments[0].Value as Type;
        var methodName = builderAttribute.ConstructorArguments[1].Value as string;
        if (builderType is null || string.IsNullOrEmpty(methodName))
            return false;

        var genericArguments = targetClrType.IsGenericType
            ? targetClrType.GetGenericArguments()
            : Type.EmptyTypes;

        var method = builderType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .Select(method =>
            {
                if (!method.IsGenericMethodDefinition)
                    return method;

                if (method.GetGenericArguments().Length != genericArguments.Length)
                    return null;

                try
                {
                    return method.MakeGenericMethod(genericArguments);
                }
                catch
                {
                    return null;
                }
            })
            .Where(static method => method is not null)
            .Cast<MethodInfo>()
            .FirstOrDefault(method =>
                method.GetParameters().Length == 0 &&
                targetClrType.IsAssignableFrom(method.ReturnType));

        if (method is null)
            return false;

        ILGenerator.Emit(OpCodes.Call, method);
        return true;
    }

    private bool TryEmitEmptyCollectionExpressionViaList(INamedTypeSymbol targetType)
    {
        if (targetType.Arity != 1)
            return false;

        var listTypeDef = (INamedTypeSymbol?)Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        if (listTypeDef is null)
            return false;

        var elementType = targetType.TypeArguments[0];
        var listType = (INamedTypeSymbol)listTypeDef.Construct(elementType);
        var conversion = Compilation.ClassifyConversion(listType, targetType, includeUserDefined: false);
        if (!conversion.Exists)
            return false;

        var ctor = listType.Constructors.FirstOrDefault(c => !c.IsStatic && c.Parameters.Length == 0)
            ?? throw new NotSupportedException("Collection type requires a parameterless constructor");
        var ctorInfo = GetConstructorInfo(ctor);
        ILGenerator.Emit(OpCodes.Newobj, ctorInfo);
        return true;
    }

    private void EmitArrayAccessExpression(BoundArrayAccessExpression boundArrayAccessExpression, bool receiverAlreadyLoaded = false)
    {
        var arrayType = boundArrayAccessExpression.Receiver.Type as IArrayTypeSymbol;

        if (arrayType is { Rank: > 1 })
        {
            EmitMultidimensionalArrayAccess(boundArrayAccessExpression, arrayType);
            return;
        }

        var requiresLength = RequiresArrayLength(boundArrayAccessExpression);

        IILocal? arrayLocal = null;
        if (requiresLength)
        {
            var receiverInfo = receiverAlreadyLoaded
                ? EmitInfo.ForValue()
                : EmitExpression(boundArrayAccessExpression.Receiver);
            arrayLocal = SpillValueToLocalIfNeeded(ResolveClrType(arrayType), receiverInfo, keepValueOnStack: true);
        }
        else if (!receiverAlreadyLoaded)
        {
            EmitExpression(boundArrayAccessExpression.Receiver);
        }

        EmitArrayIndices(boundArrayAccessExpression, arrayLocal);

        EmitLoadElement(arrayType.ElementType);
    }

    private void EmitArrayElementAddress(BoundArrayAccessExpression arrayAccess)
    {
        if (arrayAccess.Receiver.Type is not IArrayTypeSymbol arrayType)
            throw new NotSupportedException("Cannot take the address of a non-array element access.");

        if (arrayType.Rank > 1)
        {
            EmitMultidimensionalArrayElementAddress(arrayAccess, arrayType);
            return;
        }

        var requiresLength = RequiresArrayLength(arrayAccess);

        IILocal? arrayLocal = null;
        if (requiresLength)
        {
            var receiverInfo = EmitExpression(arrayAccess.Receiver);
            arrayLocal = SpillValueToLocalIfNeeded(ResolveClrType(arrayType), receiverInfo, keepValueOnStack: true);
        }
        else
        {
            EmitExpression(arrayAccess.Receiver);
        }

        EmitArrayIndices(arrayAccess, arrayLocal);

        var elementClrType = ResolveClrType(arrayType.ElementType);
        ILGenerator.Emit(OpCodes.Ldelema, elementClrType);
    }

    private void EmitArrayIndices(BoundArrayAccessExpression arrayAccess, IILocal? arrayLocal)
    {
        foreach (var argument in arrayAccess.Indices)
        {
            if (argument is BoundIndexExpression indexExpression)
            {
                if (indexExpression.IsFromEnd)
                {
                    if (arrayLocal is null)
                        throw new InvalidOperationException("Array local is required for from-end indexing.");

                    ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
                    ILGenerator.Emit(OpCodes.Ldlen);
                    ILGenerator.Emit(OpCodes.Conv_I4);
                    new ExpressionGenerator(this, indexExpression.Value).Emit();
                    ILGenerator.Emit(OpCodes.Sub);
                }
                else
                {
                    new ExpressionGenerator(this, indexExpression.Value).Emit();
                }
            }
            else if (IsSystemIndexType(argument.Type))
            {
                if (arrayLocal is null)
                    throw new InvalidOperationException("Array local is required for System.Index arguments.");

                var indexLocal = ILGenerator.DeclareLocal(typeof(Index));
                EmitExpression(argument);
                ILGenerator.Emit(OpCodes.Stloc, indexLocal);

                ILGenerator.Emit(OpCodes.Ldloca, indexLocal);
                ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
                ILGenerator.Emit(OpCodes.Ldlen);
                ILGenerator.Emit(OpCodes.Conv_I4);
                ILGenerator.Emit(OpCodes.Call, IndexGetOffsetMethod);
            }
            else
            {
                EmitExpression(argument);
            }
        }
    }

    private static bool IsSystemIndexType(ITypeSymbol? type)
        => type is INamedTypeSymbol named && named.ToFullyQualifiedMetadataName() == "System.Index";

    private static bool RequiresArrayLength(BoundArrayAccessExpression arrayAccess)
        => arrayAccess.Indices.Any(argument =>
            argument is BoundIndexExpression { IsFromEnd: true } ||
            IsSystemIndexType(argument.Type));

    private void EmitMultidimensionalArrayAccess(BoundArrayAccessExpression arrayAccess, IArrayTypeSymbol arrayType)
    {
        EmitExpression(arrayAccess.Receiver);
        EmitMultidimensionalArrayIndices(arrayAccess);

        var getMethod = ResolveMultidimensionalArrayMethod(arrayType, "Get", includeValue: false);
        ILGenerator.Emit(OpCodes.Call, getMethod);
    }

    private void EmitMultidimensionalArrayElementAddress(BoundArrayAccessExpression arrayAccess, IArrayTypeSymbol arrayType)
    {
        EmitExpression(arrayAccess.Receiver);
        EmitMultidimensionalArrayIndices(arrayAccess);

        var addressMethod = ResolveMultidimensionalArrayMethod(arrayType, "Address", includeValue: false);
        ILGenerator.Emit(OpCodes.Call, addressMethod);
    }

    private void EmitMultidimensionalArrayAssignment(BoundArrayAssignmentExpression array, IArrayTypeSymbol arrayType)
    {
        EmitExpression(array.Left.Receiver);
        EmitMultidimensionalArrayIndices(array.Left);
        EmitRequiredValue(array.Right);

        var setMethod = ResolveMultidimensionalArrayMethod(arrayType, "Set", includeValue: true);
        ILGenerator.Emit(OpCodes.Call, setMethod);
    }

    private void EmitMultidimensionalArrayIndices(BoundArrayAccessExpression arrayAccess)
    {
        foreach (var argument in arrayAccess.Indices)
        {
            if (argument is BoundIndexExpression)
                throw new NotSupportedException("Multidimensional arrays do not support System.Index operands.");

            EmitExpression(argument);
        }
    }

    private MethodInfo ResolveMultidimensionalArrayMethod(IArrayTypeSymbol arrayType, string name, bool includeValue)
    {
        var parameterCount = arrayType.Rank + (includeValue ? 1 : 0);
        var parameterTypes = new Type[parameterCount];

        for (var i = 0; i < arrayType.Rank; i++)
            parameterTypes[i] = typeof(int);

        if (includeValue)
            parameterTypes[^1] = ResolveClrType(arrayType.ElementType);

        var runtimeArrayType = ResolveClrType(arrayType);
        return runtimeArrayType.GetMethod(name, parameterTypes)
               ?? throw new NotSupportedException($"Unable to resolve multidimensional array method '{name}' on '{runtimeArrayType}'.");
    }

    private void EmitIndexerAccessExpression(BoundIndexerAccessExpression boundIndexerAccessExpression, bool receiverAlreadyLoaded = false)
    {
        var indexerProperty = boundIndexerAccessExpression.Symbol as IPropertySymbol;

        if (indexerProperty is null)
            throw new InvalidOperationException("Indexer symbol is not a property.");

        if (!receiverAlreadyLoaded &&
            TryEmitReadOnlySpanIndexerReference(boundIndexerAccessExpression, indexerProperty))
        {
            return;
        }

        EmitIndexerReceiver(boundIndexerAccessExpression.Receiver, indexerProperty, receiverAlreadyLoaded);

        foreach (var argument in boundIndexerAccessExpression.Arguments)
        {
            EmitExpression(argument);
        }

        if (indexerProperty.GetMethod is null)
            throw new InvalidOperationException("Indexer does not have a getter");

        var getter = GetMethodInfo(indexerProperty.GetMethod);
        var callOpCode = indexerProperty.IsStatic || indexerProperty.ContainingType!.IsValueType
            ? OpCodes.Call
            : OpCodes.Callvirt;

        ILGenerator.Emit(callOpCode, getter);
    }

    private bool TryEmitReadOnlySpanIndexerReference(
        BoundIndexerAccessExpression indexerAccess,
        IPropertySymbol indexerProperty)
    {
        if (indexerProperty.ContainingType is not
            {
                Name: "ReadOnlySpan",
                TypeArguments.Length: 1,
                ContainingNamespace: { } containingNamespace
            } readOnlySpanType ||
            containingNamespace.ToDisplayString() != "System" ||
            indexerAccess.Arguments is not { } arguments ||
            arguments.Count() != 1)
        {
            return false;
        }

        var index = arguments.Single();
        if (index.Type?.SpecialType != SpecialType.System_Int32)
            return false;

        var spanClrType = ResolveClrType(readOnlySpanType);
        var elementType = readOnlySpanType.TypeArguments[0];
        var elementClrType = ResolveClrType(elementType);
        var spanLocal = ILGenerator.DeclareLocal(spanClrType);
        var indexLocal = ILGenerator.DeclareLocal(typeof(int));

        EmitExpression(indexerAccess.Receiver);
        ILGenerator.Emit(OpCodes.Stloc, spanLocal);
        EmitExpression(index);
        ILGenerator.Emit(OpCodes.Stloc, indexLocal);

        var outOfRange = ILGenerator.DefineLabel();
        var inRange = ILGenerator.DefineLabel();
        ILGenerator.Emit(OpCodes.Ldloc, indexLocal);
        ILGenerator.Emit(OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Blt, outOfRange);
        ILGenerator.Emit(OpCodes.Ldloc, indexLocal);
        ILGenerator.Emit(OpCodes.Ldloca, spanLocal);
        var lengthGetter = spanClrType.GetProperty("Length")?.GetMethod
            ?? throw new InvalidOperationException($"Missing Length getter on '{spanClrType}'.");
        ILGenerator.Emit(OpCodes.Call, lengthGetter);
        ILGenerator.Emit(OpCodes.Blt, inRange);
        ILGenerator.MarkLabel(outOfRange);
        var exceptionConstructor = typeof(IndexOutOfRangeException).GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException("Missing IndexOutOfRangeException constructor.");
        ILGenerator.Emit(OpCodes.Newobj, exceptionConstructor);
        ILGenerator.Emit(OpCodes.Throw);

        ILGenerator.MarkLabel(inRange);
        ILGenerator.Emit(OpCodes.Ldloc, spanLocal);
        var getReference = typeof(System.Runtime.InteropServices.MemoryMarshal)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
                method.Name == "GetReference" &&
                method.IsGenericMethodDefinition &&
                method.GetParameters() is [{ ParameterType: { IsGenericType: true } parameterType }] &&
                parameterType.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>))
            .MakeGenericMethod(elementClrType);
        ILGenerator.Emit(OpCodes.Call, getReference);
        ILGenerator.Emit(OpCodes.Ldloc, indexLocal);
        ILGenerator.Emit(OpCodes.Conv_I);
        ILGenerator.Emit(OpCodes.Sizeof, elementClrType);
        ILGenerator.Emit(OpCodes.Mul);
        ILGenerator.Emit(OpCodes.Add);
        return true;
    }

    private void EmitLoadElement(ITypeSymbol elementType)
    {
        var clrType = ResolveClrType(elementType);

        if (!elementType.IsValueType)
        {
            ILGenerator.Emit(OpCodes.Ldelem_Ref);
            return;
        }

        // Match the logic in EmitStoreElement – use specialized opcodes for primitives
        // and ldelem.any (OpCodes.Ldelem with type) for all other structs.
        switch (elementType.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
                ILGenerator.Emit(OpCodes.Ldelem_I1);
                return;

            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Char:
                ILGenerator.Emit(OpCodes.Ldelem_I2);
                return;

            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
                ILGenerator.Emit(OpCodes.Ldelem_I4);
                return;

            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                ILGenerator.Emit(OpCodes.Ldelem_I8);
                return;

            case SpecialType.System_Single:
                ILGenerator.Emit(OpCodes.Ldelem_R4);
                return;

            case SpecialType.System_Double:
                ILGenerator.Emit(OpCodes.Ldelem_R8);
                return;

            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
                ILGenerator.Emit(OpCodes.Ldelem_I);
                return;

            default:
                // Non‑primitive struct → ldelem.any <T>
                ILGenerator.Emit(OpCodes.Ldelem, clrType);
                return;
        }
    }

    private void EmitObjectCreationExpression(BoundObjectCreationExpression objectCreationExpression)
    {
        var symbol = objectCreationExpression.Symbol;

        IMethodSymbol constructorSymbol = symbol switch
        {
            SourceMethodSymbol sm => sm,
            PEMethodSymbol a => a,
            SubstitutedMethodSymbol b => b,
            ConstructedMethodSymbol c => c,
            _ => throw new Exception("Unsupported constructor symbol")
        };

        if (constructorSymbol is not SubstitutedMethodSymbol and not ConstructedMethodSymbol &&
            objectCreationExpression.Type is ConstructedNamedTypeSymbol constructedType &&
            constructorSymbol.ContainingType is INamedTypeSymbol containingType &&
            !SymbolEqualityComparer.Default.Equals(containingType, constructedType))
        {
            constructorSymbol = new SubstitutedMethodSymbol(constructorSymbol, constructedType);
        }

        var parameters = constructorSymbol.Parameters.ToArray();
        var arguments = objectCreationExpression.Arguments.ToArray();
        var omittedImplicitUnitArgument =
            arguments.Length == 0 &&
            parameters.Length == 1 &&
            parameters[0].RefKind == RefKind.None &&
            parameters[0].Type.SpecialType == SpecialType.System_Unit;

        if (objectCreationExpression.Receiver is not null)
        {
            EmitExpression(objectCreationExpression.Receiver);
        }

        if (omittedImplicitUnitArgument)
        {
            EmitUnitValue();
        }

        for (int i = 0; i < arguments.Length; i++)
        {
            var param = parameters[i];
            var argument = arguments[i];

            if (param.RefKind is RefKind.Ref or RefKind.Out or RefKind.In)
            {
                switch (argument)
                {
                    case BoundAddressOfExpression addr:
                        EmitAddressOfExpression(addr);
                        break;

                    default:
                        throw new NotSupportedException("Invalid argument for ref/out constructor parameter");
                }
            }
            else
            {
                EmitExpression(argument);

                var argType = argument.Type;
                if (argType is { } argumentType &&
                    ShouldBoxForReferenceTarget(argumentType, param.Type))
                {
                    ILGenerator.Emit(OpCodes.Box, ResolveClrType(argumentType));
                }
            }
        }

        var constructorInfo = GetConstructorInfo(constructorSymbol);
        constructorInfo = CloseConstructorForObjectCreation(constructorInfo, constructorSymbol, objectCreationExpression.Type);
        if (CodeGenFlags.PrintDebug)
        {
            PrintDebug(
                $"[CodeGen:CtorEmit] {constructorSymbol} target={objectCreationExpression.Type} -> " +
                $"{constructorInfo.DeclaringType?.FullName}::.ctor");
        }

        if (objectCreationExpression.Receiver is not null)
        {
            ILGenerator.Emit(OpCodes.Call, constructorInfo);
        }
        else
        {
            ILGenerator.Emit(OpCodes.Newobj, constructorInfo);
        }
    }

    private ConstructorInfo CloseConstructorForObjectCreation(
        ConstructorInfo constructorInfo,
        IMethodSymbol constructorSymbol,
        ITypeSymbol? targetType)
    {
        if (targetType is null)
            return constructorInfo;

        var targetRuntimeType = Generator.InstantiateType(ResolveClrType(targetType));
        var declaringType = constructorInfo.DeclaringType;
        if (declaringType is null)
            return constructorInfo;

        if (constructorSymbol.OriginalDefinition is SourceMethodSymbol sourceConstructor &&
            GetMemberBuilder(sourceConstructor) is ConstructorInfo definitionConstructor)
        {
            try
            {
                var projected = TypeBuilder.GetConstructor(targetRuntimeType, definitionConstructor);
                if (projected is not null)
                    return projected;
            }
            catch
            {
                // Fall through to other projection strategies.
            }
        }

        if (ReferenceEquals(targetRuntimeType, declaringType))
            return constructorInfo;

        // Reflection.Emit generic instantiations need constructor remapping through TypeBuilder APIs.
        if (declaringType is TypeBuilder && targetRuntimeType.GetType().FullName == "System.Reflection.Emit.TypeBuilderInstantiation")
        {
            var mapped = TypeBuilder.GetConstructor(targetRuntimeType, constructorInfo);
            if (mapped is not null)
                return mapped;
        }

        var reboundBySignature = MethodSymbolCodeGenResolver.TryResolveConstructorBySymbolSignatureForType(
            constructorSymbol,
            targetRuntimeType,
            MethodGenerator.TypeGenerator.CodeGen);
        if (reboundBySignature is not null)
            return reboundBySignature;

        var parameterTypes = constructorSymbol.Parameters
            .Select(parameter => ResolveClrType(parameter.Type))
            .ToArray();

        var resolved = TryResolveRuntimeConstructor(targetRuntimeType, parameterTypes);
        return resolved ?? constructorInfo;
    }

    private ConstructorInfo? TryResolveRuntimeConstructor(Type targetRuntimeType, Type[] parameterTypes)
    {
        try
        {
            return targetRuntimeType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: parameterTypes,
                modifiers: null);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private void EmitAssignmentExpression(BoundAssignmentExpression node)
        => EmitAssignmentExpression(node, preserveResult: true);

    private void EmitAssignmentExpression(BoundAssignmentExpression assignmentExpression, bool preserveResult)
    {
        // Fast-path: pointer member field assignment `x->A = value`.
        // Must store through the field address, not mutate a spilled struct copy.
        if (assignmentExpression.Left is BoundPointerMemberAccessExpression pma &&
            pma.Member is IFieldSymbol field)
        {
            // Push address of the field (will go through TryEmitAddress)
            EmitExpression(pma, emitAddress: true);

            // Push RHS value
            EmitExpression(assignmentExpression.Right);

            // If assignment returns a value, keep a copy on the stack
            if (_preserveResult)
                ILGenerator.Emit(OpCodes.Dup);

            // Store through the address (stind.* / stobj)
            EmitStoreIndirect(field.Type);
            return;
        }

        switch (assignmentExpression)
        {
            case BoundLocalAssignmentExpression localAssignmentExpression:
                if (localAssignmentExpression.Local is SubmissionResultSymbol submissionResult)
                {
                    EmitRequiredValue(localAssignmentExpression.Right);
                    _ = SubmissionCodeGenerator.TryEmitResultStore(
                        this,
                        submissionResult,
                        submissionResult.Type);
                    break;
                }

                if (localAssignmentExpression.Local is SubmissionVariableSymbol submissionVariable)
                {
                    EmitSubmissionVariableAssignment(submissionVariable, localAssignmentExpression.Right, preserveResult);
                    break;
                }

                if (TryEmitCapturedAssignment(localAssignmentExpression.Local, localAssignmentExpression.Right))
                    break;

                var localBuilder = GetLocal(localAssignmentExpression.Local);
                if (localBuilder is null)
                    throw new InvalidOperationException($"Missing local builder for '{localAssignmentExpression.Local.Name}'");

                var rightExpression = localAssignmentExpression.Right;
                EmitRequiredValue(rightExpression);

                var resultType = assignmentExpression.Type;
                var needsResult = preserveResult
                    && resultType is not null
                    && resultType.SpecialType is not SpecialType.System_Unit
                    and not SpecialType.System_Void;

                var needsBox = rightExpression.Type is { IsValueType: true }
                    && resultType?.SpecialType == SpecialType.System_Object;

                if (needsBox)
                    ILGenerator.Emit(OpCodes.Box, ResolveClrType(rightExpression.Type));

                if (needsResult)
                    ILGenerator.Emit(OpCodes.Dup);

                ILGenerator.Emit(OpCodes.Stloc, localBuilder);

                _ = SubmissionCodeGenerator.TryEmitCurrentVariableStore(
                    this,
                    localAssignmentExpression.Local,
                    localBuilder);
                break;

            case BoundParameterAssignmentExpression parameterAssignmentExpression:
                EmitParameterAssignmentExpression(parameterAssignmentExpression, preserveResult);
                break;

            case BoundByRefAssignmentExpression refTypeAssignmentExpression:
                EmitByRefAssignmentExpression(refTypeAssignmentExpression, preserveResult);
                break;

            case BoundFieldAssignmentExpression fieldAssignmentExpression:
                {
                    var fieldSymbol = fieldAssignmentExpression.Field;
                    var right = fieldAssignmentExpression.Right
                        ?? new BoundDefaultValueExpression(fieldSymbol.Type);
                    var receiver = fieldAssignmentExpression.Receiver;
                    var requiresAddress = fieldAssignmentExpression.RequiresReceiverAddress;
                    var containingType = fieldSymbol.ContainingType;
                    var needsReceiverAddress = containingType?.IsValueType == true;
                    var needsAddress = requiresAddress || needsReceiverAddress;
                    var fieldResultType = assignmentExpression.Type;
                    var fieldNeedsResult = preserveResult
                        && fieldResultType is not null
                        && fieldResultType.SpecialType is not SpecialType.System_Unit
                        and not SpecialType.System_Void;
                    var fieldNeedsBox = right.Type is { IsValueType: true }
                        && !fieldSymbol.Type.IsValueType;

                    if (right is BoundDefaultValueExpression && fieldSymbol.Type.IsValueType)
                    {
                        if (fieldSymbol.IsStatic)
                        {
                            ILGenerator.Emit(OpCodes.Ldsflda, GetField(fieldSymbol));
                            if (TryGetAsyncInvestigationFieldLabel(fieldSymbol, out var staticDefaultLabel))
                                EmitAsyncInvestigationAddressLogPreservingPointer(staticDefaultLabel);
                        }
                        else
                        {
                            if (!TryEmitValueTypeReceiverAddress(receiver, receiver?.Type, containingType))
                            {
                                if (receiver is not null)
                                {
                                    EmitExpression(receiver);

                                    if (fieldSymbol.ContainingType!.IsValueType)
                                        EmitValueTypeAddressIfNeeded(receiver?.Type, fieldSymbol.ContainingType);

                                    if (needsReceiverAddress)
                                        EmitValueTypeAddressIfNeeded(containingType!);
                                }
                                else
                                {
                                    ILGenerator.Emit(OpCodes.Ldarg_0);

                                    if (needsReceiverAddress)
                                        EmitValueTypeAddressIfNeeded(containingType!);
                                }
                            }

                            ILGenerator.Emit(OpCodes.Ldflda, GetField(fieldSymbol));
                            if (TryGetAsyncInvestigationFieldLabel(fieldSymbol, out var defaultLabel))
                                EmitAsyncInvestigationAddressLogPreservingPointer(defaultLabel);
                        }

                        ILGenerator.Emit(OpCodes.Initobj, ResolveClrType(fieldSymbol.Type));
                        EmitAsyncInvestigationStore(fieldSymbol);
                        break;
                    }

                    var cacheRightValue = !fieldSymbol.IsStatic &&
                        (containingType?.IsValueType == true ||
                         receiver is BoundSelfExpression && BoundNodeFacts.ContainsControlTransfer(right));
                    IILocal? cachedRightLocal = null;

                    if (cacheRightValue)
                    {
                        EmitRequiredValue(right);

                        if (fieldNeedsBox)
                            ILGenerator.Emit(OpCodes.Box, ResolveClrType(right.Type));

                        cachedRightLocal = ILGenerator.DeclareLocal(ResolveClrType(fieldSymbol.Type));
                        ILGenerator.Emit(OpCodes.Stloc, cachedRightLocal);
                    }

                    if (!fieldSymbol.IsStatic)
                    {
                        if (needsAddress)
                        {
                            if (!TryEmitValueTypeReceiverAddress(receiver, receiver?.Type, containingType))
                            {
                                if (receiver is not null)
                                {
                                    EmitExpression(receiver);

                                    if (needsReceiverAddress)
                                        EmitValueTypeAddressIfNeeded(containingType!);
                                }
                                else
                                {
                                    ILGenerator.Emit(OpCodes.Ldarg_0);

                                    if (needsReceiverAddress)
                                        EmitValueTypeAddressIfNeeded(containingType!);
                                }
                            }
                        }
                        else if (receiver is not null)
                        {
                            EmitExpression(receiver);

                            if (fieldSymbol.ContainingType!.IsValueType)
                            {
                                EmitValueTypeAddressIfNeeded(receiver?.Type, fieldSymbol.ContainingType);
                            }
                            if (needsReceiverAddress)
                                EmitValueTypeAddressIfNeeded(containingType!);
                        }
                        else
                        {
                            ILGenerator.Emit(OpCodes.Ldarg_0);

                            if (needsReceiverAddress)
                                EmitValueTypeAddressIfNeeded(containingType!);
                        }
                    }

                    var needsTagConversion = fieldSymbol.Type.SpecialType == SpecialType.System_Byte
                        && UnionFieldUtilities.IsTagFieldName(fieldSymbol.Name);

                    if (!cacheRightValue)
                    {
                        // Emit RHS value
                        EmitRequiredValue(right);

                        // Box if assigning value type to reference type
                        if (fieldNeedsBox)
                        {
                            ILGenerator.Emit(OpCodes.Box, ResolveClrType(right.Type));
                        }

                        if (needsTagConversion)
                        {
                            ILGenerator.Emit(OpCodes.Conv_U1);
                        }

                        if (fieldNeedsResult)
                        {
                            ILGenerator.Emit(OpCodes.Dup);
                        }
                    }
                    else if (cachedRightLocal is not null)
                    {
                        ILGenerator.Emit(OpCodes.Ldloc, cachedRightLocal);

                        if (needsTagConversion)
                        {
                            ILGenerator.Emit(OpCodes.Conv_U1);
                        }
                    }

                    ILGenerator.Emit(fieldSymbol.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, (FieldInfo)GetField(fieldSymbol));
                    EmitAsyncInvestigationStore(fieldSymbol);

                    if (fieldNeedsResult && cacheRightValue && cachedRightLocal is not null)
                    {
                        ILGenerator.Emit(OpCodes.Ldloc, cachedRightLocal);
                    }
                    break;
                }

            case BoundPropertyAssignmentExpression propertyAssignmentExpression:
                {
                    var propertySymbol = (IPropertySymbol)propertyAssignmentExpression.Property;
                    var right = propertyAssignmentExpression.Right;
                    var receiver = propertyAssignmentExpression.Receiver;
                    var isExtensionProperty = propertySymbol.IsExtensionProperty;

                    // Load receiver (unless static)
                    if (isExtensionProperty)
                    {
                        if (receiver is null)
                            throw new InvalidOperationException($"Extension property '{propertySymbol.Name}' requires a receiver.");

                        EmitExpression(receiver);
                    }
                    else if (!propertySymbol.IsStatic)
                    {
                        if (receiver is not null)
                        {
                            EmitExpression(receiver);

                            if (propertySymbol.ContainingType!.IsValueType)
                                EmitValueTypeAddressIfNeeded(receiver?.Type, propertySymbol.ContainingType);
                        }
                        else
                        {
                            ILGenerator.Emit(OpCodes.Ldarg_0);
                            if (propertySymbol.ContainingType!.IsValueType)
                                EmitValueTypeAddressIfNeeded(receiver?.Type, propertySymbol.ContainingType);
                        }
                    }

                    // Emit RHS value
                    EmitRequiredValue(right);

                    // Box if assigning value type to reference type
                    if (right.Type is { } rightType &&
                        ShouldBoxForReferenceTarget(rightType, propertySymbol.Type))
                    {
                        ILGenerator.Emit(OpCodes.Box, ResolveClrType(rightType));
                    }

                    // Resolve setter
                    if (propertySymbol.SetMethod is null)
                        throw new InvalidOperationException($"Property {propertySymbol.Name} does not have a setter");

                    var setter = GetMethodInfo(propertySymbol.SetMethod);

                    if (isExtensionProperty)
                        ILGenerator.Emit(OpCodes.Call, setter);
                    else
                        ILGenerator.Emit(propertySymbol.IsStatic ? OpCodes.Call : OpCodes.Callvirt, setter);
                    break;
                }

            case BoundArrayAssignmentExpression array:
                if (array.Left.Receiver.Type is not IArrayTypeSymbol arrayType)
                    throw new InvalidOperationException("Array assignment requires an array receiver.");

                if (arrayType.Rank > 1)
                {
                    EmitMultidimensionalArrayAssignment(array, arrayType);
                    break;
                }

                var requiresLength = RequiresArrayLength(array.Left);
                IILocal? arrayLocal = null;
                if (requiresLength)
                {
                    var receiverInfo = EmitExpression(array.Left.Receiver);
                    arrayLocal = SpillValueToLocalIfNeeded(ResolveClrType(arrayType), receiverInfo, keepValueOnStack: true);
                }
                else
                {
                    EmitExpression(array.Left.Receiver);
                }

                EmitArrayIndices(array.Left, arrayLocal);

                EmitRequiredValue(array.Right);

                EmitStoreElement(arrayType.ElementType);
                break;

            case BoundIndexerAssignmentExpression indexer:
                var indexerProperty = (IPropertySymbol)indexer.Left.Symbol!;
                EmitIndexerReceiver(indexer.Left.Receiver, indexerProperty);

                foreach (var arg in indexer.Left.Arguments)
                    EmitExpression(arg);

                EmitRequiredValue(indexer.Right);

                if (indexerProperty.SetMethod is null)
                    throw new InvalidOperationException("Indexer does not have a setter");

                var setter2 = GetMethodInfo(indexerProperty.SetMethod);
                var setCallOpCode = indexerProperty.IsStatic || indexerProperty.ContainingType!.IsValueType
                    ? OpCodes.Call
                    : OpCodes.Callvirt;

                ILGenerator.Emit(setCallOpCode, setter2);
                break;

            case BoundPatternAssignmentExpression patternAssignmentExpression:
                EmitPatternAssignmentExpression(patternAssignmentExpression);
                break;

            default:
                throw new NotSupportedException($"Unknown BoundAssignmentExpression: {assignmentExpression.GetType().Name}");
        }

        if (preserveResult && assignmentExpression.Type?.SpecialType == SpecialType.System_Unit)
            EmitUnitValue();
    }

    private void EmitSubmissionVariableAssignment(
        SubmissionVariableSymbol variable,
        BoundExpression value,
        bool preserveResult)
    {
        EmitRequiredValue(value);

        if (value.Type is not null &&
            !SymbolEqualityComparer.Default.Equals(value.Type, variable.Type))
        {
            var conversion = Compilation.ClassifyConversion(value.Type, variable.Type);
            if (conversion.Exists && !conversion.IsIdentity)
                EmitConversion(value.Type, variable.Type, conversion);
        }

        var temporary = ILGenerator.DeclareLocal(ResolveClrType(variable.Type));
        ILGenerator.Emit(OpCodes.Stloc, temporary);
        SubmissionCodeGenerator.EmitVariableStore(this, variable, temporary);

        if (preserveResult &&
            variable.Type.SpecialType is not (SpecialType.System_Unit or SpecialType.System_Void))
        {
            ILGenerator.Emit(OpCodes.Ldloc, temporary);
        }
    }

    private void EmitParameterAssignmentExpression(BoundParameterAssignmentExpression node, bool preserveResult)
    {
        var rightExpression = node.Right;
        EmitRequiredValue(rightExpression);

        var resultType = node.Type;
        var needsResult = preserveResult
            && resultType is not null
            && resultType.SpecialType is not SpecialType.System_Unit
            and not SpecialType.System_Void;

        var needsBox = rightExpression.Type is { IsValueType: true }
            && resultType?.SpecialType == SpecialType.System_Object;

        if (needsBox)
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(rightExpression.Type));

        if (needsResult)
            ILGenerator.Emit(OpCodes.Dup);

        var parameterBuilder = MethodGenerator.GetParameterBuilder(node.Parameter);
        var argumentIndex = parameterBuilder.Position;
        if (MethodSymbol.IsStatic)
            argumentIndex -= 1;

        ILGenerator.Emit(OpCodes.Starg, (short)argumentIndex);
    }

    private void EmitByRefAssignmentExpression(BoundByRefAssignmentExpression node, bool preserveResult)
    {
        var reference = node.Reference;
        var rightExpression = node.Right;
        var elementType = node.ElementType;
        var resultType = node.Type;

        var needsResult = preserveResult
            && resultType is not null
            && resultType.SpecialType is not SpecialType.System_Unit
            and not SpecialType.System_Void;

        var needsBox = rightExpression.Type is { IsValueType: true }
            && resultType?.SpecialType == SpecialType.System_Object;

        EmitExpression(reference, emitAddress: reference is BoundParameterAccess);
        EmitRequiredValue(rightExpression);

        IILocal? tempLocal = null;
        ITypeSymbol? tempStorageType = null;

        if (needsResult)
        {
            tempStorageType = rightExpression.Type ?? elementType;
            var tempClrType = ResolveClrType(tempStorageType);
            tempLocal = ILGenerator.DeclareLocal(tempClrType);
            ILGenerator.Emit(OpCodes.Dup);
            ILGenerator.Emit(OpCodes.Stloc, tempLocal);
        }

        EmitStoreIndirect(elementType);

        if (needsResult && tempLocal is not null)
        {
            ILGenerator.Emit(OpCodes.Ldloc, tempLocal);

            if (needsBox && tempStorageType is not null)
                ILGenerator.Emit(OpCodes.Box, ResolveClrType(tempStorageType));
        }
    }

    private void EmitLoadIndirect(ITypeSymbol elementType)
    {
        switch (elementType.SpecialType)
        {
            case SpecialType.System_SByte:
                ILGenerator.Emit(OpCodes.Ldind_I1);
                break;
            case SpecialType.System_Byte:
            case SpecialType.System_Boolean:
                ILGenerator.Emit(OpCodes.Ldind_U1);
                break;
            case SpecialType.System_Int16:
                ILGenerator.Emit(OpCodes.Ldind_I2);
                break;
            case SpecialType.System_UInt16:
            case SpecialType.System_Char:
                ILGenerator.Emit(OpCodes.Ldind_U2);
                break;
            case SpecialType.System_Int32:
                ILGenerator.Emit(OpCodes.Ldind_I4);
                break;
            case SpecialType.System_UInt32:
                ILGenerator.Emit(OpCodes.Ldind_U4);
                break;
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                ILGenerator.Emit(OpCodes.Ldind_I8);
                break;
            case SpecialType.System_Single:
                ILGenerator.Emit(OpCodes.Ldind_R4);
                break;
            case SpecialType.System_Double:
                ILGenerator.Emit(OpCodes.Ldind_R8);
                break;
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
                ILGenerator.Emit(OpCodes.Ldind_I);
                break;
            default:
                if (elementType is ITypeParameterSymbol)
                    ILGenerator.Emit(OpCodes.Ldobj, ResolveClrType(elementType));
                else if (elementType.IsReferenceType || elementType.TypeKind == TypeKind.Pointer)
                    ILGenerator.Emit(OpCodes.Ldind_Ref);
                else
                    ILGenerator.Emit(OpCodes.Ldobj, ResolveClrType(elementType));
                break;
        }
    }

    private void EmitStoreIndirect(ITypeSymbol elementType)
    {
        switch (elementType.SpecialType)
        {
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Boolean:
                ILGenerator.Emit(OpCodes.Stind_I1);
                break;
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Char:
                ILGenerator.Emit(OpCodes.Stind_I2);
                break;
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
                ILGenerator.Emit(OpCodes.Stind_I4);
                break;
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                ILGenerator.Emit(OpCodes.Stind_I8);
                break;
            case SpecialType.System_Single:
                ILGenerator.Emit(OpCodes.Stind_R4);
                break;
            case SpecialType.System_Double:
                ILGenerator.Emit(OpCodes.Stind_R8);
                break;
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
                ILGenerator.Emit(OpCodes.Stind_I);
                break;
            default:
                if (elementType is ITypeParameterSymbol || elementType.IsValueType)
                {
                    ILGenerator.Emit(OpCodes.Stobj, ResolveClrType(elementType));
                }
                else
                {
                    ILGenerator.Emit(OpCodes.Stind_Ref);
                }

                break;
        }
    }

    private void EmitPatternAssignmentExpression(BoundPatternAssignmentExpression node)
    {
        var pattern = node.Pattern;

        if (pattern is null || !pattern.GetDesignators().Any())
        {
            var discardType = GetPatternValueType(node.Right.Type);

            if (discardType is null ||
                discardType.SpecialType is SpecialType.System_Void or SpecialType.System_Unit)
            {
                // Discarding a void/unit expression only needs side effects.
                // Materializing Unit.Value here leaves an unconsumed stack value.
                new ExpressionGenerator(this, node.Right, EmitContext.None).Emit2();
                return;
            }

            // Discarding a non-unit value still needs the produced value so we can pop it.
            EmitRequiredValue(node.Right);
            ILGenerator.Emit(OpCodes.Pop);
            return;
        }

        var valueType = GetPatternValueType(node.Right.Type) ?? GetPatternValueType(node.Pattern.Type);

        if (valueType is null || valueType.TypeKind == TypeKind.Error)
        {
            EmitRequiredValue(node.Right);
            ILGenerator.Emit(OpCodes.Pop);
            return;
        }

        // Reuse an existing local RHS directly to avoid redundant stloc/ldloc temporaries.
        if (node.Right is BoundLocalAccess { Symbol: ILocalSymbol rightLocal } &&
            GetLocal(rightLocal) is { } existingLocal)
        {
            EmitPatternAssignment(pattern, existingLocal, valueType);
            return;
        }

        // Non-discard pattern assignment must materialize RHS for deconstruction/binding.
        EmitRequiredValue(node.Right);

        var valueLocal = ILGenerator.DeclareLocal(ResolveClrType(valueType));
        ILGenerator.Emit(OpCodes.Stloc, valueLocal);

        EmitPatternAssignment(pattern, valueLocal, valueType);
    }

    private void EmitPatternAssignment(BoundPattern pattern, IILocal valueLocal, ITypeSymbol valueType)
    {
        if (pattern is null)
            return;

        switch (pattern)
        {
            case BoundPositionalPattern tuplePattern:
                EmitPositionalPatternAssignment(tuplePattern, valueLocal, valueType);
                break;

            case BoundDictionaryPattern dictionaryPattern:
                EmitDictionaryPatternAssignment(dictionaryPattern, valueLocal, valueType);
                break;

            case BoundDeconstructPattern deconstructPattern:
                EmitDeconstructPatternAssignment(deconstructPattern, valueLocal, valueType);
                break;

            case BoundDeclarationPattern declarationPattern:
                EmitDeclarationPatternAssignment(declarationPattern, valueLocal, valueType);
                break;

            case BoundDiscardPattern:
                break;

            default:
                throw new NotSupportedException($"Unsupported pattern assignment: {pattern.GetType().Name}");
        }
    }

    private void EmitPositionalPatternAssignment(BoundPositionalPattern tuplePattern, IILocal valueLocal, ITypeSymbol valueType)
    {
        var isSequencePattern = tuplePattern.IsSequence;

        if (isSequencePattern &&
            GetPatternValueType(valueType)?.SpecialType == SpecialType.System_String &&
            GetPatternValueType(tuplePattern.Type)?.SpecialType == SpecialType.System_String)
        {
            EmitStringPositionalPatternAssignment(tuplePattern, valueLocal);
            return;
        }

        if (isSequencePattern &&
            GetPatternValueType(valueType) is IArrayTypeSymbol arrayType &&
            GetPatternValueType(tuplePattern.Type) is IArrayTypeSymbol)
        {
            EmitArrayPositionalPatternAssignment(tuplePattern, valueLocal, arrayType.ElementType);
            return;
        }

        if (isSequencePattern &&
            GetPatternValueType(valueType) is { } collectionType &&
            TryGetIndexableCollectionAccess(collectionType, out var access))
        {
            EmitCollectionPositionalPatternAssignment(tuplePattern, valueLocal, collectionType, access.ElementType);
            return;
        }

        if (GetPatternValueType(valueType) is not ITupleTypeSymbol tupleType)
            return;

        var elements = tuplePattern.Elements;
        var tupleElements = tupleType.TupleElements;
        var count = Math.Min(elements.Length, tupleElements.Length);

        for (var i = 0; i < count; i++)
        {
            var elementPattern = elements[i];
            if (elementPattern is null)
                continue;
            if (elementPattern is BoundDiscardPattern)
                continue;

            var tupleElement = tupleElements[i];
            var elementValueType = GetPatternValueType(tupleElement.Type);

            ILGenerator.Emit(OpCodes.Ldloca, valueLocal);
            ILGenerator.Emit(OpCodes.Ldfld, GetField(tupleElement));

            if (elementValueType is null || elementValueType.TypeKind == TypeKind.Error)
            {
                ILGenerator.Emit(OpCodes.Pop);
                continue;
            }

            if (TryEmitDirectPatternStoreFromStack(elementPattern, elementValueType))
                continue;

            var elementLocal = ILGenerator.DeclareLocal(ResolveClrType(elementValueType));
            ILGenerator.Emit(OpCodes.Stloc, elementLocal);

            EmitPatternAssignment(elementPattern, elementLocal, elementValueType);
        }
    }

    private void EmitArrayPositionalPatternAssignment(BoundPositionalPattern pattern, IILocal arrayLocal, ITypeSymbol elementType)
    {
        var lengthLocal = ILGenerator.DeclareLocal(typeof(int));
        ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
        ILGenerator.Emit(OpCodes.Ldlen);
        ILGenerator.Emit(OpCodes.Conv_I4);
        ILGenerator.Emit(OpCodes.Stloc, lengthLocal);

        for (var i = 0; i < pattern.Elements.Length; i++)
        {
            var elementPattern = pattern.Elements[i];
            if (elementPattern is null or BoundDiscardPattern)
                continue;

            if (pattern.ElementKinds[i] == BoundPositionalPattern.SequenceElementKind.RestSegment)
            {
                var restLengthLocal = ILGenerator.DeclareLocal(typeof(int));
                ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
                ILGenerator.Emit(OpCodes.Ldc_I4, GetSequenceFixedWidth(pattern));
                ILGenerator.Emit(OpCodes.Sub);
                ILGenerator.Emit(OpCodes.Stloc, restLengthLocal);

                var restArrayType = Compilation.CreateArrayTypeSymbol(elementType);
                var restArrayLocal = EmitArraySlice(arrayLocal, elementType, GetSequencePrefixWidth(pattern, i), restLengthLocal);
                var targetType = GetSequencePatternInputType(elementPattern, restArrayType);
                var inputLocal = MaterializeSequencePatternInput(restArrayLocal, restArrayType, targetType);
                EmitPatternAssignment(elementPattern, inputLocal, targetType);
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
                EmitPatternAssignment(elementPattern, inputLocal, targetType);
                continue;
            }

            ILGenerator.Emit(OpCodes.Ldloc, arrayLocal);
            EmitLoadIndex(GetSequenceElementStartIndex(pattern, i, lengthLocal));
            EmitLoadElement(elementType);

            var elementValueType = GetPatternValueType(elementType);
            if (elementValueType is null || elementValueType.TypeKind == TypeKind.Error)
            {
                ILGenerator.Emit(OpCodes.Pop);
                continue;
            }

            if (TryEmitDirectPatternStoreFromStack(elementPattern, elementValueType))
                continue;

            var elementLocal = ILGenerator.DeclareLocal(ResolveClrType(elementValueType));
            ILGenerator.Emit(OpCodes.Stloc, elementLocal);
            EmitPatternAssignment(elementPattern, elementLocal, elementValueType);
        }
    }

    private void EmitStringPositionalPatternAssignment(BoundPositionalPattern pattern, IILocal stringLocal)
    {
        var lengthLocal = ILGenerator.DeclareLocal(typeof(int));
        var stringType = Compilation.GetSpecialType(SpecialType.System_String);
        var charType = Compilation.GetSpecialType(SpecialType.System_Char);

        ILGenerator.Emit(OpCodes.Ldloc, stringLocal);
        ILGenerator.Emit(OpCodes.Callvirt, typeof(string).GetProperty(nameof(string.Length))!.GetMethod!);
        ILGenerator.Emit(OpCodes.Stloc, lengthLocal);

        for (var i = 0; i < pattern.Elements.Length; i++)
        {
            var elementPattern = pattern.Elements[i];
            if (elementPattern is null or BoundDiscardPattern)
                continue;

            if (pattern.ElementKinds[i] == BoundPositionalPattern.SequenceElementKind.RestSegment)
            {
                var restLengthLocal = ILGenerator.DeclareLocal(typeof(int));
                ILGenerator.Emit(OpCodes.Ldloc, lengthLocal);
                ILGenerator.Emit(OpCodes.Ldc_I4, GetSequenceFixedWidth(pattern));
                ILGenerator.Emit(OpCodes.Sub);
                ILGenerator.Emit(OpCodes.Stloc, restLengthLocal);

                var restLocal = EmitStringSlice(stringLocal, GetSequencePrefixWidth(pattern, i), restLengthLocal);
                EmitPatternAssignment(elementPattern, restLocal, stringType);
                continue;
            }

            if (pattern.ElementKinds[i] == BoundPositionalPattern.SequenceElementKind.FixedSegment)
            {
                var sliceLocal = EmitStringSlice(
                    stringLocal,
                    GetSequenceElementStartIndex(pattern, i, lengthLocal),
                    pattern.ElementWidths[i]);
                EmitPatternAssignment(elementPattern, sliceLocal, stringType);
                continue;
            }

            ILGenerator.Emit(OpCodes.Ldloc, stringLocal);
            EmitLoadIndex(GetSequenceElementStartIndex(pattern, i, lengthLocal));
            ILGenerator.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Chars")!.GetMethod!);

            if (TryEmitDirectPatternStoreFromStack(elementPattern, charType))
                continue;

            var elementLocal = ILGenerator.DeclareLocal(typeof(char));
            ILGenerator.Emit(OpCodes.Stloc, elementLocal);
            EmitPatternAssignment(elementPattern, elementLocal, charType);
        }
    }

    private void EmitCollectionPositionalPatternAssignment(
        BoundPositionalPattern pattern,
        IILocal valueLocal,
        ITypeSymbol collectionType,
        ITypeSymbol elementType)
    {
        if (!TryGetIndexableCollectionAccess(collectionType, out var access))
            return;

        elementType = access.ElementType;

        IILocal collectionLocal;
        var sameRuntimeCollectionType =
            ResolveClrType(collectionType) == ResolveClrType(access.InterfaceType);
        if (sameRuntimeCollectionType)
        {
            collectionLocal = valueLocal;
        }
        else
        {
            ILGenerator.Emit(OpCodes.Ldloc, valueLocal);
            if (collectionType.IsValueType)
                ILGenerator.Emit(OpCodes.Box, ResolveClrType(collectionType));
            ILGenerator.Emit(OpCodes.Castclass, ResolveClrType(access.InterfaceType));

            collectionLocal = ILGenerator.DeclareLocal(ResolveClrType(access.InterfaceType));
            ILGenerator.Emit(OpCodes.Stloc, collectionLocal);
        }

        var lengthLocal = ILGenerator.DeclareLocal(typeof(int));
        var indexLocal = ILGenerator.DeclareLocal(typeof(int));
        var arrayType = (IArrayTypeSymbol)Compilation.CreateArrayTypeSymbol(elementType);
        var arrayLocal = ILGenerator.DeclareLocal(ResolveClrType(arrayType));

        ILGenerator.Emit(access.InterfaceType.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, collectionLocal);
        ILGenerator.Emit(
            access.InterfaceType.IsValueType ? OpCodes.Call : OpCodes.Callvirt,
            GetMethodInfo(access.CountGetter));
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
        ILGenerator.Emit(access.InterfaceType.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, collectionLocal);
        ILGenerator.Emit(OpCodes.Ldloc, indexLocal);
        ILGenerator.Emit(
            access.InterfaceType.IsValueType ? OpCodes.Call : OpCodes.Callvirt,
            GetMethodInfo(access.IndexerGetter));
        EmitStoreElement(elementType);

        ILGenerator.Emit(OpCodes.Ldloc, indexLocal);
        ILGenerator.Emit(OpCodes.Ldc_I4_1);
        ILGenerator.Emit(OpCodes.Add);
        ILGenerator.Emit(OpCodes.Stloc, indexLocal);
        ILGenerator.Emit(OpCodes.Br, loopStart);

        ILGenerator.MarkLabel(loopDone);
        EmitArrayPositionalPatternAssignment(pattern, arrayLocal, elementType);
    }

    private bool TryGetIndexableCollectionAccess(ITypeSymbol collectionType, out (INamedTypeSymbol InterfaceType, IMethodSymbol CountGetter, IMethodSymbol IndexerGetter, ITypeSymbol ElementType) access)
    {
        access = default;

        if (collectionType is not INamedTypeSymbol namedType)
            return false;

        foreach (var candidate in EnumerateSelfAndInterfaces(namedType))
        {
            if (TryCreateIndexableCollectionAccess(candidate, out access))
            {
                return true;
            }
        }

        return false;

        static IEnumerable<INamedTypeSymbol> EnumerateSelfAndInterfaces(INamedTypeSymbol type)
        {
            if (type.IsValueType)
            {
                foreach (var iface in type.AllInterfaces)
                    yield return iface;

                yield return type;
                yield break;
            }

            yield return type;
            foreach (var iface in type.AllInterfaces)
                yield return iface;
        }

        static bool TryCreateIndexableCollectionAccess(
            INamedTypeSymbol candidate,
            out (INamedTypeSymbol InterfaceType, IMethodSymbol CountGetter, IMethodSymbol IndexerGetter, ITypeSymbol ElementType) collectionAccess)
        {
            collectionAccess = default;

            var countGetter = candidate
                .GetMembers("Length")
                .Concat(candidate.GetMembers("Count"))
                .OfType<IPropertySymbol>()
                .FirstOrDefault(static p =>
                    p.Name is "Length" or "Count" &&
                    p.Parameters.Length == 0 &&
                    p.Type.SpecialType == SpecialType.System_Int32 &&
                    p.GetMethod is { DeclaredAccessibility: Accessibility.Public })
                ?.GetMethod;
            var indexerGetter = candidate
                .GetMembers("Item")
                .OfType<IPropertySymbol>()
                .FirstOrDefault(static p =>
                    p.Name == "Item" &&
                    p.Parameters.Length == 1 &&
                    p.Parameters[0].Type.SpecialType == SpecialType.System_Int32 &&
                    p.GetMethod is { DeclaredAccessibility: Accessibility.Public })
                ?.GetMethod;

            if (countGetter is null || indexerGetter is null)
                return false;

            collectionAccess = (candidate, countGetter, indexerGetter, indexerGetter.ReturnType);
            return true;
        }
    }

    private void EmitDeconstructPatternAssignment(BoundDeconstructPattern deconstructPattern, IILocal valueLocal, ITypeSymbol valueType)
    {
        var receiverType = deconstructPattern.ReceiverType;
        var conversion = Compilation.ClassifyConversion(valueType, receiverType);
        if (!conversion.Exists)
            return;

        var receiverClrType = ResolveClrType(receiverType);
        var receiverLocal = ILGenerator.DeclareLocal(receiverClrType);

        ILGenerator.Emit(OpCodes.Ldloc, valueLocal);
        EmitConversion(valueType, receiverType, conversion);
        ILGenerator.Emit(OpCodes.Stloc, receiverLocal);

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
            var elementPattern = deconstructPattern.Arguments[i];
            if (elementPattern is BoundDiscardPattern)
                continue;

            EmitPatternAssignment(elementPattern, argumentLocals[i], parameters[i + parameterOffset].Type);
        }
    }

    private void EmitDictionaryPatternAssignment(BoundDictionaryPattern pattern, IILocal valueLocal, ITypeSymbol valueType)
    {
        var runtimeValueType = GetPatternValueType(valueType);
        if (runtimeValueType is null || runtimeValueType.TypeKind == TypeKind.Error)
            return;

        var receiverClrType = ResolveClrType(pattern.ReceiverType);
        IILocal receiverLocal;

        if (!runtimeValueType.IsValueType &&
            ClrTypesMatch(ResolveClrType(runtimeValueType), receiverClrType))
        {
            receiverLocal = valueLocal;
        }
        else
        {
            receiverLocal = ILGenerator.DeclareLocal(receiverClrType);
            ILGenerator.Emit(OpCodes.Ldloc, valueLocal);
            if (runtimeValueType.IsValueType)
                ILGenerator.Emit(OpCodes.Box, ResolveClrType(runtimeValueType));
            ILGenerator.Emit(OpCodes.Castclass, receiverClrType);
            ILGenerator.Emit(OpCodes.Stloc, receiverLocal);
        }

        var indexerGetter = pattern.ReceiverType
            .GetMembers("Item")
            .OfType<IPropertySymbol>()
            .FirstOrDefault(static property => property.Parameters.Length == 1)?
            .GetMethod;

        if (indexerGetter is null)
            throw new InvalidOperationException($"Missing dictionary indexer on '{pattern.ReceiverType}'.");

        var indexerMethodInfo = GetMethodInfo(indexerGetter);
        var entryValueClrType = ResolveClrType(pattern.ValueType);

        foreach (var entry in pattern.Entries)
        {
            var elementLocal = ILGenerator.DeclareLocal(entryValueClrType);
            ILGenerator.Emit(OpCodes.Ldloc, receiverLocal);
            EmitExpression(entry.Key, EmitContext.Value);
            ILGenerator.Emit(OpCodes.Callvirt, indexerMethodInfo);
            ILGenerator.Emit(OpCodes.Stloc, elementLocal);
            EmitPatternAssignment(entry.Pattern, elementLocal, pattern.ValueType);
        }
    }

    private void EmitDeclarationPatternAssignment(BoundDeclarationPattern declarationPattern, IILocal valueLocal, ITypeSymbol valueType)
    {
        switch (declarationPattern.Designator)
        {
            case BoundSingleVariableDesignator single:
                EmitStorePatternValue(single.Local, valueLocal, valueType, GetPatternValueType(single.Local.Type));
                break;

            case BoundDiscardDesignator:
                break;

            default:
                throw new NotSupportedException($"Unsupported declaration designator: {declarationPattern.Designator.GetType().Name}");
        }
    }

    private bool TryEmitDirectPatternStoreFromStack(BoundPattern pattern, ITypeSymbol sourceType)
    {
        var normalizedSource = GetPatternValueType(sourceType);
        if (normalizedSource is null || normalizedSource.TypeKind == TypeKind.Error)
            return false;

        if (pattern is BoundDiscardPattern)
        {
            ILGenerator.Emit(OpCodes.Pop);
            return true;
        }

        if (pattern is not BoundDeclarationPattern { Designator: BoundSingleVariableDesignator single })
            return false;

        if (MethodBodyGenerator.TryGetCapturedField(single.Local, out _))
            return false;

        var localBuilder = GetLocal(single.Local);
        if (localBuilder is null)
            return false;

        var normalizedTarget = GetPatternValueType(single.Local.Type);
        if (normalizedTarget is null || normalizedTarget.TypeKind == TypeKind.Error)
            return false;

        if (!SymbolEqualityComparer.Default.Equals(normalizedSource, normalizedTarget))
            return false;

        if (single.Local.Type is not null &&
            single.Local.Type.SpecialType is SpecialType.System_Object)
        {
            return false;
        }

        ILGenerator.Emit(OpCodes.Stloc, localBuilder);
        return true;
    }

    private void EmitStorePatternValue(ILocalSymbol localSymbol, IILocal valueLocal, ITypeSymbol sourceType, ITypeSymbol? targetType)
    {
        var normalizedSource = GetPatternValueType(sourceType);
        var normalizedTarget = GetPatternValueType(targetType);

        if (MethodBodyGenerator.TryGetCapturedField(localSymbol, out var fieldBuilder))
        {
            MethodBodyGenerator.EmitLoadClosure();
            var storedType = LoadValueWithConversion(valueLocal, normalizedSource, normalizedTarget);

            if (storedType is not null && storedType.IsValueType &&
                localSymbol.Type is not null &&
                localSymbol.Type.SpecialType is SpecialType.System_Object)
            {
                ILGenerator.Emit(OpCodes.Box, ResolveClrType(storedType));
            }

            ILGenerator.Emit(OpCodes.Stfld, fieldBuilder);
            return;
        }

        var localBuilder = GetLocal(localSymbol);
        if (localBuilder is null)
            return;

        var finalType = LoadValueWithConversion(valueLocal, normalizedSource, normalizedTarget);

        if (finalType is not null && finalType.IsValueType &&
            localSymbol.Type is not null &&
            localSymbol.Type.SpecialType is SpecialType.System_Object)
        {
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(finalType));
        }

        ILGenerator.Emit(OpCodes.Stloc, localBuilder);
    }

    private ITypeSymbol? LoadValueWithConversion(IILocal valueLocal, ITypeSymbol? sourceType, ITypeSymbol? targetType)
    {
        ILGenerator.Emit(OpCodes.Ldloc, valueLocal);

        if (sourceType is not null)
            sourceType = GetPatternValueType(sourceType);

        if (targetType is not null)
            targetType = GetPatternValueType(targetType);

        if (sourceType is not null && targetType is not null &&
            !SymbolEqualityComparer.Default.Equals(sourceType, targetType))
        {
            if (TryEmitKnownSequenceMaterialization(sourceType, targetType))
                return targetType;

            var conversion = Compilation.ClassifyConversion(sourceType, targetType);
            if (conversion.Exists)
            {
                EmitConversion(sourceType, targetType, conversion);
                return targetType;
            }
        }

        return sourceType ?? targetType;
    }

    private bool TryEmitKnownSequenceMaterialization(ITypeSymbol sourceType, ITypeSymbol targetType)
    {
        if (sourceType is not IArrayTypeSymbol arrayType ||
            targetType is not INamedTypeSymbol namedTargetType ||
            namedTargetType.TypeArguments.Length != 1)
        {
            return false;
        }

        var elementType = namedTargetType.TypeArguments[0];
        if (!SymbolEqualityComparer.Default.Equals(arrayType.ElementType, elementType))
            return false;

        var metadataName = namedTargetType.OriginalDefinition.ToFullyQualifiedMetadataName();

        if (string.Equals(metadataName, "System.Collections.Generic.List`1", StringComparison.Ordinal))
            return TryEmitArrayToListMaterialization(namedTargetType);

        if (string.Equals(metadataName, "System.Collections.Immutable.ImmutableList`1", StringComparison.Ordinal))
            return TryEmitArrayToImmutableListMaterialization(elementType);

        if (string.Equals(metadataName, "System.Collections.Immutable.ImmutableArray`1", StringComparison.Ordinal))
            return TryEmitArrayToImmutableArrayMaterialization(elementType);

        return false;
    }

    private bool TryEmitArrayToListMaterialization(INamedTypeSymbol targetType)
    {
        var enumerableTypeDefinition = Compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1") as INamedTypeSymbol;
        if (enumerableTypeDefinition is null)
            return false;

        var enumerableType = enumerableTypeDefinition.Construct(targetType.TypeArguments[0]);
        var constructor = targetType.Constructors.FirstOrDefault(ctor =>
            !ctor.IsStatic &&
            ctor.Parameters.Length == 1 &&
            SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, enumerableType)) as IMethodSymbol;

        if (constructor is null)
            return false;

        ILGenerator.Emit(OpCodes.Newobj, GetConstructorInfo(constructor));
        return true;
    }

    private bool TryEmitArrayToImmutableListMaterialization(ITypeSymbol elementType)
    {
        var immutableListHelpers = Compilation.GetTypeByMetadataName("System.Collections.Immutable.ImmutableList") as INamedTypeSymbol;
        if (immutableListHelpers is null)
            return false;

        var createRangeDefinition = immutableListHelpers
            .GetMembers("CreateRange")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(static method => method.IsStatic && method.TypeParameters.Length == 1 && method.Parameters.Length == 1);

        if (createRangeDefinition is null)
            return false;

        ILGenerator.Emit(OpCodes.Call, GetMethodInfo(createRangeDefinition.Construct(elementType)));
        return true;
    }

    private bool TryEmitArrayToImmutableArrayMaterialization(ITypeSymbol elementType)
    {
        var immutableArrayHelpers = Compilation.GetTypeByMetadataName("System.Collections.Immutable.ImmutableArray") as INamedTypeSymbol;
        if (immutableArrayHelpers is null)
            return false;

        var createRangeDefinition = immutableArrayHelpers
            .GetMembers("CreateRange")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(static method => method.IsStatic && method.TypeParameters.Length == 1 && method.Parameters.Length == 1);

        if (createRangeDefinition is null)
            return false;

        ILGenerator.Emit(OpCodes.Call, GetMethodInfo(createRangeDefinition.Construct(elementType)));
        return true;
    }

    private static ITypeSymbol? GetPatternValueType(ITypeSymbol? type)
        => type?.UnwrapLiteralType() ?? type;

    private ITypeSymbol GetSequencePatternInputType(BoundPattern pattern, ITypeSymbol fallbackType)
    {
        if (pattern is BoundDeclarationPattern { Designator: BoundSingleVariableDesignator single } &&
            GetPatternValueType(single.Local.Type) is { } localType &&
            localType.TypeKind != TypeKind.Error)
        {
            return localType;
        }

        if (GetPatternValueType(pattern.Type) is { } patternType &&
            patternType.TypeKind != TypeKind.Error)
        {
            return patternType;
        }

        return fallbackType;
    }

    private IILocal MaterializeSequencePatternInput(IILocal arrayLocal, ITypeSymbol arrayType, ITypeSymbol targetType)
    {
        if (SymbolEqualityComparer.Default.Equals(GetPatternValueType(arrayType), GetPatternValueType(targetType)))
            return arrayLocal;

        var materializedLocal = ILGenerator.DeclareLocal(ResolveClrType(targetType));
        var materializedType = LoadValueWithConversion(arrayLocal, arrayType, targetType);
        if (materializedType is null || !SymbolEqualityComparer.Default.Equals(GetPatternValueType(materializedType), GetPatternValueType(targetType)))
        {
            ILGenerator.Emit(OpCodes.Pop);
            return arrayLocal;
        }

        ILGenerator.Emit(OpCodes.Stloc, materializedLocal);
        return materializedLocal;
    }

    private FieldInfo GetField(IFieldSymbol fieldSymbol)
    {
        return fieldSymbol.GetFieldInfo(MethodGenerator.TypeGenerator.CodeGen);
    }

    private void EmitStoreElement(ITypeSymbol elementType)
    {
        if (!elementType.IsValueType)
        {
            ILGenerator.Emit(OpCodes.Stelem_Ref);
            return;
        }

        // For primitive value types we can use the specialized stelem opcodes.
        // For all other structs (e.g. SyntaxTrivia), we must use stelem.any
        // (OpCodes.Stelem with the element type).
        switch (elementType.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
                ILGenerator.Emit(OpCodes.Stelem_I1);
                return;

            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Char:
                ILGenerator.Emit(OpCodes.Stelem_I2);
                return;

            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
                ILGenerator.Emit(OpCodes.Stelem_I4);
                return;

            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                ILGenerator.Emit(OpCodes.Stelem_I8);
                return;

            case SpecialType.System_Single:
                ILGenerator.Emit(OpCodes.Stelem_R4);
                return;

            case SpecialType.System_Double:
                ILGenerator.Emit(OpCodes.Stelem_R8);
                return;

            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
                ILGenerator.Emit(OpCodes.Stelem_I);
                return;

            default:
                // Non-primitive struct: stelem.any <T>
                ILGenerator.Emit(OpCodes.Stelem, ResolveClrType(elementType));
                return;
        }
    }

    private void EmitBinaryExpression(BoundBinaryExpression binaryExpression)
    {
        var op = binaryExpression.Operator;
        var operatorKind = op.OperatorKind & ~(BinaryOperatorKind.Lifted | BinaryOperatorKind.Checked);

        if (operatorKind is BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr)
        {
            EmitShortCircuitLogical(operatorKind, binaryExpression.Left, binaryExpression.Right);
            return;
        }

        if (TryEmitLiftedNullableValueEquality(binaryExpression, operatorKind))
            return;

        if (TryEmitPointerArithmeticBinary(binaryExpression, operatorKind))
            return;

        // Evaluate operands (and ensure they are converted to the operator's expected operand types).
        // Raven's binder may represent operand conversions on the BoundBinaryOperator rather than by
        // injecting explicit BoundConversion nodes into Left/Right.
        EmitPreservedValue(binaryExpression.Left);
        EmitBinaryOperandConversionIfNeeded(binaryExpression.Left.Type, op.LeftType);

        EmitPreservedValue(binaryExpression.Right);
        EmitBinaryOperandConversionIfNeeded(binaryExpression.Right.Type, op.RightType);

        // DECIMAL PATH
        if (IsDecimalBinary(op))
        {
            EmitDecimalBinary(operatorKind);
            return;
        }

        if (op.MethodSymbol is { } operatorMethod)
        {
            if (operatorMethod.IsStatic &&
                operatorMethod.IsAbstract &&
                operatorMethod.ContainingType?.TypeKind == TypeKind.Interface &&
                op.LeftType is ITypeParameterSymbol)
            {
                ILGenerator.Emit(OpCodes.Constrained, ResolveClrType(op.LeftType));
            }

            ILGenerator.Emit(OpCodes.Call, GetMethodInfo(operatorMethod));
            return;
        }

        // Normal primitive path
        switch (operatorKind)
        {
            case BinaryOperatorKind.Addition: ILGenerator.Emit(OpCodes.Add); break;
            case BinaryOperatorKind.Subtraction: ILGenerator.Emit(OpCodes.Sub); break;
            case BinaryOperatorKind.Multiplication: ILGenerator.Emit(OpCodes.Mul); break;
            case BinaryOperatorKind.Division: ILGenerator.Emit(OpCodes.Div); break;
            case BinaryOperatorKind.Modulo: ILGenerator.Emit(OpCodes.Rem); break;
            case BinaryOperatorKind.BitwiseAnd: ILGenerator.Emit(OpCodes.And); break;
            case BinaryOperatorKind.BitwiseOr: ILGenerator.Emit(OpCodes.Or); break;
            case BinaryOperatorKind.BitwiseXor: ILGenerator.Emit(OpCodes.Xor); break;
            case BinaryOperatorKind.ShiftLeft: ILGenerator.Emit(OpCodes.Shl); break;
            case BinaryOperatorKind.ShiftRight: ILGenerator.Emit(OpCodes.Shr); break;
            case BinaryOperatorKind.Equality: ILGenerator.Emit(OpCodes.Ceq); break;
            case BinaryOperatorKind.Inequality:
                ILGenerator.Emit(OpCodes.Ceq);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Ceq);
                break;
            case BinaryOperatorKind.GreaterThan: ILGenerator.Emit(OpCodes.Cgt); break;
            case BinaryOperatorKind.LessThan: ILGenerator.Emit(OpCodes.Clt); break;
            case BinaryOperatorKind.GreaterThanOrEqual:
                ILGenerator.Emit(OpCodes.Clt);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Ceq);
                break;
            case BinaryOperatorKind.LessThanOrEqual:
                ILGenerator.Emit(OpCodes.Cgt);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Ceq);
                break;
            default:
                throw new InvalidOperationException($"Invalid operator kind '{op.OperatorKind}'");
        }
    }

    private bool TryEmitPointerArithmeticBinary(BoundBinaryExpression binaryExpression, BinaryOperatorKind operatorKind)
    {
        if (operatorKind is not (BinaryOperatorKind.Addition or BinaryOperatorKind.Subtraction))
            return false;

        var leftType = binaryExpression.Left.Type;
        var rightType = binaryExpression.Right.Type;

        var leftPointer = leftType as IPointerTypeSymbol;
        var rightPointer = rightType as IPointerTypeSymbol;

        if (leftPointer is null && rightPointer is null)
            return false;

        if (operatorKind == BinaryOperatorKind.Addition && leftPointer is not null && IsIntegralType(rightType))
        {
            EmitExpression(binaryExpression.Left);
            EmitScaledPointerOffset(binaryExpression.Right, leftPointer.PointedAtType);
            ILGenerator.Emit(OpCodes.Add);
            return true;
        }

        if (operatorKind == BinaryOperatorKind.Addition && rightPointer is not null && IsIntegralType(leftType))
        {
            EmitExpression(binaryExpression.Right);
            EmitScaledPointerOffset(binaryExpression.Left, rightPointer.PointedAtType);
            ILGenerator.Emit(OpCodes.Add);
            return true;
        }

        if (operatorKind == BinaryOperatorKind.Subtraction && leftPointer is not null && IsIntegralType(rightType))
        {
            EmitExpression(binaryExpression.Left);
            EmitScaledPointerOffset(binaryExpression.Right, leftPointer.PointedAtType);
            ILGenerator.Emit(OpCodes.Sub);
            return true;
        }

        if (operatorKind == BinaryOperatorKind.Subtraction && leftPointer is not null && rightPointer is not null)
        {
            EmitExpression(binaryExpression.Left);
            EmitExpression(binaryExpression.Right);
            ILGenerator.Emit(OpCodes.Sub);

            ILGenerator.Emit(OpCodes.Sizeof, ResolveClrType(leftPointer.PointedAtType));
            ILGenerator.Emit(OpCodes.Conv_I);
            ILGenerator.Emit(OpCodes.Div);
            return true;
        }

        return false;
    }

    private void EmitScaledPointerOffset(BoundExpression offsetExpression, ITypeSymbol elementType)
    {
        EmitExpression(offsetExpression);
        ILGenerator.Emit(OpCodes.Conv_I);
        ILGenerator.Emit(OpCodes.Sizeof, ResolveClrType(elementType));
        ILGenerator.Emit(OpCodes.Conv_I);
        ILGenerator.Emit(OpCodes.Mul);
    }

    private static bool IsIntegralType(ITypeSymbol? type)
    {
        if (type is null)
            return false;

        if (type is LiteralTypeSymbol literal)
            type = literal.UnderlyingType;

        return type.SpecialType switch
        {
            SpecialType.System_SByte => true,
            SpecialType.System_Byte => true,
            SpecialType.System_Int16 => true,
            SpecialType.System_UInt16 => true,
            SpecialType.System_Int32 => true,
            SpecialType.System_UInt32 => true,
            SpecialType.System_Int64 => true,
            SpecialType.System_UInt64 => true,
            SpecialType.System_Char => true,
            SpecialType.System_IntPtr => true,
            SpecialType.System_UIntPtr => true,
            _ => false,
        };
    }

    private bool TryEmitLiftedNullableValueEquality(BoundBinaryExpression binaryExpression, BinaryOperatorKind operatorKind)
    {
        if (operatorKind is not (BinaryOperatorKind.Equality or BinaryOperatorKind.Inequality))
            return false;

        var leftType = NormalizeRuntimeType(binaryExpression.Left.Type ?? binaryExpression.Operator.LeftType);
        var rightType = NormalizeRuntimeType(binaryExpression.Right.Type ?? binaryExpression.Operator.RightType);

        var leftNullable = leftType as NullableTypeSymbol;
        var rightNullable = rightType as NullableTypeSymbol;

        var leftNullableValue = leftNullable is { UnderlyingType: { IsValueType: true } };
        var rightNullableValue = rightNullable is { UnderlyingType: { IsValueType: true } };

        if (!leftNullableValue && !rightNullableValue)
            return false;

        var compareLeftType = leftNullableValue ? leftNullable!.UnderlyingType : leftType;
        var compareRightType = rightNullableValue ? rightNullable!.UnderlyingType : rightType;

        if (!BoundBinaryOperator.TryLookup(Compilation, SyntaxKind.EqualsEqualsToken, compareLeftType, compareRightType, out var compareOperator))
            return false;

        var compareKind = compareOperator.OperatorKind & ~(BinaryOperatorKind.Lifted | BinaryOperatorKind.Checked);
        if (compareKind is not BinaryOperatorKind.Equality)
            return false;

        var leftClrType = ResolveClrType(leftType);
        var rightClrType = ResolveClrType(rightType);
        var leftLocal = ILGenerator.DeclareLocal(leftClrType);
        var rightLocal = ILGenerator.DeclareLocal(rightClrType);

        EmitExpression(binaryExpression.Left);
        ILGenerator.Emit(OpCodes.Stloc, leftLocal);

        EmitExpression(binaryExpression.Right);
        ILGenerator.Emit(OpCodes.Stloc, rightLocal);

        var isNotEquals = operatorKind == BinaryOperatorKind.Inequality;
        var doneLabel = ILGenerator.DefineLabel();

        if (leftNullableValue && rightNullableValue)
        {
            var hasValueMismatchLabel = ILGenerator.DefineLabel();
            var bothNullLabel = ILGenerator.DefineLabel();

            EmitNullableHasValue(leftLocal, leftClrType);
            EmitNullableHasValue(rightLocal, rightClrType);
            ILGenerator.Emit(OpCodes.Ceq);
            ILGenerator.Emit(OpCodes.Brfalse, hasValueMismatchLabel);

            EmitNullableHasValue(leftLocal, leftClrType);
            ILGenerator.Emit(OpCodes.Brfalse, bothNullLabel);

            EmitComparableValue(leftLocal, leftType, compareOperator.LeftType);
            EmitComparableValue(rightLocal, rightType, compareOperator.RightType);
            EmitEqualityComparison(compareOperator);
            if (isNotEquals)
            {
                ILGenerator.Emit(OpCodes.Ldc_I4_0);
                ILGenerator.Emit(OpCodes.Ceq);
            }

            ILGenerator.Emit(OpCodes.Br, doneLabel);

            ILGenerator.MarkLabel(bothNullLabel);
            ILGenerator.Emit(isNotEquals ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1);
            ILGenerator.Emit(OpCodes.Br, doneLabel);

            ILGenerator.MarkLabel(hasValueMismatchLabel);
            ILGenerator.Emit(isNotEquals ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            ILGenerator.MarkLabel(doneLabel);
            return true;
        }

        var nullCaseLabel = ILGenerator.DefineLabel();

        if (leftNullableValue)
        {
            EmitNullableHasValue(leftLocal, leftClrType);
            ILGenerator.Emit(OpCodes.Brfalse, nullCaseLabel);

            EmitComparableValue(leftLocal, leftType, compareOperator.LeftType);
            EmitComparableValue(rightLocal, rightType, compareOperator.RightType);
            EmitEqualityComparison(compareOperator);
        }
        else
        {
            EmitNullableHasValue(rightLocal, rightClrType);
            ILGenerator.Emit(OpCodes.Brfalse, nullCaseLabel);

            EmitComparableValue(leftLocal, leftType, compareOperator.LeftType);
            EmitComparableValue(rightLocal, rightType, compareOperator.RightType);
            EmitEqualityComparison(compareOperator);
        }

        if (isNotEquals)
        {
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            ILGenerator.Emit(OpCodes.Ceq);
        }

        ILGenerator.Emit(OpCodes.Br, doneLabel);
        ILGenerator.MarkLabel(nullCaseLabel);
        ILGenerator.Emit(isNotEquals ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        ILGenerator.MarkLabel(doneLabel);
        return true;
    }

    private void EmitEqualityComparison(BoundBinaryOperator compareOperator)
    {
        if (compareOperator.MethodSymbol is { } methodSymbol)
        {
            ILGenerator.Emit(OpCodes.Call, GetMethodInfo(methodSymbol));
            return;
        }

        ILGenerator.Emit(OpCodes.Ceq);
    }

    private static ITypeSymbol NormalizeRuntimeType(ITypeSymbol type)
    {
        return type is LiteralTypeSymbol literal ? literal.UnderlyingType : type;
    }

    private void EmitNullableHasValue(IILocal local, Type nullableClrType)
    {
        ILGenerator.Emit(OpCodes.Ldloca, local);
        ILGenerator.Emit(OpCodes.Call, GetNullableHasValueGetter(nullableClrType));
    }

    private void EmitComparableValue(IILocal local, ITypeSymbol runtimeType, ITypeSymbol targetType)
    {
        if (runtimeType is NullableTypeSymbol nullable &&
            nullable.GetNullableAbiProjection() == NullableAbiProjection.NullableValueType)
        {
            ILGenerator.Emit(OpCodes.Ldloca, local);
            ILGenerator.Emit(OpCodes.Call, GetNullableGetValueOrDefault(ResolveClrType(runtimeType)));
            EmitBinaryOperandConversionIfNeeded(nullable.UnderlyingType, targetType);
            return;
        }

        ILGenerator.Emit(OpCodes.Ldloc, local);
        EmitBinaryOperandConversionIfNeeded(runtimeType, targetType);
    }

    private void EmitBinaryOperandConversionIfNeeded(ITypeSymbol? fromType, ITypeSymbol toType)
    {
        if (fromType is null)
            return;

        // Most operators already get operands of the expected type.
        if (SymbolEqualityComparer.Default.Equals(fromType, toType))
            return;

        var conversion = Compilation.ClassifyConversion(fromType, toType);
        if (!conversion.Exists)
            return;

        EmitConversion(fromType, toType, conversion);
    }

    private bool IsDecimalBinary(BoundBinaryOperator op)
    {
        return op.LeftType.TypeKind != TypeKind.Error &&
               op.RightType.TypeKind != TypeKind.Error &&
               op.LeftType.SpecialType == SpecialType.System_Decimal &&
               op.RightType.SpecialType == SpecialType.System_Decimal;
    }

    private void EmitDecimalBinary(BinaryOperatorKind operatorKind)
    {
        // These are all static methods on System.Decimal
        // (operator overloads compiled to op_Addition/op_Subtraction/etc)
        var decimalType = typeof(decimal);

        string name = operatorKind switch
        {
            BinaryOperatorKind.Addition => "op_Addition",
            BinaryOperatorKind.Subtraction => "op_Subtraction",
            BinaryOperatorKind.Multiplication => "op_Multiply",
            BinaryOperatorKind.Division => "op_Division",
            BinaryOperatorKind.Modulo => "op_Modulus",

            BinaryOperatorKind.Equality => "op_Equality",
            BinaryOperatorKind.Inequality => "op_Inequality",
            BinaryOperatorKind.GreaterThan => "op_GreaterThan",
            BinaryOperatorKind.LessThan => "op_LessThan",
            BinaryOperatorKind.GreaterThanOrEqual => "op_GreaterThanOrEqual",
            BinaryOperatorKind.LessThanOrEqual => "op_LessThanOrEqual",

            _ => throw new NotSupportedException($"Decimal operator not supported: {operatorKind}")
        };

        var mi = decimalType.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { decimalType, decimalType },
            modifiers: null);

        if (mi is null)
            throw new InvalidOperationException($"Missing decimal.{name}(decimal, decimal)");

        ILGenerator.Emit(OpCodes.Call, mi);
    }

    private void EmitShortCircuitLogical(
        BinaryOperatorKind operatorKind,
        BoundExpression left,
        BoundExpression right)
    {
        var skipLabel = ILGenerator.DefineLabel();
        var endLabel = ILGenerator.DefineLabel();

        switch (operatorKind)
        {
            case BinaryOperatorKind.LogicalAnd:
                // if (!left) result = false; else result = right;
                EmitPreservedValue(left);                 // stack: [left]
                ILGenerator.Emit(OpCodes.Brfalse, skipLabel);

                // left == true → evaluate right, result = right
                EmitPreservedValue(right);                // stack: [right]
                ILGenerator.Emit(OpCodes.Br, endLabel);

                // left == false → push false
                ILGenerator.MarkLabel(skipLabel);
                ILGenerator.Emit(OpCodes.Ldc_I4_0);

                ILGenerator.MarkLabel(endLabel);
                break;

            case BinaryOperatorKind.LogicalOr:
                // if (left) result = true; else result = right;
                EmitPreservedValue(left);                 // stack: [left]
                ILGenerator.Emit(OpCodes.Brtrue, skipLabel);

                // left == false → evaluate right, result = right
                EmitPreservedValue(right);                // stack: [right]
                ILGenerator.Emit(OpCodes.Br, endLabel);

                // left == true → push true
                ILGenerator.MarkLabel(skipLabel);
                ILGenerator.Emit(OpCodes.Ldc_I4_1);

                ILGenerator.MarkLabel(endLabel);
                break;

            default:
                throw new InvalidOperationException($"Unexpected logical operator kind '{operatorKind}'.");
        }
    }

    private void EmitMemberAccessExpression(BoundMemberAccessExpression memberAccessExpression, bool receiverAlreadyLoaded = false)
    {
        var symbol = memberAccessExpression.Symbol;
        var receiver = memberAccessExpression.Receiver;

        switch (symbol)
        {
            case IPropertySymbol propertySymbol:
                if (propertySymbol.IsExtensionProperty)
                {
                    EmitExtensionPropertyAccess(propertySymbol, receiver, receiverAlreadyLoaded);
                    break;
                }

                EmitPropertyReceiver(receiver, propertySymbol, receiverAlreadyLoaded);

                if (propertySymbol.ContainingType?.SpecialType == SpecialType.System_Array &&
                    propertySymbol.Name == "Length")
                {
                    ILGenerator.Emit(OpCodes.Ldlen);
                    ILGenerator.Emit(OpCodes.Conv_I4);
                    return;
                }

                if (propertySymbol.GetMethod is null)
                    throw new InvalidOperationException($"Property '{propertySymbol.Name}' does not have a getter.");

                MethodInfo getter = GetMethodInfo(propertySymbol.GetMethod);

                if (!propertySymbol.IsStatic && !propertySymbol.ContainingType!.IsValueType)
                    EmitBoxIfNeeded(propertySymbol.ContainingType!, getter);

                ILGenerator.Emit(propertySymbol.IsStatic || propertySymbol.ContainingType!.IsValueType ? OpCodes.Call : OpCodes.Callvirt, getter);

                if (ShouldDiscardCurrentResult(memberAccessExpression))
                    ILGenerator.Emit(OpCodes.Pop);
                break;

            case IFieldSymbol fieldSymbol:
                if (!fieldSymbol.IsStatic &&
                    fieldSymbol.ContainingType is { } memberFieldContainingType &&
                    (!SymbolEqualityComparer.Default.Equals(receiver?.Type, memberFieldContainingType)) &&
                    MethodSymbol.ContainingType is SynthesizedAsyncStateMachineTypeSymbol memberStateMachine &&
                    memberStateMachine.GetConstructedMembers(memberStateMachine.AsyncMethod).ThisField is { } memberClosureField &&
                    memberFieldContainingType.Name.Contains("DisplayClass", StringComparison.Ordinal))
                {
                    ILGenerator.Emit(OpCodes.Ldarg_0);
                    ILGenerator.Emit(OpCodes.Ldfld, GetField(memberClosureField));

                    var memberFieldInfo = fieldSymbol switch
                    {
                        SourceFieldSymbol sfs => (FieldInfo)GetMemberBuilder(sfs)!,
                        _ => fieldSymbol.GetFieldInfo(MethodGenerator.TypeGenerator.CodeGen)
                    };

                    ILGenerator.Emit(OpCodes.Ldfld, memberFieldInfo);
                    break;
                }

                if (!fieldSymbol.IsStatic &&
                    fieldSymbol.ContainingType is { } closureContainingType &&
                    receiver is BoundSelfExpression self &&
                    MethodGenerator.LambdaClosure is not null &&
                    !SymbolEqualityComparer.Default.Equals(self.Type, closureContainingType) &&
                    closureContainingType.Name.Contains("DisplayClass", StringComparison.Ordinal))
                {
                    MethodBodyGenerator.EmitLoadClosure();

                    var closureFieldInfo = fieldSymbol switch
                    {
                        SourceFieldSymbol sfs => (FieldInfo)GetMemberBuilder(sfs)!,
                        _ => fieldSymbol.GetFieldInfo(MethodGenerator.TypeGenerator.CodeGen)
                    };

                    ILGenerator.Emit(OpCodes.Ldfld, closureFieldInfo);
                    break;
                }

                if (!fieldSymbol.IsStatic && fieldSymbol.ContainingType!.IsValueType)
                {
                    var containingType = fieldSymbol.ContainingType!;

                    if (receiverAlreadyLoaded)
                    {
                        EmitValueTypeAddressIfNeeded(receiver?.Type ?? containingType, containingType);
                    }
                    else if (!TryEmitInvocationReceiverAddress(receiver))
                    {
                        if (receiver is not null)
                        {
                            EmitExpression(receiver);
                            EmitValueTypeAddressIfNeeded(receiver.Type, containingType);
                        }
                        else
                        {
                            ILGenerator.Emit(OpCodes.Ldarg_0);
                        }
                    }
                }
                else
                {
                    EmitReceiverIfNeeded(receiver, fieldSymbol, receiverAlreadyLoaded);
                }

                if (fieldSymbol.IsConst)
                {
                    EmitLiteral(fieldSymbol.GetConstantValue());
                }
                else
                {
                    var opCode = fieldSymbol.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld;
                    var fieldInfo = fieldSymbol switch
                    {
                        SourceFieldSymbol sfs => (FieldInfo)GetMemberBuilder(sfs)!,
                        _ => fieldSymbol.GetFieldInfo(MethodGenerator.TypeGenerator.CodeGen)
                    };

                    ILGenerator.Emit(opCode, fieldInfo);
                }

                if (ShouldDiscardCurrentResult(memberAccessExpression))
                    ILGenerator.Emit(OpCodes.Pop);
                break;

            default:
                throw new Exception($"Unsupported member access: {memberAccessExpression}");
        }
    }

    private EmitInfo EmitPointerMemberAccessExpression(BoundPointerMemberAccessExpression expr, EmitContext context)
    {
        // Pointer member access `x->A` is conceptually `(*x).A`.
        // For reads, we can reuse the normal member-access pipeline by binding the receiver as a dereference.
        // For writes, the assignment path should request an address; address emission is handled in TryEmitAddress.

        BoundExpression derefReceiver;
        if (expr.PointerReceiver.Type is IPointerTypeSymbol ptr)
            derefReceiver = new BoundDereferenceExpression(expr.PointerReceiver, ptr.PointedAtType);
        else
            derefReceiver = new BoundDereferenceExpression(expr.PointerReceiver, expr.Member.ContainingType ?? expr.Type);

        // Fast-path for fields (common in unsafe structs) to avoid any accidental copies.
        if (expr.Member is IFieldSymbol field)
        {
            var fieldAccess = new BoundFieldAccess(derefReceiver, field, expr.Reason);
            return EmitFieldAccess(fieldAccess, context);
        }

        // Fallback: reuse the existing member access emission.
        var memberAccess = new BoundMemberAccessExpression(derefReceiver, expr.Member, expr.Reason);
        EmitMemberAccessExpression(memberAccess);
        return EmitInfo.ForValue();
    }

    private void EmitConditionalAccessExpression(BoundConditionalAccessExpression conditional, EmitContext context)
    {
        var receiverType = conditional.Receiver.Type;
        var isNullableValue = receiverType.IsNullable && receiverType.IsValueType;

        var receiverClrType = ResolveClrType(receiverType);
        var local = ILGenerator.DeclareLocal(receiverClrType);

        EmitExpression(conditional.Receiver);
        ILGenerator.Emit(OpCodes.Stloc, local);

        var whenNullLabel = ILGenerator.DefineLabel();
        var endLabel = ILGenerator.DefineLabel();

        if (!_preserveResult)
        {
            if (isNullableValue)
            {
                ILGenerator.Emit(OpCodes.Ldloca, local);
                var hasValue = GetNullableHasValueGetter(receiverClrType);
                ILGenerator.Emit(OpCodes.Call, hasValue);
                ILGenerator.Emit(OpCodes.Brfalse, endLabel);

                ILGenerator.Emit(OpCodes.Ldloca, local);
                var getValueOrDefault = GetNullableGetValueOrDefault(receiverClrType);
                ILGenerator.Emit(OpCodes.Call, getValueOrDefault);
            }
            else
            {
                ILGenerator.Emit(OpCodes.Ldloc, local);
                ILGenerator.Emit(OpCodes.Brfalse, endLabel);
                ILGenerator.Emit(OpCodes.Ldloc, local);
            }

            EmitWhenNotNull(conditional.WhenNotNull);

            if (conditional.WhenNotNull.Type.SpecialType is not SpecialType.System_Void and not SpecialType.System_Unit)
                ILGenerator.Emit(OpCodes.Pop);

            ILGenerator.MarkLabel(endLabel);
            MethodBodyGenerator.EmitDebugNop();
            return;
        }

        if (isNullableValue)
        {
            ILGenerator.Emit(OpCodes.Ldloca, local);
            var hasValue = GetNullableHasValueGetter(receiverClrType);
            ILGenerator.Emit(OpCodes.Call, hasValue);
            ILGenerator.Emit(OpCodes.Brfalse, whenNullLabel);

            ILGenerator.Emit(OpCodes.Ldloca, local);
            var getValueOrDefault = GetNullableGetValueOrDefault(receiverClrType);
            ILGenerator.Emit(OpCodes.Call, getValueOrDefault);
        }
        else
        {
            ILGenerator.Emit(OpCodes.Ldloc, local);
            ILGenerator.Emit(OpCodes.Brfalse, whenNullLabel);
            ILGenerator.Emit(OpCodes.Ldloc, local);
        }

        EmitWhenNotNull(conditional.WhenNotNull);

        if (conditional.Type is NullableTypeSymbol nullableResult &&
            nullableResult.GetNullableAbiProjection() == NullableAbiProjection.NullableValueType)
        {
            var whenNotNullType = conditional.WhenNotNull.Type;

            // normalize: if WhenNotNull is a literal type, unwrap like you do in patterns
            whenNotNullType = whenNotNullType?.UnwrapLiteralType();

            if (whenNotNullType is not null &&
                SymbolEqualityComparer.Default.Equals(whenNotNullType, nullableResult.UnderlyingType))
            {
                var nullableClr = ResolveClrType(conditional.Type);                // Nullable<T>
                var underlyingClr = ResolveClrType(nullableResult.UnderlyingType); // T

                var ctor = nullableClr.GetConstructor(new[] { underlyingClr })
                    ?? throw new InvalidOperationException($"Missing Nullable<{underlyingClr}>.ctor({underlyingClr}).");

                ILGenerator.Emit(OpCodes.Newobj, ctor); // stack: Nullable<T>
            }
        }

        ILGenerator.Emit(OpCodes.Br, endLabel);

        ILGenerator.MarkLabel(whenNullLabel);
        EmitDefaultValue(conditional.Type);

        ILGenerator.MarkLabel(endLabel);
    }

    private void EmitWhenNotNull(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundMemberAccessExpression memberAccess:
                EmitMemberAccessExpression(memberAccess, receiverAlreadyLoaded: true);
                break;
            case BoundInvocationExpression invocation:
                EmitInvocationExpression(invocation, receiverAlreadyLoaded: true);
                break;
            case BoundArrayAccessExpression arrayAccess:
                EmitArrayAccessExpression(arrayAccess, receiverAlreadyLoaded: true);
                break;
            case BoundIndexerAccessExpression indexerAccess:
                EmitIndexerAccessExpression(indexerAccess, receiverAlreadyLoaded: true);
                break;
            default:
                EmitExpression(expression);
                break;
        }
    }

    private void EmitExtensionPropertyAccess(IPropertySymbol propertySymbol, BoundExpression? receiver, bool receiverAlreadyLoaded)
    {
        if (propertySymbol.GetMethod is null)
            throw new InvalidOperationException($"Property '{propertySymbol.Name}' does not have a getter.");

        if (!receiverAlreadyLoaded)
        {
            if (receiver is null)
                throw new InvalidOperationException($"Extension property '{propertySymbol.Name}' requires a receiver.");

            EmitPreservedValue(receiver);
        }

        ILGenerator.Emit(OpCodes.Call, GetMethodInfo(propertySymbol.GetMethod));
    }

    private void EmitReceiverIfNeeded(BoundExpression? receiver, ISymbol symbol, bool receiverAlreadyLoaded)
    {
        if (receiverAlreadyLoaded)
            return;

        if (symbol.IsStatic)
            return;

        if (receiver is not null)
        {
            EmitPreservedValue(receiver);
            return;
        }

        if (MethodSymbol.IsStatic)
            throw new InvalidOperationException($"Instance member '{symbol.Name}' requires a receiver.");

        ILGenerator.Emit(OpCodes.Ldarg_0);
    }

    private void EmitPropertyReceiver(BoundExpression? receiver, IPropertySymbol propertySymbol, bool receiverAlreadyLoaded)
    {
        if (propertySymbol.IsStatic)
            return;

        if (propertySymbol.ContainingType!.IsValueType)
        {
            if (receiverAlreadyLoaded)
            {
                EmitValueTypeAddressIfNeeded(receiver?.Type, propertySymbol.ContainingType);
                return;
            }

            if (TryEmitInvocationReceiverAddress(receiver))
                return;

            if (receiver is null)
                throw new InvalidOperationException($"Instance property '{propertySymbol.Name}' requires a receiver.");

            EmitPreservedValue(receiver);
            EmitValueTypeAddressIfNeeded(receiver.Type, propertySymbol.ContainingType);
            return;
        }

        EmitReceiverIfNeeded(receiver, propertySymbol, receiverAlreadyLoaded);
    }

    private void EmitIndexerReceiver(BoundExpression receiver, IPropertySymbol indexerProperty, bool receiverAlreadyLoaded = false)
    {
        if (indexerProperty.IsStatic)
            return;

        if (indexerProperty.ContainingType!.IsValueType)
        {
            if (receiverAlreadyLoaded)
            {
                EmitValueTypeAddressIfNeeded(receiver.Type, indexerProperty.ContainingType);
                return;
            }

            if (TryEmitInvocationReceiverAddress(receiver))
                return;

            EmitPreservedValue(receiver);
            EmitValueTypeAddressIfNeeded(receiver.Type, indexerProperty.ContainingType);
            return;
        }

        if (!receiverAlreadyLoaded)
            EmitPreservedValue(receiver);
    }

    private void EmitPreservedValue(BoundExpression expression)
    {
        var saved = _preserveResult;
        _preserveResult = true;
        try
        {
            EmitExpression(expression);
        }
        finally
        {
            _preserveResult = saved;
        }
    }

    private bool ShouldDiscardCurrentResult(BoundExpression expression)
        => !_preserveResult && ReferenceEquals(expression, _expression);

    private bool TryEmitValueTypeReceiverAddress(BoundExpression? receiver, ITypeSymbol? runtimeType, ITypeSymbol? declaredType = null)
    {
        var effectiveType = runtimeType ?? declaredType;

        if (effectiveType is null || !effectiveType.IsValueType)
            return TryEmitInvocationReceiverAddress(receiver);

        if (TryEmitInvocationReceiverAddress(receiver))
            return true;

        if (receiver is BoundDereferenceExpression dereference)
        {
            EmitExpression(dereference.Reference);
            return true;
        }

        if (receiver is BoundMemberAccessExpression { Member: IFieldSymbol fieldSymbol } member && !fieldSymbol.IsStatic)
        {
            var containingType = fieldSymbol.ContainingType;

            if (TryEmitValueTypeReceiverAddress(member.Receiver, member.Receiver?.Type, containingType))
            {
                ILGenerator.Emit(OpCodes.Ldflda, GetField(fieldSymbol));
                if (TryGetAsyncInvestigationFieldLabel(fieldSymbol, out var receiverLabel))
                    EmitAsyncInvestigationAddressLogPreservingPointer(receiverLabel);
                return true;
            }
        }

        return false;
    }

    private bool TryEmitInvocationReceiverAddress(BoundExpression? receiver)
    {
        var requiresAddress = MethodSymbol.ContainingType?.IsValueType == true;

        switch (receiver)
        {
            case null:
                if (MethodSymbol.IsStatic)
                    return false;

                ILGenerator.Emit(OpCodes.Ldarg_0);
                return true;

            case BoundSelfExpression or BoundBaseExpression:
                if (MethodSymbol.IsStatic)
                    return false;

                ILGenerator.Emit(OpCodes.Ldarg_0);
                return true;

            case BoundAddressOfExpression addressOf:
                EmitAddressOfExpression(addressOf);
                return true;

            case BoundDereferenceExpression dereference:
                EmitExpression(dereference.Reference);
                return true;

            case BoundLocalAccess localAccess:
                {
                    if (MethodBodyGenerator.TryGetCapturedField(localAccess.Local, out var capturedField))
                    {
                        MethodBodyGenerator.EmitLoadClosure();
                        ILGenerator.Emit(OpCodes.Ldflda, capturedField);
                        return true;
                    }

                    var local = GetLocal(localAccess.Local);
                    if (local is null)
                        return false;

                    ILGenerator.Emit(OpCodes.Ldloca, local);
                    return true;
                }

            case BoundParameterAccess parameterAccess:
                {
                    if (MethodBodyGenerator.TryGetCapturedField(parameterAccess.Parameter, out var capturedField))
                    {
                        MethodBodyGenerator.EmitLoadClosure();
                        ILGenerator.Emit(OpCodes.Ldflda, capturedField);
                        return true;
                    }

                    var parameterBuilder = MethodGenerator.GetParameterBuilder(parameterAccess.Parameter);
                    var position = parameterBuilder.Position;
                    _ = position; // retained for minimal diff around existing debug flow
                    EmitParameterForByRefUse(parameterAccess.Parameter);
                    return true;
                }

            case BoundMemberAccessExpression memberAccess when memberAccess.Member is IFieldSymbol fieldSymbol:
                {
                    if (fieldSymbol.IsStatic)
                    {
                        ILGenerator.Emit(OpCodes.Ldsflda, GetField(fieldSymbol));
                        if (TryGetAsyncInvestigationFieldLabel(fieldSymbol, out var staticLabel))
                            EmitAsyncInvestigationAddressLogPreservingPointer(staticLabel);
                        return true;
                    }

                    if (!TryEmitInvocationReceiverAddress(memberAccess.Receiver))
                        return false;

                    ILGenerator.Emit(OpCodes.Ldflda, GetField(fieldSymbol));
                    if (TryGetAsyncInvestigationFieldLabel(fieldSymbol, out var instanceLabel))
                        EmitAsyncInvestigationAddressLogPreservingPointer(instanceLabel);
                    return true;
                }
        }

        return false;
    }

    private bool CanReEmitInvocationReceiverAddress(BoundExpression? receiver)
    {
        switch (receiver)
        {
            case null:
                return !MethodSymbol.IsStatic;

            case BoundSelfExpression or BoundBaseExpression:
                return !MethodSymbol.IsStatic;

            case BoundAddressOfExpression addressOf:
                return CanReEmitInvocationReceiverAddress(addressOf.Receiver);

            case BoundDereferenceExpression dereference:
                return CanReEmitInvocationReceiverAddress(dereference.Reference);

            case BoundLocalAccess localAccess:
                if (MethodBodyGenerator.TryGetCapturedField(localAccess.Local, out _))
                    return true;

                return GetLocal(localAccess.Local) is not null;

            case BoundParameterAccess parameterAccess:
                return true;

            case BoundMemberAccessExpression memberAccess when memberAccess.Member is IFieldSymbol fieldSymbol:
                if (fieldSymbol.IsStatic)
                    return true;

                return CanReEmitInvocationReceiverAddress(memberAccess.Receiver);

            default:
                return false;
        }
    }

    private void EmitBoxIfNeeded(ITypeSymbol type, MethodInfo method)
    {
        if (type.IsValueType && method.DeclaringType == typeof(object))
        {
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(type));
        }
    }

    private void EmitValueTypeAddressIfNeeded(ITypeSymbol? runtimeType, ITypeSymbol? declaredType = null)
    {
        runtimeType ??= declaredType;

        if (runtimeType is null || !runtimeType.IsValueType)
            return;

        var clrType = ResolveClrType(runtimeType);

        if (clrType.ContainsGenericParameters && declaredType is not null)
        {
            var declaredClrType = ResolveClrType(declaredType);

            if (!declaredClrType.ContainsGenericParameters)
                clrType = declaredClrType;
        }

        var tmp = ILGenerator.DeclareLocal(clrType);
        ILGenerator.Emit(OpCodes.Stloc, tmp);
        ILGenerator.Emit(OpCodes.Ldloca, tmp);
    }

    private void EmitLiteral(object? constant)
    {
        switch (constant)
        {
            case sbyte i:
                ILGenerator.Emit(OpCodes.Ldc_I4, i);
                break;
            case byte i:
                ILGenerator.Emit(OpCodes.Ldc_I4, i);
                break;
            case short i:
                ILGenerator.Emit(OpCodes.Ldc_I4, i);
                break;
            case ushort i:
                ILGenerator.Emit(OpCodes.Ldc_I4, i);
                break;
            case int i:
                ILGenerator.Emit(OpCodes.Ldc_I4, i);
                break;
            case uint i:
                ILGenerator.Emit(OpCodes.Ldc_I4, unchecked((int)i));
                break;
            case long i:
                ILGenerator.Emit(OpCodes.Ldc_I8, i);
                break;
            case ulong i:
                ILGenerator.Emit(OpCodes.Ldc_I8, unchecked((long)i));
                break;
            case char c:
                ILGenerator.Emit(OpCodes.Ldc_I4, c);
                break;
            case float i:
                ILGenerator.Emit(OpCodes.Ldc_R4, i);
                break;
            case double i:
                ILGenerator.Emit(OpCodes.Ldc_R8, i);
                break;
            case string s:
                ILGenerator.Emit(OpCodes.Ldstr, s);
                break;
            case bool b:
                ILGenerator.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                break;
            case null:
                ILGenerator.Emit(OpCodes.Ldnull);
                break;
            default:
                throw new NotSupportedException($"Literal value type not supported: {constant?.GetType()}");
        }
    }

    private void EmitInvocationExpression(BoundInvocationExpression invocationExpression, bool receiverAlreadyLoaded = false)
    {
        EmitInvocationExpressionBase(invocationExpression, receiverAlreadyLoaded);

        if (_preserveResult && invocationExpression.Type.SpecialType == SpecialType.System_Unit)
        {
            EmitUnitValue();
        }
    }

    /// <summary>
    /// Emit a value required for call: always preserve result (for receivers and normal arguments).
    /// </summary>
    private void EmitRequiredValue(BoundExpression expr)
    {
        // Receivers and value arguments are required to be present on the stack to perform calls.
        // They must be emitted regardless of this generator's _preserveResult.
        new ExpressionGenerator(this, expr, preserveResult: true).Emit2();
    }

    private void EmitUnitValue()
    {
        var unitType = MethodGenerator.TypeGenerator.CodeGen.UnitType
            ?? throw new InvalidOperationException("Unit type was not emitted.");
        var valueField = unitType.GetField("Value")
            ?? throw new InvalidOperationException("Unit.Value field missing.");
        ILGenerator.Emit(OpCodes.Ldsfld, valueField);
    }

    public void EmitInvocationExpressionBase(BoundInvocationExpression invocationExpression, bool receiverAlreadyLoaded = false)
    {
        var target = GetInvocationTarget(invocationExpression);
        var receiver = invocationExpression.Receiver;

        if (target is SourceLambdaSymbol { IsStatic: false } lambdaTarget)
        {
            var inferredReceiverType = receiver?.Type?.UnwrapLiteralType() ?? receiver?.Type;
            if (receiver is BoundSelfExpression or BoundBaseExpression ||
                (inferredReceiverType is not null &&
                 lambdaTarget.ContainingType is not null &&
                 !SymbolEqualityComparer.Default.Equals(inferredReceiverType, lambdaTarget.ContainingType)))
            {
                receiver = null;
            }
        }

        var isExtensionCall =
            target.IsStatic &&
            target is IMethodSymbol m &&
            m.IsExtensionMethod &&
            target.Parameters.Length > 0;

        if (isExtensionCall)
        {
            var parameters = target.Parameters.ToArray();
            var args2 = invocationExpression.Arguments.ToArray();
            var hasExplicitStaticQualifier =
                invocationExpression.ExtensionReceiver is null &&
                invocationExpression.Receiver is BoundTypeExpression;
            var loweredReceiverInArguments =
                invocationExpression.ExtensionReceiver is null &&
                invocationExpression.Receiver is null &&
                args2.Length == parameters.Length &&
                parameters.Length > 0;
            var loweredStaticQualifierInArguments =
                invocationExpression.ExtensionReceiver is null &&
                (invocationExpression.Receiver is null || hasExplicitStaticQualifier) &&
                args2.Length == parameters.Length + 1 &&
                parameters.Length > 0;
            var staticQualifiedExtensionCall =
                hasExplicitStaticQualifier &&
                args2.Length == parameters.Length &&
                parameters.Length > 0;

            var argumentStartIndex = 0;
            var parameterStartIndex = 1;

            if (loweredStaticQualifierInArguments)
            {
                argumentStartIndex = 1;
                parameterStartIndex = 0;
            }
            else if (staticQualifiedExtensionCall)
            {
                argumentStartIndex = 0;
                parameterStartIndex = 0;
            }
            else if (!receiverAlreadyLoaded)
            {
                var receiverArgument =
                    loweredReceiverInArguments
                        ? args2[0]
                        : invocationExpression.ExtensionReceiver ?? invocationExpression.Receiver;

                if (receiverArgument is null)
                {
                    throw new InvalidOperationException(
                        $"Extension invocation '{target}' did not provide a receiver during emission.");
                }

                EmitArgument(receiverArgument, parameters[0]);

                if (loweredReceiverInArguments)
                {
                    argumentStartIndex = 1;
                }
            }
            else if (loweredReceiverInArguments)
            {
                argumentStartIndex = 1;
            }

            var remainingArgumentCount = args2.Length - argumentStartIndex;
            var remainingParameterCount = parameters.Length - parameterStartIndex;
            if (remainingArgumentCount != remainingParameterCount)
            {
                var argumentDebug = string.Join(
                    ", ",
                    args2.Select(static argument => $"{argument.GetType().Name}:{argument.Type}"));
                throw new InvalidOperationException(
                    $"Extension invocation '{target}' argument count mismatch during emission. " +
                    $"Arguments={args2.Length}, Parameters={parameters.Length}, " +
                    $"HasExplicitStaticQualifier={hasExplicitStaticQualifier}, StaticQualifiedExtensionCall={staticQualifiedExtensionCall}, " +
                    $"LoweredReceiverInArguments={loweredReceiverInArguments}, LoweredStaticQualifierInArguments={loweredStaticQualifierInArguments}, ReceiverAlreadyLoaded={receiverAlreadyLoaded}, " +
                    $"ReceiverShape={invocationExpression.Receiver?.GetType().Name}:{invocationExpression.Receiver?.Type}, " +
                    $"ExtensionReceiverShape={invocationExpression.ExtensionReceiver?.GetType().Name}:{invocationExpression.ExtensionReceiver?.Type}, " +
                    $"ArgumentShapes=[{argumentDebug}].");
            }

            for (int argumentIndex = argumentStartIndex, parameterIndex = parameterStartIndex;
                 argumentIndex < args2.Length;
                 argumentIndex++, parameterIndex++)
            {
                EmitArgument(args2[argumentIndex], parameters[parameterIndex]);
            }

            ILGenerator.Emit(OpCodes.Call, GetMethodInfo(target));
            return;
        }

        var closureOwnerMethod = target switch
        {
            SourceMethodSymbol direct => direct,
            _ when target.UnderlyingSymbol is SourceMethodSymbol underlying => underlying,
            _ => null
        };

        if (target.IsStatic &&
            closureOwnerMethod is { HasCaptures: true } sourceTargetMethod &&
            MethodGenerator.TypeGenerator.TryGetMethodClosure(sourceTargetMethod, out var targetClosure))
        {
            EmitInvocationClosureArgument(targetClosure);
        }

        var receiverType = receiver?.Type?.UnwrapLiteralType() ?? receiver?.Type;
        var useConstrainedCall = !target.IsStatic &&
            receiverType is ITypeParameterSymbol typeParameterSymbol &&
            (typeParameterSymbol.ConstraintKind & TypeParameterConstraintKind.ReferenceType) == 0;

        // Emit receiver (for instance methods)
        if (!target.IsStatic)
        {
            var requiresAddress = invocationExpression.RequiresReceiverAddress ||
                useConstrainedCall ||
                (!receiverAlreadyLoaded && target.ContainingType?.IsValueType == true);
            var receiverAddressLoaded = false;

            if (!receiverAlreadyLoaded)
            {
                if (requiresAddress && TryEmitValueTypeReceiverAddress(receiver, receiver?.Type, target.ContainingType))
                {
                    receiverAlreadyLoaded = true;
                    receiverAddressLoaded = true;
                }
                else
                {
                    if (receiver is null)
                    {
                        ILGenerator.Emit(OpCodes.Ldarg_0);
                    }
                    else
                    {
                        EmitRequiredValue(receiver);
                    }
                }
            }
            else if (requiresAddress)
            {
                receiverAddressLoaded = true;
            }

            var effectiveReceiverType = receiverType;

            if (receiverType is NullableTypeSymbol nullable
                && nullable.GetNullableAbiProjection() == NullableAbiProjection.NullableValueType
                && target.ContainingType?.SpecialType != SpecialType.System_Nullable_T)
            {
                if (receiverAlreadyLoaded)
                    effectiveReceiverType = nullable.UnderlyingType;
            }

            if (useConstrainedCall)
            {
                if (!receiverAddressLoaded && effectiveReceiverType is not null)
                {
                    var constrainedClrType = Generator.InstantiateType(ResolveClrType(effectiveReceiverType));
                    var tmp = ILGenerator.DeclareLocal(constrainedClrType);
                    ILGenerator.Emit(OpCodes.Stloc, tmp);
                    ILGenerator.Emit(OpCodes.Ldloca, tmp);
                    receiverAddressLoaded = true;
                }
            }
            else if (effectiveReceiverType?.IsValueType == true)
            {
                var clrType = Generator.InstantiateType(ResolveClrType(effectiveReceiverType));
                var methodDeclaringType = target.ContainingType;
                var canUseReceiverAddress =
                    SymbolEqualityComparer.Default.Equals(effectiveReceiverType, methodDeclaringType) ||
                    HaveEquivalentValueTypeReceiverShape(effectiveReceiverType, methodDeclaringType);

                if (methodDeclaringType.SpecialType == SpecialType.System_Object ||
                    methodDeclaringType.TypeKind == TypeKind.Interface)
                {
                    ILGenerator.Emit(OpCodes.Box, clrType);
                }
                else if (!canUseReceiverAddress)
                {
                    ILGenerator.Emit(OpCodes.Box, clrType);
                }
                else if (!receiverAddressLoaded)
                {
                    var tmp = ILGenerator.DeclareLocal(clrType);
                    ILGenerator.Emit(OpCodes.Stloc, tmp);
                    ILGenerator.Emit(OpCodes.Ldloca, tmp);
                    receiverAddressLoaded = true;
                }
            }
        }

        // Emit arguments (in left-to-right order)
        var paramSymbols = target.Parameters.ToArray();
        var args = invocationExpression.Arguments.ToArray();
        var omittedImplicitUnitArgument =
            args.Length == 0 &&
            paramSymbols.Length == 1 &&
            paramSymbols[0].RefKind == RefKind.None &&
            paramSymbols[0].Type.SpecialType == SpecialType.System_Unit;

        if (omittedImplicitUnitArgument)
            EmitUnitValue();

        var hasExpandedParamsArguments =
            paramSymbols.Length > 0 &&
            paramSymbols[^1].IsVarParams &&
            args.Length > paramSymbols.Length;

        if (!hasExpandedParamsArguments &&
            !omittedImplicitUnitArgument &&
            args.Length != paramSymbols.Length)
        {
            var argumentDebug = string.Join(
                ", ",
                args.Select(static argument => $"{argument.GetType().Name}:{argument.Type}"));
            throw new InvalidOperationException(
                $"Invocation '{target}' argument count mismatch during emission. " +
                $"Arguments={args.Length}, Parameters={paramSymbols.Length}, " +
                $"Receiver={receiver}, ExtensionReceiver={invocationExpression.ExtensionReceiver}, " +
                $"ArgumentShapes=[{argumentDebug}].");
        }

        var regularArgumentCount = hasExpandedParamsArguments
            ? paramSymbols.Length - 1
            : args.Length;

        for (int i = 0; i < regularArgumentCount; i++)
        {
            var paramSymbol = paramSymbols[i];
            var argument = args[i];

            if (argument is null)
            {
                throw new InvalidOperationException(
                    $"Invocation '{target}' has a missing bound argument at index {i} during emission.");
            }

            if (paramSymbol.IsVarParams &&
                argument is BoundCollectionExpression paramsCollection)
            {
                var paramsElements = paramsCollection.Elements.ToArray();
                if (paramsElements.Length == 1 &&
                    paramsElements[0] is BoundSpreadElement singleSpread &&
                    singleSpread.Expression.Type is ITypeSymbol spreadType)
                {
                    var spreadConversion = Compilation.ClassifyConversion(spreadType, paramSymbol.Type);
                    if (spreadConversion.Exists && spreadConversion.IsImplicit)
                    {
                        EmitRequiredValue(singleSpread.Expression);
                        if (!spreadConversion.IsIdentity)
                            EmitConversion(spreadType, paramSymbol.Type, spreadConversion);

                        continue;
                    }
                }
            }

            if (paramSymbol.RefKind is RefKind.Ref or RefKind.Out or RefKind.In)
            {
                switch (argument)
                {
                    case BoundAddressOfExpression addressOf:
                        EmitAddressOfExpression(addressOf);
                        break;

                    case BoundLocalAccess { Symbol: ILocalSymbol local }:
                        ILGenerator.Emit(OpCodes.Ldloca, GetLocal(local));
                        break;

                    case BoundParameterAccess { Parameter: IParameterSymbol p }:
                        // For by-ref parameters use ldarg; otherwise load parameter address.
                        EmitParameterForByRefUse(p);
                        break;

                    default:
                        throw new NotSupportedException($"Unsupported ref/out argument: {argument?.GetType().Name}");
                }
            }
            else
            {
                if (argument?.Type?.TypeKind == TypeKind.Null &&
                    paramSymbol.Type is NullableTypeSymbol nullableParam &&
                    nullableParam.GetNullableAbiProjection() == NullableAbiProjection.NullableValueType)
                {
                    EmitDefaultValue(paramSymbol.Type);
                    continue;
                }

                EmitRequiredValue(argument);

                var argumentType = argument?.Type;
                var paramType = paramSymbol.Type;
                var finalType = argumentType;

                if (argumentType is not null &&
                    paramType is not null &&
                    !SymbolEqualityComparer.Default.Equals(argumentType, paramType))
                {
                    var conversion = Compilation.ClassifyConversion(argumentType, paramType);
                    if (conversion.Exists && !conversion.IsIdentity)
                    {
                        EmitConversion(argumentType, paramType, conversion);
                        finalType = paramType;
                    }
                }

                if (finalType is not null &&
                    RequiresValueTypeHandling(finalType) &&
                    !RequiresValueTypeHandling(paramType))
                {
                    ILGenerator.Emit(OpCodes.Box, ResolveClrType(finalType));
                }
            }
        }

        if (hasExpandedParamsArguments)
        {
            var paramsParam = paramSymbols[^1];
            if (!TryGetVarParamsElementType(paramsParam.Type, out var elementType))
                throw new NotSupportedException("Expanded varargs emission requires a collection-typed params parameter.");

            var extraCount = args.Length - regularArgumentCount;
            var emittedArrayType = Compilation.CreateArrayTypeSymbol(elementType);

            ILGenerator.Emit(OpCodes.Ldc_I4, extraCount);
            ILGenerator.Emit(OpCodes.Newarr, ResolveClrType(elementType));

            for (var i = 0; i < extraCount; i++)
            {
                var argument = args[regularArgumentCount + i];
                ILGenerator.Emit(OpCodes.Dup);
                ILGenerator.Emit(OpCodes.Ldc_I4, i);

                EmitRequiredValue(argument);

                var argumentType = argument?.Type;
                if (argumentType is not null && !SymbolEqualityComparer.Default.Equals(argumentType, elementType))
                {
                    var conversion = Compilation.ClassifyConversion(argumentType, elementType);
                    if (conversion.Exists && !conversion.IsIdentity)
                        EmitConversion(argumentType, elementType, conversion);
                }

                EmitStoreElement(elementType);
            }

            if (!SymbolEqualityComparer.Default.Equals(paramsParam.Type, emittedArrayType))
            {
                var conversion = Compilation.ClassifyConversion(emittedArrayType, paramsParam.Type);
                if (conversion.Exists && !conversion.IsIdentity)
                    EmitConversion(emittedArrayType, paramsParam.Type, conversion);
            }
        }

        // Emit the actual call
        var isInterfaceCall = target.ContainingType?.TypeKind == TypeKind.Interface;
        var useConstrainedStaticInterfaceCall =
            target.IsStatic &&
            target.IsAbstract &&
            isInterfaceCall &&
            receiverType is SourceTypeParameterSymbol &&
            target is IMethodSymbol interfaceMethod &&
            string.Equals(interfaceMethod.MetadataName, "Parse", StringComparison.Ordinal) &&
            string.Equals(interfaceMethod.ContainingType?.ToFullyQualifiedMetadataName(), "System.IParsable`1", StringComparison.Ordinal);

        var targetMethodInfo = GetMethodInfo(target);

        if (target.IsStatic)
        {
            if (useConstrainedStaticInterfaceCall && receiverType is not null)
                ILGenerator.Emit(OpCodes.Constrained, ResolveClrType(receiverType));

            ILGenerator.Emit(OpCodes.Call, targetMethodInfo);
        }
        else
        {
            if (useConstrainedCall && receiverType is not null)
            {
                ILGenerator.Emit(OpCodes.Constrained, ResolveClrType(receiverType));
                ILGenerator.Emit(OpCodes.Callvirt, targetMethodInfo);
            }
            else
            {
                var callOpCode = target.ContainingType!.IsValueType
                    ? OpCodes.Call
                    : IsBaseReceiver(receiver) ? OpCodes.Call
                    : OpCodes.Callvirt;

                ILGenerator.Emit(callOpCode, targetMethodInfo);
            }
        }

    }

    private static bool IsBaseReceiver(BoundExpression? receiver)
    {
        while (true)
        {
            switch (receiver)
            {
                case BoundBaseExpression:
                    return true;
                case BoundConversionExpression conversion when conversion.Conversion.IsIdentity:
                    receiver = conversion.Expression;
                    continue;
                case BoundRequiredResultExpression required:
                    receiver = required.Operand;
                    continue;
                default:
                    return false;
            }
        }
    }

    private static bool HaveEquivalentValueTypeReceiverShape(ITypeSymbol receiverType, ITypeSymbol methodDeclaringType)
    {
        if (receiverType is INamedTypeSymbol receiverNamed &&
            methodDeclaringType is INamedTypeSymbol declaringNamed)
        {
            return SymbolEqualityComparer.Default.Equals(receiverNamed.OriginalDefinition, declaringNamed.OriginalDefinition);
        }

        return false;
    }

    private bool TryGetVarParamsElementType(ITypeSymbol paramsType, out ITypeSymbol elementType)
    {
        paramsType = paramsType.GetNonNullableType();

        if (paramsType is IArrayTypeSymbol { Rank: 1 } arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        if (paramsType is INamedTypeSymbol namedType)
        {
            if (namedType.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
                namedType.TypeArguments.Length == 1)
            {
                elementType = namedType.TypeArguments[0];
                return true;
            }

            foreach (var iface in namedType.AllInterfaces)
            {
                if (iface.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
                    iface.TypeArguments.Length == 1)
                {
                    elementType = iface.TypeArguments[0];
                    return true;
                }
            }
        }

        elementType = null!;
        return false;
    }

    private void EmitInvocationClosureArgument(TypeGenerator.LambdaClosure closure)
    {
        if (ReferenceEquals(MethodGenerator.LambdaClosure, closure))
        {
            MethodBodyGenerator.EmitLoadClosure();
            return;
        }

        if (MethodBodyGenerator.OuterMethodClosure is { } outerClosure &&
            ReferenceEquals(outerClosure, closure) &&
            MethodBodyGenerator.OuterMethodClosureLocal is { } outerLocal)
        {
            ILGenerator.Emit(OpCodes.Ldloc, outerLocal);
            return;
        }

        var runtimeClosureType = closure.GetRuntimeType(MethodGenerator.TypeGenerator.CodeGen);
        var runtimeClosureCtor = runtimeClosureType == closure.TypeBuilder
            ? closure.Constructor
            : TypeBuilder.GetConstructor(runtimeClosureType, closure.Constructor);

        var closureLocal = ILGenerator.DeclareLocal(runtimeClosureType);
        ILGenerator.Emit(OpCodes.Newobj, runtimeClosureCtor);
        ILGenerator.Emit(OpCodes.Stloc, closureLocal);

        foreach (var captured in closure.CapturedSymbols)
        {
            ILGenerator.Emit(OpCodes.Ldloc, closureLocal);
            EmitCapturedValue(captured);
            var closureField = closure.GetField(captured);
            var runtimeField = runtimeClosureType == closure.TypeBuilder
                ? closureField
                : TypeBuilder.GetField(runtimeClosureType, closureField);
            ILGenerator.Emit(OpCodes.Stfld, runtimeField);
        }

        ILGenerator.Emit(OpCodes.Ldloc, closureLocal);
    }

    private void EmitArgument(
        BoundExpression argument,
        IParameterSymbol paramSymbol)
    {
        // ref / out / in
        if (paramSymbol.RefKind is RefKind.Ref or RefKind.Out or RefKind.In)
        {
            switch (argument)
            {
                case BoundAddressOfExpression addressOf:
                    EmitAddressOfExpression(addressOf);
                    return;

                case BoundLocalAccess { Symbol: ILocalSymbol local }:
                    ILGenerator.Emit(OpCodes.Ldloca, GetLocal(local));
                    return;

                case BoundParameterAccess { Parameter: IParameterSymbol p }:
                    {
                        // For by-ref parameters use ldarg; otherwise load parameter address.
                        EmitParameterForByRefUse(p);
                        return;
                    }

                default:
                    throw new NotSupportedException(
                        $"Unsupported ref/out argument: {argument.GetType().Name}");
            }
        }

        // Special: null literal → Nullable<T>
        if (argument.Type?.TypeKind == TypeKind.Null &&
            paramSymbol.Type is NullableTypeSymbol nullable &&
            nullable.GetNullableAbiProjection() == NullableAbiProjection.NullableValueType)
        {
            EmitDefaultValue(paramSymbol.Type);
            return;
        }

        // Normal value argument
        EmitRequiredValue(argument);

        var argumentType = argument.Type;
        var paramType = paramSymbol.Type;
        var finalType = argumentType;

        // Apply conversion if needed
        if (argumentType is not null &&
            paramType is not null &&
            !SymbolEqualityComparer.Default.Equals(argumentType, paramType))
        {
            var conversion = Compilation.ClassifyConversion(argumentType, paramType);
            if (conversion.Exists && !conversion.IsIdentity)
            {
                EmitConversion(argumentType, paramType, conversion);
                finalType = paramType;
            }
        }

        // Box value types when passing to reference-type parameters
        if (finalType is not null &&
            RequiresValueTypeHandling(finalType) &&
            !RequiresValueTypeHandling(paramType))
        {
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(finalType));
        }
    }

    private void EmitParameterForByRefUse(IParameterSymbol parameter)
    {
        var paramBuilder = MethodGenerator.GetParameterBuilder(parameter);
        var position = paramBuilder.Position;
        if (MethodSymbol.IsStatic)
            position -= 1;

        // If the parameter itself is by-ref, loading it already yields managed pointer.
        if (parameter.IsByRefParameter)
            ILGenerator.Emit(OpCodes.Ldarg, position);
        else
            ILGenerator.Emit(OpCodes.Ldarga, position);
    }

    private IMethodSymbol GetInvocationTarget(BoundInvocationExpression invocationExpression)
    {
        if (TryGetSpecializedAsyncBuilderMethod(invocationExpression, out var specialized))
            return specialized;

        if (TryGetSpecializedStaticContainingTypeMethod(invocationExpression, out specialized))
            return specialized;

        return invocationExpression.Method;
    }

    private bool TryGetSpecializedAsyncBuilderMethod(BoundInvocationExpression invocationExpression, out IMethodSymbol specialized)
    {
        var original = invocationExpression.Method;
        specialized = original;

        if (original.ContainingType is not INamedTypeSymbol containingType)
            return false;

        if (!IsAsyncTaskBuilderType(containingType))
            return false;

        var receiverType = GetAsyncBuilderReceiverType(invocationExpression);
        if (receiverType is null)
            return false;

        if (!IsAsyncTaskBuilderType(receiverType))
            return false;

        if (SymbolEqualityComparer.Default.Equals(receiverType, containingType))
            return false;

        var originalDefinition = original.OriginalDefinition ?? original;

        foreach (var candidate in receiverType.GetMembers(original.Name).OfType<IMethodSymbol>())
        {
            if (!SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition ?? candidate, originalDefinition))
                continue;

            if (candidate.IsExtensionMethod != original.IsExtensionMethod)
                continue;

            if (candidate.Parameters.Length != original.Parameters.Length)
                continue;

            var aligned = AlignMethodTypeArguments(candidate, original);
            specialized = aligned;
            return !SymbolEqualityComparer.Default.Equals(aligned, original);
        }

        return false;
    }

    private static INamedTypeSymbol? GetAsyncBuilderReceiverType(BoundInvocationExpression invocationExpression)
    {
        if (invocationExpression.Method.IsStatic)
            return null;

        var receiver = invocationExpression.Receiver;

        if (receiver?.Type is INamedTypeSymbol receiverType)
            return receiverType;

        if (receiver is BoundMemberAccessExpression memberAccess)
        {
            if (memberAccess.Member is IFieldSymbol field && field.Type is INamedTypeSymbol fieldType)
                return fieldType;
        }

        return null;
    }

    private bool TryGetSpecializedStaticContainingTypeMethod(BoundInvocationExpression invocationExpression, out IMethodSymbol specialized)
    {
        var original = invocationExpression.Method;
        specialized = original;

        if (!original.IsStatic ||
            invocationExpression.Receiver is not BoundTypeExpression { Type: INamedTypeSymbol receiverType } ||
            original.ContainingType is not INamedTypeSymbol containingType)
        {
            return false;
        }

        var receiverDefinition = receiverType.OriginalDefinition as INamedTypeSymbol ?? receiverType;
        var containingDefinition = containingType.OriginalDefinition as INamedTypeSymbol ?? containingType;
        if (!SymbolEqualityComparer.Default.Equals(receiverDefinition, containingDefinition))
            return false;

        if (SymbolEqualityComparer.Default.Equals(receiverType, containingType))
            return false;

        var originalDefinition = original.OriginalDefinition ?? original;
        foreach (var candidate in receiverType.GetMembers(original.Name).OfType<IMethodSymbol>())
        {
            if (!SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition ?? candidate, originalDefinition))
                continue;

            if (candidate.IsExtensionMethod != original.IsExtensionMethod)
                continue;

            if (candidate.Parameters.Length != original.Parameters.Length)
                continue;

            var aligned = AlignMethodTypeArguments(candidate, original);
            specialized = aligned;
            return !SymbolEqualityComparer.Default.Equals(aligned, original);
        }

        return false;
    }

    private static IMethodSymbol AlignMethodTypeArguments(IMethodSymbol candidate, IMethodSymbol original)
    {
        if (!original.IsGenericMethod)
            return candidate;

        var originalArgs = original.TypeArguments;
        if (originalArgs.IsDefaultOrEmpty)
            return candidate;

        if (!candidate.IsGenericMethod)
            return candidate;

        if (candidate.TypeArguments.Length == originalArgs.Length)
        {
            bool matches = true;
            for (var i = 0; i < originalArgs.Length; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[i], originalArgs[i]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return candidate;
        }

        return candidate.Construct(originalArgs.ToArray());
    }

    private static bool IsAsyncTaskBuilderType(INamedTypeSymbol type)
    {
        var definition = type.ConstructedFrom as INamedTypeSymbol ?? type;

        if (definition.SpecialType is SpecialType.System_Runtime_CompilerServices_AsyncTaskMethodBuilder
            or SpecialType.System_Runtime_CompilerServices_AsyncTaskMethodBuilder_T)
        {
            return true;
        }

        return definition.MetadataName is "AsyncValueTaskMethodBuilder" or "AsyncValueTaskMethodBuilder`1" &&
            definition.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices";
    }

    private bool TryGetAsyncInvestigationFieldLabel(IFieldSymbol fieldSymbol, out string fieldLabel)
    {
        var options = Compilation.Options.AsyncInvestigation;
        if (!options.IsEnabled)
        {
            fieldLabel = string.Empty;
            return false;
        }

        if (fieldSymbol is not SourceFieldSymbol sourceField)
        {
            fieldLabel = string.Empty;
            return false;
        }

        if (sourceField.ContainingType is not SynthesizedAsyncStateMachineTypeSymbol)
        {
            fieldLabel = string.Empty;
            return false;
        }

        var name = sourceField.Name;
        if (name == "_state" || name == "_builder" || name.StartsWith("<>awaiter", StringComparison.Ordinal))
        {
            fieldLabel = name;
            return true;
        }

        fieldLabel = string.Empty;
        return false;
    }

    private IILocal EnsureAsyncInvestigationPointerLocal()
    {
        return _asyncInvestigationPointerLocal ??= ILGenerator.DeclareLocal(typeof(UIntPtr));
    }

    private void EmitAsyncInvestigationLog(string fieldLabel, AsyncInvestigationOperation operation)
    {
        var options = Compilation.Options.AsyncInvestigation;
        if (!options.IsEnabled)
            return;

        var pointerLocal = EnsureAsyncInvestigationPointerLocal();
        ILGenerator.Emit(OpCodes.Conv_U);
        ILGenerator.Emit(OpCodes.Stloc, pointerLocal);

        var stepLabel = GetAsyncInvestigationStepLabel(options);
        var format = $"{stepLabel}:{fieldLabel}:{GetAsyncInvestigationOperationName(operation)} -> 0x{{0:x}}";
        ILGenerator.Emit(OpCodes.Ldstr, format);
        ILGenerator.Emit(OpCodes.Ldloc, pointerLocal);
        ILGenerator.Emit(OpCodes.Conv_U8);
        ILGenerator.Emit(OpCodes.Box, typeof(ulong));
        ILGenerator.Emit(OpCodes.Call, StringFormatStringObject);
        ILGenerator.Emit(OpCodes.Call, ConsoleWriteLineString);
    }

    private void EmitAsyncInvestigationAddressLogPreservingPointer(string fieldLabel)
    {
        if (!Compilation.Options.AsyncInvestigation.IsEnabled)
            return;

        ILGenerator.Emit(OpCodes.Dup);
        EmitAsyncInvestigationLog(fieldLabel, AsyncInvestigationOperation.Address);
    }

    private string GetAsyncInvestigationStepLabel(AsyncInvestigationOptions options)
    {
        if (options.PointerLabelScope == AsyncInvestigationPointerLabelScope.IncludeAsyncMethodName &&
            MethodSymbol.ContainingType is SourceNamedTypeSymbol { } containingType &&
            containingType is SynthesizedAsyncStateMachineTypeSymbol stateMachine &&
            stateMachine.AsyncMethod is IMethodSymbol asyncMethod)
        {
            var qualifier = SanitizeAsyncInvestigationQualifier(asyncMethod.Name);
            if (!string.IsNullOrEmpty(qualifier))
                return $"{options.StepLabel}/{qualifier}";
        }

        return options.StepLabel;
    }

    private static string SanitizeAsyncInvestigationQualifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('_');
            }
        }

        return builder.ToString().Trim('_');
    }

    private void EmitAsyncInvestigationStore(IFieldSymbol fieldSymbol)
    {
        if (!TryGetAsyncInvestigationFieldLabel(fieldSymbol, out var label))
            return;

        if (fieldSymbol.IsStatic)
        {
            ILGenerator.Emit(OpCodes.Ldsflda, GetField(fieldSymbol));
        }
        else
        {
            ILGenerator.Emit(OpCodes.Ldarg_0);
            ILGenerator.Emit(OpCodes.Ldflda, GetField(fieldSymbol));
        }

        EmitAsyncInvestigationLog(label, AsyncInvestigationOperation.Store);
    }

    private static string GetAsyncInvestigationOperationName(AsyncInvestigationOperation operation)
        => operation switch
        {
            AsyncInvestigationOperation.Load => "load",
            AsyncInvestigationOperation.Store => "store",
            AsyncInvestigationOperation.Address => "addr",
            _ => operation.ToString().ToLowerInvariant()
        };

    private enum AsyncInvestigationOperation
    {
        Load,
        Store,
        Address
    }

    private IILocal? _asyncInvestigationPointerLocal;

    private EmitInfo EmitFieldAccess(BoundFieldAccess fieldAccess, EmitContext context)
    {
        var fieldSymbol = fieldAccess.Field;

        if (!fieldSymbol.IsStatic &&
            MethodGenerator.TypeGenerator.TypeSymbol is SynthesizedAsyncStateMachineTypeSymbol asyncStateMachine &&
            asyncStateMachine.GetConstructedMembers(asyncStateMachine.AsyncMethod).ThisField is { } asyncClosureField &&
            fieldSymbol.ContainingType is { } closureContainingType &&
            closureContainingType.Name.Contains("DisplayClass", StringComparison.Ordinal))
        {
            ILGenerator.Emit(OpCodes.Ldarg_0);
            ILGenerator.Emit(OpCodes.Ldfld, GetField(asyncClosureField));
            ILGenerator.Emit(OpCodes.Ldfld, GetField(fieldSymbol));
            return EmitInfo.ForValue(fieldSymbol);
        }
        if (fieldSymbol.IsConst)
        {
            var constant = fieldSymbol.GetConstantValue();
            switch (constant)
            {
                case sbyte sb:
                    ILGenerator.Emit(OpCodes.Ldc_I4, (int)sb);
                    break;

                case byte b:
                    ILGenerator.Emit(OpCodes.Ldc_I4, (int)b);
                    break;

                case short s:
                    ILGenerator.Emit(OpCodes.Ldc_I4, (int)s);
                    break;

                case ushort us:
                    ILGenerator.Emit(OpCodes.Ldc_I4, (int)us);
                    break;

                case int i:
                    ILGenerator.Emit(OpCodes.Ldc_I4, i);
                    break;

                case uint ui:
                    ILGenerator.Emit(OpCodes.Ldc_I4, unchecked((int)ui));
                    break;

                case long l:
                    ILGenerator.Emit(OpCodes.Ldc_I8, l);
                    break;

                case ulong ul:
                    ILGenerator.Emit(OpCodes.Ldc_I8, unchecked((long)ul));
                    break;

                case float f:
                    ILGenerator.Emit(OpCodes.Ldc_R4, f);
                    break;

                case double d:
                    ILGenerator.Emit(OpCodes.Ldc_R8, d);
                    break;

                case decimal dec:
                    {
                        // decimal has no IL literal — must construct it
                        var bits = decimal.GetBits(dec);

                        ILGenerator.Emit(OpCodes.Ldc_I4, bits[0]);
                        ILGenerator.Emit(OpCodes.Ldc_I4, bits[1]);
                        ILGenerator.Emit(OpCodes.Ldc_I4, bits[2]);
                        ILGenerator.Emit(OpCodes.Ldc_I4, (bits[3] >> 16) & 0x7F); // scale
                        ILGenerator.Emit(((bits[3] & unchecked((int)0x80000000)) != 0)
                            ? OpCodes.Ldc_I4_1
                            : OpCodes.Ldc_I4_0);

                        var ctor = typeof(decimal).GetConstructor(new[]
                        {
            typeof(int), typeof(int), typeof(int), typeof(bool), typeof(byte)
        })!;

                        ILGenerator.Emit(OpCodes.Newobj, ctor);
                        break;
                    }

                case bool b:
                    ILGenerator.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    break;

                case char ch:
                    ILGenerator.Emit(OpCodes.Ldc_I4, (int)ch);
                    break;

                case string s:
                    ILGenerator.Emit(OpCodes.Ldstr, s);
                    break;

                case null:
                    ILGenerator.Emit(OpCodes.Ldnull);
                    break;

                default:
                    throw new NotSupportedException(
                        $"Literal value type not supported: {constant?.GetType()}"
                    );
            }
            return EmitInfo.ForValue(fieldSymbol);
        }

        if (fieldSymbol.IsStatic)
        {
            if (TryGetAsyncInvestigationFieldLabel(fieldSymbol, out var staticLabel))
            {
                ILGenerator.Emit(OpCodes.Ldsflda, GetField(fieldSymbol));
                EmitAsyncInvestigationLog(staticLabel, AsyncInvestigationOperation.Load);
            }

            ILGenerator.Emit(OpCodes.Ldsfld, GetField(fieldSymbol));
            return EmitInfo.ForValue(fieldSymbol);
        }

        var containingType = fieldSymbol.ContainingType;
        var requiresReceiverAddress = containingType?.IsValueType == true;
        var loadedByReference = false;

        if (requiresReceiverAddress)
        {
            loadedByReference = TryEmitValueTypeReceiverAddress(
                fieldAccess.Receiver,
                fieldAccess.Receiver?.Type,
                containingType);
        }

        if (!loadedByReference)
        {
            var shouldLoadClosureReceiver = false;
            FieldInfo? closureFieldInfo = null;

            if (MethodGenerator.TypeGenerator.TypeSymbol is SynthesizedAsyncStateMachineTypeSymbol stateMachine &&
                stateMachine.GetConstructedMembers(stateMachine.AsyncMethod).ThisField is { } thisField &&
                fieldSymbol.ContainingType is { } fieldContainingType &&
                (SymbolEqualityComparer.Default.Equals(fieldContainingType, thisField.Type) ||
                 fieldContainingType.Name.Contains("DisplayClass", StringComparison.Ordinal)))
            {
                var receiverType = fieldAccess.Receiver?.Type;
                if (receiverType is null || !SymbolEqualityComparer.Default.Equals(receiverType, fieldContainingType))
                {
                    shouldLoadClosureReceiver = true;
                    closureFieldInfo = GetField(thisField);
                }
            }

            if (shouldLoadClosureReceiver && closureFieldInfo is not null)
            {
                ILGenerator.Emit(OpCodes.Ldarg_0);
                ILGenerator.Emit(OpCodes.Ldfld, closureFieldInfo);

                if (requiresReceiverAddress)
                    EmitValueTypeAddressIfNeeded(fieldSymbol.ContainingType, containingType);
            }
            else if (fieldAccess.Receiver is not null)
            {
                EmitExpression(fieldAccess.Receiver);

                if (requiresReceiverAddress)
                    EmitValueTypeAddressIfNeeded(fieldAccess.Receiver.Type, containingType);
            }
            else
            {
                ILGenerator.Emit(OpCodes.Ldarg_0);

                if (requiresReceiverAddress)
                    EmitValueTypeAddressIfNeeded(null, containingType);
            }
        }

        if (TryGetAsyncInvestigationFieldLabel(fieldSymbol, out var label))
        {
            ILGenerator.Emit(OpCodes.Dup);
            ILGenerator.Emit(OpCodes.Ldflda, GetField(fieldSymbol));
            EmitAsyncInvestigationLog(label, AsyncInvestigationOperation.Load);
        }

        ILGenerator.Emit(OpCodes.Ldfld, GetField(fieldSymbol));
        return EmitInfo.ForValue(fieldSymbol);
    }

    private void EmitPropertyAccess(BoundPropertyAccess propertyAccess, EmitContext context)
    {
        var propertySymbol = propertyAccess.Property;

        if (propertySymbol.IsExtensionProperty)
            throw new InvalidOperationException($"Extension property '{propertySymbol.Name}' requires an explicit receiver.");

        if (propertySymbol.ContainingType!.Name == "Array") //.SpecialType is SpecialType.System_Array)
        {
            if (propertySymbol.Name == "Length")
            {
                ILGenerator.Emit(OpCodes.Ldlen);
                ILGenerator.Emit(OpCodes.Conv_I4);
            }
        }
        else
        {
            if (!propertySymbol.IsStatic)
            {
                ILGenerator.Emit(OpCodes.Ldarg_0);
                if (propertySymbol.ContainingType.IsValueType)
                    EmitValueTypeAddressIfNeeded(MethodSymbol.ContainingType, propertySymbol.ContainingType);
            }

            if (propertySymbol.GetMethod is null)
                throw new InvalidOperationException($"Property '{propertySymbol.Name}' does not have a getter.");

            MethodInfo getter = GetMethodInfo(propertySymbol.GetMethod);

            var callOpCode = propertySymbol.IsStatic || propertySymbol.ContainingType!.IsValueType
                ? OpCodes.Call
                : OpCodes.Callvirt;

            ILGenerator.Emit(callOpCode, getter);
        }
    }

    private void EmitLiteralExpression(BoundLiteralExpression literalExpression)
    {
        switch (literalExpression.Kind)
        {
            case BoundLiteralExpressionKind.NumericLiteral:
                EmitNumericConstant(literalExpression.Value);
                break;

            case BoundLiteralExpressionKind.StringLiteral:
                {
                    var v = literalExpression.Value;
                    ILGenerator.Emit(OpCodes.Ldstr, (string)v);
                    break;
                }

            case BoundLiteralExpressionKind.CharLiteral:
                {
                    if (literalExpression.Value is char)
                    {
                        ILGenerator.Emit(OpCodes.Ldc_I4, Convert.ToInt32(literalExpression.Value));
                        ILGenerator.Emit(OpCodes.Conv_U2);
                    }
                    break;
                }

            case BoundLiteralExpressionKind.TrueLiteral:
                {
                    ILGenerator.Emit(OpCodes.Ldc_I4_1);
                    break;
                }

            case BoundLiteralExpressionKind.FalseLiteral:
                {
                    ILGenerator.Emit(OpCodes.Ldc_I4_0);
                    break;
                }

            case BoundLiteralExpressionKind.NullLiteral:
                {
                    var convertedType = literalExpression.GetConvertedType();
                    if (convertedType is not null &&
                        (convertedType.IsValueType || convertedType.GetNonNullableType() is ITypeParameterSymbol))
                    {
                        EmitDefaultValue(convertedType);
                    }
                    else
                    {
                        ILGenerator.Emit(OpCodes.Ldnull);
                    }
                    break;
                }

            default:
                throw new Exception("Not supported");
        }
    }

    private void EmitNumericConstant(object value)
    {
        switch (value)
        {
            case byte b:
                ILGenerator.Emit(OpCodes.Ldc_I4, b);
                break;
            case sbyte sb:
                ILGenerator.Emit(OpCodes.Ldc_I4, sb);
                break;
            case short s:
                ILGenerator.Emit(OpCodes.Ldc_I4, s);
                break;
            case ushort us:
                ILGenerator.Emit(OpCodes.Ldc_I4, us);
                break;
            case int i:
                ILGenerator.Emit(OpCodes.Ldc_I4, i);
                break;
            case uint ui:
                ILGenerator.Emit(OpCodes.Ldc_I4, unchecked((int)ui));
                break;
            case long l:
                ILGenerator.Emit(OpCodes.Ldc_I8, l);
                break;
            case ulong ul:
                ILGenerator.Emit(OpCodes.Ldc_I8, unchecked((long)ul));
                break;
            case float f:
                ILGenerator.Emit(OpCodes.Ldc_R4, f);
                break;
            case double d:
                ILGenerator.Emit(OpCodes.Ldc_R8, d);
                break;
            case decimal dec:
                EmitDecimalConstant(dec);
                break;
            case Enum enumValue:
                EmitEnumConstant(enumValue);
                break;
            case DateTime dateTime:
                EmitDateTimeConstant(dateTime);
                break;
            default:
                throw new Exception("Not supported literal value");
        }
    }

    private void EmitDecimalConstant(decimal value)
    {
        var bits = decimal.GetBits(value);
        var isNegative = (bits[3] & unchecked((int)0x80000000)) != 0;
        var scale = (byte)((bits[3] >> 16) & 0x7F);

        ILGenerator.Emit(OpCodes.Ldc_I4, bits[0]);
        ILGenerator.Emit(OpCodes.Ldc_I4, bits[1]);
        ILGenerator.Emit(OpCodes.Ldc_I4, bits[2]);
        ILGenerator.Emit(isNegative ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        ILGenerator.Emit(OpCodes.Ldc_I4_S, scale);

        var constructor = typeof(decimal).GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(bool), typeof(byte) })!;
        ILGenerator.Emit(OpCodes.Newobj, constructor);
    }

    private void EmitEnumConstant(Enum value)
    {
        var underlying = Enum.GetUnderlyingType(value.GetType());
        var numericValue = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture)!;
        EmitNumericConstant(numericValue);
    }

    private void EmitDateTimeConstant(DateTime value)
    {
        var constructor = typeof(DateTime).GetConstructor(new[] { typeof(long), typeof(DateTimeKind) })!;

        ILGenerator.Emit(OpCodes.Ldc_I8, value.Ticks);
        ILGenerator.Emit(OpCodes.Ldc_I4, (int)value.Kind);
        ILGenerator.Emit(OpCodes.Newobj, constructor);
    }

    private void EmitIfExpression(BoundIfExpression ifStatement)
    {
        var elseLabel = ILGenerator.DefineLabel();

        // Create a scope upfront so any pattern variables introduced in the
        // condition are available within the "then" branch.
        var scope = new Scope(this);
        new ExpressionGenerator(scope, ifStatement.Condition)
            .EmitBranchOpForCondition(ifStatement.Condition, elseLabel);

        var resultClrType = ifStatement.Type is not null
            ? ResolveClrType(ifStatement.Type)
            : null;

        new ExpressionGenerator(scope, ifStatement.ThenBranch).Emit();

        var thenType = ifStatement.ThenBranch.Type;

        var thenRequiresBox =
            resultClrType is { IsValueType: false }
            && (thenType?.IsValueType ?? false);

        if (thenRequiresBox)
        {
            ILGenerator.Emit(OpCodes.Box, ResolveClrType(thenType));
        }

        if (ifStatement.ElseBranch is not null)
        {
            // Define a label for the end of the 'if' statement
            var endIfLabel = ILGenerator.DefineLabel();

            // Branch to end of 'if' after the 'if' block
            ILGenerator.Emit(OpCodes.Br, endIfLabel);

            // Mark the 'else' label
            ILGenerator.MarkLabel(elseLabel);

            // Emit the 'else' block
            var scope2 = new Scope(this);
            new ExpressionGenerator(scope2, ifStatement.ElseBranch).Emit();

            var elseType = ifStatement.ElseBranch.Type;

            var elseRequiresBox =
                resultClrType is { IsValueType: false }
                && (elseType?.IsValueType ?? false);

            if (elseRequiresBox)
            {
                ILGenerator.Emit(OpCodes.Box, ResolveClrType(elseType));
            }

            // Mark the end of the 'if' statement
            ILGenerator.MarkLabel(endIfLabel);
        }
        else
        {
            // If no 'else' block, mark the 'else' label
            ILGenerator.MarkLabel(elseLabel);
        }
    }

    internal void EmitBranchOpForCondition(BoundExpression expression, ILLabel end)
    {
        if (expression is BoundParenthesizedExpression parenthesizedExpression)
        {
            EmitBranchOpForCondition(parenthesizedExpression.Expression, end);
            return;
        }

        if (expression is BoundBinaryExpression binaryExpression)
        {
            // Use ordinary expression emission so short-circuiting and operator
            // semantics stay identical in conditions and value expressions.
            EmitExpression(binaryExpression);
            ILGenerator.Emit(OpCodes.Brfalse, end);
        }
        else if (expression is BoundLiteralExpression literalExpression)
        {
            if (literalExpression.Kind == BoundLiteralExpressionKind.TrueLiteral)
            {
                // If true, do nothing; execution continues
            }
            else if (literalExpression.Kind == BoundLiteralExpressionKind.FalseLiteral)
            {
                ILGenerator.Emit(OpCodes.Br, end);
            }
        }
        else
        {
            // Other kinds of expressions... member access etc.

            EmitExpression(expression);
            ILGenerator.Emit(OpCodes.Brfalse, end);
        }
    }

    private void EmitBlock(BoundBlockExpression block)
    {
        var scope = new Scope(this, block.LocalsToDispose);
        var statements = block.Statements.ToArray();
        var emitILScope = block.IntroduceILScope && !IsInsideExceptionHandler;

        if (emitILScope)
            ILGenerator.BeginScope();
        try
        {
            if (statements.Length > 0)
                MethodBodyGenerator.DeclareLocals(scope, statements);

            BoundExpression? resultExpression = null;
            var count = statements.Length;

            if (count > 0 &&
                statements[^1] is BoundExpressionStatement exprStmt &&
                exprStmt.Expression.Type?.SpecialType is not SpecialType.System_Void)
            {
                resultExpression = exprStmt.Expression;
                count--;
            }

            for (int i = 0; i < count; i++)
            {
                var statement = statements[i];
                EmitStatement(statement, scope);
            }

            IILocal? resultTemp = null;
            IILocal? resultLocal = null;
            if (resultExpression is not null)
            {
                if (resultExpression is BoundLocalAccess localAccess &&
                    !block.LocalsToDispose.Any(local => SymbolEqualityComparer.Default.Equals(local, localAccess.Local)) &&
                    scope.GetLocal(localAccess.Local) is { } existingLocal)
                {
                    resultLocal = existingLocal;
                }
                else
                {
                    new ExpressionGenerator(scope, resultExpression).Emit();

                    var resultType = resultExpression.Type;
                    if (resultType is not null)
                    {
                        var clrType = ResolveClrType(resultType);
                        resultTemp = ILGenerator.DeclareLocal(clrType);
                        ILGenerator.Emit(OpCodes.Stloc, resultTemp);
                    }
                }
            }

            EmitDispose(block.LocalsToDispose);

            if (_preserveResult)
            {
                if (resultTemp is not null)
                    ILGenerator.Emit(OpCodes.Ldloc, resultTemp);
                else if (resultLocal is not null)
                    ILGenerator.Emit(OpCodes.Ldloc, resultLocal);
            }
        }
        finally
        {
            if (emitILScope)
                ILGenerator.EndScope();
        }
    }

    private void EmitTryExpression(BoundTryExpression tryExpression)
    {
        var resultType = tryExpression.Type ?? Compilation.ErrorTypeSymbol;
        var resultClrType = ResolveClrType(resultType);
        var resultLocal = ILGenerator.DeclareLocal(resultClrType);

        var requestedCatchType = tryExpression.ExceptionType;
        var exceptionBaseType = Compilation.GetSpecialType(SpecialType.System_Exception);
        if (requestedCatchType.TypeKind == TypeKind.Error)
            requestedCatchType = exceptionBaseType;

        bool IsExceptionLike(ITypeSymbol type)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, exceptionBaseType))
                    return true;
            }

            return false;
        }

        var catchType = IsExceptionLike(requestedCatchType)
            ? requestedCatchType
            : exceptionBaseType;

        var expressionType = tryExpression.Expression.Type ?? Compilation.ErrorTypeSymbol;
        var okConstructor = tryExpression.OkConstructor;
        var errorConstructor = tryExpression.ErrorConstructor;
        var okCaseType = okConstructor.ContainingType ?? Compilation.ErrorTypeSymbol;
        var errorCaseType = errorConstructor.ContainingType ?? Compilation.ErrorTypeSymbol;

        var exitLabel = MethodBodyGenerator.GetOrCreateReturnLabel();

        var tryScope = new Scope(this);
        tryScope.SetExceptionExitLabel(exitLabel);
        tryScope.MarkAsInsideExceptionHandler();

        if (tryExpression.Expression is BoundBlockExpression blockExpression)
        {
            EmitTryExpressionBlock(tryScope, blockExpression);
        }
        else
        {
            ILGenerator.BeginExceptionBlock();
            new ExpressionGenerator(tryScope, tryExpression.Expression).Emit();
        }

        if (okConstructor.Parameters.Length > 0)
        {
            var okParameterType = okConstructor.Parameters[0].Type;
            if (!SymbolEqualityComparer.Default.Equals(expressionType, okParameterType))
            {
                var okParameterConversion = Compilation.ClassifyConversion(expressionType, okParameterType);
                if (okParameterConversion.Exists && !okParameterConversion.IsIdentity)
                    EmitConversion(expressionType, okParameterType, okParameterConversion);
            }
        }
        else if (expressionType.SpecialType is not SpecialType.System_Void)
        {
            ILGenerator.Emit(OpCodes.Pop);
        }

        var okConstructorInfo = GetConstructorInfo(okConstructor);
        ILGenerator.Emit(OpCodes.Newobj, okConstructorInfo);

        if (!SymbolEqualityComparer.Default.Equals(okCaseType, resultType))
        {
            var okCaseConversion = Compilation.ClassifyConversion(okCaseType, resultType);
            if (okCaseConversion.Exists && !okCaseConversion.IsIdentity)
                EmitConversion(okCaseType, resultType, okCaseConversion);
        }

        ILGenerator.Emit(OpCodes.Stloc, resultLocal);

        ILGenerator.BeginCatchBlock(ResolveClrType(catchType));

        var canConstructErrorInCatch = true;
        Conversion toErrorParameterConversion = default;
        if (errorConstructor.Parameters.Length > 0)
        {
            var errorParameterType = errorConstructor.Parameters[0].Type;
            if (!SymbolEqualityComparer.Default.Equals(catchType, errorParameterType))
            {
                toErrorParameterConversion = Compilation.ClassifyConversion(catchType, errorParameterType);
                canConstructErrorInCatch = toErrorParameterConversion.Exists;
            }
        }

        if (!canConstructErrorInCatch)
        {
            ILGenerator.Emit(OpCodes.Pop);
            ILGenerator.Emit(OpCodes.Rethrow);
            ILGenerator.EndExceptionBlock();
            ILGenerator.Emit(OpCodes.Ldloc, resultLocal);
            return;
        }

        if (errorConstructor.Parameters.Length > 0)
        {
            var errorParameterType = errorConstructor.Parameters[0].Type;
            if (!SymbolEqualityComparer.Default.Equals(catchType, errorParameterType))
            {
                var errorParameterConversion = toErrorParameterConversion.Exists
                    ? toErrorParameterConversion
                    : Compilation.ClassifyConversion(catchType, errorParameterType);
                if (errorParameterConversion.Exists && !errorParameterConversion.IsIdentity)
                    EmitConversion(catchType, errorParameterType, errorParameterConversion);
            }
        }
        else
        {
            ILGenerator.Emit(OpCodes.Pop);
        }

        var errorConstructorInfo = GetConstructorInfo(errorConstructor);
        ILGenerator.Emit(OpCodes.Newobj, errorConstructorInfo);

        if (!SymbolEqualityComparer.Default.Equals(errorCaseType, resultType))
        {
            var errorCaseConversion = Compilation.ClassifyConversion(errorCaseType, resultType);
            if (errorCaseConversion.Exists && !errorCaseConversion.IsIdentity)
                EmitConversion(errorCaseType, resultType, errorCaseConversion);
        }

        ILGenerator.Emit(OpCodes.Stloc, resultLocal);

        ILGenerator.EndExceptionBlock();

        ILGenerator.Emit(OpCodes.Ldloc, resultLocal);
    }

    private void EmitTryExpressionBlock(Scope tryScope, BoundBlockExpression blockExpression)
    {
        var blockScope = new Scope(tryScope, blockExpression.LocalsToDispose);
        var statements = blockExpression.Statements.ToArray();

        if (statements.Length > 0)
            MethodBodyGenerator.DeclareLocals(blockScope, statements);

        BoundExpression? resultExpression = null;
        var count = statements.Length;

        if (count > 0 &&
            statements[^1] is BoundExpressionStatement exprStmt &&
            exprStmt.Expression.Type?.SpecialType is not SpecialType.System_Void)
        {
            resultExpression = exprStmt.Expression;
            count--;
        }

        for (int i = 0; i < count; i++)
        {
            if (statements[i] is BoundLabeledStatement labeled)
                MethodBodyGenerator.RegisterLabelScope(labeled.Label, blockScope);
        }

        ILGenerator.BeginExceptionBlock();

        for (int i = 0; i < count; i++)
        {
            var statement = statements[i];
            if (statement is BoundLabeledStatement labeled)
            {
                MethodBodyGenerator.RegisterLabelScope(labeled.Label, blockScope);
                var ilLabel = MethodBodyGenerator.GetOrCreateLabel(labeled.Label);
                ILGenerator.MarkLabel(ilLabel);
                new StatementGenerator(blockScope, labeled.Statement).Emit();
            }
            else
            {
                new StatementGenerator(blockScope, statement).Emit();
            }
        }

        IILocal? resultTemp = null;
        if (resultExpression is not null)
        {
            new ExpressionGenerator(blockScope, resultExpression).Emit();

            var resultExprType = resultExpression.Type;
            if (resultExprType is not null)
            {
                var clrType = ResolveClrType(resultExprType);
                resultTemp = ILGenerator.DeclareLocal(clrType);
                ILGenerator.Emit(OpCodes.Stloc, resultTemp);
            }
        }

        EmitDispose(blockExpression.LocalsToDispose);

        if (resultTemp is not null)
            ILGenerator.Emit(OpCodes.Ldloc, resultTemp);
    }

    private void EmitStatement(BoundStatement statement, Scope scope)
    {
        new StatementGenerator(scope, statement).Emit();
    }

    private void EmitStatement(BoundStatement statement)
    {
        new StatementGenerator(this, statement).Emit();
    }

    private readonly struct DelegateConstructorCacheKey
    {
        public DelegateConstructorCacheKey(
            Type delegateClrType,
            ITypeSymbol returnType,
            ImmutableArray<ITypeSymbol> parameterTypes,
            ImmutableArray<RefKind> refKinds)
        {
            DelegateClrType = delegateClrType ?? throw new ArgumentNullException(nameof(delegateClrType));
            ReturnType = returnType ?? throw new ArgumentNullException(nameof(returnType));
            ParameterTypes = parameterTypes.IsDefault ? ImmutableArray<ITypeSymbol>.Empty : parameterTypes;
            RefKinds = refKinds.IsDefault ? ImmutableArray<RefKind>.Empty : refKinds;
        }

        public Type DelegateClrType { get; }

        public ITypeSymbol ReturnType { get; }

        public ImmutableArray<ITypeSymbol> ParameterTypes { get; }

        public ImmutableArray<RefKind> RefKinds { get; }
    }

    private sealed class DelegateConstructorCacheKeyComparer : IEqualityComparer<DelegateConstructorCacheKey>
    {
        public bool Equals(DelegateConstructorCacheKey x, DelegateConstructorCacheKey y)
        {
            if (!ReferenceEquals(x.DelegateClrType, y.DelegateClrType))
                return false;

            if (!SymbolEqualityComparer.Default.Equals(x.ReturnType, y.ReturnType))
                return false;

            if (x.ParameterTypes.Length != y.ParameterTypes.Length || x.RefKinds.Length != y.RefKinds.Length)
                return false;

            for (var i = 0; i < x.ParameterTypes.Length; i++)
            {
                if (x.RefKinds[i] != y.RefKinds[i])
                    return false;

                if (!SymbolEqualityComparer.Default.Equals(x.ParameterTypes[i], y.ParameterTypes[i]))
                    return false;
            }

            return true;
        }

        public int GetHashCode(DelegateConstructorCacheKey obj)
        {
            var hash = RuntimeHelpers.GetHashCode(obj.DelegateClrType);
            hash = HashCode.Combine(hash, SymbolEqualityComparer.Default.GetHashCode(obj.ReturnType));

            for (var i = 0; i < obj.ParameterTypes.Length; i++)
            {
                hash = HashCode.Combine(
                    hash,
                    SymbolEqualityComparer.Default.GetHashCode(obj.ParameterTypes[i]),
                    obj.RefKinds[i]);
            }

            return hash;
        }
    }

    private void EmitNullCoalesceExpression(BoundNullCoalesceExpression node)
    {
        // Semantics: evaluate Left; if it's non-null (or HasValue for Nullable<T>), yield it; otherwise evaluate Right.
        var leftType = node.Left.Type ?? node.Type;
        var leftClrType = ResolveClrType(leftType);

        // Nullable<T> value-type path
        if (leftClrType.IsValueType && leftClrType.IsGenericType && leftClrType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            // Evaluate once into a temp local
            var leftLocal = ILGenerator.DeclareLocal(leftClrType);
            EmitExpression(node.Left);
            ILGenerator.Emit(OpCodes.Stloc, leftLocal);

            var loadRight = ILGenerator.DefineLabel();
            var end = ILGenerator.DefineLabel();

            // if (!left.HasValue) goto loadRight;
            ILGenerator.Emit(OpCodes.Ldloca, leftLocal);
            var hasValueGetter = leftClrType.GetProperty(nameof(Nullable<int>.HasValue))?.GetMethod
                ?? throw new InvalidOperationException("Missing Nullable<T>.HasValue getter.");
            ILGenerator.Emit(OpCodes.Call, hasValueGetter);
            ILGenerator.Emit(OpCodes.Brfalse, loadRight);

            // left.Value
            ILGenerator.Emit(OpCodes.Ldloca, leftLocal);
            var valueGetter = leftClrType.GetProperty(nameof(Nullable<int>.Value))?.GetMethod
                ?? throw new InvalidOperationException("Missing Nullable<T>.Value getter.");
            ILGenerator.Emit(OpCodes.Call, valueGetter);

            // If the selected value is a value type but the result is a reference type (e.g. object/union), box it.
            if (leftClrType.GenericTypeArguments[0].IsValueType && !ResolveClrType(node.Type).IsValueType)
            {
                ILGenerator.Emit(OpCodes.Box, leftClrType.GenericTypeArguments[0]);
            }

            ILGenerator.Emit(OpCodes.Br, end);

            // right
            ILGenerator.MarkLabel(loadRight);
            EmitExpression(node.Right);

            ILGenerator.MarkLabel(end);
            return;
        }

        // Reference-type path (including nullable references):
        // stack: left
        EmitExpression(node.Left);

        var endLabel = ILGenerator.DefineLabel();

        // Duplicate left; if it's non-null, keep it and jump to end
        ILGenerator.Emit(OpCodes.Dup);
        ILGenerator.Emit(OpCodes.Brtrue, endLabel);

        // It was null: pop the null and evaluate right
        ILGenerator.Emit(OpCodes.Pop);
        EmitExpression(node.Right);

        ILGenerator.MarkLabel(endLabel);
    }

    private static MethodInfo GetNullableHasValueGetter(Type nullableClr)
    {
        var defGetter = typeof(Nullable<>).GetProperty("HasValue")!.GetGetMethod()!;

        var isTypeBuilderInstantiation = string.Equals(
            nullableClr.GetType().FullName,
            "System.Reflection.Emit.TypeBuilderInstantiation",
            StringComparison.Ordinal);

        // TypeBuilderInstantiation can't do GetProperty; map from the generic definition.
        if (isTypeBuilderInstantiation || nullableClr is TypeBuilder)
            return TypeBuilder.GetMethod(nullableClr, defGetter);

        return nullableClr.GetProperty("HasValue")!.GetGetMethod()!;
    }

    private static MethodInfo GetNullableGetValueOrDefault(Type nullableClr)
    {
        var defMethod = typeof(Nullable<>).GetMethod("GetValueOrDefault", Type.EmptyTypes)!;

        var isTypeBuilderInstantiation = string.Equals(
         nullableClr.GetType().FullName,
         "System.Reflection.Emit.TypeBuilderInstantiation",
         StringComparison.Ordinal);

        if (isTypeBuilderInstantiation || nullableClr is TypeBuilder)
            return TypeBuilder.GetMethod(nullableClr, defMethod);

        return nullableClr.GetMethod("GetValueOrDefault", Type.EmptyTypes)!;
    }

    private static MethodInfo GetNullableValueGetter(Type nullableClr)
    {
        // Nullable<T>.Value is a property getter (get_Value), not GetValueOrDefault().
        // We need to support TypeBuilderInstantiation for generic methods emitted via Reflection.Emit.

        var defGetter = typeof(Nullable<>).GetProperty("Value")!.GetGetMethod()!;

        var isTypeBuilderInstantiation = string.Equals(
            nullableClr.GetType().FullName,
            "System.Reflection.Emit.TypeBuilderInstantiation",
            StringComparison.Ordinal);

        // TypeBuilderInstantiation can't do GetProperty reliably; map from the generic definition.
        if (isTypeBuilderInstantiation || nullableClr is TypeBuilder)
            return TypeBuilder.GetMethod(nullableClr, defGetter);

        return nullableClr.GetProperty("Value")!.GetGetMethod()!;
    }
}
