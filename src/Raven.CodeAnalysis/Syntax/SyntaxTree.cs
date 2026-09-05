using System.Diagnostics.CodeAnalysis;
using System.Text;

using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Syntax;

internal enum IncrementalParseFallbackReason
{
    None,
    ConditionalDirectives,
    PreviousFallback,
    ExistingRecoverySyntax,
    ChangePolicy,
    NodeParseFailure,
    NewRecoverySyntax,
    ReconstructedTextMismatch
}

internal sealed class IncrementalParseFallbackException : InvalidOperationException
{
    internal IncrementalParseFallbackException(
        IncrementalParseFallbackReason reason,
        string filePath)
        : base($"Incremental parsing required a full-document fallback for '{filePath}': {reason}.")
    {
        Reason = reason;
    }

    internal IncrementalParseFallbackReason Reason { get; }
}

public partial class SyntaxTree
{
    internal const int IncrementalParseMaxChangeLength = 4096;

    private CompilationUnitSyntax? _compilationUnit;
    private readonly SourceText _sourceText;
    private readonly ParseOptions _options;
    private IReadOnlyList<Diagnostic>? _diagnostics;

    internal SyntaxTree(SourceText sourceText, string filePath, ParseOptions? options, bool isGenerated = false)
    {
        IsGenerated = isGenerated;
        _sourceText = sourceText;
        FilePath = filePath ?? "file";
        _options = (options ?? new ParseOptions()).Snapshot();
    }

    internal bool IsGenerated { get; }

    public Encoding Encoding => _sourceText.Encoding;
    public string FilePath { get; }
    public bool HasCompilationUnit => _compilationUnit is not null;
    public int Length => _sourceText.Length;
    public ParseOptions Options => _options;
    internal IncrementalParseFallbackReason IncrementalParseFallbackReason { get; private set; }

    public CompilationUnitSyntax GetRoot(CancellationToken cancellationToken = default) =>
        _compilationUnit ?? throw new InvalidOperationException("The syntax root has not been attached.");

    public static SyntaxTree ParseText(string text, ParseOptions? options = null, Encoding? encoding = null, string? path = null)
    {
        var sourceText = SourceText.From(text, encoding);

        return ParseText(sourceText, options, path);
    }

    public static SyntaxTree ParseText(SourceText sourceText, ParseOptions? options = null, string? path = null)
        => ParseTextCore(sourceText, options, path, isGenerated: false);

    internal static SyntaxTree ParseGeneratedText(SourceText sourceText, string path)
        => ParseTextCore(sourceText, options: null, path, isGenerated: true);

    private static SyntaxTree ParseTextCore(SourceText sourceText, ParseOptions? options, string? path, bool isGenerated)
    {
        var parser = new InternalSyntax.Parser.LanguageParser(path ?? "file", options ?? new ParseOptions());

        var parseResult = parser.Parse(sourceText);
        var compilationUnit = (CompilationUnitSyntax)parseResult.Root.CreateRed();

        var sourceTree = new SyntaxTree(sourceText, path ?? "file", options, isGenerated);

        compilationUnit = compilationUnit
            .WithSyntaxTree(sourceTree);

        sourceTree.AttachSyntaxRoot(compilationUnit);
        sourceTree.AttachDiagnostics(parseResult.Diagnostics);

        return sourceTree;
    }

    public IEnumerable<Diagnostic> GetDiagnostics(CancellationToken cancellationToken = default)
    {
        return _diagnostics ?? Enumerable.Empty<Diagnostic>();
    }

    public IEnumerable<Diagnostic> GetDiagnostics(SyntaxNodeOrToken syntaxNodeOrToken)
    {
        if (_diagnostics is null)
        {
            return Enumerable.Empty<Diagnostic>();
        }

        var span = syntaxNodeOrToken.IsNode
            ? syntaxNodeOrToken.AsNode()!.FullSpan
            : syntaxNodeOrToken.AsToken().FullSpan;

        return _diagnostics.Where(d => d.Location.SourceSpan.IntersectsWith(span));
    }

    public IEnumerable<Diagnostic> GetDiagnostics(TextSpan span)
    {
        if (_diagnostics is null)
        {
            return Enumerable.Empty<Diagnostic>();
        }

        return _diagnostics.Where(d => d.Location.SourceSpan.IntersectsWith(span));
    }

