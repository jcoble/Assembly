import { Channel } from '@tauri-apps/api/core';
import type { AgentConversationConfigState } from './shell/conversation/conversationConfig.ts';
import type { AgentConversationProvider, AguiPage, AguiSnapshot } from './shell/conversation/conversationTypes.ts';
import type { StreamChunk } from '@tanstack/ai/client';
import {
  trackTauriChannel,
  trackTauriListener,
  trackTauriSubscriber
} from './shell/resourceDiagnostics.svelte.ts';
import {
  normalizeWorkspaceSnapshot,
  type SessionWorkspaceSnapshot
} from './shell/sessionWorkspaces.ts';
import type {
  WorkflowHistoryRow,
  WorkflowPauseDecision,
  WorkflowRunRecord,
  WorkflowTaskSnapshot
} from './shell/workflows/workflowTypes.ts';
import type {
  ProjectRoot,
  SourceCodeAction,
  SourceCompletionItem,
  SourceDefinitionTarget,
  SourceDiagnostic,
  SourceDirectoryEntry,
  SourceDocumentHighlight,
  SourceInlayHint,
  SourceLspCodeActionRequest,
  SourceLspHover,
  SourceLspLookupRequest,
  SourceLspRenameRequest,
  SourceLspStatus,
  SourceLspWorkspaceSymbolRequest,
  SourcePreview,
  SourceRecord,
  SourceReferenceCountResult,
  SourceReferenceTarget,
  SourceRenameResult,
  SourceScanResult,
  SourceSearchMatch,
  SourceSemanticToken,
  SourceSignatureHelp,
  SourceSymbol,
  SourceTextEdit,
  SourceTreeSearchPage,
  SourceWorkspaceSymbol
} from './sourceData.ts';
import { bridgeTokenHeader } from './devBridgeToken.ts';
import { mapWorkspaceSnapshotPaths, parseRemoteWorkspacePath, remoteWorkspacePath } from './workspacePaths.ts';

export const defaultSourceScanLimit = 10_000;
export const expandedSourceScanLimit = 25_000;
export const nativeSourceScanProgressEvent = 'source_scan_progress';
export const terminalOutputEvent = 'terminal_output';

export type NativeSourceScanProgress = {
  scanId: string;
  visitedEntries: number;
  matchedFiles: number;
};

export type InstalledFontFamilies = {
  families: string[];
  monospacedFamilies: string[];
};

export type SourceScanProgressSubscriber = (progress: NativeSourceScanProgress) => void;

export type TerminalStartRequest = {
  cwd: string;
  shell?: string | null;
  cols?: number | null;
  rows?: number | null;
  ownedId?: string | null;
  /**
   * Run this one command instead of opening an interactive shell. The session
   * ends when the command does, and its exit code is the command's — which is
   * the only way a caller can tell "it finished" from "it crashed". Leave it out
   * and the session is the ordinary login shell it has always been.
   *
   * Desktop builds older than this field silently drop it and open a shell, so
   * anything that depends on the exit code must check
   * `hasBackendCapability('terminalCommandSpawn')` first — see
   * `src/lib/shell/backendCapabilities.ts`.
   */
  command?: string | null;
};

export type TerminalSessionInfo = {
  sessionId: string;
  cwd: string;
  shell: string;
  cols: number;
  rows: number;
  pid: number | null;
  startedAt: number;
  exited: boolean;
  exitCode: number | null;
  signal: string | null;
  kind?: 'user-pty' | 'agent-tool' | 'run-configuration' | 'browser-automation';
  ownedId?: string | null;
  toolTerminalIdentity?: {
    ownedId: string;
    turnId: string;
    toolCallId: string;
    terminalId: string;
  } | null;
};

export type ResourceSnapshot = import('./shell/resources/resourceTypes.ts').ResourceSnapshot;
export type ResourceDiskRoot = import('./shell/resources/resourceTypes.ts').ResourceDiskRoot;
export type ResourceStopRequest = import('./shell/resources/resourceTypes.ts').ResourceStopRequest;
export type ResourceCommandReceipt = import('./shell/resources/resourceTypes.ts').ResourceCommandReceipt;
export type ResourceUnavailable = import('./shell/resources/resourceTypes.ts').ResourceUnavailable;
export type ResourceCleanupRequest = import('./shell/resources/resourceTypes.ts').ResourceCleanupRequest;
export type ResourceCleanupReceipt = import('./shell/resources/resourceTypes.ts').ResourceCleanupReceipt;
export type DiskScanReport = import('./shell/resources/resourceTypes.ts').DiskScanReport;
export type ProviderUsageSnapshot = import('./shell/usage/usageTypes.ts').ProviderUsageSnapshot;
export type UsageHistoryQuery = import('./shell/usage/usageTypes.ts').UsageHistoryQuery;
export type UsageSummary = import('./shell/usage/usageTypes.ts').UsageSummary;
export type UsageBreakdownRow = import('./shell/usage/usageTypes.ts').UsageBreakdownRow;
export type UsageProviderSummaryRow = import('./shell/usage/usageTypes.ts').UsageProviderSummaryRow;
export type UsageDailyRow = import('./shell/usage/usageTypes.ts').UsageDailyRow;
export type UsageDailyTotalsRow = import('./shell/usage/usageTypes.ts').UsageDailyTotalsRow;
export type AgentConversationCapabilities = import('./shell/conversation/conversationTypes.ts').AgentCapabilities;
export type AgentConversationEvent = import('./shell/conversation/conversationTypes.ts').AgentConversationEvent;
export type AgentConversationEventPage = import('./shell/conversation/conversationTypes.ts').AgentConversationEventPage;
export type AgentConversationItemPage = import('./shell/conversation/conversationTypes.ts').AgentConversationItemPage;
export type AgentConversationSelectionSnapshot = import('./shell/conversation/conversationTypes.ts').AgentConversationSelectionSnapshot;
export type AgentConversationChildHistory = import('./shell/conversation/conversationTypes.ts').AgentConversationChildHistory;

export type TerminalOutputPayload = {
  sessionId: string;
  data: string;
  terminated: boolean;
  exitCode: number | null;
  signal: string | null;
};

export type TerminalScrollbackSnapshot = {
  data: string;
  generation: number;
  sequence: number;
};

type ProjectionStreamWireEnvelope<T> = {
  session: string;
  generation: number;
  sequence: number;
  bytes: number;
  kind: 'agent-conversation-event' | typeof terminalOutputEvent;
  resync: boolean;
  chunk: T | null;
};

export type StreamEnvelope<T> = {
  sessionId: string;
  generation: number;
  sequence: number;
  byteLength: number;
  kind: 'agent-conversation-event' | typeof terminalOutputEvent;
  chunk: T;
};

export type TerminalOutputEnvelope = StreamEnvelope<TerminalOutputPayload>;

export type ProjectionStreamRegistration = {
  unregister(): Promise<void>;
};

export type SessionSubscription = {
  unsubscribe(): void;
};

export type ProjectGitFileStatus = {
  relativePath: string;
  indexStatus: string;
  worktreeStatus: string;
  status: string;
  badge: string;
};

export type ProjectGitStatus = {
  branch: string | null;
  ahead: number;
  behind: number;
  hasUpstream: boolean;
  /** At most 1,000 files; `totalFiles` says how many git reported. */
  files: ProjectGitFileStatus[];
  totalFiles?: number;
  /** Working-tree lines added/removed against HEAD; null without a HEAD. */
  additions?: number | null;
  deletions?: number | null;
};

export type GitActionResult = {
  message: string;
  status: ProjectGitStatus;
};

export type AgentGenerationRequest = {
  root: string;
  ownedId: string;
  generation: number;
};

export type PullRequestContext = {
  branch: string;
  base: string;
  commits: string;
  diff: string;
};

export type PullRequestDetails = {
  title: string;
  description: string;
};

export type PullRequestCreated = {
  number: number;
  url: string;
};

export type PullRequestStatus = {
  number: number;
  url: string;
  state: string;
  checks: 'none' | 'pending' | 'passing' | 'failing' | string;
  checkSummary: string;
};

export type PullRequestSummary = {
  number: number;
  title: string;
  url: string;
  state: string;
  headBranch: string;
  isDraft: boolean;
  checks: 'none' | 'pending' | 'passing' | 'failing' | string;
  checkSummary: string;
};

export type GithubPullRequestSummary = {
  repository: string;
  localRoot: string;
  number: number;
  title: string;
  url: string;
  isDraft: boolean;
  updatedAt: string;
  state: string;
  author: string;
  headBranch: string;
  baseBranch: string;
};

export type GithubPullRequestPage = {
  items: GithubPullRequestSummary[];
  nextCursor: string | null;
  totalCount: number;
};

export type GithubPullRequestQuery = {
  roots: string[];
  projectFilter: string | null;
  /** GitHub search terms after `is:pr` and the repositories; see pullRequestSearch.ts. */
  search: string | null;
  cursor: string | null;
  pageSize: number;
};

export type GithubPullRequestDetail = {
  repository: string;
  localRoot: string;
  number: number;
  title: string;
  body: string;
  url: string;
  author: string;
  headBranch: string;
  baseBranch: string;
  headSha: string;
  baseSha: string;
  state: string;
  isDraft: boolean;
  mergeable: string;
  reviewDecision: string;
  createdAt: string;
  mergedAt: string;
  mergedBy: string;
  closedAt: string;
  commits: Array<{ oid: string; headline: string; author: string; committedAt: string }>;
  comments: Array<{ id: string; author: string; body: string; createdAt: string; path: string | null; line: number | null; side: string | null; replyToId: number | null }>;
  reviews: Array<{ author: string; body: string; state: string; submittedAt: string }>;
  reviewers: string[];
  checks: Array<{ name: string; status: string; conclusion: string; url: string }>;
  files: Array<{ path: string; previousPath: string | null; status: string; additions: number; deletions: number; patch: string | null }>;
  moreFiles: boolean;
};

export type GithubFileVersionQuery = {
  root: string;
  path: string;
  previousPath: string | null;
  status: string;
  baseSha: string;
  headSha: string;
};

export type GithubFileVersions = {
  originalContent: string;
  modifiedContent: string;
};

export type GithubReviewSubmission = {
  root: string;
  number: number;
  expectedHeadSha: string;
  body: string;
  comments: Array<{ path: string; line: number; side: 'LEFT' | 'RIGHT'; body: string }>;
};

export type GithubReviewReply = {
  root: string;
  number: number;
  expectedHeadSha: string;
  commentId: number;
  body: string;
};

export type GithubIssueComment = {
  root: string;
  number: number;
  body: string;
};

export type GithubMergeRequest = {
  root: string;
  number: number;
  expectedHeadSha: string;
  expectedBaseSha: string;
  method: 'merge' | 'squash' | 'rebase';
};

export type GitBranchSummary = {
  name: string;
  isCurrent: boolean;
  upstream: string; worktreePath: string; // Where the branch is checked out on the repository's machine, '' when nowhere.
  subject: string;
};

export type GitBranchList = {
  current: string;
  branches: GitBranchSummary[];
  remotes: string[];
};

export type GitStashEntry = {
  index: number;
  label: string;
  description: string;
};

export type SourceGitDiff = {
  relativePath: string;
  status: string;
  diff: string;
  isBinary: boolean;
  /** Full bounded text from HEAD (or the selected commit's first parent). */
  originalContent: string | null;
  /** Full bounded text from the working tree (or the selected commit). */
  modifiedContent: string | null;
};

export type ProjectWorktree = {
  repo: string;
  path: string;
  branch: string;
  taskID: string | null;
  isDirty: boolean;
  hasUnmergedCommits: boolean;
  isPrunable?: boolean;
  prunableReason?: string | null;
  isLocked?: boolean;
  lockedReason?: string | null;
  lastActivity: string | null;
  deleteEligibility: string;
};

export type ProjectWorktreeActionResult = {
  message: string;
  worktrees: ProjectWorktree[];
};

export type ProjectWorktreeArchiveResult = ProjectWorktreeActionResult & {
  archivePath: string;
};

