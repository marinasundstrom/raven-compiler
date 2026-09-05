using Microsoft.Extensions.Logging;

using Raven.CodeAnalysis;

namespace Raven.LanguageServer.Tests;

public sealed class LanguageServerWorkspaceEventSinkTests
{
    [Fact]
    public void AnalyzerFailure_IsVisibleEvenWhenItFailsImmediately()
    {
        var logger = new CapturingLogger();
        var sink = new LanguageServerWorkspaceEventSink(logger);

        sink.Report(new WorkspaceEvent("documentAnalyzer.failure", "App", "/tmp/input.rvn", 0,
            "ExampleAnalyzer: phase=SyntaxNode, exception=InvalidOperationException: failed"));

        var entry = logger.Entries.Single();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("ExampleAnalyzer");
        entry.Message.ShouldContain("SyntaxNode");
        entry.Message.ShouldContain("App");
        entry.Message.ShouldContain("/tmp/input.rvn");
    }

    private sealed class CapturingLogger : ILogger<LanguageServerWorkspaceEventSink>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
