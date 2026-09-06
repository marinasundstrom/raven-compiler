using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Operations;

internal static class OperationUtilities
{
    public static ImmutableArray<IOperation>.Builder AddIfNotNull(this ImmutableArray<IOperation>.Builder builder, IOperation? operation)
    {
        if (operation is not null)
            builder.Add(operation);

        return builder;
    }

    public static ImmutableArray<IOperation> CreateChildOperations(SemanticModel semanticModel, SyntaxNode syntax)
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();

        foreach (var childSyntax in syntax.ChildNodes())
        {
            var operation = semanticModel.GetOperation(childSyntax);
            if (operation is null)
                continue;

            builder.Add(operation);
        }

        return builder.ToImmutable();
    }

    public static ImmutableArray<IOperation> CreateChildOperations(SemanticModel semanticModel, IEnumerable<SyntaxNode?> syntaxNodes)
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();

        foreach (var childSyntax in syntaxNodes)
        {
            if (childSyntax is null)
                continue;

            var operation = semanticModel.GetOperation(childSyntax);
            if (operation is null)
                continue;

            builder.Add(operation);
        }

        return builder.ToImmutable();
    }

    public static IOperation? CreateOperationFromBound(SemanticModel semanticModel, BoundNode? boundNode, SyntaxNode fallbackSyntax)
    {
        if (boundNode is null)
            return null;

        var syntax = semanticModel.GetSyntax(boundNode) ?? fallbackSyntax;
        return OperationFactory.Create(semanticModel, syntax, boundNode);
    }
}

internal sealed class BlockOperation : Operation, IBlockOperation
{
    private readonly ImmutableArray<ILocalSymbol> _locals;
    private ImmutableArray<IOperation>? _operations;

    internal BlockOperation(
        SemanticModel semanticModel,
        BoundNode bound,
        OperationKind kind,
        SyntaxNode syntax,
        ImmutableArray<ILocalSymbol> locals,
        ITypeSymbol? type,
        bool isImplicit)
        : base(semanticModel, kind, syntax, type, isImplicit)
    {
        _locals = locals.IsDefault ? ImmutableArray<ILocalSymbol>.Empty : locals;
    }

    public ImmutableArray<IOperation> Operations => _operations ??= OperationUtilities.CreateChildOperations(SemanticModel, Syntax);

    public ImmutableArray<ILocalSymbol> Locals => _locals;

    protected override ImmutableArray<IOperation> GetChildrenCore() => Operations;
}

internal sealed class ExpressionStatementOperation : Operation, IExpressionStatementOperation
{
    private readonly BoundExpressionStatement _bound;
    private IOperation? _operation;

    internal ExpressionStatementOperation(
        SemanticModel semanticModel,
        BoundExpressionStatement bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.ExpressionStatement, syntax, bound.Expression.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Operation => _operation ??= OperationUtilities.CreateOperationFromBound(
        SemanticModel,
        _bound.Expression,
        Syntax is ExpressionStatementSyntax expressionStatement
            ? expressionStatement.Expression
            : Syntax);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Operation is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Operation);
    }
}

internal sealed class FunctionOperation : Operation, IFunctionOperation
{
    internal FunctionOperation(
        SemanticModel semanticModel,
        BoundFunctionStatement bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Function, syntax, null, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return OperationUtilities.CreateChildOperations(SemanticModel, Syntax);
    }
}

internal sealed class VariableDeclarationOperation : Operation, IVariableDeclarationOperation
{
    private readonly BoundLocalDeclarationStatement _bound;
    private ImmutableArray<IVariableDeclaratorOperation>? _declarators;

    internal VariableDeclarationOperation(
        SemanticModel semanticModel,
        BoundLocalDeclarationStatement bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.LocalDeclaration, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public ImmutableArray<IVariableDeclaratorOperation> Declarators => _declarators ??= CreateDeclarators();

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateRange<IOperation>(Declarators);
    }

    private ImmutableArray<IVariableDeclaratorOperation> CreateDeclarators()
    {
        var builder = ImmutableArray.CreateBuilder<IVariableDeclaratorOperation>();
        var fallbackDeclarators = GetFallbackDeclaratorSyntaxes();

        var index = 0;
        foreach (var declarator in _bound.Declarators)
        {
            var syntax = SemanticModel.GetSyntax(declarator);
            var fallbackSyntax = index < fallbackDeclarators.Length
                ? fallbackDeclarators[index]
                : Syntax;

            var operation = syntax is not null
                ? SemanticModel.GetOperation(syntax)
                : OperationUtilities.CreateOperationFromBound(SemanticModel, declarator, fallbackSyntax);
            if (operation is IVariableDeclaratorOperation variableDeclarator)
                builder.Add(variableDeclarator);

            index++;
        }

        return builder.ToImmutable();
    }

    private ImmutableArray<SyntaxNode> GetFallbackDeclaratorSyntaxes()
    {
        var declaration = Syntax switch
        {
            LocalDeclarationStatementSyntax localDeclaration => localDeclaration.Declaration,
            UseDeclarationStatementSyntax useDeclaration => useDeclaration.Declaration,
            _ => null
        };

        return declaration?.Declarators.Cast<SyntaxNode>().ToImmutableArray()
            ?? ImmutableArray<SyntaxNode>.Empty;
    }
}

internal sealed class VariableDeclaratorOperation : Operation, IVariableDeclaratorOperation
{
    private readonly BoundVariableDeclarator _bound;
    private IVariableInitializerOperation? _initializerOperation;

    internal VariableDeclaratorOperation(
        SemanticModel semanticModel,
        BoundVariableDeclarator bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.VariableDeclarator, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public ILocalSymbol Symbol => _bound.Local;

    public ImmutableArray<IOperation> IgnoredArguments => ImmutableArray<IOperation>.Empty;

    public IVariableInitializerOperation? Initializer
    {
        get
        {
            if (_initializerOperation is not null)
                return _initializerOperation;

            if (_bound.Initializer is null)
                return null;

            var syntax = (VariableDeclaratorSyntax)Syntax;
            if (syntax.Initializer is null)
                return null;

            _initializerOperation = new VariableInitializerOperation(SemanticModel, _bound.Initializer, syntax.Initializer, isImplicit: _bound.Initializer.Reason != BoundExpressionReason.None);
            return _initializerOperation;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Initializer is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create<IOperation>(Initializer);
    }
}

internal sealed class VariableInitializerOperation : Operation, IVariableInitializerOperation
{
    private readonly BoundExpression _expression;
    private IOperation? _valueOperation;

    internal VariableInitializerOperation(
        SemanticModel semanticModel,
        BoundExpression expression,
        EqualsValueClauseSyntax syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.VariableDeclarator, syntax, expression.Type, isImplicit)
    {
        _expression = expression;
    }

    public IOperation? Value => _valueOperation ??= SemanticModel.GetOperation(((EqualsValueClauseSyntax)Syntax).Value);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Value is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Value);
    }
}

internal sealed class ReturnOperation : Operation, IReturnOperation
{
    private readonly BoundReturnStatement _bound;
    private IOperation? _returnedValue;

    internal ReturnOperation(
        SemanticModel semanticModel,
        BoundReturnStatement bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Return, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? ReturnedValue => _returnedValue ??= ((ReturnStatementSyntax)Syntax).Expression is { } expression ? SemanticModel.GetOperation(expression) : null;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ReturnedValue is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(ReturnedValue);
    }
}

internal sealed class ReturnExpressionOperation : Operation, IReturnOperation
{
    private IOperation? _returnedValue;

    internal ReturnExpressionOperation(
        SemanticModel semanticModel,
        BoundReturnExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.ReturnExpression, syntax, bound.Type, isImplicit)
    {
    }

    public IOperation? ReturnedValue => _returnedValue ??= Syntax switch
    {
        ReturnExpressionSyntax { Expression: { } expression } => SemanticModel.GetOperation(expression),
        MacroExpansionExpressionSyntax macroExpansion => SemanticModel.GetOperation(macroExpansion.Expression),
        _ => null
    };

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ReturnedValue is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(ReturnedValue);
    }
}

internal sealed class BreakExpressionOperation : Operation, IBreakOperation
{
    private readonly BoundBreakExpression _bound;