export type GitRepositorySummary = {
  projectID: string;
  projectName: string;
  repo: string;
  path: string;
  rootLabel: string;
  branch: string;
  taskID: string | null;
  isWorktree: boolean;
  isDirty: boolean;
  stagedCount: number;
  unstagedCount: number;
  untrackedCount: number;
  dirtyCount: number;
  ahead: number;
  behind: number;
  hasUpstream: boolean;
  lastCommitSha: string | null;
  lastCommitSubject: string | null;
  lastCommitAt: string | null;
  dirtySinceEpochMs: number | null;
  dirtyStatusFingerprint: string;
  error: string | null;
};

export type GitCommitHistoryEntry = {
  shortSha: string;
  sha: string;
  subject: string;
  author: string;
  committedAt: string;
  refs: string;
  parentShas: string[];
  parentCount: number;
  taskID: string | null;
  taskSource: string | null;
};

export type GitHistoryPage = {
  root: string;
  relativePath: string | null;
  commits: GitCommitHistoryEntry[];
  nextCursor: string | null;
  complete: boolean;
};

export type AgentSession = {
  provider: string;
  id: string;
  title: string;
  description?: string | null;
  model: string | null;
  projectPath: string | null;
  lastActivity: string | null;
  resumeCommands: string[];
  /**
   * The transcript file the scanner read this session out of, so the app can
   * open it, show it in the file manager, or copy its path. Left out when no
   * single file describes the session, in which case those actions stay off.
   */
  logPath?: string | null;
  /**
   * What the scanner worked out about the session from its title, folder and
   * resume command. All optional: the scanner leaves a field out entirely when
   * it found nothing, and older records predate them, so a row must treat a
   * missing field and an empty one the same way — draw no chip.
   */
  branchHint?: string | null;
  taskId?: string | null;
  pullRequestHint?: string | null;
  sourceLabel?: string | null;
  /**
   * How many turns of the conversation the scanner saw, and the last one of
   * them, already written as the row shows it (`You: …` / `Agent: …`).
   *
   * The count is a floor, not a total: the scanner reads a bounded window of
   * each transcript, so a long session reports the turns inside that window and
   * no more. Optional like the hints above — a scanner that found no
   * conversation leaves them out, and a row then shows neither.
   */
  messageCount?: number | null;
  latestTurnPreview?: string | null;
  /**
   * The last thing each side said, oldest first — at most the user's most
   * recent turn and the agent's most recent one.
   *
   * This is what an expanded card shows, and it is longer than the preview
   * above on purpose: the preview has one line of a row to live in, while the
   * card scrolls. Left out entirely when the scanner read no conversation.
   */
  latestTurns?: AgentSessionTurn[];
  /**
   * The project group the scan command matched this session's folder to, in
   * SQL against the project registry: `repo:<key>`, `project:<id>` or `none`,
   * and the label History shows for it.
   */
  projectGroupKey: string;
  projectGroupLabel: string;
  /** The saved machine whose scanner found it; this Mac's scan leaves it out. */
  machine?: string;
};

/** One remembered turn of a scanned session: who spoke, and what they said. */
export type AgentSessionTurn = {
  speaker: 'user' | 'agent';
  text: string;
};

export type AgentConversationSessionMeta = {
  worktree: string | null;
  branch: string | null;
  title: string | null;
  project: string | null;
  ptySessionId: string | null;
  origin: 'app' | 'external' | null;
  source: 'scanned' | 'fresh' | null;
  viaCmux: boolean;
  resumeCommand: string | null;
  completedAt: string | null;
  settledAt: string | null;
  pinnedAt: string | null;
  taskId: string | null;
  pullRequest: string | null;
  messageCount: number | null;
  latestTurnPreview: string | null;
  scannedLastActivity: string | null;
};

export type BackgroundWorkItem = {
  id: string;
  kind: 'subagent' | 'command';
  label: string;
  startedAtMs: number;
};

export type AgentConversationSessionRecord = AgentConversationSessionMeta & {
  ownedId: string; canChangeCheckout: boolean;
  executionEnvironment: ExecutionEnvironment;
  remoteProfileId: string | null;
  provider: import('./shell/conversation/conversationTypes.ts').AgentConversationProvider;
  model: string | null;
  effort: string | null;
  cwd: string;
  state: import('./shell/ownedSessions.ts').AgentRuntimeState;
  suspended: boolean;
  createdAtMs: number;
  lastActivityAtMs: number;
  activeTurnId: string | null;
  pendingPermission: boolean;
  pendingInput: boolean;
  backgroundWork: BackgroundWorkItem[];
  nativeSessionId: string | null;
  projectId: string | null;
  /** The session's permanent handle (`brisk-heron`) other sessions message it by; renaming never changes it. */
  handle?: string | null;
  /** The session's project group, computed in SQL on this Mac: `repo:<key>`, `project:<id>` or `none`. */
  projectGroupKey: string;
  projectGroupLabel: string;
  /** When the owner marked this session as the main orchestrator; absent otherwise. */
  orchestratorMarkedAtMs?: number | null;
  /** The session that started this helper; absent for any other session. */
  helperOf?: string | null;
  /** The workflow run this worker belongs to, and its `<task key> · <workflow name>`. */
  runId?: string | null;
  runLabel?: string | null;
  /** The session that started this worker's run; absent when the owner started it. */
  runLead?: string | null;
};

export type ExecutionEnvironment = 'local' | 'remote';

export type RemoteAssemblyEnvironment = {
  profiles: RemoteAssemblyProfile[];
  readyProfileIds: string[];
};

export type RemoteBackendProfileStatus = {
  profileId: string;
  installed: boolean;
  installedVersion: string | null;
  backendBuildKind: 'release' | 'development';
  backendCommit: string | null;
  latestVersion: string;
  updateAvailable: boolean | null;
};

export type RemoteAssemblyProfile = {
  id: string;
  name: string;
  sshTarget: string;
  sourceRoot: string;
  defaultCwd: string;
};

export type RuntimeContextProject = Pick<ProjectRoot, 'id' | 'name' | 'path'>;

export type RuntimeContext = {
  pid: number;
  command: string;
  port: number;
  cwd: string;
  projectID: string | null;
  projectName: string;
  rootLabel: string;
};

export type PlaywrightProcessInfo = {
  pid: number;
  pgid: number;
  command: string;
  name: string;
  label: string;
  elapsed: string;
  args: string;
};

export type PlaywrightSessionInfo = {
  pgid: number;
  label: string;
  pids: number[];
  processes: PlaywrightProcessInfo[];
};

export type PlaywrightCleanupFailure = {
  pgid: number;
  pid: number | null;
  message: string;
};

export type PlaywrightCleanupResult = {
  sessions: PlaywrightSessionInfo[];
  terminatedPgids: number[];
  terminatedPids: number[];
  failedPgids: PlaywrightCleanupFailure[];
};

export type ProjectRootValidationResult = {
  path: string;
  exists: boolean;
  isDirectory: boolean;
  isGitRepository: boolean;
  gitRoot: string | null;
  message: string;
};

export type OrchestrationEvent = {
  schemaVersion: number;
  id: string;
  runId: string;
  timestamp: string;
  kind: string;
  status: string;
  title: string | null;
  message: string | null;
  projectID: string | null;
  projectName: string | null;
  projectPath: string | null;
  rootLabel: string | null;
  taskID: string | null;
  agentId: string | null;
  agentProvider: string | null;
  agentRole: string | null;
  stepId: string | null;
  stepKind: string | null;
  artifactId: string | null;
  artifactKind: string | null;
  artifactPath: string | null;
  artifactUrl: string | null;
  linkKind: string | null;
  linkLabel: string | null;
  linkUrl: string | null;
  scenario?: string | null;
  issueID?: string | null;
  retryAttempt?: number | null;
  approvalSubject?: string | null;
  blockerReason?: string | null;
  decisionPrompt?: string | null;
  scenarioCount?: number | null;
  issueCount?: number | null;
  testCount?: number | null;
  retestCount?: number | null;
  fixCount?: number | null;
  resolvedCount?: number | null;
  verifiedCount?: number | null;
  delegatedCount?: number | null;
  decisionCount?: number | null;
  approvalCount?: number | null;
  failedCount?: number | null;
};

export type OrchestrationRun = {
  id: string;
  title: string;
  status: string;
  phase: string;
  progress: number;
  projectID: string | null;
  projectName: string;
  projectPath: string;
  rootLabel: string;
  taskID: string | null;
  startedAt: string | null;
  updatedAt: string | null;
  summary: string;
  agents: OrchestrationAgent[];
  steps: OrchestrationStep[];
  artifacts: OrchestrationArtifact[];
  links: OrchestrationLink[];
  events: OrchestrationEvent[];
};

export type OrchestrationAgent = {
  id: string;
  provider: string;
  role: string;
  status: string;
  title: string;
  lastActivity: string | null;
};

export type OrchestrationStep = {
  id: string;
  kind: string;
  title: string;
  status: string;
  summary: string;
  agentId: string | null;
  startedAt: string | null;
  finishedAt: string | null;
};

export type OrchestrationArtifact = {
  id: string;
  kind: string;
  title: string;
  path: string | null;
  url: string | null;
  status: string;
};

export type OrchestrationLink = {
  kind: string;
  label: string;
  url: string;
};

export function createSourceScanId(): string {
  return `source-scan-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`;
}

export function isNativeTauriRuntime(): boolean {
  return isTauriRuntime();
}

export async function listInstalledFontFamiliesFromTauri(
  signal?: AbortSignal
): Promise<InstalledFontFamilies | null> {
  if (!isTauriRuntime() || signal?.aborted) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  if (signal?.aborted) return null;
  const families = await invoke<InstalledFontFamilies>('list_installed_font_families');
  return signal?.aborted ? null : families;
}

let projectRootValidationRequestId = 0;

