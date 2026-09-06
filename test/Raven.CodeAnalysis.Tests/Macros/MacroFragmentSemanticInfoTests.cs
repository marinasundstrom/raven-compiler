using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

using Xunit;

namespace Raven.CodeAnalysis.Tests.Macros;

public sealed class MacroFragmentSemanticInfoTests
{
    [Theory]
    [InlineData("token")]
    [InlineData("fragment")]
    [InlineData("annotations")]
    [InlineData("classifications")]
    public void TokenTreeQueries_ArgumentListMacro_ReturnEmpty(string query)
    {
        var tree = SyntaxTree.ParseText("let value = identity!(42)");
        var compilation = Compilation.Create("ArgumentListMacro").AddSyntaxTrees(tree);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot().DescendantNodes()
            .OfType<FreestandingMacroExpressionSyntax>().Single();

        Assert.Null(invocation.TokenTree);
        switch (query)
        {
            case "token":
                Assert.Null(model.GetMacroTokenInfo(invocation, invocation.Name.Span.Start));
                Assert.Null(model.GetMacroTokenInfo(invocation, invocation.ArgumentList!.Arguments[0].Span.Start));
                break;
            case "fragment":
                Assert.Null(model.GetMacroFragmentSemanticInfo(invocation, invocation.Name.Span.Start));
                break;
            case "annotations":
                Assert.Empty(model.GetMacroFragmentInferredTypeAnnotations(invocation));
                break;
            case "classifications":
                var classifications = model.GetMacroFragmentClassifications(invocation);
                Assert.Empty(classifications.Tokens);
                Assert.Empty(classifications.Trivia);
                break;
        }
    }

    [Fact]
    public void GetMacroFragmentSemanticInfo_ResolvesCallerLocalAndMember()
    {
        const string code = """
            import Raven.CodeAnalysis.Tests.Macros.*

            class Customer {
                val Name: string => "Ada"
            }

            func Main() {
                let customer = Customer()
                let value = fragmentHover! { customer.Name }
            }
            """;
        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "MacroFragmentHover",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(new FragmentHoverMacro()));
        var expression = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<FreestandingMacroExpressionSyntax>()
            .Single();
        var customerPosition = code.LastIndexOf("customer.Name", StringComparison.Ordinal) + 1;
        var namePosition = code.LastIndexOf("Name", StringComparison.Ordinal) + 1;

        var customerInfo = compilation.GetMacroFragmentSemanticInfo(expression, customerPosition);
        var nameInfo = compilation.GetMacroFragmentSemanticInfo(expression, namePosition);

        var customer = Assert.IsAssignableFrom<ILocalSymbol>(customerInfo?.SymbolInfo.Symbol);
        Assert.Equal("customer", customer.Name);
        Assert.Equal("Customer", customer.Type.Name);
        Assert.Equal("customer", code.Substring(customerInfo!.Span.Start, customerInfo.Span.Length));

