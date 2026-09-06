using System.Linq;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests;

public sealed class SymbolDisplayTests : CompilationTestBase
{
    [Fact]
    public void ConstField_ToDisplayString_ExcludesStaticModifier()
    {
        const string source = """
class C {
    public const MaxValue: char = 'z'
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        var field = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(declarator));

        var display = field.ToDisplayString(SymbolDisplayFormat.RavenCodeGenerationFormat);

        Assert.Equal("const MaxValue: char = 'z'", display);
        Assert.True(field.IsStatic);
        Assert.True(field.IsConst);
    }

    [Fact]
    public void ExternConstField_ToDisplayString_IncludesExternModifier()
    {
        const string source = "extern const LedPin: int = 25";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        var field = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(declarator));

        field.ToDisplayString(SymbolDisplayFormat.RavenSignatureFormat)
            .ShouldBe("extern const LedPin: int = 25");
    }

    [Fact]
    public void EnumField_ToDisplayString_ShowsUnderlyingConstantValue()
    {
        const string source = """
enum PinEventTypes {
    Falling
    Rising
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var enumDeclaration = tree.GetRoot()
            .DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .Single();
        var enumType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(enumDeclaration));
        var field = Assert.Single(enumType.GetMembers("Rising").OfType<IFieldSymbol>());

        field.ToDisplayString(SymbolDisplayFormat.RavenSignatureFormat)
            .ShouldBe("const Rising: PinEventTypes = 1");
    }

    [Fact]
    public void Property_ToDisplayString_IncludesAccessorAccessibility()
    {
        const string source = """
class C {
    val Value: int { get; private set; }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var property = tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>().Single();
        var symbol = Assert.IsAssignableFrom<IPropertySymbol>(model.GetDeclaredSymbol(property));

        var format = SymbolDisplayFormat.RavenCodeGenerationFormat
            .WithPropertyStyle(SymbolDisplayPropertyStyle.ShowReadWriteDescriptor);
        var display = symbol.ToDisplayString(format);

        Assert.Equal("Value: int { get; private set; }", display);
    }

    [Fact]
    public void Method_ToDisplayString_FormatsProtectedAccessibility()
    {
        const string source = """
class Base {
    protected func Run() -> unit { }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(method));

        var display = symbol.ToDisplayString(SymbolDisplayFormat.RavenCodeGenerationFormat);

        Assert.Equal("protected Run() -> ()", display);
    }

    [Fact]
    public void Method_ToDisplayString_FormatsAllAccessibilityModifiers()
    {
        const string source = """
class Base {
    private func PrivateRun() -> unit { }
    internal func InternalRun() -> unit { }
    protected func ProtectedRun() -> unit { }
    protected internal func ProtectedInternalRun() -> unit { }
    private protected func PrivateProtectedRun() -> unit { }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var methods = tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .ToDictionary(
                static declaration => declaration.Identifier.ValueText,
                declaration => Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(declaration)));

        methods["PrivateRun"].ToDisplayString(SymbolDisplayFormat.RavenCodeGenerationFormat)
            .ShouldBe("private PrivateRun() -> ()");
        methods["InternalRun"].ToDisplayString(SymbolDisplayFormat.RavenCodeGenerationFormat)
            .ShouldBe("internal InternalRun() -> ()");
        methods["ProtectedRun"].ToDisplayString(SymbolDisplayFormat.RavenCodeGenerationFormat)
            .ShouldBe("protected ProtectedRun() -> ()");
        methods["ProtectedInternalRun"].ToDisplayString(SymbolDisplayFormat.RavenCodeGenerationFormat)
            .ShouldBe("protected internal ProtectedInternalRun() -> ()");
        methods["PrivateProtectedRun"].ToDisplayString(SymbolDisplayFormat.RavenCodeGenerationFormat)
            .ShouldBe("private protected PrivateProtectedRun() -> ()");
    }

    [Fact]
    public void Method_ToDisplayString_IncludesOutParameterModifiers()
    {
        const string source = """
class MacroArgument {
    func TryParseValue<T>(out value: int) -> bool { false }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(method));

        symbol.ToDisplayString(SymbolDisplayFormat.RavenSignatureFormat)
            .ShouldBe("func TryParseValue<T>(out value: int) -> bool");
        symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            .ShouldBe("TryParseValue<T>(out value: int) -> bool");
    }

