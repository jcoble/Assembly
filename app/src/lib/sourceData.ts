export type SourceLanguage = string;

export type ProjectRoot = {
  id: string;
  name: string;
  path: string;
};

export type SourceRecord = {
  path: string;
  relativePath: string;
  fileName: string;
  language: SourceLanguage;
  byteCount: number;
};

export type SourceDirectoryEntry = {
  path: string;
  name: string;
  isDirectory: boolean;
  excluded: boolean;
};

export type SourceTreeSearchMatch = SourceDirectoryEntry & {
  relativePath: string;
};

export type SourceTreeSearchPage = {
  matches: SourceTreeSearchMatch[];
  nextCursor: number | null;
  complete: boolean;
};

export type SourcePreview = SourceRecord & {
  content: string;
  lineCount: number;
  /** SHA-256 of the bytes read; a save must present the same revision. */
  revision: string;
};

export type SourceSearchMatch = SourceRecord & {
  line: number;
  column: number;
  excerpt: string;
};

export type SourceDefinitionTarget = SourceRecord & {
  symbolName: string;
  kind: string;
  line: number;
  column: number;
  detail: string;
};

export type SourceWorkspaceSymbol = SourceRecord & {
  symbolName: string;
  kind: string;
  line: number;
  column: number;
  detail: string;
  containerName: string | null;
};

export type SourceNavigationLocation = {
  path: string;
  line: number | null;
};

export type SourceNavigationHistoryStep = {
  target: SourceNavigationLocation | null;
  backStack: SourceNavigationLocation[];
  forwardStack: SourceNavigationLocation[];
};

export type SourceReferenceTarget = SourceRecord & {
  symbolName: string;
  line: number;
  column: number;
  excerpt: string;
};

/**
 * How many lines mention each of several symbols, counted in one pass over the
 * project. This is what the "N references" numbers in the editor margin are
 * built from — they ask about every symbol in the open file at once instead of
 * running one project-wide search per symbol.
 *
 * `approximate` is true when the pass stopped early because it ran out of its
 * time budget. Symbols it did reach are still worth showing; a zero for a
 * symbol it may never have reached means "not counted", so the margin draws
 * nothing rather than a wrong "0 references".
 */
export type SourceReferenceCountResult = {
  counts: Record<string, number>;
  approximate: boolean;
  scannedFiles: number;
  elapsedMs: number;
};

export type SourceLspStatus = {
  language: SourceLanguage;
  languageID: string;
  available: boolean;
  serverName: string;
  command: string;
  args: string[];
  reason: string | null;
};

export type SourceLspLookupRequest = {
  root: string;
  line: number;
  column: number;
  limit?: number;
};

export type SourceLspRenameRequest = {
  root: string;
  line: number;
  column: number;
  newName: string;
};

export type SourceLspWorkspaceSymbolRequest = {
  root: string;
  query: string;
  limit?: number;
};

export type SourceCodeActionDiagnostic = {
  severity: SourceDiagnosticSeverity;
  message: string;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  source?: string;
};

export type SourceCodeActionLookupRequest = {
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  diagnostics: SourceCodeActionDiagnostic[];
};

export type SourceLspCodeActionRequest = SourceCodeActionLookupRequest & {
  root: string;
  limit?: number;
};

export type SourceLspHover = {
  contents: string[];
};

export type SourceCompletionItem = {
  label: string;
  kind: string;
  detail: string;
  insertText: string;
};

export type SourceTextEdit = {
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  newText: string;
};

export type SourceRenameFileEdit = {
  path: string;
  relativePath: string;
  edits: SourceTextEdit[];
};

export type SourceRenameResult = {
  files: SourceRenameFileEdit[];
};

export type SourceCodeAction = {
  title: string;
  kind: string;
  isPreferred: boolean;
  disabledReason: string | null;
  files: SourceRenameFileEdit[];
};

export type SourceDocumentHighlight = {
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  kind: 'text' | 'read' | 'write';
};

export type SourceSignatureParameter = {
  label: string;
  documentation: string;
};

export type SourceSignature = {
  label: string;
  documentation: string;
  parameters: SourceSignatureParameter[];
};

export type SourceSignatureHelp = {
  signatures: SourceSignature[];
  activeSignature: number;
  activeParameter: number;
};

export type SourceInlayHint = {
  label: string;
  tooltip: string;
  kind: 'type' | 'parameter' | 'other';
  line: number;
  column: number;
  paddingLeft: boolean;
  paddingRight: boolean;
};

export type SourceDiagnosticSeverity = 'error' | 'warning' | 'info' | 'hint';

export type SourceDiagnostic = {
  severity: SourceDiagnosticSeverity;
  message: string;
  line: number;
  column: number;
  source?: string;
};

export type SourceSymbol = {
  name: string;
  kind: string;
  line: number;
  column: number;
  detail: string;
};

export const sourceSemanticTokenLegend = {
  tokenTypes: [
    'namespace',
    'class',
    'interface',
    'type',
    'enum',
    'function',
    'method',
    'property',
    'variable',
    'parameter',
    'enumMember',
    'typeParameter',
    'keyword',
    'string',
    'number',
    'operator',
    'comment'
  ],
  tokenModifiers: []
} as const;

export type SourceSemanticTokenType = (typeof sourceSemanticTokenLegend.tokenTypes)[number];

export type SourceSemanticToken = {
  tokenType: SourceSemanticTokenType;
  line: number;
  startColumn: number;
  length: number;
};

export type SourceScanResult = {
  records: SourceRecord[];
  limit: number;
  truncated: boolean;
  stats?: SourceScanStats;
};

export type SourceScanStats = {
  visitedEntries: number;
  matchedFiles: number;
  skippedDirectories: number;
  unsupportedFiles: number;
  unreadableEntries: number;
  requestedLimit?: number;
  effectiveLimit?: number;
  returnedFiles?: number;
  returnedCount?: number;
  collectionLimit?: number;
  collectionLimitReached?: boolean;
  visitedEntryCount?: number;
  matchedFileCount?: number;
  skippedDirectoryCount?: number;
  unsupportedFileCount?: number;
  unreadableEntryCount?: number;
  skippedDirectorySamples?: SourceSkippedDirectory[];
};

export type SourceSkippedDirectory = {
  path: string;
  name: string;
  reason: string;
};

export type SourceScanHealthStatus =
  | 'scanning'
  | 'loading'
  | 'error'
  | 'empty'
  | 'filtered'
  | 'suspicious'
  | 'truncated'
  | 'ready';

export type SourceScanHealth = {
  status: SourceScanHealthStatus;
  needsAttention: boolean;
  summary: string;
  action: string | null;
};

export type SourceScanHealthInput = {
  totalCount: number;
  filteredCount: number;
  truncated: boolean;
  requestedLimit: number;
  suspiciousThreshold: number;
  query: string;
  scanning: boolean;
  loading: boolean;
  error: string;
};

export type SourceScanRecoveryAction = 'reset-index' | 'choose-root' | 'copy-diagnostic';

export type SourceScanRecovery = {
  visible: boolean;
  title: string;
  detail: string;
  primaryAction: SourceScanRecoveryAction | null;
  secondaryAction: SourceScanRecoveryAction | null;
};

export type SourceScanRecoveryInput = SourceScanHealthInput & {
  stats?: SourceScanStats | null;
};

export type SourceScanEvidenceMode =
  | 'idle'
  | 'cache'
  | 'native'
  | 'repair'
  | 'tiny'
  | 'background'
  | 'failed'
  | 'stopped';

export type SourceScanEvidenceTone = 'active' | 'ready' | 'warning' | 'error' | 'muted';

export type SourceScanEvidence = {
  label: string;
  detail: string;
  tone: SourceScanEvidenceTone;
};

export type SourceScanEvidenceInput = {
  project: ProjectRoot;
  mode: SourceScanEvidenceMode;
  entry: SourceScanCacheEntry | null;
  requestedLimit: number;
  scanning: boolean;
  loading: boolean;
  error: string;
  now?: number;
};

export type SourceRecentRecord = SourceRecord & {
  projectID: string;
  projectName: string;
  openedAt: number;
};

export type SourceOpenTab = SourceRecentRecord;

export type SourceScanCacheEntry = {
  key: string;
  projectID: string;
  projectPath: string;
  sourceSignature?: string;
  limit: number;
  truncated: boolean;
  records: SourceRecord[];
  scannedAt: number;
  stats?: SourceScanStats;
};

export type SourceScanCache = Record<string, SourceScanCacheEntry>;

export type ProjectActivationScanReason = 'cache' | 'force' | 'missing-or-stale' | 'repair';

export type ProjectActivationScanPlan = {
  shouldScan: boolean;
  reason: ProjectActivationScanReason;
  status: string;
  detail: string;
  cacheRecords: number;
};

export type ProjectActivationScanPlanInput = {
  project: ProjectRoot;
  entry: SourceScanCacheEntry | null;
  forceScan: boolean;
  limit: number;
  suspiciousThreshold: number;
  now?: number;
};

export type GitTaskMetadata = {
  taskID?: string | null;
};

export type GitTaskSourceMetadata = GitTaskMetadata & {
  sourceLabel: string;
  sourceDetail?: string | null;
};

export type GitTaskSourceGroup = {
  taskID: string;
  sourceSummary: string;
  detailSummary: string;
  sources: GitTaskSourceMetadata[];
};

export type GitBranchHealthTone = 'clean' | 'dirty' | 'warning' | 'error' | 'muted';

export type GitBranchHealthChip = {
  label: string;
  value: string;
  tone: GitBranchHealthTone;
};

export type GitBranchHealthInput = {
  branch?: string | null;
  ahead?: number | null;
  behind?: number | null;
  stagedCount?: number | null;
  unstagedCount?: number | null;
  untrackedCount?: number | null;
  dirtyCount?: number | null;
  changedCount?: number | null;
  isDirty?: boolean | null;
  error?: string | null;
  rootLabel?: string | null;
  lastCommitSha?: string | null;
};

export type GitBranchHealthSummary = {
  branch: string;
  sync: string;
  dirty: string;
  detail: string;
  tone: GitBranchHealthTone;
  chips: GitBranchHealthChip[];
};

export type GitCommitGraphKind = 'head' | 'branch' | 'merge' | 'root' | 'commit';

export type GitCommitOwnershipBadgeTone = 'head' | 'upstream' | 'tag' | 'branch' | 'task' | 'merge' | 'root';

export type GitCommitOwnershipBadge = {
  label: string;
  title: string;
  tone: GitCommitOwnershipBadgeTone;
};

export type GitCommitOwnershipBadgeInput = {
  refs: string;
  taskID?: string | null;
  taskSource?: string | null;
  parentCount?: number | null;
};

export type SourceContextGitStatus = {
  branch?: string | null;
  ahead: number;
  behind: number;
  files: Array<unknown>;
};

export type SourceContextIdentity = {
  projectName: string;
  rootLabel: string;
  rootPath: string;
  gitSummary: string;
  runtime: string;
  summary: string;
};

export type CloseOpenSourceTabResult = {
  tabs: SourceOpenTab[];
  nextActivePath: string | null;
};

export type CloseOpenSourceTabsResult = CloseOpenSourceTabResult & {
  closedCount: number;
  retainedDirtyCount: number;
};

export type QuickOpenQuery = {
  searchQuery: string;
  targetLine: number | null;
};

