/**
 * sessionWorkspaces.ts — what each session had open, kept per session.
 *
 * PURE: no store, no DOM, no backend call. It only turns "this is what the
 * editor and the file tree look like right now" into a small record, and
 * normalizes records read back from SQLite.
 *
 * The problem it exists for: the editor and the file tree are one of each for
 * the whole shell, so two sessions working in the same repository were sharing
 * one set of tabs. Switching between them lost your place both ways. Giving
 * every session its own record means switching can put back what that session
 * had, and the two stop overwriting each other.
 *
 * What is NOT here: any decision about when to save or restore. The page owns
 * that (see `+page.svelte`), so nothing in this file can fire on its own.
 */
import type {
  AgentConfigValue,
  AgentWriterLease,
  AgentWriterLeaseTransition
} from './conversation/conversationTypes.ts';
import {
  DEFAULT_RIGHT_TAB,
  isRightTabId
} from './layout/workbenchTabs.ts';
import { normalizeTopTabs, type SessionTopTabsWorkspace } from './layout/topTabsOps.ts';
import type { AgentExecutionOwner } from './ownedSessions.ts';
import type { RightTabId } from './workbenchNavigation.ts';

export const SESSION_CONVERSATION_WORKSPACE_VERSION = 1;

export type DiffMode = 'unified' | 'side-by-side';
export const DEFAULT_DIFF_MODE: DiffMode = 'unified';

export function isDiffMode(value: unknown): value is DiffMode {
  return value === 'unified' || value === 'side-by-side';
}

export interface ConversationViewState {
  followLatest: boolean;
  anchor?: { itemId: string; firstSequence: number; offsetPx: number };
  expandedTurns: Record<string, boolean>;
  disclosures?: Record<string, boolean>;
}

export interface SessionConversationWorkspace {
  mode: 'structured' | 'raw';
  version?: number;
  generation?: number;
  owner?: AgentExecutionOwner;
  attachmentIds?: string[];
  config?: Record<string, AgentConfigValue>;
  viewByHistoryId?: Record<string, ConversationViewState>;
  sequence?: number;
  telemetry?: Record<string, AgentConfigValue>;
  writerLease?: AgentWriterLease;
  writerLeaseTransition?: AgentWriterLeaseTransition | null;
  selectedChildId?: string | null;
  providerGeneration?: number;
  lastSequence?: number;
  [key: string]: unknown;
}

export interface SessionBrowserTabWorkspace {
  id: string;
  url: string;
  inputUrl: string;
  title: string;
}

export interface SessionBrowserWorkspace {
  tabs: SessionBrowserTabWorkspace[];
  activeTabId: string | null;
}

export interface SessionSourceControlWorkspace {
  openSectionIds: string[];
  scrollTop: number;
}

export interface SessionWorkspaceFileState {
  /** Unsaved text only. Saved file contents are always read from disk. */
  draftContent?: string;
  /** Disk revision the draft was edited from, so a restore can tell if the file changed. */
  baseRevision?: string;
  /** CodeMirror cursor, scroll and folding state. */
  viewState?: object;
}

export type SessionWorkspaceExpandedPathsByRoot = Record<string, string[]>;

/** What one session had open, small enough to store for every session at once. */
export interface SessionWorkspaceSnapshot {
  /** Editor tabs in strip order, newest at the end. */
  openPaths: string[];
  /** The tab that was showing. */
  activePath: string | null;
  /** State that belongs to individual open paths. Clean files need no entry. */
  fileStates?: Record<string, SessionWorkspaceFileState>;
  /** The highlighted file in the tree. */
  selectedPath: string | null;
  /** Scroll offset of the tree, in pixels. */
  scrollTop: number;
  /**
   * The file the Diff tab was showing, as a path relative to the repository, or
   * null when it was showing nothing.
   */
  diffPath: string | null;
  /**
   * The project folder `diffPath` is inside, or null when there is no diff.
   *
   * Stored next to the path because a path on its own cannot be checked: the
   * Diff tab is one tab for the whole shell, and "src/lib/index.ts" names a real
   * file in most projects. Keeping the folder it came from is what lets
   * {@link diffPathFor} tell "this is your file" from "this is the last
   * project's file that happens to have the same name".
   */
  diffRoot: string | null;
  /** How the active Diff tab renders its comparison. Legacy captures omit it. */
  diffMode?: DiffMode;
  /** Small conversation UI state only. Provider history is never stored here. */
  conversation?: SessionConversationWorkspace;
  /** The embedded Browser state owned by this session. */
  browser?: SessionBrowserWorkspace;
  /** The top tab row's order and active tab owned by this session. */
  topTabs?: SessionTopTabsWorkspace;
  /** The selected tab in the right column. */
  rightTab: RightTabId;
  /** Expanded lazy-tree directories, keyed by the root they belong to. */
  expandedPathsByRoot?: SessionWorkspaceExpandedPathsByRoot;
  /** Files panel checkout being inspected, or null for the session checkout. */
  filesInspectionRoot?: string | null;
  /** Source-control checkout being inspected, or null for the session checkout. */
  sourceControlInspectionRoot?: string | null;
  /** Small view state for the lazily mounted Source Control panel. */
  sourceControl?: SessionSourceControlWorkspace;
  /** Another project's folder Files and Git show instead, kept with its machine prefix; unset for the session's own. */
  viewedProjectRoot?: string | null;
}

