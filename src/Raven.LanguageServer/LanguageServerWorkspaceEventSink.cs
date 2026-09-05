using Microsoft.Extensions.Logging;

using OmniSharp.Extensions.LanguageServer.Protocol;

using Raven.CodeAnalysis;

namespace Raven.LanguageServer;

internal sealed class LanguageServerWorkspaceEventSink : IWorkspaceEventSink
{
    private readonly ILogger<LanguageServerWorkspaceEventSink> _logger;

    public LanguageServerWorkspaceEventSink(ILogger<LanguageServerWorkspaceEventSink> logger)
    {
        _logger = logger;
    }

    public void Report(WorkspaceEvent workspaceEvent)
    {
        if (workspaceEvent.Operation == "documentAnalyzer.failure")
        {
            _logger.LogWarning(
                "Analyzer failure in project {Project}, document {Document}: {Detail}",
                workspaceEvent.ProjectName ?? "<none>",
                workspaceEvent.DocumentPath ?? "<none>",
                workspaceEvent.Detail);
        }
        else if (workspaceEvent.ElapsedMilliseconds >= 50)
        {
            _logger.LogInformation(
                "Workspace event {Operation}: elapsed={ElapsedMs:F1}ms project={Project} document={Document} detail={Detail}.",
                workspaceEvent.Operation,
                workspaceEvent.ElapsedMilliseconds,
                workspaceEvent.ProjectName ?? "<none>",
                workspaceEvent.DocumentPath ?? "<none>",
                workspaceEvent.Detail);
        }
        else
        {
            _logger.LogDebug(
                "Workspace event {Operation}: elapsed={ElapsedMs:F1}ms project={Project} document={Document} detail={Detail}.",
                workspaceEvent.Operation,
                workspaceEvent.ElapsedMilliseconds,
                workspaceEvent.ProjectName ?? "<none>",
                workspaceEvent.DocumentPath ?? "<none>",
                workspaceEvent.Detail);
        }

        LanguageServerPerformanceInstrumentation.RecordOperation(
            workspaceEvent.Operation,
            workspaceEvent.DocumentPath is { Length: > 0 } documentPath
                ? DocumentUri.FromFileSystemPath(documentPath)
                : null,
            version: null,
            totalMs: workspaceEvent.ElapsedMilliseconds,
            detail: $"{workspaceEvent.ProjectName ?? "<none>"} {workspaceEvent.DocumentPath ?? "<none>"} {workspaceEvent.Detail}");
    }
}