export async function validateProjectRootFromTauri(
  path: string,
  signal?: AbortSignal
): Promise<ProjectRootValidationResult | null> {
  if (!path.trim()) {
    return null;
  }
  if (signal?.aborted) {
    return null;
  }

  if (!isTauriRuntime()) {
    const result = await postLocalSourceBridge<ProjectRootValidationResult>('validate', { path });
    return signal?.aborted ? null : result;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  const generation = ++projectRootValidationRequestId;
  const cancel = (): void => {
    void cancelProjectRootValidation(generation);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    if (signal?.aborted) {
      await cancelProjectRootValidation(generation);
      return null;
    }
    const result = await invoke<ProjectRootValidationResult | null>('validate_project_root', {
      path,
      generation
    }, undefined, signal);
    return signal?.aborted ? null : result;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

async function cancelProjectRootValidation(generation: number): Promise<void> {
  if (!isTauriRuntime()) return;
  try {
    const { invoke } = await import('./workspaceInvoke.ts');
    await invoke('cancel_project_root_validation', { generation });
  } catch {
    // The frontend owner has already abandoned this validation.
  }
}

export async function listSourceFilesFromTauri(
  root: string,
  query = '',
  limit = defaultSourceScanLimit,
  scanId: string | null = null
): Promise<SourceScanResult | null> {
  if (!isTauriRuntime()) {
    return postLocalSourceBridge<SourceScanResult>('list', {
      root,
      query: query.trim() || null,
      limit,
      scanId
    });
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceScanResult>('list_source_files', {
    root,
    limit,
    query: query.trim() || null,
    scanId
  });
}

export async function listSourceDirectoryFromTauri(
  root: string,
  directory: string,
  includeExcluded = false,
  scanId: string | null = null,
  signal?: AbortSignal
): Promise<SourceDirectoryEntry[] | null> {
  if (signal?.aborted) return null;
  if (!isTauriRuntime()) {
    const entries = await postLocalSourceBridge<SourceDirectoryEntry[]>('list-directory', {
      root,
      directory,
      includeExcluded
    });
    return signal?.aborted ? null : entries;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  if (signal?.aborted) return null;
  const cancel = (): void => {
    if (scanId) void cancelSourceScanFromTauri(scanId);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    const entries = await invoke<SourceDirectoryEntry[]>('list_source_directory', {
      root,
      directory,
      includeExcluded,
      scanId
    });
    return signal?.aborted ? null : entries;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

export async function searchSourceTreeFromTauri(
  root: string,
  query: string,
  pageSize = 50,
  cursor: number | null = null,
  includeExcluded = false,
  scanId: string | null = null,
  signal?: AbortSignal
): Promise<SourceTreeSearchPage | null> {
  if (signal?.aborted) return null;
  const request = { root, query: query.trim(), pageSize, cursor, includeExcluded, scanId };
  if (!isTauriRuntime()) {
    const page = await postLocalSourceBridge<SourceTreeSearchPage>('search-tree', request);
    return signal?.aborted ? null : page;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  if (signal?.aborted) return null;
  const cancel = (): void => {
    if (scanId) void cancelSourceScanFromTauri(scanId);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    const page = await invoke<SourceTreeSearchPage>('search_source_tree', request);
    return signal?.aborted ? null : page;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

export async function cancelSourceScanFromTauri(scanId: string): Promise<boolean> {
  if (!isTauriRuntime() || !scanId.trim()) {
    return false;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<boolean>('cancel_source_scan', { scanId });
}

/** The red close button or Cmd-Q; the native side holds it until `continueAppExit`. */
export type AppExitKind = 'close' | 'quit';

export async function listenToAppExitRequests(handler: (kind: AppExitKind) => void): Promise<() => void> {
  if (!isTauriRuntime()) return () => {};
  const { listen } = await import('@tauri-apps/api/event');
  return trackTauriListener(await listen<{ kind: AppExitKind }>('assembly_exit_requested', (event) => handler(event.payload.kind)));
}

/** Let the held close or quit go ahead, once. */
export async function continueAppExit(kind: AppExitKind): Promise<void> {
  const { invoke } = await import('@tauri-apps/api/core');
  await invoke('continue_app_exit', { kind });
}

/** The person kept the app open; the next close or quit warns again. */
export async function cancelAppExit(): Promise<void> {
  const { invoke } = await import('@tauri-apps/api/core');
  await invoke('cancel_app_exit');
}

const sourceScanProgressSubscribers = new Set<SourceScanProgressSubscriber>();
let sourceScanProgressUnlisten: (() => void) | null = null;
let sourceScanProgressSetup: Promise<void> | null = null;
let sourceScanProgressGeneration = 0;

export function subscribeToSourceScanProgress(
  subscriber: SourceScanProgressSubscriber
): () => void {
  sourceScanProgressSubscribers.add(subscriber);
  ensureSourceScanProgressListener();
  return trackTauriSubscriber(() => {
    sourceScanProgressSubscribers.delete(subscriber);
    if (sourceScanProgressSubscribers.size === 0) stopSourceScanProgressListener();
  });
}

function ensureSourceScanProgressListener(): void {
  if (!isTauriRuntime() || sourceScanProgressUnlisten || sourceScanProgressSetup) return;
  const generation = sourceScanProgressGeneration;
  const setup = setupSourceScanProgressListener(generation);
  sourceScanProgressSetup = setup;
  void clearSourceScanProgressSetup(setup);
}

async function setupSourceScanProgressListener(generation: number): Promise<void> {
  try {
    const { listen } = await import('@tauri-apps/api/event');
    const stopNative = await listen<NativeSourceScanProgress>(nativeSourceScanProgressEvent, (event) => {
      for (const current of sourceScanProgressSubscribers) current(event.payload);
    });
    const stop = trackTauriListener(stopNative);
    if (generation !== sourceScanProgressGeneration || sourceScanProgressSubscribers.size === 0) {
      stop();
      return;
    }
    sourceScanProgressUnlisten = stop;
  } catch {
    // Older controllers have no progress event; source reads remain authoritative.
  }
}

async function clearSourceScanProgressSetup(setup: Promise<void>): Promise<void> {
  await setup;
  if (sourceScanProgressSetup === setup) sourceScanProgressSetup = null;
}

function stopSourceScanProgressListener(): void {
  sourceScanProgressGeneration += 1;
  sourceScanProgressUnlisten?.();
  sourceScanProgressUnlisten = null;
  sourceScanProgressSetup = null;
}

export async function startTerminalSessionFromTauri(
  request: TerminalStartRequest,
  signal?: AbortSignal
): Promise<TerminalSessionInfo | null> {
  if (!isTauriRuntime() || signal?.aborted) return null;
  // A remote session's shell starts over the network, so leaving cancels that request.
  const requestId = createAgentConversationRequestId();
  const { invoke } = await import('./workspaceInvoke.ts');
  const cancel = (): void => {
    void cancelAgentConversationRequestFromTauri(requestId);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    return await invoke<TerminalSessionInfo>('start_terminal_session', { request, requestId });
  } catch (error) {
    if (signal?.aborted) return null;
    throw error;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

export async function listTerminalSessionsFromTauri(): Promise<TerminalSessionInfo[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<TerminalSessionInfo[]>('list_terminal_sessions');
}

export async function readResourceSnapshotFromTauri(): Promise<ResourceSnapshot | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<ResourceSnapshot>('read_resource_snapshot');
}

export async function cancelResourceSnapshotFromTauri(): Promise<void> {
  if (!isTauriRuntime()) return;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke('cancel_resource_snapshot');
}

export async function readResourceDiskScanFromTauri(
  roots: ResourceDiskRoot[],
  maxDepth = 3,
  maxEntries = 2000
): Promise<DiskScanReport | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<DiskScanReport>('read_resource_disk_scan', { roots, maxDepth, maxEntries });
}

export async function cancelResourceDiskScanFromTauri(): Promise<void> {
  if (!isTauriRuntime()) return;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke('cancel_resource_disk_scan');
}

export async function stopOwnedResourceFromTauri(
  request: ResourceStopRequest
): Promise<ResourceCommandReceipt | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<ResourceCommandReceipt>('stop_owned_resource', { request });
}

export async function restartLanguageServerRootFromTauri(
  root: string
): Promise<ResourceUnavailable | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<ResourceUnavailable>('restart_language_server_root', { request: { root } });
}

export async function applyResourceMemoryPressureFromTauri(
  level: string
): Promise<ResourceUnavailable | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<ResourceUnavailable>('apply_resource_memory_pressure', { level });
}

export async function readLanguageServerLogFromTauri(
  root: string
): Promise<ResourceUnavailable | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<ResourceUnavailable>('read_language_server_log', { request: { root } });
}

export async function cleanupWorkspaceDiskEntryFromTauri(
  request: ResourceCleanupRequest
): Promise<ResourceCleanupReceipt | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<ResourceCleanupReceipt>('cleanup_workspace_disk_entry', { request });
}

export async function setActiveSourceRootFromTauri(root: string): Promise<string | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<string>('set_active_source_root', { request: { root } });
}

export async function readCurrentProviderUsageFromTauri(
  provider: string | null,
  instanceId: string | null
): Promise<ProviderUsageSnapshot | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<ProviderUsageSnapshot>('read_current_provider_usage', { provider, instanceId });
}

export async function readUsageSummaryFromTauri(query: UsageHistoryQuery = {}): Promise<UsageSummary | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<UsageSummary>('read_usage_summary', { query });
}

export async function readUsageBreakdownFromTauri(query: UsageHistoryQuery = {}): Promise<UsageBreakdownRow[] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<UsageBreakdownRow[]>('read_usage_breakdown', { query });
}

export async function readUsageProviderSummaryFromTauri(query: UsageHistoryQuery = {}): Promise<UsageProviderSummaryRow[] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<UsageProviderSummaryRow[]>('read_usage_provider_summary', { query });
}

export async function readUsageDailyFromTauri(query: UsageHistoryQuery = {}): Promise<UsageDailyRow[] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<UsageDailyRow[]>('read_usage_daily', { query });
}

export async function readUsageDailyTotalsFromTauri(query: UsageHistoryQuery = {}): Promise<UsageDailyTotalsRow[] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<UsageDailyTotalsRow[]>('read_usage_daily_totals', { query });
}

export async function refreshUsageHistoryFromTauri(): Promise<number | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<number>('refresh_usage_history');
}

/** Which installed CLI or API service runs the Helper job. */
export type HelperVendor = 'openai' | 'anthropic';
export type HelperRoute = 'cli' | 'api';

/** What Settings needs to draw the Helper section. The model lists come from
 *  the backend so that the ids live in one place. */
export type HelperSettingsView = {
  vendor: HelperVendor | null;
  model: string;
  route: HelperRoute;
  hasKey: boolean;
  openaiModels: string[];
  anthropicModels: string[];
};

/** The answer to the Test button: a sentence either way. */
export type HelperTestResult = {
  ok: boolean;
  message: string;
};

export async function readHelperSettingsFromTauri(): Promise<HelperSettingsView | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<HelperSettingsView>('read_helper_settings');
}

export async function writeHelperSettingsFromTauri(
  settings: { vendor: HelperVendor | null; model: string; route: HelperRoute }
): Promise<void> {
  if (!isTauriRuntime()) return;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke<void>('write_helper_settings', { settings });
}

/** Stores the key in the Keychain. An empty key removes the one that is there. */
export async function setHelperKeyFromTauri(vendor: HelperVendor, key: string): Promise<void> {
  if (!isTauriRuntime()) return;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke<void>('set_helper_key', { vendor, key });
}

export async function testHelperFromTauri(): Promise<HelperTestResult | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<HelperTestResult>('test_helper');
}

/**
 * Ask the utility model to do one small job and give back its answer.
 *
 * Unlike its neighbours this cannot answer with `null`: an empty answer and a
 * utility model that is switched off would look the same to the caller. Away
 * from the desktop app there is no utility model at all, so this rejects with the sentence to
 * show instead. The backend rejects the same way — one plain-English sentence,
 * "No key — utility model off" among them.
 */
export async function runHelperJobFromTauri(
  job: 'title' | 'inspect' | 'review',
  input: string
): Promise<string> {
  if (!isTauriRuntime()) throw new Error('The utility model only runs in the desktop app.');
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<string>('run_helper_job', { job, input });
}

export async function readTerminalSessionScrollbackFromTauri(
  sessionId: string,
  maxBytes?: number
): Promise<TerminalScrollbackSnapshot | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<TerminalScrollbackSnapshot | null>('read_terminal_session_scrollback', {
    sessionId,
    maxBytes
  });
}