export type SourceTreeNode = {
  id: string;
  name: string;
  relativePath: string;
  file: SourceRecord | null;
  children: SourceTreeNode[];
};

export type SourceTreeRow = {
  node: SourceTreeNode;
  level: number;
};

export type VirtualSourceTreeRows = {
  rows: SourceTreeRow[];
  startIndex: number;
  endIndex: number;
  topSpacerHeight: number;
  bottomSpacerHeight: number;
  totalHeight: number;
};

export const defaultProjectRoots: ProjectRoot[] = [
  {
    id: 'ediplatform',
    name: 'EdiPlatform',
    path: '/Users/blackcolours/dev/work/EdiPlatform'
  },
  {
    id: 'mac-command-bar',
    name: 'MacCommandBar',
    path: '/Users/blackcolours/dev/work/mac-command-bar'
  }
];

export const projectRoots = defaultProjectRoots;

export function normalizeProjectPath(path: string): string {
  return path.trim().replace(/\/+$/, '');
}

export function projectRootNameFromPath(path: string): string {
  const normalizedPath = normalizeProjectPath(path);
  return normalizedPath.split('/').filter(Boolean).at(-1) ?? 'Project';
}

export function createProjectRoot(name: string, path: string): ProjectRoot {
  const normalizedPath = normalizeProjectPath(path);
  return {
    id: `custom-${stableIDFor(normalizedPath)}`,
    name: name.trim() || projectRootNameFromPath(normalizedPath),
    path: normalizedPath
  };
}

export function formatSourceContextRootLabel(path: string): string {
  const normalizedPath = normalizeProjectPath(path);
  const segments = normalizedPath.split('/').filter(Boolean);
  const worktreesIndex = segments.findIndex((segment) => segment === 'worktrees');

  if (worktreesIndex >= 0 && segments[worktreesIndex + 2]) {
    const worktreeName = segments[worktreesIndex + 2];
    const nestedPath = segments.slice(worktreesIndex + 3).join('/');
    return nestedPath ? `worktree:${worktreeName}/${nestedPath}` : `worktree:${worktreeName}`;
  }

  const devWorkIndex = segments.findIndex(
    (segment, index) => segment === 'work' && segments[index - 1] === 'dev'
  );
  if (devWorkIndex >= 0 && segments[devWorkIndex + 1]) {
    const repositoryName = segments[devWorkIndex + 1];
    const nestedPath = segments.slice(devWorkIndex + 2).join('/');
    return nestedPath ? `nested:${repositoryName}/${nestedPath}` : 'main checkout';
  }

  return projectRootNameFromPath(normalizedPath);
}

export function formatSourceContextRuntime(runtime: string): string {
  return runtime.trim() || 'browser preview';
}

export function formatSourceContextGitSummary(
  status: SourceContextGitStatus | null,
  loadingGit: boolean,
  gitError: string
): string {
  if (loadingGit) return 'git loading';
  if (!status) return gitError ? 'git unavailable' : 'git clean';

  const branch = status.branch ?? 'detached';
  const syncParts = [
    status.ahead > 0 ? `ahead ${status.ahead}` : '',
    status.behind > 0 ? `behind ${status.behind}` : ''
  ].filter(Boolean);
  const changeLabel =
    status.files.length === 0
      ? 'clean'
      : `${status.files.length} change${status.files.length === 1 ? '' : 's'}`;

  return [branch, ...syncParts, changeLabel].join(' · ');
}

export function formatGitBranchHealthSummary(input: GitBranchHealthInput): GitBranchHealthSummary {
  const branch = input.branch?.trim() || 'detached';
  const ahead = safeGitCount(input.ahead);
  const behind = safeGitCount(input.behind);
  const stagedCount = nullableGitCount(input.stagedCount);
  const unstagedCount = nullableGitCount(input.unstagedCount);
  const untrackedCount = nullableGitCount(input.untrackedCount);
  const hasDetailedDirtyCounts =
    stagedCount !== null || unstagedCount !== null || untrackedCount !== null;
  const detailedDirtyCount =
    safeGitCount(stagedCount) + safeGitCount(unstagedCount) + safeGitCount(untrackedCount);
  const fallbackDirtyCount = safeGitCount(input.dirtyCount ?? input.changedCount);
  const dirtyCount = hasDetailedDirtyCounts ? detailedDirtyCount : fallbackDirtyCount;
  const hasDirtyState = Boolean(input.isDirty) || dirtyCount > 0;
  const error = input.error?.trim() || '';
  const syncParts = [
    ahead > 0 ? `ahead ${ahead}` : '',
    behind > 0 ? `behind ${behind}` : ''
  ].filter(Boolean);
  const sync = syncParts.length > 0 ? syncParts.join(' / ') : 'up to date';
  const dirty = formatGitDirtyHealthLabel({
    error,
    hasDetailedDirtyCounts,
    stagedCount: stagedCount ?? 0,
    unstagedCount: unstagedCount ?? 0,
    untrackedCount: untrackedCount ?? 0,
    dirtyCount,
    hasDirtyState
  });
  const tone: GitBranchHealthTone = error
    ? 'error'
    : hasDirtyState
      ? 'dirty'
      : ahead > 0 || behind > 0
        ? 'warning'
        : 'clean';
  const syncTone: GitBranchHealthTone = ahead > 0 || behind > 0 ? 'warning' : 'clean';
  const dirtyTone: GitBranchHealthTone = error ? 'error' : hasDirtyState ? 'dirty' : 'clean';
  const rootLabel = input.rootLabel?.trim() || '';
  const lastCommitSha = input.lastCommitSha?.trim() || '';
  const chips: GitBranchHealthChip[] = [
    { label: 'Branch', value: branch, tone },
    { label: 'Sync', value: sync, tone: syncTone },
    { label: 'Worktree', value: dirty, tone: dirtyTone },
    rootLabel ? { label: 'Root', value: rootLabel, tone: 'muted' } : null,
    lastCommitSha ? { label: 'Head', value: lastCommitSha, tone: 'muted' } : null
  ].filter((chip): chip is GitBranchHealthChip => Boolean(chip));

  return {
    branch,
    sync,
    dirty,
    detail: [rootLabel, branch, sync, dirty, lastCommitSha].filter(Boolean).join(' · '),
    tone,
    chips
  };
}

function formatGitDirtyHealthLabel(input: {
  error: string;
  hasDetailedDirtyCounts: boolean;
  stagedCount: number;
  unstagedCount: number;
  untrackedCount: number;
  dirtyCount: number;
  hasDirtyState: boolean;
}) {
  if (input.error) return 'error';
  if (!input.hasDirtyState) return 'clean';

  if (input.hasDetailedDirtyCounts) {
    const parts = [
      input.stagedCount > 0 ? `staged ${input.stagedCount}` : '',
      input.unstagedCount > 0 ? `unstaged ${input.unstagedCount}` : '',
      input.untrackedCount > 0 ? `untracked ${input.untrackedCount}` : ''
    ].filter(Boolean);

    if (parts.length > 0) return parts.join(' / ');
  }

  if (input.dirtyCount > 0) {
    return `${input.dirtyCount} change${input.dirtyCount === 1 ? '' : 's'}`;
  }

  return 'dirty';
}

function nullableGitCount(value: number | null | undefined): number | null {
  if (value === null || value === undefined) return null;
  return safeGitCount(value);
}

function safeGitCount(value: number | null | undefined): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return 0;
  return Math.max(0, Math.floor(value));
}

export function formatSourceContextIdentity(
  project: ProjectRoot,
  gitSummary: string,
  runtime: string
): SourceContextIdentity {
  const projectName = project.name.trim() || projectRootNameFromPath(project.path);
  const rootPath = normalizeProjectPath(project.path);
  const rootLabel = formatSourceContextRootLabel(rootPath);
  const normalizedGitSummary = gitSummary.trim() || 'git clean';
  const normalizedRuntime = formatSourceContextRuntime(runtime);

  return {
    projectName,
    rootLabel,
    rootPath,
    gitSummary: normalizedGitSummary,
    runtime: normalizedRuntime,
    summary: [projectName, rootLabel, normalizedGitSummary, normalizedRuntime].join(' · ')
  };
}

export function mergeProjectRoots(defaultRoots: ProjectRoot[], customRoots: ProjectRoot[]): ProjectRoot[] {
  const seenPaths = new Set<string>();
  const mergedRoots: ProjectRoot[] = [];

  for (const root of [...defaultRoots, ...customRoots]) {
    const normalizedPath = normalizeProjectPath(root.path);
    if (!root.id || !root.name.trim() || !normalizedPath || seenPaths.has(normalizedPath)) {
      continue;
    }

    seenPaths.add(normalizedPath);
    mergedRoots.push({
      ...root,
      name: root.name.trim(),
      path: normalizedPath
    });
  }

  return mergedRoots;
}

function stableIDFor(value: string): string {
  let hash = 5381;
  for (const char of value) {
    hash = (hash * 33) ^ char.charCodeAt(0);
  }
  return (hash >>> 0).toString(36);
}

const formatResolverContent = String.raw`using EdiPlatform.Core.Entities;
using EdiPlatform.Core.Models.RetailerRuntime;
using Microsoft.Extensions.Logging;

namespace EdiPlatform.Core.Services;

/// <summary>
/// Resolves which EDI format to use for a file, considering trading partner
/// profile settings and runtime package metadata.
/// </summary>
public class FormatResolver
{
    private readonly FormatDetector _detector;
    private readonly ILogger<FormatResolver>? _logger;

    public FormatResolver(FormatDetector detector, ILogger<FormatResolver>? logger = null)
    {
        _detector = detector;
        _logger = logger;
    }

    public async Task<ResolvedFormat> ResolveAsync(
        Stream source,
        TradingPartnerProfile profile,
        CancellationToken cancellationToken = default)
    {
        var detection = await _detector.DetectAsync(source, cancellationToken);
        if (detection.DocumentType is null)
        {
            _logger?.LogWarning("Could not detect document type for {Partner}", profile.Name);
            return ResolvedFormat.Unknown(profile.Id);
        }

        var package = profile.RuntimePackages
            .FirstOrDefault(item => item.DocumentType == detection.DocumentType);

        return package is null
            ? ResolvedFormat.Unsupported(profile.Id, detection.DocumentType)
            : ResolvedFormat.Runtime(profile.Id, package.PackageId, detection.DocumentType);
    }
}`;

const formatDetectorContent = String.raw`using System.Buffers.Text;
using System.Text;

namespace EdiPlatform.Core.Services;

public sealed class FormatDetector
{
    private static readonly byte[] IsaMarker = "ISA"u8.ToArray();
    private static readonly byte[] StMarker = "ST"u8.ToArray();

    public async Task<FormatDetection> DetectAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[Math.Min(source.Length, 16_384)];
        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        var slice = buffer.AsSpan(0, read);

        if (slice.StartsWith(IsaMarker))
        {
            return DetectX12(slice);
        }

        return FormatDetection.Unknown();
    }

    private static FormatDetection DetectX12(ReadOnlySpan<byte> source)
    {
        var text = Encoding.ASCII.GetString(source);
        var segments = text.Split('~', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (!segment.StartsWith("ST*", StringComparison.Ordinal))
            {
                continue;
            }

            var elements = segment.Split('*');
            return elements.Length > 1
                ? FormatDetection.X12(elements[1])
                : FormatDetection.Unknown();
        }

        return FormatDetection.Unknown();
    }
}`;