    public IEnumerable<TextChange> GetChanges(SyntaxTree oldTree)
    {
        return GetText().GetTextChanges(oldTree.GetText());
    }

    public static SyntaxTree Create(CompilationUnitSyntax compilationUnit, ParseOptions? options = null, Encoding? encoding = null, string? filePath = null)
    {
        var sourceText = SourceText.From(compilationUnit.ToFullString(), encoding);

        var syntaxTree = new SyntaxTree(sourceText, filePath ?? "file", options);

        compilationUnit = compilationUnit
            .WithSyntaxTree(syntaxTree);

        syntaxTree.AttachSyntaxRoot(compilationUnit);
        syntaxTree.AttachDiagnostics(Array.Empty<InternalSyntax.DiagnosticInfo>());

        return syntaxTree;
    }


    internal static SyntaxTree Create(
        SourceText sourceText,
        CompilationUnitSyntax compilationUnit,
        ParseOptions options,
        string? filePath = null,
        IEnumerable<InternalSyntax.DiagnosticInfo>? diagnostics = null,
        bool isGenerated = false)
    {
        var syntaxTree = new SyntaxTree(sourceText, filePath ?? string.Empty, options, isGenerated);

        compilationUnit = compilationUnit
            .WithSyntaxTree(syntaxTree);

        syntaxTree.AttachSyntaxRoot(compilationUnit);
        syntaxTree.AttachDiagnostics(diagnostics ?? Array.Empty<InternalSyntax.DiagnosticInfo>());

        return syntaxTree;
    }

    internal void AttachSyntaxRoot(CompilationUnitSyntax compilationUnit)
    {
        _compilationUnit = compilationUnit;
    }

    internal void AttachDiagnostics(IEnumerable<InternalSyntax.DiagnosticInfo> diagnostics)
    {
        _diagnostics = diagnostics
            .Select(d => Diagnostic.Create(d.Descriptor, GetLocation(d.Span), d.Args))
            .ToArray();
    }

    public Location GetLocation(TextSpan span)
    {
        var sourceText = GetText();

        var (line, col) = sourceText.GetLineAndColumn(span);

        return Location.Create(this, span);
    }

    public SourceText GetText() => _sourceText;

    public bool TryGetText([NotNullWhen(true)] out SourceText? text)
    {
        text = _sourceText;
        return true;
    }

    /// <summary>
    /// Gets the nodes in span.
    /// </summary>
    /// <param name="span"></param>
    /// <returns>An enumerable of nodes that return the the innermost node first</returns>
    public IEnumerable<SyntaxNode> GetNodesInSpan(TextSpan span)
    {
        // Ensure the SyntaxTree corresponds to the SourceText
        if (!this.TryGetText(out var syntaxTreeText))
            throw new ArgumentException("SourceText does not match the provided SyntaxTree.");

        // Get the root node of the syntax tree
        var root = GetRoot();

        // Find the nodes whose span matches the given TextSpan

        var matchingNodes = root.DescendantNodesAndSelf()
            .Where(node => node.Span.Contains(span))
            .Reverse();

        return matchingNodes;
    }

    public SyntaxNode? GetNodeToReplace(TextSpan span)
    {
        var matchingNodes = GetNodesInSpan(span);
        var node = matchingNodes.FirstOrDefault();
        if (span.Length == 0)
        {
            // TEMPORARY:
            // If the length of "span" is 0, then something has been added to the tree.
            // We should get the parent node of the innermost instead.
            node = node?.Parent;
        }
        return node;
    }


    public SyntaxNode? GetNodeForSpan(TextSpan span)
    {
        // Get the first node whose span matches the given TextSpan

        var matchingNodes = GetNodesInSpan(span);
        return matchingNodes.FirstOrDefault();
    }

