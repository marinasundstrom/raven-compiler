namespace Raven.CodeAnalysis.Syntax;

public class TextSpan
{
    public int Start { get; }

    public TextSpan(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public int Length { get; }

    public int End => Start + Length;
}