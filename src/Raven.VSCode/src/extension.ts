import * as fs from 'fs';
import * as path from 'path';
import * as crypto from 'crypto';
import { execFile, execFileSync, ExecFileOptions } from 'child_process';
import * as vscode from 'vscode';
import { CloseAction, ErrorAction, InlayHintRefreshRequest, InlayHintRequest, InlayHintsProviderShape, LanguageClient, LanguageClientOptions, ServerOptions, State, StateChangeEvent, Trace } from 'vscode-languageclient/node';
import { Diagnostic as ProtocolDiagnostic } from 'vscode-languageserver-protocol';
import {
  ExpandedSyntaxContentProvider,
  LoadedSyntaxTree,
  revealSyntaxTreeItem,
  SyntaxTreeDataProvider,
  SyntaxTreeItem,
  SyntaxTreeToolDocument,
  SyntaxTreeViewMode
} from './syntaxTreeVisualizer';

let client: LanguageClient | undefined;
let clientStopPromise: Promise<void> | undefined;
let clientStartPromise: Promise<void> | undefined;
let languageServerBuildPromise: Promise<void> | undefined;
const output = vscode.window.createOutputChannel('Raven');
let extensionInstallPath = '';
let activeServerResolution: ResolvedServerPath | undefined;
let pendingInlayHintRefresh: NodeJS.Timeout | undefined;
let pendingImportCompletionTrigger: NodeJS.Timeout | undefined;
const inlayHintRequestVersions = new Map<string, number>();
let inlayHintRefreshEpoch = 0;
let inlayHintRefreshPromise: Promise<void> | undefined;
const sdkInstallPromptDismissedKey = 'raven.sdkInstallPromptDismissed';
const sdkInstallationDocumentationUrl = 'https://github.com/marinasundstrom/raven/blob/main/docs/compiler/distribution.md';
const macroEmbeddedLanguageProjectionMethod = 'raven/macroEmbeddedLanguageProjection';
const macroEmbeddedDocumentScheme = 'raven-embedded';

interface LspPosition {
  line: number;
  character: number;
}

interface LspRange {
  start: LspPosition;
  end: LspPosition;
}

interface MacroEmbeddedLanguageProjectionResponse {
  languageId: string;
  text: string;
  range: LspRange;
}

interface GeneratedSourceResponse {
  text: string;
  diagnostics: ProtocolDiagnostic[];
}

interface OpenMacroEmbeddedDocument {
  virtualUri: vscode.Uri;
  virtualDocument: vscode.TextDocument;
  virtualPosition: vscode.Position;
  sourceStartOffset: number;
  requestedVersion: number;
}

class MacroEmbeddedDocumentContentProvider implements vscode.TextDocumentContentProvider, vscode.Disposable {
  private readonly contents = new Map<string, string>();
  private readonly changed = new vscode.EventEmitter<vscode.Uri>();

  readonly onDidChange = this.changed.event;

  setContent(uri: vscode.Uri, content: string): void {
    const key = uri.toString();
    this.contents.delete(key);
    this.contents.set(key, content);
    while (this.contents.size > 32) {
      const oldest = this.contents.keys().next().value as string | undefined;
      if (!oldest) {
        break;
      }
      this.contents.delete(oldest);
    }
    this.changed.fire(uri);
  }

  provideTextDocumentContent(uri: vscode.Uri): string {
    return this.contents.get(uri.toString()) ?? '';
  }

  dispose(): void {
    this.contents.clear();
    this.changed.dispose();
  }
}

const macroEmbeddedDocuments = new MacroEmbeddedDocumentContentProvider();

type ExecFileTextOptions = Omit<ExecFileOptions, 'encoding'>;

interface ExecFileTextResult {
  stdout: string;
  stderr: string;
}

interface ExecFileTextError extends Error {
  stdout?: string;
  stderr?: string;
}

interface ResolvedServerPath {
  path: string;
  source: string;
}

function execFileText(command: string, args: readonly string[], options: ExecFileTextOptions = {}): Promise<ExecFileTextResult> {
  return new Promise((resolve, reject) => {
    execFile(command, [...args], { ...options, encoding: 'buffer' }, (error, stdout, stderr) => {
      const result = {
        stdout: bufferToText(stdout),
        stderr: bufferToText(stderr)
      };

      if (error) {
        const execError = error as ExecFileTextError;
        execError.stdout = result.stdout;
        execError.stderr = result.stderr;
        reject(execError);
        return;
      }

      resolve(result);
    });
  });
}

function bufferToText(value: string | Buffer | Uint8Array | null | undefined): string {
  if (value === null || value === undefined) {
    return '';
  }

  return Buffer.isBuffer(value) || value instanceof Uint8Array
    ? Buffer.from(value).toString('utf8')
    : value;
}

function appendLifecycleLog(message: string): void {
  output.appendLine(`[lifecycle ${new Date().toISOString()}] ${message}`);
}

function toVsCodePosition(position: LspPosition): vscode.Position {
  return new vscode.Position(position.line, position.character);
}

function mapEmbeddedRangeToSource(
  range: vscode.Range,
  virtualDocument: vscode.TextDocument,
  sourceDocument: vscode.TextDocument,
  sourceStartOffset: number
): vscode.Range {
  const start = sourceDocument.positionAt(sourceStartOffset + virtualDocument.offsetAt(range.start));
  const end = sourceDocument.positionAt(sourceStartOffset + virtualDocument.offsetAt(range.end));
  return new vscode.Range(start, end);
}

function mapEmbeddedCompletionItemToSource(
  item: vscode.CompletionItem,
  virtualDocument: vscode.TextDocument,
  sourceDocument: vscode.TextDocument,
  sourceStartOffset: number
): vscode.CompletionItem {
  item.sortText = `90_${item.sortText ?? getCompletionLabel(item)}`;
  item.preselect = false;

  if (item.range instanceof vscode.Range) {
    item.range = mapEmbeddedRangeToSource(
      item.range,
      virtualDocument,
      sourceDocument,
      sourceStartOffset);
  } else if (item.range) {
    item.range = {
      inserting: mapEmbeddedRangeToSource(
        item.range.inserting,
        virtualDocument,
        sourceDocument,
        sourceStartOffset),
      replacing: mapEmbeddedRangeToSource(
        item.range.replacing,
        virtualDocument,
        sourceDocument,
        sourceStartOffset)
    };
  }

  if (item.additionalTextEdits) {
    item.additionalTextEdits = item.additionalTextEdits.map(edit => new vscode.TextEdit(
      mapEmbeddedRangeToSource(
        edit.range,
        virtualDocument,
        sourceDocument,
        sourceStartOffset),
      edit.newText));
  }

  return item;
}

function getCompletionLabel(item: vscode.CompletionItem): string {
  return typeof item.label === 'string' ? item.label : item.label.label;
}

async function openMacroEmbeddedDocument(
  document: vscode.TextDocument,
  position: vscode.Position,
  token: vscode.CancellationToken
): Promise<OpenMacroEmbeddedDocument | undefined> {
  if (!client || token.isCancellationRequested || document.languageId !== 'raven') {
    return undefined;
  }

  const requestedVersion = document.version;
  const projection = await client.sendRequest<MacroEmbeddedLanguageProjectionResponse | null>(
    macroEmbeddedLanguageProjectionMethod,
    {
      textDocument: { uri: document.uri.toString() },
      position: { line: position.line, character: position.character }
    },
    token);
  if (!projection || token.isCancellationRequested || document.version !== requestedVersion) {
    return undefined;
  }

  const sourceStartOffset = document.offsetAt(toVsCodePosition(projection.range.start));
  const sourceEndOffset = document.offsetAt(toVsCodePosition(projection.range.end));
  const sourcePositionOffset = document.offsetAt(position);
  if (sourcePositionOffset < sourceStartOffset || sourcePositionOffset > sourceEndOffset) {
    return undefined;
  }

  const languageId = projection.languageId.trim();
  if (!/^[a-z0-9][a-z0-9._-]*$/i.test(languageId)) {
    return undefined;
  }

  const identity = crypto.createHash('sha256')
    .update(document.uri.toString())
    .update('\0')
    .update(String(requestedVersion))
    .update('\0')
    .update(String(sourceStartOffset))
    .digest('hex')
    .slice(0, 20);
  const virtualUri = vscode.Uri.from({
    scheme: macroEmbeddedDocumentScheme,
    authority: 'macro',
    path: `/projection-${identity}.${languageId}`
  });
  macroEmbeddedDocuments.setContent(virtualUri, projection.text);

  let virtualDocument = await vscode.workspace.openTextDocument(virtualUri);
  if (virtualDocument.languageId !== languageId) {
    virtualDocument = await vscode.languages.setTextDocumentLanguage(virtualDocument, languageId);
  }
  if (token.isCancellationRequested || document.version !== requestedVersion) {
    return undefined;
  }

  return {
    virtualUri,
    virtualDocument,
    virtualPosition: virtualDocument.positionAt(sourcePositionOffset - sourceStartOffset),
    sourceStartOffset,
    requestedVersion
  };
}

async function provideMacroEmbeddedLanguageCompletions(
  document: vscode.TextDocument,
  position: vscode.Position,
  context: vscode.CompletionContext,
  token: vscode.CancellationToken
): Promise<vscode.CompletionList | undefined> {
  const embedded = await openMacroEmbeddedDocument(document, position, token);
  if (!embedded) {
    return undefined;
  }

  const triggerCharacter = context.triggerKind === vscode.CompletionTriggerKind.TriggerCharacter
    ? context.triggerCharacter
    : undefined;
  const completion = await vscode.commands.executeCommand<vscode.CompletionList>(
    'vscode.executeCompletionItemProvider',
    embedded.virtualUri,
    embedded.virtualPosition,
    triggerCharacter);
  if (!completion || token.isCancellationRequested || document.version !== embedded.requestedVersion) {
    return undefined;
  }

  return new vscode.CompletionList(
    completion.items.map(item => mapEmbeddedCompletionItemToSource(
      item,
      embedded.virtualDocument,
      document,
      embedded.sourceStartOffset)),
    completion.isIncomplete);
}

async function provideMacroEmbeddedLanguageHover(
  document: vscode.TextDocument,
  position: vscode.Position,
  token: vscode.CancellationToken
): Promise<vscode.Hover | undefined> {
  const embedded = await openMacroEmbeddedDocument(document, position, token);
  if (!embedded) {
    return undefined;
  }

  const hovers = await vscode.commands.executeCommand<vscode.Hover[]>(
    'vscode.executeHoverProvider',
    embedded.virtualUri,
    embedded.virtualPosition);
  if (!hovers?.length || token.isCancellationRequested || document.version !== embedded.requestedVersion) {
    return undefined;
  }

  const range = hovers
    .map(hover => hover.range)
    .find((candidate): candidate is vscode.Range => candidate !== undefined);
  const sourceRange = range
    ? mapEmbeddedRangeToSource(
        range,
        embedded.virtualDocument,
        document,
        embedded.sourceStartOffset)
    : undefined;
  return new vscode.Hover(
    hovers.flatMap(hover => hover.contents),
    sourceRange);
}

function formatClientState(state: State): string {
  switch (state) {
    case State.Starting:
      return 'Starting';
    case State.Running:
      return 'Running';
    case State.Stopped:
      return 'Stopped';
    default:
      return `Unknown(${state})`;
  }
}

