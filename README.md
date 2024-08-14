# Raven programming language

Experimental compiler based on the .NET Roslyn compiler architecture.

Built for fun and learning!

The purpose is to build a modern compiler that provides an API for manipulating syntax is an efficient immutable fashion. This could be referred to as a "Compiler-as-a-Service".

It is a merger of the projects in the [compiler-projects](https://github.com/marinasundstrom/compiler-projects) repo.

Look at unit tests.

## Compiler

The Parser and the AST is not yet in sync.

Syntax to be decided. Should it be a C-style language, or something else?

Expression parser logic can be taken from [ExpressionEvaluator](https://github.com/marinasundstrom/ExpressionEvaluator). This is a [Operator-precedence parser](https://en.wikipedia.org/wiki/Operator-precedence_parser), originally based on the IronPython source code (in C#),

### AST

The Abstract Syntax Tree (AST) is immutable. And "changing" a syntax node should result in a copy of the tree.

We need to handle syntax trivia - whitespaces, comments, and other things with no meaning.


#### Internal Tree 

Re-usable part. Kept when modifying the tree. 

Nodes that know their children. But not their parent.