    internal BreakExpressionOperation(
        SemanticModel semanticModel,
        BoundBreakExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.BreakExpression, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public ILabelSymbol? TargetLabel => _bound.TargetLabel;

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class ContinueExpressionOperation : Operation, IContinueOperation
{
    private readonly BoundContinueExpression _bound;

    internal ContinueExpressionOperation(
        SemanticModel semanticModel,
        BoundContinueExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.ContinueExpression, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public ILabelSymbol? TargetLabel => _bound.TargetLabel;

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class YieldExpressionOperation : Operation, IYieldOperation
{
    private readonly BoundYieldExpression _bound;
    private IOperation? _returnedValue;

    internal YieldExpressionOperation(
        SemanticModel semanticModel,
        BoundYieldExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.YieldExpression, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? ReturnedValue => _returnedValue ??=
        SemanticModel.GetOperation(((YieldExpressionSyntax)Syntax).Expression);

    public bool IsDelegating => _bound.Iteration is not null;

    public ITypeSymbol ElementType => _bound.ElementType;

    protected override ImmutableArray<IOperation> GetChildrenCore()
        => ReturnedValue is null ? ImmutableArray<IOperation>.Empty : ImmutableArray.Create(ReturnedValue);
}

internal sealed class YieldOperation : Operation, IYieldOperation
{
    private readonly BoundYieldStatement _bound;
    private IOperation? _returnedValue;

    internal YieldOperation(
        SemanticModel semanticModel,
        BoundYieldStatement bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Yield, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? ReturnedValue => _returnedValue ??= Syntax switch
    {
        YieldStatementSyntax yieldStatement when yieldStatement.Expression is { } expression => SemanticModel.GetOperation(expression),
        _ => null,
    };

    public bool IsDelegating => _bound.Iteration is not null;

    public ITypeSymbol ElementType => _bound.ElementType;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ReturnedValue is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(ReturnedValue);
    }
}

internal sealed class ThrowOperation : Operation, IThrowOperation
{
    private readonly BoundThrowStatement _bound;
    private IOperation? _exception;

    internal ThrowOperation(
        SemanticModel semanticModel,
        BoundThrowStatement bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Throw, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Exception => _exception ??= SemanticModel.GetOperation(((ThrowStatementSyntax)Syntax).Expression);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Exception is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Exception);
    }
}

internal sealed class ThrowExpressionOperation : Operation, IThrowOperation
{
    private IOperation? _exception;

    internal ThrowExpressionOperation(
        SemanticModel semanticModel,
        BoundThrowExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.ThrowExpression, syntax, bound.Type, isImplicit)
    {
    }

    public IOperation? Exception => _exception ??= SemanticModel.GetOperation(((ThrowExpressionSyntax)Syntax).Expression);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Exception is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Exception);
    }
}

internal sealed class LiteralOperation : Operation, ILiteralOperation
{
    private readonly BoundLiteralExpression _bound;

    internal LiteralOperation(
        SemanticModel semanticModel,
        BoundLiteralExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Literal, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public object Value => _bound.Value;

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class InterpolatedStringOperation : Operation, IInterpolatedStringOperation
{
    private ImmutableArray<IInterpolatedStringContentOperation>? _contents;

    internal InterpolatedStringOperation(
        SemanticModel semanticModel,
        BoundExpression bound,
        InterpolatedStringExpressionSyntax syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.InterpolatedString, syntax, bound.Type, isImplicit)
    {
    }

    public ImmutableArray<IInterpolatedStringContentOperation> Contents
        => _contents ??= OperationUtilities.CreateChildOperations(SemanticModel, ((InterpolatedStringExpressionSyntax)Syntax).Contents)
            .OfType<IInterpolatedStringContentOperation>()
            .ToImmutableArray();

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Contents.IsDefaultOrEmpty
            ? ImmutableArray<IOperation>.Empty
            : Contents.Cast<IOperation>().ToImmutableArray();
    }
}

internal sealed class InterpolatedStringTextOperation : Operation, IInterpolatedStringTextOperation
{
    internal InterpolatedStringTextOperation(
        SemanticModel semanticModel,
        InterpolatedStringTextSyntax syntax,
        bool isImplicit)
        : base(
            semanticModel,
            OperationKind.InterpolatedStringText,
            syntax,
            semanticModel.Compilation.GetSpecialType(SpecialType.System_String),
            isImplicit)
    {
    }

    public string Text => ((InterpolatedStringTextSyntax)Syntax).Token.ValueText ?? string.Empty;

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class InterpolationOperation : Operation, IInterpolationOperation
{
    private IOperation? _expression;

    internal InterpolationOperation(
        SemanticModel semanticModel,
        InterpolationSyntax syntax,
        bool isImplicit)
        : base(
            semanticModel,
            OperationKind.Interpolation,
            syntax,
            semanticModel.GetBoundNode(syntax.Expression).Type,
            isImplicit)
    {
    }

    public IOperation? Expression => _expression ??= SemanticModel.GetOperation(((InterpolationSyntax)Syntax).Expression);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Expression is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Expression);
    }
}

internal sealed class DefaultValueOperation : Operation, IDefaultValueOperation
{
    internal DefaultValueOperation(
        SemanticModel semanticModel,
        BoundDefaultValueExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.DefaultValue, syntax, bound.Type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class BreakOperation : Operation, IBreakOperation
{
    private readonly BoundBreakStatement _bound;

    internal BreakOperation(SemanticModel semanticModel, BoundBreakStatement bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Break, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public ILabelSymbol? TargetLabel => _bound.TargetLabel;

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class ContinueOperation : Operation, IContinueOperation
{
    private readonly BoundContinueStatement _bound;

    internal ContinueOperation(SemanticModel semanticModel, BoundContinueStatement bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Continue, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public ILabelSymbol? TargetLabel => _bound.TargetLabel;

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class GotoOperation : Operation, IGotoOperation
{
    private readonly BoundGotoStatement _bound;

    internal GotoOperation(SemanticModel semanticModel, BoundGotoStatement bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Goto, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public ILabelSymbol Target => _bound.Target;

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class ConditionalGotoOperation : Operation, IConditionalGotoOperation
{
    private readonly BoundConditionalGotoStatement _bound;
    private IOperation? _condition;

    internal ConditionalGotoOperation(SemanticModel semanticModel, BoundConditionalGotoStatement bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.ConditionalGoto, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public ILabelSymbol Target => _bound.Target;

    public bool JumpIfTrue => _bound.JumpIfTrue;

    public IOperation? Condition
    {
        get
        {
            if (_condition is not null)
                return _condition;

            var conditionSyntax = SemanticModel.GetSyntax(_bound.Condition);
            _condition = conditionSyntax is null ? null : SemanticModel.GetOperation(conditionSyntax);

            return _condition;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Condition is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Condition);
    }
}

internal sealed class LabeledOperation : Operation, ILabeledOperation
{
    private readonly BoundLabeledStatement _bound;

    internal LabeledOperation(SemanticModel semanticModel, BoundLabeledStatement bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Labeled, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public ILabelSymbol Label => _bound.Label;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return OperationUtilities.CreateChildOperations(SemanticModel, Syntax);
    }
}

public abstract class SymbolReferenceOperation<TSymbol> : Operation, ISymbolReferenceOperation<TSymbol> where TSymbol : ISymbol
{
    internal SymbolReferenceOperation(
        SemanticModel semanticModel,
        OperationKind kind,
        SyntaxNode syntax,
        TSymbol symbol,
        ITypeSymbol? type,
        bool isImplicit)
        : base(semanticModel, kind, syntax, type, isImplicit)
    {
        Symbol = symbol;
    }

    public TSymbol Symbol { get; }

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class LocalReferenceOperation : SymbolReferenceOperation<ILocalSymbol>, ILocalReferenceOperation
{
    internal LocalReferenceOperation(SemanticModel semanticModel, BoundLocalAccess bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.LocalReference, syntax, bound.Local, bound.Type, isImplicit)
    {
    }

    public ILocalSymbol Local => Symbol;
}

internal sealed class VariableReferenceOperation : SymbolReferenceOperation<ILocalSymbol>, IVariableReferenceOperation
{
    internal VariableReferenceOperation(SemanticModel semanticModel, BoundVariableExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.VariableReference, syntax, bound.Variable, bound.Type, isImplicit)
    {
    }

    public ILocalSymbol Variable => Symbol;
}

internal sealed class ParameterReferenceOperation : SymbolReferenceOperation<IParameterSymbol>, IParameterReferenceOperation
{
    internal ParameterReferenceOperation(SemanticModel semanticModel, BoundParameterAccess bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.ParameterReference, syntax, bound.Parameter, bound.Type, isImplicit)
    {
    }

    public IParameterSymbol Parameter => Symbol;
}

internal sealed class FieldReferenceOperation : SymbolReferenceOperation<IFieldSymbol>, IFieldReferenceOperation
{
    internal FieldReferenceOperation(SemanticModel semanticModel, BoundFieldAccess bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.FieldReference, syntax, bound.Field, bound.Type, isImplicit)
    {
    }

    public IFieldSymbol Field => Symbol;
}

internal sealed class PropertyReferenceOperation : SymbolReferenceOperation<IPropertySymbol>, IPropertyReferenceOperation
{
    internal PropertyReferenceOperation(SemanticModel semanticModel, BoundPropertyAccess bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.PropertyReference, syntax, bound.Property, bound.Type, isImplicit)
    {
    }

    public IPropertySymbol Property => Symbol;
}

internal sealed class MethodReferenceOperation : SymbolReferenceOperation<IMethodSymbol>, IMethodReferenceOperation
{
    internal MethodReferenceOperation(SemanticModel semanticModel, BoundMethodGroupExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(
            semanticModel,
            OperationKind.MethodReference,
            syntax,
            bound.SelectedMethod ?? bound.Methods[0],
            bound.Type,
            isImplicit)
    {
    }

    public IMethodSymbol Method => Symbol;
}

internal sealed class MemberReferenceOperation : SymbolReferenceOperation<ISymbol>, IMemberReferenceOperation
{
    internal MemberReferenceOperation(
        SemanticModel semanticModel,
        BoundMemberAccessExpression bound,
        OperationKind kind,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, kind, syntax, bound.Member, bound.Type, isImplicit)
    {
    }

    internal MemberReferenceOperation(
        SemanticModel semanticModel,
        BoundPointerMemberAccessExpression bound,
        OperationKind kind,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, kind, syntax, bound.Member, bound.Type, isImplicit)
    {
    }
}

internal sealed class UnaryOperation : Operation, IUnaryOperation
{
    private readonly BoundUnaryExpression _bound;
    private IOperation? _operand;

    internal UnaryOperation(
        SemanticModel semanticModel,
        BoundUnaryExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Unary, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    internal BoundUnaryOperator Operator => _bound.Operator;

    public IOperation? Operand => _operand ??= SemanticModel.GetOperation(Syntax.ChildNodes().OfType<ExpressionSyntax>().FirstOrDefault());

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Operand is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Operand);
    }
}

internal sealed class BinaryOperation : Operation, IBinaryOperation
{
    private readonly BoundBinaryExpression _bound;
    private IOperation? _left;
    private IOperation? _right;

    internal BinaryOperation(
        SemanticModel semanticModel,
        BoundBinaryExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Binary, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    internal BoundBinaryOperator Operator => _bound.Operator;

    public IOperation? Left => _left ??= SemanticModel.GetOperation(((InfixOperatorExpressionSyntax)Syntax).Left);

    public IOperation? Right => _right ??= SemanticModel.GetOperation(((InfixOperatorExpressionSyntax)Syntax).Right);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Left)
            .AddIfNotNull(Right)
            .ToImmutable();
    }
}

internal sealed class CoalesceOperation : Operation, ICoalesceOperation
{
    private IOperation? _left;
    private IOperation? _right;

    internal CoalesceOperation(
        SemanticModel semanticModel,
        BoundNullCoalesceExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Coalesce, syntax, bound.Type, isImplicit)
    {
    }

    public IOperation? Left => _left ??= SemanticModel.GetOperation(((NullCoalesceExpressionSyntax)Syntax).Left);

    public IOperation? Right => _right ??= SemanticModel.GetOperation(((NullCoalesceExpressionSyntax)Syntax).Right);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Left)
            .AddIfNotNull(Right)
            .ToImmutable();
    }
}

internal sealed class NameOfOperation : Operation, INameOfOperation
{
    private readonly BoundNameOfExpression _bound;
    private IOperation? _operand;

    internal NameOfOperation(
        SemanticModel semanticModel,
        BoundNameOfExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.NameOf, syntax, bound.StringType, isImplicit)
    {
        _bound = bound;
    }

    public string Name => _bound.Name;

    public IOperation? Operand => _operand ??= SemanticModel.GetOperation(((NameOfExpressionSyntax)Syntax).Operand);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Operand is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Operand);
    }
}

internal sealed class ParenthesizedOperation : Operation, IParenthesizedOperation
{
    private IOperation? _operation;

    internal ParenthesizedOperation(
        SemanticModel semanticModel,
        BoundParenthesizedExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Parenthesized, syntax, bound.Type, isImplicit)
    {
    }

    public IOperation? Operand => _operation ??= SemanticModel.GetOperation(((ParenthesizedExpressionSyntax)Syntax).Expression);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Operand is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Operand);
    }
}

internal sealed class ConversionOperation : Operation, IConversionOperation
{
    private readonly BoundExpression _bound;
    private readonly Conversion _conversion;
    private IOperation? _operand;

    internal ConversionOperation(
        SemanticModel semanticModel,
        BoundExpression bound,
        Conversion conversion,
        SyntaxNode syntax,
        ITypeSymbol type,
        bool isImplicit)
        : base(semanticModel, OperationKind.Conversion, syntax, type, isImplicit)
    {
        _bound = bound;
        _conversion = conversion;
    }

    public Conversion Conversion => _conversion;

    public IOperation? Operand
    {
        get
        {
            if (_operand is not null)
                return _operand;

            if (Syntax is CastExpressionSyntax cast)
                _operand = SemanticModel.GetOperation(cast.Expression);
            else if (Syntax is AsExpressionSyntax asExpression)
                _operand = SemanticModel.GetOperation(asExpression.Expression);
            else
                _operand = _bound switch
                {
                    BoundConversionExpression conversionExpression => OperationUtilities.CreateOperationFromBound(SemanticModel, conversionExpression.Expression, Syntax),
                    BoundAsExpression boundAsExpression => OperationUtilities.CreateOperationFromBound(SemanticModel, boundAsExpression.Expression, Syntax),
                    _ => null
                };

            return _operand;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Operand is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Operand);
    }
}

internal sealed class PropagationOperation : Operation, IPropagationOperation
{
    private readonly BoundPropagateExpression _bound;
    private IOperation? _operand;
    private bool _operandInitialized;

    internal PropagationOperation(
        SemanticModel semanticModel,
        BoundPropagateExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Propagate, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public ITypeSymbol OkType => _bound.OkType;

    public ITypeSymbol? ErrorType => _bound.ErrorType;

    public INamedTypeSymbol EnclosingResultType => _bound.EnclosingResultType;

    public IMethodSymbol EnclosingErrorConstructor => _bound.EnclosingErrorConstructor;

    public IOperation? Operand
    {
        get
        {
            if (_operandInitialized)
                return _operand;

            _operandInitialized = true;
            var fallback = Syntax is PropagateExpressionSyntax propagateExpression ? propagateExpression.Expression : Syntax;
            _operand = OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.Operand, fallback);
            return _operand;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Operand is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Operand);
    }
}

internal sealed class DereferenceOperation : Operation, IDereferenceOperation
{
    private readonly BoundDereferenceExpression _bound;
    private IOperation? _operand;
    private bool _operandInitialized;

    internal DereferenceOperation(
        SemanticModel semanticModel,
        BoundDereferenceExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Dereference, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Operand
    {
        get
        {
            if (_operandInitialized)
                return _operand;

            _operandInitialized = true;
            var fallback = Syntax is PrefixOperatorExpressionSyntax prefix ? prefix.Expression : Syntax;
            _operand = OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.Reference, fallback);
            return _operand;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Operand is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Operand);
    }
}

internal sealed class ConditionalOperation : Operation, IConditionalOperation
{
    private readonly BoundNode _bound;
    private IOperation? _condition;
    private IOperation? _whenTrue;
    private IOperation? _whenFalse;

    internal ConditionalOperation(SemanticModel semanticModel, BoundNode bound, SyntaxNode syntax, ITypeSymbol? type, bool isImplicit)
        : base(semanticModel, OperationKind.Conditional, syntax, type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Condition
    {
        get
        {
            if (_condition is not null)
                return _condition;

            _condition = Syntax switch
            {
                IfStatementSyntax ifStatement => SemanticModel.GetOperation(ifStatement.Condition),
                IfPatternStatementSyntax ifPatternStatement when _bound is BoundIfStatement boundIf
                    => OperationUtilities.CreateOperationFromBound(SemanticModel, boundIf.Condition, ifPatternStatement.Expression),
                IfExpressionSyntax ifExpression => SemanticModel.GetOperation(ifExpression.Condition),
                IfPatternExpressionSyntax ifPatternExpression when _bound is BoundIfExpression boundIf
                    => OperationUtilities.CreateOperationFromBound(SemanticModel, boundIf.Condition, ifPatternExpression.Value),
                _ => null
            };

            return _condition;
        }
    }

    public IOperation? WhenTrue
    {
        get
        {
            if (_whenTrue is not null)
                return _whenTrue;

            _whenTrue = Syntax switch
            {
                IfStatementSyntax ifStatement => SemanticModel.GetOperation(ifStatement.ThenStatement),
                IfPatternStatementSyntax ifPatternStatement => SemanticModel.GetOperation(ifPatternStatement.ThenStatement),
                IfExpressionSyntax ifExpression => SemanticModel.GetOperation(ifExpression.Expression),
                IfPatternExpressionSyntax ifPatternExpression => SemanticModel.GetOperation(ifPatternExpression.Expression),
                _ => null
            };

            return _whenTrue;
        }
    }

    public IOperation? WhenFalse
    {
        get
        {
            if (_whenFalse is not null)
                return _whenFalse;

            _whenFalse = Syntax switch
            {
                IfStatementSyntax ifStatement => ifStatement.ElseClause is { } elseClause
                    ? SemanticModel.GetOperation(elseClause.Statement)
                    : null,
                IfPatternStatementSyntax ifPatternStatement => ifPatternStatement.ElseClause is { } elseClause
                    ? SemanticModel.GetOperation(elseClause.Statement)
                    : null,
                IfExpressionSyntax ifExpression => ifExpression.ElseClause is { } elseClause
                    ? SemanticModel.GetOperation(elseClause.Expression)
                    : null,
                IfPatternExpressionSyntax ifPatternExpression => ifPatternExpression.ElseClause is { } elseClause
                    ? SemanticModel.GetOperation(elseClause.Expression)
                    : null,
                _ => null
            };

            return _whenFalse;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Condition)
            .AddIfNotNull(WhenTrue)
            .AddIfNotNull(WhenFalse)
            .ToImmutable();
    }
}

internal sealed class ConditionalAccessOperation : Operation, IConditionalAccessOperation
{
    private readonly BoundExpression _bound;
    private IOperation? _receiver;
    private IOperation? _whenNotNull;

    internal ConditionalAccessOperation(SemanticModel semanticModel, BoundExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.ConditionalAccess, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Receiver
    {
        get
        {
            if (_receiver is not null)
                return _receiver;

            if (_bound is BoundConditionalAccessExpression conditionalAccess)
            {
                _receiver = CreateOperationForBoundExpression(conditionalAccess.Receiver);
            }
            else if (_bound is BoundCarrierConditionalAccessExpression carrierConditionalAccess)
            {
                _receiver = CreateOperationForBoundExpression(carrierConditionalAccess.Receiver);
            }
            else if (Syntax is ConditionalAccessExpressionSyntax conditionalSyntax)
            {
                _receiver = SemanticModel.GetOperation(conditionalSyntax.Expression);
            }
            else if (Syntax is TryExpressionSyntax trySyntax)
            {
                _receiver = SemanticModel.GetOperation(trySyntax.Expression);
            }

            return _receiver;
        }
    }

    public IOperation? WhenNotNull
    {
        get
        {
            if (_whenNotNull is not null)
                return _whenNotNull;

            if (_bound is BoundConditionalAccessExpression conditionalAccess)
            {
                _whenNotNull = CreateOperationForBoundExpression(conditionalAccess.WhenNotNull);
            }
            else if (_bound is BoundCarrierConditionalAccessExpression carrierConditionalAccess)
            {
                _whenNotNull = CreateOperationForBoundExpression(carrierConditionalAccess.WhenPresent);
            }
            else if (Syntax is ConditionalAccessExpressionSyntax conditionalSyntax)
            {
                _whenNotNull = SemanticModel.GetOperation(conditionalSyntax.WhenNotNull);
            }

            return _whenNotNull;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Receiver)
            .AddIfNotNull(WhenNotNull)
            .ToImmutable();
    }

    private IOperation? CreateOperationForBoundExpression(BoundExpression expression)
    {
        var syntax = SemanticModel.GetSyntax(expression) ?? Syntax;
        if (_bound is BoundCarrierConditionalAccessExpression && expression is BoundTryExpression tryExpression && Syntax is TryExpressionSyntax trySyntax)
        {
            // Preserve the semantic nesting for `try?`: conditional access over a try-expression receiver.
            return new TryExpressionOperation(SemanticModel, tryExpression, trySyntax, isImplicit: true);
        }

        return OperationFactory.Create(SemanticModel, syntax, expression);
    }
}

internal sealed class AwaitOperation : Operation, IAwaitOperation
{
    private readonly BoundAwaitExpression _bound;
    private IOperation? _operation;

    internal AwaitOperation(
        SemanticModel semanticModel,
        BoundAwaitExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Await, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Operation => _operation ??= SemanticModel.GetOperation(((PrefixOperatorExpressionSyntax)Syntax).Expression);

    public IMethodSymbol GetAwaiterMethod => _bound.GetAwaiterMethod;

    public IMethodSymbol GetResultMethod => _bound.GetResultMethod;

    public IPropertySymbol IsCompletedProperty => _bound.IsCompletedProperty;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Operation is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Operation);
    }
}

internal sealed class ArgumentOperation : Operation, IArgumentOperation
{
    private IOperation? _value;
    private string? _name;

    internal ArgumentOperation(
        SemanticModel semanticModel,
        BoundExpression bound,
        ArgumentSyntax syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Argument, syntax, bound.Type, isImplicit)
    {
    }

    public IOperation? Value => _value ??= SemanticModel.GetOperation(((ArgumentSyntax)Syntax).Expression);

    public string? Name => _name ??= ((ArgumentSyntax)Syntax).NameColon is { Name: IdentifierNameSyntax identifier }
        ? identifier.Identifier.ValueText
        : null;

    public bool IsNamed => Name is not null;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Value is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Value);
    }
}

internal sealed class InvocationOperation : Operation, IInvocationOperation
{
    private readonly BoundInvocationExpression _bound;
    private ImmutableArray<IOperation>? _arguments;
    private IOperation? _instance;

    internal InvocationOperation(
        SemanticModel semanticModel,
        BoundInvocationExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Invocation, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IMethodSymbol TargetMethod => _bound.Method;

    public IOperation? Instance => _instance ??= (Syntax as InvocationExpressionSyntax)?.Expression is { } expr ? SemanticModel.GetOperation(expr) : null;

    public ImmutableArray<IOperation> Arguments
    {
        get
        {
            if (_arguments.HasValue)
                return _arguments.Value;

            if (Syntax is InvocationExpressionSyntax invocation)
            {
                _arguments = OperationUtilities.CreateChildOperations(SemanticModel, invocation.ArgumentList.Arguments);
            }
            else
            {
                _arguments = ImmutableArray<IOperation>.Empty;
            }

            return _arguments.Value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();

        if (Instance is not null)
            builder.Add(Instance);

        builder.AddRange(Arguments);
        return builder.ToImmutable();
    }
}

internal sealed class DelegateCreationOperation : Operation, IDelegateCreationOperation
{
    internal DelegateCreationOperation(SemanticModel semanticModel, BoundDelegateCreationExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.DelegateCreation, syntax, bound.Type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => OperationUtilities.CreateChildOperations(SemanticModel, Syntax);
}

internal sealed class AddressOfOperation : Operation, IAddressOfOperation
{
    internal AddressOfOperation(SemanticModel semanticModel, BoundAddressOfExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.AddressOf, syntax, bound.Type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => OperationUtilities.CreateChildOperations(SemanticModel, Syntax);
}

internal sealed class StackAllocOperation : Operation, IStackAllocOperation
{
    private readonly StackAllocExpressionSyntax _syntax;
    private IOperation? _count;

    internal StackAllocOperation(
        SemanticModel semanticModel,
        BoundStackAllocExpression bound,
        StackAllocExpressionSyntax syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.StackAlloc, syntax, bound.Type, isImplicit)
    {
        _syntax = syntax;
        ElementType = bound.ElementType;
    }

    public ITypeSymbol ElementType { get; }

    public IOperation Count => _count ??= SemanticModel.GetOperation(_syntax.Count)
        ?? throw new InvalidOperationException("Stack allocation count operation is unavailable.");

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray.Create(Count);
}

internal sealed class ElementAccessOperation : Operation, IElementAccessOperation
{
    private readonly BoundExpression _bound;
    private IOperation? _instance;
    private ImmutableArray<IOperation>? _arguments;

    internal ElementAccessOperation(SemanticModel semanticModel, BoundExpression bound, OperationKind kind, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, kind, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Instance
    {
        get
        {
            if (_instance is not null)
                return _instance;

            if (Syntax is ElementAccessExpressionSyntax elementAccess)
            {
                _instance = SemanticModel.GetOperation(elementAccess.Expression);
            }
            else if (_bound is BoundArrayAccessExpression arrayAccess)
            {
                var receiverSyntax = SemanticModel.GetSyntax(arrayAccess.Receiver);
                if (receiverSyntax is not null)
                    _instance = SemanticModel.GetOperation(receiverSyntax);
            }
            else if (_bound is BoundIndexerAccessExpression indexerAccess)
            {
                var receiverSyntax = SemanticModel.GetSyntax(indexerAccess.Receiver);
                if (receiverSyntax is not null)
                    _instance = SemanticModel.GetOperation(receiverSyntax);
            }

            return _instance;
        }
    }

    public ImmutableArray<IOperation> Arguments
    {
        get
        {
            if (_arguments.HasValue)
                return _arguments.Value;

            _arguments = Syntax is ElementAccessExpressionSyntax elementAccess
                ? OperationUtilities.CreateChildOperations(SemanticModel, elementAccess.ArgumentList.Arguments)
                : ImmutableArray<IOperation>.Empty;

            return _arguments.Value;
        }
    }

    public IPropertySymbol? Indexer => _bound is BoundIndexerAccessExpression indexerAccess
        ? indexerAccess.Indexer
        : null;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();
        builder.AddIfNotNull(Instance);
        builder.AddRange(Arguments);
        return builder.ToImmutable();
    }
}

internal sealed class IndexOperation : Operation, IIndexOperation
{
    private readonly BoundIndexExpression _bound;
    private IOperation? _value;

    internal IndexOperation(SemanticModel semanticModel, BoundIndexExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Index, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public bool IsFromEnd => _bound.IsFromEnd;

    public IOperation? Value => _value ??= SemanticModel.GetOperation(((IndexExpressionSyntax)Syntax).Expression);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Value is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Value);
    }
}

internal sealed class RangeOperation : Operation, IRangeOperation
{
    private IOperation? _left;
    private IOperation? _right;

    internal RangeOperation(SemanticModel semanticModel, BoundRangeExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Range, syntax, bound.Type, isImplicit)
    {
    }

    public IOperation? Left => _left ??= ((RangeExpressionSyntax)Syntax).LeftExpression is { } left ? SemanticModel.GetOperation(left) : null;

    public IOperation? Right => _right ??= ((RangeExpressionSyntax)Syntax).RightExpression is { } right ? SemanticModel.GetOperation(right) : null;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();

        builder.AddIfNotNull(Left);
        builder.AddIfNotNull(Right);

        return builder.ToImmutable();
    }
}

internal sealed class TypeOfOperation : Operation, ITypeOfOperation
{
    internal TypeOfOperation(SemanticModel semanticModel, BoundTypeOfExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.TypeOf, syntax, bound.Type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => OperationUtilities.CreateChildOperations(SemanticModel, Syntax);
}

internal sealed class ObjectCreationOperation : Operation, IObjectCreationOperation
{
    private readonly BoundObjectCreationExpression _bound;
    private ImmutableArray<IOperation>? _arguments;
    private IOperation? _initializer;
    private bool _initializerInitialized;

    internal ObjectCreationOperation(
        SemanticModel semanticModel,
        BoundObjectCreationExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.ObjectCreation, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IMethodSymbol? Constructor => _bound.Constructor;

    public ImmutableArray<IOperation> Arguments => _arguments ??= Syntax is InvocationExpressionSyntax creation
        ? OperationUtilities.CreateChildOperations(SemanticModel, creation.ArgumentList.Arguments)
        : ImmutableArray<IOperation>.Empty;

    public IOperation? Initializer
    {
        get
        {
            if (_initializerInitialized)
                return _initializer;

            _initializerInitialized = true;

            SyntaxNode? syntaxInitializer = Syntax switch
            {
                InvocationExpressionSyntax invocation => invocation.Initializer,
                WithExpressionSyntax withExpression => withExpression,
                _ => null
            };

            if (_bound.Initializer is null || syntaxInitializer is null)
                return null;

            _initializer = OperationFactory.Create(SemanticModel, syntaxInitializer, _bound.Initializer);
            return _initializer;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();
        builder.AddRange(Arguments);
        builder.AddIfNotNull(Initializer);
        return builder.ToImmutable();
    }
}

internal sealed class ObjectInitializerOperation : Operation, IObjectInitializerOperation
{
    private readonly BoundObjectInitializer _bound;
    private ImmutableArray<IOperation>? _entries;

    internal ObjectInitializerOperation(
        SemanticModel semanticModel,
        BoundObjectInitializer bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.ObjectInitializer, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public ImmutableArray<IOperation> Entries
    {
        get
        {
            if (_entries.HasValue)
                return _entries.Value;

            var syntaxEntries = Syntax switch
            {
                WithExpressionSyntax withExpression => withExpression.Entries.ToArray<SyntaxNode>(),
                _ => []
            };
            var boundEntries = _bound.Entries.ToArray();
            var builder = ImmutableArray.CreateBuilder<IOperation>();

            if (boundEntries.Length == syntaxEntries.Length)
            {
                for (var i = 0; i < boundEntries.Length; i++)
                {
                    builder.AddIfNotNull(OperationFactory.Create(SemanticModel, syntaxEntries[i], boundEntries[i]));
                }
            }
            else
            {
                foreach (var boundEntry in boundEntries)
                {
                    var syntax = SemanticModel.GetSyntax(boundEntry) ?? Syntax;
                    builder.AddIfNotNull(OperationFactory.Create(SemanticModel, syntax, boundEntry));
                }
            }

            _entries = builder.ToImmutable();
            return _entries.Value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => Entries;
}

internal sealed class ObjectInitializerAssignmentOperation : Operation, IObjectInitializerAssignmentOperation
{
    private readonly BoundObjectInitializerAssignmentEntry _bound;
    private IOperation? _value;
    private bool _valueInitialized;

    internal ObjectInitializerAssignmentOperation(
        SemanticModel semanticModel,
        BoundObjectInitializerAssignmentEntry bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.ObjectInitializerAssignment, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public ISymbol Member => _bound.Member;

    public SyntaxKind OperatorTokenKind => _bound.OperatorTokenKind;

    public IOperation? Value
    {
        get
        {
            if (_valueInitialized)
                return _value;

            _valueInitialized = true;
            var fallback = Syntax switch
            {
                ObjectInitializerAssignmentEntrySyntax assignmentEntry => assignmentEntry.Expression,
                WithAssignmentSyntax withAssignment => withAssignment.Expression,
                WithExpressionEntrySyntax withExpressionEntry => withExpressionEntry.Expression,
                _ => Syntax
            };
            _value = OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.Value, fallback);
            return _value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Value is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Value);
    }
}

internal sealed class ObjectInitializerExpressionEntryOperation : Operation, IObjectInitializerExpressionEntryOperation
{
    private readonly BoundObjectInitializerExpressionEntry _bound;
    private IOperation? _expression;
    private bool _expressionInitialized;

    internal ObjectInitializerExpressionEntryOperation(
        SemanticModel semanticModel,
        BoundObjectInitializerExpressionEntry bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.ObjectInitializerExpressionEntry, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Expression
    {
        get
        {
            if (_expressionInitialized)
                return _expression;

            _expressionInitialized = true;
            var fallback = Syntax switch
            {
                ObjectInitializerExpressionEntrySyntax expressionEntry => expressionEntry.Expression,
                WithExpressionEntrySyntax withExpressionEntry => withExpressionEntry.Expression,
                _ => Syntax
            };
            _expression = OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.Expression, fallback);
            return _expression;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Expression is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Expression);
    }
}

internal sealed class WithOperation : Operation, IWithOperation
{
    private readonly BoundWithExpression _bound;
    private IOperation? _receiver;
    private ImmutableArray<IOperation>? _values;

    internal WithOperation(
        SemanticModel semanticModel,
        BoundWithExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.With, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Receiver => _receiver ??= SemanticModel.GetOperation(((WithExpressionSyntax)Syntax).Expression);

    public ImmutableArray<ISymbol> Members => _bound.Assignments.Select(assignment => assignment.Member).ToImmutableArray();

    public ImmutableArray<IOperation> Values => _values ??= OperationUtilities.CreateChildOperations(
        SemanticModel,
        ((WithExpressionSyntax)Syntax).Entries.OfType<WithAssignmentSyntax>().Select(assignment => assignment.Expression));

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();
        builder.AddIfNotNull(Receiver);
        builder.AddRange(Values);
        return builder.ToImmutable();
    }
}

internal sealed class AssignmentOperation : Operation, IAssignmentOperation
{
    private readonly BoundExpression _bound;
    private IOperation? _target;
    private IOperation? _value;

    internal AssignmentOperation(
        SemanticModel semanticModel,
        BoundExpression bound,
        SyntaxNode syntax,
        ITypeSymbol? type,
        bool isImplicit)
        : base(semanticModel, OperationKind.Assignment, syntax, type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Target
    {
        get
        {
            if (_target is not null)
                return _target;

            if (Syntax is AssignmentExpressionSyntax assignment)
                _target = SemanticModel.GetOperation(assignment.Left);
            else if (Syntax is AssignmentStatementSyntax statement)
                _target = SemanticModel.GetOperation(statement.Left);

            return _target;
        }
    }

    public IOperation? Value
    {
        get
        {
            if (_value is not null)
                return _value;

            if (Syntax is AssignmentExpressionSyntax assignment)
                _value = SemanticModel.GetOperation(assignment.Right);
            else if (Syntax is AssignmentStatementSyntax statement)
                _value = SemanticModel.GetOperation(statement.Right);

            return _value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Target)
            .AddIfNotNull(Value)
            .ToImmutable();
    }
}

internal abstract class LoopOperation : Operation, ILoopOperation
{
    private IOperation? _body;

    protected LoopOperation(
        SemanticModel semanticModel,
        OperationKind kind,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, kind, syntax, null, isImplicit)
    {
    }

    protected abstract StatementSyntax BodySyntax { get; }

    public IOperation? Body => _body ??= SemanticModel.GetOperation(BodySyntax);
}

internal sealed class WhileLoopOperation : LoopOperation, IWhileLoopOperation
{
    private readonly BoundWhileStatement _bound;
    private IOperation? _condition;

    internal WhileLoopOperation(SemanticModel semanticModel, BoundWhileStatement bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.WhileLoop, syntax, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Condition => _condition ??= Syntax switch
    {
        WhileStatementSyntax whileStatement => SemanticModel.GetOperation(whileStatement.Condition),
        WhilePatternStatementSyntax whilePatternStatement
            => OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.Condition, whilePatternStatement.Expression),
        _ => null
    };

    protected override StatementSyntax BodySyntax => Syntax switch
    {
        WhileStatementSyntax whileStatement => whileStatement.Statement,
        WhilePatternStatementSyntax whilePatternStatement => whilePatternStatement.Statement,
        _ => throw new InvalidOperationException($"Unexpected while loop syntax '{Syntax.Kind}'.")
    };

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Condition)
            .AddIfNotNull(Body)
            .ToImmutable();
    }
}

internal sealed class LoopStatementOperation : LoopOperation
{
    internal LoopStatementOperation(SemanticModel semanticModel, BoundLoopStatement bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Loop, syntax, isImplicit)
    {
    }

    protected override StatementSyntax BodySyntax => ((LoopStatementSyntax)Syntax).Statement;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Body)
            .ToImmutable();
    }
}

internal sealed class ForLoopOperation : LoopOperation, IForLoopOperation
{
    private readonly BoundForStatement _bound;
    private IOperation? _collection;
    private IOperation? _pattern;
    private bool _patternComputed;

    internal ForLoopOperation(SemanticModel semanticModel, BoundForStatement bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.ForLoop, syntax, isImplicit)
    {
        _bound = bound;
    }

    public ILocalSymbol? Local => ((ForStatementSyntax)Syntax).Target is IdentifierNameSyntax or
        VariablePatternSyntax
    {
        BindingKeyword.Kind: SyntaxKind.None,
        Designation: TypedVariableDesignationSyntax
        {
            Designation: SingleVariableDesignationSyntax
        }
    }
        ? _bound.Local
        : null;

    public ITypeSymbol ElementType => _bound.Iteration.ElementType;

    public IOperation? Collection => _collection ??= SemanticModel.GetOperation(((ForStatementSyntax)Syntax).Expression);

    public IOperation? Pattern
    {
        get
        {
            if (_patternComputed)
                return _pattern;

            _pattern = ((ForStatementSyntax)Syntax).Target is PatternSyntax pattern
                ? SemanticModel.GetOperation(pattern)
                : null;
            _patternComputed = true;
            return _pattern;
        }
    }

    protected override StatementSyntax BodySyntax => ((ForStatementSyntax)Syntax).Body;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Collection)
            .AddIfNotNull(Pattern)
            .AddIfNotNull(Body)
            .ToImmutable();
    }
}

internal sealed class TupleOperation : Operation, ITupleOperation
{
    private ImmutableArray<IOperation>? _elements;

    internal TupleOperation(
        SemanticModel semanticModel,
        BoundTupleExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Tuple, syntax, bound.Type, isImplicit)
    {
    }

    public ImmutableArray<IOperation> Elements
    {
        get
        {
            if (_elements.HasValue)
                return _elements.Value;

            _elements = Syntax is TupleExpressionSyntax tuple
                ? OperationUtilities.CreateChildOperations(SemanticModel, tuple.Arguments)
                : ImmutableArray<IOperation>.Empty;

            return _elements.Value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => Elements;
}

internal sealed class TryOperation : Operation, ITryOperation
{
    private readonly BoundTryStatement _bound;
    private ImmutableArray<ICatchClauseOperation>? _catches;
    private IOperation? _body;
    private IOperation? _finally;

    internal TryOperation(SemanticModel semanticModel, BoundTryStatement bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Try, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Body => _body ??= SemanticModel.GetOperation(((TryStatementSyntax)Syntax).Block);

    public ImmutableArray<ICatchClauseOperation> Catches
    {
        get
        {
            if (_catches.HasValue)
                return _catches.Value;

            var catches = ((TryStatementSyntax)Syntax).CatchClauses;
            var builder = ImmutableArray.CreateBuilder<ICatchClauseOperation>();
            for (var i = 0; i < _bound.CatchClauses.Length && i < catches.Count; i++)
            {
                builder.Add(new CatchClauseOperation(SemanticModel, _bound.CatchClauses[i], catches[i], isImplicit: false));
            }

            _catches = builder.ToImmutable();

            return _catches.Value;
        }
    }

    public IOperation? Finally => _finally ??= ((TryStatementSyntax)Syntax).FinallyClause is { } finallyClause
        ? SemanticModel.GetOperation(finallyClause.Block)
        : null;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();
        builder.AddIfNotNull(Body);
        builder.AddRange(Catches.Cast<IOperation>());
        builder.AddIfNotNull(Finally);
        return builder.ToImmutable();
    }
}

internal sealed class LockOperation : Operation, ILockOperation
{
    private IOperation? _lockedValue;
    private IOperation? _body;

    internal LockOperation(
        SemanticModel semanticModel,
        BoundLockStatement bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Lock, syntax, null, isImplicit)
    {
    }

    public IOperation? LockedValue => _lockedValue ??=
        SemanticModel.GetOperation(((LockStatementSyntax)Syntax).Expression);

    public IOperation? Body => _body ??=
        SemanticModel.GetOperation(((LockStatementSyntax)Syntax).Statement);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();
        builder.AddIfNotNull(LockedValue);
        builder.AddIfNotNull(Body);
        return builder.ToImmutable();
    }
}

internal sealed class CatchClauseOperation : Operation, ICatchClauseOperation
{
    private readonly BoundCatchClause _bound;
    private IOperation? _body;

    internal CatchClauseOperation(SemanticModel semanticModel, BoundCatchClause bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.CatchClause, syntax, bound.ExceptionType, isImplicit)
    {
        _bound = bound;
    }

    public ITypeSymbol ExceptionType => _bound.ExceptionType;

    public ILocalSymbol? Local => _bound.Local;

    public IOperation? Body => _body ??= SemanticModel.GetOperation(((CatchClauseSyntax)Syntax).Block);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Body is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Body);
    }
}

internal sealed class TryExpressionOperation : Operation, ITryExpressionOperation
{
    private readonly BoundTryExpression _bound;
    private IOperation? _operation;

    internal TryExpressionOperation(SemanticModel semanticModel, BoundTryExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.TryExpression, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Operation => _operation ??= SemanticModel.GetOperation(((TryExpressionSyntax)Syntax).Expression);

    public ITypeSymbol ExceptionType => _bound.ExceptionType;

    public IMethodSymbol OkConstructor => _bound.OkConstructor;

    public IMethodSymbol ErrorConstructor => _bound.ErrorConstructor;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Operation is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Operation);
    }
}

internal sealed class LambdaOperation : Operation, ILambdaOperation
{
    private readonly BoundFunctionExpression _bound;
    private IOperation? _body;
    private ImmutableArray<IParameterSymbol>? _parameters;
    private ImmutableArray<ISymbol>? _capturedVariables;

    internal LambdaOperation(
        SemanticModel semanticModel,
        BoundFunctionExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Lambda, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public ImmutableArray<IParameterSymbol> Parameters => _parameters ??= _bound.Parameters.ToImmutableArray();

    public ITypeSymbol ReturnType => _bound.ReturnType;

    public IOperation? Body
    {
        get
        {
            if (_body is not null)
                return _body;

            if (Syntax is FunctionExpressionSyntax lambda)
                _body = SemanticModel.GetOperation((SyntaxNode?)lambda.Body ?? lambda.ExpressionBody?.Expression);

            return _body;
        }
    }

    public ImmutableArray<INamedTypeSymbol> CandidateDelegates => _bound.CandidateDelegates;

    public ImmutableArray<ISymbol> CapturedVariables => _capturedVariables ??= _bound.CapturedVariables.ToImmutableArray();

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Body is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Body);
    }
}

internal sealed class UnionCaseOperation : Operation, IUnionCaseOperation
{
    private readonly BoundUnionCaseExpression _bound;
    private ImmutableArray<IOperation>? _arguments;

    internal UnionCaseOperation(
        SemanticModel semanticModel,
        BoundUnionCaseExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.UnionCase, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public INamedTypeSymbol UnionType => _bound.UnionType;

    public INamedTypeSymbol CaseType => _bound.CaseType;

    public IMethodSymbol? Constructor => _bound.CaseConstructor;

    public ImmutableArray<IOperation> Arguments
    {
        get
        {
            if (_arguments.HasValue)
                return _arguments.Value;

            var builder = ImmutableArray.CreateBuilder<IOperation>();
            foreach (var argument in _bound.Arguments)
            {
                builder.AddIfNotNull(OperationUtilities.CreateOperationFromBound(SemanticModel, argument, Syntax));
            }

            _arguments = builder.ToImmutable();
            return _arguments.Value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => Arguments;
}

internal sealed class MatchOperation : Operation, ISwitchOperation
{
    private readonly ImmutableArray<BoundMatchArm> _boundArms;
    private IOperation? _value;
    private ImmutableArray<IOperation>? _patterns;
    private ImmutableArray<IOperation>? _guards;
    private ImmutableArray<IOperation>? _armValues;

    internal MatchOperation(SemanticModel semanticModel, BoundMatchExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Switch, syntax, bound.Type, isImplicit)
    {
        _boundArms = bound.Arms;
    }

    internal MatchOperation(SemanticModel semanticModel, BoundMatchStatement bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Switch, syntax, null, isImplicit)
    {
        _boundArms = bound.Arms;
    }

    private ExpressionSyntax? MatchInput => Syntax switch
    {
        MatchExpressionSyntax matchExpression => matchExpression.Expression,
        PostfixMatchExpressionSyntax matchExpression => matchExpression.Expression,
        MatchStatementSyntax matchStatement => matchStatement.Expression,
        _ => null
    };

    private SyntaxList<MatchArmSyntax> MatchArms => Syntax switch
    {
        MatchExpressionSyntax matchExpression => matchExpression.Arms,
        PostfixMatchExpressionSyntax matchExpression => matchExpression.Arms,
        MatchStatementSyntax matchStatement => matchStatement.Arms,
        _ => default
    };

    public IOperation? Value => _value ??= MatchInput is null ? null : SemanticModel.GetOperation(MatchInput);

    public ImmutableArray<IOperation> Patterns
    {
        get
        {
            if (_patterns.HasValue)
                return _patterns.Value;

            var builder = ImmutableArray.CreateBuilder<IOperation>();
            foreach (var arm in MatchArms)
            {
                builder.AddIfNotNull(SemanticModel.GetOperation(arm.Pattern));
            }

            _patterns = builder.ToImmutable();
            return _patterns.Value;
        }
    }

    public ImmutableArray<IOperation> Guards
    {
        get
        {
            if (_guards.HasValue)
                return _guards.Value;

            var builder = ImmutableArray.CreateBuilder<IOperation>();
            foreach (var arm in MatchArms)
            {
                if (arm.WhenClause is null)
                    continue;

                builder.AddIfNotNull(SemanticModel.GetOperation(arm.WhenClause.Guard));
            }

            _guards = builder.ToImmutable();
            return _guards.Value;
        }
    }

    public ImmutableArray<IOperation> ArmValues
    {
        get
        {
            if (_armValues.HasValue)
                return _armValues.Value;

            var builder = ImmutableArray.CreateBuilder<IOperation>();
            var matchArms = MatchArms;

            if (_boundArms.Length == matchArms.Count)
            {
                for (var i = 0; i < matchArms.Count; i++)
                {
                    builder.AddIfNotNull(OperationUtilities.CreateOperationFromBound(SemanticModel, _boundArms[i].Expression, matchArms[i].Expression));
                }
            }
            else
            {
                foreach (var arm in matchArms)
                {
                    builder.AddIfNotNull(SemanticModel.GetOperation(arm.Expression));
                }
            }

            _armValues = builder.ToImmutable();
            return _armValues.Value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();
        builder.AddIfNotNull(Value);
        builder.AddRange(Patterns);
        builder.AddRange(Guards);
        builder.AddRange(ArmValues);
        return builder.ToImmutable();
    }
}

internal sealed class IsPatternOperation : Operation, IIsPatternOperation
{
    private readonly BoundIsPatternExpression _bound;
    private IOperation? _value;
    private IOperation? _pattern;

    internal IsPatternOperation(SemanticModel semanticModel, BoundIsPatternExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.IsPattern, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Value => _value ??= OperationUtilities.CreateOperationFromBound(
        SemanticModel,
        _bound.Expression,
        Syntax switch
        {
            IsPatternExpressionSyntax isPattern => isPattern.Expression,
            IfPatternStatementSyntax ifPattern => ifPattern.Expression,
            IfPatternExpressionSyntax ifPattern => ifPattern.Value,
            WhilePatternStatementSyntax whilePattern => whilePattern.Expression,
            _ => Syntax
        });

    public IOperation? Pattern => _pattern ??= OperationUtilities.CreateOperationFromBound(
        SemanticModel,
        _bound.Pattern,
        Syntax switch
        {
            IsPatternExpressionSyntax isPattern => isPattern.Pattern,
            IfPatternStatementSyntax ifPattern => ifPattern.Pattern,
            IfPatternExpressionSyntax ifPattern => ifPattern.Pattern,
            WhilePatternStatementSyntax whilePattern => whilePattern.Pattern,
            _ => Syntax
        });

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();

        builder.AddIfNotNull(Value);
        builder.AddIfNotNull(Pattern);

        return builder.ToImmutable();
    }
}

internal abstract class PatternOperation : Operation, IPatternOperation
{
    protected PatternOperation(
        SemanticModel semanticModel,
        OperationKind kind,
        SyntaxNode syntax,
        ITypeSymbol? type,
        bool isImplicit)
        : base(semanticModel, kind, syntax, type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => OperationUtilities.CreateChildOperations(SemanticModel, Syntax);
}

internal sealed class CasePatternOperation : PatternOperation, ICasePatternOperation
{
    private readonly BoundCasePattern _bound;
    private ImmutableArray<IOperation>? _arguments;

    internal CasePatternOperation(
        SemanticModel semanticModel,
        BoundCasePattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.CasePattern, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IUnionCaseTypeSymbol CaseSymbol => _bound.CaseSymbol;

    public IMethodSymbol TryGetMethod => _bound.TryGetMethod;

    public ImmutableArray<IOperation> Arguments
    {
        get
        {
            if (_arguments.HasValue)
                return _arguments.Value;

            var builder = ImmutableArray.CreateBuilder<IOperation>();
            foreach (var argument in _bound.Arguments)
            {
                var syntax = SemanticModel.GetSyntax(argument);
                if (syntax is null)
                    continue;

                builder.AddIfNotNull(SemanticModel.GetOperation(syntax));
            }

            _arguments = builder.ToImmutable();
            return _arguments.Value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => Arguments;
}

internal sealed class DeclarationPatternOperation : PatternOperation, IDeclarationPatternOperation
{
    private readonly BoundDeclarationPattern _bound;
    private IOperation? _designator;

    internal DeclarationPatternOperation(
        SemanticModel semanticModel,
        BoundDeclarationPattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.DeclarationPattern, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public ITypeSymbol DeclaredType => _bound.DeclaredType;

    public IOperation? Designator
    {
        get
        {
            if (_designator is not null)
                return _designator;

            var designatorSyntax = SemanticModel.GetSyntax(_bound.Designator);
            _designator = designatorSyntax is null ? null : SemanticModel.GetOperation(designatorSyntax);

            return _designator;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();

        builder.AddRange(OperationUtilities.CreateChildOperations(SemanticModel, Syntax));
        builder.AddIfNotNull(Designator);

        return builder.ToImmutable();
    }
}

internal sealed class ConstantPatternOperation : PatternOperation, IConstantPatternOperation
{
    private readonly BoundConstantPattern _bound;
    private IOperation? _value;

    internal ConstantPatternOperation(
        SemanticModel semanticModel,
        BoundConstantPattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.ConstantPattern, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public object ConstantValue => _bound.ConstantValue;

    public IOperation? Value
    {
        get
        {
            if (_value is not null)
                return _value;

            if (_bound.Expression is not null)
            {
                var syntax = SemanticModel.GetSyntax(_bound.Expression);
                if (syntax is not null)
                    _value = SemanticModel.GetOperation(syntax);
            }
            else if (Syntax is ConstantPatternSyntax constantPattern)
            {
                _value = SemanticModel.GetOperation(constantPattern.Expression);
            }
            else if (Syntax is ComparisonPatternSyntax comparisonPattern)
            {
                _value = SemanticModel.GetOperation(comparisonPattern.Expression);
            }

            return _value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Value is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Value);
    }
}

internal sealed class ComparisonPatternOperation : PatternOperation, IComparisonPatternOperation
{
    private readonly BoundComparisonPattern _bound;
    private IOperation? _value;
    private bool _valueInitialized;

    internal ComparisonPatternOperation(
        SemanticModel semanticModel,
        BoundComparisonPattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.ComparisonPattern, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public SyntaxKind OperatorKind => Syntax is ComparisonPatternSyntax comparisonPattern
        ? comparisonPattern.OperatorToken.Kind
        : _bound.Operator switch
        {
            BoundComparisonPatternOperator.Equals => SyntaxKind.EqualsEqualsToken,
            BoundComparisonPatternOperator.NotEquals => SyntaxKind.NotEqualsToken,
            BoundComparisonPatternOperator.LessThan => SyntaxKind.LessThanExpression,
            BoundComparisonPatternOperator.LessThanOrEqual => SyntaxKind.LessThanOrEqualsExpression,
            BoundComparisonPatternOperator.GreaterThan => SyntaxKind.GreaterThanExpression,
            BoundComparisonPatternOperator.GreaterThanOrEqual => SyntaxKind.GreaterThanOrEqualsExpression,
            _ => SyntaxKind.None
        };

    public IOperation? Value
    {
        get
        {
            if (_valueInitialized)
                return _value;

            _valueInitialized = true;
            var fallback = Syntax is ComparisonPatternSyntax comparisonPattern ? comparisonPattern.Expression : Syntax;
            _value = OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.Value, fallback);
            return _value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Value is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Value);
    }
}

internal sealed class PositionalPatternOperation : PatternOperation, IPositionalPatternOperation
{
    private ImmutableArray<IOperation>? _subpatterns;

    internal PositionalPatternOperation(
        SemanticModel semanticModel,
        BoundPositionalPattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.PositionalPattern, syntax, bound.Type, isImplicit)
    {
    }

    public ImmutableArray<IOperation> Subpatterns
    {
        get
        {
            if (_subpatterns.HasValue)
                return _subpatterns.Value;

            _subpatterns = Syntax is PositionalPatternSyntax positionalPattern
                ? OperationUtilities.CreateChildOperations(SemanticModel, positionalPattern.Elements.Select(element => element.Pattern))
                : ImmutableArray<IOperation>.Empty;

            return _subpatterns.Value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => Subpatterns;
}

internal sealed class RecursivePatternOperation : PatternOperation, IRecursivePatternOperation
{
    private readonly BoundDeconstructPattern _bound;
    private ImmutableArray<IOperation>? _arguments;

    internal RecursivePatternOperation(
        SemanticModel semanticModel,
        BoundDeconstructPattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.RecursivePattern, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public ITypeSymbol ReceiverType => _bound.ReceiverType;

    public ITypeSymbol? NarrowedType => _bound.NarrowedType;

    public IMethodSymbol DeconstructMethod => _bound.DeconstructMethod;

    public ImmutableArray<IOperation> Arguments
    {
        get
        {
            if (_arguments.HasValue)
                return _arguments.Value;

            var syntaxArguments = Syntax switch
            {
                NominalDeconstructionPatternSyntax nominalPattern => nominalPattern.ArgumentList.Arguments.Select(argument => argument.Pattern).ToImmutableArray(),
                PositionalPatternSyntax positionalPattern => positionalPattern.Elements.Select(element => element.Pattern).ToImmutableArray(),
                _ => ImmutableArray<PatternSyntax>.Empty
            };

            var builder = ImmutableArray.CreateBuilder<IOperation>();

            if (_bound.Arguments.Length == syntaxArguments.Length)
            {
                for (var i = 0; i < _bound.Arguments.Length; i++)
                {
                    builder.AddIfNotNull(OperationUtilities.CreateOperationFromBound(
                        SemanticModel,
                        _bound.Arguments[i],
                        syntaxArguments[i]));
                }
            }
            else
            {
                foreach (var argumentSyntax in syntaxArguments)
                    builder.AddIfNotNull(SemanticModel.GetOperation(argumentSyntax));
            }

            _arguments = builder.ToImmutable();
            return _arguments.Value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => Arguments;
}

internal sealed class RangePatternOperation : PatternOperation, IRangePatternOperation
{
    private readonly BoundRangePattern _bound;
    private IOperation? _lowerBound;
    private IOperation? _upperBound;
    private bool _lowerBoundInitialized;
    private bool _upperBoundInitialized;

    internal RangePatternOperation(
        SemanticModel semanticModel,
        BoundRangePattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.RangePattern, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? LowerBound
    {
        get
        {
            if (_lowerBoundInitialized)
                return _lowerBound;

            _lowerBoundInitialized = true;
            var fallback = Syntax is RangePatternSyntax rangePattern && rangePattern.LowerBound is not null
                ? rangePattern.LowerBound
                : Syntax;
            _lowerBound = OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.LowerBound, fallback);
            return _lowerBound;
        }
    }

    public IOperation? UpperBound
    {
        get
        {
            if (_upperBoundInitialized)
                return _upperBound;

            _upperBoundInitialized = true;
            var fallback = Syntax is RangePatternSyntax rangePattern && rangePattern.UpperBound is not null
                ? rangePattern.UpperBound
                : Syntax;
            _upperBound = OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.UpperBound, fallback);
            return _upperBound;
        }
    }

    public bool IsUpperExclusive => _bound.IsUpperExclusive;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(LowerBound)
            .AddIfNotNull(UpperBound)
            .ToImmutable();
    }
}

internal sealed class PropertyPatternOperation : PatternOperation, IPropertyPatternOperation
{
    private readonly BoundPropertyPattern _bound;
    private IOperation? _designator;
    private bool _designatorInitialized;
    private ImmutableArray<ISymbol>? _members;
    private ImmutableArray<IOperation>? _subpatterns;

    internal PropertyPatternOperation(
        SemanticModel semanticModel,
        BoundPropertyPattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.PropertyPattern, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public ITypeSymbol ReceiverType => _bound.ReceiverType;

    public ITypeSymbol? NarrowedType => _bound.NarrowedType;

    public IOperation? Designator
    {
        get
        {
            if (_designatorInitialized)
                return _designator;

            _designatorInitialized = true;
            _designator = _bound.Designator is null
                ? null
                : OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.Designator, Syntax);
            return _designator;
        }
    }

    public ImmutableArray<ISymbol> Members => _members ??= _bound.Properties.Select(property => property.Member).ToImmutableArray();

    public ImmutableArray<IOperation> Subpatterns
    {
        get
        {
            if (_subpatterns.HasValue)
                return _subpatterns.Value;

            var syntaxSubpatterns = Syntax is PropertyPatternSyntax propertyPattern
                ? propertyPattern.PropertyPatternClause.Properties.Select(property => property.Pattern).ToImmutableArray()
                : ImmutableArray<PatternSyntax>.Empty;

            var builder = ImmutableArray.CreateBuilder<IOperation>();

            if (syntaxSubpatterns.IsEmpty && _bound.Properties.Length > 0)
            {
                foreach (var property in _bound.Properties)
                {
                    builder.AddIfNotNull(OperationUtilities.CreateOperationFromBound(
                        SemanticModel,
                        property.Pattern,
                        Syntax));
                }

                _subpatterns = builder.ToImmutable();
                return _subpatterns.Value;
            }

            if (_bound.Properties.Length == syntaxSubpatterns.Length)
            {
                for (var i = 0; i < _bound.Properties.Length; i++)
                {
                    builder.AddIfNotNull(OperationUtilities.CreateOperationFromBound(
                        SemanticModel,
                        _bound.Properties[i].Pattern,
                        syntaxSubpatterns[i]));
                }
            }
            else
            {
                foreach (var pattern in syntaxSubpatterns)
                    builder.AddIfNotNull(SemanticModel.GetOperation(pattern));
            }

            _subpatterns = builder.ToImmutable();
            return _subpatterns.Value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();
        builder.AddIfNotNull(Designator);
        builder.AddRange(Subpatterns);
        return builder.ToImmutable();
    }
}

internal sealed class DictionaryPatternOperation : PatternOperation, IDictionaryPatternOperation
{
    private readonly BoundDictionaryPattern _bound;
    private IOperation? _designator;
    private bool _designatorInitialized;
    private ImmutableArray<IOperation>? _keys;
    private ImmutableArray<IOperation>? _subpatterns;

    internal DictionaryPatternOperation(
        SemanticModel semanticModel,
        BoundDictionaryPattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.DictionaryPattern, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public ITypeSymbol ReceiverType => _bound.ReceiverType;

    public ITypeSymbol? NarrowedType => null;

    public ITypeSymbol KeyType => _bound.KeyType;

    public ITypeSymbol ValueType => _bound.ValueType;

    public IOperation? Designator
    {
        get
        {
            if (_designatorInitialized)
                return _designator;

            _designatorInitialized = true;
            _designator = _bound.Designator is null
                ? null
                : OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.Designator, Syntax);
            return _designator;
        }
    }

    public ImmutableArray<IOperation> Keys
    {
        get
        {
            if (_keys.HasValue)
                return _keys.Value;

            var syntaxEntries = Syntax is DictionaryPatternSyntax dictionaryPattern
                ? dictionaryPattern.Entries.ToImmutableArray()
                : ImmutableArray<DictionaryPatternEntrySyntax>.Empty;

            var builder = ImmutableArray.CreateBuilder<IOperation>();

            if (_bound.Entries.Length == syntaxEntries.Length)
            {
                for (var i = 0; i < _bound.Entries.Length; i++)
                {
                    builder.AddIfNotNull(OperationUtilities.CreateOperationFromBound(
                        SemanticModel,
                        _bound.Entries[i].Key,
                        syntaxEntries[i].Key));
                }
            }
            else
            {
                foreach (var entry in syntaxEntries)
                    builder.AddIfNotNull(SemanticModel.GetOperation(entry.Key));
            }

            _keys = builder.ToImmutable();
            return _keys.Value;
        }
    }

    public ImmutableArray<IOperation> Subpatterns
    {
        get
        {
            if (_subpatterns.HasValue)
                return _subpatterns.Value;

            var syntaxEntries = Syntax is DictionaryPatternSyntax dictionaryPattern
                ? dictionaryPattern.Entries.ToImmutableArray()
                : ImmutableArray<DictionaryPatternEntrySyntax>.Empty;

            var builder = ImmutableArray.CreateBuilder<IOperation>();

            if (_bound.Entries.Length == syntaxEntries.Length)
            {
                for (var i = 0; i < _bound.Entries.Length; i++)
                {
                    builder.AddIfNotNull(OperationUtilities.CreateOperationFromBound(
                        SemanticModel,
                        _bound.Entries[i].Pattern,
                        syntaxEntries[i].Pattern));
                }
            }
            else
            {
                foreach (var entry in syntaxEntries)
                    builder.AddIfNotNull(SemanticModel.GetOperation(entry.Pattern));
            }

            _subpatterns = builder.ToImmutable();
            return _subpatterns.Value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        var builder = ImmutableArray.CreateBuilder<IOperation>();
        builder.AddIfNotNull(Designator);
        builder.AddRange(Keys);
        builder.AddRange(Subpatterns);
        return builder.ToImmutable();
    }
}

internal sealed class DiscardPatternOperation : PatternOperation, IDiscardPatternOperation
{
    internal DiscardPatternOperation(
        SemanticModel semanticModel,
        BoundDiscardPattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.DiscardPattern, syntax, bound.Type, isImplicit)
    {
    }
}

internal sealed class NotPatternOperation : PatternOperation, INotPatternOperation
{
    private IOperation? _pattern;

    internal NotPatternOperation(
        SemanticModel semanticModel,
        BoundNotPattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.NotPattern, syntax, bound.Type, isImplicit)
    {
    }

    public IOperation? Pattern => _pattern ??= Syntax is UnaryPatternSyntax unaryPattern
        ? SemanticModel.GetOperation(unaryPattern.Pattern)
        : null;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Pattern is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Pattern);
    }
}

internal sealed class AndPatternOperation : PatternOperation, IAndPatternOperation
{
    private IOperation? _left;
    private IOperation? _right;

    internal AndPatternOperation(
        SemanticModel semanticModel,
        BoundAndPattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.AndPattern, syntax, bound.Type, isImplicit)
    {
    }

    public IOperation? Left => _left ??= Syntax is BinaryPatternSyntax binaryPattern
        ? SemanticModel.GetOperation(binaryPattern.Left)
        : null;

    public IOperation? Right => _right ??= Syntax is BinaryPatternSyntax binaryPattern
        ? SemanticModel.GetOperation(binaryPattern.Right)
        : null;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Left)
            .AddIfNotNull(Right)
            .ToImmutable();
    }
}

internal sealed class OrPatternOperation : PatternOperation, IOrPatternOperation
{
    private IOperation? _left;
    private IOperation? _right;

    internal OrPatternOperation(
        SemanticModel semanticModel,
        BoundOrPattern bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.OrPattern, syntax, bound.Type, isImplicit)
    {
    }

    public IOperation? Left => _left ??= Syntax is BinaryPatternSyntax binaryPattern
        ? SemanticModel.GetOperation(binaryPattern.Left)
        : null;

    public IOperation? Right => _right ??= Syntax is BinaryPatternSyntax binaryPattern
        ? SemanticModel.GetOperation(binaryPattern.Right)
        : null;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Left)
            .AddIfNotNull(Right)
            .ToImmutable();
    }
}

internal abstract class DesignatorOperation : Operation, IDesignatorOperation
{
    protected DesignatorOperation(
        SemanticModel semanticModel,
        OperationKind kind,
        SyntaxNode syntax,
        ITypeSymbol? type,
        bool isImplicit)
        : base(semanticModel, kind, syntax, type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class SingleVariableDesignatorOperation : DesignatorOperation, ISingleVariableDesignatorOperation
{
    private readonly BoundSingleVariableDesignator _bound;

    internal SingleVariableDesignatorOperation(
        SemanticModel semanticModel,
        BoundSingleVariableDesignator bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.SingleVariableDesignator, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public ILocalSymbol Local => _bound.Local;
}

internal sealed class DiscardDesignatorOperation : DesignatorOperation, IDiscardDesignatorOperation
{
    internal DiscardDesignatorOperation(
        SemanticModel semanticModel,
        BoundDiscardDesignator bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.DiscardDesignator, syntax, bound.Type, isImplicit)
    {
    }
}

internal sealed class CollectionOperation : Operation, ICollectionOperation
{
    private readonly BoundCollectionExpression _bound;
    private ImmutableArray<IOperation>? _elements;

    internal CollectionOperation(SemanticModel semanticModel, BoundCollectionExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Collection, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        if (_elements.HasValue)
            return _elements.Value;

        var syntaxElements = Syntax is CollectionExpressionSyntax collectionSyntax
            ? collectionSyntax.Elements.ToArray()
            : [];
        var boundElements = _bound.Elements.ToArray();
        var builder = ImmutableArray.CreateBuilder<IOperation>();

        if (boundElements.Length == syntaxElements.Length)
        {
            for (var i = 0; i < boundElements.Length; i++)
                builder.AddIfNotNull(OperationUtilities.CreateOperationFromBound(SemanticModel, boundElements[i], syntaxElements[i]));
        }
        else
        {
            foreach (var boundElement in boundElements)
                builder.AddIfNotNull(OperationUtilities.CreateOperationFromBound(SemanticModel, boundElement, Syntax));
        }

        _elements = builder.ToImmutable();
        return _elements.Value;
    }
}

internal sealed class CollectionComprehensionOperation : Operation, ICollectionComprehensionOperation
{
    private readonly BoundCollectionComprehensionExpression _bound;
    private IOperation? _source;
    private IOperation? _condition;
    private IOperation? _selector;
    private bool _conditionInitialized;

    internal CollectionComprehensionOperation(
        SemanticModel semanticModel,
        BoundCollectionComprehensionExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.CollectionComprehension, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Source
    {
        get
        {
            if (_source is not null)
                return _source;

            var fallback = Syntax is CollectionComprehensionElementSyntax comprehension
                ? comprehension.Source
                : Syntax;
            _source = OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.Source, fallback);
            return _source;
        }
    }

    public IOperation? Condition
    {
        get
        {
            if (_conditionInitialized)
                return _condition;

            _conditionInitialized = true;
            if (_bound.Condition is null)
                return null;

            var fallback = Syntax is CollectionComprehensionElementSyntax comprehension && comprehension.Condition is not null
                ? comprehension.Condition
                : Syntax;
            _condition = OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.Condition, fallback);
            return _condition;
        }
    }

    public IOperation? Selector
    {
        get
        {
            if (_selector is not null)
                return _selector;

            var fallback = Syntax is CollectionComprehensionElementSyntax comprehension
                ? comprehension.Selector
                : Syntax;
            _selector = OperationUtilities.CreateOperationFromBound(SemanticModel, _bound.Selector, fallback);
            return _selector;
        }
    }

    public ILocalSymbol IterationLocal => _bound.IterationLocal;

    public ITypeSymbol ElementType => _bound.ElementType;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Source)
            .AddIfNotNull(Condition)
            .AddIfNotNull(Selector)
            .ToImmutable();
    }
}

internal sealed class DictionaryOperation : Operation, IDictionaryOperation
{
    private readonly BoundDictionaryExpression _bound;
    private ImmutableArray<IOperation>? _elements;

    internal DictionaryOperation(SemanticModel semanticModel, BoundDictionaryExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.Dictionary, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public ImmutableArray<IOperation> Elements
    {
        get
        {
            if (_elements.HasValue)
                return _elements.Value;

            var syntaxElements = Syntax is CollectionExpressionSyntax collectionSyntax
                ? collectionSyntax.Elements.ToArray()
                : [];
            var boundElements = _bound.Elements.ToArray();
            var builder = ImmutableArray.CreateBuilder<IOperation>();

            if (boundElements.Length == syntaxElements.Length)
            {
                for (var i = 0; i < boundElements.Length; i++)
                {
                    IOperation? operation = boundElements[i] switch
                    {
                        DictionaryEntryBinding entry => new DictionaryElementOperation(SemanticModel, entry, syntaxElements[i], isImplicit: false),
                        DictionarySpreadBinding spread => new DictionarySpreadElementOperation(SemanticModel, spread, syntaxElements[i], isImplicit: false),
                        DictionaryComprehensionBinding comprehension => new DictionaryComprehensionOperation(SemanticModel, comprehension, syntaxElements[i], isImplicit: false),
                        _ => null
                    };

                    builder.AddIfNotNull(operation);
                }
            }

            _elements = builder.ToImmutable();
            return _elements.Value;
        }
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => Elements;
}

internal sealed class DictionaryElementOperation : Operation, IDictionaryElementOperation
{
    private readonly DictionaryEntryBinding _bound;
    private IOperation? _key;
    private IOperation? _value;

    internal DictionaryElementOperation(SemanticModel semanticModel, DictionaryEntryBinding bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.DictionaryElement, syntax, bound.Value.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Key => _key ??= OperationUtilities.CreateOperationFromBound(
        SemanticModel,
        _bound.Key,
        Syntax switch
        {
            DictionaryElementSyntax entry => entry.Key,
            DictionarySpreadElementSyntax spreadEntry => spreadEntry.Key,
            _ => Syntax
        });

    public IOperation? Value => _value ??= OperationUtilities.CreateOperationFromBound(
        SemanticModel,
        _bound.Value,
        Syntax switch
        {
            DictionaryElementSyntax entry => entry.Value,
            DictionarySpreadElementSyntax spreadEntry => spreadEntry.Value,
            _ => Syntax
        });

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Key)
            .AddIfNotNull(Value)
            .ToImmutable();
    }
}

internal sealed class DictionarySpreadElementOperation : Operation, IDictionarySpreadElementOperation
{
    private readonly DictionarySpreadBinding _bound;
    private IOperation? _expression;

    internal DictionarySpreadElementOperation(SemanticModel semanticModel, DictionarySpreadBinding bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.DictionarySpreadElement, syntax, bound.Expression.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Expression => _expression ??= OperationUtilities.CreateOperationFromBound(
        SemanticModel,
        _bound.Expression,
        Syntax is SpreadElementSyntax spreadElement ? spreadElement.Expression : Syntax);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Expression is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Expression);
    }
}

internal sealed class DictionaryComprehensionOperation : Operation, IDictionaryComprehensionOperation
{
    private readonly DictionaryComprehensionBinding _bound;
    private IOperation? _source;
    private IOperation? _condition;
    private IOperation? _keySelector;
    private IOperation? _valueSelector;
    private bool _conditionInitialized;

    internal DictionaryComprehensionOperation(SemanticModel semanticModel, DictionaryComprehensionBinding bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.DictionaryComprehension, syntax, null, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Source => _source ??= OperationUtilities.CreateOperationFromBound(
        SemanticModel,
        _bound.Source,
        Syntax is DictionaryComprehensionElementSyntax comprehension ? comprehension.Source : Syntax);

    public IOperation? Condition
    {
        get
        {
            if (_conditionInitialized)
                return _condition;

            _conditionInitialized = true;
            if (_bound.Condition is null)
                return null;

            _condition = OperationUtilities.CreateOperationFromBound(
                SemanticModel,
                _bound.Condition,
                Syntax is DictionaryComprehensionElementSyntax comprehension && comprehension.Condition is not null
                    ? comprehension.Condition
                    : Syntax);
            return _condition;
        }
    }

    public IOperation? KeySelector => _keySelector ??= OperationUtilities.CreateOperationFromBound(
        SemanticModel,
        _bound.KeySelector,
        Syntax is DictionaryComprehensionElementSyntax comprehension ? comprehension.KeySelector : Syntax);

    public IOperation? ValueSelector => _valueSelector ??= OperationUtilities.CreateOperationFromBound(
        SemanticModel,
        _bound.ValueSelector,
        Syntax is DictionaryComprehensionElementSyntax comprehension ? comprehension.ValueSelector : Syntax);

    public ILocalSymbol IterationLocal => _bound.IterationLocal;

    public ITypeSymbol KeyType => _bound.KeyType;

    public ITypeSymbol ValueType => _bound.ValueType;

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return ImmutableArray.CreateBuilder<IOperation>()
            .AddIfNotNull(Source)
            .AddIfNotNull(Condition)
            .AddIfNotNull(KeySelector)
            .AddIfNotNull(ValueSelector)
            .ToImmutable();
    }
}

internal sealed class EmptyCollectionOperation : Operation, IEmptyCollectionOperation
{
    internal EmptyCollectionOperation(SemanticModel semanticModel, BoundEmptyCollectionExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.EmptyCollection, syntax, bound.Type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class SpreadElementOperation : Operation, ISpreadElementOperation
{
    private readonly BoundSpreadElement _bound;
    private IOperation? _expression;

    internal SpreadElementOperation(SemanticModel semanticModel, BoundSpreadElement bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.SpreadElement, syntax, bound.Type, isImplicit)
    {
        _bound = bound;
    }

    public IOperation? Expression => _expression ??= OperationUtilities.CreateOperationFromBound(
        SemanticModel,
        _bound.Expression,
        Syntax is SpreadElementSyntax spreadElement ? spreadElement.Expression : Syntax);

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return Expression is null
            ? ImmutableArray<IOperation>.Empty
            : ImmutableArray.Create(Expression);
    }
}

internal sealed class TypeOperation : Operation, ITypeOperation
{
    internal TypeOperation(SemanticModel semanticModel, BoundTypeExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.TypeExpression, syntax, bound.Type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => OperationUtilities.CreateChildOperations(SemanticModel, Syntax);
}

internal sealed class NamespaceOperation : Operation, INamespaceOperation
{
    internal NamespaceOperation(SemanticModel semanticModel, BoundNamespaceExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.NamespaceExpression, syntax, bound.Type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => OperationUtilities.CreateChildOperations(SemanticModel, Syntax);
}

internal sealed class SelfOperation : Operation, ISelfOperation
{
    internal SelfOperation(SemanticModel semanticModel, BoundSelfExpression bound, SyntaxNode syntax, bool isImplicit)
        : base(semanticModel, OperationKind.SelfReference, syntax, bound.Type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class UnitOperation : Operation, IUnitOperation
{
    internal UnitOperation(
        SemanticModel semanticModel,
        BoundUnitExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Unit, syntax, bound.Type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore() => ImmutableArray<IOperation>.Empty;
}

internal sealed class InvalidOperation : Operation, IInvalidOperation
{
    internal InvalidOperation(
        SemanticModel semanticModel,
        BoundErrorExpression bound,
        SyntaxNode syntax,
        bool isImplicit)
        : base(semanticModel, OperationKind.Invalid, syntax, bound.Type, isImplicit)
    {
    }

    protected override ImmutableArray<IOperation> GetChildrenCore()
    {
        return OperationUtilities.CreateChildOperations(SemanticModel, Syntax);
    }
}