function logStateChange(event: StateChangeEvent): void {
  appendLifecycleLog(`Language client state changed: ${formatClientState(event.oldState)} -> ${formatClientState(event.newState)}`);
}

function formatRequestType(type: string | { method?: string }): string {
  if (typeof type === 'string') {
    return type;
  }

  if (type && typeof type === 'object' && typeof type.method === 'string') {
    return type.method;
  }

  return '<unknown>';
}

function formatRequestTarget(param: unknown): string {
  if (!param || typeof param !== 'object') {
    return '';
  }

  const candidate = param as {
    textDocument?: { uri?: string };
    uri?: string;
    position?: { line?: number; character?: number };
    range?: { start?: { line?: number; character?: number }; end?: { line?: number; character?: number } };
  };
  const uri = candidate.textDocument?.uri ?? candidate.uri;
  if (!uri) {
    return '';
  }

  const position = candidate.position;
  if (position?.line !== undefined && position.character !== undefined) {
    return ` ${uri} ${position.line}:${position.character}`;
  }

  const range = candidate.range;
  if (range?.start?.line !== undefined &&
      range.start.character !== undefined &&
      range.end?.line !== undefined &&
      range.end.character !== undefined) {
    return ` ${uri} ${range.start.line}:${range.start.character}-${range.end.line}:${range.end.character}`;
  }

  return ` ${uri}`;
}

function formatRequestResult(method: string, result: unknown): string {
  if (method === 'textDocument/completion') {
    if (Array.isArray(result)) {
      return ` items=${result.length}`;
    }

    if (result && typeof result === 'object') {
      const candidate = result as { items?: unknown[] };
      if (Array.isArray(candidate.items)) {
        return ` items=${candidate.items.length}`;
      }
    }
  }

  if (method === 'textDocument/codeAction' && Array.isArray(result)) {
    return ` actions=${result.length}`;
  }

  return '';
}

function areInferredTypeInlayHintsEnabled(): boolean {
  return vscode.workspace
    .getConfiguration('raven')
    .get<boolean>('inlayHints.inferredTypes.enabled', true);
}

function areRavenInlayHintsEnabled(): boolean {
  return vscode.workspace
    .getConfiguration('raven')
    .get<boolean>('inlayHints.enabled', true);
}

function areNameInlayHintsEnabled(): boolean {
  return vscode.workspace
    .getConfiguration('raven')
    .get<boolean>('inlayHints.names.enabled', true);
}

function getInlayHintRequestDebounceMilliseconds(): number {
  const configured = vscode.workspace
    .getConfiguration('raven')
    .get<number>('inlayHints.requestDebounceMilliseconds', 250);

  if (!Number.isFinite(configured)) {
    return 250;
  }

  return Math.max(0, Math.min(2000, Math.trunc(configured)));
}

function bumpInlayHintRequestVersion(key: string): number {
  const requestVersion = (inlayHintRequestVersions.get(key) ?? 0) + 1;
  inlayHintRequestVersions.set(key, requestVersion);
  return requestVersion;
}

function isCurrentInlayHintRequest(key: string, requestVersion: number): boolean {
  return inlayHintRequestVersions.get(key) === requestVersion;
}

function countVisibleRavenDocuments(): number {
  let count = 0;
  for (const editor of vscode.window.visibleTextEditors) {
    if (editor.document.languageId !== 'raven') {
      continue;
    }

    count++;
  }

  return count;
}

function areSemanticTokensEnabled(): boolean {
  return vscode.workspace
    .getConfiguration('raven')
    .get<boolean>('semanticTokens.enabled', true);
}

function isSemanticTokensRequest(method: string): boolean {
  return method === 'textDocument/semanticTokens/full' ||
    method === 'textDocument/semanticTokens/range';
}

function delay(milliseconds: number): Promise<void> {
  if (milliseconds <= 0) {
    return Promise.resolve();
  }

  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

async function refreshInlayHints(): Promise<void> {
  inlayHintRefreshEpoch++;

  if (!inlayHintRefreshPromise) {
    inlayHintRefreshPromise = runInlayHintRefreshLoop()
      .finally(() => {
        inlayHintRefreshPromise = undefined;
      });
  }

  return inlayHintRefreshPromise;
}

async function runInlayHintRefreshLoop(): Promise<void> {
  let completedEpoch = 0;
  do {
    const refreshEpoch = inlayHintRefreshEpoch;
    await delay(50);

    if (refreshEpoch !== inlayHintRefreshEpoch) {
      appendLifecycleLog(`Inlay hint refresh epoch ${refreshEpoch} superseded before provider invalidation.`);
      continue;
    }

    pulseVisibleInlayHintProviders(refreshEpoch);
    completedEpoch = refreshEpoch;
  } while (completedEpoch !== inlayHintRefreshEpoch);
}

function pulseVisibleInlayHintProviders(refreshEpoch: number): void {
  const visibleDocumentCount = countVisibleRavenDocuments();
  const providerCount = fireVisibleInlayHintProviders();
  appendLifecycleLog(`Inlay hint refresh pulse completed: epoch=${refreshEpoch} visibleDocuments=${visibleDocumentCount} providers=${providerCount}.`);
}

function fireVisibleInlayHintProviders(): number {
  const activeClient = client;
  if (!activeClient || activeClient.state !== State.Running) {
    return 0;
  }

  let providerCount = 0;
  const seen = new Set<InlayHintsProviderShape>();
  try {
    const feature = activeClient.getFeature(InlayHintRequest.method);
    for (const editor of vscode.window.visibleTextEditors) {
      if (editor.document.languageId !== 'raven') {
        continue;
      }

      const provider = feature.getProvider(editor.document);
      if (!provider || seen.has(provider)) {
        continue;
      }

      seen.add(provider);
      provider.onDidChangeInlayHints.fire();
      providerCount++;
    }
  } catch (error) {
    const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
    appendLifecycleLog(`Unable to invalidate inlay hint providers: ${message}`);
    return 0;
  }

  if (providerCount > 0) {
    appendLifecycleLog(`Invalidated ${providerCount} visible inlay hint provider(s).`);
  }

  return providerCount;
}

function scheduleInlayHintRefresh(): void {
  if (pendingInlayHintRefresh) {
    clearTimeout(pendingInlayHintRefresh);
  }

  pendingInlayHintRefresh = setTimeout(() => {
    pendingInlayHintRefresh = undefined;
    void refreshInlayHints();
  }, getInlayHintRequestDebounceMilliseconds());
}

function shouldTriggerImportCompletionAfterQuietPeriod(event: vscode.TextDocumentChangeEvent): boolean {
  if (event.contentChanges.length === 0) {
    return false;
  }

  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.document.uri.toString() !== event.document.uri.toString()) {
    return false;
  }

  return event.contentChanges.some(change => {
    const lineNumber = Math.min(change.range.start.line, event.document.lineCount - 1);
    const line = event.document.lineAt(lineNumber).text;
    return /^\s*import\s+[\w.]*\.\s*$/.test(line);
  });
}

function scheduleImportCompletionTrigger(document: vscode.TextDocument): void {
  if (pendingImportCompletionTrigger) {
    clearTimeout(pendingImportCompletionTrigger);
  }

  const uri = document.uri.toString();
  const version = document.version;

  pendingImportCompletionTrigger = setTimeout(() => {
    pendingImportCompletionTrigger = undefined;

    const editor = vscode.window.activeTextEditor;
    if (!editor ||
        editor.document.uri.toString() !== uri ||
        editor.document.version !== version ||
        editor.document.languageId !== 'raven') {
      return;
    }

    const cursor = editor.selection.active;
    const line = editor.document.lineAt(cursor.line).text;
    const prefix = line.slice(0, cursor.character);
    if (!/^\s*import\s+[\w.]*\.$/.test(prefix)) {
      return;
    }

    appendLifecycleLog(`Triggering import completion after document quiet period at ${uri} ${cursor.line}:${cursor.character}.`);
    void vscode.commands.executeCommand('editor.action.triggerSuggest');
  }, 125);
}

async function stopClient(reason: string): Promise<void> {
  const activeClient = client;
  if (!activeClient) {
    appendLifecycleLog(`stopClient(${reason}) skipped: no active client.`);
    return;
  }

  if (clientStopPromise) {
    appendLifecycleLog(`stopClient(${reason}) joined existing stop operation.`);
    return clientStopPromise;
  }

  const startedAt = Date.now();
  appendLifecycleLog(`stopClient(${reason}) started.`);

  clientStopPromise = activeClient.stop().then(
    () => {
      appendLifecycleLog(`stopClient(${reason}) completed in ${Date.now() - startedAt}ms.`);
    },
    error => {
      const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
      appendLifecycleLog(`stopClient(${reason}) failed after ${Date.now() - startedAt}ms: ${message}`);
      throw error;
    }
  ).finally(() => {
    if (client === activeClient) {
      client = undefined;
    }

    clientStopPromise = undefined;
  });

  return clientStopPromise;
}

function resolveLanguageServerProjectPath(): string | undefined {
  const searchRoots = new Set<string>();
  for (const folder of vscode.workspace.workspaceFolders ?? []) {
    searchRoots.add(folder.uri.fsPath);
  }
  if (extensionInstallPath.length > 0) {
    searchRoots.add(extensionInstallPath);
  }
  searchRoots.add(process.cwd());

  for (const root of searchRoots) {
    for (const dir of enumerateAncestorDirectories(root)) {
      const candidate = path.join(dir, 'src', 'Raven.LanguageServer', 'Raven.LanguageServer.csproj');
      if (fs.existsSync(candidate)) {
        return candidate;
      }
    }
  }

  return undefined;
}

async function ensureLanguageServerBuilt(): Promise<void> {
  const configuration = vscode.workspace.getConfiguration('raven');
  if (!configuration.get<boolean>('autoBuildLanguageServerOnActivate', false)) {
    return;
  }

  if (configuration.get<string>('languageServerPath')?.trim()) {
    return;
  }

  const projectPath = resolveLanguageServerProjectPath();
  if (!projectPath) {
    return;
  }

  if (languageServerBuildPromise) {
    return languageServerBuildPromise;
  }

  languageServerBuildPromise = (async () => {
    const projectDirectory = path.dirname(projectPath);
    const args = ['build', projectPath, '/property:WarningLevel=0'];
    appendLifecycleLog(`Building language server: dotnet ${args.join(' ')}`);

    try {
      const { stdout, stderr } = await execFileText('dotnet', args, {
        cwd: projectDirectory,
        maxBuffer: 10 * 1024 * 1024
      });

      if (stdout.trim().length > 0) {
        output.appendLine(stdout);
      }
      if (stderr.trim().length > 0) {
        output.appendLine(stderr);
      }

      appendLifecycleLog('Language server build completed.');
    } catch (error) {
      const e = error as Error & { stdout?: string; stderr?: string };
      if (e.stdout) output.appendLine(e.stdout);
      if (e.stderr) output.appendLine(e.stderr);

      const message = `Failed to build Raven language server before activation. ${e.message}`;
      appendLifecycleLog(message);
      void vscode.window.showErrorMessage(message);
      throw new Error(message);
    }
  })().finally(() => {
    languageServerBuildPromise = undefined;
  });

  return languageServerBuildPromise;
}

