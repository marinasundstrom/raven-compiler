namespace Raven.CodeAnalysis.Syntax;

public enum SyntaxKind
{
    EndOfFileTrivia,

    WhitespaceTrivia,
    NewlineTrivia,

    IdentifierToken,
    NumberToken,

    OpeningParenToken,
    ClosingParenToken,
    OpenBraceToken,
    CloseBraceToken,
    OpenSquareToken,
    CloseSquareToken,
    OpenAngleToken,
    CloseAngleToken,

    AssignToken,
    EqualsToken,
    NotEqualsToken,
    GreaterEqualsToken,
    LessEqualsToken,

    PlusToken,
    DashToken,
    SlashToken,
    BackslashToken,
    StarToken,
    PercentToken,
    ColonToken,
    SemicolonToken,
    DoublequoteToken,
    SinglequoteToken,
    BackquoteToken,

    VarToken,
    LetToken,
    IfToken,
    ThenToken,
    ElseToken,
    TrueToken,
    FalseToken,

    IfStatementSyntax,
    ElseClauseSyntax,
    BlockStatementSyntax,
    DotToken,
    InvalidSyntax,
    TabTrivia,
    ElasticToken,
    AssingmentExpression,
    BooleanFalseExpression,
    BooleanTrueExpression,
    IdentifierExpression,
    IfElseExpression,
    LetDeclarationExpression,
    NumberExpression,
    ParenthesisExpression
}