export async function writeTerminalSessionFromTauri(
  sessionId: string,
  data: string
): Promise<boolean> {
  if (!isTauriRuntime()) {
    return false;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<boolean>('write_terminal_session', { sessionId, data });
}

export async function resizeTerminalSessionFromTauri(
  sessionId: string,
  cols: number,
  rows: number
): Promise<boolean> {
  if (!isTauriRuntime()) {
    return false;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<boolean>('resize_terminal_session', { sessionId, cols, rows });
}

export async function closeTerminalSessionFromTauri(sessionId: string): Promise<boolean> {
  if (!isTauriRuntime()) {
    return false;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<boolean>('close_terminal_session', { sessionId });
}

async function registerProjectionStream<T>(input: {
  registerCommand: string;
  acknowledgeCommand: string;
  unregisterCommand: string;
  onEnvelope: (envelope: StreamEnvelope<T>) => void;
  onResync?: () => void;
}): Promise<ProjectionStreamRegistration | null> {
  if (!isTauriRuntime()) return null;

  const { invoke } = await import('./workspaceInvoke.ts');
  const registrationId = globalThis.crypto.randomUUID();
  let active = true;
  const owner: ProjectionAckOwner = {
    isActive: () => active,
    acknowledge: (frames, bytes) => invoke(input.acknowledgeCommand, {
      registrationId,
      frames,
      bytes
    })
  };
  const channel = new Channel<ProjectionStreamWireEnvelope<T>>((envelope) => {
    if (!active) return;
    try {
      if (envelope.resync || envelope.chunk === null) {
        input.onResync?.();
      } else {
        input.onEnvelope({
          sessionId: envelope.session,
          generation: envelope.generation,
          sequence: envelope.sequence,
          byteLength: envelope.bytes,
          kind: envelope.kind,
          chunk: envelope.chunk
        });
      }
    } finally {
      void acknowledgeProjectionEnvelope(owner, envelope.bytes);
    }
  });
  await invoke(input.registerCommand, { registrationId, channel });
  const stopTracking = trackTauriChannel();
  return {
    async unregister(): Promise<void> {
      if (!active) return;
      active = false;
      try {
        await invoke(input.unregisterCommand, { registrationId });
      } catch {
        // The webview may already be closing. Rust still bounds the abandoned
        // in-flight window, and a later registration atomically replaces it.
      } finally {
        stopTracking();
      }
    }
  };
}

type ProjectionAckOwner = {
  isActive(): boolean;
  acknowledge(frames: number, bytes: number): Promise<void>;
};

async function acknowledgeProjectionEnvelope(owner: ProjectionAckOwner, bytes: number): Promise<void> {
  try {
    if (!owner.isActive()) return;
    await owner.acknowledge(1, bytes);
  } catch {
    // A closed stream is already bounded by Rust's registration window.
  }
}

export async function registerAgentConversationStream(
  onEnvelope: (envelope: StreamEnvelope<AgentConversationEvent>) => void,
  onResync?: () => void
): Promise<ProjectionStreamRegistration | null> {
  const registration = await registerProjectionStream({
    registerCommand: 'register_agent_conversation_stream',
    acknowledgeCommand: 'acknowledge_agent_conversation_stream',
    unregisterCommand: 'unregister_agent_conversation_stream',
    onEnvelope,
    onResync
  });
  return registration;
}

/** The AG-UI channel: one envelope per native event, holding its chunks for the open chat views. */
export async function registerAguiStream(
  onEnvelope: (envelope: StreamEnvelope<StreamChunk[]>) => void,
  onResync: () => void
): Promise<ProjectionStreamRegistration | null> {
  return await registerProjectionStream({
    registerCommand: 'register_agui_stream',
    acknowledgeCommand: 'acknowledge_agui_stream',
    unregisterCommand: 'unregister_agui_stream',
    onEnvelope,
    onResync
  });
}

export async function registerTerminalOutputStream(
  onEnvelope: (envelope: StreamEnvelope<TerminalOutputPayload>) => void
): Promise<ProjectionStreamRegistration | null> {
  const registration = await registerProjectionStream({
    registerCommand: 'register_terminal_output_stream',
    acknowledgeCommand: 'acknowledge_terminal_output_stream',
    unregisterCommand: 'unregister_terminal_output_stream',
    onEnvelope
  });
  return registration;
}

export async function listenToTerminalOutput(
  handler: (envelope: TerminalOutputEnvelope) => void
): Promise<SessionSubscription | null> {
  const registration = await registerTerminalOutputStream((envelope) => {
    handler(envelope);
  });
  return registration
    ? { unsubscribe: () => void registration.unregister() }
    : null;
}

export async function readSourceFromTauri(record: SourceRecord): Promise<SourcePreview | null> {
  if (!isTauriRuntime()) {
    const preview = await postLocalSourceBridge<SourcePreview>('read', { path: record.path });
    return preview
      ? {
          ...preview,
          relativePath: record.relativePath,
          language: record.language,
          byteCount: record.byteCount
        }
      : null;
  }

  const generation = sourceFileReadGeneration;
  const { invoke } = await import('./workspaceInvoke.ts');
  const preview = await invoke<SourcePreview | null>('read_source_file', {
    path: record.path,
    generation
  });
  if (!preview) return null;
  return {
    ...preview,
    relativePath: record.relativePath,
    language: record.language,
    byteCount: record.byteCount
  };
}

/** Read one raster image for the editor without turning its bytes into JSON. */
export async function readSourceImageFromTauri(path: string, signal?: AbortSignal): Promise<Uint8Array> {
  if (!isTauriRuntime()) {
    throw new Error('Image preview is available in the desktop app.');
  }
  const { invoke } = await import('./workspaceInvoke.ts');
  const buffer = await invoke<ArrayBuffer>('read_source_image', { path }, undefined, signal);
  return new Uint8Array(buffer);
}

let sourceFileReadGeneration = 1;

export async function cancelSourceFileReadsFromTauri(): Promise<void> {
  if (!isTauriRuntime()) return;
  sourceFileReadGeneration += 1;
  const generation = sourceFileReadGeneration;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke('cancel_source_file_reads', { generation });
}

export async function readNativeCsharpFileFromTauri(
  root: string,
  path: string
): Promise<SourcePreview | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourcePreview>('read_native_csharp_file', { root, path });
}

export async function ensureNativeCsharpLanguageClientFromTauri(
  root: string
): Promise<{ wsUrl: string; root: string } | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<{ wsUrl: string; root: string }>('ensure_native_csharp_language_client', { root });
}

/** What the desktop app says about one project's editor mode. */
export interface WorkspaceLanguageIntelligence {
  root: string;
  enabled: boolean;
  /** Language-server processes running for this project right now. */
  runningServers: number;
  /** Their process ids — the same numbers the resource view shows. */
  serverPids: number[];
  /** How many were stopped by this call. */
  stoppedServers: number;
  message: string;
}

/**
 * Read whether full mode is on for a project. Costs nothing and starts nothing.
 * `null` outside the desktop app: a browser tab has no language servers at all.
 */
export async function readWorkspaceLanguageIntelligenceFromTauri(
  root: string
): Promise<WorkspaceLanguageIntelligence | null> {
  if (!isTauriRuntime() || !root.trim()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkspaceLanguageIntelligence>('read_workspace_language_intelligence', { root });
}

/**
 * Turn full mode on or off for a project. Pass the language of the file on
 * screen and turning it on starts that language's server now; leave it out and
 * the choice is only recorded, with the next file opened starting the server.
 * Off stops that project's language server now.
 */
export async function setWorkspaceLanguageIntelligenceFromTauri(
  root: string,
  enabled: boolean,
  language?: string | null
): Promise<WorkspaceLanguageIntelligence | null> {
  if (!isTauriRuntime() || !root.trim()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkspaceLanguageIntelligence>('set_workspace_language_intelligence', {
    root,
    enabled,
    language: language ?? null
  });
}

export async function markNativeCsharpLanguageClientReadyFromTauri(root: string): Promise<void> {
  if (!isTauriRuntime()) return;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke('mark_native_csharp_language_client_ready', { root });
}

export async function writeSourceToTauri(
  record: SourceRecord,
  content: string,
  expectedRevision: string
): Promise<SourcePreview | null> {
  if (!isTauriRuntime()) {
    const preview = await postLocalSourceBridge<SourcePreview>('write', {
      path: record.path,
      content,
      expectedRevision
    });
    return preview
      ? {
          ...preview,
          relativePath: record.relativePath,
          language: record.language
        }
      : null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  const preview = await invoke<SourcePreview>('write_source_file', {
    path: record.path,
    content,
    expectedRevision
  });
  return {
    ...preview,
    relativePath: record.relativePath,
    language: record.language
  };
}

export async function openSourceFileFromTauri(path: string): Promise<boolean> {
  return runPathCommand('open_source_file', path);
}

export async function revealSourceFileFromTauri(path: string): Promise<boolean> {
  return runPathCommand('reveal_source_file', path);
}

export async function openPathFromTauri(path: string): Promise<boolean> {
  return runPathCommand('open_path', path);
}

export async function revealPathFromTauri(path: string): Promise<boolean> {
  return runPathCommand('reveal_path', path);
}

/**
 * Move one path to the Finder's Trash. The file-system plugin's `remove`
 * deletes for good, so anything a person can undo goes through here instead.
 * Rejects when the path is outside the folders the window may change.
 */
export async function moveToTrashFromTauri(path: string): Promise<boolean> {
  if (!isTauriRuntime() || !path.trim()) {
    return false;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke('move_to_trash', { path });
  return true;
}

export async function openTerminalPathFromTauri(
  path: string,
  terminal = 'Warp'
): Promise<boolean> {
  if (!isTauriRuntime() || !path.trim()) {
    return false;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke('open_terminal_path', {
    path,
    terminal: terminal.trim() || null
  });
  return true;
}

export async function openTerminalCommandFromTauri(
  path: string,
  command: string,
  terminal = 'Warp'
): Promise<boolean> {
  if (!isTauriRuntime() || !path.trim() || !command.trim()) {
    return false;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke('open_terminal_command', {
    path,
    command,
    terminal: terminal.trim() || null
  });
  return true;
}

/**
 * Invoke one of the native browser commands when the shell is running inside
 * Tauri. The browser model keeps the command names and payload shapes in its
 * typed backend; this bridge owns the runtime check and IPC import.
 */
export async function invokeBrowserCommandFromTauri<T>(
  command: string,
  input: unknown
): Promise<T> {
  if (!isTauriRuntime()) {
    throw new Error('Native browser commands require the Tauri runtime');
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<T>(command, input as Record<string, unknown>);
}

export async function readProjectGitStatusFromTauri(
  root: string
): Promise<ProjectGitStatus | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<ProjectGitStatus>('project_git_status', { root });
}

export async function readSourceGitDiffFromTauri(
  root: string,
  path: string,
  signal?: AbortSignal
): Promise<SourceGitDiff | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceGitDiff>('read_source_git_diff', { root, path }, undefined, signal);
}

export async function stageGitPathsFromTauri(
  root: string,
  paths: string[]
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('stage_git_paths', { root, paths });
}

export async function unstageGitPathsFromTauri(
  root: string,
  paths: string[]
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('unstage_git_paths', { root, paths });
}

export async function commitGitRepositoryFromTauri(
  root: string,
  message: string
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('commit_git_repository', { root, message });
}

export async function fetchGitRepositoryFromTauri(
  root: string
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('fetch_git_repository', { root });
}

export async function pullGitRepositoryFromTauri(
  root: string
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('pull_git_repository', { root });
}

export async function pushGitRepositoryFromTauri(
  root: string
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('push_git_repository', { root });
}

export async function publishGitRepositoryFromTauri(
  root: string,
  expectedBranch: string,
  remote: string
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('publish_git_repository', { root, expectedBranch, remote });
}

export async function addGitRemoteFromTauri(
  root: string,
  name: string,
  url: string
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('add_git_remote', { root, name, url });
}

export async function discardGitPathsFromTauri(
  root: string,
  paths: string[]
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('discard_git_paths', { root, paths });
}

export async function discardAllGitChangesFromTauri(
  root: string,
  includeUntracked: boolean
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('discard_all_git_changes', { root, includeUntracked });
}

export async function listGitBranchesFromTauri(root: string, signal?: AbortSignal): Promise<GitBranchList | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitBranchList>('list_git_branches', { root }, undefined, signal);
}

/** Make the folder a repository. `path` stays machine-qualified for a remote folder. */
export async function initGitRepositoryFromTauri(path: string, signal: AbortSignal): Promise<void> {
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke<null>('workspace_git_init', { path }, undefined, signal);
}

export async function createGitBranchFromTauri(
  root: string,
  name: string,
  checkout: boolean
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('create_git_branch', { root, name, checkout });
}

export async function switchGitBranchFromTauri(
  root: string,
  name: string
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('switch_git_branch', { root, name });
}

export async function stashGitChangesFromTauri(
  root: string,
  includeUntracked: boolean,
  message: string
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('stash_git_changes', { root, includeUntracked, message });
}

export async function popGitStashFromTauri(
  root: string,
  index: number | null
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('pop_git_stash', { root, index });
}

export async function listGitStashesFromTauri(root: string): Promise<GitStashEntry[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitStashEntry[]>('list_git_stashes', { root });
}

export async function amendGitCommitFromTauri(
  root: string,
  message: string
): Promise<GitActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitActionResult>('amend_git_commit', { root, message });
}

export async function listOpenPullRequestsFromTauri(
  root: string
): Promise<PullRequestSummary[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<PullRequestSummary[]>('list_open_pull_requests', { root });
}

export async function listGithubPullRequestsFromTauri(
  query: GithubPullRequestQuery,
  signal?: AbortSignal
): Promise<GithubPullRequestPage | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GithubPullRequestPage>('list_github_pull_requests', { query }, undefined, signal);
}

export async function readGithubPullRequestFromTauri(
  root: string,
  number: number
): Promise<GithubPullRequestDetail | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GithubPullRequestDetail>('read_github_pull_request', { root, number });
}

export async function readGithubPullRequestFileFromTauri(
  query: GithubFileVersionQuery
): Promise<GithubFileVersions | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GithubFileVersions>('read_github_pull_request_file', { query });
}

export async function submitGithubPullRequestReviewFromTauri(
  submission: GithubReviewSubmission
): Promise<string> {
  if (!isTauriRuntime()) throw new Error('Reviews can only be submitted from the desktop app.');
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<string>('submit_github_pull_request_review', { submission });
}

export async function commentGithubPullRequestFromTauri(comment: GithubIssueComment): Promise<string> {
  if (!isTauriRuntime()) throw new Error('Comments can only be posted from the desktop app.');
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<string>('comment_github_pull_request', { comment });
}

export async function replyGithubPullRequestCommentFromTauri(
  reply: GithubReviewReply
): Promise<string> {
  if (!isTauriRuntime()) throw new Error('Review replies can only be submitted from the desktop app.');
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<string>('reply_github_pull_request_comment', { reply });
}

export async function mergeGithubPullRequestFromTauri(
  request: GithubMergeRequest
): Promise<string> {
  if (!isTauriRuntime()) throw new Error('Pull requests can only be merged from the desktop app.');
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<string>('merge_github_pull_request', { request });
}

export async function generateCommitMessageFromTauri(
  request: AgentGenerationRequest
): Promise<string | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<string>('generate_commit_message', { request });
}