function createLanguageClient(context: vscode.ExtensionContext): LanguageClient {
  let serverResolution: ResolvedServerPath;
  try {
    serverResolution = resolveServerPath(context, output);
    activeServerResolution = serverResolution;
  } catch (e) {
    const message = e instanceof Error ? e.message : String(e);
    output.appendLine(message);
    output.show(true);
    throw new Error(`Raven: ${message}`);
  }

  let isolatedServerPath: string;
  try {
    isolatedServerPath = stageServerForIsolatedLaunch(context, serverResolution.path);
  } catch (e) {
    const message = e instanceof Error ? e.message : String(e);
    output.appendLine(`Failed to stage isolated language server: ${message}`);
    output.show(true);
    throw new Error(`Raven: Failed to stage isolated language server: ${message}`);
  }

  output.appendLine(`Using language server (${serverResolution.source}): ${serverResolution.path}`);
  output.appendLine(`Using isolated language server: ${isolatedServerPath}`);
  const languageServerWorkingDirectory = tryFindRepositoryRoot(serverResolution.path) ?? path.dirname(isolatedServerPath);
  output.appendLine(`Using language server working directory: ${languageServerWorkingDirectory}`);
  appendToolchainReport(context);

  const runCommand = {
    command: 'dotnet',
    args: [isolatedServerPath],
    options: {
      cwd: languageServerWorkingDirectory
    }
  };

  const serverOptions: ServerOptions = {
    run: runCommand,
    debug: runCommand
  };

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: 'file', language: 'raven' }, { scheme: 'raven-generated', language: 'raven' }],
    synchronize: {
      configurationSection: 'raven',
      fileEvents: vscode.workspace.createFileSystemWatcher('**/*.{rvn,rav,rvnproj,csproj,fsproj}')
    },
    outputChannel: output,
    traceOutputChannel: output,
    errorHandler: {
      error(error, message, count) {
        const errorMessage = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
        const messageSummary = message ? JSON.stringify(message) : '<none>';
        appendLifecycleLog(
          `Language client transport error: error=${errorMessage} message=${messageSummary} count=${count ?? '<none>'}`
        );
        return { action: ErrorAction.Continue };
      },
      closed() {
        appendLifecycleLog('Language client transport closed. Requesting restart.');
        return { action: CloseAction.Restart };
      }
    },
    middleware: {
      async provideHover(document, position, token, next) {
        const embeddedPromise = provideMacroEmbeddedLanguageHover(
          document,
          position,
          token).catch(error => {
            if (!token.isCancellationRequested) {
              const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
              appendLifecycleLog(`Embedded-language hover failed: ${message}`);
            }
            return undefined;
          });
        const [ravenHover, embeddedHover] = await Promise.all([
          Promise.resolve(next(document, position, token)),
          embeddedPromise
        ]);
        if (!embeddedHover || token.isCancellationRequested) {
          return ravenHover;
        }
        if (!ravenHover) {
          return embeddedHover;
        }

        return new vscode.Hover(
          [...ravenHover.contents, ...embeddedHover.contents],
          ravenHover.range ?? embeddedHover.range);
      },
      async provideCompletionItem(document, position, completionContext, token, next) {
        const embeddedPromise = provideMacroEmbeddedLanguageCompletions(
          document,
          position,
          completionContext,
          token).catch(error => {
            if (!token.isCancellationRequested) {
              const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
              appendLifecycleLog(`Embedded-language completion failed: ${message}`);
            }
            return undefined;
          });
        const [ravenCompletion, embeddedCompletion] = await Promise.all([
          Promise.resolve(next(document, position, completionContext, token)),
          embeddedPromise
        ]);
        if (!embeddedCompletion || token.isCancellationRequested) {
          return ravenCompletion;
        }

        const ravenItems = ravenCompletion instanceof vscode.CompletionList
          ? ravenCompletion.items
          : ravenCompletion ?? [];
        const labels = new Set(ravenItems.map(item => getCompletionLabel(item).toLowerCase()));
        const mergedItems = [...ravenItems];
        for (const item of embeddedCompletion.items) {
          const label = getCompletionLabel(item).toLowerCase();
          if (labels.has(label)) {
            continue;
          }
          labels.add(label);
          mergedItems.push(item);
        }

        const ravenIncomplete = ravenCompletion instanceof vscode.CompletionList
          ? ravenCompletion.isIncomplete
          : false;
        return new vscode.CompletionList(
          mergedItems,
          ravenIncomplete || embeddedCompletion.isIncomplete);
      },
      async sendRequest(type, param, token, next) {
        const method = formatRequestType(type);
        const interesting =
          method === 'textDocument/hover' ||
          method === 'textDocument/completion' ||
          method === 'textDocument/inlayHint' ||
          method === 'textDocument/semanticTokens/full' ||
          method === 'textDocument/semanticTokens/range' ||
          method === 'textDocument/documentSymbol' ||
          method === 'textDocument/documentDiagnostic' ||
          method === 'textDocument/codeAction' ||
          method === 'workspace/diagnostic';

        const startedAt = Date.now();
        const target = interesting ? formatRequestTarget(param) : '';
        if (interesting) {
          appendLifecycleLog(`Request started: ${method}${target}`);
        }

        if (isSemanticTokensRequest(method) && !areSemanticTokensEnabled()) {
          appendLifecycleLog(`Request completed: ${method}${target} in 0ms. semantic tokens disabled by raven.semanticTokens.enabled`);
          return { data: [] } as Awaited<ReturnType<typeof next>>;
        }

        try {
          const result = await next(type, param, token);
          if (interesting) {
            appendLifecycleLog(`Request completed: ${method}${target} in ${Date.now() - startedAt}ms.${formatRequestResult(method, result)}`);
          }

          return result;
        } catch (error) {
          const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
          if (interesting) {
            appendLifecycleLog(`Request failed: ${method}${target} after ${Date.now() - startedAt}ms: ${message}`);
          }

          throw error;
        }
      },
      async sendNotification(type, next, params) {
        const method = formatRequestType(type);
        const interesting =
          method === 'textDocument/didOpen' ||
          method === 'textDocument/didChange' ||
          method === 'textDocument/didSave' ||
          method === 'textDocument/didClose';

        if (interesting) {
          appendLifecycleLog(`Notification sent: ${method}${formatRequestTarget(params)}`);
        }

        return next(type, params);
      },
      async provideInlayHints(document, viewPort, token, next) {
        if (document.languageId === 'raven') {
          if (!areRavenInlayHintsEnabled()) {
            return [];
          }

          const showInferredTypes = areInferredTypeInlayHintsEnabled();
          const showNames = areNameInlayHintsEnabled();
          if (!showInferredTypes && !showNames) {
            return [];
          }

          const key = document.uri.toString();
          const requestVersion = bumpInlayHintRequestVersion(key);

          if (!isCurrentInlayHintRequest(key, requestVersion)) {
            throw new vscode.CancellationError();
          }

          const hints = await next(document, viewPort, token);
          if (!isCurrentInlayHintRequest(key, requestVersion)) {
            throw new vscode.CancellationError();
          }

          if (!hints) {
            return hints;
          }

          if (showInferredTypes && showNames) {
            return hints;
          }

          return hints.filter(hint => {
            if (hint.kind === vscode.InlayHintKind.Type) {
              return showInferredTypes;
            }

            if (hint.kind === vscode.InlayHintKind.Parameter) {
              return showNames;
            }

            return true;
          });
        }

        return next(document, viewPort, token);
      }
    }
  };

  const createdClient = new LanguageClient(
    'ravenLanguageServer',
    'Raven Language Server',
    serverOptions,
    clientOptions
  );
  createdClient.onDidChangeState(logStateChange);
  createdClient.setTrace(Trace.Verbose);
  appendLifecycleLog('Language client trace level set to Verbose.');
  return createdClient;
}

async function startClient(context: vscode.ExtensionContext, reason: string): Promise<void> {
  if (clientStartPromise) {
    appendLifecycleLog(`startClient(${reason}) joined existing start operation.`);
    return clientStartPromise;
  }

  clientStartPromise = (async () => {
    if (client) {
      appendLifecycleLog(`startClient(${reason}) skipped: client already active.`);
      return;
    }

    await ensureLanguageServerBuilt();

    try {
      client = createLanguageClient(context);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      void vscode.window.showErrorMessage(message);
      throw error;
    }

    appendLifecycleLog(`Starting language client (${reason}).`);
    await client.start();
    client.onRequest(InlayHintRefreshRequest.type, async () => {
      appendLifecycleLog('Received workspace/inlayHint/refresh from language server.');
      await refreshInlayHints();
    });
  })().finally(() => {
    clientStartPromise = undefined;
  });

  return clientStartPromise;
}

async function restartClient(context: vscode.ExtensionContext, reason: string): Promise<void> {
  appendLifecycleLog(`restartClient(${reason}) requested.`);
  await stopClient(`restart:${reason}`);
  await startClient(context, `restart:${reason}`);
}

class RavenDocumentationContentProvider implements vscode.TextDocumentContentProvider {
  provideTextDocumentContent(uri: vscode.Uri): string {
    const params = new URLSearchParams(uri.query);
    const label = params.get('label')?.trim() || 'Documentation';
    const target = params.get('target')?.trim() || '';

    const lines = [
      `# ${label}`,
      '',
      'This editor link targets the following symbol reference:',
      '',
      target.length > 0 ? `- Symbol: \`${target}\`` : '- Symbol: unavailable',
      '',
      'Full symbol-page resolution will be provided by the dedicated Raven documentation view.'
    ];

    return `${lines.join('\n')}\n`;
  }
}

function parseDocumentationUriArgument(value: unknown): vscode.Uri | undefined {
  if (value instanceof vscode.Uri) {
    return value;
  }

  if (typeof value === 'string' && value.trim().length > 0) {
    try {
      return vscode.Uri.parse(value);
    } catch {
      return undefined;
    }
  }

  if (value && typeof value === 'object' && 'scheme' in value) {
    try {
      return vscode.Uri.from(value as { scheme: string; authority?: string; path?: string; query?: string; fragment?: string });
    } catch {
      return undefined;
    }
  }

  return undefined;
}

function resolveExplicitSdkPath(): string | undefined {
  const configuration = vscode.workspace.getConfiguration('raven');
  const configuredPath = configuration.get<string>('sdkPath')?.trim()
    || process.env.RAVEN_SDK_ROOT?.trim();
  if (!configuredPath) {
    return undefined;
  }

  const absolutePath = path.isAbsolute(configuredPath)
    ? configuredPath
    : path.resolve(vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? extensionInstallPath, configuredPath);

  return fs.existsSync(absolutePath) ? absolutePath : undefined;
}

function resolveConfiguredSdkPath(): string | undefined {
  const explicitPath = resolveExplicitSdkPath();
  if (explicitPath) {
    return explicitPath;
  }

  try {
    const discoveredPath = execFileSync('rvn', ['sdk', 'path'], {
      encoding: 'utf8',
      timeout: 5000,
      windowsHide: true
    }).trim();

    if (discoveredPath && fs.existsSync(discoveredPath)) {
      return discoveredPath;
    }
  } catch {
    // The SDK is optional for syntax-only extension use with a bundled server.
  }

  return undefined;
}

function getExtensionModeName(mode: vscode.ExtensionMode): string {
  switch (mode) {
    case vscode.ExtensionMode.Development:
      return 'repository development host';
    case vscode.ExtensionMode.Test:
      return 'extension test host';
    default:
      return 'installed extension';
  }
}

function tryReadSdkVersion(sdkPath: string): string {
  try {
    const version = fs.readFileSync(path.join(sdkPath, 'VERSION'), 'utf8').trim();
    if (version.length > 0) {
      return version;
    }
  } catch {
    // Older or custom SDK layouts may not carry VERSION.
  }

  return path.basename(sdkPath);
}