const coreClientContent = String.raw`public struct CoreClient: CoreSending {
    public var executableURL: URL

    public init(executableURL: URL) {
        self.executableURL = executableURL
    }

    public func send(_ request: CoreRequest) async throws -> CoreResponse {
        try await Task.detached(priority: .utility) {
            try Self.sendSync(request, executableURL: executableURL)
        }.value
    }
}`;

const sourceIntelligenceContent = String.raw`export type SourceDiagnostic = {
  severity: 'error' | 'warning' | 'info' | 'hint';
  message: string;
  line: number;
  column: number;
};

export class SourceIntelligenceIndex {
  private diagnostics = new Map<string, SourceDiagnostic[]>();

  record(path: string, diagnostics: SourceDiagnostic[]) {
    this.diagnostics.set(path, diagnostics);
  }

  problemsFor(path: string) {
    return this.diagnostics.get(path) ?? [];
  }
}

export function formatDiagnosticCount(diagnostics: SourceDiagnostic[]) {
  return diagnostics.length === 0 ? 'No problems' : diagnostics.length + ' problems';
}`;

export const sourceRecords: SourceRecord[] = [
  {
    path: '/Users/blackcolours/dev/work/EdiPlatform/EdiPlatform.Core/Services/FormatResolver.cs',
    relativePath: 'EdiPlatform.Core/Services/FormatResolver.cs',
    fileName: 'FormatResolver.cs',
    language: 'csharp',
    byteCount: 8037
  },
  {
    path: '/Users/blackcolours/dev/work/EdiPlatform/EdiPlatform.Core/Services/FormatDetector.cs',
    relativePath: 'EdiPlatform.Core/Services/FormatDetector.cs',
    fileName: 'FormatDetector.cs',
    language: 'csharp',
    byteCount: 12047
  },
  {
    path: '/Users/blackcolours/dev/work/mac-command-bar/Sources/MacCommandBarKit/CoreClient.swift',
    relativePath: 'Sources/MacCommandBarKit/CoreClient.swift',
    fileName: 'CoreClient.swift',
    language: 'swift',
    byteCount: 2900
  },
  {
    path: '/Users/blackcolours/dev/work/mac-command-bar/tauri-svelte-preview/src/lib/sourceIntelligence.ts',
    relativePath: 'tauri-svelte-preview/src/lib/sourceIntelligence.ts',
    fileName: 'sourceIntelligence.ts',
    language: 'typescript',
    byteCount: 868
  }
];

const demoContentByPath = new Map<string, string>([
  [sourceRecords[0].path, formatResolverContent],
  [sourceRecords[1].path, formatDetectorContent],
  [sourceRecords[2].path, coreClientContent],
  [sourceRecords[3].path, sourceIntelligenceContent]
]);

export function demoPreviewFor(record: SourceRecord): SourcePreview {
  const content = demoContentByPath.get(record.path) ?? '';
  return previewFromContent(record, content);
}

export function demoRecordsForProject(project: ProjectRoot): SourceRecord[] {
  const matches = sourceRecords.filter((record) => record.path.startsWith(`${project.path}/`));
  return matches.length > 0 ? matches : sourceRecords;
}

export function previewFromContent(record: SourceRecord, content: string): SourcePreview {
  return {
    ...record,
    content,
    lineCount: content.length === 0 ? 0 : content.split(/\r\n|\r|\n/).length,
    revision: `demo:${content.length}`
  };
}

export function formatSourceRecordCount(
  filteredCount: number,
  totalCount: number,
  truncated: boolean
): string {
  const totalLabel = `${formatCount(totalCount)}${truncated ? '+' : ''}`;
  return filteredCount === totalCount ? totalLabel : `${formatCount(filteredCount)} / ${totalLabel}`;
}

export function formatSourceScanSummary(
  filteredCount: number,
  totalCount: number,
  truncated: boolean,
  query: string
): string {
  const safeFilteredCount = Math.max(0, Math.floor(filteredCount));
  const safeTotalCount = Math.max(0, Math.floor(totalCount));
  const normalizedQuery = query.trim();
  const totalScope = truncated
    ? `first ${formatCount(safeTotalCount)} files`
    : `${formatCount(safeTotalCount)} ${safeTotalCount === 1 ? 'file' : 'files'}`;

  if (normalizedQuery.length === 0) {
    return truncated
      ? `${formatCount(safeTotalCount)} indexed files; scan limit reached`
      : `${formatCount(safeTotalCount)} indexed ${safeTotalCount === 1 ? 'file' : 'files'}`;
  }

  return `${formatCount(safeFilteredCount)} ${
    safeFilteredCount === 1 ? 'match' : 'matches'
  } for "${normalizedQuery}" across ${totalScope}`;
}

export function formatSourceScanStats(stats: SourceScanStats | null | undefined): string {
  if (!stats) return '';

  const visitedEntries = sourceScanMetric(stats, 'visitedEntries', 'visitedEntryCount');
  const matchedFiles = sourceScanMetric(stats, 'matchedFiles', 'matchedFileCount');
  const skippedDirectories = sourceScanMetric(
    stats,
    'skippedDirectories',
    'skippedDirectoryCount'
  );
  const unsupportedFiles = sourceScanMetric(stats, 'unsupportedFiles', 'unsupportedFileCount');
  const unreadableEntries = sourceScanMetric(stats, 'unreadableEntries', 'unreadableEntryCount');
  const returnedFiles = optionalSourceScanMetric(stats.returnedFiles ?? stats.returnedCount);
  const collectionLimit = optionalSourceScanMetric(stats.collectionLimit);
  const skippedSampleSummary = sourceScanSkippedDirectorySampleSummary(stats);
  const parts = [];

  if (returnedFiles !== null) {
    parts.push(`${formatCount(returnedFiles)} returned / ${formatCount(matchedFiles)} matched`);
  } else if (matchedFiles > 0) {
    parts.push(`${formatCount(matchedFiles)} matched`);
  }

  parts.push(`${formatCount(visitedEntries)} entries checked`);

  if (skippedDirectories > 0) {
    const label = `${formatCount(skippedDirectories)} ${
      skippedDirectories === 1 ? 'dir' : 'dirs'
    } skipped`;
    parts.push(skippedSampleSummary ? `${label} (${skippedSampleSummary})` : label);
  }
  if (unsupportedFiles > 0) {
    parts.push(`${formatCount(unsupportedFiles)} unsupported`);
  }
  if (unreadableEntries > 0) {
    parts.push(`${formatCount(unreadableEntries)} unreadable`);
  }
  if (collectionLimit !== null) {
    parts.push(
      stats.collectionLimitReached
        ? `collection cap ${formatCount(collectionLimit)} reached`
        : `collection cap ${formatCount(collectionLimit)}`
    );
  }

  return parts.join(' · ');
}

function sourceScanMetric(
  stats: SourceScanStats,
  primaryKey: keyof SourceScanStats,
  fallbackKey: keyof SourceScanStats
): number {
  const primaryValue = stats[primaryKey];
  if (typeof primaryValue === 'number' && Number.isFinite(primaryValue)) {
    return normalSourceScanMetric(primaryValue);
  }

  const fallbackValue = stats[fallbackKey];
  if (typeof fallbackValue === 'number' && Number.isFinite(fallbackValue)) {
    return normalSourceScanMetric(fallbackValue);
  }

  return 0;
}

function normalSourceScanMetric(value: number | null | undefined): number {
  return Math.max(0, Math.floor(typeof value === 'number' && Number.isFinite(value) ? value : 0));
}

function optionalSourceScanMetric(value: number | null | undefined): number | null {
  if (typeof value !== 'number' || !Number.isFinite(value)) return null;
  return normalSourceScanMetric(value);
}

export function formatSourceScanHealth(input: SourceScanHealthInput): SourceScanHealth {
  const totalCount = Math.max(0, Math.floor(input.totalCount));
  const filteredCount = Math.max(0, Math.floor(input.filteredCount));
  const requestedLimit = Math.max(0, Math.floor(input.requestedLimit));
  const query = input.query.trim();
  const error = input.error.trim();

  if (input.scanning) {
    return {
      status: 'scanning',
      needsAttention: false,
      summary: `Scanning source files: ${formatCount(totalCount)} indexed so far`,
      action: null
    };
  }

  if (input.loading) {
    return {
      status: 'loading',
      needsAttention: false,
      summary: 'Loading source index',
      action: null
    };
  }

  if (error) {
    return {
      status: 'error',
      needsAttention: true,
      summary: `Scan failed: ${error}`,
      action: 'Check root'
    };
  }

  if (query) {
    return {
      status: 'filtered',
      needsAttention: false,
      summary: `${formatCount(filteredCount)} ${filteredCount === 1 ? 'match' : 'matches'} for "${query}"`,
      action: null
    };
  }

  if (
    isSuspiciousSourceScanResult(
      totalCount,
      input.truncated,
      requestedLimit,
      input.suspiciousThreshold
    )
  ) {
    return {
      status: 'suspicious',
      needsAttention: true,
      summary: `Only ${formatCount(totalCount)} ${
        totalCount === 1 ? 'file' : 'files'
      } indexed from a ${formatCount(requestedLimit)}-file scan. Confirm the project root is the repository root, then reset the index.`,
      action: 'Reset index'
    };
  }

  if (input.truncated) {
    return {
      status: 'truncated',
      needsAttention: false,
      summary: `Scan limit reached after ${formatCount(totalCount)} files`,
      action: 'Scan more'
    };
  }

  if (totalCount === 0) {
    return {
      status: 'empty',
      needsAttention: false,
      summary: 'No source files indexed yet',
      action: null
    };
  }

  return {
    status: 'ready',
    needsAttention: false,
    summary: `Index healthy: ${formatCount(totalCount)} ${totalCount === 1 ? 'file' : 'files'} indexed`,
    action: null
  };
}

export function formatSourceScanRecovery(input: SourceScanRecoveryInput): SourceScanRecovery {
  const totalCount = Math.max(0, Math.floor(input.totalCount));
  const requestedLimit = Math.max(0, Math.floor(input.requestedLimit));
  const query = input.query.trim();
  const error = input.error.trim();
  const statsSummary = sourceScanRecoveryStatsSummary(input.stats);

  if (input.scanning || input.loading || query || input.truncated) {
    return emptySourceScanRecovery();
  }

  if (error) {
    return {
      visible: true,
      title: 'Scan failed',
      detail: `${error}. Check folder access or choose the correct project root. ${statsSummary}`,
      primaryAction: 'choose-root',
      secondaryAction: 'copy-diagnostic'
    };
  }

  if (
    isSuspiciousSourceScanResult(
      totalCount,
      input.truncated,
      requestedLimit,
      input.suspiciousThreshold
    )
  ) {
    return {
      visible: true,
      title: 'Tiny source index',
      detail: `Only ${formatCount(totalCount)} ${
        totalCount === 1 ? 'file was' : 'files were'
      } indexed from a ${formatCount(requestedLimit)}-file scan. Reset the index; if it stays tiny, choose the repo or worktree root. ${statsSummary}`,
      primaryAction: 'reset-index',
      secondaryAction: 'copy-diagnostic'
    };
  }

  if (totalCount === 0) {
    return {
      visible: true,
      title: 'No source files indexed',
      detail: `Choose the repo or worktree root, then scan again. ${statsSummary}`,
      primaryAction: 'choose-root',
      secondaryAction: 'copy-diagnostic'
    };
  }

  return emptySourceScanRecovery();
}

