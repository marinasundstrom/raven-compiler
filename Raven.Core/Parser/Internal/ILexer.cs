using Raven.CodeAnalysis.Syntax.InternalSyntax;

namespace Raven.CodeAnalysis.Parser.Internal;

public interface ILexer
{
    bool IsEndOfLine { get; }

    SyntaxToken ReadToken();

    SyntaxToken PeekToken();
}