function findNearestGlobalJson(startPath: string): string | undefined {
  for (const directory of enumerateAncestorDirectories(startPath)) {
    const candidate = path.join(directory, 'global.json');
    if (fs.existsSync(candidate)) {
      return candidate;
    }
  }

  return undefined;
}

function tryReadProjectSdkSelection(globalJsonPath: string): string | undefined {
  try {
    const content = JSON.parse(fs.readFileSync(globalJsonPath, 'utf8')) as {
      'msbuild-sdks'?: Record<string, unknown>;
    };
    const selectedVersion = content['msbuild-sdks']?.['Raven.Sdk'];
    return typeof selectedVersion === 'string' && selectedVersion.trim().length > 0
      ? selectedVersion.trim()
      : undefined;
  } catch {
    return undefined;
  }
}

function appendToolchainReport(context: vscode.ExtensionContext): void {
  const extensionVersion = String(context.extension.packageJSON.version ?? '<unknown>');
  output.appendLine('Raven toolchain provenance:');
  output.appendLine(`- Extension: ${extensionVersion} (${getExtensionModeName(context.extensionMode)})`);
  output.appendLine(`- Extension path: ${context.extensionPath}`);

  if (activeServerResolution) {
    output.appendLine(`- Language server source: ${activeServerResolution.source}`);
    output.appendLine(`- Language server path: ${activeServerResolution.path}`);
  } else {
    output.appendLine('- Language server: not resolved yet');
  }

  const sdkPath = resolveConfiguredSdkPath();
  if (sdkPath) {
    output.appendLine(`- Installed SDK: ${tryReadSdkVersion(sdkPath)}`);
    output.appendLine(`- Installed SDK path: ${sdkPath}`);
  } else {
    output.appendLine('- Installed SDK: not found');
  }

  const workspaceFolders = vscode.workspace.workspaceFolders ?? [];
  if (workspaceFolders.length === 0) {
    output.appendLine('- Project SDK selection: no workspace folder');
  }

  for (const workspaceFolder of workspaceFolders) {
    const globalJsonPath = findNearestGlobalJson(workspaceFolder.uri.fsPath);
    const selectedVersion = globalJsonPath
      ? tryReadProjectSdkSelection(globalJsonPath)
      : undefined;
    output.appendLine(
      selectedVersion
        ? `- Project SDK (${workspaceFolder.name}): ${selectedVersion} via ${globalJsonPath}`
        : `- Project SDK (${workspaceFolder.name}): no Raven.Sdk selection in nearest global.json`
    );
  }
}

async function offerSdkInstallationIfMissing(context: vscode.ExtensionContext): Promise<void> {
  if (resolveConfiguredSdkPath() || context.globalState.get<boolean>(sdkInstallPromptDismissedKey, false)) {
    return;
  }

  const installAction = 'View Installation Instructions';
  const dismissAction = "Don't Show Again";
  const selectedAction = await vscode.window.showWarningMessage(
    'The Raven SDK was not found. Editor features use the bundled language server, but build, run, and debug commands require the SDK.',
    installAction,
    dismissAction
  );

  if (selectedAction === installAction) {
    await vscode.env.openExternal(vscode.Uri.parse(sdkInstallationDocumentationUrl));
  } else if (selectedAction === dismissAction) {
    await context.globalState.update(sdkInstallPromptDismissedKey, true);
  }
}

function resolveServerPath(context: vscode.ExtensionContext, output: vscode.OutputChannel): ResolvedServerPath {
  const configuration = vscode.workspace.getConfiguration('raven');
  const settingPath = configuration.get<string>('languageServerPath')?.trim();
  const environmentPath = process.env.RAVEN_LANGUAGE_SERVER_PATH?.trim();
  const configuredPath = settingPath || environmentPath;

  const attempts: string[] = [];

  if (configuredPath) {
    attempts.push(configuredPath);
    if (fs.existsSync(configuredPath)) {
      return {
        path: configuredPath,
        source: settingPath
          ? 'explicit raven.languageServerPath'
          : 'RAVEN_LANGUAGE_SERVER_PATH environment'
      };
    }
  }

  const sdkPath = resolveExplicitSdkPath();
  if (sdkPath) {
    const sdkCandidates = [
      path.join(sdkPath, 'Raven.LanguageServer.dll'),
      path.join(sdkPath, 'tools', 'language-server', 'Raven.LanguageServer.dll'),
      path.join(sdkPath, 'server', 'Raven.LanguageServer.dll'),
      path.join(sdkPath, 'net10.0', 'Raven.LanguageServer.dll'),
      path.join(sdkPath, 'net11.0', 'Raven.LanguageServer.dll')
    ];

    for (const candidate of sdkCandidates) {
      attempts.push(candidate);
      if (fs.existsSync(candidate)) {
        return { path: candidate, source: 'explicit raven.sdkPath' };
      }
    }
  }

  // 1) Dev/workspace copy next to the extension folder.
  // Prefer this over the packaged server so local compiler/language-server
  // changes are reflected in diagnostics during development.
  // <repo>/src/Raven.LanguageServer/bin/{Debug|Release}/{tfm}/Raven.LanguageServer.dll
  const repoCandidateRoots = [
    path.join(context.extensionPath, '..', 'Raven.LanguageServer', 'bin'),
    path.join(context.extensionPath, '..', '..', 'Raven.LanguageServer', 'bin')
  ];

  const configurations = ['Debug', 'Release'];
  const tfms = ['net11.0', 'net10.0', 'net8.0', 'net7.0'];

  for (const root of repoCandidateRoots) {
    for (const cfg of configurations) {
      for (const tfm of tfms) {
        const candidate = path.join(root, cfg, tfm, 'Raven.LanguageServer.dll');
        attempts.push(candidate);
        if (fs.existsSync(candidate)) {
          return { path: candidate, source: 'repository build' };
        }
      }
    }
  }

  // 2) Packaged copy: <extension>/server/Raven.LanguageServer.dll
  const packagedPath = context.asAbsolutePath(path.join('server', 'Raven.LanguageServer.dll'));
  attempts.push(packagedPath);
  if (fs.existsSync(packagedPath)) {
    return { path: packagedPath, source: 'installed extension bundle' };
  }

  // An automatically discovered SDK can be older than the installed extension.
  // Use its server only as a final fallback when this extension has no matching
  // packaged or workspace-built server. An explicit raven.sdkPath still takes
  // precedence above for users intentionally selecting an SDK toolchain.
  const discoveredSdkPath = resolveConfiguredSdkPath();
  if (discoveredSdkPath) {
    const discoveredSdkCandidates = [
      path.join(discoveredSdkPath, 'Raven.LanguageServer.dll'),
      path.join(discoveredSdkPath, 'tools', 'language-server', 'Raven.LanguageServer.dll'),
      path.join(discoveredSdkPath, 'server', 'Raven.LanguageServer.dll'),
      path.join(discoveredSdkPath, 'net10.0', 'Raven.LanguageServer.dll'),
      path.join(discoveredSdkPath, 'net11.0', 'Raven.LanguageServer.dll')
    ];

    for (const candidate of discoveredSdkCandidates) {
      attempts.push(candidate);
      if (fs.existsSync(candidate)) {
        output.appendLine(`Bundled language server unavailable; falling back to discovered SDK server: ${candidate}`);
        return { path: candidate, source: 'auto-discovered installed SDK fallback' };
      }
    }
  }

  output.appendLine('Failed to locate Raven.LanguageServer.dll. Tried:');
  for (const p of attempts) output.appendLine(`- ${p}`);

  throw new Error(
    'Unable to locate Raven.LanguageServer.dll. Build the language server or set "raven.languageServerPath" to the compiled DLL.'
  );
}

function createStableHash(input: string): string {
  return crypto.createHash('sha256').update(input).digest('hex').slice(0, 12);
}

function createDirectoryFingerprint(directoryPath: string): string {
  const entries: string[] = [];

  function visit(currentDirectory: string): void {
    for (const entry of fs.readdirSync(currentDirectory, { withFileTypes: true })) {
      const fullPath = path.join(currentDirectory, entry.name);
      const relativePath = path.relative(directoryPath, fullPath).split(path.sep).join('/');

      if (entry.isDirectory()) {
        visit(fullPath);
        continue;
      }

      if (!entry.isFile()) {
        continue;
      }

      const stat = fs.statSync(fullPath);
      entries.push(`${relativePath}|${stat.mtimeMs}|${stat.size}`);
    }
  }

  visit(directoryPath);
  entries.sort();
  return createStableHash(entries.join('\n'));
}

function stageServerForIsolatedLaunch(context: vscode.ExtensionContext, sourceServerPath: string): string {
  const sourceDirectory = path.dirname(sourceServerPath);
  const fingerprint = createStableHash(`${sourceDirectory}|${createDirectoryFingerprint(sourceDirectory)}`);
  const stagingRoot = path.join(context.globalStorageUri.fsPath, 'language-server');
  const targetDirectory = path.join(stagingRoot, fingerprint);
  const targetServerPath = path.join(targetDirectory, path.basename(sourceServerPath));

  fs.mkdirSync(stagingRoot, { recursive: true });

  if (!fs.existsSync(targetServerPath)) {
    fs.rmSync(targetDirectory, { recursive: true, force: true });
    fs.mkdirSync(targetDirectory, { recursive: true });
    fs.cpSync(sourceDirectory, targetDirectory, { recursive: true });
  }

  return targetServerPath;
}

function tryFindRepositoryRoot(startPath: string): string | undefined {
  let current = fs.statSync(startPath).isDirectory()
    ? path.resolve(startPath)
    : path.dirname(path.resolve(startPath));

  while (true) {
    if (fs.existsSync(path.join(current, 'Raven.sln'))) {
      return current;
    }

    const parent = path.dirname(current);
    if (parent === current) {
      return undefined;
    }

    current = parent;
  }
}

function resolveCompilerProjectPath(): string | undefined {
  const configuration = vscode.workspace.getConfiguration('raven');
  const configuredPath = configuration.get<string>('compilerProjectPath')?.trim();
  if (configuredPath) {
    const absolutePath = path.isAbsolute(configuredPath)
      ? configuredPath
      : path.resolve(vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '', configuredPath);
    return fs.existsSync(absolutePath) ? absolutePath : undefined;
  }

  const searchRoots = new Set<string>();
  for (const folder of vscode.workspace.workspaceFolders ?? []) {
    searchRoots.add(folder.uri.fsPath);
  }
  if (extensionInstallPath.length > 0) {
    searchRoots.add(extensionInstallPath);
  }
  searchRoots.add(process.cwd());

  for (const root of searchRoots) {
    for (const dir of enumerateAncestorDirectories(root)) {
      const candidate = path.join(dir, 'src', 'Raven.Compiler', 'Raven.Compiler.csproj');
      if (fs.existsSync(candidate)) {
        return candidate;
      }
    }
  }

  return undefined;
}

function resolveFrontendProjectPath(): string | undefined {
  const searchRoots = new Set<string>();
  for (const folder of vscode.workspace.workspaceFolders ?? []) {
    searchRoots.add(folder.uri.fsPath);
  }
  if (extensionInstallPath.length > 0) {
    searchRoots.add(extensionInstallPath);
  }

  const compilerProjectPath = resolveCompilerProjectPath();
  if (compilerProjectPath) {
    searchRoots.add(path.dirname(compilerProjectPath));
  }

  searchRoots.add(process.cwd());

  for (const root of searchRoots) {
    for (const dir of enumerateAncestorDirectories(root)) {
      const candidate = path.join(dir, 'src', 'Raven', 'Raven.csproj');
      if (fs.existsSync(candidate)) {
        return candidate;
      }
    }
  }

  return undefined;
}

