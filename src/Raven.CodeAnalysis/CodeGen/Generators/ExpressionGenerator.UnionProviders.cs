using System.Linq;
using System.Reflection.Emit;

using Raven.CodeAnalysis.Symbols;

namespace Raven.CodeAnalysis.CodeGen;

internal partial class ExpressionGenerator
{
    private bool EmitProviderPattern(
        BoundPattern pattern,
        INamedTypeSymbol union,
        INamedTypeSymbol provider,
        Generator scope,
        IILocal? existingLocal)
    {
        if (pattern is not (BoundDeclarationPattern or BoundConstantPattern or BoundPropertyPattern or
            BoundDeconstructPattern or BoundPositionalPattern or BoundComparisonPattern or BoundRangePattern))
            return false;

        // An inferred binding retains the carrier, just as for ordinary unions.
        if (pattern is BoundDeclarationPattern binding &&
            SymbolEqualityComparer.Default.Equals(binding.Type, union))
            return false;

        var unionClr = Generator.InstantiateType(ResolveClrType(union));
        var local = existingLocal ?? ILGenerator.DeclareLocal(unionClr);
        if (existingLocal is null)
            ILGenerator.Emit(OpCodes.Stloc, local);
        else
            ILGenerator.Emit(OpCodes.Pop);

        var present = ILGenerator.DefineLabel();
        var done = ILGenerator.DefineLabel();
        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        if (!union.IsValueType)
        {
            ILGenerator.Emit(OpCodes.Ldloc, local);
            ILGenerator.Emit(OpCodes.Brtrue, present);
            ILGenerator.Emit(OpCodes.Ldnull);
            EmitPattern(pattern, objectType, scope);
            ILGenerator.Emit(OpCodes.Br, done);
            ILGenerator.MarkLabel(present);
        }

        var isNull = pattern is BoundConstantPattern constant && IsNullConstantPattern(constant) ||
            pattern is BoundDeclarationPattern { Type.TypeKind: TypeKind.Null };
        var hasValue = provider.GetMembers("HasValue").OfType<IPropertySymbol>()
            .FirstOrDefault(property => property.Type.SpecialType == SpecialType.System_Boolean &&
                property.GetMethod is { IsStatic: false, DeclaredAccessibility: Accessibility.Public })?.GetMethod;
        var extraction = pattern is BoundDeclarationPattern declaration
            ? provider.GetMembers("TryGetValue").OfType<IMethodSymbol>().FirstOrDefault(method =>
                !method.IsStatic && method.DeclaredAccessibility == Accessibility.Public &&
                method.ReturnType.SpecialType == SpecialType.System_Boolean && method.Parameters.Length == 1 &&
                method.Parameters[0].RefKind == RefKind.Out &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].GetByRefElementType(), declaration.Type))
            : null;

        if (isNull && hasValue is not null)
        {
            LoadReceiver();
            CallInterface(hasValue);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
            ILGenerator.Emit(OpCodes.Ceq);
        }
        else if (extraction is not null)
        {
            var memberType = extraction.Parameters[0].GetByRefElementType();
            var member = ILGenerator.DeclareLocal(ResolveClrType(memberType));
            var failed = ILGenerator.DefineLabel();
            LoadReceiver();
            ILGenerator.Emit(OpCodes.Ldloca, member);
            CallInterface(extraction);
            ILGenerator.Emit(OpCodes.Brfalse, failed);
            ILGenerator.Emit(OpCodes.Ldloc, member);
            EmitPattern(pattern, memberType, scope, member);
            ILGenerator.Emit(OpCodes.Br, done);
            ILGenerator.MarkLabel(failed);
            ILGenerator.Emit(OpCodes.Ldc_I4_0);
        }
        else
        {
            var getter = provider.GetMembers("Value").OfType<IPropertySymbol>()
                .Single(property => property.GetMethod is { IsStatic: false, DeclaredAccessibility: Accessibility.Public }).GetMethod!;
            LoadReceiver();
            CallInterface(getter);
            EmitPattern(pattern, objectType, scope);
        }

        ILGenerator.MarkLabel(done);
        return true;

        void LoadReceiver() => ILGenerator.Emit(union.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, local);

        void CallInterface(IMethodSymbol method)
        {
            if (union.IsValueType)
                ILGenerator.Emit(OpCodes.Constrained, unionClr);
            ILGenerator.Emit(OpCodes.Callvirt, GetMethodInfo(method));
        }
    }
}
