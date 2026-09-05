using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using Raven;
using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

using Spectre.Console;

using static Raven.AppHostBuilder;
using static Raven.ConsoleEx;

if (args.Length == 1 && args[0] is "--version" or "version")
{
    Console.WriteLine(
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown");
    return;
}

var stopwatch = Stopwatch.StartNew();
var invocationName = Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs().FirstOrDefault() ?? "rvn");
var isFrontendInvocation = string.Equals(
    Environment.GetEnvironmentVariable("RAVEN_FRONTEND_INVOCATION"),
    "1",
    StringComparison.Ordinal);
var isCompilerDriverInvocation =
    string.Equals(invocationName, "rvnc", StringComparison.OrdinalIgnoreCase) &&
    !isFrontendInvocation;

// Options:
// --framework <tfm> - target framework
// --no-framework-references - do not add the default .NET targeting-pack references
// --target-core-library <path> - retarget emitted core type scopes to this assembly identity
// --configuration <name> - MSBuild configuration used to evaluate a project
// --refs <path>     - additional metadata reference (repeatable)
// --define <symbols> - conditional compilation symbols (comma/semicolon separated)
// --raven-core <path> - path to a prebuilt Raven.Core.dll (skips embedded core types)
// --emit-core-types-only - embed the Raven.Core shims instead of referencing Raven.Core.dll
// --output-type <console|classlib> - output kind
// --dependency-file <path> - write observed compile-time file dependencies
// --unsafe           - enable unsafe mode (required for pointer declarations/usages)
// --no-global-statements - disable top-level/global statements
// --no-namespace-members - disable namespace-scope function and const declarations
// --no-namespace-member-imports - disable namespace imports from [TopLevel] containers
// --runtime-async    - enable runtime-async metadata emission
// --no-runtime-async - disable runtime-async metadata emission
// -o <path>         - output assembly path
// -s [flat|group]   - display the syntax tree (single file only)
// -d [plain|pretty[:no-diagnostics]] - dump syntax (single file only)
// --dump-macros [original|expanded|both][:plain|pretty[:no-diagnostics]] - dump macro source views (single file only)
// -r                - print the source (single file only)
// -b                - print binder tree (single file only)
// -bt               - print binder and bound tree (single file only)
// -q, --quote       - print AST as C# compilable code
// --symbols [list|hierarchy] - inspect symbols produced from source
// --doc-tool [ravendoc|comments] - documentation generator
// --doc-format [md|xml] - documentation format (comment emission only)
// --no-emit         - skip emitting the output assembly
// --publish         - emit runtime artifacts (.runtimeconfig.json and apphost for console apps)
// --fix             - apply supported code fixes before diagnostics/emission
// --format          - normalize whitespace and indentation in source files
// --highlight       - display diagnostics with highlighted source
// --suggestions     - display instructional rewrite suggestions for diagnostics that provide them
// --returned-value-handling <default|full|none|info|warning|error> - configure full RAV9034 returned-value analysis
// -h, --help        - display help
// --run             - execute the produced assembly when compilation succeeds (console apps only)

if (args.Length > 0 && string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
{
    AnsiConsole.MarkupLine("[red]rvnc is the compiler driver and does not scaffold projects. Use 'rvn init'.[/]");
    Environment.ExitCode = 1;
    return;
}

var sourceFiles = new List<string>();
var applicationArguments = new List<string>();
var additionalRefs = new List<string>();
var externalConstantValues = new Dictionary<string, string>(StringComparer.Ordinal);
var externalConstantOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
var conditionalSymbols = new HashSet<string>(StringComparer.Ordinal);
string? targetFrameworkTfm = null;
string? targetCoreLibraryPath = null;
var includeFrameworkReferences = true;
string? requestedConfiguration = null;
string? outputPath = null;
string? dependencyFilePath = null;
string? generatedFilesOutputPath = null;
var generatedFilesOutputPathSpecified = false;
var outputKind = OutputKind.ConsoleApplication;
string? ravenCorePath = null;
var ravenCoreExplicitlyProvided = false;
var embedCoreTypes = false;
var skipDefaultRavenCoreLookup = false;
var allowUnsafe = false;
var allowGlobalStatements = true;
var allowNamespaceMembers = true;
var allowNamespaceMembersSpecified = false;
var allowNamespaceMemberImports = true;
var allowNamespaceMemberImportsSpecified = false;
bool? runtimeAsyncOverride = null;

var printSyntaxTree = false;
var printSyntaxTreeInternal = false;
var syntaxTreeFormat = SyntaxTreeFormat.Flat;
var printSyntax = false;
var printRawSyntax = false;
var prettyIncludeDiagnostics = true;
string? macroSourceDumpTarget = null;
var printMacroSyntax = false;
var printMacroRawSyntax = false;
var macroPrettyIncludeDiagnostics = true;
var printBinders = false;
var printBoundTree = false;
var printBoundTreeErrors = true;
var boundTreeView = BoundTreeView.Original;
var symbolDumpMode = SymbolDumpMode.None;
var printParseSequence = false;
var parseSequenceThrottleMilliseconds = 0;
var showHelp = false;
var noEmit = false;
var fix = false;
var format = false;
var hasInvalidOption = false;
var highlightDiagnostics = false;
var showSuggestions = false;
ReturnedValueHandlingMode? returnedValueHandlingModeOverride = null;
ReportDiagnostic? returnedValueHandlingDiagnostic = null;
var quote = false;
var runIlVerify = false;
string? ilVerifyPath = null;
var enableAsyncInvestigation = false;
string asyncInvestigationLabel = "Step14";
var asyncInvestigationScope = AsyncInvestigationPointerLabelScope.FieldOnly;
var enableOverloadLog = false;
string? overloadLogPath = null;
OverloadResolutionLog? overloadResolutionLog = null;
var run = false;
var publish = false;
var restoreProjectReferences = true;
var emitDocs = false;
string? documentationOutputPath = null;
var documentationFormat = DocumentationFormat.Markdown;
var documentationTool = DocumentationTool.RavenDoc;
var documentationFormatExplicitlySet = false;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--")
    {
        applicationArguments.AddRange(args.Skip(i + 1));
        break;
    }

    switch (args[i])
    {
        case "-o":
        case "--output":
            if (i + 1 < args.Length)
                outputPath = args[++i];
            break;
        case "--generated-files-output-path":
            if (i + 1 < args.Length)
            {
                generatedFilesOutputPath = args[++i];
                generatedFilesOutputPathSpecified = true;
            }
            else
                hasInvalidOption = true;
            break;
        case "--dependency-file":
            if (i + 1 < args.Length)
                dependencyFilePath = args[++i];
            break;
        case "-s":
        case "--syntax-tree":
            printSyntaxTree = true;
            if (!TryParseSyntaxTreeFormat(args, ref i, out syntaxTreeFormat))
                hasInvalidOption = true;
            break;
        case "-si":
        case "--syntax-tree--internal":
            printSyntaxTreeInternal = true;
            hasInvalidOption = false;
            break;
        case "--display-tree":
            printSyntaxTree = true;
            syntaxTreeFormat = SyntaxTreeFormat.Group;
            break;
        case "-se":
        case "--display-expanded-tree":
            printSyntaxTree = true;
            syntaxTreeFormat = SyntaxTreeFormat.Flat;
            break;
        case "-d":
        case "--dump":
            if (!TryParseSyntaxDumpFormat(args, ref i, out printRawSyntax, out printSyntax,
                    out prettyIncludeDiagnostics))
            {
                hasInvalidOption = true;
            }
            break;
        case "--dump-macros":
        case "--macro-source":
            if (!TryParseMacroSourceDumpFormat(
                    args,
                    ref i,
                    out macroSourceDumpTarget,
                    out printMacroRawSyntax,
                    out printMacroSyntax,
                    out macroPrettyIncludeDiagnostics))
            {
                hasInvalidOption = true;
            }
            break;
        case "--print-syntax":
            printRawSyntax = true;
            printSyntax = false;
            break;
        case "-dp":
        case "--pretty-print":
            printRawSyntax = false;
            printSyntax = true;
            prettyIncludeDiagnostics = true;
            break;
        case "-b":
        case "--display-binders":
            printBinders = true;
            break;
        case "-bt":
        case "--bound-tree":
        case "--display-bound-tree":
            printBoundTree = true;
            break;
        case "--bound-tree-view":
        case "--bt-view":
            if (!TryParseBoundTreeView(args, ref i, out var parsedBoundTreeView))
                hasInvalidOption = true;
            else
                boundTreeView = parsedBoundTreeView;
            break;
        case "-bte":
            printBoundTree = true;
            printBoundTreeErrors = true;
            break;
        case "-ps":
        case "--parse-sequence":
            printParseSequence = true;
            break;
        case "--ps-delay":
        case "--parse-sequence-delay":
            if (!TryParseNonNegativeInt(args, ref i, out parseSequenceThrottleMilliseconds))
                hasInvalidOption = true;
            break;
        case "--symbols":
        case "--dump-symbols":
            if (!TryParseSymbolDumpMode(args, ref i, out var mode))
                hasInvalidOption = true;
            else
                symbolDumpMode = mode;
            break;
        case "--no-emit":
            noEmit = true;
            break;
        case "--no-project-restore":
            restoreProjectReferences = false;
            break;
        case "--fix":
            fix = true;
            break;
        case "--format":
            format = true;
            break;
        case "--highlight":
            highlightDiagnostics = true;
            break;
        case "--suggestions":
            showSuggestions = true;
            break;
        case "--returned-value-handling":
        case "--returned-value-diagnostic":
            if (!TryParseReturnedValueHandlingDiagnostic(args, ref i, out returnedValueHandlingModeOverride, out returnedValueHandlingDiagnostic))
                hasInvalidOption = true;
            break;
        case "--force-returned-value-handling":
            returnedValueHandlingModeOverride = ReturnedValueHandlingMode.Full;
            returnedValueHandlingDiagnostic = ReportDiagnostic.Error;
            break;
        case "--quote":
        case "-q":
            quote = true;
            break;
        case "--doc":
        case "--emit-docs":
            emitDocs = true;
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                documentationOutputPath = args[++i];
            break;
        case "--doc-output":
            emitDocs = true;
            if (i + 1 < args.Length)
                documentationOutputPath = args[++i];
            else
                hasInvalidOption = true;
            break;
        case "--doc-format":
            if (!TryParseDocumentationFormat(args, ref i, out var parsedFormat))
                hasInvalidOption = true;
            else
                documentationFormat = parsedFormat;
            documentationFormatExplicitlySet = true;
            break;
        case "--doc-tool":
            if (!TryParseDocumentationTool(args, ref i, out var parsedTool))
                hasInvalidOption = true;
            else
                documentationTool = parsedTool;
            break;
        case "--ilverify":
            runIlVerify = true;
            break;
        case "--ilverify-path":
            if (i + 1 < args.Length)
                ilVerifyPath = args[++i];
            else
                hasInvalidOption = true;
            break;
        case "--async-investigation":
            enableAsyncInvestigation = true;
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                asyncInvestigationLabel = args[++i];
            break;
        case "--async-investigation-scope":
            if (i + 1 < args.Length)
            {
                var scopeValue = args[++i];
                if (string.Equals(scopeValue, "method", StringComparison.OrdinalIgnoreCase))
                {
                    asyncInvestigationScope = AsyncInvestigationPointerLabelScope.IncludeAsyncMethodName;
                }
                else if (!string.Equals(scopeValue, "field", StringComparison.OrdinalIgnoreCase))
                {
                    hasInvalidOption = true;
                }
            }
            else
            {
                hasInvalidOption = true;
            }
            break;
        case "--overload-log":
            enableOverloadLog = true;
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                overloadLogPath = args[++i];
            break;
        case "--ref":
        case "--refs":
            if (i + 1 < args.Length)
                additionalRefs.Add(args[++i]);
            break;
        case "--no-framework-references":
            includeFrameworkReferences = false;
            break;
        case "--target-core-library":
            if (i + 1 < args.Length)
                targetCoreLibraryPath = args[++i];
            else
                hasInvalidOption = true;
            break;
        case "--define":
        case "-define":
            if (i + 1 < args.Length)
            {
                foreach (var symbol in args[++i].Split(
                             [';', ','],
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    conditionalSymbols.Add(symbol);
                }
            }
            else
            {
                hasInvalidOption = true;
            }
            break;
        case "--constant":
            if (i + 1 >= args.Length || !TryParseExternalConstant(args[++i], externalConstantValues))
                hasInvalidOption = true;
            break;
        case "--constant-overrides":
            if (i + 1 >= args.Length || !TryDecodeExternalConstantOverrides(args[++i], externalConstantOverrides))
                hasInvalidOption = true;
            break;
        case "--raven-core":
            if (i + 1 < args.Length)
            {
                ravenCorePath = args[++i];
                embedCoreTypes = false;
                ravenCoreExplicitlyProvided = true;
            }
            else
            {
                hasInvalidOption = true;
            }
            break;
        case "--emit-core-types-only":
            embedCoreTypes = true;
            skipDefaultRavenCoreLookup = true;
            ravenCorePath = null;
            break;
        case "--run":
            run = true;
            break;
        case "--publish":
            publish = true;
            break;
        case "--output-type":
            if (!TryParseOutputKind(args, ref i, out var kind))
                hasInvalidOption = true;
            else
                outputKind = kind;
            break;
        case "--unsafe":
            allowUnsafe = true;
            break;
        case "--global-statements":
            allowGlobalStatements = true;
            break;
        case "--no-global-statements":
            allowGlobalStatements = false;
            break;
        case "--namespace-members":
        case "--top-level-members":
            allowNamespaceMembers = true;
            allowNamespaceMembersSpecified = true;
            break;
        case "--no-namespace-members":
        case "--no-top-level-members":
            allowNamespaceMembers = false;
            allowNamespaceMembersSpecified = true;
            break;
        case "--namespace-member-imports":
        case "--top-level-member-imports":
            allowNamespaceMemberImports = true;
            allowNamespaceMemberImportsSpecified = true;
            break;
        case "--no-namespace-member-imports":
        case "--no-top-level-member-imports":
            allowNamespaceMemberImports = false;
            allowNamespaceMemberImportsSpecified = true;
            break;
        case "--runtime-async":
            runtimeAsyncOverride = true;
            break;
        case "--no-runtime-async":
            runtimeAsyncOverride = false;
            break;
        case "--framework":
            if (i + 1 < args.Length)
                targetFrameworkTfm = args[++i];
            break;
        case "--configuration":
            if (i + 1 < args.Length)
                requestedConfiguration = args[++i];
            break;
        case "-h":
        case "--help":
            showHelp = true;
            break;
        default:
            if (!args[i].StartsWith('-'))
                sourceFiles.Add(args[i]);
            break;
    }
}

if (showHelp || hasInvalidOption)
{
    if (hasInvalidOption)
        Environment.ExitCode = 1;
    else
        Environment.ExitCode = 0;

    PrintHelp(isCompilerDriverInvocation);
    return;
}

if (applicationArguments.Count > 0 && !run)
{
    AnsiConsole.MarkupLine("[red]Application arguments after '--' require --run.[/]");
    Environment.ExitCode = 1;
    return;
}

if (isCompilerDriverInvocation &&
    TryGetCompilerDriverRejectedOption(
        run,
        publish,
        fix,
        format,
        emitDocs,
        highlightDiagnostics,
        showSuggestions,
        printSyntaxTree,
        printSyntaxTreeInternal,
        printRawSyntax,
        printSyntax,
        macroSourceDumpTarget,
        printBinders,
        printBoundTree,
        printParseSequence,
        quote,
        symbolDumpMode,
        out var rejectedOption))
{
    AnsiConsole.MarkupLine(
        $"[red]rvnc is the compiler driver and does not support '{Markup.Escape(rejectedOption)}'. Use 'rvn' or 'rvn dev ...' for frontend tooling commands.[/]");
    Environment.ExitCode = 1;
    return;
}

if (printParseSequence)
{
    SyntaxParserFlags.PrintParseSequence = true;
    SyntaxParserFlags.ParseSequenceThrottleMilliseconds = parseSequenceThrottleMilliseconds;
}

if (sourceFiles.Count == 0)
{
    var defaultSampleCandidates = new[]
    {
        Path.Combine("samples", "sandbox", $"test{RavenFileExtensions.Raven}"),
        Path.Combine("..", "..", "..", "..", "..", "samples", "sandbox", $"test{RavenFileExtensions.Raven}")
    };

    sourceFiles.Add(defaultSampleCandidates.FirstOrDefault(File.Exists) ?? defaultSampleCandidates[0]);
}

if (emitDocs && documentationTool == DocumentationTool.RavenDoc && documentationFormatExplicitlySet &&
    documentationFormat == DocumentationFormat.Xml)
{
    AnsiConsole.MarkupLine("[yellow]RavenDoc only supports Markdown documentation. Ignoring --doc-format xml.[/]");
    documentationFormat = DocumentationFormat.Markdown;
}

var inputMayBeProjectFile = sourceFiles.Count == 1 && IsRavenProjectFile(sourceFiles[0]);

if (run && outputKind != OutputKind.ConsoleApplication && !inputMayBeProjectFile)
{
    AnsiConsole.MarkupLine("[red]--run is only supported for console applications. Class libraries cannot be executed.[/]");
    Environment.ExitCode = 1;
    return;
}

static bool IsValidAssemblyReference(string path)
{
    try
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length == 0)
            return false;

        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        return peReader.HasMetadata;
    }
    catch
    {
        return false;
    }
}