type ToolInvocation = {
  executable: string;
  args: string[];
  description: string;
};

function resolveCompilerInvocation(targetFramework: string | undefined): ToolInvocation | undefined {
  const sdkPath = resolveConfiguredSdkPath();
  const bundledRoots = extensionInstallPath.length > 0
    ? [
        path.join(extensionInstallPath, 'compiler'),
        path.join(extensionInstallPath, 'server')
      ]
    : [];
  const preferredTfms = targetFramework
    ? [targetFramework, 'net10.0', 'net11.0']
    : ['net10.0', 'net11.0'];

  if (sdkPath) {
    const sdkRoots = [
      sdkPath,
      path.join(sdkPath, 'tools', 'rvnc'),
      path.join(sdkPath, 'compiler'),
      path.join(sdkPath, 'server')
    ];

    for (const root of sdkRoots) {
      for (const tfm of preferredTfms) {
        const tfmCandidate = path.join(root, tfm, 'rvnc.dll');
        if (fs.existsSync(tfmCandidate)) {
          return {
            executable: 'dotnet',
            args: [tfmCandidate],
            description: tfmCandidate
          };
        }
      }

      const flatCandidate = path.join(root, 'rvnc.dll');
      if (fs.existsSync(flatCandidate)) {
        return {
          executable: 'dotnet',
          args: [flatCandidate],
          description: flatCandidate
        };
      }
    }
  }

  for (const root of bundledRoots) {
    for (const tfm of preferredTfms) {
      const tfmCandidate = path.join(root, tfm, 'rvnc.dll');
      if (fs.existsSync(tfmCandidate)) {
        return {
          executable: 'dotnet',
          args: [tfmCandidate],
          description: tfmCandidate
        };
      }
    }

    const flatCandidate = path.join(root, 'rvnc.dll');
    if (fs.existsSync(flatCandidate)) {
      return {
        executable: 'dotnet',
        args: [flatCandidate],
        description: flatCandidate
      };
    }
  }

  const compilerProjectPath = resolveCompilerProjectPath();
  if (!compilerProjectPath) {
    return undefined;
  }

  const compilerDirectory = path.dirname(compilerProjectPath);

  for (const tfm of preferredTfms) {
    const candidate = path.join(compilerDirectory, 'bin', 'Debug', tfm, 'rvnc.dll');
    if (fs.existsSync(candidate)) {
      return {
        executable: 'dotnet',
        args: [candidate],
        description: candidate
      };
    }
  }

  return undefined;
}

function resolveFrontendInvocation(targetFramework: string | undefined): ToolInvocation | undefined {
  const sdkPath = resolveConfiguredSdkPath();
  const bundledRoots = extensionInstallPath.length > 0
    ? [
        path.join(extensionInstallPath, 'tools'),
        path.join(extensionInstallPath, 'compiler'),
        path.join(extensionInstallPath, 'server')
      ]
    : [];
  const preferredTfms = targetFramework
    ? [targetFramework, 'net10.0', 'net11.0']
    : ['net10.0', 'net11.0'];

  if (sdkPath) {
    const sdkRoots = [
      sdkPath,
      path.join(sdkPath, 'tools', 'rvn'),
      path.join(sdkPath, 'tools'),
      path.join(sdkPath, 'compiler'),
      path.join(sdkPath, 'server')
    ];

    for (const root of sdkRoots) {
      for (const tfm of preferredTfms) {
        const tfmCandidate = path.join(root, tfm, 'rvn.dll');
        if (fs.existsSync(tfmCandidate)) {
          return {
            executable: 'dotnet',
            args: [tfmCandidate],
            description: tfmCandidate
          };
        }
      }

      const flatCandidate = path.join(root, 'rvn.dll');
      if (fs.existsSync(flatCandidate)) {
        return {
          executable: 'dotnet',
          args: [flatCandidate],
          description: flatCandidate
        };
      }
    }
  }

  for (const root of bundledRoots) {
    for (const tfm of preferredTfms) {
      const tfmCandidate = path.join(root, tfm, 'rvn.dll');
      if (fs.existsSync(tfmCandidate)) {
        return {
          executable: 'dotnet',
          args: [tfmCandidate],
          description: tfmCandidate
        };
      }
    }

    const flatCandidate = path.join(root, 'rvn.dll');
    if (fs.existsSync(flatCandidate)) {
      return {
        executable: 'dotnet',
        args: [flatCandidate],
        description: flatCandidate
      };
    }
  }

  const frontendProjectPath = resolveFrontendProjectPath();
  if (!frontendProjectPath) {
    return undefined;
  }

  const frontendDirectory = path.dirname(frontendProjectPath);

  for (const tfm of preferredTfms) {
    const candidate = path.join(frontendDirectory, 'bin', 'Debug', tfm, 'rvn.dll');
    if (fs.existsSync(candidate)) {
      return {
        executable: 'dotnet',
        args: [candidate],
        description: candidate
      };
    }
  }

  return undefined;
}

async function loadSyntaxTree(
  document: vscode.TextDocument,
  view: SyntaxTreeViewMode,
  expandedContentProvider: ExpandedSyntaxContentProvider,
  storagePath: string
): Promise<LoadedSyntaxTree> {
  const targetFramework = document.uri.scheme === 'file'
    ? resolveTargetFramework(document.fileName)
    : undefined;
  const frontend = resolveFrontendInvocation(targetFramework);
  if (!frontend) {
    throw new Error(
      'Unable to locate the Raven developer tool. Install the Raven SDK, set "raven.sdkPath", or build src/Raven/Raven.csproj.'
    );
  }

  const temporaryDirectory = path.join(
    storagePath,
    'syntax-tree',
    createStableHash(document.uri.toString())
  );
  fs.mkdirSync(temporaryDirectory, { recursive: true });
  const temporaryPath = path.join(
    temporaryDirectory,
    `${crypto.randomUUID()}${path.extname(document.fileName) || '.rvn'}`
  );

  try {
    await fs.promises.writeFile(temporaryPath, document.getText(), 'utf8');
    const workspaceFolder = vscode.workspace.getWorkspaceFolder(document.uri)?.uri.fsPath;
    const sourcePath = document.uri.scheme === 'file' && fs.existsSync(document.fileName)
      ? document.fileName
      : undefined;
    const projectPath = sourcePath ? resolveOwningProjectPath(sourcePath) : undefined;
    const inputPath = projectPath ?? sourcePath ?? temporaryPath;
    const syntaxArguments = [
      ...frontend.args,
      'dev',
      'syntax',
      'json',
      '--syntax-view',
      view
    ];
    if (sourcePath) {
      syntaxArguments.push('--document', sourcePath, '--source-text', temporaryPath);
    }
    syntaxArguments.push(inputPath);

    const result = await execFileText(
      frontend.executable,
      syntaxArguments,
      {
        cwd: workspaceFolder ?? path.dirname(temporaryPath),
        maxBuffer: 32 * 1024 * 1024
      }
    );
    const toolDocument = JSON.parse(result.stdout) as SyntaxTreeToolDocument;
    return {
      root: toolDocument.root,
      navigationUri: toolDocument.view === 'expanded'
        ? expandedContentProvider.update(document.uri, toolDocument.sourceText)
        : document.uri
    };
  } catch (error) {
    const execError = error as ExecFileTextError;
    const details = execError.stderr?.trim() || execError.stdout?.trim() || execError.message;
    throw new Error(details);
  } finally {
    await fs.promises.rm(temporaryPath, { force: true });
  }
}

function resolveTargetFramework(targetPath: string): string | undefined {
  const configuration = vscode.workspace.getConfiguration('raven');
  const configuredFramework = configuration.get<string>('targetFramework')?.trim();
  if (configuredFramework && configuredFramework.length > 0) {
    return configuredFramework;
  }

  const projectFilePath = resolveOwningProjectPath(targetPath);
  if (!projectFilePath) {
    return undefined;
  }

  return getProjectTargetFramework(projectFilePath);
}

function isRavenFile(filePath: string): boolean {
  const ext = path.extname(filePath).toLowerCase();
  return ext === '.rvn' || ext === '.rav' || ext === '.rvnproj';
}

function resolveDebugTarget(
  config: vscode.DebugConfiguration,
  workspaceFolder?: string
): string | undefined {
  const folder = workspaceFolder ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  const configuredTarget = typeof config.target === 'string' ? config.target.trim() : '';
  const configuredProject = typeof config.project === 'string' ? config.project.trim() : '';
  const candidate = expandDebugPathVariables(configuredProject || configuredTarget, folder);

  if (candidate.length > 0) {
    return path.isAbsolute(candidate) ? candidate : path.resolve(folder ?? '', candidate);
  }

  const activeDocument = vscode.window.activeTextEditor?.document;
  if (activeDocument && activeDocument.uri.scheme === 'file' && isRavenFile(activeDocument.fileName)) {
    return activeDocument.fileName;
  }

  return undefined;
}

function getProjectAssemblyName(projectFilePath: string): string {
  const fallback = path.basename(projectFilePath, path.extname(projectFilePath));
  const xml = fs.readFileSync(projectFilePath, 'utf8');
  const assemblyNameMatch = xml.match(/<AssemblyName>\s*([^<]+)\s*<\/AssemblyName>/i);
  const outputAttributeMatch = xml.match(/\bOutput\s*=\s*"([^"]+)"/i);
  return assemblyNameMatch?.[1]?.trim() || outputAttributeMatch?.[1]?.trim() || fallback;
}

function getProjectTargetFramework(projectFilePath: string): string | undefined {
  try {
    const xml = fs.readFileSync(projectFilePath, 'utf8');
    const attributeMatch = xml.match(/\bTargetFramework\s*=\s*"([^"]+)"/i);
    if (attributeMatch?.[1]?.trim()) {
      return attributeMatch[1].trim();
    }

    const elementMatch = xml.match(/<TargetFramework>\s*([^<]+)\s*<\/TargetFramework>/i);
    if (elementMatch?.[1]?.trim()) {
      return elementMatch[1].trim();
    }
  } catch {
    // Ignore read/parse issues and fall back to defaults.
  }

  return undefined;
}

function resolveOwningProjectPath(targetPath: string): string | undefined {
  if (isRavenProjectFile(targetPath)) {
    return fs.existsSync(targetPath) ? targetPath : undefined;
  }

  const workspaceBoundary = resolveWorkspaceBoundary(targetPath);
  for (const directory of enumerateAncestorDirectories(path.dirname(targetPath), workspaceBoundary)) {
    const candidates = findRavenProjectsInDirectory(directory);
    if (candidates.length === 0) {
      continue;
    }

    if (candidates.length === 1) {
      return candidates[0];
    }

    const directoryName = path.basename(directory).toLowerCase();
    const preferred = candidates.find(candidate =>
      path.basename(candidate, path.extname(candidate)).toLowerCase() === directoryName
    );
    return preferred ?? candidates[0];
  }

  return undefined;
}

function findRavenProjectsInDirectory(directory: string): string[] {
  try {
    return fs
      .readdirSync(directory)
      .filter(entry => isRavenProjectFile(entry))
      .map(entry => path.join(directory, entry))
      .sort((left, right) => left.localeCompare(right));
  } catch {
    return [];
  }
}