function emptySourceScanRecovery(): SourceScanRecovery {
  return {
    visible: false,
    title: '',
    detail: '',
    primaryAction: null,
    secondaryAction: null
  };
}

function sourceScanRecoveryStatsSummary(stats: SourceScanStats | null | undefined): string {
  if (!stats) return 'No native scan stats are available yet.';

  const parts = [
    `${formatCount(sourceScanMetric(stats, 'visitedEntries', 'visitedEntryCount'))} entries checked`,
    `${formatCount(sourceScanMetric(stats, 'unsupportedFiles', 'unsupportedFileCount'))} unsupported`,
    `${formatCount(sourceScanMetric(stats, 'skippedDirectories', 'skippedDirectoryCount'))} dirs skipped`
  ];
  const unreadableEntries = sourceScanMetric(stats, 'unreadableEntries', 'unreadableEntryCount');
  if (unreadableEntries > 0) parts.push(`${formatCount(unreadableEntries)} unreadable`);
  const skippedSampleSummary = sourceScanSkippedDirectorySampleSummary(stats);
  if (skippedSampleSummary) parts.push(`skipped samples: ${skippedSampleSummary}`);
  return parts.join(' · ');
}

function sourceScanSkippedDirectorySampleSummary(stats: SourceScanStats, limit = 3): string {
  return formatSourceSkippedDirectorySamples(sourceScanSkippedDirectorySamples(stats), limit);
}

export function formatSourceSkippedDirectorySamples(
  skippedDirectories: SourceSkippedDirectory[] | null | undefined,
  limit = 3
): string {
  const sampleLimit = Math.max(0, Math.floor(limit));
  if (sampleLimit === 0) return '';

  const samples = (skippedDirectories ?? [])
    .filter((sample) => sample.name.trim() && sample.reason.trim())
    .slice(0, sampleLimit)
    .map((sample) => `${sample.name}: ${sample.reason}`);

  if (samples.length === 0) return '';
  const remainingSampleCount = Math.max(0, (skippedDirectories?.length ?? 0) - samples.length);
  return remainingSampleCount > 0 ? `${samples.join('; ')}; +${remainingSampleCount} more` : samples.join('; ');
}

function sourceScanSkippedDirectorySamples(stats: SourceScanStats): SourceSkippedDirectory[] {
  if (Array.isArray(stats.skippedDirectorySamples)) {
    return stats.skippedDirectorySamples;
  }

  const diagnosticSkippedDirectories = (stats as { skippedDirectories?: unknown }).skippedDirectories;
  return Array.isArray(diagnosticSkippedDirectories)
    ? diagnosticSkippedDirectories.filter(isSourceSkippedDirectory)
    : [];
}

function isSourceSkippedDirectory(value: unknown): value is SourceSkippedDirectory {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as SourceSkippedDirectory;
  return (
    typeof candidate.path === 'string' &&
    typeof candidate.name === 'string' &&
    typeof candidate.reason === 'string'
  );
}

export function selectBackgroundIndexProjects(
  projects: ProjectRoot[],
  activeProjectID: string,
  cache: SourceScanCache,
  now: number,
  maxAgeMs: number,
  limit: number,
  suspiciousThreshold = 0,
  sourceSignatureForProject?: (project: ProjectRoot) => string | null
): ProjectRoot[] {
  return projects.filter(
    (project) => {
      if (project.id === activeProjectID) return false;

      const sourceSignature = sourceSignatureForProject?.(project) ?? null;
      if (sourceSignatureForProject && !sourceSignature) return true;

      const cacheEntry = getSourceScanCacheEntry(cache, project, limit, now, maxAgeMs, sourceSignature);
      return (
        cacheEntry === null ||
        sourceScanCacheEntryNeedsRepair(cacheEntry, limit, suspiciousThreshold)
      );
    }
  );
}

export function formatSourceIndexSummary(
  entry: SourceScanCacheEntry | null,
  indexing: boolean,
  error: string
): string {
  if (indexing) return 'Indexing in background';

  const normalizedError = error.trim();
  if (normalizedError) return `Index failed: ${normalizedError}`;

  if (!entry) return 'Index not ready';

  return `Index ready: ${formatCount(entry.records.length)}${entry.truncated ? '+' : ''} ${
    entry.records.length === 1 && !entry.truncated ? 'file' : 'files'
  }`;
}

export function formatSourceScanEvidence(input: SourceScanEvidenceInput): SourceScanEvidence {
  const rootLabel = formatSourceContextRootLabel(input.project.path);
  const rootPath = normalizeProjectPath(input.project.path);
  const requestedLimit = Math.max(0, Math.floor(input.requestedLimit));
  const limitLabel = `${formatCount(requestedLimit)} limit`;
  const error = input.error.trim();
  const countLabel = input.entry
    ? `${formatCount(input.entry.records.length)}${input.entry.truncated ? '+' : ''} ${
        input.entry.records.length === 1 && !input.entry.truncated ? 'file' : 'files'
      }`
    : 'no index';
  const ageLabel = input.entry ? formatSourceScanAge(input.entry.scannedAt, input.now ?? Date.now()) : '';
  const cacheDetail = input.entry
    ? `${countLabel} · ${ageLabel} · scanned ${input.entry.projectPath} · ${limitLabel}`
    : `${rootPath} · ${limitLabel}`;

  if (error || input.mode === 'failed') {
    return {
      label: `Scan failed · ${rootLabel} · ${limitLabel}`,
      detail: [error || 'The source scan failed', rootPath, countLabel].join(' · '),
      tone: 'error'
    };
  }

  if (input.scanning) {
    return {
      label: `Scanning · ${rootLabel} · ${limitLabel}`,
      detail: `Native source scan running for ${rootPath}`,
      tone: 'active'
    };
  }

  if (input.mode === 'repair') {
    return {
      label: `Rebuilding tiny index · ${rootLabel} · ${limitLabel}`,
      detail: `The previous source index was too small. Rebuilding ${rootPath}.`,
      tone: 'warning'
    };
  }

  if (input.mode === 'tiny') {
    return {
      label: `Tiny index · ${rootLabel} · ${countLabel}`,
      detail: cacheDetail,
      tone: 'warning'
    };
  }

  if (input.mode === 'stopped') {
    return {
      label: `Scan stopped · ${rootLabel} · ${countLabel}`,
      detail: cacheDetail,
      tone: input.entry ? 'muted' : 'warning'
    };
  }

  if (input.mode === 'cache' && input.entry) {
    return {
      label: `Cached index · ${ageLabel} · ${countLabel}`,
      detail: cacheDetail,
      tone: 'ready'
    };
  }

  if (input.mode === 'native' && input.entry) {
    return {
      label: `Fresh index · ${ageLabel} · ${countLabel}`,
      detail: cacheDetail,
      tone: 'ready'
    };
  }

  if (input.mode === 'background' && input.entry) {
    return {
      label: `Background index · ${ageLabel} · ${countLabel}`,
      detail: cacheDetail,
      tone: 'ready'
    };
  }

  if (input.entry) {
    return {
      label: `Index ready · ${ageLabel} · ${countLabel}`,
      detail: cacheDetail,
      tone: 'ready'
    };
  }

  if (input.loading) {
    return {
      label: `Loading index · ${rootLabel} · ${limitLabel}`,
      detail: rootPath,
      tone: 'active'
    };
  }

  return {
    label: `No index · ${rootLabel} · ${limitLabel}`,
    detail: rootPath,
    tone: 'muted'
  };
}

export function isSuspiciousSourceScanResult(
  totalCount: number,
  truncated: boolean,
  requestedLimit: number,
  suspiciousThreshold: number
): boolean {
  const safeTotalCount = Math.max(0, Math.floor(totalCount));
  const safeRequestedLimit = Math.max(0, Math.floor(requestedLimit));
  const safeSuspiciousThreshold = Math.max(0, Math.floor(suspiciousThreshold));

  return (
    !truncated &&
    safeTotalCount > 0 &&
    safeTotalCount <= safeSuspiciousThreshold &&
    safeRequestedLimit > safeSuspiciousThreshold
  );
}

export function shouldRepairSuspiciousSourceScan(
  totalCount: number,
  truncated: boolean,
  requestedLimit: number,
  suspiciousThreshold: number
): boolean {
  return isSuspiciousSourceScanResult(totalCount, truncated, requestedLimit, suspiciousThreshold);
}

export function sourceScanCacheEntryNeedsRepair(
  entry: SourceScanCacheEntry,
  requestedLimit: number,
  suspiciousThreshold: number
): boolean {
  return isSuspiciousSourceScanResult(
    entry.records.length,
    entry.truncated,
    Math.max(entry.limit, requestedLimit),
    suspiciousThreshold
  );
}

export function buildProjectActivationScanPlan(
  input: ProjectActivationScanPlanInput
): ProjectActivationScanPlan {
  const limit = Math.max(0, Math.floor(input.limit));
  const limitLabel = `${formatCount(limit)} source files`;
  const detailPrefix = `${normalizeProjectPath(input.project.path)} · ${formatCount(limit)} limit`;
  const cacheRecords = input.entry?.records.length ?? 0;

  if (input.forceScan) {
    return {
      shouldScan: true,
      reason: 'force',
      status: `Rebuilding ${input.project.name} index up to ${limitLabel}`,
      detail: `${detailPrefix} · forced rebuild`,
      cacheRecords
    };
  }

  if (!input.entry) {
    return {
      shouldScan: true,
      reason: 'missing-or-stale',
      status: `Auto-scanning ${input.project.name} up to ${limitLabel}`,
      detail: `${detailPrefix} · cache missing or stale`,
      cacheRecords: 0
    };
  }

  if (sourceScanCacheEntryNeedsRepair(input.entry, limit, input.suspiciousThreshold)) {
    return {
      shouldScan: true,
      reason: 'repair',
      status: `Repairing tiny ${input.project.name} index: ${formatCount(cacheRecords)} ${
        cacheRecords === 1 ? 'file' : 'files'
      } from a ${formatCount(Math.max(input.entry.limit, limit))}-file scan`,
      detail: `${detailPrefix} · previous index looked incomplete`,
      cacheRecords
    };
  }

  return {
    shouldScan: false,
    reason: 'cache',
    status: `Using cached index for ${input.project.name}: ${formatCount(cacheRecords)} ${
      cacheRecords === 1 ? 'file' : 'files'
    }`,
    detail: `${detailPrefix} · ${formatSourceScanAge(input.entry.scannedAt, input.now ?? Date.now())}`,
    cacheRecords
  };
}

export function monacoLanguageForSource(language: SourceLanguage): string {
  switch (language) {
    case 'tsx':
      return 'typescript';
    case 'jsx':
      return 'javascript';
    case 'svelte':
      return 'svelte';
    case 'shell':
      return 'shellscript';
    case 'toml':
      return 'ini';
    case 'plain':
      return 'plaintext';
    default:
      return language;
  }
}

