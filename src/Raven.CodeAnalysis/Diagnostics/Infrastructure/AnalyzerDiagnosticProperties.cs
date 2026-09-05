namespace Raven.CodeAnalysis.Diagnostics;

internal static class AnalyzerDiagnosticProperties
{
    public const string AnalyzerName = "Raven.AnalyzerName";

    public static Diagnostic WithAnalyzerOrigin(Diagnostic diagnostic, DiagnosticAnalyzer analyzer)
        => new(diagnostic.Descriptor, diagnostic.Location, diagnostic.GetMessageArgs(),
            diagnostic.Severity, diagnostic.IsSuppressed,
            diagnostic.Properties.SetItem(AnalyzerName, analyzer.GetType().FullName ?? analyzer.GetType().Name));
}