function isRavenProjectFile(filePath: string): boolean {
  const ext = path.extname(filePath).toLowerCase();
  return ext === '.rvnproj';
}

function* enumerateAncestorDirectories(startPath: string, stopDirectory?: string): Generator<string> {
  let current = path.resolve(startPath);
  const stop = stopDirectory ? path.resolve(stopDirectory) : undefined;
  while (true) {
    yield current;
    if (stop && current.toLowerCase() === stop.toLowerCase()) {
      break;
    }

    const parent = path.dirname(current);
    if (parent === current) {
      break;
    }

    current = parent;
  }
}

function resolveWorkspaceBoundary(targetPath: string): string | undefined {
  const targetDirectory = path.dirname(path.resolve(targetPath));
  const containingWorkspace = vscode.workspace.workspaceFolders
    ?.map(folder => folder.uri.fsPath)
    .find(folderPath => isWithinDirectory(targetDirectory, folderPath));

  return containingWorkspace;
}

function isWithinDirectory(candidatePath: string, directoryPath: string): boolean {
  const normalizedCandidate = path.resolve(candidatePath);
  const normalizedDirectory = path.resolve(directoryPath);

  if (normalizedCandidate.toLowerCase() === normalizedDirectory.toLowerCase()) {
    return true;
  }

  const prefix = normalizedDirectory.endsWith(path.sep)
    ? normalizedDirectory
    : `${normalizedDirectory}${path.sep}`;
  return normalizedCandidate.toLowerCase().startsWith(prefix.toLowerCase());
}

type OutputLayout = {
  effectiveTargetPath: string;
  targetIsProject: boolean;
  outputDirectory: string;
  outputDllPath: string;
  workspaceFolder: string;
  cwd: string;
  targetFramework?: string;
};

type DebugLaunchOutput = {
  outputDllPath: string;
  cwd: string;
  environment?: Record<string, string>;
  applicationUrl?: string;
  launchBrowser?: boolean;
};

type DotNetLaunchProfile = {
  environment: Record<string, string>;
  applicationUrl?: string;
  launchBrowser: boolean;
};

function getContainingWorkspaceFolderPath(targetPath: string): string {
  const targetDirectory = path.dirname(path.resolve(targetPath));
  const containingWorkspace = vscode.workspace.workspaceFolders
    ?.map(folder => folder.uri.fsPath)
    .find(folderPath => isWithinDirectory(targetDirectory, folderPath));
  return containingWorkspace ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? targetDirectory;
}

function hashPathForOutput(targetPath: string): string {
  return crypto.createHash('sha256').update(path.resolve(targetPath)).digest('hex').slice(0, 12);
}

function normalizePathSegment(value: string): string {
  const normalized = value.trim().replace(/[^A-Za-z0-9_-]/g, '_');
  return normalized.length > 0 ? normalized : 'unknown';
}

function getTfmPathSegment(targetFramework: string | undefined, fallback: string): string {
  const tfm = targetFramework?.trim();
  return tfm && tfm.length > 0 ? tfm : fallback;
}

function resolveOutputLayout(targetPath: string, configuration: 'Debug' | 'Release'): OutputLayout {
  const effectiveTargetPath = resolveEffectiveTargetPath(targetPath);
  const targetFramework = resolveTargetFramework(effectiveTargetPath);
  const targetIsProject = isRavenProjectFile(effectiveTargetPath);
  const workspaceFolder = getContainingWorkspaceFolderPath(effectiveTargetPath);

  if (targetIsProject) {
    const projectDirectory = path.dirname(effectiveTargetPath);
    const tfmSegment = getTfmPathSegment(targetFramework, 'unknown-tfm');
    const outputDirectory = path.join(projectDirectory, 'bin', configuration, tfmSegment);
    return {
      effectiveTargetPath,
      targetIsProject,
      outputDirectory,
      outputDllPath: path.join(outputDirectory, `${getProjectAssemblyName(effectiveTargetPath)}.dll`),
      workspaceFolder,
      cwd: projectDirectory,
      targetFramework
    };
  }

  const fileBaseName = path.basename(effectiveTargetPath, path.extname(effectiveTargetPath));
  const tfmSegment = getTfmPathSegment(targetFramework, 'no-tfm');
  const deterministicDirectory = `${fileBaseName}-${hashPathForOutput(effectiveTargetPath)}`;
  const outputDirectory = path.join(workspaceFolder, '.raven-build', configuration, tfmSegment, deterministicDirectory);
  return {
    effectiveTargetPath,
    targetIsProject,
    outputDirectory,
    outputDllPath: path.join(outputDirectory, `${fileBaseName}.dll`),
    workspaceFolder,
    cwd: path.dirname(effectiveTargetPath),
    targetFramework
  };
}

function resolveProjectOutputLayout(
  projectPath: string,
  configuration: 'Debug' | 'Release'
): OutputLayout {
  const resolvedProjectPath = path.resolve(projectPath);
  const projectDirectory = path.dirname(resolvedProjectPath);
  const targetFramework = getProjectTargetFramework(resolvedProjectPath);
  const tfmSegment = getTfmPathSegment(targetFramework, 'unknown-tfm');
  const outputDirectory = path.join(projectDirectory, 'bin', configuration, tfmSegment);
  return {
    effectiveTargetPath: resolvedProjectPath,
    targetIsProject: true,
    outputDirectory,
    outputDllPath: path.join(outputDirectory, `${getProjectAssemblyName(resolvedProjectPath)}.dll`),
    workspaceFolder: getContainingWorkspaceFolderPath(resolvedProjectPath),
    cwd: projectDirectory,
    targetFramework
  };
}

function resolveConfiguredPath(value: unknown, workspaceFolder: string | undefined): string | undefined {
  if (typeof value !== 'string' || value.trim().length === 0) {
    return undefined;
  }

  const configuredPath = expandDebugPathVariables(value.trim(), workspaceFolder);
  return path.isAbsolute(configuredPath)
    ? configuredPath
    : path.resolve(workspaceFolder ?? '', configuredPath);
}

function expandDebugPathVariables(value: string, workspaceFolder: string | undefined): string {
  const activeFile = vscode.window.activeTextEditor?.document.uri.scheme === 'file'
    ? vscode.window.activeTextEditor.document.fileName
    : undefined;
  return replaceAllLiteral(
    replaceAllLiteral(
      replaceAllLiteral(value, '${workspaceFolder}', workspaceFolder ?? ''),
      '${fileDirname}',
      activeFile ? path.dirname(activeFile) : ''),
    '${file}',
    activeFile ?? '');
}

function replaceAllLiteral(value: string, search: string, replacement: string): string {
  return value.split(search).join(replacement);
}

function readDotNetLaunchProfile(
  projectPath: string,
  requestedProfile: unknown
): DotNetLaunchProfile | undefined {
  const launchSettingsPath = path.join(path.dirname(projectPath), 'Properties', 'launchSettings.json');
  if (!fs.existsSync(launchSettingsPath)) {
    return undefined;
  }

  try {
    const document = JSON.parse(fs.readFileSync(launchSettingsPath, 'utf8')) as {
      profiles?: Record<string, {
        commandName?: string;
        launchBrowser?: boolean;
        applicationUrl?: string;
        environmentVariables?: Record<string, string>;
      }>;
    };
    const profiles = document.profiles ?? {};
    const requestedName = typeof requestedProfile === 'string' ? requestedProfile.trim() : '';
    const selected = requestedName.length > 0
      ? profiles[requestedName]
      : Object.values(profiles).find(profile => profile.commandName === 'Project');
    if (!selected || (selected.commandName && selected.commandName !== 'Project')) {
      return undefined;
    }

    const applicationUrl = selected.applicationUrl
      ?.split(';')
      .map(value => value.trim())
      .find(value => value.length > 0);
    return {
      environment: selected.environmentVariables ?? {},
      applicationUrl,
      launchBrowser: selected.launchBrowser === true
    };
  } catch (error) {
    output.appendLine(`Unable to read launch profile '${launchSettingsPath}': ${String(error)}`);
    return undefined;
  }
}

function writeBuildManifest(layout: OutputLayout, mode: 'build' | 'debug'): void {
  const manifestPath = path.join(layout.outputDirectory, '.raven-build-manifest.json');
  const manifest = {
    mode,
    targetPath: layout.effectiveTargetPath,
    targetKind: layout.targetIsProject ? 'project' : 'file',
    targetFramework: layout.targetFramework ?? null,
    outputDirectory: layout.outputDirectory,
    outputDllPath: layout.outputDllPath,
    cwd: layout.cwd,
    generatedAtUtc: new Date().toISOString()
  };
  fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2));
}

async function compileForDebug(
  targetPath: string,
  startupProjectPath?: string,
  launchProfile?: unknown
): Promise<DebugLaunchOutput> {
  if (startupProjectPath) {
    const startupLayout = resolveProjectOutputLayout(startupProjectPath, 'Debug');
    const dotnetArgs = [
      'build',
      startupLayout.effectiveTargetPath,
      '--configuration',
      'Debug',
      ...(startupLayout.targetFramework ? ['--framework', startupLayout.targetFramework] : [])
    ];
    output.appendLine(`Compiling Raven debug host via dotnet: dotnet ${dotnetArgs.join(' ')}`);

    try {
      const { stdout, stderr } = await execFileText('dotnet', dotnetArgs, {
        cwd: startupLayout.workspaceFolder,
        maxBuffer: 10 * 1024 * 1024
      });
      if (stdout.trim().length > 0) output.appendLine(stdout);
      if (stderr.trim().length > 0) output.appendLine(stderr);
    } catch (error) {
      const e = error as Error & { stdout?: string; stderr?: string };
      if (e.stdout) output.appendLine(e.stdout);
      if (e.stderr) output.appendLine(e.stderr);
      throw new Error(`Raven debug host build failed. See the Raven output channel for details. ${e.message}`);
    }

    if (!fs.existsSync(startupLayout.outputDllPath)) {
      throw new Error(`Debug host assembly not found at '${startupLayout.outputDllPath}'.`);
    }

    const profile = readDotNetLaunchProfile(startupLayout.effectiveTargetPath, launchProfile);
    return {
      outputDllPath: startupLayout.outputDllPath,
      cwd: startupLayout.cwd,
      environment: profile?.applicationUrl
        ? { ...profile.environment, ASPNETCORE_URLS: profile.applicationUrl }
        : profile?.environment,
      applicationUrl: profile?.applicationUrl,
      launchBrowser: profile?.launchBrowser
    };
  }

  const layout = resolveOutputLayout(targetPath, 'Debug');
  const invocation = layout.targetIsProject
    ? resolveFrontendInvocation(layout.targetFramework)
    : resolveCompilerInvocation(layout.targetFramework);
  if (!invocation) {
    throw new Error(layout.targetIsProject
      ? 'Unable to locate a built rvn.dll. Build Raven first or set "raven.sdkPath" to an SDK directory containing rvn.dll.'
      : 'Unable to locate a built rvnc.dll. Build Raven.Compiler first or point "raven.compilerProjectPath" at a workspace containing src/Raven.Compiler/bin/Debug/<tfm>/rvnc.dll.'
    );
  }

  if (!layout.targetIsProject) {
    if (fs.existsSync(layout.outputDirectory)) {
      fs.rmSync(layout.outputDirectory, { recursive: true, force: true });
    }
    fs.mkdirSync(layout.outputDirectory, { recursive: true });
  }

  const dotnetArgs = layout.targetIsProject
    ? [
        ...invocation.args,
        'build',
        layout.effectiveTargetPath,
        '--configuration',
        'Debug',
        ...(layout.targetFramework ? ['--framework', layout.targetFramework] : [])
      ]
    : [
        ...invocation.args,
        layout.effectiveTargetPath,
        '-o',
        layout.outputDllPath,
        ...(layout.targetFramework ? ['--framework', layout.targetFramework] : [])
      ];

  output.appendLine(`Compiling for debug via ${invocation.description}: ${invocation.executable} ${dotnetArgs.join(' ')}`);

  try {
    const { stdout, stderr } = await execFileText(invocation.executable, dotnetArgs, {
      cwd: layout.workspaceFolder,
      maxBuffer: 10 * 1024 * 1024
    });

    if (stdout.trim().length > 0) {
      output.appendLine(stdout);
    }
    if (stderr.trim().length > 0) {
      output.appendLine(stderr);
    }
  } catch (error) {
    const e = error as Error & { stdout?: string; stderr?: string };
    if (e.stdout) output.appendLine(e.stdout);
    if (e.stderr) output.appendLine(e.stderr);
    throw new Error(`Raven compile failed. See the Raven output channel for details. ${e.message}`);
  }

  if (!fs.existsSync(layout.outputDllPath)) {
    throw new Error(`Compiled assembly not found at '${layout.outputDllPath}'.`);
  }

  writeBuildManifest(layout, 'debug');
  return { outputDllPath: layout.outputDllPath, cwd: layout.cwd };
}