/**
 * How many tabs a session's record keeps. Twelve is more than anyone has open
 * at once and far less than the hundreds a long session can accumulate. The
 * limit bounds both the SQLite row and the active frontend projection.
 */
export const OPEN_PATHS_CAP = 12;

/** The strip, trimmed by priority: active path, every drafted path, then the
 * most recent remaining paths. Drafts raise the ordinary cap rather than being lost. */
function cappedPaths(
  paths: string[],
  activePath: string | null,
  draftedPaths: readonly string[] = []
): string[] {
  const unique: string[] = [];
  for (const path of paths) {
    if (typeof path === 'string' && path && !unique.includes(path)) unique.push(path);
  }
  if (unique.length <= OPEN_PATHS_CAP) return unique;

  const prioritized = new Set<string>();
  if (activePath && unique.includes(activePath)) prioritized.add(activePath);
  for (const path of draftedPaths) {
    if (unique.includes(path)) prioritized.add(path);
  }
  const cap = Math.max(OPEN_PATHS_CAP, prioritized.size);
  for (let index = unique.length - 1; index >= 0; index -= 1) {
    if (prioritized.size >= cap) break;
    prioritized.add(unique[index]);
  }
  return unique.filter((path) => prioritized.has(path));
}

/** Turn what is on screen into a record. Nothing is read from anywhere: the
 * caller passes the editor's and the tree's own state straight in. */
export function captureWorkspace(input: {
  openFiles: {
    path: string;
    draftContent?: string | null;
    dirty?: boolean;
    baseRevision?: string | null;
  }[];
  activePath: string | null;
  viewStates?: Record<string, unknown>;
  selectedPath: string | null;
  scrollTop: number;
  /** What the Diff tab is showing, if the caller passes it. Optional so the
   * page can start recording it separately from the rest of this record; a
   * caller that says nothing gets "no diff", which is what an older stored
   * record means too. */
  diffPath?: string | null;
  /** The project folder that diff came from. See `diffRoot` on the record. */
  diffRoot?: string | null;
  diffMode?: DiffMode;
  conversation?: SessionConversationWorkspace;
  browser?: SessionBrowserWorkspace;
  topTabs?: SessionTopTabsWorkspace;
  rightTab: RightTabId;
  expandedPathsByRoot?: SessionWorkspaceExpandedPathsByRoot;
  filesInspectionRoot?: string | null;
  sourceControlInspectionRoot?: string | null;
  sourceControl?: SessionSourceControlWorkspace;
}): SessionWorkspaceSnapshot {
  const activePath = input.activePath ?? null;
  // A path with no folder cannot be checked against the session being restored,
  // and the safe reading of an unknown owner is "not this session's" — so half
  // an answer is stored as no answer rather than as a diff we would then show
  // to the wrong project.
  const diffPath = pathOf(input.diffPath);
  const diffRoot = pathOf(input.diffRoot);
  const bothKnown = diffPath !== null && diffRoot !== null;
  const draftedPaths = input.openFiles
    .filter((file) => file.dirty === true && typeof file.draftContent === 'string')
    .map((file) => file.path);
  const openPaths = cappedPaths(
    input.openFiles.map((file) => file.path),
    activePath,
    draftedPaths
  );
  const filesByPath = new Map(input.openFiles.map((file) => [file.path, file]));
  const fileStates: Record<string, SessionWorkspaceFileState> = {};
  for (const path of openPaths) {
    const file = filesByPath.get(path);
    const state: SessionWorkspaceFileState = {};
    if (file?.dirty === true && typeof file.draftContent === 'string') {
      state.draftContent = file.draftContent;
      if (typeof file.baseRevision === 'string') state.baseRevision = file.baseRevision;
    }
    const viewState = input.viewStates?.[path];
    if (isRecord(viewState)) state.viewState = viewState;
    if (Object.keys(state).length > 0) fileStates[path] = state;
  }
  const snapshot: SessionWorkspaceSnapshot = {
    openPaths,
    activePath,
    selectedPath: input.selectedPath ?? null,
    scrollTop: Math.max(0, input.scrollTop),
    diffPath: bothKnown ? diffPath : null,
    diffRoot: bothKnown ? diffRoot : null,
    rightTab: input.rightTab
  };
  if (isDiffMode(input.diffMode)) snapshot.diffMode = input.diffMode;
  if (Object.keys(fileStates).length > 0) snapshot.fileStates = fileStates;
  if (input.conversation) snapshot.conversation = normalizeConversation(input.conversation);
  if (input.browser) snapshot.browser = normalizeBrowser(input.browser);
  if (input.topTabs) {
    snapshot.topTabs = normalizeTopTabs(input.topTabs, {
      editorPaths: openPaths,
      browserTabIds: snapshot.browser?.tabs.map((tab) => tab.id) ?? []
    });
  }
  const expandedPathsByRoot = normalizeExpandedPathsByRoot(input.expandedPathsByRoot);
  if (expandedPathsByRoot) snapshot.expandedPathsByRoot = expandedPathsByRoot;
  const filesInspectionRoot = normalizeInspectionRoot(input.filesInspectionRoot);
  if (filesInspectionRoot) snapshot.filesInspectionRoot = filesInspectionRoot;
  const sourceControlInspectionRoot = normalizeInspectionRoot(input.sourceControlInspectionRoot);
  if (sourceControlInspectionRoot) snapshot.sourceControlInspectionRoot = sourceControlInspectionRoot;
  const sourceControl = normalizeSourceControl(input.sourceControl);
  if (sourceControl) snapshot.sourceControl = sourceControl;
  return snapshot;
}