static bool IsRavenProjectFile(string path)
    => RavenFileExtensions.HasProjectExtension(path);

static bool IsAssemblyCompatibleWithTargetFramework(string path, TargetFrameworkMoniker targetFramework)
{
    try
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            return false;

        var metadataReader = peReader.GetMetadataReader();
        foreach (var attributeHandle in metadataReader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadataReader.GetCustomAttribute(attributeHandle);
            if (!IsTargetFrameworkAttribute(metadataReader, attribute.Constructor))
                continue;

            var valueReader = metadataReader.GetBlobReader(attribute.Value);
            if (valueReader.ReadUInt16() != 1)
                return false;

            var frameworkName = valueReader.ReadSerializedString();
            if (string.IsNullOrWhiteSpace(frameworkName))
                return false;

            var assemblyTargetFramework = TargetFrameworkMoniker.Parse(frameworkName);
            return assemblyTargetFramework.Framework == targetFramework.Framework &&
                assemblyTargetFramework.Version <= targetFramework.Version;
        }

        return true;
    }
    catch
    {
        return false;
    }
}

static bool IsTargetFrameworkAttribute(MetadataReader reader, EntityHandle constructorHandle)
{
    EntityHandle typeHandle = constructorHandle.Kind switch
    {
        HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructorHandle).Parent,
        HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructorHandle).GetDeclaringType(),
        _ => default
    };

    return typeHandle.Kind switch
    {
        HandleKind.TypeReference => IsTargetFrameworkTypeReference(reader, reader.GetTypeReference((TypeReferenceHandle)typeHandle)),
        HandleKind.TypeDefinition => IsTargetFrameworkTypeDefinition(reader, reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle)),
        _ => false
    };
}

static bool IsTargetFrameworkTypeReference(MetadataReader reader, TypeReference type)
    => reader.StringComparer.Equals(type.Namespace, "System.Runtime.Versioning") &&
        reader.StringComparer.Equals(type.Name, "TargetFrameworkAttribute");

static bool IsTargetFrameworkTypeDefinition(MetadataReader reader, TypeDefinition type)
    => reader.StringComparer.Equals(type.Namespace, "System.Runtime.Versioning") &&
        reader.StringComparer.Equals(type.Name, "TargetFrameworkAttribute");

static string? TryReadProjectTargetFramework(string projectFilePath)
{
    try
    {
        var xdoc = XDocument.Load(projectFilePath);
        return GetProjectPropertyValue(xdoc, "TargetFramework");
    }
    catch
    {
        return null;
    }
}

static string? TryReadProjectAssemblyName(string projectFilePath)
{
    try
    {
        var xdoc = XDocument.Load(projectFilePath);
        return GetProjectPropertyValue(xdoc, "AssemblyName");
    }
    catch
    {
        return null;
    }
}

static string TryReadProjectConfiguration(string projectFilePath)
{
    try
    {
        var xdoc = XDocument.Load(projectFilePath);
        var configuration = GetProjectPropertyValue(xdoc, "Configuration");
        return RavenProjectConventions.Default.NormalizeConfiguration(configuration);
    }
    catch
    {
        return RavenProjectConventions.Default.DefaultConfiguration;
    }
}

static string? GetProjectPropertyValue(XDocument document, string propertyName)
{
    return document.Root?
        .Descendants()
        .FirstOrDefault(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.OrdinalIgnoreCase))
        ?.Value;
}

for (int i = 0; i < sourceFiles.Count; i++)
{
    sourceFiles[i] = Path.GetFullPath(sourceFiles[i]);
    if (!File.Exists(sourceFiles[i]))
    {
        AnsiConsole.MarkupLine($"[red]Input file '{sourceFiles[i]}' doesn't exist.[/]");
        Environment.ExitCode = 1;
        return;
    }
}

var projectFileInput = sourceFiles.Count == 1 &&
                       IsRavenProjectFile(sourceFiles[0])
    ? sourceFiles[0]
    : null;

AssemblyName? targetCoreLibraryIdentity = null;
if (!string.IsNullOrWhiteSpace(targetCoreLibraryPath))
{
    targetCoreLibraryPath = Path.GetFullPath(targetCoreLibraryPath);
    if (!File.Exists(targetCoreLibraryPath) || !IsValidAssemblyReference(targetCoreLibraryPath))
    {
        AnsiConsole.MarkupLine(
            $"[red]Target core library '{Markup.Escape(targetCoreLibraryPath)}' is not a valid managed assembly.[/]");
        Environment.ExitCode = 1;
        return;
    }

    targetCoreLibraryIdentity = AssemblyName.GetAssemblyName(targetCoreLibraryPath);
}

var projectTargetFramework = projectFileInput is null ? null : TryReadProjectTargetFramework(projectFileInput);
var projectConfiguration = requestedConfiguration is null
    ? projectFileInput is null
        ? RavenProjectConventions.Default.DefaultConfiguration
        : TryReadProjectConfiguration(projectFileInput)
    : RavenProjectConventions.Default.NormalizeConfiguration(requestedConfiguration);
var hostDefaultFramework = TargetFrameworkUtil.Resolve(AppContext.TargetFrameworkName);
var targetFramework = targetFrameworkTfm
    ?? projectTargetFramework
    ?? hostDefaultFramework;
var targetFrameworkMoniker = TargetFrameworkMoniker.Parse(targetFramework);
if (projectFileInput is not null && targetFrameworkMoniker.Framework == FrameworkId.NetNanoFramework)
{
    includeFrameworkReferences = false;
    embedCoreTypes = true;
    skipDefaultRavenCoreLookup = true;
}
var frameworkResolutionTarget = includeFrameworkReferences
    ? targetFramework
    : hostDefaultFramework;
var version = TargetFrameworkResolver.ResolveVersion(frameworkResolutionTarget);
var preferredCoreTfm = version.Moniker.ToTfm();
if (runtimeAsyncOverride is true &&
    version.Moniker.Framework == FrameworkId.NetCoreApp &&
    version.Moniker.Version.Major < 11)
{
    AnsiConsole.MarkupLine(
        $"[red]--runtime-async is only supported for net11.0+ (current target: {Markup.Escape(version.Moniker.ToTfm())}).[/]");
    Environment.ExitCode = 1;
    return;
}
var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var fallbackLocalTfms = new[] { "net11.0", "net10.0" }
    .Where(tfm => !string.Equals(tfm, preferredCoreTfm, StringComparison.OrdinalIgnoreCase))
    .ToArray();

if (!ravenCoreExplicitlyProvided && !skipDefaultRavenCoreLookup)
{
    var specificCoreCandidates = new[]
    {
        Path.Combine(repositoryRoot, "src", "Raven.Core", "bin", "Debug", preferredCoreTfm, "Raven.Core.dll"),
        Path.Combine(repositoryRoot, "src", "Raven.Core", "bin", "Debug", preferredCoreTfm, preferredCoreTfm, "Raven.Core.dll"),
    };

    var baseCandidate = Path.Combine(AppContext.BaseDirectory, "Raven.Core.dll");
    var invalidSpecificCandidate = specificCoreCandidates.FirstOrDefault(File.Exists) is { } existingSpecific
        && !IsValidAssemblyReference(existingSpecific)
        ? existingSpecific
        : null;

    if (invalidSpecificCandidate is not null)
    {
        AnsiConsole.MarkupLine(
            $"[red]Raven core assembly '{Markup.Escape(invalidSpecificCandidate)}' for target framework '{Markup.Escape(preferredCoreTfm)}' is invalid (possibly empty or stale).[/]");
        AnsiConsole.MarkupLine("[yellow]Rebuild Raven.Core for that target before compiling, for example via scripts/codex-build.sh.[/]");
        Environment.ExitCode = 1;
        return;
    }

    foreach (var candidate in specificCoreCandidates)
    {
        if (File.Exists(candidate) && IsValidAssemblyReference(candidate))
        {
            ravenCorePath = candidate;
            break;
        }
    }

    if (string.IsNullOrWhiteSpace(ravenCorePath) && File.Exists(baseCandidate) && IsValidAssemblyReference(baseCandidate))
        ravenCorePath = baseCandidate;

    if (string.IsNullOrWhiteSpace(ravenCorePath))
    {
        if (File.Exists(baseCandidate) && !IsValidAssemblyReference(baseCandidate))
        {
            AnsiConsole.MarkupLine(
                $"[red]Found Raven core assembly at '{Markup.Escape(baseCandidate)}' but it is invalid (possibly empty or stale).[/]");
            AnsiConsole.MarkupLine("[yellow]Rebuild Raven.Core for the selected target framework before compiling.[/]");
            Environment.ExitCode = 1;
            return;
        }

        embedCoreTypes = true;
    }
}
else if (string.IsNullOrWhiteSpace(ravenCorePath))
{
    embedCoreTypes = true;
}

if (!string.IsNullOrWhiteSpace(ravenCorePath))
{
    ravenCorePath = Path.GetFullPath(ravenCorePath);
    if (!File.Exists(ravenCorePath))
    {
        if (ravenCoreExplicitlyProvided)
        {
            AnsiConsole.MarkupLine($"[red]Raven core assembly '{ravenCorePath}' doesn't exist.[/]");
            Environment.ExitCode = 1;
            return;
        }

        ravenCorePath = null;
    }
    else if (!IsValidAssemblyReference(ravenCorePath))
    {
        if (ravenCoreExplicitlyProvided)
        {
            AnsiConsole.MarkupLine($"[red]Raven core assembly '{ravenCorePath}' is not a valid managed assembly.[/]");
            Environment.ExitCode = 1;
            return;
        }

        ravenCorePath = null;
    }
}

var explicitOutputPath = !string.IsNullOrEmpty(outputPath);
var defaultAssemblyBaseName = projectFileInput is not null
    ? Path.GetFileNameWithoutExtension(projectFileInput)
    : Path.GetFileNameWithoutExtension(sourceFiles[0]);

string outputFilePath;
string assemblyName;
string outputDirectory;
if (projectFileInput is not null)
{
    if (explicitOutputPath)
    {
        var outputExtension = Path.GetExtension(outputPath!);
        if (string.Equals(outputExtension, ".dll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputExtension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]For project-file inputs, -o/--output must be a directory path, not a file path.[/]");
            Environment.ExitCode = 1;
            return;
        }
    }

    if (explicitOutputPath)
    {
        outputDirectory = Path.GetFullPath(outputPath!);
        assemblyName = TryReadProjectAssemblyName(projectFileInput) ?? defaultAssemblyBaseName;
        outputFilePath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
    }
    else
    {
        outputFilePath = MsBuildProjectOutputResolver.ResolveProjectOutputPath(
            projectFileInput,
            targetFramework,
            requestedConfiguration: projectConfiguration);
        outputDirectory = Path.GetDirectoryName(outputFilePath)!;
        assemblyName = Path.GetFileNameWithoutExtension(outputFilePath);

        if (publish)
        {
            var projectDirectory = Path.GetDirectoryName(projectFileInput)!;
            outputDirectory = Path.Combine(projectDirectory, "bin", projectConfiguration, "publish");
            outputFilePath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
        }
    }
}
else
{
    outputPath = !string.IsNullOrEmpty(outputPath) ? outputPath : defaultAssemblyBaseName;
    if (!Path.HasExtension(outputPath))
        outputPath = $"{outputPath}.dll";
    outputFilePath = Path.GetFullPath(outputPath);
    assemblyName = Path.GetFileNameWithoutExtension(outputFilePath);
    outputDirectory = Path.GetDirectoryName(outputFilePath)!;
    if (string.IsNullOrEmpty(outputDirectory))
        outputDirectory = Directory.GetCurrentDirectory();
}
Directory.CreateDirectory(outputDirectory);