        var name = Assert.IsAssignableFrom<IPropertySymbol>(nameInfo?.SymbolInfo.Symbol);
        Assert.Equal("Name", name.Name);
        Assert.Equal(SpecialType.System_String, name.Type.SpecialType);
        Assert.Equal("Name", code.Substring(nameInfo!.Span.Start, nameInfo.Span.Length));
    }

    [Fact]
    public void GetMacroFragmentSemanticInfo_ResolvesMacroIntroducedLocalAndMember()
    {
        const string code = """
            import Raven.CodeAnalysis.Tests.Macros.*

            class Customer {
                val Name: string => "Ada"
            }

            func Main() {
                let value = fragmentLocalHover! { item.Name }
            }
            """;
        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "MacroFragmentLocalHover",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(new FragmentLocalHoverMacro()));
        var expression = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<FreestandingMacroExpressionSyntax>()
            .Single();
        var itemPosition = code.LastIndexOf("item.Name", StringComparison.Ordinal) + 1;
        var namePosition = code.LastIndexOf("Name", StringComparison.Ordinal) + 1;

        var itemInfo = compilation.GetMacroFragmentSemanticInfo(expression, itemPosition);
        var nameInfo = compilation.GetMacroFragmentSemanticInfo(expression, namePosition);

        var item = Assert.IsAssignableFrom<ILocalSymbol>(itemInfo?.SymbolInfo.Symbol);
        Assert.Equal("item", item.Name);
        Assert.Equal("Customer", item.Type.Name);
        Assert.Equal("item", code.Substring(itemInfo!.Span.Start, itemInfo.Span.Length));

        var name = Assert.IsAssignableFrom<IPropertySymbol>(nameInfo?.SymbolInfo.Symbol);
        Assert.Equal("Name", name.Name);
        Assert.Equal(SpecialType.System_String, name.Type.SpecialType);
    }

    [Fact]
    public void GetMacroFragmentSemanticInfo_ResolvesCollectionComprehensionSymbols()
    {
        const string code = """
            import Raven.CodeAnalysis.Tests.Macros.*

            class Customer {
                val Name: string => "Ada"
            }

            func Main() {
                let customers = [Customer()]
                let values = fragmentHover! {
                    [for customer in customers if customer.Name.Length > 0 => customer.Name]
                }
            }
            """;
        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "MacroFragmentComprehensionHover",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(new FragmentHoverMacro()));
        var expression = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<FreestandingMacroExpressionSyntax>()
            .Single();
        var sourcePosition = code.LastIndexOf("customers if", StringComparison.Ordinal) + 1;
        var localPosition = code.LastIndexOf("customer.Name", StringComparison.Ordinal) + 1;
        var memberPosition = code.LastIndexOf("Name]", StringComparison.Ordinal) + 1;

        var sourceInfo = compilation.GetMacroFragmentSemanticInfo(expression, sourcePosition);
        var localInfo = compilation.GetMacroFragmentSemanticInfo(expression, localPosition);
        var memberInfo = compilation.GetMacroFragmentSemanticInfo(expression, memberPosition);

        var source = Assert.IsAssignableFrom<ILocalSymbol>(sourceInfo?.SymbolInfo.Symbol);
        Assert.Equal("customers", source.Name);
        Assert.Equal("ImmutableList<Customer>", source.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        var local = Assert.IsAssignableFrom<ILocalSymbol>(localInfo?.SymbolInfo.Symbol);
        Assert.Equal("customer", local.Name);
        Assert.Equal("Customer", local.Type.Name);

        var member = Assert.IsAssignableFrom<IPropertySymbol>(memberInfo?.SymbolInfo.Symbol);
        Assert.Equal("Name", member.Name);
        Assert.Equal(SpecialType.System_String, member.Type.SpecialType);
    }

    [Fact]
    public void GetMacroFragmentInferredTypeAnnotations_ReportsCollectionComprehensionTarget()
    {
        const string code = """
            import Raven.CodeAnalysis.Tests.Macros.*

            class Customer {
                val Name: string => "Ada"
            }

            func Main() {
                let customers = [Customer()]
                let values = fragmentHover! {
                    [for customer in customers => customer.Name]
                }
            }
            """;
        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "MacroFragmentComprehensionInlays",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(new FragmentHoverMacro()));
        var expression = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<FreestandingMacroExpressionSyntax>()
            .Single();

        var annotation = Assert.Single(
            compilation.GetSemanticModel(syntaxTree)
                .GetMacroFragmentInferredTypeAnnotations(expression));

        Assert.Equal("customer", code.Substring(annotation.Span.Start, annotation.Span.Length));
        Assert.Equal("Customer", annotation.Type.Name);
    }

    [Fact]
    public void GetMacroFragmentInferredTypeAnnotations_ReportsInferredLocalInDeclarationBlock()
    {
        const string code = """
            import Raven.CodeAnalysis.Tests.Macros.*

            component! Greeting(Name: string) {
                let x = 42
            }
            """;
        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "MacroDeclarationFragmentLocalInlays",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(new ComponentFragmentMacro()));
        var declaration = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<FreestandingMacroDeclarationSyntax>()
            .Single();

        var annotation = Assert.Single(
            compilation.GetSemanticModel(syntaxTree)
                .GetMacroFragmentInferredTypeAnnotations(declaration));

        Assert.Equal("x", code.Substring(annotation.Span.Start, annotation.Span.Length));
        Assert.Equal(SpecialType.System_Int32, annotation.Type.SpecialType);
    }

    [Fact]
    public void GetMacroFragmentInferredTypeAnnotations_ReportsInferredMatchPatternLocal()
    {
        const string code = """
            import Raven.CodeAnalysis.Tests.Macros.*

            union Command {
                case Add(int)
            }

            func Main() {
                let command: Command = .Add(1)
                let value = fragmentHover! {
                    match command {
                        .Add(let amount) => amount
                    }
                }
            }
            """;
        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "MacroFragmentMatchPatternInlays",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(new FragmentHoverMacro()));
        var expression = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<FreestandingMacroExpressionSyntax>()
            .Single();

        var annotation = Assert.Single(
            compilation.GetSemanticModel(syntaxTree)
                .GetMacroFragmentInferredTypeAnnotations(expression));

        Assert.Equal("amount", code.Substring(annotation.Span.Start, annotation.Span.Length));
        Assert.Equal(SpecialType.System_Int32, annotation.Type.SpecialType);
    }

    [Fact]
    public void GetMacroFragmentSemanticInfo_ResolvesIndependentDeclarationBlockRegions()
    {
        const string code = """
            import Raven.CodeAnalysis.Tests.Macros.*

            class Customer {
                val Name: string => "Ada"
            }

            structured! Greeting(customer: Customer) {
                started { customer.Name }
                stopping { customer.Name }
            }
            """;
        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "IndependentDeclarationBlockFragments",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(new StructuredBlockFragmentMacro()));
        var declaration = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<FreestandingMacroDeclarationSyntax>()
            .Single();
        var firstNamePosition = code.IndexOf("customer.Name", StringComparison.Ordinal) + "customer.".Length + 1;
        var secondNamePosition = code.LastIndexOf("customer.Name", StringComparison.Ordinal) + "customer.".Length + 1;

        var firstInfo = compilation.GetMacroFragmentSemanticInfo(declaration, firstNamePosition);
        var secondInfo = compilation.GetMacroFragmentSemanticInfo(declaration, secondNamePosition);

        Assert.Equal("Name", Assert.IsAssignableFrom<IPropertySymbol>(firstInfo?.SymbolInfo.Symbol).Name);
        Assert.Equal("Name", Assert.IsAssignableFrom<IPropertySymbol>(secondInfo?.SymbolInfo.Symbol).Name);
    }

    [Fact]
    public void GetMacroFragmentSemanticInfo_ResolvesPatternDeclarationAndReferenceInBlock()
    {
        const string code = """
            import Raven.CodeAnalysis.Tests.Macros.*

            union Command {
                case Add(amount: int)
            }

            func Main(command: Command) {
                blockFragment! {
                    match command {
                        .Add(let amount) => amount
                    }
                }
            }
            """;
        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "MacroBlockPatternHover",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(new BlockFragmentMacro()));
        var expression = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<FreestandingMacroExpressionSyntax>()
            .Single();
        var declarationPosition = code.LastIndexOf("let amount", StringComparison.Ordinal) + "let ".Length + 1;
        var referencePosition = code.LastIndexOf("=> amount", StringComparison.Ordinal) + "=> ".Length + 1;

        var declarationInfo = compilation.GetMacroFragmentSemanticInfo(expression, declarationPosition);
        var referenceInfo = compilation.GetMacroFragmentSemanticInfo(expression, referencePosition);

        var declaration = Assert.IsAssignableFrom<ILocalSymbol>(declarationInfo?.SymbolInfo.Symbol);
        var reference = Assert.IsAssignableFrom<ILocalSymbol>(referenceInfo?.SymbolInfo.Symbol);
        Assert.Equal("amount", declaration.Name);
        Assert.Equal(SpecialType.System_Int32, declaration.Type.SpecialType);
        Assert.Equal("amount", reference.Name);
        Assert.Equal(SpecialType.System_Int32, reference.Type.SpecialType);
    }

    [Fact]
    public void GetMacroFragmentSemanticInfo_PrefersContextuallyBoundArgumentOverInvocation()
    {
        const string code = """
            import Raven.CodeAnalysis.Tests.Macros.*

            interface Events {
                func Stopping(value: int)
            }

            func Main(events: Events, count: int) {
                blockFragment! {
                    events.Stopping(count)
                }
            }
            """;
        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "MacroBlockContextualArgumentHover",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(new BlockFragmentMacro()));
        var expression = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<FreestandingMacroExpressionSyntax>()
            .Single();
        var position = code.LastIndexOf("count", StringComparison.Ordinal) + 1;

        var info = compilation.GetMacroFragmentSemanticInfo(expression, position);

        var count = Assert.IsAssignableFrom<IParameterSymbol>(info?.SymbolInfo.Symbol);
        Assert.Equal("count", count.Name);
        Assert.Equal(SpecialType.System_Int32, count.Type.SpecialType);
        Assert.Equal("count", code.Substring(info!.Span.Start, info.Span.Length));
    }

    [Fact]
    public void GetMacroFragmentSemanticInfo_ResolvesNestedMacroFragmentSymbols()
    {
        const string code = """
            import Raven.CodeAnalysis.Tests.Macros.*

            class Customer {
                val Name: string => "Ada"
            }

            func Main() {
                let customer = Customer()
                let value = fragmentHover! {
                    fragmentHover! { customer.Name }
                }
            }
            """;
        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "NestedMacroFragmentHover",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(new FragmentHoverMacro()));
        var expression = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<FreestandingMacroExpressionSyntax>()
            .First();
        var customerPosition = code.LastIndexOf("customer.Name", StringComparison.Ordinal) + 1;
        var namePosition = code.LastIndexOf("Name", StringComparison.Ordinal) + 1;

        var macroPosition = code.LastIndexOf("fragmentHover!", StringComparison.Ordinal) + 1;
        var macroInfo = compilation.GetMacroFragmentSemanticInfo(expression, macroPosition);
        var macro = Assert.IsAssignableFrom<IMacroSymbol>(macroInfo?.SymbolInfo.Symbol);
        Assert.Equal("fragmentHover", macro.Name);
        Assert.True(macroInfo!.Span.Contains(macroPosition));

        var customerInfo = compilation.GetMacroFragmentSemanticInfo(expression, customerPosition);
        var nameInfo = compilation.GetMacroFragmentSemanticInfo(expression, namePosition);

        var customer = Assert.IsAssignableFrom<ILocalSymbol>(customerInfo?.SymbolInfo.Symbol);
        Assert.Equal("customer", customer.Name);
        Assert.Equal("Customer", customer.Type.Name);

        var name = Assert.IsAssignableFrom<IPropertySymbol>(nameInfo?.SymbolInfo.Symbol);
        Assert.Equal("Name", name.Name);
        Assert.Equal(SpecialType.System_String, name.Type.SpecialType);
    }

    [Fact]
    public void GetMacroFragmentSemanticInfo_UsesExpressionTargetTypeForLambdaParameters()
    {
        const string code = """
            import Raven.CodeAnalysis.Tests.Macros.*

            func Main() {
                let value = targetTypedFragment! { (value) => value.ToString() }
            }
            """;
        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "MacroFragmentTargetTypedHover",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(new TargetTypedFragmentMacro()));
        var expression = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<FreestandingMacroExpressionSyntax>()
            .Single();
        var parameterPosition = code.LastIndexOf("(value)", StringComparison.Ordinal) + 2;
        var referencePosition = code.LastIndexOf("value.ToString", StringComparison.Ordinal) + 2;

        var parameterInfo = compilation.GetMacroFragmentSemanticInfo(expression, parameterPosition);
        var referenceInfo = compilation.GetMacroFragmentSemanticInfo(expression, referencePosition);

        var parameter = Assert.IsAssignableFrom<IParameterSymbol>(parameterInfo?.SymbolInfo.Symbol);
        Assert.Equal("value", parameter.Name);
        Assert.Equal(SpecialType.System_Int32, parameter.Type.SpecialType);

        var reference = Assert.IsAssignableFrom<IParameterSymbol>(referenceInfo?.SymbolInfo.Symbol);
        Assert.Equal("value", reference.Name);
        Assert.Equal(SpecialType.System_Int32, reference.Type.SpecialType);
    }

    [Fact]
    public void VisibleValueLookup_DoesNotRequireDetachedFragmentSyntaxTree()
    {
        var syntaxTree = SyntaxTree.ParseText("func Main() { }");
        var compilation = Compilation.Create(
                "DetachedMacroFragment",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(syntaxTree);
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var fragment = Assert.IsAssignableFrom<FunctionExpressionSyntax>(
            SyntaxFactory.ParseExpression("(value: int) => value"));

        var declarations = semanticModel.GetVisibleValueDeclarationsForTesting(fragment);

        var declaration = Assert.Single(declarations);
        Assert.Equal("value", declaration.Name);
        Assert.Null(declaration.DeclarationNode.SyntaxTree);
    }

    private sealed class FragmentHoverMacro : IMacroDefinition, IMacroFragmentProvider
    {
        public string Name => "fragmentHover";

        public FreestandingMacroExpansionResult Expand(TokenTreeMacroContext context)
            => FreestandingMacroExpansionResult.Empty;

        public ImmutableArray<MacroFragmentRegion> GetFragmentRegions(TokenTreeMacroContext context)
            =>
            [
                context.CreateFragmentRegion(
                    MacroFragmentKind.Expression,
                    new TextSpan(0, context.BodySpan.Length))
            ];
    }

    private sealed class BlockFragmentMacro : IMacroDefinition, IMacroFragmentProvider
    {
        public string Name => "blockFragment";

        public FreestandingMacroExpansionResult Expand(TokenTreeMacroContext context)
            => FreestandingMacroExpansionResult.Empty;

        public ImmutableArray<MacroFragmentRegion> GetFragmentRegions(TokenTreeMacroContext context)
            =>
            [
                context.CreateFragmentRegion(
                    MacroFragmentKind.Block,
                    new TextSpan(0, context.BodySpan.Length))
            ];
    }

    private sealed class FragmentLocalHoverMacro : IMacroDefinition, IMacroFragmentProvider
    {
        public string Name => "fragmentLocalHover";

        public FreestandingMacroExpansionResult Expand(TokenTreeMacroContext context)
            => FreestandingMacroExpansionResult.Empty;

        public ImmutableArray<MacroFragmentRegion> GetFragmentRegions(TokenTreeMacroContext context)
        {
            var customerType = context.Compilation.GetTypeByMetadataName("Customer")!;
            var item = context.CreateFragmentLocal("item", customerType);
            return
            [
                context.CreateFragmentRegion(
                    MacroFragmentKind.Expression,
                    new TextSpan(0, context.BodySpan.Length),
                    [item])
            ];
        }
    }

    private sealed class TargetTypedFragmentMacro : IMacroDefinition, IMacroFragmentProvider
    {
        public string Name => "targetTypedFragment";

        public FreestandingMacroExpansionResult Expand(TokenTreeMacroContext context)
            => FreestandingMacroExpansionResult.Empty;

        public ImmutableArray<MacroFragmentRegion> GetFragmentRegions(TokenTreeMacroContext context)
        {
            var actionDefinition = context.Compilation.GetTypeByMetadataName("System.Action`1")!;
            var intType = context.Compilation.GetSpecialType(SpecialType.System_Int32);
            var actionType = actionDefinition.Construct(intType);
            return
            [
                context.CreateExpressionFragmentRegion(
                    new TextSpan(0, context.BodySpan.Length),
                    actionType)
            ];
        }
    }

    private sealed class ComponentFragmentMacro : IMacroDefinition, IMacroFragmentProvider
    {
        public string Name => "FunctionComponent";

        public string? Alias => "component";

        public MacroInvocationTargets InvocationTargets =>
            MacroInvocationTargets.NamespaceMember | MacroInvocationTargets.TypeMember;

        public ImmutableArray<MacroFragmentRegion> GetFragmentRegions(TokenTreeMacroContext context)
            =>
            [
                context.CreateFragmentRegion(
                    MacroFragmentKind.Block,
                    new TextSpan(0, context.BodySpan.Length))
            ];

        public MemberDeclarationSyntax Expand(
            FreestandingMacroDeclarationSyntax declaration,
            TokenTreeMacroContext context)
            => SyntaxFactory.ParseMemberDeclaration($"class {declaration.Identifier.ValueText} {{ }}")!;
    }

    private sealed class StructuredBlockFragmentMacro : IMacroDefinition, IMacroFragmentProvider
    {
        public string Name => "structured";

        public MacroInvocationTargets InvocationTargets => MacroInvocationTargets.NamespaceMember;

        public ImmutableArray<MacroFragmentRegion> GetFragmentRegions(TokenTreeMacroContext context)
        {
            var declaration = (FreestandingMacroDeclarationSyntax)context.Syntax;
            var parameter = declaration.ParameterList!.Parameters[0];
            var parameterType = context.SemanticModel.GetTypeInfo(parameter.TypeAnnotation!.Type).ConvertedType!;
            var local = context.CreateFragmentParameter(
                parameter.Identifier.ValueText,
                parameterType,
                parameter.Identifier.Span);
            var body = context.GetBodyText();
            var firstStart = body.IndexOf("customer.Name", StringComparison.Ordinal);
            var secondStart = body.LastIndexOf("customer.Name", StringComparison.Ordinal);

            return
            [
                context.CreateFragmentRegion(
                    MacroFragmentKind.Block,
                    new TextSpan(firstStart, "customer.Name".Length),
                    [local]),
                context.CreateFragmentRegion(
                    MacroFragmentKind.Block,
                    new TextSpan(secondStart, "customer.Name".Length),
                    [local])
            ];
        }

        public MemberDeclarationSyntax Expand(
            FreestandingMacroDeclarationSyntax declaration,
            TokenTreeMacroContext context)
            => SyntaxFactory.ParseMemberDeclaration($"class {declaration.Identifier.ValueText} {{ }}")!;
    }
}
