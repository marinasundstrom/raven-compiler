namespace Raven.CodeAnalysis.Diagnostics;

/// <summary>Controls analyzer callbacks and diagnostic reporting for source-generated trees.</summary>
[Flags]
public enum GeneratedCodeAnalysisFlags
{
    None = 0,
    Analyze = 1,
    ReportDiagnostics = 2
}