if ((publish || run) && !string.IsNullOrEmpty(ravenCorePath))
{
    Directory.CreateDirectory(outputDirectory!);
    var ravenCoreDestination = Path.Combine(outputDirectory!, Path.GetFileName(ravenCorePath));

    // When targeting a project output directory, Raven.Core.dll may already be loaded by a running app.
    // In that case, overwriting can fail on some platforms. Prefer reusing the existing file if it looks identical.
    if (!string.Equals(ravenCoreDestination, ravenCorePath, StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            if (File.Exists(ravenCoreDestination))
            {
                var srcInfo = new FileInfo(ravenCorePath);
                var dstInfo = new FileInfo(ravenCoreDestination);

                // Fast equality check: same size and same last write time (UTC). If so, keep the existing copy.
                if (srcInfo.Exists && dstInfo.Exists &&
                    srcInfo.Length == dstInfo.Length &&
                    srcInfo.LastWriteTimeUtc == dstInfo.LastWriteTimeUtc)
                {
                    ravenCorePath = ravenCoreDestination;
                }
                else
                {
                    File.Copy(ravenCorePath, ravenCoreDestination, overwrite: true);
                    // Preserve timestamps so subsequent runs can use the fast equality check.
                    File.SetLastWriteTimeUtc(ravenCoreDestination, srcInfo.LastWriteTimeUtc);
                    ravenCorePath = ravenCoreDestination;
                }
            }
            else
            {
                File.Copy(ravenCorePath, ravenCoreDestination, overwrite: true);
                var srcInfo = new FileInfo(ravenCorePath);
                if (srcInfo.Exists)
                    File.SetLastWriteTimeUtc(ravenCoreDestination, srcInfo.LastWriteTimeUtc);
                ravenCorePath = ravenCoreDestination;
            }
        }
        catch (IOException)
        {
            // If the destination is locked (e.g. a previously run app is still using Raven.Core.dll),
            // keep using the existing file if present.
            if (File.Exists(ravenCoreDestination))
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Warning: Could not overwrite '{Markup.Escape(ravenCoreDestination)}' because it is in use. Reusing the existing file.[/]");
                ravenCorePath = ravenCoreDestination;
            }
            else
            {
                throw;
            }
        }
    }
}

string? ResolveAndCopyLocalDependency(
    string fileName,
    Func<string, bool>? candidateFilter,
    bool copyLocal,
    params string[] candidates)
{
    foreach (var candidate in candidates)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            continue;

        var full = Path.GetFullPath(candidate);
        if (!File.Exists(full) || !IsValidAssemblyReference(full))
            continue;
        if (candidateFilter is not null && !candidateFilter(full))
            continue;

        if (!copyLocal)
            return full;

        Directory.CreateDirectory(outputDirectory!);
        var destination = Path.Combine(outputDirectory!, fileName);

        if (!string.Equals(full, destination, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Same "in use" issue can happen for local dependencies when the previous output is still running.
                if (File.Exists(destination))
                {
                    var srcInfo = new FileInfo(full);
                    var dstInfo = new FileInfo(destination);
                    if (srcInfo.Exists && dstInfo.Exists &&
                        srcInfo.Length == dstInfo.Length &&
                        srcInfo.LastWriteTimeUtc == dstInfo.LastWriteTimeUtc)
                    {
                        return destination;
                    }
                }

                File.Copy(full, destination, overwrite: true);
                var src = new FileInfo(full);
                if (src.Exists)
                    File.SetLastWriteTimeUtc(destination, src.LastWriteTimeUtc);
            }
            catch (IOException)
            {
                if (File.Exists(destination))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Warning: Could not overwrite '{Markup.Escape(destination)}' because it is in use. Reusing the existing file.[/]");
                    return destination;
                }

                throw;
            }
        }

        return destination;
    }

    return null;
}

var ravenCodeAnalysisCandidates = RepositoryDependencyCandidates.Create(
    AppContext.BaseDirectory,
    repositoryRoot,
    "Raven.CodeAnalysis",
    projectConfiguration,
    preferredCoreTfm,
    fallbackLocalTfms,
    "Raven.CodeAnalysis.dll",
    "Raven.CodeAnalysis.dll");

var ravenCodeAnalysisPath = ResolveAndCopyLocalDependency(
    "Raven.CodeAnalysis.dll",
    path => IsAssemblyCompatibleWithTargetFramework(path, version.Moniker),
    copyLocal: false,
    ravenCodeAnalysisCandidates.ToArray());
var ravenMacrosCandidates = RepositoryDependencyCandidates.Create(
    AppContext.BaseDirectory,
    repositoryRoot,
    "Raven.Macros",
    projectConfiguration,
    preferredCoreTfm,
    fallbackLocalTfms,
    "Raven.Macros.dll",
    "Raven.Macros.dll",
    "../../sdk/Raven.Macros.dll");

var ravenMacrosPath = ResolveAndCopyLocalDependency(
    "Raven.Macros.dll",
    path => IsAssemblyCompatibleWithTargetFramework(path, version.Moniker),
    copyLocal: false,
    ravenMacrosCandidates.ToArray());

var useRuntimeAsync = runtimeAsyncOverride
    ?? (includeFrameworkReferences &&
        version.Moniker.Framework == FrameworkId.NetCoreApp &&
        version.Moniker.Version.Major >= 11);
#if RAVEN_INSTRUMENTATION
var performanceInstrumentation = FindDebugDirectory() is not null
    ? new PerformanceInstrumentation()
    : PerformanceInstrumentation.Disabled;
#else
var performanceInstrumentation = PerformanceInstrumentation.Disabled;
#endif

// Overrides are intentionally applied after every ordinary --constant value,
// independent of their position on the command line. MSBuild project items
// provide defaults; an explicit override must always win over those defaults.
foreach (var pair in externalConstantOverrides)
    externalConstantValues[pair.Key] = pair.Value;

var executionOptions = new CompilerExecutionOptions(
    outputKind,
    string.Equals(projectConfiguration, "Release", StringComparison.OrdinalIgnoreCase)
        ? OptimizationLevel.Release
        : OptimizationLevel.Debug,
    allowUnsafe,
    allowGlobalStatements,
    allowNamespaceMembers,
    allowNamespaceMembersSpecified,
    allowNamespaceMemberImports,
    allowNamespaceMemberImportsSpecified,
    useRuntimeAsync,
    showSuggestions,
    enableAsyncInvestigation,
    asyncInvestigationLabel,
    asyncInvestigationScope,
    embedCoreTypes,
    enableOverloadLog,
    overloadLogPath,
    performanceInstrumentation,
    externalConstantValues);
var optionsResult = CreateCompilationOptions(executionOptions);
var options = optionsResult.Options;
overloadResolutionLog = optionsResult.OverloadResolutionLog;
if (skipDefaultRavenCoreLookup)
    options = options.WithFrameworkProjectionMode(FrameworkProjectionMode.None);
if (targetFrameworkTfm?.StartsWith("netnano", StringComparison.OrdinalIgnoreCase) == true)
    options = options.WithSynthesizeStructuralToString(false);
var workspace = RavenWorkspace.Create(
    targetFramework: targetFramework,
    projectSystemService: new MsBuildProjectSystemService(
        RavenProjectConventions.Default,
        true,
        projectConfiguration,
        targetFrameworkTfm,
        includeFrameworkReferences,
        new[] { ravenMacrosPath, ravenCodeAnalysisPath }.OfType<string>(),
        allowPackageRestore: restoreProjectReferences,
        projectReferenceLoadMode: ProjectReferenceLoadMode.Metadata));
workspace.Services.SyntaxTreeProvider.ParseOptions = new ParseOptions
{
    DocumentationMode = true,
    DocumentationFormat = documentationFormat,
    PreprocessorSymbolNames = conditionalSymbols
};
ProjectId projectId;
Project project;

if (projectFileInput is not null)
{
    projectId = workspace.OpenProject(projectFileInput);
    project = workspace.CurrentSolution.GetProject(projectId)!;
    project = project.WithParseOptions(
        (project.ParseOptions ?? workspace.Services.SyntaxTreeProvider.ParseOptions)
        .WithPreprocessorSymbols(
            (project.ParseOptions?.PreprocessorSymbolNames ?? [])
            .Concat(conditionalSymbols)));

    if (targetFrameworkTfm is not null && includeFrameworkReferences)
    {
        var frameworkReferences = TargetFrameworkResolver.GetReferenceAssemblies(version)
            .Select(MetadataReference.CreateFromFile)
            .ToArray();
        foreach (var reference in frameworkReferences)
            project = AddMetadataReferenceIfMissing(project, reference.FilePath);
    }
}
else
{
    projectId = workspace.AddProject(
        assemblyName,
        compilationOptions: options,
        parseOptions: workspace.Services.SyntaxTreeProvider.ParseOptions);
    project = workspace.CurrentSolution.GetProject(projectId)!;

    foreach (var filePath in sourceFiles)
    {
        using var file = File.OpenRead(filePath);
        var sourceText = SourceText.From(file);
        var parsedTree = SyntaxTree.ParseText(sourceText, path: filePath);
        var document = project.AddDocument(Path.GetFileName(filePath), sourceText, filePath);
        project = document.Project;
    }

    if (!embedCoreTypes &&
        !sourceFiles.Any(static filePath => Path.GetFileName(filePath).EndsWith(".Prelude.g.rvn", StringComparison.OrdinalIgnoreCase)))
    {
        var preludeSource = RavenPrelude.CreateDefaultSourceText();
        var preludeName = $"{assemblyName}.Prelude.g.rvn";
        var preludeDirectory = sourceFiles.Count > 0
            ? Path.GetDirectoryName(sourceFiles[0]) ?? Environment.CurrentDirectory
            : Environment.CurrentDirectory;
        var preludePath = Path.Combine(preludeDirectory, preludeName);
        var preludeDocument = project.AddDocument(preludeName, preludeSource, preludePath);
        project = preludeDocument.Project;
    }

    if (includeFrameworkReferences)
    {
        var frameworkReferences = TargetFrameworkResolver.GetReferenceAssemblies(version)
            .Select(MetadataReference.CreateFromFile)
            .ToArray();

        foreach (var reference in frameworkReferences)
        {
            project = AddMetadataReferenceIfMissing(project, reference.FilePath);
        }
    }
}

if (!string.IsNullOrWhiteSpace(ravenCorePath))
{
    project = ReplaceMetadataReferenceByAssemblyIdentity(project, ravenCorePath);
}

if (string.IsNullOrWhiteSpace(targetCoreLibraryPath) &&
    targetFrameworkMoniker.Framework == FrameworkId.NetNanoFramework)
{
    targetCoreLibraryPath = project.MetadataReferences
        .OfType<PortableExecutableReference>()
        .Select(static reference => reference.FilePath)
        .FirstOrDefault(static path =>
            string.Equals(Path.GetFileName(path), "mscorlib.dll", StringComparison.OrdinalIgnoreCase));

    if (!string.IsNullOrWhiteSpace(targetCoreLibraryPath))
        targetCoreLibraryIdentity = AssemblyName.GetAssemblyName(targetCoreLibraryPath);
}

if (!string.IsNullOrWhiteSpace(targetCoreLibraryPath))
{
    project = ReplaceMetadataReferenceByAssemblyIdentity(project, targetCoreLibraryPath);
}

if (!string.IsNullOrWhiteSpace(ravenCodeAnalysisPath))
    project = ReplaceMetadataReferenceByAssemblyIdentity(project, ravenCodeAnalysisPath);

if (!string.Equals(assemblyName, "Raven.Macros", StringComparison.OrdinalIgnoreCase) &&
    !string.IsNullOrWhiteSpace(ravenMacrosPath))
{
    project = ReplaceMetadataReferenceByAssemblyIdentity(project, ravenMacrosPath);
    project = ReplaceMacroReferenceByAssemblyIdentity(project, ravenMacrosPath);
}

foreach (var r in additionalRefs)
{
    var full = Path.GetFullPath(r);
    project = AddMetadataReferenceIfMissing(project, full);
}

if (projectFileInput is not null)
{
    if (project.CompilationOptions is { } projectOptions)
    {
        options = projectOptions
            .WithSpecificDiagnosticOptions(options.SpecificDiagnosticOptions)
            .WithOptimizationLevel(projectOptions.OptimizationLevel)
            .WithPerformanceInstrumentation(options.PerformanceInstrumentation)
            .WithLoweringTrace(options.LoweringTrace)
            .WithAsyncInvestigation(options.AsyncInvestigation)
            .WithOverloadResolutionLogger(options.OverloadResolutionLogger)
            .WithEmbedCoreTypes(options.EmbedCoreTypes)
            .WithAllowUnsafe(options.AllowUnsafe)
            .WithAllowGlobalStatements(options.AllowGlobalStatements)
            .WithEnableSuggestions(options.EnableSuggestions)
            .WithRuntimeAsync(options.UseRuntimeAsync)
            .WithEnableIsNotNullNarrowing(projectOptions.EnableIsNotNullNarrowing)
            .WithExternalConstantValues(
                projectOptions.ExternalConstantValues.SetItems(options.ExternalConstantValues));

        if (executionOptions.AllowNamespaceMembersSpecified)
            options = options.WithAllowNamespaceMembers(executionOptions.AllowNamespaceMembers);

        if (executionOptions.AllowNamespaceMemberImportsSpecified)
            options = options.WithAllowNamespaceMemberImports(executionOptions.AllowNamespaceMemberImports);
    }

    assemblyName = !string.IsNullOrWhiteSpace(project.AssemblyName)
        ? project.AssemblyName
        : !string.IsNullOrWhiteSpace(project.Name)
            ? project.Name
            : assemblyName;
    outputFilePath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
}

var sourceDocumentPaths = project.Documents
    .Select(static d => d.FilePath)
    .Where(static path => !string.IsNullOrWhiteSpace(path))
    .Select(static path => path!)
    .ToArray();
var editorConfigAnchorPath = projectFileInput ?? sourceDocumentPaths.FirstOrDefault();
if (!string.IsNullOrWhiteSpace(editorConfigAnchorPath))
{
    options = EditorConfigDiagnosticOptions.ApplyOptions(
        options,
        editorConfigAnchorPath,
        sourceDocumentPaths);
}