    public SyntaxTree WithChangedText(SourceText newText)
    {
        var oldText = GetText();

        var changeRanges = newText.GetChangeRanges(oldText);

        if (changeRanges.Count == 0)
            return this;

        var root = GetRoot();

        // A fallback tree is correct but does not carry incremental identity
        // guarantees for the malformed region. Give the next edit one clean
        // full parse before resuming incremental replacement. PreviousFallback
        // is deliberately not sticky, so recovery does not disable incremental
        // parsing for all later edits.
        if (IncrementalParseFallbackReason is not IncrementalParseFallbackReason.None and
            not IncrementalParseFallbackReason.PreviousFallback)
        {
            return ParseTextWithFallback(newText, IncrementalParseFallbackReason.PreviousFallback);
        }

        if (ContainsConditionalDirectives(oldText) || ContainsConditionalDirectives(newText))
            return ParseTextWithFallback(newText, IncrementalParseFallbackReason.ConditionalDirectives);

        if (ShouldFullyReparseChangedText(oldText, newText, changeRanges))
            return ParseTextWithFallback(newText, IncrementalParseFallbackReason.ChangePolicy);

        var changes = newText.GetTextChanges(oldText);

        CompilationUnitSyntax newCompilationUnit = root;
        var updatedDiagnostics = GetDiagnostics()
            .Select(static diagnostic => InternalSyntax.DiagnosticInfo.Create(
                diagnostic.Descriptor,
                diagnostic.Location.SourceSpan,
                diagnostic.GetMessageArgs()))
            .ToList();

        var fallbackReason = IncrementalParseFallbackReason.None;

        foreach (var change in changes)
        {
            var changedNode = GetNodeToReplace(change.Span);

            if (changedNode is null)
                continue;

            // Recovery nodes can own text outside the construct that originally
            // triggered them. Only edits whose replacement region contains recovery
            // syntax need the conservative full-document parse; unrelated malformed
            // siblings must not defeat incremental identity reuse.
            if (ContainsRecoverySyntax(changedNode))
            {
                fallbackReason = IncrementalParseFallbackReason.ExistingRecoverySyntax;
                break;
            }

            var parseResult = ParseNodeFromText(change.Span, newText, changedNode);

            if (parseResult is null)
            {
                // Failed to resolve target syntax type
                fallbackReason = IncrementalParseFallbackReason.NodeParseFailure;
                break;
            }

            if (ContainsRecoverySyntax(parseResult.Value.Node))
            {
                fallbackReason = IncrementalParseFallbackReason.NewRecoverySyntax;
                break;
            }

            newCompilationUnit = newCompilationUnit
                .ReplaceNode(parseResult.Value.ReplacedNode, parseResult.Value.Node);

            updatedDiagnostics = UpdateDiagnostics(
                updatedDiagnostics,
                parseResult.Value.ReplacedNode.FullSpan,
                change,
                parseResult.Value.Diagnostics);
        }

        if (fallbackReason != IncrementalParseFallbackReason.None)
        {
            // Fallback: Reparse the entire tree
            return ParseTextWithFallback(newText, fallbackReason);
        }

        var updatedTree = Create(
            newText,
            newCompilationUnit,
            _options,
            FilePath,
            updatedDiagnostics.OrderBy(static diagnostic => diagnostic.Span.Start), isGenerated: IsGenerated);
        if (!string.Equals(updatedTree.GetRoot().ToFullString(), newText.ToString(), StringComparison.Ordinal))
        {
            return ParseTextWithFallback(newText, IncrementalParseFallbackReason.ReconstructedTextMismatch);
        }

        return updatedTree;
    }

    private static bool ContainsRecoverySyntax(SyntaxNode node)
        => node.DescendantNodesAndSelf().Any(static descendant => descendant.IsMissing) ||
           node.DescendantTokens().Any(static token => token.IsMissing) ||
           node.DescendantTrivia().Any(static trivia => trivia.Kind == SyntaxKind.SkippedTokensTrivia);

    private SyntaxTree ParseTextWithFallback(SourceText newText, IncrementalParseFallbackReason reason)
    {
        if (_options.ThrowOnIncrementalParseFallback)
            throw new IncrementalParseFallbackException(reason, FilePath);

        var tree = ParseTextCore(newText, _options, FilePath, IsGenerated);
        tree.IncrementalParseFallbackReason = reason;
        return tree;
    }

    private static List<InternalSyntax.DiagnosticInfo> UpdateDiagnostics(
        IEnumerable<InternalSyntax.DiagnosticInfo> existingDiagnostics,
        TextSpan replacedSpan,
        TextChange change,
        IEnumerable<InternalSyntax.DiagnosticInfo> replacementDiagnostics)
    {
        var delta = change.NewText.Length - change.Span.Length;
        var diagnostics = new List<InternalSyntax.DiagnosticInfo>();

        foreach (var diagnostic in existingDiagnostics)
        {
            if (IsOwnedByReplacedSpan(diagnostic.Span, replacedSpan))
                continue;

            var span = diagnostic.Span;
            if (span.Start >= change.Span.End)
            {
                span = new TextSpan(span.Start + delta, span.Length);
            }

            diagnostics.Add(InternalSyntax.DiagnosticInfo.Create(
                diagnostic.Descriptor,
                span,
                diagnostic.Args));
        }

        diagnostics.AddRange(replacementDiagnostics);
        return diagnostics;
    }

