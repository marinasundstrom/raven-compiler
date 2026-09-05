using System.Collections.Immutable;
using System.Text;

using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis;

public readonly struct GeneratorInitializationContext
{
    internal GeneratorInitializationContext(CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
    }

    public CancellationToken CancellationToken { get; }
}

public sealed class GeneratorExecutionContext
{
    private readonly Dictionary<string, SourceText> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Diagnostic> _diagnostics = [];

    internal GeneratorExecutionContext(Compilation compilation, CancellationToken cancellationToken)
    {
        Compilation = compilation;
        CancellationToken = cancellationToken;
    }

    public Compilation Compilation { get; }

    public CancellationToken CancellationToken { get; }

    public void AddSource(string hintName, string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        AddSource(hintName, SourceText.From(source, Encoding.UTF8));
    }

    public void AddSource(string hintName, SourceText sourceText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hintName);
        ArgumentNullException.ThrowIfNull(sourceText);

        if (Path.IsPathRooted(hintName))
            throw new ArgumentException("The hint name must be a relative path.", nameof(hintName));

        var normalizedHintName = NormalizeHintName(hintName);
        if (!_sources.TryAdd(normalizedHintName, sourceText))
            throw new ArgumentException($"A source named '{hintName}' has already been added.", nameof(hintName));
    }

    public void ReportDiagnostic(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _diagnostics.Add(diagnostic);
    }

    internal ImmutableArray<GeneratedSourceResult> GetGeneratedSources(string generatorName, string? outputPath = null)
        => _sources
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(item => new GeneratedSourceResult(
                item.Key,
                item.Value,
                Syntax.SyntaxTree.ParseGeneratedText(
                    item.Value,
                    path: Path.Combine(outputPath ?? "generated", SanitizePathSegment(generatorName), item.Key))))
            .ToImmutableArray();

    internal ImmutableArray<Diagnostic> GetDiagnostics() => _diagnostics.ToImmutableArray();

    private static string NormalizeHintName(string hintName)
    {
        var normalized = hintName.Replace('\\', '/');
        if (normalized.Split('/').Any(static segment => segment is "" or "." or ".."))
            throw new ArgumentException("The hint name must not contain empty, '.' or '..' path segments.", nameof(hintName));

        return normalized.EndsWith(".rvn", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".rvn";
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
    }
}