export function sourceLanguageForPath(path: string): SourceLanguage {
  const extension = path.split('/').filter(Boolean).at(-1)?.split('.').at(-1)?.toLowerCase() ?? '';
  switch (extension) {
    case 'cs':
      return 'csharp';
    case 'ts':
      return 'typescript';
    case 'tsx':
      return 'tsx';
    case 'js':
      return 'javascript';
    case 'jsx':
      return 'jsx';
    case 'svelte':
      return 'svelte';
    case 'html':
    case 'htm':
      return 'html';
    case 'css':
      return 'css';
    case 'json':
      return 'json';
    case 'md':
    case 'mdx':
      return 'markdown';
    case 'rs':
      return 'rust';
    case 'swift':
      return 'swift';
    case 'py':
      return 'python';
    case 'go':
      return 'go';
    case 'java':
      return 'java';
    case 'kt':
    case 'kts':
      return 'kotlin';
    case 'rb':
      return 'ruby';
    case 'php':
      return 'php';
    case 'sh':
    case 'bash':
    case 'zsh':
      return 'shell';
    case 'sql':
      return 'sql';
    case 'yaml':
    case 'yml':
      return 'yaml';
    case 'xml':
      return 'xml';
    case 'toml':
      return 'toml';
    default:
      return 'plain';
  }
}

export function sourceSupportsLanguageIntelligence(language: SourceLanguage): boolean {
  return ['typescript', 'tsx', 'javascript', 'jsx', 'csharp', 'rust', 'svelte'].includes(language);
}

export function applySourceTextEdits(content: string, edits: SourceTextEdit[]): string {
  return [...edits]
    .sort(compareSourceTextEditsDescending)
    .reduce((nextContent, edit) => {
      const startOffset = sourceOffsetForLineColumn(nextContent, edit.startLine, edit.startColumn);
      const endOffset = sourceOffsetForLineColumn(nextContent, edit.endLine, edit.endColumn);
      if (startOffset > endOffset) return nextContent;

      return `${nextContent.slice(0, startOffset)}${edit.newText}${nextContent.slice(endOffset)}`;
    }, content);
}

function compareSourceTextEditsDescending(left: SourceTextEdit, right: SourceTextEdit): number {
  if (left.startLine !== right.startLine) return right.startLine - left.startLine;
  if (left.startColumn !== right.startColumn) return right.startColumn - left.startColumn;
  if (left.endLine !== right.endLine) return right.endLine - left.endLine;
  return right.endColumn - left.endColumn;
}

function sourceOffsetForLineColumn(content: string, line: number, column: number): number {
  const targetLine = Math.max(1, Math.floor(line));
  const targetColumn = Math.max(1, Math.floor(column));
  let currentLine = 1;
  let lineStartOffset = 0;

  while (currentLine < targetLine) {
    const nextNewlineOffset = content.indexOf('\n', lineStartOffset);
    if (nextNewlineOffset < 0) return content.length;
    lineStartOffset = nextNewlineOffset + 1;
    currentLine += 1;
  }

  const nextNewlineOffset = content.indexOf('\n', lineStartOffset);
  const lineEndOffset = nextNewlineOffset < 0 ? content.length : nextNewlineOffset;
  return Math.min(lineStartOffset + targetColumn - 1, lineEndOffset);
}

export function textMatchesSearchTokens(
  filter: string,
  ...values: Array<string | number | boolean | null | undefined>
): boolean {
  const tokens = filter
    .trim()
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean);
  if (tokens.length === 0) return true;

  const haystack = values.map((value) => String(value ?? '').toLowerCase()).join(' ');
  return tokens.every((token) => haystack.includes(token));
}

export function gitRefLabels(refs: string): string[] {
  return refs
    .split(',')
    .map((ref) => ref.trim())
    .filter(Boolean);
}

export function gitCommitGraphKind(refs: string, _index: number, parentCount = 1): GitCommitGraphKind {
  const labels = gitRefLabels(refs);
  if (labels.some((label) => label === 'HEAD' || label.startsWith('HEAD ->'))) return 'head';
  if (parentCount > 1) return 'merge';
  if (labels.length > 0) return 'branch';
  if (parentCount === 0) return 'root';
  return 'commit';
}

export function gitCommitTopologyLabel(refs: string, index: number, parentCount = 1): string {
  const kind = gitCommitGraphKind(refs, index, parentCount);

  switch (kind) {
    case 'head':
      return 'HEAD';
    case 'branch':
      return 'REF';
    case 'merge':
      return 'MERGE';
    case 'root':
      return 'ROOT';
    case 'commit':
    default:
      return 'COMMIT';
  }
}

export function gitCommitOwnershipBadges(input: GitCommitOwnershipBadgeInput): GitCommitOwnershipBadge[] {
  const labels = gitRefLabels(input.refs);
  const badges: GitCommitOwnershipBadge[] = [];
  const headLabels = labels.filter((label) => label === 'HEAD' || label.startsWith('HEAD ->'));
  const upstreamLabels = labels.filter(isGitRemoteRefLabel);
  const tagLabels = labels.filter((label) => label.startsWith('tag:'));
  const branchLabels = labels.filter(
    (label) =>
      !headLabels.includes(label) &&
      !upstreamLabels.includes(label) &&
      !tagLabels.includes(label)
  );
  const normalizedTaskID = normalizeTaskID(input.taskID);
  const parentCount = safeGitCount(input.parentCount ?? 1);

  if (headLabels.length > 0) {
    badges.push({ label: 'HEAD', title: headLabels.join(', '), tone: 'head' });
  }

  if (upstreamLabels.length > 0) {
    badges.push({ label: 'UPSTREAM', title: upstreamLabels.join(', '), tone: 'upstream' });
  }

  if (tagLabels.length > 0) {
    badges.push({ label: 'TAG', title: tagLabels.join(', '), tone: 'tag' });
  } else if (branchLabels.length > 0 && headLabels.length === 0) {
    badges.push({ label: 'BRANCH', title: branchLabels.join(', '), tone: 'branch' });
  }

  if (normalizedTaskID) {
    badges.push({
      label: normalizedTaskID,
      title: gitCommitTaskSourceTitle(input.taskSource),
      tone: 'task'
    });
  }

  if (parentCount > 1) {
    badges.push({ label: 'MERGE', title: `${parentCount} parents`, tone: 'merge' });
  } else if (parentCount === 0) {
    badges.push({ label: 'ROOT', title: 'Root commit', tone: 'root' });
  }

  return badges;
}

function isGitRemoteRefLabel(label: string): boolean {
  if (!label || label === 'HEAD' || label.startsWith('HEAD ->') || label.startsWith('tag:')) return false;
  if (label.startsWith('refs/')) return false;
  return /^[A-Za-z0-9_.-]+\/.+/.test(label);
}

function gitCommitTaskSourceTitle(taskSource: string | null | undefined): string {
  if (taskSource === 'refs') return 'Task from branch/ref';
  if (taskSource === 'subject') return 'Task from subject';
  return 'Task from Git metadata';
}

export function uniqueTaskIDsFromGitMetadata(...groups: GitTaskMetadata[][]): string[] {
  const seen = new Set<string>();
  const taskIDs: string[] = [];

  for (const group of groups) {
    for (const item of group) {
      const taskID = normalizeTaskID(item.taskID);
      if (!taskID || seen.has(taskID)) continue;

      seen.add(taskID);
      taskIDs.push(taskID);
    }
  }

  return taskIDs;
}

export function buildGitTaskSourceGroups(...groups: GitTaskSourceMetadata[][]): GitTaskSourceGroup[] {
  const groupedSources = new Map<string, GitTaskSourceMetadata[]>();

  for (const group of groups) {
    for (const item of group) {
      const taskID = normalizeTaskID(item.taskID);
      if (!taskID) continue;

      const sourceLabel = item.sourceLabel.trim() || 'metadata';
      const sourceDetail = item.sourceDetail?.trim() || null;
      const source = { ...item, taskID, sourceLabel, sourceDetail };
      const sources = groupedSources.get(taskID) ?? [];
      sources.push(source);
      groupedSources.set(taskID, sources);
    }
  }

  return Array.from(groupedSources.entries()).map(([taskID, sources]) => {
    const sourceLabels = Array.from(new Set(sources.map((source) => source.sourceLabel)));
    return {
      taskID,
      sourceSummary: sourceLabels.join(', '),
      detailSummary: sources
        .map((source) =>
          source.sourceDetail ? `${source.sourceLabel}: ${source.sourceDetail}` : source.sourceLabel
        )
        .join(' · '),
      sources
    };
  });
}

export function formatGitTaskSourceGroupHandoff(
  group: GitTaskSourceGroup,
  taskUrl: string | null = null
): string {
  return [
    `Task: ${group.taskID}`,
    taskUrl ? `Task link: ${taskUrl}` : '',
    `Sources: ${group.sourceSummary}`,
    'Details:',
    ...group.sources.map((source) =>
      `- ${source.sourceLabel}${source.sourceDetail ? `: ${source.sourceDetail}` : ''}`
    )
  ].filter(Boolean).join('\n');
}

export function taskReferenceUrl(
  taskID: string | null | undefined,
  knownTaskUrls: Record<string, string>
): string | null {
  const normalized = normalizeTaskID(taskID);
  if (!normalized) return null;

  return knownTaskUrls[normalized] ?? `https://www.notion.so/search?q=${encodeURIComponent(normalized)}`;
}

export function formatSourceDiagnosticSummary(diagnostics: SourceDiagnostic[]): string {
  if (diagnostics.length === 0) return 'No problems';

  const errors = diagnostics.filter((diagnostic) => diagnostic.severity === 'error').length;
  const warnings = diagnostics.filter((diagnostic) => diagnostic.severity === 'warning').length;
  const infos = diagnostics.length - errors - warnings;
  const parts = [
    formatProblemCount(errors, 'error'),
    formatProblemCount(warnings, 'warning'),
    formatProblemCount(infos, 'note')
  ].filter(Boolean);

  return parts.join(', ');
}

export function extractSourceSymbols(preview: SourcePreview, content: string): SourceSymbol[] {
  switch (preview.language) {
    case 'typescript':
    case 'tsx':
    case 'javascript':
    case 'jsx':
      return extractTypeScriptSymbols(content);
    case 'csharp':
      return extractCSharpSymbols(content);
    default:
      return [];
  }
}

export function extractSourceSemanticTokens(
  preview: SourcePreview,
  content: string
): SourceSemanticToken[] {
  const lines = content.split(/\r\n|\r|\n/);

  return extractSourceSymbols(preview, content)
    .map((symbol): SourceSemanticToken | null => {
      const tokenType = semanticTokenTypeForSourceSymbol(symbol.kind);
      if (!tokenType) return null;

      const line = lines[symbol.line - 1] ?? '';
      const startIndex = line.lastIndexOf(symbol.name);
      if (startIndex < 0) return null;

      return {
        tokenType,
        line: symbol.line,
        startColumn: startIndex + 1,
        length: symbol.name.length
      };
    })
    .filter((token): token is SourceSemanticToken => token !== null)
    .sort((left, right) => left.line - right.line || left.startColumn - right.startColumn);
}

function formatProblemCount(count: number, label: string): string {
  if (count === 0) return '';
  return `${count} ${label}${count === 1 ? '' : 's'}`;
}