    [Fact]
    public void Method_ToDisplayString_IncludesParameterDefaultValues()
    {
        const string source = """
class WebApplication {
    func Run(port: int = 5000) -> unit { }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(method));

        symbol.ToDisplayString(SymbolDisplayFormat.RavenSignatureFormat)
            .ShouldBe("func Run(port: int = 5000) -> ()");
        symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            .ShouldBe("Run(port: int = 5000) -> ()");
    }

    [Fact]
    public void Operators_ToDisplayString_DoNotRepeatFunctionKeyword()
    {
        const string source = """
class Number {
    static func implicit(value: Number) -> string { "" }
    static func +(left: Number, right: Number) -> Number { left }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var symbols = tree.GetRoot()
            .DescendantNodes()
            .OfType<BaseMethodDeclarationSyntax>()
            .Select(declaration =>
                Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(declaration)))
            .ToArray();

        symbols.Single(symbol => symbol.MethodKind == MethodKind.Conversion)
            .ToDisplayString(SymbolDisplayFormat.RavenSignatureFormat)
            .ShouldBe("static func implicit(value: Number) -> string");
        symbols.Single(symbol => symbol.MethodKind == MethodKind.UserDefinedOperator)
            .ToDisplayString(SymbolDisplayFormat.RavenSignatureFormat)
            .ShouldBe("static func +(left: Number, right: Number) -> Number");
    }

    [Fact]
    public void Method_ToDisplayString_PreservesDefaultLiteralParameterValue()
    {
        const string source = """
func Do(no: int = default) -> unit { }
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<FunctionStatementSyntax>().Single();
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(method));
        var parameter = symbol.Parameters.Single();

        parameter.HasExplicitDefaultValue.ShouldBeTrue();
        parameter.ExplicitDefaultValue.ShouldBe(0);
        symbol.ToDisplayString(SymbolDisplayFormat.RavenSignatureFormat)
            .ShouldBe("static func Do(no: int = default) -> ()");
    }

    [Fact]
    public void Method_ToDisplayString_FormatsTargetTypedEnumParameterDefaultValue()
    {
        const string source = """
enum ServiceLifetime {
    Singleton,
    Scoped,
    Transient
}

func Configure(contextLifetime: ServiceLifetime = .Scoped) -> unit { }
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<FunctionStatementSyntax>().Single();
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(method));
        var parameter = symbol.Parameters.Single();

        parameter.HasExplicitDefaultValue.ShouldBeTrue();
        parameter.ExplicitDefaultValue.ShouldBe(1);
        symbol.ToDisplayString(SymbolDisplayFormat.RavenSignatureFormat)
            .ShouldBe("static func Configure(contextLifetime: ServiceLifetime = .Scoped) -> ()");