    private static bool IsOwnedByReplacedSpan(TextSpan diagnosticSpan, TextSpan replacedSpan)
    {
        if (diagnosticSpan.Length != 0)
            return diagnosticSpan.IntersectsWith(replacedSpan);

        return diagnosticSpan.Start >= replacedSpan.Start &&
               diagnosticSpan.Start < replacedSpan.End;
    }

    private static bool ContainsConditionalDirectives(SourceText text)
    {
        using var reader = text.GetTextReader();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.AsSpan().TrimStart();
            if (trimmed.StartsWith("#if", StringComparison.Ordinal) ||
                trimmed.StartsWith("#elif", StringComparison.Ordinal) ||
                trimmed.StartsWith("#else", StringComparison.Ordinal) ||
                trimmed.StartsWith("#endif", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ShouldFullyReparseChangedText(
        SourceText oldText,
        SourceText newText,
        IReadOnlyList<TextChangeRange> changeRanges)
    {
        if (changeRanges.Count != 1)
            return true;

        var change = changeRanges[0];
        if (change.Span.Start == 0 &&
            change.Span.Length == oldText.Length &&
            change.NewLength == newText.Length)
        {
            return true;
        }

        return change.Span.Length > IncrementalParseMaxChangeLength ||
               change.NewLength > IncrementalParseMaxChangeLength;
    }

    private IncrementalParseResult? ParseNodeFromText(TextSpan changeSpan, SourceText newText, SyntaxNode nodeToReplace)
    {
        for (var candidate = nodeToReplace; candidate.Parent is not null; candidate = candidate.Parent)
        {
            Type requestedSyntaxType;
            var parent = candidate.Parent;

            if (changeSpan.Length == 0 && ReferenceEquals(candidate, nodeToReplace))
            {
                requestedSyntaxType = candidate.GetType();
            }
            else if (parent is TypeDeclarationSyntax && candidate is MemberDeclarationSyntax)
            {
                // A standalone MemberDeclaration parse has compilation-unit context and may
                // classify a method-shaped declaration as a global statement. Preserve the
                // concrete member category while reparsing inside a type. If an edit truly
                // changes the declaration category, the type check below triggers the safe
                // full-tree fallback.
                requestedSyntaxType = candidate.GetType();
            }
            else if (parent is BlockStatementSyntax)
            {
                requestedSyntaxType = typeof(StatementSyntax);
            }
            else
            {
                var childType = parent.GetPropertyTypeForChild(candidate);
                if (childType is null)
                    continue;

                requestedSyntaxType = childType;
            }

            var parser = new InternalSyntax.Parser.LanguageParser(string.Empty, _options);
            var parseResult = parser.ParseSyntaxWithDiagnostics(
                requestedSyntaxType,
                newText,
                candidate.FullSpan.Start);
            if (parseResult is null)
                continue;

            var newNode = parseResult.Value.Root.CreateRed();
            if (!requestedSyntaxType.IsInstanceOfType(newNode) || newNode.IsMissing)
                continue;

            return new IncrementalParseResult(candidate, newNode, parseResult.Value.Diagnostics);
        }

        return null;
    }

    private readonly record struct IncrementalParseResult(
        SyntaxNode ReplacedNode,
        SyntaxNode Node,
        IReadOnlyList<InternalSyntax.DiagnosticInfo> Diagnostics);
}

public static partial class SyntaxFactory
{
    public static SyntaxTree ParseSyntaxTree(SourceText sourceText, ParseOptions? options = null, string? filePath = null) => Syntax.SyntaxTree.ParseText(sourceText, options, filePath);

    public static SyntaxTree ParseSyntaxTree(string text, ParseOptions? options = null, Encoding? encoding = null, string? filePath = null) => Syntax.SyntaxTree.ParseText(text, options, encoding, filePath);

    //public static SyntaxTree SyntaxTree(CompilationUnitSyntax root, ParseOptions? options = default, string path = "", Encoding? encoding = default)
    //    => Syntax.SyntaxTree.Create(root, options, encoding);
}
