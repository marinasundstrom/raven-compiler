using System;
using System.Collections.Generic;
using System.Linq;

using Raven.CodeAnalysis.Parser.Internal;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Syntax.InternalSyntax;

namespace Raven.CodeAnalysis.Syntax.InternalSyntax
{
    public class SyntaxToken : SyntaxNode
    {
        public SyntaxToken(SyntaxKind kind, string? text = null)
            : this(new SyntaxTriviaList(), kind, text, new SyntaxTriviaList())
        {

        }

        public SyntaxToken(SyntaxKind kind, int width)
            : this(new SyntaxTriviaList(), kind, width, new SyntaxTriviaList())
        {

        }

        public SyntaxToken(SyntaxTriviaList leadingTrivia, SyntaxKind kind, string text, SyntaxTriviaList trailingTrivia)
        {
            LeadingTrivia = leadingTrivia;
            Kind = kind;
            Text = text;
            Width = text?.Length ?? 0;
            TrailingTrivia = trailingTrivia;
        }

        public SyntaxToken(SyntaxTriviaList leadingTrivia, SyntaxKind kind, int width, SyntaxTriviaList trailingTrivia)
        {
            LeadingTrivia = leadingTrivia;
            Kind = kind;
            Text = string.Empty;
            Width = width;
            TrailingTrivia = trailingTrivia;
        }

        public override SyntaxKind Kind { get; }

        public override int Width { get; }

        public override int FullWidth => LeadingTrivia.Width + Width + TrailingTrivia.Width;

        public string? Text { get; }

        public object? Value { get; }

        public string? TextValue { get; }

        public SyntaxTriviaList LeadingTrivia { get; }

        public SyntaxTriviaList TrailingTrivia { get; }

        public override string ToFullString()
        {
            return LeadingTrivia.ToFullString() + Text + TrailingTrivia.ToFullString();
        }

        public override string ToString()
        {
            return Text;
        }

        public SyntaxToken PrependLeadingTrivia(params SyntaxTrivia[] syntaxTrivia)
        {
            var trivia = new SyntaxTriviaList(TrailingTrivia);
            trivia.AddRange(syntaxTrivia);

            return new SyntaxToken(LeadingTrivia, Kind, Text, TrailingTrivia);
        }

        public SyntaxToken AppendTrailingTrivia(params SyntaxTrivia[] syntaxTrivia)
        {
            var trivia = new SyntaxTriviaList(TrailingTrivia);
            trivia.AddRange(syntaxTrivia);

            return new SyntaxToken(LeadingTrivia, Kind, Text, trivia);
        }
    }
}