export async function readPullRequestContextFromTauri(
  root: string
): Promise<PullRequestContext | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<PullRequestContext>('read_pull_request_context', { root });
}

export async function generatePullRequestDetailsFromTauri(
  request: AgentGenerationRequest
): Promise<PullRequestDetails | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<PullRequestDetails>('generate_pull_request_details', { request });
}

export async function createPullRequestFromTauri(input: {
  root: string;
  title: string;
  description: string;
  base: string;
  draft: boolean;
}): Promise<PullRequestCreated | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<PullRequestCreated>('create_pull_request', input);
}

export async function readPullRequestStatusFromTauri(
  root: string,
  branch: string
): Promise<PullRequestStatus | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<PullRequestStatus>('read_pull_request_status', { root, branch });
}

export async function readGitCommitHistoryFromTauri(
  root: string,
  cursor: string | null = null,
  relativePath: string | null = null
): Promise<GitHistoryPage | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitHistoryPage>('read_git_commit_history', { root, cursor, relativePath });
}

export async function listProjectWorktreesFromTauri(
  root: string
): Promise<ProjectWorktree[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<ProjectWorktree[]>('list_project_worktrees', { root });
}

export async function removeProjectWorktreeFromTauri(
  root: string,
  path: string
): Promise<ProjectWorktreeActionResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<ProjectWorktreeActionResult>('remove_project_worktree', { root, path });
}

export async function archiveProjectWorktreeFromTauri(
  root: string,
  path: string
): Promise<ProjectWorktreeArchiveResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<ProjectWorktreeArchiveResult>('archive_project_worktree', { root, path });
}

export async function listGitRepositorySummariesFromTauri(
  projects: RuntimeContextProject[]
): Promise<GitRepositorySummary[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<GitRepositorySummary[]>('list_git_repository_summaries', { projects });
}

/** This Mac's past sessions, or with `remoteProfileId` the ones that machine's own scanner finds. */
export async function listAgentSessionsFromTauri(remoteProfileId: string | null = null): Promise<AgentSession[] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<AgentSession[]>('list_agent_sessions', { remoteProfileId });
}

export async function listAgentSessionsForProjectFromTauri(
  path: string
): Promise<AgentSession[] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<AgentSession[]>('list_agent_sessions_for_project', { projectPath: path });
}

export async function readAgentSessionDetailsFromTauri(
  logPath: string,
  remoteProfileId: string | null = null
): Promise<AgentSession[] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<AgentSession[]>('read_agent_session_details', { logPath, remoteProfileId });
}

export interface RemoteSavedSessionDetails {
  turnCount: number | null;
  firstPrompt: string | null;
  startComplete: boolean;
  latestTurns: AgentSessionTurn[];
}

