using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Raven.CodeAnalysis;

public static class EditorConfigDiagnosticOptions
{
    private const string EditorConfigFileName = ".editorconfig";
    private const string DiagnosticSeverityPrefix = "dotnet_diagnostic.";
    private const string DiagnosticSeveritySuffix = ".severity";
    private const string AnalyzerSeverityKey = "dotnet_analyzer_diagnostic.severity";

    public static CompilationOptions ApplyDiagnosticSeverityOptions(
        CompilationOptions options,
        string? projectOrSourcePath,
        IEnumerable<string?> sourceFilePaths)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = LoadDiagnosticSeverityOptions(projectOrSourcePath, sourceFilePaths);
        return diagnostics.Count == 0
            ? options
            : options.WithExactSpecificDiagnosticOptions(options.SpecificDiagnosticOptions.SetItems(diagnostics));
    }

    public static CompilationOptions ApplyOptions(
        CompilationOptions options,
        string? projectOrSourcePath,
        IEnumerable<string?> sourceFilePaths)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sourcePaths = sourceFilePaths.ToArray();
        var generatedCode = LoadGeneratedCodeOptions(projectOrSourcePath, sourcePaths);
        var result = ApplyDiagnosticSeverityOptions(options, projectOrSourcePath, sourcePaths);
        if (!GeneratedCodeOptionsEqual(result.GeneratedCodeOptions, generatedCode))
            result = result.WithGeneratedCodeOptions(generatedCode);

        return result;
    }

    public static ImmutableDictionary<string, ReportDiagnostic> LoadDiagnosticSeverityOptions(
        string? projectOrSourcePath,
        IEnumerable<string?> sourceFilePaths)
    {
        ArgumentNullException.ThrowIfNull(sourceFilePaths);

        var normalizedSourcePaths = sourceFilePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedSourcePaths.Length == 0 && !string.IsNullOrWhiteSpace(projectOrSourcePath))
        {
            var anchorPath = Path.GetFullPath(projectOrSourcePath!);
            var directory = Directory.Exists(anchorPath)
                ? anchorPath
                : Path.GetDirectoryName(anchorPath);
            if (!string.IsNullOrWhiteSpace(directory))
                normalizedSourcePaths = [Path.Combine(directory!, $"__editorconfig__{RavenFileExtensions.Raven}")];
        }

        if (normalizedSourcePaths.Length == 0)
            return ImmutableDictionary<string, ReportDiagnostic>.Empty;

        var merged = ImmutableDictionary.Create<string, ReportDiagnostic>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in normalizedSourcePaths)
        {
            foreach (var (key, value) in LoadPropertiesForSourcePath(sourcePath))
            {
                if (TryParseDiagnosticOption(key, value, out var id, out var option))
                    merged = merged.SetItem(id, option);
            }
        }

        return merged;
    }

    public static ImmutableDictionary<string, bool> LoadGeneratedCodeOptions(
        string? projectOrSourcePath,
        IEnumerable<string?> sourceFilePaths)
    {
        ArgumentNullException.ThrowIfNull(sourceFilePaths);
        _ = projectOrSourcePath;

        var result = ImmutableDictionary.CreateBuilder<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in sourceFilePaths
                     .Where(static path => !string.IsNullOrWhiteSpace(path))
                     .Select(static path => Path.GetFullPath(path!))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var properties = LoadPropertiesForSourcePath(sourcePath);
            if (properties.TryGetValue("generated_code", out var value) && bool.TryParse(value, out var isGenerated))
                result[sourcePath] = isGenerated;
        }

        return result.ToImmutable();
    }

    private static ImmutableDictionary<string, string> LoadPropertiesForSourcePath(string sourcePath)
    {
        var applicableFiles = DiscoverApplicableEditorConfigs(sourcePath);
        if (applicableFiles.Count == 0)
            return ImmutableDictionary<string, string>.Empty;

        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in applicableFiles)
        {
            var relativePath = NormalizePath(Path.GetRelativePath(file.DirectoryPath, sourcePath));
            var fileName = Path.GetFileName(sourcePath);

            foreach (var section in file.Sections)
            {
                if (!section.IsMatch(relativePath, fileName))
                    continue;

                foreach (var (key, value) in section.Properties)
                    result[key] = value;
            }
        }

        return result.ToImmutable();
    }

    private static bool GeneratedCodeOptionsEqual(
        ImmutableDictionary<string, bool> left,
        ImmutableDictionary<string, bool> right)
    {
        if (left.Count != right.Count)
            return false;
        return left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static List<ParsedEditorConfig> DiscoverApplicableEditorConfigs(string sourcePath)
    {
        var files = new List<ParsedEditorConfig>();
        var directory = Path.GetDirectoryName(sourcePath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var filePath = Path.Combine(directory, EditorConfigFileName);
            if (File.Exists(filePath))
            {
                var parsed = ParseEditorConfig(filePath, directory);
                files.Add(parsed);
                if (parsed.IsRoot)
                    break;
            }

            var parent = Directory.GetParent(directory);
            directory = parent?.FullName;
        }

        files.Reverse();
        return files;
    }

    private static ParsedEditorConfig ParseEditorConfig(string filePath, string directoryPath)
    {
        var sections = new List<EditorConfigSection>();
        var isRoot = false;
        EditorConfigSectionBuilder? currentSection = null;

        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length >= 2)
            {
                if (currentSection is not null)
                    sections.Add(currentSection.Build());

                var pattern = line[1..^1].Trim();
                currentSection = new EditorConfigSectionBuilder(pattern);
                continue;
            }

            var splitIndex = line.IndexOf('=');
            if (splitIndex < 0)
                splitIndex = line.IndexOf(':');
            if (splitIndex <= 0)
                continue;

            var key = line[..splitIndex].Trim();
            var value = line[(splitIndex + 1)..].Trim();

            if (currentSection is null)
            {
                if (key.Equals("root", StringComparison.OrdinalIgnoreCase) &&
                    bool.TryParse(value, out var rootValue) &&
                    rootValue)
                {
                    isRoot = true;
                }

                continue;
            }

            currentSection.Properties[key] = value;
        }

        if (currentSection is not null)
            sections.Add(currentSection.Build());

        return new ParsedEditorConfig(directoryPath, isRoot, sections);
    }

    private static bool TryParseDiagnosticOption(string key, string value, out string diagnosticId, out ReportDiagnostic option)
    {
        if (key.Equals(AnalyzerSeverityKey, StringComparison.OrdinalIgnoreCase))
        {
            diagnosticId = "*";
            return TryParseSeverity(value, out option);
        }

        if (!key.StartsWith(DiagnosticSeverityPrefix, StringComparison.OrdinalIgnoreCase) ||
            !key.EndsWith(DiagnosticSeveritySuffix, StringComparison.OrdinalIgnoreCase))
        {
            diagnosticId = string.Empty;
            option = default;
            return false;
        }

        var idStart = DiagnosticSeverityPrefix.Length;
        var idLength = key.Length - DiagnosticSeverityPrefix.Length - DiagnosticSeveritySuffix.Length;
        if (idLength <= 0)
        {
            diagnosticId = string.Empty;
            option = default;
            return false;
        }

        diagnosticId = key.Substring(idStart, idLength).Trim();
        if (diagnosticId.Length == 0)
        {
            option = default;
            return false;
        }

        return TryParseSeverity(value, out option);
    }

    private static bool TryParseSeverity(string value, out ReportDiagnostic option)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "none":
            case "suppress":
                option = ReportDiagnostic.Suppress;
                return true;
            case "silent":
            case "hidden":
                option = ReportDiagnostic.Hidden;
                return true;
            case "suggestion":
            case "info":
                option = ReportDiagnostic.Info;
                return true;
            case "warning":
            case "warn":
                option = ReportDiagnostic.Warn;
                return true;
            case "error":
                option = ReportDiagnostic.Error;
                return true;
            case "default":
                option = ReportDiagnostic.Default;
                return true;
            default:
                option = default;
                return false;
        }
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/');

    private sealed record ParsedEditorConfig(string DirectoryPath, bool IsRoot, IReadOnlyList<EditorConfigSection> Sections);

    private sealed class EditorConfigSectionBuilder
    {
        public EditorConfigSectionBuilder(string pattern)
        {
            Pattern = pattern;
            Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Pattern { get; }

        public Dictionary<string, string> Properties { get; }

        public EditorConfigSection Build()
            => new(Pattern, Properties.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class EditorConfigSection
    {
        private readonly string _pattern;
        private readonly ImmutableArray<Regex> _matchers;

        public EditorConfigSection(string pattern, ImmutableDictionary<string, string> properties)
        {
            _pattern = NormalizePath(pattern.Trim());
            Properties = properties;
            _matchers = BuildMatchers(_pattern);
        }

        public ImmutableDictionary<string, string> Properties { get; }

        public bool IsMatch(string relativePath, string fileName)
        {
            var target = _pattern.Contains('/') ? relativePath : fileName;
            return _matchers.Any(regex => regex.IsMatch(target));
        }

        private static ImmutableArray<Regex> BuildMatchers(string pattern)
        {
            if (pattern.Length == 0)
                return ImmutableArray<Regex>.Empty;

            var expanded = ExpandBraces(pattern)
                .Distinct(StringComparer.Ordinal)
                .Select(static p => new Regex($"^{ConvertGlobToRegex(p)}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToImmutableArray();

            return expanded;
        }

        private static IEnumerable<string> ExpandBraces(string pattern)
        {
            var openIndex = pattern.IndexOf('{');
            if (openIndex < 0)
                return [pattern];

            var closeIndex = FindMatchingBrace(pattern, openIndex);
            if (closeIndex <= openIndex)
                return [pattern];

            var prefix = pattern[..openIndex];
            var suffix = pattern[(closeIndex + 1)..];
            var content = pattern[(openIndex + 1)..closeIndex];
            var alternatives = SplitBraceAlternatives(content);

            var results = new List<string>();
            foreach (var alternative in alternatives)
            {
                foreach (var expanded in ExpandBraces(prefix + alternative + suffix))
                    results.Add(expanded);
            }

            return results;
        }

        private static int FindMatchingBrace(string pattern, int openIndex)
        {
            var depth = 0;
            for (var i = openIndex; i < pattern.Length; i++)
            {
                if (pattern[i] == '{')
                    depth++;
                else if (pattern[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static IEnumerable<string> SplitBraceAlternatives(string content)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;

            for (var i = 0; i < content.Length; i++)
            {
                var ch = content[i];
                if (ch == '{')
                {
                    depth++;
                    continue;
                }

                if (ch == '}')
                {
                    depth--;
                    continue;
                }

                if (ch == ',' && depth == 0)
                {
                    parts.Add(content[start..i]);
                    start = i + 1;
                }
            }

            parts.Add(content[start..]);
            return parts;
        }

        private static string ConvertGlobToRegex(string pattern)
        {
            var normalized = NormalizePath(pattern);
            if (normalized.StartsWith('/'))
                normalized = normalized[1..];

            var regex = new System.Text.StringBuilder();
            for (var i = 0; i < normalized.Length; i++)
            {
                var ch = normalized[i];
                if (ch == '*')
                {
                    var isDoubleStar = i + 1 < normalized.Length && normalized[i + 1] == '*';
                    if (isDoubleStar)
                    {
                        var hasTrailingSlash = i + 2 < normalized.Length && normalized[i + 2] == '/';
                        regex.Append(hasTrailingSlash ? "(?:.*/)?" : ".*");
                        i += hasTrailingSlash ? 2 : 1;
                        continue;
                    }

                    regex.Append("[^/]*");
                    continue;
                }

                if (ch == '?')
                {
                    regex.Append("[^/]");
                    continue;
                }

                regex.Append(Regex.Escape(ch.ToString()));
            }

            return regex.ToString();
        }
    }
}
