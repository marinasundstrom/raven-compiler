using System.Text;
using System.Text.Json;

using MediatR;

using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis.Syntax;

namespace Raven.LanguageServer;

internal static class GeneratedSourceDocument
{
    public const string Scheme = "raven-generated";

    public static DocumentUri GetUri(DocumentStore.DocumentAnalysisContext context, DocumentUri origin, SyntaxTree tree)
    {
        if (!context.Compilation.SyntaxTrees.Contains(tree) ||
            context.Document.Project.Documents.Any(document => document.FilePath == tree.FilePath))
            return DocumentUri.FromFileSystemPath(tree.FilePath);

        if (TryParse(origin, out var sourceOrigin, out _))
            origin = sourceOrigin;
        var displayPath = Path.GetFileName(Path.GetDirectoryName(tree.FilePath)) + "/" + Path.GetFileName(tree.FilePath);
        var query = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { origin.ToString(), tree.FilePath })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return DocumentUri.Parse($"{Scheme}:/{displayPath}?{query}");
    }

    public static bool TryParse(DocumentUri uri, out DocumentUri origin, out string path)
    {
        origin = null!;
        path = string.Empty;
        if (!Uri.TryCreate(uri.ToString(), UriKind.Absolute, out var parsed) || parsed.Scheme != Scheme)
            return false;
        try
        {
            var query = parsed.Query.TrimStart('?').Replace('-', '+').Replace('_', '/');
            query = query.PadRight((query.Length + 3) / 4 * 4, '=');
            var fields = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(Convert.FromBase64String(query)));
            if (fields is not { Length: 2 } || !Uri.TryCreate(fields[0], UriKind.Absolute, out var originUri) || !originUri.IsFile)
                return false;
            origin = DocumentUri.Parse(fields[0]);
            path = fields[1];
            return !string.IsNullOrEmpty(path);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }
}

[Method("raven/generatedSource", Direction.ClientToServer)]
internal sealed record GeneratedSourceParams : IRequest<GeneratedSourceResult?>
{
    public required DocumentUri Uri { get; init; }
}

internal sealed record GeneratedSourceResult
{
    public required string Text { get; init; }
    public required Diagnostic[] Diagnostics { get; init; }
}

internal sealed class GeneratedSourceHandler(DocumentStore documents) : IJsonRpcRequestHandler<GeneratedSourceParams, GeneratedSourceResult?>
{
    public async Task<GeneratedSourceResult?> Handle(GeneratedSourceParams request, CancellationToken cancellationToken)
    {
        if (!GeneratedSourceDocument.TryParse(request.Uri, out _, out _))
            return null;
        var context = await documents.GetAnalysisContextAsync(request.Uri, cancellationToken).ConfigureAwait(false);
        if (context is null)
            return null;

        var diagnostics = await documents.GetDiagnosticsAsync(request.Uri, cancellationToken).ConfigureAwait(false);
        return new GeneratedSourceResult
        {
            Text = context.Value.SourceText.ToString(),
            Diagnostics = diagnostics.ToArray()
        };
    }
}