if (returnedValueHandlingDiagnostic is { } returnedValueHandlingOption)
{
    options = options.WithSpecificDiagnosticOption(
        UnusedExpressionResultAnalyzer.DiagnosticId,
        returnedValueHandlingOption);
}

if (returnedValueHandlingModeOverride is { } returnedValueHandlingMode)
    options = options.WithReturnedValueHandlingMode(returnedValueHandlingMode);

if (skipDefaultRavenCoreLookup)
    options = options.WithFrameworkProjectionMode(FrameworkProjectionMode.None);
if (targetFrameworkTfm?.StartsWith("netnano", StringComparison.OrdinalIgnoreCase) == true)
    options = options.WithSynthesizeStructuralToString(false);

project = project.WithCompilationOptions(options);
project = AddDefaultAnalyzers(project, options.EnableSuggestions);

if (generatedFilesOutputPathSpecified)
    project = project.WithCompilerGeneratedFilesOutputPath(string.IsNullOrWhiteSpace(generatedFilesOutputPath)
        ? null
        : Path.GetFullPath(generatedFilesOutputPath));
workspace.TryApplyChanges(project.Solution);
project = workspace.CurrentSolution.GetProject(projectId)!;

if (!noEmit && (publish || run))
{
    CopyNuGetReferencesToOutput(project, targetFramework, outputDirectory!);
}

if (run && options.OutputKind != OutputKind.ConsoleApplication)
{
    AnsiConsole.MarkupLine("[red]--run is only supported for console applications. Class libraries cannot be executed.[/]");
    Environment.ExitCode = 1;
    return;
}

project = ApplyCodeFixesIfRequested(workspace, projectId, project, fix);
project = ApplyFormattingIfRequested(workspace, projectId, project, format);