export async function readRemoteSavedSessionDetailsFromTauri(
  profileId: string, ownedId: string, signal: AbortSignal
): Promise<RemoteSavedSessionDetails | null> {
  if (signal.aborted || !isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  if (signal.aborted) return null;
  const details = await invoke<RemoteSavedSessionDetails>('read_remote_saved_session_details', { profileId, ownedId });
  return signal.aborted ? null : details;
}

/** One checkout of a repository: its own folder, or one of its worktrees. */
export type RepositoryCheckout = {
  path: string;
  branch: string;
  /** True for the repository's own folder rather than one of its worktrees. */
  isMain: boolean;
};

/**
 * The checkouts of each repository that are still on disk, keyed by the
 * repository root that was asked about.
 *
 * History draws its tree from this rather than from wherever sessions happen to
 * have been run, so a worktree that only ever hosted dispatched lanes still
 * appears. Deleted checkouts are left out by the backend.
 */
export async function listRepositoryCheckoutsFromTauri(
  roots: readonly string[], signal?: AbortSignal
): Promise<Record<string, RepositoryCheckout[]> | null> {
  if (!isTauriRuntime() || roots.length === 0) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<Record<string, RepositoryCheckout[]>>('list_repository_checkouts', { roots }, undefined, signal);
}

export async function readAgentConversationCapabilitiesFromTauri(
  ownedId: string,
  signal?: AbortSignal
): Promise<AgentConversationCapabilities | null> {
  if (!isTauriRuntime() || !ownedId.trim() || signal?.aborted) return null;
  const requestId = createAgentConversationRequestId();
  const { invoke } = await import('./workspaceInvoke.ts');
  const cancel = (): void => {
    void cancelAgentConversationRequestFromTauri(requestId);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    if (signal?.aborted) {
      await cancelAgentConversationRequestFromTauri(requestId);
      return null;
    }
    const capabilities = await invoke<AgentConversationCapabilities>('read_agent_conversation_capabilities', {
      ownedId,
      requestId
    });
    return signal?.aborted ? null : capabilities;
  } catch (error) {
    if (signal?.aborted) return null;
    throw error;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

// Millisecond epoch plus room for rapid calls stays within JavaScript's exact
// integer range and remains newer after a frontend hot reload.
let latestAgentConversationRequest = Math.trunc(Date.now() * 1_000);

export function createAgentConversationRequestId(): number {
  latestAgentConversationRequest += 1;
  return latestAgentConversationRequest;
}

export async function cancelAgentConversationRequestFromTauri(requestId: number): Promise<void> {
  if (!isTauriRuntime()) return;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke('cancel_agent_conversation_request', { requestId });
}

export async function probeAgentProviderConfigFromTauri(
  input: {
    provider: AgentConversationProvider;
    executionEnvironment: ExecutionEnvironment;
    remoteProfileId: string | null;
    cwd: string;
  },
  signal?: AbortSignal
): Promise<AgentConversationConfigState | null> {
  if (!isTauriRuntime() || signal?.aborted) return null;
  const requestId = createAgentConversationRequestId();
  const { invoke } = await import('./workspaceInvoke.ts');
  const cancel = (): void => { void invoke('cancel_agent_provider_probe', { requestId }); };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    if (signal?.aborted) return null;
    const result = await invoke<AgentConversationConfigState>('probe_agent_provider_config', {
      ...input,
      requestId
    });
    return signal?.aborted ? null : result;
  } catch (error) {
    if (signal?.aborted) return null;
    throw error;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

export type HistoryDownloadProgress = {
  profileId: string;
  sourceOwnedId: string;
  workspaceOwnedId: string;
  childSessionId: string | null;
  totalBytes: number;
  coveredBytes: number;
  phase: 'downloading' | 'paused' | 'complete' | 'error';
  ready: boolean;
  error: string | null;
};

export async function listenHistoryDownloadProgress(handler: (progress: HistoryDownloadProgress) => void, signal: AbortSignal): Promise<void> {
  if (!isTauriRuntime() || signal.aborted) return;
  const { listen } = await import('@tauri-apps/api/event');
  if (signal.aborted) return;
  const stop = trackTauriListener(await listen<HistoryDownloadProgress>('agent-conversation-history-download', (event) => {
    if (!signal.aborted) handler(event.payload);
  }));
  if (signal.aborted) { stop(); return; }
  signal.addEventListener('abort', stop, { once: true });
}

export async function readHistoryDownloadProgressFromTauri(ownedId: string, signal: AbortSignal): Promise<HistoryDownloadProgress | null> {
  if (!isTauriRuntime() || signal.aborted) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  if (signal.aborted) return null;
  const progress = await invoke<HistoryDownloadProgress | null>('read_agent_conversation_history_download', { ownedId });
  return signal.aborted ? null : progress;
}

export async function readAgentConversationSelectionFromTauri(
  ownedId: string,
  maxBytes: number,
  minimumGeneration?: number,
  signal?: AbortSignal
): Promise<AguiSnapshot | null> {
  if (!isTauriRuntime() || !ownedId.trim() || signal?.aborted) return null;
  const requestId = createAgentConversationRequestId();
  const cancel = (): void => { void cancelAgentConversationRequestFromTauri(requestId); };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    if (signal?.aborted) return null;
    const { invoke } = await import('./workspaceInvoke.ts');
    // Opens the session's view on the AG-UI channel as well.
    const snapshot = await invoke<AguiSnapshot | null>(
      'read_agui_snapshot',
      { ownedId, requestId, maxBytes, minimumGeneration }
    );
    if (signal?.aborted || !snapshot) return null;
    return {
      ...snapshot,
      page: {
        ...snapshot.page,
        beforeCursor: snapshot.page.beforeCursor ?? undefined,
        afterCursor: snapshot.page.afterCursor ?? undefined
      }
    };
  } catch (error) {
    if (signal?.aborted) return null;
    throw error;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

export async function readAgentConversationChildHistoryFromTauri(input: {
  parentOwnedId: string;
  childSessionId: string;
  requestId: number;
  maxBytes: number;
  signal?: AbortSignal;
}): Promise<AgentConversationChildHistory> {
  const signal = input.signal;
  const cancel = (): void => { void cancelAgentConversationRequestFromTauri(input.requestId); };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    if (signal?.aborted) return {} as AgentConversationChildHistory;
    const { invoke } = await import('./workspaceInvoke.ts');
    if (signal?.aborted) return {} as AgentConversationChildHistory;
    const { signal: _signal, ...args } = input;
    const result = await invoke<AgentConversationChildHistory>('read_agent_conversation_child_history', args);
    return signal?.aborted ? {} as AgentConversationChildHistory : result;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

export async function stopAgentConversationChildHistoryFromTauri(
  parentOwnedId: string,
  requestId: number
): Promise<boolean> {
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<boolean>('stop_agent_conversation_child_history', { parentOwnedId, requestId });
}

export async function listAgentConversationSessionsFromTauri(): Promise<AgentConversationSessionRecord[] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<AgentConversationSessionRecord[]>('list_agent_conversation_sessions');
}

/** Marks a session as the owner's main orchestrator on its machine and clears the mark elsewhere; `null` clears it everywhere. */
export async function setMainOrchestratorFromTauri(ownedId: string | null, remoteProfileId: string | null): Promise<void> {
  if (!isTauriRuntime()) return;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke<void>('set_main_orchestrator', { ownedId, remoteProfileId });
}

/** One agent-to-agent message in the owner's traffic view; `machine` is where it is stored (its receiver's machine). */
export type AgentMessage = {
  id: string;
  createdAtMs: number;
  senderName: string;
  fromOwnedId: string;
  toOwnedId: string;
  receiver: string;
  body: string;
  receipt: 'queued' | 'delivered' | 'failed' | 'expired';
  runId: string | null;
  kind: string;
  machine: string;
};

export type AgentMessageFilter = {
  machine?: string | null;
  runId?: string | null;
  session?: string | null;
  status?: AgentMessage['receipt'] | null;
  search?: string;
  sort?: 'time' | 'sender' | 'receiver';
  /** The last message already shown, with its name key when sorting by name. */
  before?: (Pick<AgentMessage, 'createdAtMs' | 'id'> & { key?: string }) | null;
  limit: number;
};

/** A page of agent-to-agent messages, newest first, from one machine or every reachable one. */
export async function listAgentMessagesFromTauri(filter: AgentMessageFilter, signal: AbortSignal): Promise<AgentMessage[] | null> {
  return invokeCancellable<AgentMessage[]>('list_agent_messages', {
    machine: filter.machine ?? null,
    runId: filter.runId ?? null,
    session: filter.session ?? null,
    status: filter.status ?? null,
    search: filter.search ?? '',
    sort: filter.sort ?? 'time',
    beforeMs: filter.before?.createdAtMs ?? null,
    beforeId: filter.before?.id ?? null,
    beforeKey: filter.before?.key ?? null,
    limit: filter.limit
  }, signal);
}

export async function countAgentMessagesFromTauri(session: string, afterMs: number, signal: AbortSignal): Promise<number | null> {
  return invokeCancellable<number>('count_agent_messages', { session, afterMs }, signal);
}

/** The owner aborts this listener when its view closes, including during registration. */
export async function listenAgentMessagesChanged(handler: () => void, signal: AbortSignal): Promise<void> {
  if (!isTauriRuntime() || signal.aborted) return;
  const { listen } = await import('@tauri-apps/api/event');
  if (signal.aborted) return;
  const stop = trackTauriListener(await listen('agent-messages-changed', () => {
    if (!signal.aborted) handler();
  }));
  if (signal.aborted) { stop(); return; }
  signal.addEventListener('abort', stop, { once: true });
}

/** A workflow step waiting on the owner: a pause to answer (`waiting-input`) or a gate to approve (`waiting-approval`). */
export type OwnerInboxStep = {
  runId: string;
  taskKey: string | null;
  workflowName: string | null;
  nodeId: string;
  state: 'waiting-input' | 'waiting-approval';
  reason: string | null;
  ownedId: string | null;
  updatedAtMs: number;
  /** `mac` for this Mac, otherwise the remote profile id that owns the run. */
  machine: string;
};

/** Workflow steps waiting on the owner on this Mac and every connected machine. */
export async function listOwnerInboxFromTauri(signal: AbortSignal): Promise<OwnerInboxStep[] | null> {
  return invokeCancellable<OwnerInboxStep[]>('list_owner_inbox', {}, signal);
}

/** Invokes a command that reads other machines; aborting `signal` cancels it there and returns null. */
export async function invokeCancellable<T>(command: string, args: Record<string, unknown>, signal: AbortSignal): Promise<T | null> {
  if (!isTauriRuntime() || signal.aborted) return null;
  const requestId = createAgentConversationRequestId();
  const { invoke } = await import('./workspaceInvoke.ts');
  const cancel = (): void => {
    void cancelAgentConversationRequestFromTauri(requestId);
  };
  signal.addEventListener('abort', cancel, { once: true });
  try {
    if (signal.aborted) return null;
    return await invoke<T>(command, { ...args, requestId });
  } catch (error) {
    if (signal.aborted) return null;
    throw error;
  } finally {
    signal.removeEventListener('abort', cancel);
  }
}

export async function listRemoteAgentConversationSessionsFromTauri(
  signal?: AbortSignal
): Promise<AgentConversationSessionRecord[] | null> {
  if (!isTauriRuntime() || signal?.aborted) return null;
  const requestId = createAgentConversationRequestId();
  const { invoke } = await import('./workspaceInvoke.ts');
  const cancel = (): void => {
    void cancelAgentConversationRequestFromTauri(requestId);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    if (signal?.aborted) {
      await cancelAgentConversationRequestFromTauri(requestId);
      return null;
    }
    return await invoke<AgentConversationSessionRecord[]>('list_remote_agent_conversation_sessions', {
      requestId
    });
  } catch (error) {
    if (signal?.aborted) return null;
    throw error;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

export async function readRemoteAssemblyEnvironmentFromTauri(): Promise<RemoteAssemblyEnvironment> {
  if (!isTauriRuntime()) {
    return { profiles: [], readyProfileIds: [] };
  }
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<RemoteAssemblyEnvironment>('read_remote_assembly_environment');
}

export async function readRemoteBackendStatusesFromTauri(
  profiles: RemoteAssemblyProfile[]
): Promise<RemoteBackendProfileStatus[]> {
  if (!isTauriRuntime() || profiles.length === 0) return [];
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<RemoteBackendProfileStatus[]>('read_remote_backend_statuses', { profiles });
}

export async function removeRemoteAssemblyProfileFromTauri(
  profileId: string
): Promise<RemoteAssemblyEnvironment> {
  if (!isTauriRuntime()) throw new Error('Remote setup is available in the desktop app.');
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<RemoteAssemblyEnvironment>('remove_remote_assembly_profile', { profileId });
}

export async function disconnectRemoteAssemblyFromTauri(profileId: string): Promise<RemoteAssemblyEnvironment> {
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<RemoteAssemblyEnvironment>('disconnect_remote_assembly', { profileId });
}

export async function connectRemoteAssemblyFromTauri(
  profile: RemoteAssemblyProfile,
  signal: AbortSignal,
  onStatus: (status: string) => void
): Promise<{ profile: RemoteAssemblyProfile; sessions: AgentConversationSessionRecord[]; replacedProfileId: string | null }> {
  if (!isTauriRuntime()) throw new Error('Remote setup is available in the desktop app.');
  if (signal.aborted) throw new Error('Connection cancelled');
  const { invoke, Channel } = await import('@tauri-apps/api/core');
  const operationId = crypto.randomUUID();
  let registered = false;
  const cancel = () => {
    if (registered) void cancelRemoteConnection(operationId);
  };
  const status = new Channel<string>((message) => {
    registered = true;
    if (signal.aborted) cancel();
    else onStatus(message);
  });
  signal.addEventListener('abort', cancel, { once: true });
  try {
    return await invoke('connect_remote_assembly', { profile, operationId, status });
  } finally {
    signal.removeEventListener('abort', cancel);
  }
}

/** Stops a running connect or install; a failed cancel leaves the attempt to its own timeout. */
async function cancelRemoteConnection(operationId: string): Promise<void> {
  const { invoke } = await import('@tauri-apps/api/core');
  try {
    await invoke('cancel_remote_connection', { operationId });
  } catch (error) {
    console.warn('Could not cancel the remote connection', error);
  }
}

export type RemoteInstallUpdate = string | {
  kind: 'transfer'; stage: 'download'; bytes: number; total: number;
  bytesPerSecond: number | null; etaSeconds: number | null;
};

export async function installRemoteAssemblyFromTauri(
  profile: RemoteAssemblyProfile,
  signal: AbortSignal,
  onStatus: (status: RemoteInstallUpdate) => void
): Promise<{ profile: RemoteAssemblyProfile; sessions: AgentConversationSessionRecord[]; replacedProfileId: string | null }> {
  if (!isTauriRuntime()) throw new Error('Remote installation is available in the desktop app.');
  if (signal.aborted) throw new Error('Installation cancelled');
  const { invoke, Channel } = await import('@tauri-apps/api/core');
  const operationId = crypto.randomUUID();
  let registered = false;
  const cancel = () => {
    if (registered) void cancelRemoteConnection(operationId);
  };
  const status = new Channel<string>((message) => {
    registered = true;
    if (signal.aborted) cancel();
    else {
      try {
        const update = JSON.parse(message) as RemoteInstallUpdate;
        onStatus(typeof update === 'object' && update?.kind === 'transfer' ? update : message);
      } catch {
        onStatus(message);
      }
    }
  });
  signal.addEventListener('abort', cancel, { once: true });
  try {
    return await invoke('install_remote_assembly', { profile, operationId, status });
  } finally {
    signal.removeEventListener('abort', cancel);
  }
}

export async function deployRemoteBackendDev(
  destination: string,
  onLine: (line: string) => void
): Promise<void> {
  if (!isTauriRuntime()) throw new Error('Development deployment is available in the desktop app.');
  const { invoke, Channel } = await import('@tauri-apps/api/core');
  const onEvent = new Channel<string>(onLine);
  await invoke('deploy_remote_backend_dev', { destination, onEvent });
}

export async function uninstallRemoteAssemblyFromTauri(
  profile: RemoteAssemblyProfile,
  deleteData: boolean
): Promise<{ environment: RemoteAssemblyEnvironment; replacedProfileId: string | null }> {
  if (!isTauriRuntime()) throw new Error('Remote uninstall is available in the desktop app.');
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<{ environment: RemoteAssemblyEnvironment; replacedProfileId: string | null }>(
    'uninstall_remote_assembly',
    { profile, deleteData }
  );
}

export async function changeAgentConversationCheckoutFromTauri(input: {
  ownedId: string;
  generation: number;
  cwd: string;
}): Promise<AgentConversationSessionRecord | null> {
  if (!isTauriRuntime() || !input.ownedId.trim() || !input.cwd.trim()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<AgentConversationSessionRecord>('change_agent_conversation_checkout', {
    request: input
  });
}

export async function listAgentConversationEventsFromTauri(
  ownedId: string,
  fromSequence = 0
): Promise<AgentConversationEvent[] | null> {
  if (!isTauriRuntime() || !ownedId.trim()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<AgentConversationEvent[]>('list_agent_conversation_events', { ownedId, fromSequence });
}

/** The page of stored events just older than `beforeSequence`, for scrolling up. */
export async function listAgentConversationEventsBeforeFromTauri(
  ownedId: string,
  beforeSequence: number,
  maxBytes: number,
  signal?: AbortSignal
): Promise<AgentConversationEventPage | null> {
  if (!isTauriRuntime() || !ownedId.trim() || signal?.aborted) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  try {
    const page = await invoke<AgentConversationEventPage>('list_agent_conversation_events_before', {
      ownedId,
      beforeSequence,
      maxBytes
    });
    return signal?.aborted ? null : page;
  } catch (error) {
    if (signal?.aborted) return null;
    throw error;
  }
}

export async function listAgentConversationItemsBeforeFromTauri(
  ownedId: string,
  beforeSequence: number,
  maxBytes: number,
  signal?: AbortSignal
): Promise<AguiPage | null> {
  if (!isTauriRuntime() || !ownedId.trim() || signal?.aborted) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  const requestId = createAgentConversationRequestId();
  const cancel = (): void => { void cancelAgentConversationRequestInBackground(requestId); };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    const page = await invoke<AguiPage>('list_agui_events_before', {
      ownedId, beforeSequence, maxBytes, requestId
    });
    return signal?.aborted ? null : { ...page, beforeCursor: page.beforeCursor ?? undefined, afterCursor: page.afterCursor ?? undefined };
  } catch (error) {
    if (signal?.aborted) return null;
    throw error;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

export async function listAgentConversationItemsAfterFromTauri(
  ownedId: string,
  afterSequence: number,
  maxBytes: number,
  signal?: AbortSignal
): Promise<AguiPage | null> {
  if (!isTauriRuntime() || !ownedId.trim() || signal?.aborted) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  const requestId = createAgentConversationRequestId();
  const cancel = (): void => { void cancelAgentConversationRequestInBackground(requestId); };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    const page = await invoke<AguiPage>('list_agui_events_after', {
      ownedId, afterSequence, maxBytes, requestId
    });
    return signal?.aborted ? null : { ...page, beforeCursor: page.beforeCursor ?? undefined, afterCursor: page.afterCursor ?? undefined };
  } catch (error) {
    if (signal?.aborted) return null;
    throw error;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

/** A tool call's whole output and diff, read when its row is opened; null outside the app or once `signal` aborts. */
export async function readAgentConversationToolCallFromTauri(ownedId: string, itemId: string, signal: AbortSignal): Promise<{ output: string | null; diff: string | null } | null> {
  if (!isTauriRuntime() || signal.aborted) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  const call = await invoke<{ output: string | null; diff: string | null }>('read_agent_conversation_tool_call', { ownedId, itemId });
  return signal.aborted ? null : call;
}

/** For an abort listener: cancels the request and reports, rather than throws, a failure. */
export async function cancelAgentConversationRequestInBackground(requestId: number): Promise<void> {
  try {
    await cancelAgentConversationRequestFromTauri(requestId);
  } catch (error) {
    console.warn('Could not cancel the backend request', error);
  }
}

/** The chat view closed: the backend stops projecting its session to the AG-UI channel. The session goes on. */
export async function closeAguiViewInBackground(ownedId: string): Promise<void> {
  if (!isTauriRuntime()) return;
  try { await (await import('./workspaceInvoke.ts')).invoke('close_agui_view', { ownedId }); } catch (error) { console.warn('Could not close the chat view', error); }
}

export async function updateAgentConversationSessionMetaFromTauri(input: {
  ownedId: string;
  model: string | null;
  effort: string | null;
  meta: AgentConversationSessionMeta;
}): Promise<AgentConversationSessionRecord | null> {
  if (!isTauriRuntime() || !input.ownedId.trim()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<AgentConversationSessionRecord>('update_agent_conversation_session_meta', {
    request: input
  });
}

/** Takes a conversation out of the store for good; the agent's own transcript
 * on disk stays. Answers whether there was anything to delete. */
export async function deleteAgentConversationSessionFromTauri(ownedId: string): Promise<boolean> {
  if (!isTauriRuntime() || !ownedId.trim()) return false;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<boolean>('delete_agent_conversation_session', { ownedId });
}

export async function writeAgentConversationWorkspaceFromTauri(
  ownedId: string,
  snapshot: SessionWorkspaceSnapshot, profileId: string | null
): Promise<boolean> {
  if (!isTauriRuntime()) return true;
  if (!ownedId.trim()) return false;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke<void>('write_agent_conversation_workspace', {
    ownedId,
    snapshotJson: JSON.stringify(mapWorkspaceSnapshotPaths(snapshot, profileId, 'save'))
  });
  return true;
}

export async function readAgentConversationWorkspaceFromTauri(
  ownedId: string
): Promise<SessionWorkspaceSnapshot | null> {
  if (!isTauriRuntime() || !ownedId.trim()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  const snapshot = await invoke<string | null>('read_agent_conversation_workspace', { ownedId });
  if (snapshot === null) return null;
  const parsed: unknown = JSON.parse(snapshot);
  return normalizeWorkspaceSnapshot(parsed);
}

export async function readAgentConversationWorkspaceExpandedPathsFromTauri(
  ownedId: string,
  root: string,
  signal?: AbortSignal
): Promise<string[]> {
  if (!isTauriRuntime() || !ownedId.trim() || !root.trim() || signal?.aborted) return [];
  const requestId = createAgentConversationRequestId();
  const { invoke } = await import('@tauri-apps/api/core');
  const cancel = (): void => {
    void cancelAgentConversationRequestFromTauri(requestId);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    if (signal?.aborted) {
      await cancelAgentConversationRequestFromTauri(requestId);
      return [];
    }
    const paths = await invoke<string[]>('read_agent_conversation_workspace_expanded_paths', {
      ownedId,
      root: parseRemoteWorkspacePath(root)?.path ?? root,
      requestId
    });
    const remote = parseRemoteWorkspacePath(root);
    return signal?.aborted ? [] : remote ? paths.map((path) => remoteWorkspacePath(remote.profileId, path)) : paths;
  } catch (error) {
    if (signal?.aborted) return [];
    throw error;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

export async function writeAgentConversationWorkspaceExpandedPathsFromTauri(
  ownedId: string,
  root: string,
  paths: readonly string[]
): Promise<void> {
  if (!isTauriRuntime() || !ownedId.trim() || !root.trim()) return;
  const { invoke } = await import('@tauri-apps/api/core');
  await invoke<void>('write_agent_conversation_workspace_expanded_paths', {
    ownedId,
    root: parseRemoteWorkspacePath(root)?.path ?? root,
    paths: paths.map((path) => parseRemoteWorkspacePath(path)?.path ?? path)
  });
}

export async function deleteAgentConversationWorkspaceFromTauri(ownedId: string): Promise<void> {
  if (!isTauriRuntime() || !ownedId.trim()) return;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke<void>('delete_agent_conversation_workspace', { ownedId });
}

export async function clearAgentConversationWorkspaceEditorsFromTauri(): Promise<void> {
  if (!isTauriRuntime()) return;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke<void>('clear_agent_conversation_workspace_editors');
}

export async function clearAgentConversationWorkspaceTabsFromTauri(): Promise<void> {
  if (!isTauriRuntime()) return;
  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke<void>('clear_agent_conversation_workspace_tabs');
}

async function completedAssemblySettingWrite(): Promise<void> {}

const completedAssemblySettingWritePromise = completedAssemblySettingWrite();
const assemblySettingWriteQueues = new Map<string, Promise<void>>();

export async function writeAssemblySettingFromTauri(
  settingKey: string,
  value: unknown
): Promise<void> {
  if (!isTauriRuntime() || !settingKey.trim()) return;
  const previous = assemblySettingWriteQueues.get(settingKey) ?? completedAssemblySettingWritePromise;
  let write!: Promise<void>;
  write = writeAssemblySettingInOrder(settingKey, value, previous, () => assemblySettingWriteQueues.get(settingKey) === write);
  assemblySettingWriteQueues.set(settingKey, write);
  await write;
}

async function writeAssemblySettingInOrder(
  settingKey: string,
  value: unknown,
  previous: Promise<void>,
  ownsQueueSlot: () => boolean
): Promise<void> {
  try {
    await previous;
  } catch {
    // A failed older write must not block the latest value.
  }
  try {
    const { invoke } = await import('./workspaceInvoke.ts');
    await invoke<void>('write_assembly_setting', {
      settingKey,
      valueJson: JSON.stringify(value)
    });
  } finally {
    if (ownsQueueSlot()) {
      assemblySettingWriteQueues.delete(settingKey);
    }
  }
}

export async function readAssemblySettingFromTauri(settingKey: string): Promise<unknown> {
  if (!isTauriRuntime() || !settingKey.trim()) return null;
  try {
    await assemblySettingWriteQueues.get(settingKey);
  } catch {
    // Reads should still proceed after a failed pending write.
  }
  const { invoke } = await import('./workspaceInvoke.ts');
  const valueJson = await invoke<string | null>('read_assembly_setting', { settingKey });
  return valueJson === null ? null : JSON.parse(valueJson) as unknown;
}

/** Everything the backend needs to read a past session's transcript file in. */
export interface AgentConversationTranscriptImport {
  provider: import('./shell/conversation/conversationTypes.ts').AgentConversationProvider;
  nativeSessionId: string;
  transcriptPath: string;
  cwd: string;
  /** What the past session was already called. Stored with the row, so the rail
   * still knows the name after it next reloads from the database. */
  title?: string | null;
  /** The saved machine the transcript is on; that machine names the session and reads its newest page. */
  remoteProfileId?: string | null;
}

/**
 * Name a provider's past transcript as an app-owned conversation and hand back
 * its id and whether a session already here was cleared to be read again, without reading any of it. This is the fast half of resuming: the
 * session exists, with its name and its agent, and can be shown at once.
 *
 * Reading the transcript is `finishAgentConversationImportFromTauri`, which the
 * caller runs next — alongside starting the agent, not before it.
 */
export async function beginAgentConversationImportFromTauri(
  request: AgentConversationTranscriptImport
): Promise<[ownedId: string, rebuilt: boolean] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<[string, boolean]>('begin_agent_conversation_import', { remoteProfileId: null, ...request });
}

/**
 * Read the end of a named import's transcript file into it, and report how many
 * events that added. The backend reads a bounded window of the file, so a long
 * session arrives with its most recent part first.
 */
export async function finishAgentConversationImportFromTauri(
  ownedId: string
): Promise<number | null> {
  if (!isTauriRuntime() || !ownedId.trim()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<number>('finish_agent_conversation_import', { ownedId });
}

/** What one reach further back into a transcript found. */
export interface ExtendedImport {
  added: number;
  reachedStart: boolean;
}

/** Read further back into an imported transcript, past what it already holds.
 * Reports what that added and whether the beginning has now been reached. A
 * stretch of transcript holding nothing a reader wants is not the beginning,
 * so the two are answered separately. */
export async function extendAgentConversationImportFromTauri(
  ownedId: string,
  signal?: AbortSignal
): Promise<ExtendedImport | null> {
  if (!isTauriRuntime() || !ownedId.trim() || signal?.aborted) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  if (signal?.aborted) return null;
  const result = await invoke<ExtendedImport>('extend_agent_conversation_import', { ownedId });
  return signal?.aborted ? null : result;
}

export async function listAgentSessionsFromLocalBridge(): Promise<AgentSession[] | null> {
  return postLocalSourceBridge<AgentSession[]>('agent-sessions', {});
}

export async function listRuntimeContextsFromTauri(
  projects: RuntimeContextProject[]
): Promise<RuntimeContext[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<RuntimeContext[]>('list_runtime_contexts', { projects });
}

export async function listPlaywrightSessionsFromTauri(): Promise<PlaywrightSessionInfo[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<PlaywrightSessionInfo[]>('list_playwright_sessions');
}

export async function killPlaywrightSessionsFromTauri(): Promise<PlaywrightCleanupResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<PlaywrightCleanupResult>('kill_playwright_sessions');
}

export async function listOrchestrationRunsFromTauri(
  projects: RuntimeContextProject[]
): Promise<OrchestrationRun[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<OrchestrationRun[]>('list_orchestration_runs', { projects });
}

export async function recordOrchestrationEventToTauri(
  event: OrchestrationEvent
): Promise<OrchestrationRun | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<OrchestrationRun>('record_orchestration_event', { event });
}

export async function listWorkflowRunsFromTauri(): Promise<WorkflowRunRecord[] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkflowRunRecord[]>('list_workflow_runs');
}

export async function listWorkflowRunHistoryFromTauri(runId: string): Promise<WorkflowHistoryRow[] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkflowHistoryRow[]>('workflow_history', { runId });
}

export async function createWorkflowRunFromTauri(
  repoRoot: string,
  workflowPath: string,
  task: WorkflowTaskSnapshot,
  input: Record<string, unknown>,
  idempotencyKey: string
): Promise<WorkflowRunRecord | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkflowRunRecord>('create_workflow_run', { repoRoot, workflowPath, task, input, idempotencyKey });
}

/** One workflow operation on a remote machine's own engine (`remote_workflow`). */
export async function remoteWorkflowFromTauri<T>(
  profileId: string,
  operation: string,
  args: object,
  signal?: AbortSignal
): Promise<T | null> {
  if (!isTauriRuntime() || signal?.aborted) return null;
  const requestId = signal ? createAgentConversationRequestId() : null;
  const { invoke } = await import('./workspaceInvoke.ts');
  const cancel = (): void => {
    if (requestId !== null) void cancelAgentConversationRequestInBackground(requestId);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    const result = await invoke<T>('remote_workflow', { profileId, operation, args, requestId });
    return signal?.aborted ? null : result;
  } catch (error) {
    if (signal?.aborted) return null;
    throw error;
  } finally {
    signal?.removeEventListener('abort', cancel);
  }
}

export async function listWorkflowFilesFromTauri(repoRoot: string): Promise<string[] | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<string[]>('list_workflow_files', { repoRoot });
}

export async function answerWorkflowPauseFromTauri(
  runId: string,
  nodeId: string,
  decision: WorkflowPauseDecision,
  reply: string | null,
  idempotencyKey: string
): Promise<WorkflowRunRecord | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkflowRunRecord>('answer_workflow_pause', { runId, nodeId, decision, reply, idempotencyKey });
}

export async function startWorkflowRunFromTauri(runId: string, idempotencyKey: string): Promise<WorkflowRunRecord | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkflowRunRecord>('start_workflow_run', { runId, idempotencyKey });
}

export async function pauseWorkflowRunFromTauri(runId: string, idempotencyKey: string): Promise<WorkflowRunRecord | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkflowRunRecord>('pause_workflow_run', { runId, idempotencyKey });
}

export async function resumeWorkflowRunFromTauri(runId: string, idempotencyKey: string): Promise<WorkflowRunRecord | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkflowRunRecord>('resume_workflow_run', { runId, idempotencyKey });
}

export async function cancelWorkflowRunFromTauri(runId: string, idempotencyKey: string): Promise<WorkflowRunRecord | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkflowRunRecord>('cancel_workflow_run', { runId, idempotencyKey });
}

export async function retryWorkflowNodeFromTauri(runId: string, nodeId: string, idempotencyKey: string): Promise<WorkflowRunRecord | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkflowRunRecord>('retry_workflow_node', { runId, nodeId, idempotencyKey });
}

export async function approveWorkflowGateFromTauri(
  runId: string,
  nodeId: string,
  approval: { approved: boolean; [key: string]: unknown },
  idempotencyKey: string
): Promise<WorkflowRunRecord | null> {
  if (!isTauriRuntime()) return null;
  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<WorkflowRunRecord>('approve_workflow_gate', { runId, nodeId, approval, idempotencyKey });
}

export async function searchSourceFilesFromTauri(
  records: SourceRecord[],
  query: string,
  limit = 50
): Promise<SourceSearchMatch[] | null> {
  if (!isTauriRuntime()) {
    return postLocalSourceBridge<SourceSearchMatch[]>('search', {
      records,
      query,
      limit
    });
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceSearchMatch[]>('search_source_files', {
    records,
    query,
    limit
  });
}

export async function findSourceDefinitionsFromTauri(
  records: SourceRecord[],
  symbolName: string,
  limit = 20
): Promise<SourceDefinitionTarget[] | null> {
  if (!isTauriRuntime()) {
    return postLocalSourceBridge<SourceDefinitionTarget[]>('definitions', {
      records,
      symbolName,
      limit
    });
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceDefinitionTarget[]>('find_source_definitions', {
    records,
    symbolName,
    limit
  });
}

export async function findSourceDefinitionsInRootFromTauri(
  root: string,
  symbolName: string,
  limit = 20
): Promise<SourceDefinitionTarget[] | null> {
  if (!isTauriRuntime()) return null;

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceDefinitionTarget[]>('find_source_definitions_in_root', {
    root,
    symbolName,
    limit
  });
}

export async function readSourceLspStatusFromTauri(
  root: string,
  language: string
): Promise<SourceLspStatus | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceLspStatus>('read_source_lsp_status', {
    root,
    language
  });
}

export async function readSourceLspReadinessFromTauri(
  root: string
): Promise<SourceLspStatus[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceLspStatus[]>('list_source_lsp_statuses', { root });
}

/**
 * Proactively re-point any already-running language server(s) at a freshly-selected
 * project root so the cold re-index warms in the background on switch, rather than on the
 * first file-open under the new project. No-op (returns null) outside the Tauri runtime,
 * and a no-op in the backend when no server is running for that root's languages. Returns
 * the count of running servers that were re-pointed.
 */
export async function warmSourceLspForRootFromTauri(root: string): Promise<number | null> {
  if (!isTauriRuntime() || !root.trim()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<number>('warm_source_lsp_for_root', { root });
}

export async function findSourceLspDefinitionsFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest
): Promise<SourceDefinitionTarget[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceDefinitionTarget[]>('find_source_lsp_definitions', {
    preview,
    request
  });
}

export async function findSourceLspCompletionsFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest
): Promise<SourceCompletionItem[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceCompletionItem[]>('find_source_lsp_completions', {
    preview,
    request
  });
}

export async function findSourceLspReferencesFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest
): Promise<SourceReferenceTarget[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceReferenceTarget[]>('find_source_lsp_references', {
    preview,
    request
  });
}

export async function findSourceLspImplementationsFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest
): Promise<SourceDefinitionTarget[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceDefinitionTarget[]>('find_source_lsp_implementations', {
    preview,
    request
  });
}

export async function findSourceLspTypeDefinitionsFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest
): Promise<SourceDefinitionTarget[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceDefinitionTarget[]>('find_source_lsp_type_definitions', {
    preview,
    request
  });
}

export async function findSourceLspDocumentHighlightsFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest
): Promise<SourceDocumentHighlight[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceDocumentHighlight[]>('find_source_lsp_document_highlights', {
    preview,
    request
  });
}

export async function formatSourceWithLspFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest
): Promise<SourceTextEdit[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceTextEdit[]>('format_source_with_lsp', {
    preview,
    request
  });
}

export async function renameSourceWithLspFromTauri(
  preview: SourcePreview,
  request: SourceLspRenameRequest
): Promise<SourceRenameResult | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceRenameResult>('rename_source_with_lsp', {
    preview,
    request
  });
}

export async function findSourceLspCodeActionsFromTauri(
  preview: SourcePreview,
  request: SourceLspCodeActionRequest
): Promise<SourceCodeAction[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceCodeAction[]>('find_source_lsp_code_actions', {
    preview,
    request
  });
}

export async function findSourceLspSignatureHelpFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest,
  signal?: AbortSignal
): Promise<SourceSignatureHelp | null> {
  if (!isTauriRuntime() || signal?.aborted) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceSignatureHelp | null>('find_source_lsp_signature_help', {
    preview,
    request
  });
}

export async function findSourceLspInlayHintsFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest,
  signal?: AbortSignal
): Promise<SourceInlayHint[] | null> {
  if (!isTauriRuntime() || signal?.aborted) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceInlayHint[]>('find_source_lsp_inlay_hints', {
    preview,
    request
  });
}

export async function findSourceLspSemanticTokensFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest,
  signal?: AbortSignal
): Promise<SourceSemanticToken[] | null> {
  if (!isTauriRuntime() || signal?.aborted) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceSemanticToken[]>('find_source_lsp_semantic_tokens', {
    preview,
    request
  });
}

export async function findSourceLspHoverFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest,
  signal?: AbortSignal
): Promise<SourceLspHover | null> {
  if (!isTauriRuntime() || signal?.aborted) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceLspHover | null>('find_source_lsp_hover', {
    preview,
    request
  });
}

export async function findSourceLspSymbolsFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest
): Promise<SourceSymbol[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceSymbol[]>('find_source_lsp_symbols', {
    preview,
    request
  });
}

export async function findSourceLspWorkspaceSymbolsFromTauri(
  preview: SourcePreview,
  request: SourceLspWorkspaceSymbolRequest,
  signal?: AbortSignal
): Promise<SourceWorkspaceSymbol[] | null> {
  if (!isTauriRuntime() || signal?.aborted) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceWorkspaceSymbol[]>('find_source_lsp_workspace_symbols', {
    preview,
    request
  });
}

export async function readSourceLspDiagnosticsFromTauri(
  preview: SourcePreview,
  request: SourceLspLookupRequest
): Promise<SourceDiagnostic[] | null> {
  if (!isTauriRuntime()) {
    return null;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceDiagnostic[]>('read_source_lsp_diagnostics', {
    preview,
    request
  });
}

export async function findSourceReferencesFromTauri(
  records: SourceRecord[],
  symbolName: string,
  limit = 50 ,signal?: AbortSignal
): Promise<SourceReferenceTarget[] | null> {
  if (!isTauriRuntime() || signal?.aborted) {
    return postLocalSourceBridge<SourceReferenceTarget[]>('references', {
      records,
      symbolName,
      limit
    });
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceReferenceTarget[]>('find_source_references', {
    records,
    symbolName,
    limit
  });
}

export async function findSourceReferencesInRootFromTauri(
  root: string,
  symbolName: string,
  limit = 50 ,
  signal?: AbortSignal
): Promise<SourceReferenceTarget[] | null> {
  if (!isTauriRuntime() || signal?.aborted) return null;

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceReferenceTarget[]>('find_source_references_in_root', {
    root,
    symbolName,
    limit
  });
}

/**
 * Count how many lines mention each of `symbolNames`, across the whole project
 * under `root`, in one pass.
 *
 * Deliberately takes no file list: the backend walks the project itself. The
 * margin counts used to send the entire scanned file list across the bridge
 * once per symbol, and with a hundred-odd symbols on screen that alone was
 * enough to lock up the app.
 */
export async function countSourceReferencesFromTauri(
  root: string,
  symbolNames: string[],
  deadlineMs?: number ,
  signal?: AbortSignal
): Promise<SourceReferenceCountResult | null> {
  if (!isTauriRuntime() || signal?.aborted) {
    return postLocalSourceBridge<SourceReferenceCountResult>('reference-counts', {
      root,
      symbolNames,
      deadlineMs: deadlineMs ?? null
    });
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  return invoke<SourceReferenceCountResult>('count_source_references', {
    root,
    symbolNames,
    deadlineMs: deadlineMs ?? null
  });
}

async function runPathCommand(command: string, path: string, signal?: AbortSignal): Promise<boolean> {
  if (!isTauriRuntime() || signal?.aborted) {
    return false;
  }

  const { invoke } = await import('./workspaceInvoke.ts');
  await invoke(command, { path });
  return true;
}

export function isTauriRuntime(): boolean {
  return typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window;
}

async function postLocalSourceBridge<T>(
  action: string,
  payload: Record<string, unknown> ,signal?: AbortSignal
): Promise<T | null> {
  if (typeof window === 'undefined' || signal?.aborted) {
    return null;
  }

  let response: Response;
  try {
    response = await fetch(`/__mcb/source/${action}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', ...bridgeTokenHeader() },
      body: JSON.stringify(payload)
    });
  } catch {
    return null;
  }

  const contentType = response.headers.get('content-type') ?? '';
  const text = await response.text();
  if (!contentType.includes('application/json')) {
    return null;
  }

  const body = text ? JSON.parse(text) : null;
  if (!response.ok) {
    const message =
      body && typeof body === 'object' && 'error' in body
        ? String((body as { error: unknown }).error)
        : `Local source bridge failed with HTTP ${response.status}`;
    throw new Error(message);
  }

  return body as T;
}

/**
 * A registered project. `groupKey` is the rail group its sessions share and `lastUsedMs` its sessions' latest
 * activity on this Mac, both computed in SQL. `alreadyExisted` is set only on an `add_project` answer for a folder
 * that was already a project.
 */
export type ProjectRecord = {
  id: string;
  machine: string;
  rootPath: string;
  title: string;
  repoKey: string;
  createdAtMs: number;
  groupKey: string;
  pinnedAtMs: number | null;
  lastUsedMs: number | null;
  alreadyExisted?: boolean;
};
export type FolderListing = { path: string; directories: string[]; truncated: boolean };

export async function listProjectsFromTauri(): Promise<ProjectRecord[]> {
  if (!isTauriRuntime()) return [];
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<ProjectRecord[]>('list_projects');
}

/** How a new project folder starts: `git init` in it, or a `git clone` of a URL or path. */
export type ProjectStart = 'git' | { clone: string };

/**
 * Inspects the folder on its machine and registers it; the same folder returns the same project.
 * With `create`, the folder is made first; `start` then runs `git init` in it, or makes it with `git clone`.
 */
export async function addProjectFromTauri(machine: string, path: string, create: boolean, start?: ProjectStart): Promise<ProjectRecord> {
  if (!isTauriRuntime()) throw new Error('Projects are available in the desktop app.');
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<ProjectRecord>('add_project', { machine, path, create, start });
}

/** Changes the project's title only. */
export async function renameProjectFromTauri(id: string, name: string): Promise<void> {
  const { invoke } = await import('@tauri-apps/api/core');
  await invoke('rename_project', { id, name });
}

/** Moves the project to another folder on its machine, after the same checks as adding one. */
export async function setProjectRootFromTauri(id: string, root: string): Promise<void> {
  const { invoke } = await import('@tauri-apps/api/core');
  await invoke('set_project_root', { id, root });
}

export async function setProjectPinnedFromTauri(id: string, pinned: boolean): Promise<void> {
  const { invoke } = await import('@tauri-apps/api/core');
  await invoke('set_project_pinned', { id, pinned });
}

/** Takes the project off the list; its sessions stay without a project unless `deleteSessions`. Files stay. */
export async function removeProjectFromTauri(id: string, deleteSessions: boolean): Promise<void> {
  const { invoke } = await import('@tauri-apps/api/core');
  await invoke('remove_project', { id, deleteSessions });
}

/** The folders inside `path` on `machine` ('local' or a saved remote profile id). `~` is that machine's home. */
export async function listFoldersFromTauri(machine: string, path: string): Promise<FolderListing> {
  if (!isTauriRuntime()) throw new Error('Folder browsing is available in the desktop app.');
  const { invoke } = await import('@tauri-apps/api/core');
  if (machine === 'local') return invoke<FolderListing>('list_folders', { path });
  return invoke<FolderListing>('remote_workspace', { profileId: machine, operation: 'list_folders', args: { path } });
}
