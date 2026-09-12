using System;
using System.Linq;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;

using Xunit;

namespace Raven.CodeAnalysis.Syntax.Tests;

public class PatternSyntaxParserTests
{
    [Theory]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData(">")]
    [InlineData(">=")]
    [InlineData("<")]
    [InlineData("<=")]
    public void ComparisonPattern_VariableOperand_DoesNotConsumeMatchArmArrow(string op)
    {
        var tree = SyntaxTree.ParseText($"let result = value match {{ {op} preferred => true, _ => false }}");
        AssertNoErrors(tree);
        var comparison = tree.GetRoot().DescendantNodes().OfType<ComparisonPatternSyntax>().Single();
        Assert.Equal("preferred", Assert.IsType<IdentifierNameSyntax>(comparison.Expression).Identifier.ValueText);
    }

    [Fact]
    public void DeclarationPattern_WithIdentifier_Parses()
    {
        var (pattern, tree) = ParsePattern("int number");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var declaration = Assert.IsType<DeclarationPatternSyntax>(pattern);
        Assert.Equal("int number", sourceText.ToString(declaration.Span));
        Assert.Equal("int", declaration.Type.ToString());

        var designation = Assert.IsType<SingleVariableDesignationSyntax>(declaration.Designation);
        Assert.Equal("number", designation.Identifier.ValueText);

        AssertNoErrors(tree);
    }

    [Fact]
    public void DeclarationPattern_WithoutIdentifier_HasNoDesignation()
    {
        var (pattern, tree) = ParsePattern("int");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var declaration = Assert.IsType<DeclarationPatternSyntax>(pattern);
        Assert.Equal("int", sourceText.ToString(declaration.Span));
        Assert.Equal("int", declaration.Type.ToString());
        Assert.Null(declaration.Designation);

        AssertNoErrors(tree);
    }

    [Fact]
    public void DiscardPattern_Parses()
    {
        var (pattern, tree) = ParsePattern("_");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var discard = Assert.IsType<DiscardPatternSyntax>(pattern);
        Assert.Equal("_", sourceText.ToString(discard.Span));

        AssertNoErrors(tree);
    }

    [Fact]
    public void VariablePattern_WithTypedParenthesizedDesignation_Parses()
    {
        var (pattern, tree) = ParsePattern("let (first, second): (int, string)");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var variablePattern = Assert.IsType<VariablePatternSyntax>(pattern);
        Assert.Equal("let (first, second): (int, string)", sourceText.ToString(variablePattern.Span));
        Assert.Equal("let", variablePattern.BindingKeyword.Text);

        var typedDesignation = Assert.IsType<TypedVariableDesignationSyntax>(variablePattern.Designation);
        var tupleDesignation = Assert.IsType<ParenthesizedVariableDesignationSyntax>(typedDesignation.Designation);
        Assert.Equal(2, tupleDesignation.Variables.Count);

        Assert.Collection(
            tupleDesignation.Variables,
            variable =>
            {
                var single = Assert.IsType<SingleVariableDesignationSyntax>(variable);
                Assert.Equal("first", single.Identifier.ValueText);
            },
            variable =>
            {
                var single = Assert.IsType<SingleVariableDesignationSyntax>(variable);
                Assert.Equal("second", single.Identifier.ValueText);
            });

        Assert.Equal("(int, string)", typedDesignation.TypeAnnotation.Type.ToString());

        AssertNoErrors(tree);
    }

    [Fact]
    public void UnaryPattern_WithNotKeyword_Parses()
    {
        var (pattern, tree) = ParsePattern("not let value");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var unaryPattern = Assert.IsType<UnaryPatternSyntax>(pattern);
        Assert.Equal("not let value", sourceText.ToString(unaryPattern.Span));
        Assert.Equal("not", unaryPattern.OperatorToken.Text);
        Assert.IsType<VariablePatternSyntax>(unaryPattern.Pattern);

        AssertNoErrors(tree);
    }

    [Fact]
    public void MemberPattern_WithShorthandPath_Parses()
    {
        var (pattern, tree) = ParsePattern(".Identifier(let text)");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var casePattern = Assert.IsType<MemberPatternSyntax>(pattern);
        Assert.Equal(".Identifier(let text)", sourceText.ToString(casePattern.Span));
        Assert.Null(casePattern.Path.Qualifier);
        Assert.Equal("Identifier", casePattern.Path.Identifier.ValueText);

        var argumentList = casePattern.ArgumentList;
        Assert.NotNull(argumentList);
        var argument = Assert.Single(argumentList!.Arguments);
        var payload = Assert.IsType<VariablePatternSyntax>(argument);
        var designation = Assert.IsType<SingleVariableDesignationSyntax>(payload.Designation);
        Assert.Equal("text", designation.Identifier.ValueText);

        AssertNoErrors(tree);
    }

    [Fact]
    public void NominalDeconstructionPattern_WithQualifiedTypeAndPayload_Parses()
    {
        var (pattern, tree) = ParsePattern("Token.Identifier(let text)");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var recordPattern = Assert.IsType<NominalDeconstructionPatternSyntax>(pattern);
        Assert.Equal("Token.Identifier(let text)", sourceText.ToString(recordPattern.Span));
        Assert.Equal("Token.Identifier", recordPattern.Type.ToString());

        var argument = Assert.Single(recordPattern.ArgumentList.Arguments);
        Assert.Null(argument.NameColon);
        Assert.IsType<VariablePatternSyntax>(argument.Pattern);

        AssertNoErrors(tree);
    }

    [Fact]
    public void NominalDeconstructionPattern_WithArguments_Parses()
    {
        var (pattern, tree) = ParsePattern("Person(let name, let age)");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var recordPattern = Assert.IsType<NominalDeconstructionPatternSyntax>(pattern);
        Assert.Equal("Person(let name, let age)", sourceText.ToString(recordPattern.Span));
        Assert.Equal("Person", Assert.IsType<IdentifierNameSyntax>(recordPattern.Type).Identifier.ValueText);

        var argumentList = recordPattern.ArgumentList;
        Assert.Equal(2, argumentList.Arguments.Count);
        Assert.All(argumentList.Arguments, argument =>
        {
            Assert.Null(argument.NameColon);
            Assert.IsType<VariablePatternSyntax>(argument.Pattern);
        });

        AssertNoErrors(tree);
    }

    [Fact]
    public void NominalDeconstructionPattern_WithNamedArguments_Parses()
    {
        var (pattern, tree) = ParsePattern("Person(Items: let items, Name: let name, Age: 42)");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var recordPattern = Assert.IsType<NominalDeconstructionPatternSyntax>(pattern);
        Assert.Equal("Person(Items: let items, Name: let name, Age: 42)", sourceText.ToString(recordPattern.Span));

        Assert.Collection(
            recordPattern.ArgumentList.Arguments,
            argument =>
            {
                Assert.Equal("Items", argument.NameColon?.Name.Identifier.ValueText);
                Assert.IsType<VariablePatternSyntax>(argument.Pattern);
            },
            argument =>
            {
                Assert.Equal("Name", argument.NameColon?.Name.Identifier.ValueText);
                Assert.IsType<VariablePatternSyntax>(argument.Pattern);
            },
            argument =>
            {
                Assert.Equal("Age", argument.NameColon?.Name.Identifier.ValueText);
                Assert.IsType<ConstantPatternSyntax>(argument.Pattern);
            });

        AssertNoErrors(tree);
    }

    [Fact]
    public void NominalDeconstructionPattern_WithNestedPositionalPattern_Parses()
    {
        var (pattern, tree) = ParsePattern("Foo(true, (let a, let b))");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var recordPattern = Assert.IsType<NominalDeconstructionPatternSyntax>(pattern);
        Assert.Equal("Foo(true, (let a, let b))", sourceText.ToString(recordPattern.Span));
        Assert.Equal("Foo", Assert.IsType<IdentifierNameSyntax>(recordPattern.Type).Identifier.ValueText);

        var argumentList = recordPattern.ArgumentList;
        Assert.Equal(2, argumentList.Arguments.Count);
        Assert.Null(argumentList.Arguments[1].NameColon);
        Assert.IsType<PositionalPatternSyntax>(argumentList.Arguments[1].Pattern);

        AssertNoErrors(tree);
    }

    [Fact]
    public void NominalDeconstructionPattern_WithNestedTypedRecursivePattern_Parses()
    {
        var (pattern, tree) = ParsePattern("Error(ParseIntError(let kind, _))");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var outerPattern = Assert.IsType<NominalDeconstructionPatternSyntax>(pattern);
        Assert.Equal("Error(ParseIntError(let kind, _))", sourceText.ToString(outerPattern.Span));
        Assert.Equal("Error", Assert.IsType<IdentifierNameSyntax>(outerPattern.Type).Identifier.ValueText);

        var innerPattern = Assert.IsType<NominalDeconstructionPatternSyntax>(Assert.Single(outerPattern.ArgumentList.Arguments).Pattern);
        Assert.Equal("ParseIntError", Assert.IsType<IdentifierNameSyntax>(innerPattern.Type).Identifier.ValueText);
        Assert.Equal(2, innerPattern.ArgumentList.Arguments.Count);
        Assert.IsType<VariablePatternSyntax>(innerPattern.ArgumentList.Arguments[0].Pattern);
        Assert.IsType<DiscardPatternSyntax>(innerPattern.ArgumentList.Arguments[1].Pattern);

        AssertNoErrors(tree);
    }

    [Fact]
    public void SequencePattern_WithBracketSyntax_Parses()
    {
        var (pattern, tree) = ParsePattern("[let first, _]");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var collectionPattern = Assert.IsType<SequencePatternSyntax>(pattern);
        Assert.Equal("[let first, _]", sourceText.ToString(collectionPattern.Span));
        Assert.Equal(SyntaxKind.OpenBracketToken, collectionPattern.OpenBracketToken.Kind);
        Assert.Equal(SyntaxKind.CloseBracketToken, collectionPattern.CloseBracketToken.Kind);
        Assert.Equal(2, collectionPattern.Elements.Count);
        Assert.IsType<VariablePatternSyntax>(collectionPattern.Elements[0].Pattern);
        Assert.IsType<DiscardPatternSyntax>(collectionPattern.Elements[1].Pattern);

        AssertNoErrors(tree);
    }

    [Fact]
    public void SequencePattern_WithRestElement_Parses()
    {
        var (pattern, tree) = ParsePattern("[let first, ..let rest, _]");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var collectionPattern = Assert.IsType<SequencePatternSyntax>(pattern);
        Assert.Equal("[let first, ..let rest, _]", sourceText.ToString(collectionPattern.Span));
        Assert.Equal(3, collectionPattern.Elements.Count);

        var restElement = collectionPattern.Elements[1];
        Assert.Equal(SyntaxKind.DotDotToken, restElement.Prefix.DotDotToken.Kind);

        var restPattern = Assert.IsType<VariablePatternSyntax>(restElement.Pattern);
        var restDesignation = Assert.IsType<SingleVariableDesignationSyntax>(restPattern.Designation);
        Assert.Equal("rest", restDesignation.Identifier.ValueText);

        AssertNoErrors(tree);
    }

    [Fact]
    public void SequencePattern_WithTripleDotRestElement_Parses()
    {
        var (pattern, tree) = ParsePattern("[let first, ...let rest, _]");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var collectionPattern = Assert.IsType<SequencePatternSyntax>(pattern);
        Assert.Equal("[let first, ...let rest, _]", sourceText.ToString(collectionPattern.Span));
        Assert.Equal(3, collectionPattern.Elements.Count);

        var restElement = collectionPattern.Elements[1];
        Assert.Equal(SyntaxKind.DotDotDotToken, restElement.Prefix.DotDotToken.Kind);

        var restPattern = Assert.IsType<VariablePatternSyntax>(restElement.Pattern);
        var restDesignation = Assert.IsType<SingleVariableDesignationSyntax>(restPattern.Designation);
        Assert.Equal("rest", restDesignation.Identifier.ValueText);

        AssertNoErrors(tree);
    }

    [Fact]
    public void SequencePattern_WithTrailingTripleDotDiscardRest_Parses()
    {
        var (pattern, tree) = ParsePattern("[let first, ...]");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var collectionPattern = Assert.IsType<SequencePatternSyntax>(pattern);
        Assert.Equal("[let first, ...]", sourceText.ToString(collectionPattern.Span));
        Assert.Equal(2, collectionPattern.Elements.Count);

        var restElement = collectionPattern.Elements[1];
        Assert.Equal(SyntaxKind.DotDotDotToken, restElement.Prefix.DotDotToken.Kind);

        var restPattern = Assert.IsType<DiscardPatternSyntax>(restElement.Pattern);
        Assert.True(restPattern.UnderscoreToken.IsMissing);

        AssertNoErrors(tree);
    }

    [Fact]
    public void SequencePattern_WithMiddleTripleDotDiscardRest_Parses()
    {
        var (pattern, tree) = ParsePattern("[let first, ..., let last]");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var collectionPattern = Assert.IsType<SequencePatternSyntax>(pattern);
        Assert.Equal("[let first, ..., let last]", sourceText.ToString(collectionPattern.Span));
        Assert.Equal(3, collectionPattern.Elements.Count);

        var restElement = collectionPattern.Elements[1];
        Assert.Equal(SyntaxKind.DotDotDotToken, restElement.Prefix.DotDotToken.Kind);

        var restPattern = Assert.IsType<DiscardPatternSyntax>(restElement.Pattern);
        Assert.True(restPattern.UnderscoreToken.IsMissing);

        AssertNoErrors(tree);
    }

    [Fact]
    public void SequencePattern_WithFixedSegmentElement_Parses()
    {
        var (pattern, tree) = ParsePattern("[..2 let start, let end]");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var collectionPattern = Assert.IsType<SequencePatternSyntax>(pattern);
        Assert.Equal("[..2 let start, let end]", sourceText.ToString(collectionPattern.Span));
        Assert.Equal(2, collectionPattern.Elements.Count);

        var segmentElement = collectionPattern.Elements[0];
        Assert.Equal(SyntaxKind.DotDotToken, segmentElement.Prefix.DotDotToken.Kind);
        Assert.Equal(SyntaxKind.NumericLiteralToken, segmentElement.Prefix.SegmentLengthToken.Kind);

        var segmentPattern = Assert.IsType<VariablePatternSyntax>(segmentElement.Pattern);
        var segmentDesignation = Assert.IsType<SingleVariableDesignationSyntax>(segmentPattern.Designation);
        Assert.Equal("start", segmentDesignation.Identifier.ValueText);

        AssertNoErrors(tree);
    }

    [Fact]
    public void DictionaryPattern_WithBracketSyntax_Parses()
    {
        var (pattern, tree) = ParsePattern("[\"a\": let first, \"b\": _]");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var dictionaryPattern = Assert.IsType<DictionaryPatternSyntax>(pattern);
        Assert.Equal("[\"a\": let first, \"b\": _]", sourceText.ToString(dictionaryPattern.Span));
        Assert.Equal(SyntaxKind.OpenBracketToken, dictionaryPattern.OpenBracketToken.Kind);
        Assert.Equal(SyntaxKind.CloseBracketToken, dictionaryPattern.CloseBracketToken.Kind);
        Assert.Equal(2, dictionaryPattern.Entries.Count);

        var firstEntry = dictionaryPattern.Entries[0];
        Assert.IsType<LiteralExpressionSyntax>(firstEntry.Key);
        Assert.Equal(SyntaxKind.ColonToken, firstEntry.ColonToken.Kind);
        Assert.IsType<VariablePatternSyntax>(firstEntry.Pattern);

        var secondEntry = dictionaryPattern.Entries[1];
        Assert.IsType<LiteralExpressionSyntax>(secondEntry.Key);
        Assert.IsType<DiscardPatternSyntax>(secondEntry.Pattern);

        AssertNoErrors(tree);
    }

    [Fact]
    public void SequencePattern_WithBareFixedSegmentElement_ParsesAsDiscard()
    {
        var (pattern, tree) = ParsePattern("[..2, let end]");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var collectionPattern = Assert.IsType<SequencePatternSyntax>(pattern);
        Assert.Equal("[..2, let end]", sourceText.ToString(collectionPattern.Span));
        Assert.Equal(2, collectionPattern.Elements.Count);

        var segmentElement = collectionPattern.Elements[0];
        Assert.Equal(SyntaxKind.DotDotToken, segmentElement.Prefix.DotDotToken.Kind);
        Assert.Equal(SyntaxKind.NumericLiteralToken, segmentElement.Prefix.SegmentLengthToken.Kind);

        var discardPattern = Assert.IsType<DiscardPatternSyntax>(segmentElement.Pattern);
        Assert.True(discardPattern.UnderscoreToken.IsMissing);

        AssertNoErrors(tree);
    }

    [Fact]
    public void PositionalPattern_WithExplicitBindingAndEqualityPattern_Parses()
    {
        var (pattern, tree) = ParsePattern("(let a, == existingValue)");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var positional = Assert.IsType<PositionalPatternSyntax>(pattern);
        Assert.Equal("(let a, == existingValue)", sourceText.ToString(positional.Span));

        var first = Assert.IsType<VariablePatternSyntax>(positional.Elements[0].Pattern);
        Assert.Equal(SyntaxKind.LetKeyword, first.BindingKeyword.Kind);
        var firstDesignation = Assert.IsType<SingleVariableDesignationSyntax>(first.Designation);
        Assert.Equal("a", firstDesignation.Identifier.ValueText);

        var second = Assert.IsType<ComparisonPatternSyntax>(positional.Elements[1].Pattern);
        Assert.Equal(SyntaxKind.EqualsPattern, second.Kind);
        Assert.Equal(SyntaxKind.EqualsEqualsToken, second.OperatorToken.Kind);
        var identifier = Assert.IsType<IdentifierNameSyntax>(second.Expression);
        Assert.Equal("existingValue", identifier.Identifier.ValueText);

        AssertNoErrors(tree);
    }

    [Fact]
    public void PositionalPattern_WithoutBindingKeyword_ParsesAsConstantPattern()
    {
        var (pattern, tree) = ParsePattern("(a, == existingValue)");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var positional = Assert.IsType<PositionalPatternSyntax>(pattern);
        Assert.Equal("(a, == existingValue)", sourceText.ToString(positional.Span));

        var first = Assert.IsType<ConstantPatternSyntax>(positional.Elements[0].Pattern);
        var identifier = Assert.IsType<IdentifierNameSyntax>(first.Expression);
        Assert.Equal("a", identifier.Identifier.ValueText);

        var second = Assert.IsType<ComparisonPatternSyntax>(positional.Elements[1].Pattern);
        Assert.Equal(SyntaxKind.EqualsPattern, second.Kind);

        AssertNoErrors(tree);
    }

    [Fact]
    public void PositionalPattern_WithoutBindingContext_PreservesNamedSubpattern()
    {
        var (pattern, tree) = ParsePattern("(key: string)");

        var positional = Assert.IsType<PositionalPatternSyntax>(pattern);
        var element = Assert.Single(positional.Elements);

        Assert.NotNull(element.NameColon);
        Assert.Equal("key", element.NameColon!.Name.Identifier.ValueText);
        Assert.IsType<DeclarationPatternSyntax>(element.Pattern);

        AssertNoErrors(tree);
    }

    [Fact]
    public void SequencePattern_WithExplicitBindingAndEqualityPattern_Parses()
    {
        var (pattern, tree) = ParsePattern("[let head, == sentinel, ..let tail]");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var sequence = Assert.IsType<SequencePatternSyntax>(pattern);
        Assert.Equal("[let head, == sentinel, ..let tail]", sourceText.ToString(sequence.Span));

        Assert.IsType<VariablePatternSyntax>(sequence.Elements[0].Pattern);
        var second = Assert.IsType<ComparisonPatternSyntax>(sequence.Elements[1].Pattern);
        Assert.Equal(SyntaxKind.EqualsPattern, second.Kind);
        Assert.IsType<VariablePatternSyntax>(sequence.Elements[2].Pattern);

        AssertNoErrors(tree);
    }

    [Fact]
    public void BracketPattern_WithoutBindingContext_PreservesDictionaryPattern()
    {
        var (pattern, tree) = ParsePattern("[key: string]");

        var dictionary = Assert.IsType<DictionaryPatternSyntax>(pattern);
        var entry = Assert.Single(dictionary.Entries);

        var key = Assert.IsType<IdentifierNameSyntax>(entry.Key);
        Assert.Equal("key", key.Identifier.ValueText);
        Assert.IsType<DeclarationPatternSyntax>(entry.Pattern);

        AssertNoErrors(tree);
    }

    [Fact]
    public void NominalDeconstructionPattern_WithoutBindingKeyword_ParsesBareIdentifierAsConstantPattern()
    {
        var (pattern, tree) = ParsePattern("Foo(name)");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var recordPattern = Assert.IsType<NominalDeconstructionPatternSyntax>(pattern);
        Assert.Equal("Foo(name)", sourceText.ToString(recordPattern.Span));

        var element = Assert.Single(recordPattern.ArgumentList.Arguments);
        Assert.Null(element.NameColon);
        var argument = Assert.IsType<ConstantPatternSyntax>(element.Pattern);
        var identifier = Assert.IsType<IdentifierNameSyntax>(argument.Expression);
        Assert.Equal("name", identifier.Identifier.ValueText);

        AssertNoErrors(tree);
    }

    [Fact]
    public void RangePattern_WithExclusiveUpperBound_Parses()
    {
        var (pattern, tree) = ParsePattern("2..<10");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var rangePattern = Assert.IsType<RangePatternSyntax>(pattern);
        Assert.Equal("2..<10", sourceText.ToString(rangePattern.Span));
        Assert.Equal(SyntaxKind.LessThanToken, rangePattern.LessThanToken.Kind);
        Assert.NotNull(rangePattern.LowerBound);
        Assert.NotNull(rangePattern.UpperBound);

        AssertNoErrors(tree);
    }

    [Fact]
    public void BinaryPattern_WithAndHasHigherPrecedenceThanOr()
    {
        var (pattern, tree) = ParsePattern("let left and let right or let fallback");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var orPattern = Assert.IsType<BinaryPatternSyntax>(pattern);
        Assert.Equal("let left and let right or let fallback", sourceText.ToString(orPattern.Span));
        Assert.Equal(SyntaxKind.OrPattern, orPattern.Kind);
        Assert.Equal("or", orPattern.OperatorToken.Text);

        var andPattern = Assert.IsType<BinaryPatternSyntax>(orPattern.Left);
        Assert.Equal(SyntaxKind.AndPattern, andPattern.Kind);
        Assert.Equal("and", andPattern.OperatorToken.Text);

        var left = Assert.IsType<VariablePatternSyntax>(andPattern.Left);
        var leftDesignation = Assert.IsType<SingleVariableDesignationSyntax>(left.Designation);
        Assert.Equal("left", leftDesignation.Identifier.ValueText);

        var right = Assert.IsType<VariablePatternSyntax>(andPattern.Right);
        var rightDesignation = Assert.IsType<SingleVariableDesignationSyntax>(right.Designation);
        Assert.Equal("right", rightDesignation.Identifier.ValueText);

        var fallback = Assert.IsType<VariablePatternSyntax>(orPattern.Right);
        var fallbackDesignation = Assert.IsType<SingleVariableDesignationSyntax>(fallback.Designation);
        Assert.Equal("fallback", fallbackDesignation.Identifier.ValueText);

        AssertNoErrors(tree);
    }

    [Fact]
    public void BinaryPattern_WithBarToken_ParsesAsOrPattern()
    {
        var (pattern, tree) = ParsePattern("\"Bob\" | \"bob\"");
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text.");

        var orPattern = Assert.IsType<BinaryPatternSyntax>(pattern);
        Assert.Equal("\"Bob\" | \"bob\"", sourceText.ToString(orPattern.Span));
        Assert.Equal(SyntaxKind.OrPattern, orPattern.Kind);
        Assert.Equal("|", orPattern.OperatorToken.Text);
        Assert.IsType<ConstantPatternSyntax>(orPattern.Left);
        Assert.IsType<ConstantPatternSyntax>(orPattern.Right);

        AssertNoErrors(tree);
    }

    private static (PatternSyntax Pattern, SyntaxTree Tree) ParsePattern(string patternText)
    {
        var useIsPatternWrapper =
            patternText.StartsWith("let ", StringComparison.Ordinal) ||
            patternText.StartsWith("let ", StringComparison.Ordinal) ||
            patternText.StartsWith("var ", StringComparison.Ordinal);

        var code = useIsPatternWrapper
            ? $$"""
let value: object = (1, "two")

if value is {{patternText}} {
}
"""
            : $$"""
let value: object = (1, "two")

let result = match value {
    {{patternText}} => value
    _ => value
}
""";

        var tree = SyntaxTree.ParseText(code);
        var sourceText = tree.GetText() ?? throw new InvalidOperationException("Missing source text for syntax tree.");

        PatternSyntax pattern;
        TextSpan patternSpan;

        if (useIsPatternWrapper)
        {
            var isPattern = tree.GetRoot().DescendantNodes().OfType<IsPatternExpressionSyntax>().Single();
            pattern = isPattern.Pattern;
            patternSpan = pattern.Span;
        }
        else
        {
            var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
            var arm = match.Arms.First();
            pattern = arm.Pattern;
            patternSpan = arm.BindingKeyword.Kind == SyntaxKind.None
                ? pattern.Span
                : new TextSpan(arm.BindingKeyword.SpanStart, pattern.Span.End - arm.BindingKeyword.SpanStart);
        }

        if (sourceText.ToString(patternSpan) != patternText)
            throw new InvalidOperationException($"Unable to locate pattern '{patternText}'.");

        return (pattern, tree);
    }

    private static void AssertNoErrors(SyntaxTree tree)
    {
        Assert.DoesNotContain(tree.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