// Print syntax tree BEFORE binding/diagnostics retrieval.
// Binding may be triggered by workspace diagnostics/semantic model creation, so keep syntax-only printing above that.
var allowConsoleOutputPreBinding = project.Documents.Count() == 1;
if (allowConsoleOutputPreBinding && (printSyntaxTree || printSyntaxTreeInternal))
{
    var document = project.Documents.Single();
    var syntaxTree = document.GetSyntaxTreeAsync().Result!;
    var root = syntaxTree.GetRoot();

    if (printSyntaxTree)
    {
        root.PrintSyntaxTree(new PrinterOptions
        {
            IncludeNames = true,
            IncludeTokens = true,
            IncludeTrivia = true,
            IncludeSpans = true,
            IncludeLocations = true,
            Colorize = true,
            ExpandListsAsProperties = syntaxTreeFormat == SyntaxTreeFormat.Flat,
            // IMPORTANT: keep this syntax-only. Do not include diagnostics here, since fetching diagnostics can bind.
            IncludeDiagnostics = false,
            IncludeAnnotations = true,
            DiagnosticsAsChildren = false,
            AnnotationsAsChildren = true
        });
    }
    else
    {
        // Internal (green) syntax tree printer.
        var green = (GreenNode?)typeof(SyntaxNode)
            .GetField("Green", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(root);

        var greenPrinterOptions = new Raven.CodeAnalysis.Syntax.InternalSyntax.PrettyGreenTreePrinterOptions()
        {
            IncludeTrivia = true,
            IncludeTriviaText = true,
            FlattenSyntaxLists = false,
            IncludeSlotIndices = true,
            IncludeWidths = true,
            HideZeroWidthSlots = false,
            IncludeDiagnostics = false,
            IncludeAnnotations = true,
            DiagnosticsAsChildren = false,
            AnnotationsAsChildren = true
        };

        Console.WriteLine(Raven.CodeAnalysis.Syntax.InternalSyntax.PrettyGreenTreePrinter.PrintToString(green!, greenPrinterOptions));
    }

    // Avoid printing the same tree again later.
    printSyntaxTree = false;
    printSyntaxTreeInternal = false;
}

var compilation = workspace.GetCompilation(projectId);

if (!noEmit && project.CompilerGeneratedFilesOutputPath is { } generatedOutputDirectory)
{
    try
    {
        var documentPaths = project.Documents.Select(document => document.FilePath).ToHashSet(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (documentPaths.Contains(tree.FilePath))
                continue;
            var relativePath = Path.GetRelativePath(generatedOutputDirectory, tree.FilePath);
            if (Path.IsPathRooted(relativePath) || relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(tree.FilePath)!);
            File.WriteAllText(tree.FilePath, tree.GetText().ToString(), tree.GetText().Encoding ?? System.Text.Encoding.UTF8);
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Failed to write compiler-generated files: {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }
}

var projectDocumentationOptions = project.DocumentationOptions;
var automaticDocumentationOutputs = new List<(DocumentationFormat Format, string OutputPath)>();
var requiresWorkspaceDiagnostics = noEmit || project.AnalyzerReferences.Any() || project.GeneratorReferences.Any();
ImmutableArray<Diagnostic> diagnostics = requiresWorkspaceDiagnostics
    ? workspace.GetDiagnostics(projectId)
    : ImmutableArray<Diagnostic>.Empty;

EmitResult? result = null;
if (!noEmit)
{
    if (requiresWorkspaceDiagnostics && diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
    {
        result = new EmitResult(false, diagnostics);
    }
    else
    {
        var pdbFilePath = Path.ChangeExtension(outputFilePath, ".pdb");
        var tempOutputFilePath = CreateTemporaryOutputPath(outputFilePath);
        var tempPdbFilePath = CreateTemporaryOutputPath(pdbFilePath);

        TryDeleteFile(tempOutputFilePath);
        TryDeleteFile(tempPdbFilePath);

        using (var stream = File.Open(tempOutputFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var pdbStream = File.Open(tempPdbFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            result = compilation.Emit(
                stream,
                pdbStream,
                requiresWorkspaceDiagnostics ? diagnostics : null,
                targetCoreLibraryIdentity is null ? null : new EmitOptions(targetCoreLibraryIdentity));
        }

        if (result.Success)
        {
            ReplaceFile(tempOutputFilePath, outputFilePath);
            ReplaceFile(tempPdbFilePath, pdbFilePath);
            CopyEmittedLocalDependencies(project, outputFilePath, outputDirectory, targetFramework);
            if (!string.IsNullOrWhiteSpace(dependencyFilePath))
                WriteDependencyFile(dependencyFilePath, compilation.GetObservedMacroFilePaths());
        }
        else
        {
            TryDeleteFile(tempOutputFilePath);
            TryDeleteFile(tempPdbFilePath);
        }
    }

    diagnostics = requiresWorkspaceDiagnostics
        ? result!.Diagnostics
        : result!.Diagnostics;
}

static void ReplaceFile(string sourcePath, string destinationPath)
{
    try
    {
        File.Move(sourcePath, destinationPath, overwrite: true);
    }
    catch (IOException) when (File.Exists(destinationPath))
    {
        File.Delete(destinationPath);
        File.Move(sourcePath, destinationPath);
    }
}

static string CreateTemporaryOutputPath(string destinationPath)
{
    var directory = Path.GetDirectoryName(destinationPath);
    var fileName = Path.GetFileName(destinationPath);
    var uniqueSuffix = $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
    return directory is null
        ? fileName + uniqueSuffix
        : Path.Combine(directory, fileName + uniqueSuffix);
}

static void TryDeleteFile(string path)
{
    try
    {
        if (File.Exists(path))
            File.Delete(path);
    }
    catch
    {
    }
}

static void WriteDependencyFile(string path, IEnumerable<string> dependencies)
{
    var fullPath = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);

    var temporaryPath = CreateTemporaryOutputPath(fullPath);
    File.WriteAllLines(temporaryPath, dependencies);
    ReplaceFile(temporaryPath, fullPath);
}

if (!emitDocs &&
    projectFileInput is not null &&
    projectDocumentationOptions is not null &&
    (projectDocumentationOptions.GenerateMarkdownDocumentation || projectDocumentationOptions.GenerateXmlDocumentation))
{
    if (projectDocumentationOptions.GenerateMarkdownDocumentation)
    {
        automaticDocumentationOutputs.Add((DocumentationFormat.Markdown, ResolveDocumentationOutputPath(
            outputDirectory!,
            outputFilePath,
            projectFileInput,
            projectDocumentationOptions.MarkdownDocumentationOutputPath,
            DocumentationFormat.Markdown)));
    }

    if (projectDocumentationOptions.GenerateXmlDocumentation)
    {
        automaticDocumentationOutputs.Add((DocumentationFormat.Xml, ResolveDocumentationOutputPath(
            outputDirectory!,
            outputFilePath,
            projectFileInput,
            projectDocumentationOptions.XmlDocumentationFile,
            DocumentationFormat.Xml)));
    }
}

if (emitDocs)
{
    if (documentationTool == DocumentationTool.RavenDoc)
    {
        documentationOutputPath ??= Path.Combine(outputDirectory!, $"{assemblyName}.docs");
        DocumentationGenerator.ProcessCompilation(compilation, documentationOutputPath);
        AnsiConsole.MarkupLine($"[green]Documentation written to '{documentationOutputPath}'.[/]");
    }
    else
    {
        documentationOutputPath ??= documentationFormat == DocumentationFormat.Markdown
            ? Path.Combine(outputDirectory!, $"{assemblyName}.docs")
            : Path.ChangeExtension(outputFilePath, ".xml");

        var actualDocumentationPath = DocumentationEmitter.WriteDocumentation(compilation, documentationFormat, documentationOutputPath);
        AnsiConsole.MarkupLine($"[green]Documentation written to '{actualDocumentationPath}'.[/]");
    }
}

if (!emitDocs && automaticDocumentationOutputs.Count > 0)
{
    foreach (var (docFormat, docOutputPath) in automaticDocumentationOutputs)
    {
        var includeMarkdownWhenEmittingXml =
            docFormat != DocumentationFormat.Xml ||
            projectDocumentationOptions?.GenerateXmlDocumentationFromMarkdownComments == true;
        var actualDocumentationPath = DocumentationEmitter.WriteDocumentation(
            compilation,
            docFormat,
            docOutputPath,
            includeMarkdownWhenEmittingXml);
        AnsiConsole.MarkupLine($"[green]Documentation written to '{actualDocumentationPath}'.[/]");
    }
}

stopwatch.Stop();

var allowConsoleOutput = project.Documents.Count() == 1;
var debugDir = FindDebugDirectory();
var ilVerifyFailed = false;
var writeDebugSourceArtifacts = printRawSyntax ||
                                printSyntaxTree ||
                                printSyntax ||
                                macroSourceDumpTarget is not null ||
                                printBinders ||
                                printBoundTree;

if (debugDir is not null)
{
    Directory.CreateDirectory(debugDir);

#if RAVEN_INSTRUMENTATION
    File.WriteAllText(
        Path.Combine(debugDir, "binder-reentry.compile.txt"),
        compilation.PerformanceInstrumentation.BinderReentry.GetSummary());
    compilation.PerformanceInstrumentation.BinderReentry.Reset();
    File.WriteAllText(
        Path.Combine(debugDir, "macro-performance.compile.txt"),
        compilation.PerformanceInstrumentation.Macros.GetSummary());
    compilation.PerformanceInstrumentation.Macros.Reset();
    File.WriteAllText(
        Path.Combine(debugDir, "compiler-setup.compile.txt"),
        compilation.PerformanceInstrumentation.Setup.GetSummary());
    compilation.PerformanceInstrumentation.Setup.Reset();

    if (writeDebugSourceArtifacts)
    {
        foreach (var document in project.Documents)
        {
            var syntaxTree = document.GetSyntaxTreeAsync().Result!;
            var root = syntaxTree.GetRoot();
            var name = Path.GetFileNameWithoutExtension(document.FilePath) ?? document.Name;
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var expandedRoot = semanticModel.GetExpandedRoot();
            var expandedSyntaxTree = SyntaxTree.Create(
                expandedRoot,
                syntaxTree.Options,
                syntaxTree.Encoding,
                syntaxTree.FilePath);
            expandedRoot = expandedSyntaxTree.GetRoot();
            var expandedCompilation = Compilation.Create(
                compilation.AssemblyName,
                [expandedSyntaxTree],
                compilation.References.ToArray(),
                compilation.MacroReferences.ToArray(),
                compilation.Options);

            DeleteLegacyDebugSourceArtifact(debugDir, $"{name}.raw{RavenFileExtensions.Raven}");
            DeleteLegacyDebugSourceArtifact(debugDir, $"{name}.macro-original{RavenFileExtensions.Raven}");
            DeleteLegacyDebugSourceArtifact(debugDir, $"{name}.macro-expanded{RavenFileExtensions.Raven}");

            File.WriteAllText(Path.Combine(debugDir, $"{name}.raw.txt"), root.ToFullString());
            File.WriteAllText(Path.Combine(debugDir, $"{name}.macro-original.txt"), root.ToFullString());
            File.WriteAllText(Path.Combine(debugDir, $"{name}.macro-expanded.txt"), expandedRoot.ToFullString());

            var treeText = root.GetSyntaxTreeRepresentation(new PrinterOptions
            {
                IncludeNames = true,
                IncludeTokens = true,
                IncludeTrivia = true,
                IncludeSpans = true,
                IncludeLocations = true,
                Colorize = false,
                ExpandListsAsProperties = true,
                IncludeDiagnostics = true,
                IncludeAnnotations = true,
                DiagnosticsAsChildren = true,
                AnnotationsAsChildren = true,
            }).StripAnsiCodes();

            File.WriteAllText(Path.Combine(debugDir, $"{name}.syntax-tree.txt"), treeText);

            ConsoleSyntaxHighlighter.ColorScheme = ColorScheme.Light;
            var syntaxDiagnostics = diagnostics.Where(d => d.Location.SourceTree == syntaxTree);
            var syntax = root.WriteNodeToText(compilation, includeDiagnostics: true, diagnostics: syntaxDiagnostics)
                .StripAnsiCodes();
            File.WriteAllText(Path.Combine(debugDir, $"{name}.syntax.txt"), syntax);

            var expandedSyntax = expandedRoot.WriteNodeToText(expandedCompilation, includeDiagnostics: false)
                .StripAnsiCodes();
            File.WriteAllText(Path.Combine(debugDir, $"{name}.macro-expanded.syntax.txt"), expandedSyntax);

            using (var sw = new StringWriter())
            {
                var original = Console.Out;
                Console.SetOut(sw);
                semanticModel.PrintBinderTree(colorize: false);
                Console.SetOut(original);
                File.WriteAllText(Path.Combine(debugDir, $"{name}.binders.txt"), sw.ToString());
            }

            using (var sw = new StringWriter())
            {
                var original = Console.Out;
                Console.SetOut(sw);
                semanticModel.PrintBoundTree(includeChildPropertyNames: true, groupChildCollections: true, colorize: false);
                Console.SetOut(original);
                File.WriteAllText(Path.Combine(debugDir, $"{name}.bound-tree.txt"), sw.ToString());
            }
        }
    }

    File.WriteAllText(
        Path.Combine(debugDir, "binder-reentry.debug-artifacts.txt"),
        compilation.PerformanceInstrumentation.BinderReentry.GetSummary());
    File.WriteAllText(
        Path.Combine(debugDir, "macro-performance.debug-artifacts.txt"),
        compilation.PerformanceInstrumentation.Macros.GetSummary());
    File.WriteAllText(
        Path.Combine(debugDir, "compiler-setup.debug-artifacts.txt"),
        compilation.PerformanceInstrumentation.Setup.GetSummary());
#endif

    AnsiConsole.MarkupLine($"[yellow]Debug output written to '{debugDir}'.[/]");
}

if (allowConsoleOutput && writeDebugSourceArtifacts)
{
    var document = project.Documents.Single();
    var syntaxTree = document.GetSyntaxTreeAsync().Result!;
    var root = syntaxTree.GetRoot();

    if (printRawSyntax)
    {
        Console.WriteLine(root.ToFullString());
        Console.WriteLine();
    }

    if (printSyntaxTree)
    {
        var includeLocations = true;
        root.PrintSyntaxTree(new PrinterOptions
        {
            IncludeNames = true,
            IncludeTokens = true,
            IncludeTrivia = true,
            IncludeSpans = true,
            IncludeLocations = includeLocations,
            Colorize = true,
            ExpandListsAsProperties = syntaxTreeFormat == SyntaxTreeFormat.Flat,
            IncludeDiagnostics = true,
            IncludeAnnotations = true,
            DiagnosticsAsChildren = true,
            AnnotationsAsChildren = true
        });
    }
    else if (printSyntaxTreeInternal)
    {
        var green = (GreenNode?)typeof(SyntaxNode)
            .GetField("Green", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(root);

        var greenPrinterOptions = new Raven.CodeAnalysis.Syntax.InternalSyntax.PrettyGreenTreePrinterOptions()
        {
            IncludeTrivia = true,
            IncludeTriviaText = true,
            FlattenSyntaxLists = false,
            IncludeSlotIndices = true,
            IncludeWidths = true,
            HideZeroWidthSlots = false,
            IncludeDiagnostics = true,
            IncludeAnnotations = true,
            DiagnosticsAsChildren = true,
            AnnotationsAsChildren = true
        };

        Console.WriteLine(Raven.CodeAnalysis.Syntax.InternalSyntax.PrettyGreenTreePrinter.PrintToString(green!, greenPrinterOptions));
    }

    if (printSyntax)
    {
        ConsoleSyntaxHighlighter.ColorScheme = ColorScheme.Light;
        var prettyDiagnostics = prettyIncludeDiagnostics
            ? diagnostics.Where(d => d.Location.SourceTree == syntaxTree)
            : null;
        var highlighted = root.WriteNodeToText(compilation, includeDiagnostics: prettyIncludeDiagnostics,
            diagnostics: prettyDiagnostics);
        if (highlighted.Length > 0)
        {
            Console.WriteLine(highlighted);
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine();
        }
    }

    if (macroSourceDumpTarget is not null)
    {
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var expandedRoot = semanticModel.GetExpandedRoot();

        if (macroSourceDumpTarget is "original" or "both")
        {
            PrintMacroSourceDump(
                "Original",
                root,
                compilation,
                syntaxTree,
                diagnostics,
                printMacroRawSyntax,
                printMacroSyntax,
                macroPrettyIncludeDiagnostics);
        }

        if (macroSourceDumpTarget is "expanded" or "both")
        {
            PrintMacroSourceDump(
                "Expanded",
                expandedRoot,
                compilation,
                syntaxTree: null,
                diagnostics,
                printMacroRawSyntax,
                printMacroSyntax,
                includeDiagnostics: false);
        }
    }

    if (printBinders || printBoundTree)
    {
        var semanticModel = compilation.GetSemanticModel(syntaxTree);

        if (printBinders)
        {
            semanticModel.PrintBinderTree();
            Console.WriteLine();
        }

        if (printBoundTree)
        {
            if (!printBinders)
            {
                semanticModel.PrintBinderTree();
                Console.WriteLine();
            }

            semanticModel.PrintBoundTree(includeChildPropertyNames: true, groupChildCollections: true, displayCollectionIndices: false, onlyBlockRoots: !printBoundTreeErrors, includeErrorNodes: printBoundTreeErrors, view: boundTreeView);
            Console.WriteLine();
        }
    }
}
else if (writeDebugSourceArtifacts)
{
    if (debugDir is null)
        AnsiConsole.MarkupLine("[yellow]Create a '.debug' directory to capture debug output.[/]");
}

if (symbolDumpMode != SymbolDumpMode.None)
{
    var filter = static (ISymbol symbol) =>
        symbol.Kind is SymbolKind.Assembly or SymbolKind.Module or SymbolKind.Namespace ||
        !symbol.DeclaringSyntaxReferences.IsDefaultOrEmpty;

    switch (symbolDumpMode)
    {
        case SymbolDumpMode.Hierarchy:
            {
                var symbolDump = compilation.Assembly.ToSymbolHierarchyString(filter);
                Console.WriteLine(symbolDump);
                Console.WriteLine();
                break;
            }

        case SymbolDumpMode.List:
            {
                var symbolDump = compilation.Assembly.ToSymbolListString(filter);
                Console.WriteLine(symbolDump);
                Console.WriteLine();
                break;
            }
    }
}

if (quote)
{
    if (compilation.SyntaxTrees.FirstOrDefault() is not { } firstSyntaxTree)
    {
        AnsiConsole.MarkupLine("[yellow]No syntax tree available to quote.[/]");
    }
    else
    {
        var code = firstSyntaxTree.GetText()!.ToString();
        var quoted = RavenQuoter.QuoteText(code, new RavenQuoterOptions
        {
            IncludeTrivia = true,
            GenerateUsingDirectives = true,
            UseStaticSyntaxFactoryImport = true,
            UseNamedArguments = true,
            IgnoreNullValue = true,
            UseFactoryPropsForSimpleTokens = true
        });

        Console.WriteLine(quoted);
        Console.WriteLine();
    }
}

if (runIlVerify)
{
    if (noEmit)
    {
        AnsiConsole.MarkupLine("[red]--ilverify requires the assembly to be emitted. Remove --no-emit to run the verifier.[/]");
        ilVerifyFailed = true;
    }
    else if (result is null)
    {
        ilVerifyFailed = true;
    }
    else if (result.Success)
    {
        ilVerifyFailed = !IlVerifyRunner.Verify(ilVerifyPath, outputFilePath, compilation);
    }
}

if (diagnostics.Length > 0)
{
    PrintDiagnostics(diagnostics, compilation, highlightDiagnostics, showSuggestions);
    Console.WriteLine();
}

var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
var emitFailed = result is { Success: false };

if (errors.Any() || emitFailed || ilVerifyFailed)
{
    Environment.ExitCode = 1;
    if (emitFailed && result is not null)
    {
        Failed(result);
    }
    else if (errors.Any() && result is not null)
        Failed(result);
    else if (errors.Any())
        Failed(errors.Count());
    else
        Failed(1);
}
else
{
    var warningsCount = diagnostics.Count(x => x.Severity == DiagnosticSeverity.Warning);
    if (warningsCount > 0)
    {
        Environment.ExitCode = 0;
        SucceededWithWarnings(warningsCount, stopwatch.Elapsed);
    }
    else
    {
        Environment.ExitCode = 0;
        Succeeded(stopwatch.Elapsed);
    }

    if (result is not null)
        CreateAppHost(compilation, outputFilePath, targetFramework);

    if (run)
    {
        if (noEmit || result is null)
        {
            AnsiConsole.MarkupLine("[yellow]--run was specified but no assembly was emitted to execute.[/]");
        }
        else if (result.Success)
        {
            RunAssembly(outputFilePath, applicationArguments);
        }
    }
}

overloadResolutionLog?.Dispose();

static Project AddDefaultAnalyzers(Project project, bool enableSuggestions)
{
    return project.AddBuiltInAnalyzers(enableSuggestions);
}

static (CompilationOptions Options, OverloadResolutionLog? OverloadResolutionLog) CreateCompilationOptions(
    CompilerExecutionOptions executionOptions)
{
    var options = new CompilationOptions(executionOptions.OutputKind)
        .WithOptimizationLevel(executionOptions.OptimizationLevel)
        .WithAllowUnsafe(executionOptions.AllowUnsafe)
        .WithAllowGlobalStatements(executionOptions.AllowGlobalStatements)
        .WithAllowNamespaceMembers(executionOptions.AllowNamespaceMembers)
        .WithAllowNamespaceMemberImports(executionOptions.AllowNamespaceMemberImports)
        .WithRuntimeAsync(executionOptions.UseRuntimeAsync)
        .WithEmbedCoreTypes(executionOptions.EmbedCoreTypes)
        .WithEnableSuggestions(executionOptions.EnableSuggestions)
        .WithPerformanceInstrumentation(executionOptions.PerformanceInstrumentation)
        .WithExternalConstantValues(executionOptions.ExternalConstantValues);

    if (executionOptions.EnableAsyncInvestigation)
    {
        options = options.WithAsyncInvestigation(
            AsyncInvestigationOptions.Enable(
                executionOptions.AsyncInvestigationLabel,
                executionOptions.AsyncInvestigationScope));
    }

    OverloadResolutionLog? overloadResolutionLog = null;
    if (executionOptions.EnableOverloadLog)
    {
        var ownsWriter = !string.IsNullOrWhiteSpace(executionOptions.OverloadLogPath);
        var writer = ownsWriter
            ? new StreamWriter(File.Open(executionOptions.OverloadLogPath!, FileMode.Create, FileAccess.Write, FileShare.Read))
            : Console.Out;

        overloadResolutionLog = new OverloadResolutionLog(writer, ownsWriter);
        options = options.WithOverloadResolutionLogger(overloadResolutionLog);
    }

    return (options, overloadResolutionLog);
}

static Project ApplyCodeFixesIfRequested(RavenWorkspace workspace, ProjectId projectId, Project project, bool fix)
{
    if (!fix)
        return project;

    var applyResult = workspace.ApplyCodeFixes(
        projectId,
        BuiltInCodeFixProviders.CreateDefault());

    if (applyResult.AppliedFixCount == 0)
        return project;

    workspace.TryApplyChanges(applyResult.Solution);
    project = workspace.CurrentSolution.GetProject(projectId)!;

    foreach (var appliedFix in applyResult.AppliedFixes)
    {
        var document = workspace.CurrentSolution.GetDocument(appliedFix.DocumentId);
        var path = document?.FilePath ?? document?.Name ?? "<unknown>";
        var lineSpan = appliedFix.Diagnostic.Location.GetLineSpan();
        var line = lineSpan.StartLinePosition.Line + 1;
        var column = lineSpan.StartLinePosition.Character + 1;
        var diagnosticId = appliedFix.Diagnostic.Id;
        var displayPath = path;
        if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path))
            displayPath = Path.GetRelativePath(Environment.CurrentDirectory, path);

        var escapedPath = Markup.Escape(displayPath);
        AnsiConsole.MarkupLine(
            $"[grey]{escapedPath}({line},{column}):[/] [blue]{diagnosticId}[/] {appliedFix.Action.Title}");
    }

    WriteProjectDocumentsToDisk(project);
    AnsiConsole.MarkupLine($"[green]Applied {applyResult.AppliedFixCount} code fix(es).[/]");
    return project;
}

static Project ApplyFormattingIfRequested(RavenWorkspace workspace, ProjectId projectId, Project project, bool format)
{
    if (!format)
        return project;

    var formattedDocumentCount = 0;
    var formattedSolution = project.Solution;

    foreach (var document in project.Documents)
    {
        var currentDocument = formattedSolution.GetDocument(document.Id);
        if (currentDocument is null)
            continue;

        var syntaxTree = currentDocument.GetSyntaxTreeAsync().GetAwaiter().GetResult();
        if (syntaxTree is null)
            continue;

        var root = syntaxTree.GetRoot();
        var normalizedRoot = root.NormalizeWhitespace();
        if (string.Equals(root.ToFullString(), normalizedRoot.ToFullString(), StringComparison.Ordinal))
            continue;

        formattedSolution = currentDocument.WithSyntaxRoot(normalizedRoot).Project.Solution;
        formattedDocumentCount++;
    }

    if (formattedDocumentCount == 0)
    {
        AnsiConsole.MarkupLine("[green]All files are already formatted.[/]");
        return project;
    }

    workspace.TryApplyChanges(formattedSolution);
    project = workspace.CurrentSolution.GetProject(projectId)!;

    var updatedDocumentCount = WriteProjectDocumentsToDisk(project);
    AnsiConsole.MarkupLine($"[green]Formatted {updatedDocumentCount} file(s).[/]");
    return project;
}

static int WriteProjectDocumentsToDisk(Project project)
{
    var updatedDocumentCount = 0;

    foreach (var document in project.Documents)
    {
        if (string.IsNullOrWhiteSpace(document.FilePath))
            continue;

        var text = document.GetTextAsync().GetAwaiter().GetResult();
        var newText = text.ToString();
        var existingText = File.Exists(document.FilePath) ? File.ReadAllText(document.FilePath) : null;
        if (string.Equals(existingText, newText, StringComparison.Ordinal))
            continue;

        var encoding = text.Encoding ?? Encoding.UTF8;
        if (encoding is UTF8Encoding)
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        File.WriteAllText(document.FilePath, newText, encoding);
        updatedDocumentCount++;
    }

    return updatedDocumentCount;
}

static Project AddMetadataReferenceIfMissing(Project project, string assemblyPath)
{
    var fullPath = Path.GetFullPath(assemblyPath);
    var assemblyName = Path.GetFileNameWithoutExtension(fullPath);
    return project.MetadataReferences
        .OfType<PortableExecutableReference>()
        .Any(reference =>
            string.Equals(
                Path.GetFileNameWithoutExtension(reference.FilePath),
                assemblyName,
                StringComparison.OrdinalIgnoreCase))
        ? project
        : project.AddMetadataReference(MetadataReference.CreateFromFile(fullPath));
}

static Project ReplaceMetadataReferenceByAssemblyIdentity(Project project, string assemblyPath)
{
    var fullPath = Path.GetFullPath(assemblyPath);
    var replacementIdentity = AssemblyName.GetAssemblyName(fullPath).Name;
    var references = project.MetadataReferences
        .Where(reference => reference is not PortableExecutableReference portableReference ||
            !HasAssemblyName(portableReference.FilePath, replacementIdentity))
        .Append(MetadataReference.CreateFromFile(fullPath));

    return project.WithMetadataReferences(references);
}

static Project ReplaceMacroReferenceByAssemblyIdentity(Project project, string assemblyPath)
{
    var fullPath = Path.GetFullPath(assemblyPath);
    var replacementIdentity = AssemblyName.GetAssemblyName(fullPath).Name;
    var references = project.MacroReferences
        .Where(reference =>
            !Path.IsPathFullyQualified(reference.Display) ||
            !HasAssemblyName(reference.Display, replacementIdentity))
        .Append(MacroReference.CreateFromFile(fullPath));

    return project.Solution
        .WithMacroReferences(project.Id, references)
        .GetProject(project.Id)!;
}

static bool HasAssemblyName(string assemblyPath, string? expectedName)
{
    if (string.IsNullOrWhiteSpace(expectedName))
        return false;

    try
    {
        return string.Equals(
            AssemblyName.GetAssemblyName(assemblyPath).Name,
            expectedName,
            StringComparison.OrdinalIgnoreCase);
    }
    catch (BadImageFormatException)
    {
        return false;
    }
    catch (FileLoadException)
    {
        return false;
    }
    catch (FileNotFoundException)
    {
        return false;
    }
}

static void CopyEmittedLocalDependencies(
    Project project,
    string assemblyPath,
    string outputDirectory,
    string targetFramework)
{
    if (!File.Exists(assemblyPath))
        return;

    var knownAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
    {
        if (!string.IsNullOrWhiteSpace(reference.FilePath) &&
            File.Exists(reference.FilePath))
        {
            var dependencyPath = ResolveNuGetRuntimeAssemblyPath(
                Path.GetFullPath(reference.FilePath),
                targetFramework);
            knownAssemblies.TryAdd(
                Path.GetFileNameWithoutExtension(reference.FilePath),
                dependencyPath);
        }
    }

    foreach (var directory in knownAssemblies.Values
        .Select(Path.GetDirectoryName)
        .Append(AppContext.BaseDirectory)
        .Where(static path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray())
    {
        foreach (var candidate in Directory.EnumerateFiles(directory!, "*.dll"))
            knownAssemblies.TryAdd(
                Path.GetFileNameWithoutExtension(candidate),
                candidate);
    }

    var copiedFiles = new List<string>();
    var pending = new Queue<string>(GetReferencedAssemblyNames(assemblyPath));
    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    while (pending.TryDequeue(out var referencedAssemblyName))
    {
        if (!visited.Add(referencedAssemblyName) ||
            !knownAssemblies.TryGetValue(referencedAssemblyName, out var dependencyPath))
        {
            continue;
        }
        if (IsPlatformAssemblyReference(referencedAssemblyName, dependencyPath))
            continue;

        var destinationPath = Path.Combine(outputDirectory, Path.GetFileName(dependencyPath));
        if (string.Equals(dependencyPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            continue;

        File.Copy(dependencyPath, destinationPath, overwrite: true);
        copiedFiles.Add(Path.GetFileName(destinationPath));
        foreach (var transitiveName in GetReferencedAssemblyNames(dependencyPath))
            pending.Enqueue(transitiveName);
    }

    var manifestPath = Path.Combine(outputDirectory, ".raven-runtime-dependencies");
    if (copiedFiles.Count == 0)
        TryDeleteFile(manifestPath);
    else
        File.WriteAllLines(manifestPath, copiedFiles.Distinct(StringComparer.OrdinalIgnoreCase));
}

static IEnumerable<string> GetReferencedAssemblyNames(string assemblyPath)
{
    using var stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    if (!peReader.HasMetadata)
        yield break;

    var reader = peReader.GetMetadataReader();
    foreach (var handle in reader.AssemblyReferences)
        yield return reader.GetString(reader.GetAssemblyReference(handle).Name);
}

static bool IsPlatformAssemblyReference(string assemblyName, string assemblyPath)
    => string.Equals(assemblyName, "Raven.Core", StringComparison.OrdinalIgnoreCase) ||
       string.Equals(assemblyName, "System.Private.CoreLib", StringComparison.OrdinalIgnoreCase) ||
       string.Equals(assemblyName, "mscorlib", StringComparison.OrdinalIgnoreCase) ||
       string.Equals(assemblyName, "netstandard", StringComparison.OrdinalIgnoreCase) ||
       IsUnderAnyDotNetSharedRoot(assemblyPath) ||
       assemblyPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
           .Any(static segment => string.Equals(segment, "packs", StringComparison.OrdinalIgnoreCase));

static string? FindDebugDirectory()
{
    var dir = Environment.CurrentDirectory;
    while (dir is not null)
    {
        var debug = Path.Combine(dir, ".debug");
        if (Directory.Exists(debug))
            return debug;

        dir = Path.GetDirectoryName(dir);
    }

    return null;
}

static void CopyNuGetReferencesToOutput(Project project, string targetFramework, string outputDirectory)
{
    var globalPackages = GetNuGetGlobalPackagesFolder();
    if (string.IsNullOrWhiteSpace(globalPackages) || !Directory.Exists(globalPackages))
        return;

    var copiedFileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var nuGetPackageRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
    {
        var referencePath = reference.FilePath;
        if (string.IsNullOrWhiteSpace(referencePath))
            continue;

        var fullReferencePath = Path.GetFullPath(referencePath);
        var isNuGetPackageReference = IsUnderDirectory(fullReferencePath, globalPackages);
        var isSharedFrameworkReference = IsUnderAnyDotNetSharedRoot(fullReferencePath);
        if (!isNuGetPackageReference && !isSharedFrameworkReference)
            continue;

        if (isNuGetPackageReference && TryGetNuGetPackageRoot(fullReferencePath, globalPackages, out var packageRoot))
            nuGetPackageRoots.Add(packageRoot);

        var runtimePath = isNuGetPackageReference
            ? ResolveNuGetRuntimeAssemblyPath(fullReferencePath, targetFramework)
            : fullReferencePath;
        if (!File.Exists(runtimePath))
            continue;

        Directory.CreateDirectory(outputDirectory);
        var fileName = Path.GetFileName(runtimePath);
        var destination = Path.Combine(outputDirectory, fileName);

        if (copiedFileNames.TryGetValue(fileName, out var existingSource) &&
            !string.Equals(existingSource, runtimePath, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning: Skipping NuGet reference '{Markup.Escape(runtimePath)}' because '{Markup.Escape(fileName)}' was already copied from '{Markup.Escape(existingSource)}'.[/]");
            continue;
        }

        copiedFileNames[fileName] = runtimePath;

        if (!string.Equals(runtimePath, destination, StringComparison.OrdinalIgnoreCase))
            File.Copy(runtimePath, destination, overwrite: true);

        CopyDependencySidecarFile(runtimePath, outputDirectory, ".pdb");
        CopyDependencySidecarFile(runtimePath, outputDirectory, ".xml");
    }

    CopyNuGetNativeRuntimeAssets(nuGetPackageRoots, globalPackages, outputDirectory, copiedFileNames);
}

static void CopyNuGetNativeRuntimeAssets(
    HashSet<string> packageRoots,
    string globalPackages,
    string outputDirectory,
    Dictionary<string, string> copiedFileNames)
{
    if (packageRoots.Count == 0)
        return;

    Directory.CreateDirectory(outputDirectory);

    foreach (var packageRoot in ExpandNuGetPackageRootsWithDependencies(packageRoots, globalPackages))
    {
        foreach (var runtimeIdentifier in GetNativeRuntimeIdentifierFallbacks())
        {
            var nativeDirectory = Path.Combine(packageRoot, "runtimes", runtimeIdentifier, "native");
            if (!Directory.Exists(nativeDirectory))
                continue;

            foreach (var assetPath in Directory.EnumerateFiles(nativeDirectory, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(assetPath);
                var destination = Path.Combine(outputDirectory, fileName);

                if (copiedFileNames.TryGetValue(fileName, out var existingSource) &&
                    !string.Equals(existingSource, assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Warning: Skipping NuGet native asset '{Markup.Escape(assetPath)}' because '{Markup.Escape(fileName)}' was already copied from '{Markup.Escape(existingSource)}'.[/]");
                    continue;
                }

                copiedFileNames[fileName] = assetPath;

                if (!string.Equals(assetPath, destination, StringComparison.OrdinalIgnoreCase))
                    File.Copy(assetPath, destination, overwrite: true);
            }
        }
    }
}

static IEnumerable<string> ExpandNuGetPackageRootsWithDependencies(HashSet<string> packageRoots, string globalPackages)
{
    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var queue = new Queue<string>(packageRoots);

    while (queue.Count > 0)
    {
        var packageRoot = queue.Dequeue();
        if (!visited.Add(packageRoot))
            continue;

        yield return packageRoot;

        foreach (var dependencyRoot in ResolveNuGetPackageDependencyRoots(packageRoot, globalPackages))
        {
            if (!visited.Contains(dependencyRoot))
                queue.Enqueue(dependencyRoot);
        }
    }
}

static IEnumerable<string> ResolveNuGetPackageDependencyRoots(string packageRoot, string globalPackages)
{
    var nuspecPath = Directory.EnumerateFiles(packageRoot, "*.nuspec", SearchOption.TopDirectoryOnly)
        .FirstOrDefault();
    if (nuspecPath is null)
        yield break;

    XDocument document;
    try
    {
        document = XDocument.Load(nuspecPath);
    }
    catch
    {
        yield break;
    }

    foreach (var dependency in document.Descendants().Where(static element => element.Name.LocalName == "dependency"))
    {
        var id = dependency.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(id))
            continue;

        var version = NormalizeNuGetDependencyVersion(dependency.Attribute("version")?.Value);
        if (!string.IsNullOrWhiteSpace(version))
        {
            var exactRoot = Path.Combine(globalPackages, id.ToLowerInvariant(), version.ToLowerInvariant());
            if (Directory.Exists(exactRoot))
            {
                yield return exactRoot;
                continue;
            }
        }

        var packageDirectory = Path.Combine(globalPackages, id.ToLowerInvariant());
        if (!Directory.Exists(packageDirectory))
            continue;

        var installedRoot = Directory.EnumerateDirectories(packageDirectory)
            .OrderByDescending(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (installedRoot is not null)
            yield return installedRoot;
    }
}

static string? NormalizeNuGetDependencyVersion(string? version)
{
    if (string.IsNullOrWhiteSpace(version))
        return null;

    version = version.Trim();
    if (version.Length >= 2 && (version[0] == '[' || version[0] == '('))
    {
        var end = version.IndexOf(',', StringComparison.Ordinal);
        if (end > 0)
            return version[1..end].Trim();

        return version.Trim('[', ']', '(', ')').Trim();
    }

    return version;
}

static IEnumerable<string> GetNativeRuntimeIdentifierFallbacks()
{
    var architecture = GetRuntimeIdentifierArchitecture();

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        yield return $"osx-{architecture}";
        yield return "osx";
        yield return "unix";
        yield break;
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        yield return $"win-{architecture}";
        yield return "win";
        yield break;
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        yield return $"linux-{architecture}";
        yield return "linux";
        yield return "unix";
    }
}

static string GetRuntimeIdentifierArchitecture() =>
    RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
    };

static bool TryGetNuGetPackageRoot(string referencePath, string globalPackages, out string packageRoot)
{
    packageRoot = string.Empty;

    var relativePath = Path.GetRelativePath(globalPackages, referencePath);
    if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        return false;

    var segments = relativePath.Split(
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
        StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length < 3)
        return false;

    var candidate = Path.Combine(globalPackages, segments[0], segments[1]);
    if (!Directory.Exists(candidate))
        return false;

    packageRoot = candidate;
    return true;
}

static void CopyDependencySidecarFile(string assemblyPath, string outputDirectory, string extension)
{
    var sidecarPath = Path.ChangeExtension(assemblyPath, extension);
    if (!File.Exists(sidecarPath))
        return;

    var destinationPath = Path.Combine(outputDirectory, Path.GetFileName(sidecarPath));
    if (!string.Equals(sidecarPath, destinationPath, StringComparison.OrdinalIgnoreCase))
        File.Copy(sidecarPath, destinationPath, overwrite: true);
}

static string ResolveNuGetRuntimeAssemblyPath(string referencePath, string targetFramework)
{
    var normalized = referencePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    var refSegment = $"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}";
    var refIndex = normalized.IndexOf(refSegment, StringComparison.OrdinalIgnoreCase);
    if (refIndex < 0)
        return referencePath;

    if (TryResolveSharedFrameworkRuntimeAssembly(
            normalized,
            refIndex,
            targetFramework,
            out var sharedFrameworkAssembly))
    {
        return sharedFrameworkAssembly;
    }

    var libCandidate = normalized[..refIndex] +
                       $"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}" +
                       normalized[(refIndex + refSegment.Length)..];
    if (File.Exists(libCandidate))
        return libCandidate;

    var packageRoot = FindNuGetPackageRoot(normalized, refIndex);
    if (packageRoot is null)
        return referencePath;

    var fileName = Path.GetFileName(referencePath);
    var targetCandidate = Path.Combine(packageRoot, "lib", targetFramework, fileName);
    if (File.Exists(targetCandidate))
        return targetCandidate;

    var alternatives = Directory.Exists(Path.Combine(packageRoot, "lib"))
        ? Directory.EnumerateFiles(Path.Combine(packageRoot, "lib"), fileName, SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        : Enumerable.Empty<string>();

    return alternatives.FirstOrDefault() ?? referencePath;
}

static bool TryResolveSharedFrameworkRuntimeAssembly(
    string normalizedReferencePath,
    int refSegmentStartIndex,
    string targetFramework,
    out string assemblyPath)
{
    assemblyPath = string.Empty;

    var packageRoot = FindNuGetPackageRoot(normalizedReferencePath, refSegmentStartIndex);
    var packageId = packageRoot is null ? null : Path.GetFileName(packageRoot);
    var frameworkName = packageId?.ToLowerInvariant() switch
    {
        "microsoft.aspnetcore.app.ref" => "Microsoft.AspNetCore.App",
        "microsoft.netcore.app.ref" => "Microsoft.NETCore.App",
        _ => null
    };
    if (frameworkName is null)
        return false;

    var targetMoniker = TargetFrameworkMoniker.Parse(targetFramework);
    var fileName = Path.GetFileName(normalizedReferencePath);
    foreach (var dotNetRoot in GetDotNetRootsForDependencyCopy())
    {
        var frameworkRoot = Path.Combine(dotNetRoot, "shared", frameworkName);
        if (!Directory.Exists(frameworkRoot))
            continue;

        var exactVersion = $"{targetMoniker.Version.Major}.{targetMoniker.Version.Minor}.0";
        var exactCandidate = Path.Combine(frameworkRoot, exactVersion, fileName);
        if (File.Exists(exactCandidate))
        {
            assemblyPath = exactCandidate;
            return true;
        }

        var candidate = Directory.EnumerateDirectories(frameworkRoot)
            .Select(path => new
            {
                Path = path,
                Name = Path.GetFileName(path),
                Version = Version.TryParse(Path.GetFileName(path).Split('-')[0], out var version)
                    ? version
                    : null
            })
            .Where(candidate =>
                candidate.Version is not null &&
                candidate.Version.Major == targetMoniker.Version.Major &&
                candidate.Version.Minor == targetMoniker.Version.Minor)
            .OrderByDescending(static candidate => candidate.Version)
            .ThenByDescending(static candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => Path.Combine(candidate.Path, fileName))
            .FirstOrDefault(File.Exists);
        if (candidate is not null)
        {
            assemblyPath = candidate;
            return true;
        }
    }

    return false;
}

static string? FindNuGetPackageRoot(string normalizedReferencePath, int refSegmentStartIndex)
{
    // Path shape: .../<id>/<version>/ref/<tfm>/<assembly>.dll
    var beforeRef = normalizedReferencePath[..refSegmentStartIndex];
    var versionDir = Path.GetDirectoryName(beforeRef);
    if (string.IsNullOrWhiteSpace(versionDir))
        return null;
    var packageDir = Path.GetDirectoryName(versionDir);
    if (string.IsNullOrWhiteSpace(packageDir))
        return null;
    return Path.Combine(packageDir, Path.GetFileName(versionDir));
}

static string GetNuGetGlobalPackagesFolder()
{
    var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (!string.IsNullOrWhiteSpace(env))
        return Path.GetFullPath(env);

    var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    if (string.IsNullOrWhiteSpace(userProfile))
        return string.Empty;

    return Path.Combine(userProfile, ".nuget", "packages");
}

static bool IsUnderDirectory(string path, string directory)
{
    var fullPath = Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var fullDirectory = Path.GetFullPath(directory)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase);
}

static bool IsUnderAnyDotNetSharedRoot(string path)
{
    foreach (var root in GetDotNetRootsForDependencyCopy())
    {
        var sharedRoot = Path.Combine(root, "shared");
        if (Directory.Exists(sharedRoot) && IsUnderDirectory(path, sharedRoot))
            return true;
    }

    return false;
}

static IEnumerable<string> GetDotNetRootsForDependencyCopy()
{
    var envRoots = new[]
    {
        Environment.GetEnvironmentVariable("DOTNET_ROOT"),
        Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)")
    }.Where(static value => !string.IsNullOrWhiteSpace(value));

    foreach (var root in envRoots)
    {
        if (Directory.Exists(root))
            yield return root!;
    }

    if (OperatingSystem.IsWindows())
    {
        var x64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        var x86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet");
        if (Directory.Exists(x64))
            yield return x64;
        if (Directory.Exists(x86))
            yield return x86;
    }
    else if (OperatingSystem.IsMacOS())
    {
        const string appleDefault = "/usr/local/share/dotnet";
        const string brew = "/opt/homebrew/opt/dotnet/libexec";
        if (Directory.Exists(appleDefault))
            yield return appleDefault;
        if (Directory.Exists(brew))
            yield return brew;
    }
    else
    {
        var candidates = new[]
        {
            "/usr/share/dotnet",
            "/usr/lib/dotnet",
            "/snap/dotnet-sdk/current",
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                yield return candidate;
        }
    }
}

static void PrintHelp(bool compilerDriverOnly)
{
    if (compilerDriverOnly)
    {
        Console.WriteLine("Usage: rvnc [compiler-options] <source-files|project-file.rvnproj>");
        Console.WriteLine();
        Console.WriteLine("Compiler options:");
        Console.WriteLine("  --version          Print the Raven version");
        Console.WriteLine("  --framework <tfm>  Target framework (e.g. net10.0)");
        Console.WriteLine("  --no-framework-references");
        Console.WriteLine("                     Do not add the default .NET targeting pack.");
        Console.WriteLine("  --target-core-library <path>");
        Console.WriteLine("                     Retarget emitted core type scopes to the supplied assembly identity.");
        Console.WriteLine("  --configuration <name>");
        Console.WriteLine("                     MSBuild configuration used to evaluate a project (default: Debug)");
        Console.WriteLine("  --refs <path>      Additional metadata reference (repeatable)");
        Console.WriteLine("  --define <symbols> Conditional compilation symbols separated by ',' or ';'");
        Console.WriteLine("  --constant <name=value>");
        Console.WriteLine("                     Supply or override a typed extern const (repeatable)");
        Console.WriteLine("  --raven-core <path> Reference a prebuilt Raven.Core.dll instead of embedding compiler shims");
        Console.WriteLine("  --emit-core-types-only Embed Raven.Core shims even when Raven.Core.dll is available");
        Console.WriteLine("  --output-type <console|classlib>");
        Console.WriteLine("                     Output kind for the produced assembly.");
        Console.WriteLine("  --generated-files-output-path <path>  Write source-generator output to this directory.");
        Console.WriteLine("  --dependency-file <path>");
        Console.WriteLine("                     Write observed compile-time file dependencies.");
        Console.WriteLine("  --unsafe           Enable unsafe mode (required for pointer declarations/usages)");
        Console.WriteLine("  --global-statements");
        Console.WriteLine("                     Enable top-level/global statements (default)");
        Console.WriteLine("  --no-global-statements");
        Console.WriteLine("                     Disable top-level/global statements");
        Console.WriteLine("  --namespace-members");
        Console.WriteLine("                     Enable namespace-scope function and const declarations (default)");
        Console.WriteLine("  --no-namespace-members");
        Console.WriteLine("                     Disable namespace-scope function and const declarations");
        Console.WriteLine("  --namespace-member-imports");
        Console.WriteLine("                     Enable namespace imports from [TopLevel] containers (default)");
        Console.WriteLine("  --no-namespace-member-imports");
        Console.WriteLine("                     Disable namespace imports from [TopLevel] containers");
        Console.WriteLine("  --runtime-async    Enable runtime-async metadata emission");
        Console.WriteLine("  --no-runtime-async");
        Console.WriteLine("                     Disable runtime-async metadata emission (auto-enabled for net11+)");
        Console.WriteLine("  --no-project-restore");
        Console.WriteLine("                     Use already resolved references instead of restoring project packages");
        Console.WriteLine("  -o <path>          Output path.");
        Console.WriteLine("  --no-emit          Skip emitting the output assembly");
        Console.WriteLine("  -h, --help         Display help");
        return;
    }

    Console.WriteLine("Usage: rvn [options] <source-files|project-file.rvnproj>");
    Console.WriteLine("       rvnc [compiler-options] <source-files|project-file.rvnproj>");
    Console.WriteLine("       rvn init [console|classlib] [--name <project-name>] [--framework <tfm>] [--force]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --version          Print the Raven version");
    Console.WriteLine("  --framework <tfm>  Target framework (e.g. net8.0)");
    Console.WriteLine("  --refs <path>      Additional metadata reference (repeatable)");
    Console.WriteLine("  --define <symbols> Conditional compilation symbols separated by ',' or ';'");
    Console.WriteLine("  --constant <name=value> Supply or override a typed extern const (repeatable)");
    Console.WriteLine("  --raven-core <path> Reference a prebuilt Raven.Core.dll instead of embedding compiler shims");
    Console.WriteLine("  --emit-core-types-only Embed Raven.Core shims even when Raven.Core.dll is available");
    Console.WriteLine("  --emit-docs       Emit documentation from comments");
    Console.WriteLine("  --output-type <console|classlib>");
    Console.WriteLine("                     Output kind for the produced assembly.");
    Console.WriteLine("  --unsafe         Enable unsafe mode (required for pointer declarations/usages)");
    Console.WriteLine("  --global-statements");
    Console.WriteLine("                     Enable top-level/global statements (default)");
    Console.WriteLine("  --no-global-statements");
    Console.WriteLine("                     Disable top-level/global statements");
    Console.WriteLine("  --namespace-members");
    Console.WriteLine("                     Enable namespace-scope function and const declarations (default)");
    Console.WriteLine("  --no-namespace-members");
    Console.WriteLine("                     Disable namespace-scope function and const declarations");
    Console.WriteLine("  --namespace-member-imports");
    Console.WriteLine("                     Enable namespace imports from [TopLevel] containers (default)");
    Console.WriteLine("  --no-namespace-member-imports");
    Console.WriteLine("                     Disable namespace imports from [TopLevel] containers");
    Console.WriteLine("  --runtime-async  Enable runtime-async metadata emission");
    Console.WriteLine("  --no-runtime-async");
    Console.WriteLine("                     Disable runtime-async metadata emission (auto-enabled for net11+)");
    Console.WriteLine("  -o <path>          Output path.");
    Console.WriteLine("                     For Raven source-file inputs (.rvn, legacy .rav): output assembly path.");
    Console.WriteLine("                     For Raven project-file inputs (.rvnproj): output directory path (default: <project-dir>/bin/<Configuration>).");
    Console.WriteLine("  -s [flat|group]    Display the syntax tree (single file only)");
    Console.WriteLine("                     Use 'group' to display syntax lists grouped by property.");
    Console.WriteLine("  -ps                Print the parsing sequence");
    Console.WriteLine("  --ps-delay <ms>    Delay each parse-sequence log line by <ms> milliseconds");
    Console.WriteLine("  -d [plain|pretty[:no-diagnostics]] Dump syntax (single file only)");
    Console.WriteLine("                     'plain' writes the source text, 'pretty' writes highlighted syntax.");
    Console.WriteLine("                     Append ':no-diagnostics' to skip diagnostic underlines when using 'pretty'.");
    Console.WriteLine("  --dump-macros [original|expanded|both][:plain|pretty[:no-diagnostics]]");
    Console.WriteLine("                     Dump original and/or expanded macro source (single file only).");
    Console.WriteLine("                     Defaults to 'both:plain'. Expanded pretty output is colorized without diagnostic underlines.");
    Console.WriteLine("  -r                 Print the source (single file only)");
    Console.WriteLine("  -b                 Print binder tree (single file only)");
    Console.WriteLine("  -bt                Print binder and bound tree (single file only)");
    Console.WriteLine("  -bte               Print binder and bound tree with error nodes (single file only)");
    Console.WriteLine("  --bound-tree-view [original|lowered|both]");
    Console.WriteLine("                     Select which bound tree view to print (default: original).");
    Console.WriteLine("  --symbols [list|hierarchy]");
    Console.WriteLine("                     Inspect symbols produced from source.");
    Console.WriteLine("                     'list' dumps properties, 'hierarchy' prints the tree.");
    Console.WriteLine("  --overload-log [path]");
    Console.WriteLine("                     Log overload resolution details to the console or the provided file.");
    Console.WriteLine("  --highlight       Display diagnostics with highlighted source snippets");
    Console.WriteLine("  --suggestions    Display educational rewrite suggestions for diagnostics that provide them");
    Console.WriteLine("  --returned-value-handling <default|full|none|info|warning|error>");
    Console.WriteLine("                    Configure full RAV9034 analysis for returned values that are not handled");
    Console.WriteLine("  --force-returned-value-handling");
    Console.WriteLine("                    Treat returned values that are not handled as errors");
    Console.WriteLine("  -q, --quote        Display AST as compilable C# code.");
    Console.WriteLine("  --no-emit        Skip emitting the output assembly");
    Console.WriteLine("  --publish        Emit runtime artifacts; for Raven project-file inputs defaults output to <project-dir>/bin/<Configuration>/publish");
    Console.WriteLine("  --fix            Apply supported code fixes to source files before compiling");
    Console.WriteLine("  --format         Normalize whitespace and indentation in source files before compiling");
    Console.WriteLine("  --doc-tool [ravendoc|comments]");
    Console.WriteLine("                    Documentation generator to use (default: ravendoc).");
    Console.WriteLine("  --doc-format [md|xml]");
    Console.WriteLine("                    Documentation format for comment emission (default: md).");
    Console.WriteLine("  --ilverify       Verify emitted IL using the 'ilverify' tool");
    Console.WriteLine("  --ilverify-path <path>");
    Console.WriteLine("                    Path to the ilverify executable when not on PATH");
    Console.WriteLine("  --run             Execute the produced assembly after a successful compilation (console apps only)");
    Console.WriteLine("  -h, --help         Display help");
    Console.WriteLine();
    Console.WriteLine("Init command:");
    Console.WriteLine("  init              Create a .rvnproj project scaffold in the current directory.");
    Console.WriteLine("  init [console|classlib]");
    Console.WriteLine("                    Choose the scaffold type (default: console).");
    Console.WriteLine("  init --name <name>");
    Console.WriteLine("                    Override the generated project/assembly name.");
    Console.WriteLine("  init --framework <tfm>");
    Console.WriteLine("                    Set TargetFramework in the generated project file.");
    Console.WriteLine("  init --type <console|classlib>");
    Console.WriteLine("                    Compatibility alias for selecting the scaffold type.");
    Console.WriteLine("  init --force      Overwrite existing scaffold files.");
}

static bool IsHelp(string value)
    => value is "-h" or "--help" or "help";

static bool TryGetCompilerDriverRejectedOption(
    bool run,
    bool publish,
    bool fix,
    bool format,
    bool emitDocs,
    bool highlightDiagnostics,
    bool showSuggestions,
    bool printSyntaxTree,
    bool printSyntaxTreeInternal,
    bool printRawSyntax,
    bool printSyntax,
    string? macroSourceDumpTarget,
    bool printBinders,
    bool printBoundTree,
    bool printParseSequence,
    bool quote,
    SymbolDumpMode symbolDumpMode,
    out string option)
{
    option = run ? "--run"
        : publish ? "--publish"
        : fix ? "--fix"
        : format ? "--format"
        : emitDocs ? "--emit-docs"
        : highlightDiagnostics ? "--highlight"
        : showSuggestions ? "--suggestions"
        : printSyntaxTree ? "--syntax-tree"
        : printSyntaxTreeInternal ? "--syntax-tree--internal"
        : printRawSyntax || printSyntax ? "--dump"
        : macroSourceDumpTarget is not null ? "--dump-macros"
        : printBinders ? "--display-binders"
        : printBoundTree ? "--bound-tree"
        : printParseSequence ? "--parse-sequence"
        : quote ? "--quote"
        : symbolDumpMode != SymbolDumpMode.None ? "--symbols"
        : string.Empty;

    return option.Length > 0;
}

static void RunAssembly(string outputFilePath, IReadOnlyList<string> arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        UseShellExecute = false,
    };
    startInfo.ArgumentList.Add(outputFilePath);
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);

    using var process = Process.Start(startInfo);

    if (process is null)
    {
        AnsiConsole.MarkupLine("[red]Failed to start the produced assembly.[/]");
        Environment.ExitCode = 1;
        return;
    }

    process.WaitForExit();
    Environment.ExitCode = process.ExitCode;
}

static bool TryParseSyntaxTreeFormat(string[] args, ref int index, out SyntaxTreeFormat format)
{
    var value = ConsumeOptionValue(args, ref index);
    if (value is null)
    {
        format = SyntaxTreeFormat.Flat;
        return true;
    }

    switch (value.ToLowerInvariant())
    {
        case "group":
        case "grouped":
            format = SyntaxTreeFormat.Group;
            return true;
        case "flat":
            format = SyntaxTreeFormat.Flat;
            return true;
    }

    AnsiConsole.MarkupLine($"[red]Unknown syntax tree format '{value}'.[/]");
    format = SyntaxTreeFormat.Flat;
    return false;
}

static bool TryParseSyntaxDumpFormat(string[] args, ref int index, out bool printRawSyntax, out bool printSyntax,
    out bool includeDiagnostics)
{
    printRawSyntax = false;
    printSyntax = false;
    includeDiagnostics = true;

    var value = ConsumeOptionValue(args, ref index);
    if (value is null)
    {
        printRawSyntax = true;
        return true;
    }

    var segments = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0)
    {
        printRawSyntax = true;
        return true;
    }

    switch (segments[0].ToLowerInvariant())
    {
        case "plain":
            if (segments.Length > 1)
            {
                AnsiConsole.MarkupLine("[red]The 'plain' syntax dump does not accept modifiers.[/]");
                return false;
            }

            printRawSyntax = true;
            return true;

        case "pretty":
            includeDiagnostics = true;
            foreach (var modifier in segments.Skip(1))
            {
                switch (modifier.ToLowerInvariant())
                {
                    case "no-diagnostics":
                    case "no-underline":
                        includeDiagnostics = false;
                        break;
                    case "diagnostics":
                    case "underline":
                        includeDiagnostics = true;
                        break;
                    default:
                        AnsiConsole.MarkupLine($"[red]Unknown modifier '{modifier}' for pretty syntax dump.[/]");
                        return false;
                }
            }

            printSyntax = true;
            return true;
    }

    AnsiConsole.MarkupLine($"[red]Unknown syntax dump format '{value}'.[/]");
    return false;
}

static bool TryParseMacroSourceDumpFormat(
    string[] args,
    ref int index,
    out string? target,
    out bool printRawSyntax,
    out bool printSyntax,
    out bool includeDiagnostics)
{
    target = "both";
    printRawSyntax = true;
    printSyntax = false;
    includeDiagnostics = true;

    var value = ConsumeOptionValue(args, ref index);
    if (value is null)
        return true;

    foreach (var segment in value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        switch (segment.ToLowerInvariant())
        {
            case "original":
                target = "original";
                break;
            case "expanded":
                target = "expanded";
                break;
            case "both":
                target = "both";
                break;
            case "plain":
                printRawSyntax = true;
                printSyntax = false;
                break;
            case "pretty":
                printRawSyntax = false;
                printSyntax = true;
                break;
            case "no-diagnostics":
            case "no-underline":
                includeDiagnostics = false;
                break;
            case "diagnostics":
            case "underline":
                includeDiagnostics = true;
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Unknown macro dump segment '{segment}'.[/]");
                return false;
        }
    }

    return true;
}

static void PrintMacroSourceDump(
    string label,
    SyntaxNode root,
    Compilation compilation,
    SyntaxTree? syntaxTree,
    ImmutableArray<Diagnostic> diagnostics,
    bool printRawSyntax,
    bool printSyntax,
    bool includeDiagnostics)
{
    AnsiConsole.MarkupLine($"[grey]Macro source: {Markup.Escape(label)}[/]");

    if (printRawSyntax)
    {
        Console.WriteLine(root.ToFullString());
        Console.WriteLine();
    }

    if (printSyntax)
    {
        ConsoleSyntaxHighlighter.ColorScheme = ColorScheme.Light;
        var prettyDiagnostics = includeDiagnostics && syntaxTree is not null
            ? diagnostics.Where(d => d.Location.SourceTree == syntaxTree)
            : null;
        var highlighted = root.WriteNodeToText(
            compilation,
            includeDiagnostics: includeDiagnostics && syntaxTree is not null,
            diagnostics: prettyDiagnostics);

        Console.WriteLine(highlighted);
        Console.WriteLine();
    }
}

static void DeleteLegacyDebugSourceArtifact(string debugDir, string fileName)
{
    var path = Path.Combine(debugDir, fileName);
    if (File.Exists(path))
        File.Delete(path);
}

static bool TryParseOutputKind(string[] args, ref int index, out OutputKind outputKind)
{
    var value = ConsumeOptionValue(args, ref index);

    if (value is null)
    {
        outputKind = OutputKind.ConsoleApplication;
        return true;
    }

    switch (value.ToLowerInvariant())
    {
        case "console":
        case "consoleapp":
        case "console-app":
        case "exe":
            outputKind = OutputKind.ConsoleApplication;
            return true;

        case "classlib":
        case "class-library":
        case "classlibrary":
        case "library":
        case "dll":
            outputKind = OutputKind.DynamicallyLinkedLibrary;
            return true;
    }

    AnsiConsole.MarkupLine($"[red]Unknown output type '{value}'.[/]");
    outputKind = OutputKind.ConsoleApplication;
    return false;
}

static bool TryParseDocumentationFormat(string[] args, ref int index, out DocumentationFormat format)
{
    var value = ConsumeOptionValue(args, ref index);

    if (value is null)
    {
        format = DocumentationFormat.Markdown;
        return true;
    }

    switch (value.ToLowerInvariant())
    {
        case "md":
        case "markdown":
            format = DocumentationFormat.Markdown;
            return true;
        case "xml":
            format = DocumentationFormat.Xml;
            return true;
    }

    AnsiConsole.MarkupLine($"[red]Unknown documentation format '{value}'.[/]");
    format = DocumentationFormat.Markdown;
    return false;
}

static bool TryParseDocumentationTool(string[] args, ref int index, out DocumentationTool tool)
{
    var value = ConsumeOptionValue(args, ref index);

    if (value is null)
    {
        tool = DocumentationTool.RavenDoc;
        return true;
    }

    switch (value.ToLowerInvariant())
    {
        case "ravendoc":
        case "doc":
        case "docs":
            tool = DocumentationTool.RavenDoc;
            return true;
        case "comments":
        case "comment":
        case "emitted":
            tool = DocumentationTool.CommentEmitter;
            return true;
    }

    AnsiConsole.MarkupLine($"[red]Unknown documentation tool '{value}'.[/]");
    tool = DocumentationTool.RavenDoc;
    return false;
}

static string? ConsumeOptionValue(string[] args, ref int index)
{
    if (index + 1 < args.Length)
    {
        var candidate = args[index + 1];
        if (!candidate.StartsWith('-'))
        {
            index++;
            return candidate;
        }
    }

    return null;
}

static bool TryParseSymbolDumpMode(string[] args, ref int index, out SymbolDumpMode mode)
{
    var value = ConsumeOptionValue(args, ref index);

    if (value is null)
    {
        mode = SymbolDumpMode.List;
        return true;
    }

    switch (value.ToLowerInvariant())
    {
        case "list":
            mode = SymbolDumpMode.List;
            return true;
        case "hierarchy":
            mode = SymbolDumpMode.Hierarchy;
            return true;
    }

    AnsiConsole.MarkupLine($"[red]Unknown symbol dump format '{value}'.[/]");
    mode = SymbolDumpMode.None;
    return false;
}

static bool TryParseBoundTreeView(string[] args, ref int index, out BoundTreeView view)
{
    var value = ConsumeOptionValue(args, ref index);

    if (value is null)
    {
        view = BoundTreeView.Original;
        return true;
    }

    switch (value.ToLowerInvariant())
    {
        case "original":
            view = BoundTreeView.Original;
            return true;
        case "lowered":
            view = BoundTreeView.Lowered;
            return true;
        case "both":
            view = BoundTreeView.Both;
            return true;
    }

    AnsiConsole.MarkupLine($"[red]Unknown bound tree view '{value}'.[/]");
    view = BoundTreeView.Original;
    return false;
}

static bool TryParseNonNegativeInt(string[] args, ref int index, out int value)
{
    value = 0;

    if (index + 1 >= args.Length)
        return false;

    if (!int.TryParse(args[++index], out value))
        return false;

    if (value < 0)
        return false;

    return true;
}

static bool TryParseExternalConstant(string specification, IDictionary<string, string> values)
{
    var separator = specification.IndexOf('=');
    if (separator <= 0)
    {
        AnsiConsole.MarkupLine(
            $"[red]Invalid external constant '{Markup.Escape(specification)}'. Expected NAME=VALUE.[/]");
        return false;
    }

    var name = specification[..separator].Trim();
    if (name.Length == 0)
    {
        AnsiConsole.MarkupLine("[red]External constant names cannot be empty.[/]");
        return false;
    }

    values[name] = specification[(separator + 1)..];
    return true;
}

static bool TryDecodeExternalConstantOverrides(string payload, IDictionary<string, string> values)
{
    try
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        var decoded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (decoded is null)
            return false;

        foreach (var pair in decoded)
            values[pair.Key] = pair.Value;

        return true;
    }
    catch (Exception ex) when (ex is FormatException or JsonException)
    {
        AnsiConsole.MarkupLine("[red]Invalid encoded external constant overrides.[/]");
        return false;
    }
}

static bool TryParseReturnedValueHandlingDiagnostic(
    string[] args,
    ref int index,
    out ReturnedValueHandlingMode? mode,
    out ReportDiagnostic? option)
{
    mode = null;
    option = null;
    var value = ConsumeOptionValue(args, ref index);
    if (value is null)
    {
        AnsiConsole.MarkupLine("[red]--returned-value-handling requires a mode or diagnostic level.[/]");
        return false;
    }

    switch (value.Trim().ToLowerInvariant())
    {
        case "default":
            return true;
        case "full":
        case "enabled":
        case "enable":
        case "true":
            mode = ReturnedValueHandlingMode.Full;
            return true;
        case "none":
        case "off":
        case "disabled":
        case "disable":
        case "false":
        case "suppress":
        case "suppressed":
            mode = ReturnedValueHandlingMode.Off;
            option = ReportDiagnostic.Suppress;
            return true;
        case "hidden":
        case "silent":
        case "suggestion":
            mode = ReturnedValueHandlingMode.Full;
            option = ReportDiagnostic.Hidden;
            return true;
        case "info":
        case "information":
            mode = ReturnedValueHandlingMode.Full;
            option = ReportDiagnostic.Info;
            return true;
        case "warning":
        case "warn":
            mode = ReturnedValueHandlingMode.Full;
            option = ReportDiagnostic.Warn;
            return true;
        case "error":
            mode = ReturnedValueHandlingMode.Full;
            option = ReportDiagnostic.Error;
            return true;
    }

    AnsiConsole.MarkupLine($"[red]Unknown returned-value handling value '{value}'.[/]");
    return false;
}

static string ResolveDocumentationOutputPath(
    string outputDirectory,
    string outputFilePath,
    string? projectFilePath,
    string? configuredPath,
    DocumentationFormat format)
{
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        var path = configuredPath!
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(path))
            return path;

        if (!string.IsNullOrWhiteSpace(projectFilePath) &&
            path.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
            if (!string.IsNullOrWhiteSpace(projectDirectory))
            {
                var projectRelativePath = Path.GetFullPath(Path.Combine(projectDirectory, path));
                var configuredDirectory = Path.GetDirectoryName(projectRelativePath);
                if (string.Equals(
                        Path.TrimEndingDirectorySeparator(configuredDirectory ?? string.Empty),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory)),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return projectRelativePath;
                }

                return Path.ChangeExtension(outputFilePath, ".xml");
            }
        }

        return Path.GetFullPath(Path.Combine(outputDirectory, path));
    }

    return format == DocumentationFormat.Markdown
        ? Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(outputFilePath)}.docs")
        : Path.ChangeExtension(outputFilePath, ".xml");
}

enum SyntaxTreeFormat
{
    Flat,
    Group
}

enum SymbolDumpMode
{
    None,
    List,
    Hierarchy
}

enum DocumentationTool
{
    RavenDoc,
    CommentEmitter
}

readonly record struct CompilerExecutionOptions(
    OutputKind OutputKind,
    OptimizationLevel OptimizationLevel,
    bool AllowUnsafe,
    bool AllowGlobalStatements,
    bool AllowNamespaceMembers,
    bool AllowNamespaceMembersSpecified,
    bool AllowNamespaceMemberImports,
    bool AllowNamespaceMemberImportsSpecified,
    bool UseRuntimeAsync,
    bool EnableSuggestions,
    bool EnableAsyncInvestigation,
    string AsyncInvestigationLabel,
    AsyncInvestigationPointerLabelScope AsyncInvestigationScope,
    bool EmbedCoreTypes,
    bool EnableOverloadLog,
    string? OverloadLogPath,
    PerformanceInstrumentation PerformanceInstrumentation,
    IReadOnlyDictionary<string, string> ExternalConstantValues);