        var qualifiedFormat = SymbolDisplayFormat.RavenSignatureFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.RavenSignatureFormat.MiscellaneousOptions &
            ~SymbolDisplayMiscellaneousOptions.UseTargetTypedMemberBinding);
        symbol.ToDisplayString(qualifiedFormat)
            .ShouldBe("static func Configure(contextLifetime: ServiceLifetime = ServiceLifetime.Scoped) -> ()");
    }

    [Fact]
    public void UnionType_ToDisplayString_IncludesUnionRepresentationKeyword()
    {
        const string source = """
record Cash(amount: decimal)
record Card(reference: string)

union Payment(Cash | Card)

union Response<T> {
    case Success(value: T)
    case Failure(message: string)
}

union struct ValueOption<T> {
    case Some(value: T)
    case None
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var declarations = tree.GetRoot().DescendantNodes().OfType<UnionDeclarationSyntax>().ToArray();

        var payment = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(declarations[0]));
        var response = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(declarations[1]));
        var valueOption = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(declarations[2]));

        var format = SymbolDisplayFormat.MinimallyQualifiedFormat.WithKindOptions(
            SymbolDisplayFormat.MinimallyQualifiedFormat.KindOptions |
            SymbolDisplayKindOptions.IncludeTypeKeyword);
        var declarationFormat = format.WithMiscellaneousOptions(
            format.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeUnionMemberTypes);

        payment.ToDisplayString(format).ShouldBe("union struct Payment");
        response.ToDisplayString(format).ShouldBe("union struct Response<T>");
        valueOption.ToDisplayString(format).ShouldBe("union struct ValueOption<T>");

        payment.ToDisplayString(declarationFormat).ShouldBe("union struct Payment(Cash | Card)");
        response.ToDisplayString(declarationFormat).ShouldBe("union struct Response<T>(Success<T> | Failure)");
        valueOption.ToDisplayString(declarationFormat).ShouldBe("union struct ValueOption<T>(Some<T> | None)");
    }

    [Fact]
    public void GetDeclaredSymbol_UnionCaseParameter_ReturnsConstructorParameter()
    {
        const string source = """
union Result {
    case Ok(value: string)
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var parameter = tree.GetRoot().DescendantNodes().OfType<ParameterSyntax>().Single();

        var symbol = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(parameter));

        Assert.Equal("value", symbol.Name);
        Assert.Equal(SpecialType.System_String, symbol.Type.SpecialType);
        var constructor = Assert.IsAssignableFrom<IMethodSymbol>(symbol.ContainingSymbol);
        var caseSymbol = Assert.IsAssignableFrom<IUnionCaseTypeSymbol>(constructor.ContainingType);
        Assert.Equal("Ok", caseSymbol.Name);
    }

    [Fact]
    public void UnionCaseSymbol_ToDisplayString_UsesCaseMemberKeyword()
    {
        const string source = """
union Result<T> {
    case Ok(value: T)
    case Error(message: string)
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();
        var model = compilation.GetSemanticModel(tree);
        var okClause = tree.GetRoot().DescendantNodes().OfType<CaseDeclarationSyntax>().First();
        var okSymbol = Assert.IsAssignableFrom<IUnionCaseTypeSymbol>(model.GetDeclaredSymbol(okClause));

        var format = SymbolDisplayFormat.MinimallyQualifiedFormat.WithKindOptions(
            SymbolDisplayFormat.MinimallyQualifiedFormat.KindOptions |
            SymbolDisplayKindOptions.IncludeMemberKeyword);

        okSymbol.ToDisplayString(format).ShouldBe("case Ok(value: T)");
    }

    [Fact]
    public void UnionCaseSymbol_InsideGenericSignature_DoesNotUseCaseMemberKeyword()
    {
        const string source = """
record Box<T>(value: T)

union Result<T> {
    case Ok(value: T)
    case Error(message: string)
}

class C {
    func Wrap(value: Box<Result<int, string>.Ok>) -> unit { }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(method));

        var format = SymbolDisplayFormat.RavenSignatureFormat.WithKindOptions(
            SymbolDisplayFormat.RavenSignatureFormat.KindOptions |
            SymbolDisplayKindOptions.IncludeMemberKeyword);

        var display = symbol.ToDisplayString(format);

        display.ShouldNotContain("case ");
        display.ShouldBe("func Wrap(value: Box<Ok<T>>) -> ()");
    }

    [Fact]
    public void GenericParenthesizedUnion_ToDisplayString_IncludesTypeArgumentsAndMemberTypes()
    {
        const string source = """
union Either<T1, T2>(T1 | T2)

func Test() {
    let value: Either<int, string> = 42
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var declaration = tree.GetRoot().DescendantNodes().OfType<UnionDeclarationSyntax>().Single();
        var local = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();

        var either = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(declaration));
        var value = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(local));

        var format = SymbolDisplayFormat.MinimallyQualifiedFormat.WithKindOptions(
            SymbolDisplayFormat.MinimallyQualifiedFormat.KindOptions |
            SymbolDisplayKindOptions.IncludeTypeKeyword);
        var declarationFormat = format.WithMiscellaneousOptions(
            format.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeUnionMemberTypes);

        either.ToDisplayString(format).ShouldBe("union struct Either<T1, T2>");
        value.Type.ToDisplayString(format).ShouldBe("union struct Either<int, string>");

        either.ToDisplayString(declarationFormat).ShouldBe("union struct Either<T1, T2>(T1 | T2)");
        value.Type.ToDisplayString(declarationFormat).ShouldBe("union struct Either<int, string>(int | string)");
    }

    [Fact]
    public void SealedHierarchyTypes_ToDisplayString_UsesSealedModifierForClassAndInterface()
    {
        const string source = """
sealed class Expr {}
sealed interface HttpResponse {}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var classSymbol = Assert.IsAssignableFrom<INamedTypeSymbol>(
            model.GetDeclaredSymbol(root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single()));
        var interfaceSymbol = Assert.IsAssignableFrom<INamedTypeSymbol>(
            model.GetDeclaredSymbol(root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Single()));

        var format = SymbolDisplayFormat.MinimallyQualifiedFormat.WithKindOptions(
            SymbolDisplayFormat.MinimallyQualifiedFormat.KindOptions |
            SymbolDisplayKindOptions.IncludeTypeKeyword);

        classSymbol.ToDisplayString(format).ShouldBe("sealed class Expr");
        interfaceSymbol.ToDisplayString(format).ShouldBe("sealed interface HttpResponse");
    }
}