async function buildTarget(targetPath: string): Promise<{ outputPath: string; cwd: string }> {
  const layout = resolveOutputLayout(targetPath, 'Debug');
  const invocation = layout.targetIsProject
    ? resolveFrontendInvocation(layout.targetFramework)
    : resolveCompilerInvocation(layout.targetFramework);
  if (!invocation) {
    throw new Error(layout.targetIsProject
      ? 'Unable to locate a built rvn.dll. Build Raven first or set "raven.sdkPath" to an SDK directory containing rvn.dll.'
      : 'Unable to locate a built rvnc.dll. Build Raven.Compiler first or point "raven.compilerProjectPath" at a workspace containing src/Raven.Compiler/bin/Debug/<tfm>/rvnc.dll.'
    );
  }

  if (!layout.targetIsProject) {
    fs.mkdirSync(layout.outputDirectory, { recursive: true });
  }

  const dotnetArgs = layout.targetIsProject
    ? [
        ...invocation.args,
        'build',
        layout.effectiveTargetPath,
        '--configuration',
        'Debug',
        ...(layout.targetFramework ? ['--framework', layout.targetFramework] : [])
      ]
    : [
        ...invocation.args,
        layout.effectiveTargetPath,
        '-o',
        layout.outputDllPath,
        ...(layout.targetFramework ? ['--framework', layout.targetFramework] : [])
      ];

  output.appendLine(`Building Raven target via ${invocation.description}: ${invocation.executable} ${dotnetArgs.join(' ')}`);

  try {
    const { stdout, stderr } = await execFileText(invocation.executable, dotnetArgs, {
      cwd: layout.workspaceFolder,
      maxBuffer: 10 * 1024 * 1024
    });

    if (stdout.trim().length > 0) {
      output.appendLine(stdout);
    }
    if (stderr.trim().length > 0) {
      output.appendLine(stderr);
    }
  } catch (error) {
    const e = error as Error & { stdout?: string; stderr?: string };
    if (e.stdout) output.appendLine(e.stdout);
    if (e.stderr) output.appendLine(e.stderr);
    throw new Error(`Raven build failed. See the Raven output channel for details. ${e.message}`);
  }

  writeBuildManifest(layout, 'build');
  return { outputPath: layout.outputDllPath, cwd: layout.cwd };
}

function resolveCommandTarget(uri?: vscode.Uri): string | undefined {
  const directTarget = uri?.scheme === 'file' ? uri.fsPath : undefined;
  if (directTarget && isRavenFile(directTarget)) {
    return directTarget;
  }

  const activeTarget = vscode.window.activeTextEditor?.document.fileName;
  if (activeTarget && isRavenFile(activeTarget)) {
    return activeTarget;
  }

  return undefined;
}

function resolveEffectiveTargetPath(targetPath: string): string {
  return resolveOwningProjectPath(targetPath) ?? targetPath;
}

