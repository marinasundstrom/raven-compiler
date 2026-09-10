using System.Linq;
using System.Threading.Tasks;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests;

public sealed class ClassifyConversionTests : CompilationTestBase
{
    [Theory]
    [InlineData(SpecialType.System_Int32)]
    [InlineData(SpecialType.System_String)]
    [InlineData(SpecialType.System_Boolean)]
    public void IdentityConversions_AreImplicitAndIdentity(SpecialType specialType)
    {
        var compilation = CreateCompilation();
        var type = compilation.GetSpecialType(specialType);

        var conversion = compilation.ClassifyConversion(type, type);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsIdentity);
        Assert.False(conversion.IsNumeric);
        Assert.False(conversion.IsReference);
        Assert.False(conversion.IsBoxing);
        Assert.False(conversion.IsUnboxing);
        Assert.False(conversion.IsUserDefined);
    }

    [Theory]
    [InlineData("System.Func`1", SpecialType.System_String, SpecialType.System_Object, true)]
    [InlineData("System.Func`1", SpecialType.System_Object, SpecialType.System_String, false)]
    [InlineData("System.Action`1", SpecialType.System_Object, SpecialType.System_String, true)]
    [InlineData("System.Action`1", SpecialType.System_String, SpecialType.System_Object, false)]
    [InlineData("System.Func`1", SpecialType.System_Int32, SpecialType.System_Object, false)]
    [InlineData("System.Action`1", SpecialType.System_Object, SpecialType.System_Int32, false)]
    public void DelegateVariance_RequiresReferenceConversion(
        string metadataName, SpecialType sourceArgument, SpecialType targetArgument, bool expected)
    {
        var compilation = CreateCompilation();
        var definition = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(metadataName));
        var source = definition.Construct(compilation.GetSpecialType(sourceArgument));
        var target = definition.Construct(compilation.GetSpecialType(targetArgument));

        var conversion = compilation.ClassifyConversion(source, target);

        Assert.Equal(expected, conversion.Exists && conversion.IsImplicit);
        if (expected)
            Assert.True(conversion.IsReference);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DelegateCovariance_AllowsNullableReturnWideningOnly(bool reverse, bool expected)
    {
        var compilation = CreateCompilation();
        var definition = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("System.Func`1"));
        var text = compilation.GetSpecialType(SpecialType.System_String);
        var nonNullable = definition.Construct(text);
        var nullable = definition.Construct(text.GetNullableType());

        var conversion = reverse
            ? compilation.ClassifyConversion(nullable, nonNullable)
            : compilation.ClassifyConversion(nonNullable, nullable);

        Assert.Equal(expected, conversion.Exists && conversion.IsImplicit);
    }

    [Fact]
    public void Null_ConvertsToNullableReference()
    {
        var compilation = CreateCompilation();
        var stringType = compilation.GetSpecialType(SpecialType.System_String);
        var nullableString = stringType.GetNullableType();

        var conversion = compilation.ClassifyConversion(compilation.NullTypeSymbol, nullableString);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsReference);
        Assert.False(conversion.IsIdentity);
    }

    [Fact]
    public void ValueType_LiftsToNullableImplicitly()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var nullableInt = intType.GetNullableType();

        var conversion = compilation.ClassifyConversion(intType, nullableInt);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.False(conversion.IsIdentity);
        Assert.False(conversion.IsReference);
        Assert.False(conversion.IsBoxing);
    }

    [Fact]
    public void NullableValueType_ToUnderlying_IsExplicit()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var nullableInt = intType.GetNullableType();

        var conversion = compilation.ClassifyConversion(nullableInt, intType);

        Assert.True(conversion.Exists);
        Assert.False(conversion.IsImplicit);
        Assert.True(conversion.IsIdentity);
        Assert.True(conversion.IsLifted);
    }

    [Fact]
    public void NullableValueType_ToNullableDestination_IsLiftedNumericConversion()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var longType = compilation.GetSpecialType(SpecialType.System_Int64);
        var nullableInt = intType.GetNullableType();
        var nullableLong = longType.GetNullableType();

        var conversion = compilation.ClassifyConversion(nullableInt, nullableLong);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsNumeric);
        Assert.True(conversion.IsLifted);
        Assert.False(conversion.IsIdentity);
    }

    [Fact]
    public void NullableValueType_ToNullableReference_DoesNotConvert()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var stringType = compilation.GetSpecialType(SpecialType.System_String);
        var nullableInt = intType.GetNullableType();
        var nullableString = stringType.GetNullableType();

        var conversion = compilation.ClassifyConversion(nullableInt, nullableString);

        Assert.False(conversion.Exists);
    }

    [Fact]
    public void ArrayElementNullability_DoesNotChangeRuntimeConversionIdentity()
    {
        var compilation = CreateCompilation();
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);
        var source = compilation.CreateArrayTypeSymbol(objectType);
        var destination = compilation.CreateArrayTypeSymbol(objectType.GetNullableType());

        var conversion = compilation.ClassifyConversion(source, destination);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
    }

    [Fact]
    public void ArrayValueElements_DoNotUseElementNumericConversions()
    {
        var compilation = CreateCompilation();
        var source = compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Int32));
        var destination = compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Int64));

        var conversion = compilation.ClassifyConversion(source, destination);

        Assert.False(conversion.Exists);
    }

    [Theory]
    [InlineData(SpecialType.System_Int32, SpecialType.System_Int64)]
    [InlineData(SpecialType.System_Int32, SpecialType.System_Double)]
    [InlineData(SpecialType.System_Single, SpecialType.System_Double)]
    public void ImplicitNumericConversions_AreMarkedNumeric(SpecialType sourceSpecialType, SpecialType destinationSpecialType)
    {
        var compilation = CreateCompilation();
        var source = compilation.GetSpecialType(sourceSpecialType);
        var destination = compilation.GetSpecialType(destinationSpecialType);

        var conversion = compilation.ClassifyConversion(source, destination);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsNumeric);
        Assert.False(conversion.IsIdentity);
    }

    [Theory]
    [InlineData(SpecialType.System_Double, SpecialType.System_Int32)]
    [InlineData(SpecialType.System_Int64, SpecialType.System_Int32)]
    public void ExplicitNumericConversions_AreMarkedNumeric(SpecialType sourceSpecialType, SpecialType destinationSpecialType)
    {
        var compilation = CreateCompilation();
        var source = compilation.GetSpecialType(sourceSpecialType);
        var destination = compilation.GetSpecialType(destinationSpecialType);

        var conversion = compilation.ClassifyConversion(source, destination);

        Assert.True(conversion.Exists);
        Assert.False(conversion.IsImplicit);
        Assert.True(conversion.IsNumeric);
    }

    [Theory]
    [InlineData(SpecialType.System_Byte)]
    [InlineData(SpecialType.System_Int32)]
    [InlineData(SpecialType.System_UInt64)]
    [InlineData(SpecialType.System_Char)]
    public void EnumConversions_ToAndFromIntegralTypes_AreExplicitNumeric(SpecialType integralSpecialType)
    {
        const string source = """
enum Status : byte {
    Ready
}
""";

        var (compilation, tree) = CreateCompilation(source);
        Assert.Empty(compilation.GetDiagnostics());

        var model = compilation.GetSemanticModel(tree);
        var enumDeclaration = tree.GetRoot().DescendantNodes().OfType<EnumDeclarationSyntax>().Single();
        var enumType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(enumDeclaration));
        var integralType = compilation.GetSpecialType(integralSpecialType);

        var enumToIntegral = compilation.ClassifyConversion(enumType, integralType);
        Assert.True(enumToIntegral.Exists);
        Assert.False(enumToIntegral.IsImplicit);
        Assert.True(enumToIntegral.IsNumeric);

        var integralToEnum = compilation.ClassifyConversion(integralType, enumType);
        Assert.True(integralToEnum.Exists);
        Assert.False(integralToEnum.IsImplicit);
        Assert.True(integralToEnum.IsNumeric);
    }

    [Fact]
    public void EnumConversions_BetweenEnumTypes_AreExplicitNumeric()
    {
        const string source = """
enum SourceKind : byte {
    A
}

enum DestinationKind : long {
    B
}
""";

        var (compilation, tree) = CreateCompilation(source);
        Assert.Empty(compilation.GetDiagnostics());

        var model = compilation.GetSemanticModel(tree);
        var enumTypes = tree.GetRoot()
            .DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .Select(declaration => Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(declaration)))
            .ToArray();

        var conversion = compilation.ClassifyConversion(enumTypes[0], enumTypes[1]);

        Assert.True(conversion.Exists);
        Assert.False(conversion.IsImplicit);
        Assert.True(conversion.IsNumeric);
    }

    [Fact]
    public void ReferenceConversion_ToBaseType_IsImplicit()
    {
        var source = """
        open class Animal {}
        class Dog : Animal {}
        """;

        var (compilation, tree) = CreateCompilation(source);
        Assert.Empty(compilation.GetDiagnostics());
        var model = compilation.GetSemanticModel(tree);
        var classes = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().ToArray();
        var animal = (INamedTypeSymbol)model.GetDeclaredSymbol(classes[0])!;
        var dog = (INamedTypeSymbol)model.GetDeclaredSymbol(classes[1])!;

        var conversion = compilation.ClassifyConversion(dog, animal);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsReference);
        Assert.False(conversion.IsIdentity);
    }

    [Fact]
    public void ReferenceConversion_ToImplementedInterface_IsImplicit()
    {
        var source = """
import System.*

class Foo : IDisposable {
    init() {}

    func Dispose() -> unit {}
}
""";

        var (compilation, tree) = CreateCompilation(source);
        Assert.Empty(compilation.GetDiagnostics());
        var model = compilation.GetSemanticModel(tree);
        var classDeclaration = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var foo = (INamedTypeSymbol)model.GetDeclaredSymbol(classDeclaration)!;
        var disposable = compilation.GetTypeByMetadataName("System.IDisposable")!;

        var conversion = compilation.ClassifyConversion(foo, disposable);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsReference);
        Assert.False(conversion.IsIdentity);
    }

    [Fact]
    public void ReferenceConversion_HandlesInterfaceCovariance()
    {
        var compilation = CreateCompilation();
        var stringType = compilation.GetSpecialType(SpecialType.System_String);
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);

        var listDefinition = (INamedTypeSymbol)compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!;
        var listOfString = (INamedTypeSymbol)listDefinition.Construct(stringType);
        var enumerableDefinition = (INamedTypeSymbol)compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1")!;
        var enumerableOfObject = (INamedTypeSymbol)enumerableDefinition.Construct(objectType);

        var conversion = compilation.ClassifyConversion(listOfString, enumerableOfObject);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsReference);
    }

    [Fact]
    public void ReferenceConversion_ArrayToGenericIEnumerable_IsImplicit()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var arrayType = compilation.CreateArrayTypeSymbol(intType);
        var enumerableDefinition = (INamedTypeSymbol)compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1")!;
        var enumerableOfInt = (INamedTypeSymbol)enumerableDefinition.Construct(intType);

        var conversion = compilation.ClassifyConversion(arrayType, enumerableOfInt);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsReference);
    }

    [Fact]
    public void ReferenceConversion_ArrayToNonGenericIEnumerable_IsImplicit()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var arrayType = compilation.CreateArrayTypeSymbol(intType);
        var enumerableType = compilation.GetSpecialType(SpecialType.System_Collections_IEnumerable);

        var conversion = compilation.ClassifyConversion(arrayType, enumerableType);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsReference);
    }

    [Fact]
    public void ReferenceConversion_FixedArrayToOpenArray_IsImplicit()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var fixedArray = compilation.CreateArrayTypeSymbol(intType, fixedLength: 3);
        var openArray = compilation.CreateArrayTypeSymbol(intType);

        var conversion = compilation.ClassifyConversion(fixedArray, openArray);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsReference);
    }

    [Fact]
    public void ReferenceConversion_OpenArrayToFixedArray_DoesNotExist()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var fixedArray = compilation.CreateArrayTypeSymbol(intType, fixedLength: 3);
        var openArray = compilation.CreateArrayTypeSymbol(intType);

        var conversion = compilation.ClassifyConversion(openArray, fixedArray);

        Assert.False(conversion.Exists);
    }

    [Fact]
    public void ClassifyConversion_IsStableUnderConcurrentAccess()
    {
        var compilation = CreateCompilation();
        var source = compilation.GetSpecialType(SpecialType.System_Int32);
        var destination = compilation.GetSpecialType(SpecialType.System_Int64);
        var results = new Conversion[256];

        Parallel.For(0, results.Length, i =>
        {
            results[i] = compilation.ClassifyConversion(source, destination);
        });

        Assert.All(results, conversion =>
        {
            Assert.True(conversion.Exists);
            Assert.True(conversion.IsImplicit);
            Assert.True(conversion.IsNumeric);
        });
    }

    [Fact]
    public void ReferenceConversion_SourceInterfaceVariance_IsImplicit()
    {
        var source = """
interface Producer<out T> {}

class Widget : Producer<string> {}
""";

        var (compilation, tree) = CreateCompilation(source);
        Assert.Empty(compilation.GetDiagnostics());

        var model = compilation.GetSemanticModel(tree);
        var interfaceSyntax = tree.GetRoot().DescendantNodes().OfType<InterfaceDeclarationSyntax>().Single();
        var classSyntax = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

        var producerDefinition = (INamedTypeSymbol)model.GetDeclaredSymbol(interfaceSyntax)!;
        var widget = (INamedTypeSymbol)model.GetDeclaredSymbol(classSyntax)!;
        var producerOfObject = (INamedTypeSymbol)producerDefinition.Construct(compilation.GetSpecialType(SpecialType.System_Object));

        var conversion = compilation.ClassifyConversion(widget, producerOfObject);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsReference);
    }

    [Fact]
    public void ReferenceConversion_HandlesInterfaceContravariance()
    {
        var source = """
    import System.Collections.Generic.*

class Comparer : IComparer<object>
{
    init() {}

    func Compare(x: object?, y: object?) -> int => 0
}
""";

        var (compilation, tree) = CreateCompilation(source);
        Assert.Empty(compilation.GetDiagnostics());
        var model = compilation.GetSemanticModel(tree);
        var comparerDeclaration = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var comparer = (INamedTypeSymbol)model.GetDeclaredSymbol(comparerDeclaration)!;
        var comparerDefinition = (INamedTypeSymbol)compilation.GetTypeByMetadataName("System.Collections.Generic.IComparer`1")!;
        var comparerOfString = (INamedTypeSymbol)comparerDefinition.Construct(compilation.GetSpecialType(SpecialType.System_String));

        var conversion = compilation.ClassifyConversion(comparer, comparerOfString);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsReference);
    }

    [Fact]
    public void BoxingConversion_ValueTypeToObject_IsImplicit()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);

        var conversion = compilation.ClassifyConversion(intType, objectType);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsBoxing);
        Assert.False(conversion.IsReference);
        Assert.False(conversion.IsIdentity);
    }

    [Fact]
    public void UnboxingConversion_ObjectToValueType_IsExplicit()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);

        var conversion = compilation.ClassifyConversion(objectType, intType);

        Assert.True(conversion.Exists);
        Assert.False(conversion.IsImplicit);
        Assert.True(conversion.IsUnboxing);
    }

    [Fact]
    public void BoxingConversion_ValueTypeToImplementedInterface_IsImplicit()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var comparableType = compilation.GetTypeByMetadataName("System.IComparable")!;

        var conversion = compilation.ClassifyConversion(intType, comparableType);

        Assert.True(conversion.Exists);
        Assert.True(conversion.IsImplicit);
        Assert.True(conversion.IsBoxing);
        Assert.False(conversion.IsReference);
    }

    [Fact]
    public void UnboxingConversion_InterfaceToImplementingValueType_IsExplicit()
    {
        var compilation = CreateCompilation();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var comparableType = compilation.GetTypeByMetadataName("System.IComparable")!;

        var conversion = compilation.ClassifyConversion(comparableType, intType);

        Assert.True(conversion.Exists);
        Assert.False(conversion.IsImplicit);
        Assert.True(conversion.IsUnboxing);
    }

}