function semanticTokenTypeForSourceSymbol(kind: string): SourceSemanticTokenType | null {
  switch (kind) {
    case 'namespace':
    case 'class':
    case 'interface':
    case 'type':
    case 'enum':
    case 'function':
    case 'method':
      return kind;
    case 'constructor':
      return 'method';
    case 'property':
      return 'property';
    case 'constant':
      return 'variable';
    case 'variable':
      return 'variable';
    case 'record':
    case 'struct':
      return 'class';
    default:
      return null;
  }
}

function extractTypeScriptSymbols(content: string): SourceSymbol[] {
  const symbols: SourceSymbol[] = [];
  const lines = content.split(/\r\n|\r|\n/);

  lines.forEach((line, index) => {
    const trimmed = line.trim();
    const lineNumber = index + 1;
    const column = line.search(/\S/) + 1 || 1;
    const declaration = /^(?:export\s+)?(?:default\s+)?(?:abstract\s+)?(class|interface|type|enum)\s+([A-Za-z_$][\w$]*)/.exec(trimmed);
    if (declaration) {
      symbols.push({
        kind: declaration[1],
        name: declaration[2],
        line: lineNumber,
        column,
        detail: trimmed
      });
      return;
    }

    const functionDeclaration = /^(?:export\s+)?(?:async\s+)?function\s+([A-Za-z_$][\w$]*)\s*\(/.exec(trimmed);
    if (functionDeclaration) {
      symbols.push({
        kind: 'function',
        name: functionDeclaration[1],
        line: lineNumber,
        column,
        detail: trimmed
      });
      return;
    }

    const constantDeclaration = /^(?:export\s+)?const\s+([A-Za-z_$][\w$]*)\s*=/.exec(trimmed);
    if (constantDeclaration) {
      symbols.push({
        kind: 'constant',
        name: constantDeclaration[1],
        line: lineNumber,
        column,
        detail: trimmed
      });
      return;
    }

    const methodDeclaration =
      /^(?:(?:public|private|protected|static|async|readonly|override)\s+)*([A-Za-z_$][\w$]*)\s*\(/.exec(
        trimmed
      );
    if (methodDeclaration && !isControlFlowKeyword(methodDeclaration[1])) {
      symbols.push({
        kind: 'method',
        name: methodDeclaration[1],
        line: lineNumber,
        column,
        detail: trimmed
      });
    }
  });

  return symbols;
}

function isControlFlowKeyword(value: string) {
  return ['for', 'foreach', 'if', 'switch', 'while', 'catch'].includes(value);
}

function extractCSharpSymbols(content: string): SourceSymbol[] {
  const symbols: SourceSymbol[] = [];
  const lines = content.split(/\r\n|\r|\n/);

  lines.forEach((line, index) => {
    const trimmed = line.trim();
    const lineNumber = index + 1;
    const column = line.search(/\S/) + 1 || 1;
    const namespaceDeclaration = /^namespace\s+([A-Za-z_][\w.]*);?/.exec(trimmed);
    if (namespaceDeclaration) {
      symbols.push({
        kind: 'namespace',
        name: namespaceDeclaration[1],
        line: lineNumber,
        column,
        detail: trimmed
      });
      return;
    }

    const typeDeclaration = /^(?:(?:public|private|protected|internal|sealed|abstract|static|partial)\s+)*(class|interface|record|enum)\s+([A-Za-z_][\w]*)/.exec(trimmed);
    if (typeDeclaration) {
      symbols.push({
        kind: typeDeclaration[1],
        name: typeDeclaration[2],
        line: lineNumber,
        column,
        detail: trimmed
      });
      return;
    }

    const fieldDeclaration =
      /^(?:(?:public|private|protected|internal|static|readonly|const|volatile|required|new)\s+)+[\w<>,.?[\]\s]+\s+([A-Za-z_][\w]*)\s*(?:=|;)/.exec(
        trimmed
      );
    if (fieldDeclaration) {
      symbols.push({
        kind: 'variable',
        name: fieldDeclaration[1],
        line: lineNumber,
        column,
        detail: trimmed
      });
      return;
    }

    const constructorDeclaration =
      /^(?:(?:public|private|protected|internal|static|new)\s+)+([A-Z][A-Za-z_0-9]*)\s*\(/.exec(
        trimmed
      );
    if (constructorDeclaration) {
      symbols.push({
        kind: 'constructor',
        name: constructorDeclaration[1],
        line: lineNumber,
        column,
        detail: trimmed
      });
      return;
    }

    const propertyDeclaration =
      /^(?:(?:public|private|protected|internal|static|virtual|override|sealed|required|new)\s+)+[\w<>,.?[\]\s]+\s+([A-Za-z_][\w]*)\s*\{/.exec(
        trimmed
      );
    if (propertyDeclaration) {
      symbols.push({
        kind: 'property',
        name: propertyDeclaration[1],
        line: lineNumber,
        column,
        detail: trimmed
      });
      return;
    }

    const methodDeclaration = /^(?:(?:public|private|protected|internal|static|async|virtual|override|sealed)\s+)+[\w<>,.?[\]\s]+\s+([A-Za-z_][\w]*)\s*\(/.exec(trimmed);
    if (methodDeclaration) {
      symbols.push({
        kind: 'method',
        name: methodDeclaration[1],
        line: lineNumber,
        column,
        detail: trimmed
      });
    }
  });

  return symbols;
}

export function selectPreferredSourceRecord(
  records: SourceRecord[],
  preferredPath: string | null | undefined,
  fallbackPath: string | null | undefined = null
): SourceRecord | null {
  const preferredRecord = preferredPath
    ? records.find((record) => record.path === preferredPath)
    : null;
  if (preferredRecord) return preferredRecord;

  const fallbackRecord = fallbackPath
    ? records.find((record) => record.path === fallbackPath)
    : null;
  return fallbackRecord ?? records[0] ?? null;
}

export function upsertRecentSourceRecord(
  recents: SourceRecentRecord[],
  record: SourceRecord,
  project: ProjectRoot,
  openedAt = Date.now(),
  limit = 10
): SourceRecentRecord[] {
  const cappedLimit = Math.max(0, Math.floor(limit));
  if (cappedLimit === 0) return [];

  return [
    createProjectSourceRecord(record, project, openedAt),
    ...recents.filter((recentRecord) => recentRecord.path !== record.path)
  ].slice(0, cappedLimit);
}

export function sourceNavigationLocationForRecord(
  record: SourceRecord,
  line: number | null = null
): SourceNavigationLocation {
  return {
    path: record.path,
    line: normalizeSourceNavigationLine(line)
  };
}

export function pushSourceNavigationHistory(
  backStack: SourceNavigationLocation[],
  currentLocation: SourceNavigationLocation | null,
  nextLocation: SourceNavigationLocation | null,
  maxEntries = 50
): SourceNavigationLocation[] {
  if (!currentLocation || sourceNavigationLocationsEqual(currentLocation, nextLocation)) {
    return backStack;
  }

  const nextBackStack = [...backStack, currentLocation];
  return nextBackStack.slice(Math.max(0, nextBackStack.length - Math.max(1, maxEntries)));
}

export function navigateSourceHistoryBack(
  backStack: SourceNavigationLocation[],
  forwardStack: SourceNavigationLocation[],
  currentLocation: SourceNavigationLocation | null
): SourceNavigationHistoryStep {
  const target = backStack.at(-1) ?? null;
  if (!target) {
    return { target: null, backStack, forwardStack };
  }

  return {
    target,
    backStack: backStack.slice(0, -1),
    forwardStack: currentLocation ? [...forwardStack, currentLocation] : forwardStack
  };
}

export function navigateSourceHistoryForward(
  backStack: SourceNavigationLocation[],
  forwardStack: SourceNavigationLocation[],
  currentLocation: SourceNavigationLocation | null
): SourceNavigationHistoryStep {
  const target = forwardStack.at(-1) ?? null;
  if (!target) {
    return { target: null, backStack, forwardStack };
  }

  return {
    target,
    backStack: currentLocation ? [...backStack, currentLocation] : backStack,
    forwardStack: forwardStack.slice(0, -1)
  };
}

export function sourceNavigationLocationsEqual(
  left: SourceNavigationLocation | null,
  right: SourceNavigationLocation | null
) {
  return Boolean(left && right && left.path === right.path && left.line === right.line);
}

function normalizeSourceNavigationLine(line: number | null | undefined) {
  if (line === null || line === undefined || !Number.isFinite(line)) return null;
  return Math.max(1, Math.floor(line));
}

export function getSourceScanCacheEntry(
  cache: SourceScanCache,
  project: ProjectRoot,
  limit: number,
  now = Date.now(),
  maxAgeMs = 5 * 60 * 1000,
  sourceSignature?: string | null
): SourceScanCacheEntry | null {
  const projectPath = normalizeProjectPath(project.path);
  const requestedLimit = Math.max(0, Math.floor(limit));
  const expectedSourceSignature = normalizeSourceScanCacheSignature(sourceSignature);
  const entries = Object.values(cache)
    .filter(
      (entry) =>
        entry.projectPath === projectPath &&
        entry.limit >= requestedLimit &&
        now - entry.scannedAt <= maxAgeMs &&
        sourceScanCacheSignaturesMatch(entry.sourceSignature, expectedSourceSignature)
    )
    .sort((left, right) => left.limit - right.limit || right.scannedAt - left.scannedAt);

  return entries[0] ?? null;
}

export function upsertSourceScanCacheEntry(
  cache: SourceScanCache,
  project: ProjectRoot,
  records: SourceRecord[],
  limit: number,
  scannedAt = Date.now(),
  maxEntries = 8,
  truncated = false,
  stats?: SourceScanStats,
  sourceSignature?: string | null
): SourceScanCache {
  const cappedMaxEntries = Math.max(0, Math.floor(maxEntries));
  if (cappedMaxEntries === 0) return {};

  const key = sourceScanCacheKey(project, limit);
  const normalizedSourceSignature = normalizeSourceScanCacheSignature(sourceSignature);
  const nextEntries = [
    ...Object.values(cache).filter((entry) => entry.key !== key),
    {
      key,
      projectID: project.id,
      projectPath: normalizeProjectPath(project.path),
      ...(normalizedSourceSignature ? { sourceSignature: normalizedSourceSignature } : {}),
      limit,
      truncated,
      records,
      scannedAt,
      stats
    }
  ]
    .sort((left, right) => left.scannedAt - right.scannedAt)
    .slice(-cappedMaxEntries);

  return Object.fromEntries(nextEntries.map((entry) => [entry.key, entry]));
}

export function parseStoredSourceScanCache(
  value: unknown,
  now = Date.now(),
  maxAgeMs = 12 * 60 * 60 * 1000,
  maxEntries = 8
): SourceScanCache {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {};

  const safeNow = normalSourceScanTimestamp(now);
  const safeMaxAgeMs = Math.max(0, Math.floor(maxAgeMs));
  const safeMaxEntries = Math.max(0, Math.floor(maxEntries));
  if (safeMaxEntries === 0) return {};

  const entries = Object.values(value)
    .map(parseStoredSourceScanCacheEntry)
    .filter((entry): entry is SourceScanCacheEntry => {
      if (!entry) return false;
      return safeNow - entry.scannedAt <= safeMaxAgeMs;
    })
    .sort((left, right) => left.scannedAt - right.scannedAt)
    .slice(-safeMaxEntries);

  return Object.fromEntries(entries.map((entry) => [entry.key, entry]));
}

function parseStoredSourceScanCacheEntry(value: unknown): SourceScanCacheEntry | null {
  if (!value || typeof value !== 'object') return null;

  const entry = value as Partial<SourceScanCacheEntry>;
  if (
    typeof entry.projectID !== 'string' ||
    typeof entry.projectPath !== 'string' ||
    typeof entry.sourceSignature !== 'string' ||
    typeof entry.limit !== 'number' ||
    typeof entry.truncated !== 'boolean' ||
    typeof entry.scannedAt !== 'number' ||
    !Array.isArray(entry.records) ||
    !Number.isFinite(entry.limit) ||
    !Number.isFinite(entry.scannedAt)
  ) {
    return null;
  }

  const projectPath = normalizeProjectPath(entry.projectPath);
  const sourceSignature = normalizeSourceScanCacheSignature(entry.sourceSignature);
  const limit = Math.max(0, Math.floor(entry.limit));
  const records = entry.records
    .map(parseStoredSourceRecord)
    .filter((record): record is SourceRecord => record !== null);

  if (!entry.projectID.trim() || !projectPath || !sourceSignature || records.length !== entry.records.length) {
    return null;
  }

  const key = sourceScanCacheKey({ id: entry.projectID, name: entry.projectID, path: projectPath }, limit);
  return {
    key,
    projectID: entry.projectID,
    projectPath,
    sourceSignature,
    limit,
    truncated: entry.truncated,
    records,
    scannedAt: normalSourceScanTimestamp(entry.scannedAt),
    stats: parseStoredSourceScanStats(entry.stats)
  };
}

function parseStoredSourceRecord(value: unknown): SourceRecord | null {
  if (!value || typeof value !== 'object') return null;

  const record = value as Partial<SourceRecord>;
  if (
    typeof record.path !== 'string' ||
    typeof record.relativePath !== 'string' ||
    typeof record.fileName !== 'string' ||
    typeof record.language !== 'string' ||
    typeof record.byteCount !== 'number' ||
    !Number.isFinite(record.byteCount)
  ) {
    return null;
  }

  return {
    path: record.path,
    relativePath: record.relativePath,
    fileName: record.fileName,
    language: record.language,
    byteCount: Math.max(0, Math.floor(record.byteCount))
  };
}

function parseStoredSourceScanStats(value: unknown): SourceScanStats | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;

  const stats = value as SourceScanStats;
  const nextStats: SourceScanStats = {
    visitedEntries: sourceScanMetric(stats, 'visitedEntries', 'visitedEntryCount'),
    matchedFiles: sourceScanMetric(stats, 'matchedFiles', 'matchedFileCount'),
    skippedDirectories: sourceScanMetric(stats, 'skippedDirectories', 'skippedDirectoryCount'),
    unsupportedFiles: sourceScanMetric(stats, 'unsupportedFiles', 'unsupportedFileCount'),
    unreadableEntries: sourceScanMetric(stats, 'unreadableEntries', 'unreadableEntryCount')
  };

  for (const key of [
    'requestedLimit',
    'effectiveLimit',
    'returnedFiles',
    'returnedCount',
    'collectionLimit',
    'visitedEntryCount',
    'matchedFileCount',
    'skippedDirectoryCount',
    'unsupportedFileCount',
    'unreadableEntryCount'
  ] as const) {
    const value = optionalSourceScanMetric(stats[key]);
    if (value !== null) {
      nextStats[key] = value;
    }
  }

  if (typeof stats.collectionLimitReached === 'boolean') {
    nextStats.collectionLimitReached = stats.collectionLimitReached;
  }

  const skippedDirectorySamples = sourceScanSkippedDirectorySamples(stats).filter(isSourceSkippedDirectory);
  if (skippedDirectorySamples.length > 0) {
    nextStats.skippedDirectorySamples = skippedDirectorySamples;
  }

  return nextStats;
}

function normalSourceScanTimestamp(value: number): number {
  return Math.max(0, Math.floor(Number.isFinite(value) ? value : 0));
}

function normalizeSourceScanCacheSignature(value: string | null | undefined): string {
  return String(value ?? '').trim().slice(0, 512);
}

function sourceScanCacheSignaturesMatch(
  cachedSignature: string | null | undefined,
  expectedSignature: string
): boolean {
  if (!expectedSignature) return true;

  const normalizedCachedSignature = normalizeSourceScanCacheSignature(cachedSignature);
  if (!normalizedCachedSignature) return false;
  if (normalizedCachedSignature === expectedSignature) return true;

  const cachedScope = stableSourceScanCacheSignatureScope(normalizedCachedSignature);
  const expectedScope = stableSourceScanCacheSignatureScope(expectedSignature);
  return Boolean(cachedScope && expectedScope && cachedScope === expectedScope);
}

function stableSourceScanCacheSignatureScope(signature: string): string | null {
  const parts = signature.split('|');
  if (parts.length >= 6 && parts[1] === 'git') {
    return parts.slice(0, 6).join('|');
  }

  if (parts.length >= 3 && parts[1] === 'path') {
    return parts.slice(0, 3).join('|');
  }

  return null;
}

export function removeSourceScanCacheEntries(
  cache: SourceScanCache,
  project: ProjectRoot
): SourceScanCache {
  const projectPath = normalizeProjectPath(project.path);
  return Object.fromEntries(
    Object.entries(cache).filter(([, entry]) => entry.projectPath !== projectPath)
  );
}

function sourceScanCacheKey(project: ProjectRoot, limit: number): string {
  return `${normalizeProjectPath(project.path)}::${Math.max(0, Math.floor(limit))}`;
}

function formatCount(value: number): string {
  return Math.max(0, Math.floor(value)).toLocaleString('en-US');
}

function formatSourceScanAge(scannedAt: number, now: number): string {
  const elapsedMs = Math.max(0, now - scannedAt);
  if (elapsedMs < 10_000) return 'just now';
  if (elapsedMs < 60_000) return `${Math.floor(elapsedMs / 1_000)}s ago`;
  if (elapsedMs < 3_600_000) return `${Math.floor(elapsedMs / 60_000)}m ago`;
  if (elapsedMs < 86_400_000) return `${Math.floor(elapsedMs / 3_600_000)}h ago`;
  return `${Math.floor(elapsedMs / 86_400_000)}d ago`;
}

export function upsertOpenSourceTab(
  tabs: SourceOpenTab[],
  record: SourceRecord,
  project: ProjectRoot,
  openedAt = Date.now(),
  limit = 8
): SourceOpenTab[] {
  const cappedLimit = Math.max(0, Math.floor(limit));
  if (cappedLimit === 0) return [];

  const nextTab = createProjectSourceRecord(record, project, openedAt);
  const existingTabIndex = tabs.findIndex((tab) => tab.path === record.path);
  const nextTabs =
    existingTabIndex === -1
      ? [...tabs, nextTab]
      : tabs.map((tab, index) => (index === existingTabIndex ? nextTab : tab));

  return nextTabs.slice(Math.max(0, nextTabs.length - cappedLimit));
}

export function closeOpenSourceTab(
  tabs: SourceOpenTab[],
  closedPath: string,
  activePath: string | null | undefined
): CloseOpenSourceTabResult {
  const closedTabIndex = tabs.findIndex((tab) => tab.path === closedPath);
  if (closedTabIndex === -1) {
    return {
      tabs,
      nextActivePath: activePath ?? tabs[0]?.path ?? null
    };
  }

  const nextTabs = tabs.filter((tab) => tab.path !== closedPath);
  const closedActiveTab = activePath === closedPath;
  if (!closedActiveTab) {
    return {
      tabs: nextTabs,
      nextActivePath: activePath ?? nextTabs[0]?.path ?? null
    };
  }

  return {
    tabs: nextTabs,
    nextActivePath: nextTabs[closedTabIndex]?.path ?? nextTabs[closedTabIndex - 1]?.path ?? null
  };
}

export function closeOtherCleanOpenSourceTabs(
  tabs: SourceOpenTab[],
  activePath: string | null | undefined,
  dirtyPaths: ReadonlySet<string>
): CloseOpenSourceTabsResult {
  return closeOpenSourceTabsWhere(
    tabs,
    activePath,
    dirtyPaths,
    (tab) => tab.path !== activePath && !dirtyPaths.has(tab.path)
  );
}

export function closeAllCleanOpenSourceTabs(
  tabs: SourceOpenTab[],
  activePath: string | null | undefined,
  dirtyPaths: ReadonlySet<string>
): CloseOpenSourceTabsResult {
  return closeOpenSourceTabsWhere(tabs, activePath, dirtyPaths, (tab) => !dirtyPaths.has(tab.path));
}

function closeOpenSourceTabsWhere(
  tabs: SourceOpenTab[],
  activePath: string | null | undefined,
  dirtyPaths: ReadonlySet<string>,
  shouldClose: (tab: SourceOpenTab) => boolean
): CloseOpenSourceTabsResult {
  const nextTabs = tabs.filter((tab) => !shouldClose(tab));
  const closedCount = tabs.length - nextTabs.length;
  const retainedDirtyCount = nextTabs.filter((tab) => dirtyPaths.has(tab.path)).length;

  return {
    tabs: nextTabs,
    nextActivePath: nextActivePathAfterBulkClose(tabs, nextTabs, activePath),
    closedCount,
    retainedDirtyCount
  };
}

function nextActivePathAfterBulkClose(
  previousTabs: SourceOpenTab[],
  nextTabs: SourceOpenTab[],
  activePath: string | null | undefined
): string | null {
  if (activePath && nextTabs.some((tab) => tab.path === activePath)) return activePath;
  if (nextTabs.length === 0) return null;

  const nextPaths = new Set(nextTabs.map((tab) => tab.path));
  const activeIndex = previousTabs.findIndex((tab) => tab.path === activePath);
  if (activeIndex === -1) return nextTabs[0]?.path ?? null;

  for (let index = activeIndex; index < previousTabs.length; index += 1) {
    const candidate = previousTabs[index];
    if (candidate && nextPaths.has(candidate.path)) return candidate.path;
  }

  for (let index = activeIndex - 1; index >= 0; index -= 1) {
    const candidate = previousTabs[index];
    if (candidate && nextPaths.has(candidate.path)) return candidate.path;
  }

  return nextTabs[0]?.path ?? null;
}

export function findAdjacentSourceDiagnostic(
  diagnostics: SourceDiagnostic[],
  currentLine: number,
  currentColumn: number,
  direction: 1 | -1
): SourceDiagnostic | null {
  if (diagnostics.length === 0) return null;

  const sortedDiagnostics = [...diagnostics].sort((left, right) =>
    left.line === right.line ? left.column - right.column : left.line - right.line
  );
  const line = Math.max(1, Math.floor(currentLine));
  const column = Math.max(1, Math.floor(currentColumn));

  if (direction === 1) {
    return (
      sortedDiagnostics.find((diagnostic) =>
        diagnostic.line > line || (diagnostic.line === line && diagnostic.column > column)
      ) ?? sortedDiagnostics[0] ?? null
    );
  }

  for (let index = sortedDiagnostics.length - 1; index >= 0; index -= 1) {
    const diagnostic = sortedDiagnostics[index];
    if (!diagnostic) continue;
    if (diagnostic.line < line || (diagnostic.line === line && diagnostic.column < column)) {
      return diagnostic;
    }
  }

  return sortedDiagnostics[sortedDiagnostics.length - 1] ?? null;
}

export function filterSourceRecords(records: SourceRecord[], query: string): SourceRecord[] {
  const normalizedQuery = query.trim().toLowerCase();
  if (!normalizedQuery) return records;

  return records.filter((record) =>
    `${record.relativePath} ${record.fileName} ${record.language}`.toLowerCase().includes(normalizedQuery)
  );
}

export function rankSourceRecords(
  records: SourceRecord[],
  query: string,
  limit = 20
): SourceRecord[] {
  const cappedLimit = Math.max(0, Math.floor(limit));
  if (cappedLimit === 0) return [];

  const normalizedQuery = parseQuickOpenQuery(query).searchQuery.toLowerCase();
  if (!normalizedQuery) return records.slice(0, cappedLimit);

  return records
    .map((record) => ({
      record,
      score: scoreSourceRecord(record, normalizedQuery)
    }))
    .filter((result): result is { record: SourceRecord; score: number } => result.score !== null)
    .sort(
      (left, right) =>
        left.score - right.score ||
        localizedAscending(left.record.relativePath, right.record.relativePath)
    )
    .slice(0, cappedLimit)
    .map((result) => result.record);
}

export function findSourceSearchMatches(
  previews: SourcePreview[],
  query: string,
  limit = 50
): SourceSearchMatch[] {
  const cappedLimit = Math.max(0, Math.floor(limit));
  if (cappedLimit === 0) return [];

  const normalizedQuery = query.trim().toLowerCase();
  if (!normalizedQuery) return [];

  const matches: SourceSearchMatch[] = [];
  for (const preview of previews) {
    const lines = preview.content.split(/\r\n|\r|\n/);
    for (const [lineIndex, line] of lines.entries()) {
      const columnIndex = line.toLowerCase().indexOf(normalizedQuery);
      if (columnIndex < 0) continue;

      matches.push({
        path: preview.path,
        relativePath: preview.relativePath,
        fileName: preview.fileName,
        language: preview.language,
        byteCount: preview.byteCount,
        line: lineIndex + 1,
        column: columnIndex + 1,
        excerpt: compactSourceLineExcerpt(line)
      });

      if (matches.length >= cappedLimit) {
        return matches;
      }
    }
  }

  return matches;
}

export function findSourceDefinitionTargets(
  previews: SourcePreview[],
  symbolName: string,
  limit = 20
): SourceDefinitionTarget[] {
  const cappedLimit = Math.max(0, Math.floor(limit));
  if (cappedLimit === 0) return [];

  const normalizedSymbolName = symbolName.trim().toLowerCase();
  if (!normalizedSymbolName) return [];

  const targets: SourceDefinitionTarget[] = [];
  for (const preview of previews) {
    for (const symbol of extractSourceSymbols(preview, preview.content)) {
      if (symbol.name.toLowerCase() !== normalizedSymbolName) continue;

      targets.push({
        path: preview.path,
        relativePath: preview.relativePath,
        fileName: preview.fileName,
        language: preview.language,
        byteCount: preview.byteCount,
        symbolName: symbol.name,
        kind: symbol.kind,
        line: symbol.line,
        column: symbol.detail.indexOf(symbol.name) + 1 || symbol.column,
        detail: symbol.detail
      });

      if (targets.length >= cappedLimit) {
        return targets;
      }
    }
  }

  return targets;
}

export function findSourceReferenceTargets(
  previews: SourcePreview[],
  symbolName: string,
  limit = 50
): SourceReferenceTarget[] {
  const cappedLimit = Math.max(0, Math.floor(limit));
  if (cappedLimit === 0) return [];

  const normalizedSymbolName = symbolName.trim();
  if (!normalizedSymbolName) return [];

  const symbolPattern = new RegExp(`\\b${escapeRegExp(normalizedSymbolName)}\\b`, 'i');
  const targets: SourceReferenceTarget[] = [];
  for (const preview of previews) {
    const lines = preview.content.split(/\r\n|\r|\n/);
    for (const [lineIndex, line] of lines.entries()) {
      const match = symbolPattern.exec(line);
      if (!match) continue;

      targets.push({
        path: preview.path,
        relativePath: preview.relativePath,
        fileName: preview.fileName,
        language: preview.language,
        byteCount: preview.byteCount,
        symbolName: match[0],
        line: lineIndex + 1,
        column: match.index + 1,
        excerpt: compactSourceLineExcerpt(line)
      });

      if (targets.length >= cappedLimit) {
        return targets;
      }
    }
  }

  return targets;
}

export function parseQuickOpenQuery(query: string): QuickOpenQuery {
  const trimmedQuery = query.trim();
  const lineMatch = /^(.*):(\d*)$/.exec(trimmedQuery);
  if (!lineMatch) {
    return {
      searchQuery: trimmedQuery,
      targetLine: null
    };
  }

  if (!lineMatch[2]) {
    return {
      searchQuery: lineMatch[1].trim(),
      targetLine: null
    };
  }

  const parsedLine = Number.parseInt(lineMatch[2], 10);
  if (!Number.isSafeInteger(parsedLine) || parsedLine < 1) {
    return {
      searchQuery: trimmedQuery,
      targetLine: null
    };
  }

  return {
    searchQuery: lineMatch[1].trim(),
    targetLine: parsedLine
  };
}

export function folderIdsForSourceRecord(record: SourceRecord): string[] {
  const folderComponents = record.relativePath.split('/').filter(Boolean).slice(0, -1);
  return folderComponents.map((_, index) => `folder:${folderComponents.slice(0, index + 1).join('/')}`);
}

export function buildSourceTree(records: SourceRecord[]): SourceTreeNode[] {
  const entries = records.map((record) => ({
    components: record.relativePath.split('/').filter(Boolean),
    record
  }));

  return buildTreeNodes(entries, []);
}

export function flattenSourceTree(
  nodes: SourceTreeNode[],
  expandedFolderIds: Set<string>,
  autoExpandFolders: boolean
): SourceTreeRow[] {
  const rows: SourceTreeRow[] = [];

  function visit(nodeList: SourceTreeNode[], level: number) {
    for (const node of nodeList) {
      rows.push({ node, level });
      if (node.file === null && (autoExpandFolders || expandedFolderIds.has(node.id))) {
        visit(node.children, level + 1);
      }
    }
  }

  visit(nodes, 0);
  return rows;
}

export function virtualizeSourceTreeRows(
  rows: SourceTreeRow[],
  scrollTop: number,
  viewportHeight: number,
  rowHeight: number,
  overscanRows: number
): VirtualSourceTreeRows {
  if (rows.length === 0) {
    return {
      rows: [],
      startIndex: 0,
      endIndex: 0,
      topSpacerHeight: 0,
      bottomSpacerHeight: 0,
      totalHeight: 0
    };
  }

  const safeRowHeight = Math.max(1, rowHeight);
  const safeOverscanRows = Math.max(0, Math.floor(overscanRows));
  const visibleRowCount = Math.max(1, Math.ceil(Math.max(0, viewportHeight) / safeRowHeight));
  const maxFirstVisibleIndex = Math.max(0, rows.length - visibleRowCount);
  const firstVisibleIndex = Math.min(
    maxFirstVisibleIndex,
    Math.max(0, Math.floor(Math.max(0, scrollTop) / safeRowHeight))
  );
  const startIndex = Math.max(0, firstVisibleIndex - safeOverscanRows);
  const endIndex = Math.min(rows.length, firstVisibleIndex + visibleRowCount + safeOverscanRows);

  return {
    rows: rows.slice(startIndex, endIndex),
    startIndex,
    endIndex,
    topSpacerHeight: startIndex * safeRowHeight,
    bottomSpacerHeight: Math.max(0, (rows.length - endIndex) * safeRowHeight),
    totalHeight: rows.length * safeRowHeight
  };
}

export function scrollTopForSourceTreeReveal(
  rowIndex: number,
  currentScrollTop: number,
  viewportHeight: number,
  rowHeight: number
): number {
  if (rowIndex < 0) return currentScrollTop;

  const safeRowHeight = Math.max(1, rowHeight);
  const safeViewportHeight = Math.max(safeRowHeight, viewportHeight);
  const safeScrollTop = Math.max(0, currentScrollTop);
  const rowTop = rowIndex * safeRowHeight;
  const rowBottom = rowTop + safeRowHeight;
  const viewportBottom = safeScrollTop + safeViewportHeight;

  if (rowTop < safeScrollTop) return rowTop;
  if (rowBottom > viewportBottom) return rowBottom - safeViewportHeight;
  return safeScrollTop;
}

type SourceTreeEntry = {
  components: string[];
  record: SourceRecord;
};

function buildTreeNodes(entries: SourceTreeEntry[], prefix: string[]): SourceTreeNode[] {
  const folderEntries = new Map<string, SourceTreeEntry[]>();
  const fileEntries: SourceTreeEntry[] = [];

  for (const entry of entries) {
    const firstComponent = entry.components[0];
    if (!firstComponent) continue;

    if (entry.components.length === 1) {
      fileEntries.push(entry);
      continue;
    }

    const existing = folderEntries.get(firstComponent) ?? [];
    existing.push({
      components: entry.components.slice(1),
      record: entry.record
    });
    folderEntries.set(firstComponent, existing);
  }

  const folderNodes = [...folderEntries.keys()]
    .sort(localizedAscending)
    .map((folderName) => {
      const relativePath = [...prefix, folderName].join('/');
      return {
        id: `folder:${relativePath}`,
        name: folderName,
        relativePath,
        file: null,
        children: buildTreeNodes(folderEntries.get(folderName) ?? [], [...prefix, folderName])
      };
    });

  const fileNodes = fileEntries
    .sort((left, right) => localizedAscending(left.record.fileName, right.record.fileName))
    .map((entry) => ({
      id: `file:${entry.record.path}`,
      name: entry.record.fileName,
      relativePath: entry.record.relativePath,
      file: entry.record,
      children: []
    }));

  return [...folderNodes, ...fileNodes];
}

function localizedAscending(left: string, right: string): number {
  return left.localeCompare(right, undefined, { sensitivity: 'base', numeric: true });
}

function scoreSourceRecord(record: SourceRecord, normalizedQuery: string): number | null {
  const fileName = record.fileName.toLowerCase();
  const relativePath = record.relativePath.toLowerCase();
  const language = record.language.toLowerCase();

  if (fileName === normalizedQuery) return 0;
  if (fileName.startsWith(normalizedQuery)) return 1;
  if (fileName.includes(normalizedQuery)) return 2;
  if (relativePath.startsWith(normalizedQuery)) return 3;
  if (relativePath.includes(normalizedQuery)) return 4;
  if (language.includes(normalizedQuery)) return 5;
  return null;
}

function compactSourceLineExcerpt(line: string): string {
  const trimmedLine = line.trim();
  if (trimmedLine.length <= 180) return trimmedLine;
  return `${trimmedLine.slice(0, 177)}...`;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function normalizeTaskID(taskID: string | null | undefined): string | null {
  const normalized = String(taskID ?? '').trim().toUpperCase();
  return /^TSK-\d+$/.test(normalized) ? normalized : null;
}

function createProjectSourceRecord(
  record: SourceRecord,
  project: ProjectRoot,
  openedAt: number
): SourceRecentRecord {
  return {
    ...record,
    projectID: project.id,
    projectName: project.name,
    openedAt
  };
}