/** The unsaved drafts a session keeps when it moves to another checkout. Each
 * draft inside the old checkout moves to the same file in the new one; its base
 * revision comes along, so a file that differs there opens flagged as changed. */
export function draftsForCheckout<T extends { path: string; dirty?: boolean; draftContent?: string | null }>(
  files: readonly T[],
  fromRoot: string | null,
  toRoot: string
): T[] {
  const from = fromRoot?.replace(/\/+$/, '');
  const to = toRoot.replace(/\/+$/, '');
  return files
    .filter((file) => file.dirty === true && typeof file.draftContent === 'string')
    .map((file) => from && file.path.startsWith(`${from}/`)
      ? { ...file, path: `${to}${file.path.slice(from.length)}` }
      : file);
}

function stringsOf(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is string => typeof entry === 'string' && entry.length > 0);
}

function pathOf(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

function nonNegativeInteger(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : undefined;
}

function normalizeConversationViews(value: unknown): Record<string, ConversationViewState> {
  if (!isRecord(value)) return {};
  return Object.fromEntries(Object.entries(value).flatMap(([historyId, view]) => {
    if (!isRecord(view) || typeof view.followLatest !== 'boolean') return [];
    const anchor = view.anchor;
    return [[historyId, {
      followLatest: view.followLatest,
      ...(isRecord(anchor) && typeof anchor.itemId === 'string' && anchor.itemId.length > 0
        && typeof anchor.firstSequence === 'number' && Number.isSafeInteger(anchor.firstSequence)
        && typeof anchor.offsetPx === 'number' && Number.isFinite(anchor.offsetPx)
        ? { anchor: { itemId: anchor.itemId, firstSequence: anchor.firstSequence, offsetPx: anchor.offsetPx } }
        : {}),
      expandedTurns: Object.fromEntries(Object.entries(isRecord(view.expandedTurns) ? view.expandedTurns : {})
        .filter((entry): entry is [string, boolean] => typeof entry[1] === 'boolean')),
      ...(isRecord(view.disclosures) ? { disclosures: Object.fromEntries(Object.entries(view.disclosures)
        .filter((entry): entry is [string, boolean] => typeof entry[1] === 'boolean')) } : {})
    }]];
  }));
}

function normalizeConversation(value: unknown): SessionConversationWorkspace {
  const entry = value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {};
  const preserved = { ...entry };
  for (const key of [
    'mode', 'draft', 'version', 'generation', 'owner', 'attachmentIds', 'config',
    'viewByHistoryId', 'parentScrollTop', 'childScrollTopById', 'sequence', 'telemetry', 'writerLease',
    'writerLeaseTransition', 'selectedChildId', 'scrollTop', 'providerGeneration', 'lastSequence'
  ]) delete preserved[key];
  return {
    ...preserved,
    mode: entry.mode === 'raw' ? 'raw' : 'structured',
    ...(isRecord(entry.viewByHistoryId) ? { viewByHistoryId: normalizeConversationViews(entry.viewByHistoryId) } : {}),
    ...(nonNegativeInteger(entry.version) !== undefined
      ? { version: nonNegativeInteger(entry.version) }
      : {}),
    ...(nonNegativeInteger(entry.generation) !== undefined
      ? { generation: nonNegativeInteger(entry.generation) }
      : {}),
    ...(isExecutionOwner(entry.owner) ? { owner: entry.owner } : {}),
    ...(Array.isArray(entry.attachmentIds)
      ? { attachmentIds: stringsOf(entry.attachmentIds) }
      : {}),
    ...(isRecord(entry.config) ? { config: entry.config as Record<string, AgentConfigValue> } : {}),
    ...(nonNegativeInteger(entry.sequence) !== undefined
      ? { sequence: nonNegativeInteger(entry.sequence) }
      : {}),
    ...(isRecord(entry.telemetry)
      ? { telemetry: entry.telemetry as Record<string, AgentConfigValue> }
      : {}),
    ...(isWriterLease(entry.writerLease) ? { writerLease: entry.writerLease } : {}),
    ...(entry.writerLeaseTransition === null || isWriterLeaseTransition(entry.writerLeaseTransition)
      ? { writerLeaseTransition: entry.writerLeaseTransition }
      : {}),
    ...(typeof entry.selectedChildId === 'string' || entry.selectedChildId === null
      ? { selectedChildId: entry.selectedChildId as string | null }
      : {}),
    ...(nonNegativeInteger(entry.providerGeneration) !== undefined
      ? { providerGeneration: nonNegativeInteger(entry.providerGeneration) }
      : {}),
    ...(nonNegativeInteger(entry.lastSequence) !== undefined
      ? { lastSequence: nonNegativeInteger(entry.lastSequence) }
      : {})
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

function isExecutionOwner(value: unknown): value is AgentExecutionOwner {
  return [
    'structured',
    'terminal',
    'transitioning-to-structured',
    'transitioning-to-terminal',
    'stopped'
  ].includes(value as string);
}

function isWriterLease(value: unknown): value is AgentWriterLease {
  if (!isRecord(value)) return false;
  return typeof value.ownedId === 'string'
    && nonNegativeInteger(value.generation) !== undefined
    && ['structured', 'terminal', 'none'].includes(value.owner as string);
}

function isWriterLeaseTransition(value: unknown): value is AgentWriterLeaseTransition {
  if (!isRecord(value)) return false;
  return typeof value.ownedId === 'string'
    && nonNegativeInteger(value.generation) !== undefined
    && ['structured', 'terminal', 'none'].includes(value.from as string)
    && ['structured', 'terminal', 'none'].includes(value.to as string)
    && ['requested', 'committed', 'failed'].includes(value.state as string)
    && (value.error === undefined || typeof value.error === 'string');
}

function normalizeBrowser(value: unknown): SessionBrowserWorkspace {
  const entry = value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {};
  const seen = new Set<string>();
  const tabs = Array.isArray(entry.tabs)
    ? entry.tabs.flatMap((value): SessionBrowserTabWorkspace[] => {
        if (!isRecord(value)) return [];
        const id = typeof value.id === 'string' ? value.id.trim() : '';
        if (!id || seen.has(id)) return [];
        seen.add(id);
        return [{
          id,
          url: typeof value.url === 'string' ? value.url : '',
          inputUrl: typeof value.inputUrl === 'string' ? value.inputUrl : '',
          title: typeof value.title === 'string' && value.title.trim()
            ? value.title.trim()
            : 'New browser tab'
        }];
      })
    : [];

  // Records written by the single-page Browser become the one tab in the new
  // shape as they are read. New captures write only `tabs` and `activeTabId`.
  if (tabs.length === 0 && typeof entry.url === 'string' && entry.url) {
    tabs.push({
      id: 'restored-browser-tab',
      url: entry.url,
      inputUrl: typeof entry.inputUrl === 'string' ? entry.inputUrl : entry.url,
      title: entry.url
    });
  }
  const requestedActive = typeof entry.activeTabId === 'string' ? entry.activeTabId : null;
  return {
    tabs,
    activeTabId: requestedActive && tabs.some((tab) => tab.id === requestedActive)
      ? requestedActive
      : tabs.at(-1)?.id ?? null
  };
}

function normalizeFileStates(
  value: unknown,
  openPaths: readonly string[]
): Record<string, SessionWorkspaceFileState> | undefined {
  if (!isRecord(value)) return undefined;
  const allowed = new Set(openPaths);
  const normalized: Record<string, SessionWorkspaceFileState> = {};
  for (const [path, rawState] of Object.entries(value)) {
    if (!allowed.has(path) || !isRecord(rawState)) continue;
    const state: SessionWorkspaceFileState = {};
    if (typeof rawState.draftContent === 'string') state.draftContent = rawState.draftContent;
    if (typeof rawState.baseRevision === 'string') state.baseRevision = rawState.baseRevision;
    if (isRecord(rawState.viewState)) state.viewState = rawState.viewState;
    if (Object.keys(state).length > 0) normalized[path] = state;
  }
  return Object.keys(normalized).length > 0 ? normalized : undefined;
}

function canonicalWorkspacePath(value: string): string {
  const prefix = /^(?:assembly-remote:\/\/[^/]+|file:\/\/)(?=\/)/.exec(value.trim())?.[0] ?? ''; // A machine prefix is kept as is.
  const path = value.trim().slice(prefix.length).replaceAll('\\', '/');
  if (!path) return '';
  const absolute = path.startsWith('/');
  const parts: string[] = [];
  for (const part of path.split('/')) {
    if (!part || part === '.') continue;
    if (part === '..') {
      if (parts.length > 0 && parts[parts.length - 1] !== '..') parts.pop();
      else if (!absolute) parts.push(part);
      continue;
    }
    parts.push(part);
  }
  const joined = parts.join('/');
  if (!joined) return absolute ? `${prefix}/` : '.';
  return absolute ? `${prefix}/${joined}` : joined;
}

function normalizeInspectionRoot(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  return canonicalWorkspacePath(value) || null;
}

function normalizeSourceControl(value: unknown): SessionSourceControlWorkspace | null {
  if (!isRecord(value)) return null;
  return {
    openSectionIds: [...new Set(stringsOf(value.openSectionIds))],
    scrollTop: typeof value.scrollTop === 'number' && value.scrollTop > 0 ? value.scrollTop : 0
  };
}

function isPathAtOrBelow(path: string, root: string): boolean {
  if (path === root) return true;
  if (root === '/') return path.startsWith('/');
  return path.startsWith(`${root}/`);
}

function normalizeExpandedPathsByRoot(
  value: unknown
): SessionWorkspaceExpandedPathsByRoot | undefined {
  if (!isRecord(value)) return undefined;
  const normalized: SessionWorkspaceExpandedPathsByRoot = {};
  for (const [rawRoot, rawPaths] of Object.entries(value)) {
    const root = canonicalWorkspacePath(rawRoot);
    if (!root || !Array.isArray(rawPaths)) continue;
    const paths = [...new Set(
      rawPaths
        .filter((path): path is string => typeof path === 'string')
        .map(canonicalWorkspacePath)
        .filter((path) => path && path !== root && isPathAtOrBelow(path, root))
    )].sort();
    normalized[root] = paths;
  }
  return Object.keys(normalized).length > 0 ? normalized : undefined;
}

/** One stored entry, or null if it is not a record at all. Fields of the wrong
 * type become their empty version rather than sinking the whole entry: a
 * half-corrupt record still restores most of a session. */
export function normalizeWorkspaceSnapshot(value: unknown): SessionWorkspaceSnapshot | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;

  const entry = value as Record<string, unknown>;
  const activePath = pathOf(entry.activePath);
  const scrollTop = typeof entry.scrollTop === 'number' && entry.scrollTop > 0 ? entry.scrollTop : 0;
  // Records written before the Diff tab was remembered have neither field, and
  // they read back as "this session was not looking at a diff" — which is the
  // right answer for them and the safe one in general.
  const diffPath = pathOf(entry.diffPath);
  const diffRoot = pathOf(entry.diffRoot);
  const bothKnown = diffPath !== null && diffRoot !== null;
  const draftedPaths = isRecord(entry.fileStates)
    ? Object.entries(entry.fileStates)
        .filter(([, state]) => isRecord(state) && typeof state.draftContent === 'string')
        .map(([path]) => path)
    : [];
  const openPaths = cappedPaths(stringsOf(entry.openPaths), activePath, draftedPaths);
  const snapshot: SessionWorkspaceSnapshot = {
    openPaths,
    activePath,
    selectedPath: pathOf(entry.selectedPath),
    scrollTop,
    diffPath: bothKnown ? diffPath : null,
    diffRoot: bothKnown ? diffRoot : null,
    diffMode: isDiffMode(entry.diffMode) ? entry.diffMode : DEFAULT_DIFF_MODE,
    rightTab: isRightTabId(entry.rightTab) ? entry.rightTab : DEFAULT_RIGHT_TAB
  };
  const fileStates = normalizeFileStates(entry.fileStates, openPaths);
  if (fileStates) snapshot.fileStates = fileStates;
  if ('conversation' in entry) snapshot.conversation = normalizeConversation(entry.conversation);
  if ('browser' in entry) snapshot.browser = normalizeBrowser(entry.browser);
  const topTabs = normalizeTopTabs(entry.topTabs, {
    editorPaths: openPaths,
    browserTabIds: snapshot.browser?.tabs.map((tab) => tab.id) ?? []
  });
  if (topTabs) snapshot.topTabs = topTabs;
  const expandedPathsByRoot = normalizeExpandedPathsByRoot(entry.expandedPathsByRoot);
  if (expandedPathsByRoot) snapshot.expandedPathsByRoot = expandedPathsByRoot;
  const filesInspectionRoot = normalizeInspectionRoot(entry.filesInspectionRoot);
  if (filesInspectionRoot) snapshot.filesInspectionRoot = filesInspectionRoot;
  const sourceControlInspectionRoot = normalizeInspectionRoot(entry.sourceControlInspectionRoot);
  if (sourceControlInspectionRoot) snapshot.sourceControlInspectionRoot = sourceControlInspectionRoot;
  if (typeof entry.viewedProjectRoot === 'string' && entry.viewedProjectRoot.trim()) snapshot.viewedProjectRoot = entry.viewedProjectRoot.trim();
  const sourceControl = normalizeSourceControl(entry.sourceControl);
  if (sourceControl) snapshot.sourceControl = sourceControl;
  return snapshot;
}

/** Trailing slashes make two spellings of one folder look different. */
function sameFolder(left: string, right: string): boolean {
  return left.replace(/\/+$/, '') === right.replace(/\/+$/, '');
}

/**
 * The file the Diff tab should show for a session whose project folder is
 * `root`, or null when it should show nothing.
 *
 * This is the answer to the bug where switching session left the Diff tab
 * showing a file from the project you had just left. There are three ways to get
 * null, and all of them mean "clear the tab": the session has no stored record
 * at all, it was not looking at a diff, or the diff it was looking at belongs to
 * a different project. Only a diff from this session's own project comes back.
 *
 * A session with no folder (`root` empty) can own no diff, so it always clears.
 */
export function diffPathFor(
  snapshot: SessionWorkspaceSnapshot | null | undefined,
  root: string | null | undefined
): string | null {
  const folder = (root ?? '').trim();
  if (!snapshot || !folder || !snapshot.diffPath || !snapshot.diffRoot) return null;
  return sameFolder(snapshot.diffRoot, folder) ? snapshot.diffPath : null;
}

/** What the page has to do to put a session's editor back the way it was. */
export interface WorkspaceRestorePlan {
  /** Lightweight tab descriptors to rebuild synchronously. */
  openFiles: ({ path: string } & SessionWorkspaceFileState)[];
  /** The file to mark active without reading it, or null when there is nothing to show. */
  activePath: string | null;
}

/**
 * Work out how to put a session's editor back from its small stored record.
 */
export function planWorkspaceRestore(
  snapshot: SessionWorkspaceSnapshot | null | undefined
): WorkspaceRestorePlan {
  const openFiles = (snapshot?.openPaths ?? []).map((path) => ({
    path,
    ...(snapshot?.fileStates?.[path] ?? {})
  }));
  const rememberedActive = snapshot?.activePath ?? null;
  const activePath = openFiles.some((file) => file.path === rememberedActive)
    ? rememberedActive
    : openFiles.at(-1)?.path ?? null;
  return {
    openFiles,
    activePath
  };
}