function quoteTerminalArgument(value: string): string {
  if (process.platform === 'win32') {
    return `"${value.replace(/"/g, '""')}"`;
  }

  return `'${value.replace(/'/g, `'\\''`)}'`;
}

function createTerminalCommand(invocation: ToolInvocation, args: readonly string[]): string {
  return [invocation.executable, ...invocation.args, ...args]
    .map(quoteTerminalArgument)
    .join(' ');
}

class RavenDebugConfigurationProvider implements vscode.DebugConfigurationProvider {
  provideDebugConfigurations(): vscode.ProviderResult<vscode.DebugConfiguration[]> {
    return [{
      type: 'raven',
      request: 'launch',
      name: 'Raven: Compile and Debug',
      target: '${file}'
    }];
  }

  async resolveDebugConfiguration(
    _folder: vscode.WorkspaceFolder | undefined,
    config: vscode.DebugConfiguration
  ): Promise<vscode.DebugConfiguration | null | undefined> {
    const workspaceFolder = _folder?.uri.fsPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    const targetPath = resolveDebugTarget(config, workspaceFolder);
    if (!targetPath || !isRavenFile(targetPath)) {
      void vscode.window.showErrorMessage(
        'Select a .rvn, .rvnproj, or .rav file, or set "target"/"project" in launch.json.'
      );
      return undefined;
    }

    return vscode.window.withProgress(
      {
        location: vscode.ProgressLocation.Notification,
        title: `Compiling ${path.basename(resolveEffectiveTargetPath(targetPath))}`
      },
      async () => {
        const startupProjectPath = resolveConfiguredPath(config.startupProject, workspaceFolder);
        if (startupProjectPath && !fs.existsSync(startupProjectPath)) {
          throw new Error(`Configured startup project was not found at '${startupProjectPath}'.`);
        }
        const launchOutput = await compileForDebug(
          targetPath,
          startupProjectPath,
          config.launchProfile);
        const ravenConfiguration = vscode.workspace.getConfiguration('raven');
        const stopAtEntry = ravenConfiguration.get<boolean>('debugStopAtEntry', false);
        const justMyCode = ravenConfiguration.get<boolean>('debugJustMyCode', false);
        const moduleLoadMessages = ravenConfiguration.get<boolean>('debugModuleLoadMessages', false);
        const engineLogging = ravenConfiguration.get<boolean>('debugEngineLogging', false);
        const excludeFrameworkModules = ravenConfiguration.get<boolean>('debugExcludeFrameworkModules', true);

        const symbolOptions: {
          searchMicrosoftSymbolServer: boolean;
          searchNuGetOrgSymbolServer: boolean;
          moduleFilter?: {
            mode: string;
            excludedModules: string[];
          };
        } = {
          searchMicrosoftSymbolServer: false,
          searchNuGetOrgSymbolServer: false
        };

        if (excludeFrameworkModules) {
          symbolOptions.moduleFilter = {
            mode: 'loadAllButExcluded',
            excludedModules: [
              'System.*',
              'Microsoft.*'
            ]
          };
        }

        const debugConfiguration: vscode.DebugConfiguration = {
          name: config.name ?? 'Raven: Compile and Debug',
          type: 'coreclr',
          request: 'launch',
          program: 'dotnet',
          args: [launchOutput.outputDllPath],
          cwd: launchOutput.cwd,
          console: launchOutput.applicationUrl ? 'internalConsole' : 'integratedTerminal',
          stopAtEntry,
          justMyCode,
          requireExactSource: false,
          logging: {
            moduleLoad: moduleLoadMessages,
            engineLogging
          },
          symbolOptions,
          ...(launchOutput.environment ? { env: launchOutput.environment } : {})
        };

        if (launchOutput.launchBrowser && launchOutput.applicationUrl) {
          debugConfiguration.serverReadyAction = {
            action: 'openExternally',
            pattern: '\\bNow listening on:\\s+(https?://\\S+)',
            uriFormat: '%s'
          };
        }

        return debugConfiguration;
      }
    );
  }
}

export function activate(context: vscode.ExtensionContext): void {
  extensionInstallPath = context.extensionPath;
  output.appendLine('Activating Raven VS Code extension...');
  appendLifecycleLog(`Extension activate() called. extensionPath=${context.extensionPath}`);
  appendToolchainReport(context);

  // Ensure VS Code disposes the client on shutdown.
  context.subscriptions.push({
    dispose: () => {
      appendLifecycleLog('Extension subscription dispose() called.');
      return stopClient('subscription dispose');
    }
  });

  context.subscriptions.push(
    macroEmbeddedDocuments,
    vscode.workspace.registerTextDocumentContentProvider(
      macroEmbeddedDocumentScheme,
      macroEmbeddedDocuments)
  );

  const generatedSourceChanged = new vscode.EventEmitter<vscode.Uri>();
  const generatedSourceDiagnostics = vscode.languages.createDiagnosticCollection('raven-generated');
  let generatedSourceRefresh: ReturnType<typeof setTimeout> | undefined;
  context.subscriptions.push(
    generatedSourceChanged,
    generatedSourceDiagnostics,
    { dispose: () => clearTimeout(generatedSourceRefresh) },
    vscode.workspace.registerTextDocumentContentProvider('raven-generated', {
      onDidChange: generatedSourceChanged.event,
      async provideTextDocumentContent(uri, token): Promise<string> {
        if (clientStartPromise) {
          await clientStartPromise;
        }
        const response = await client?.sendRequest<GeneratedSourceResponse | null>('raven/generatedSource', { uri: uri.toString() }, token);
        if (!response) {
          generatedSourceDiagnostics.delete(uri);
          return '// This generated source is no longer available.\n';
        }

        const diagnostics = client
          ? await client.protocol2CodeConverter.asDiagnostics(response.diagnostics, token)
          : [];
        if (!token.isCancellationRequested) {
          generatedSourceDiagnostics.set(uri, diagnostics);
        }
        return response.text;
      }
    }),
    vscode.workspace.onDidCloseTextDocument(document => {
      if (document.uri.scheme === 'raven-generated') {
        generatedSourceDiagnostics.delete(document.uri);
      }
    }),
    vscode.workspace.onDidChangeTextDocument(event => {
      if (event.document.uri.scheme !== 'file' || event.document.languageId !== 'raven') {
        return;
      }
      clearTimeout(generatedSourceRefresh);
      generatedSourceRefresh = setTimeout(() => {
        for (const document of vscode.workspace.textDocuments) {
          if (document.uri.scheme === 'raven-generated') {
            generatedSourceChanged.fire(document.uri);
          }
        }
      }, 300);
    })
  );

  void startClient(context, 'activate');
  void offerSdkInstallationIfMissing(context);

  const expandedSyntaxContentProvider = new ExpandedSyntaxContentProvider();
  context.subscriptions.push(
    expandedSyntaxContentProvider,
    vscode.workspace.registerTextDocumentContentProvider(
      'raven-expanded',
      expandedSyntaxContentProvider
    )
  );
  const syntaxTreeProvider = new SyntaxTreeDataProvider(
    (document, view) => loadSyntaxTree(
      document,
      view,
      expandedSyntaxContentProvider,
      context.globalStorageUri.fsPath
    ),
    message => output.appendLine(message)
  );
  const syntaxTreeView = vscode.window.createTreeView('raven.syntaxTree', {
    treeDataProvider: syntaxTreeProvider,
    showCollapseAll: true
  });
  context.subscriptions.push(syntaxTreeProvider, syntaxTreeView);
  syntaxTreeProvider.setActiveDocument(vscode.window.activeTextEditor?.document);
  syntaxTreeProvider.setVisible(syntaxTreeView.visible);

  const focusSyntaxTreeView = async (): Promise<void> => {
    await vscode.commands.executeCommand('workbench.view.explorer');
    await vscode.commands.executeCommand('raven.syntaxTree.focus');
    syntaxTreeProvider.refresh();
  };

  const openExpandedSyntaxDocument = async (): Promise<void> => {
    const document = syntaxTreeProvider.getActiveDocument();
    if (!document) {
      void vscode.window.showInformationMessage('Open a Raven document to view its expanded source.');
      return;
    }

    const loadedTree = await loadSyntaxTree(
      document,
      'expanded',
      expandedSyntaxContentProvider,
      context.globalStorageUri.fsPath
    );
    const expandedDocument = await vscode.workspace.openTextDocument(loadedTree.navigationUri);
    await vscode.window.showTextDocument(expandedDocument, {
      preview: true,
      viewColumn: vscode.ViewColumn.Beside
    });
  };

  context.subscriptions.push(
    syntaxTreeView.onDidChangeVisibility(event => syntaxTreeProvider.setVisible(event.visible)),
    vscode.window.onDidChangeActiveTextEditor(editor => syntaxTreeProvider.setActiveDocument(editor?.document)),
    vscode.commands.registerCommand('raven.syntaxTree.refresh', () => syntaxTreeProvider.refresh()),
    vscode.commands.registerCommand('raven.syntaxTree.showAuthored', async () => {
      syntaxTreeProvider.setView('authored');
      syntaxTreeView.description = 'Authored';
      await vscode.commands.executeCommand('setContext', 'raven.syntaxTreeView', 'authored');
      await focusSyntaxTreeView();
    }),
    vscode.commands.registerCommand('raven.syntaxTree.showExpanded', async () => {
      syntaxTreeProvider.setView('expanded');
      syntaxTreeView.description = 'Expanded';
      await vscode.commands.executeCommand('setContext', 'raven.syntaxTreeView', 'expanded');
      await openExpandedSyntaxDocument();
      await focusSyntaxTreeView();
    }),
    vscode.commands.registerCommand('raven.syntaxTree.showExpandedDocument', openExpandedSyntaxDocument),
    vscode.commands.registerCommand('raven.syntaxTree.reveal', async (item: SyntaxTreeItem) => {
      await revealSyntaxTreeItem(item);
    })
  );
  syntaxTreeView.description = 'Authored';
  void vscode.commands.executeCommand('setContext', 'raven.syntaxTreeView', 'authored');

  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration(event => {
      if (event.affectsConfiguration('raven.inlayHints.enabled') ||
          event.affectsConfiguration('raven.inlayHints.inferredTypes.enabled') ||
          event.affectsConfiguration('raven.inlayHints.names.enabled')) {
        void refreshInlayHints();
      }
    })
  );

  context.subscriptions.push(
    vscode.workspace.onDidChangeTextDocument(event => {
      if (event.document.languageId !== 'raven') {
        return;
      }

      if (vscode.window.activeTextEditor?.document.uri.toString() === event.document.uri.toString()) {
        syntaxTreeProvider.scheduleRefresh();
      }

      if (areRavenInlayHintsEnabled() &&
          (areInferredTypeInlayHintsEnabled() || areNameInlayHintsEnabled()) &&
          event.contentChanges.length > 0) {
        scheduleInlayHintRefresh();
      }

      if (shouldTriggerImportCompletionAfterQuietPeriod(event)) {
        scheduleImportCompletionTrigger(event.document);
      }
    })
  );

  context.subscriptions.push(
    vscode.workspace.onDidCloseTextDocument(document => {
      if (document.languageId === 'raven') {
        inlayHintRequestVersions.delete(document.uri.toString());
      }
    })
  );

  context.subscriptions.push(
    vscode.workspace.onDidChangeWorkspaceFolders(event => {
      const added = event.added.map(folder => folder.uri.fsPath).join(', ') || '<none>';
      const removed = event.removed.map(folder => folder.uri.fsPath).join(', ') || '<none>';
      appendLifecycleLog(`Workspace folders changed. added=${added} removed=${removed}`);
      void restartClient(context, 'workspace folders changed');
    })
  );

  const debugConfigurationProvider = new RavenDebugConfigurationProvider();
  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider(
      'raven',
      debugConfigurationProvider,
      vscode.DebugConfigurationProviderTriggerKind.Dynamic
    )
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('raven.showToolchainInfo', () => {
      appendToolchainReport(context);
      output.show(true);
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('raven.toggleInlayHints', async () => {
      const configuration = vscode.workspace.getConfiguration('raven');
      const current = configuration.get<boolean>('inlayHints.enabled', true);
      const next = !current;
      await configuration.update('inlayHints.enabled', next, vscode.ConfigurationTarget.Global);
      await refreshInlayHints();
      void vscode.window.showInformationMessage(
        `Raven inlay hints ${next ? 'enabled' : 'disabled'}.`
      );
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('raven.toggleInferredTypeInlayHints', async () => {
      const configuration = vscode.workspace.getConfiguration('raven');
      const current = configuration.get<boolean>('inlayHints.inferredTypes.enabled', true);
      const next = !current;
      await configuration.update('inlayHints.inferredTypes.enabled', next, vscode.ConfigurationTarget.Global);
      await refreshInlayHints();
      void vscode.window.showInformationMessage(
        `Raven inferred type inlay hints ${next ? 'enabled' : 'disabled'}.`
      );
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('raven.toggleNameInlayHints', async () => {
      const configuration = vscode.workspace.getConfiguration('raven');
      const current = configuration.get<boolean>('inlayHints.names.enabled', true);
      const next = !current;
      await configuration.update('inlayHints.names.enabled', next, vscode.ConfigurationTarget.Global);
      await refreshInlayHints();
      void vscode.window.showInformationMessage(
        `Raven name inlay hints ${next ? 'enabled' : 'disabled'}.`
      );
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('raven.showMacroExpansion', async (_uri?: string, macroName?: string, expansionText?: string) => {
      if (!expansionText || expansionText.trim().length === 0) {
        void vscode.window.showInformationMessage('No macro expansion is available at the current location.');
        return;
      }

      const header = macroName && macroName.trim().length > 0
        ? `// Macro expansion for #[${macroName}]\n\n`
        : '// Macro expansion\n\n';

      const document = await vscode.workspace.openTextDocument({
        content: `${header}${expansionText}\n`,
        language: 'raven'
      });

      await vscode.window.showTextDocument(document, {
        preview: true,
        viewColumn: vscode.ViewColumn.Beside
      });
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('raven.showCodeActionPreview', async (_uri?: string, actionTitle?: string, beforeText?: string, afterText?: string) => {
      if (typeof beforeText !== 'string' || typeof afterText !== 'string') {
        void vscode.window.showInformationMessage('No code action preview is available.');
        return;
      }

      const beforeDocument = await vscode.workspace.openTextDocument({
        content: beforeText,
        language: 'raven'
      });

      const afterDocument = await vscode.workspace.openTextDocument({
        content: afterText,
        language: 'raven'
      });

      const title = actionTitle && actionTitle.trim().length > 0
        ? `Preview: ${actionTitle}`
        : 'Code Action Preview';

      await vscode.commands.executeCommand(
        'vscode.diff',
        beforeDocument.uri,
        afterDocument.uri,
        title,
        { preview: true }
      );
    })
  );

  const documentationProvider = new RavenDocumentationContentProvider();
  context.subscriptions.push(
    vscode.workspace.registerTextDocumentContentProvider('raven-doc', documentationProvider)
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('raven.openDocumentation', async (uriOrString?: unknown) => {
      const uri = parseDocumentationUriArgument(uriOrString);
      if (!uri) {
        void vscode.window.showErrorMessage('Unable to open Raven documentation for this symbol.');
        return;
      }

      const document = await vscode.workspace.openTextDocument(uri);
      await vscode.window.showTextDocument(document, {
        preview: true,
        viewColumn: vscode.ViewColumn.Beside
      });
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('raven.debug.compileAndDebug', async (uri?: vscode.Uri) => {
      const target = resolveCommandTarget(uri);
      if (!target) {
        void vscode.window.showErrorMessage('No active Raven file to debug.');
        return;
      }
      const effectiveTarget = resolveEffectiveTargetPath(target);

      await vscode.debug.startDebugging(undefined, {
        type: 'raven',
        request: 'launch',
        name: 'Raven: Compile and Debug',
        target: effectiveTarget
      });
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('raven.run.compileAndRun', async (uri?: vscode.Uri) => {
      const target = resolveCommandTarget(uri);
      if (!target) {
        void vscode.window.showErrorMessage('No active Raven file to run.');
        return;
      }
      const effectiveTarget = resolveEffectiveTargetPath(target);

      const frontend = resolveFrontendInvocation(resolveTargetFramework(effectiveTarget));
      if (!frontend) {
        void vscode.window.showErrorMessage(
          'Unable to locate rvn. Install the Raven SDK, set "raven.sdkPath", or build src/Raven/Raven.csproj.'
        );
        return;
      }

      const cwd = path.dirname(effectiveTarget);
      const terminal = vscode.window.createTerminal({
        name: isRavenProjectFile(effectiveTarget) ? 'Raven: Run Project' : 'Raven: Run File',
        cwd
      });
      terminal.show(true);
      terminal.sendText(createTerminalCommand(frontend, ['run', effectiveTarget]));
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('raven.build.activeTarget', async (uri?: vscode.Uri) => {
      const target = resolveCommandTarget(uri);
      if (!target) {
        void vscode.window.showErrorMessage('No active Raven file or project to build.');
        return;
      }

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `Building ${path.basename(target)}`
        },
        async () => {
          const { outputPath } = await buildTarget(target);
          output.appendLine(`Build output: ${outputPath}`);
          void vscode.window.showInformationMessage(`Raven build succeeded: ${path.basename(outputPath)}`);
        }
      );
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('raven.build.clean', async (_uri?: vscode.Uri) => {
      const removedPaths: string[] = [];
      const workspaceFolders = vscode.workspace.workspaceFolders?.map(folder => folder.uri.fsPath)
        ?? [process.cwd()];

      for (const workspaceFolder of workspaceFolders) {
        const candidate = path.join(workspaceFolder, '.raven-build');
        if (!fs.existsSync(candidate)) {
          continue;
        }

        fs.rmSync(candidate, { recursive: true, force: true });
        removedPaths.push(candidate);
      }

      if (removedPaths.length === 0) {
        void vscode.window.showInformationMessage('Raven clean completed. No build artifacts were found.');
        return;
      }

      output.appendLine(`Cleaned Raven artifacts:\n- ${removedPaths.join('\n- ')}`);
      void vscode.window.showInformationMessage('Raven clean completed.');
    })
  );
}

export function deactivate(): Thenable<void> | undefined {
  appendLifecycleLog('Extension deactivate() called.');
  return stopClient('deactivate');
}
