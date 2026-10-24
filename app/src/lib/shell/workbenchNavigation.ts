/**
 * workbenchNavigation.ts — the one way a panel asks for something outside
 * itself.
 *
 * Panels never import each other. Opening a file in the editor, showing a
 * file's changes, pointing the browser at a URL, handing an attachment to the
 * composer, starting a session: every one of those crosses a panel boundary,
 * and every one of them goes through this module. The page registers the real
 * work once, on mount, the same register-then-call shape `stackService.ts`
 * already uses; a panel only ever calls the plain functions below.
 *
 * PURE: no DOM, no Svelte, no backend call. Every caller is a no-op — never a
 * throw — while nothing is registered, so a panel that runs before the page is
 * ready simply does nothing. Browser URLs are the one queued handoff because
 * showing a browser tab is what registers the handler that can place its view.
 */
import type { ConversationAttachment } from './conversation/conversationTypes.ts';
import { requestOpenFile, type OpenFileRequest } from './openFileBus.ts';
import type { NotionTaskRow } from './notionTasks.ts';
import { receiveNotionOAuthCode } from './notionOAuth.ts';
import { remoteWorkspacePath } from '../workspacePaths.ts';

/** The chat and the five kinds of tab the top tab row can bring forward. */
export type CenterTabId = 'session' | 'editor' | 'browser' | 'diff' | 'git-history' | 'pull-requests';

/** The panels the right drawer's icon strip switches between. */
export type RightTabId =
  | 'files'
  | 'source-control'
  | 'worktrees'
  | 'terminal'
  | 'run'
  | 'context'
  | 'agents'
  | 'history'
  | 'tasks';

export const CENTER_TAB_IDS: readonly CenterTabId[] = [
  'session',
  'editor',
  'browser',
  'diff',
  'git-history',
  'pull-requests'
];

export const RIGHT_TAB_IDS: readonly RightTabId[] = [
  'files',
  'source-control',
  'worktrees',
  'terminal',
  'run',
  'context',
  'agents',
  'history',
  'tasks'
];

export interface OpenDiffRequest {
  /** Repository root the file belongs to. */
  projectRoot: string;
  /** Path relative to `projectRoot`. */
  relativePath: string;
}

export interface OpenUrlRequest {
  url: string;
}

/** One pull request, named the way its github.com link names it. */
export interface PullRequestLink {
  /** `owner/repo` */
  repository: string;
  number: number;
}

export interface ComposerHandoff {
  ownedId: string;
  attachments?: ConversationAttachment[];
  /** Appended to the composer's current draft, on its own line. */
  appendText?: string;
}

export interface SendToSessionRequest {
  ownedId: string;
  /** The turn's words. May be empty when the picture says it all. */
  text: string;
  attachments?: ConversationAttachment[];
}

export interface StartSessionRequest {
  prompt: string;
  cwd: string;
  projectPath: string;
  title: string;
  provider?: 'codex' | 'claude' | 'antigravity';
}

export interface WorkbenchNavigationHandlers {
  showCenterTab(id: CenterTabId): void;
  showRightTab(id: RightTabId): void;
  openDiff(request: OpenDiffRequest): void | Promise<void>;
  openFileTimeline(request: OpenDiffRequest): void | boolean | Promise<void | boolean>;
  openUrl(request: OpenUrlRequest): void | Promise<void>;
  openPullRequest(link: PullRequestLink): void;
  focusComposer(handoff: ComposerHandoff): void | Promise<void>;
  sendToSession(request: SendToSessionRequest): Promise<void>;
  startSession(request: StartSessionRequest): Promise<string | null>;
  selectSession(ownedId: string): Promise<void>;
}

let handlers: Partial<WorkbenchNavigationHandlers> = {};
let pendingOpenUrl: OpenUrlRequest | null = null;
let notionTaskOpener: ((task: NotionTaskRow) => void) | null = null;
let pendingNotionTask: NotionTaskRow | null = null;

/** The page registers the real handlers once, on mount. Registering again
 * merges: a caller may hand over one field without clearing the rest. */
export function registerWorkbenchNavigation(next: Partial<WorkbenchNavigationHandlers>): void {
  handlers = { ...handlers, ...next };
}

export function clearWorkbenchNavigation(): void {
  handlers = {};
  pendingOpenUrl = null;
  pendingNotionTask = null;
}

/** BrowserPanel listens for URLs only while a browser tab is showing. Keep
 * the one URL that brought it forward until the panel owns the native-view
 * placement needed to open it. */
export function registerBrowserUrlNavigation(
  openUrl: (request: OpenUrlRequest) => void
): () => void {
  handlers.openUrl = openUrl;
  const pending = pendingOpenUrl;
  pendingOpenUrl = null;
  if (pending) void openUrl(pending);
  return () => {
    if (handlers.openUrl === openUrl) delete handlers.openUrl;
  };
}

/** TasksPanel opens a linked task while it is mounted; until then the task
 * waits, the same way a URL waits for the browser panel. */
export function registerNotionTaskNavigation(open: (task: NotionTaskRow) => void): () => void {
  notionTaskOpener = open;
  const pending = pendingNotionTask;
  pendingNotionTask = null;
  if (pending) open(pending);
  return () => {
    if (notionTaskOpener === open) notionTaskOpener = null;
  };
}

export function showCenterTab(id: CenterTabId): void {
  handlers.showCenterTab?.(id);
}

export function showRightTab(id: RightTabId): void {
  handlers.showRightTab?.(id);
}

/** Open a file and bring the editor forward. The request goes on the existing
 * file bus, which the editor subscribes to on its own. */
export function openFileInEditor(request: OpenFileRequest): void {
  requestOpenFile(request);
  showCenterTab('editor');
}

/** Show one file's changes after the selected diff belongs to that file. */
export async function openDiffForFile(request: OpenDiffRequest): Promise<void> {
  await handlers.openDiff?.(request);
  showCenterTab('diff');
}

/** Show the paged history for one repository-relative file. */
export async function openFileTimeline(request: OpenDiffRequest): Promise<void> {
  const opened = await handlers.openFileTimeline?.(request);
  if (opened === false) return;
  showCenterTab('git-history');
}

/** `https://github.com/<owner>/<repo>/pull/<n>` (any trailing path, query or
 * fragment) as a pull request; anything else is null. */
export function parseGithubPullRequestUrl(url: string): PullRequestLink | null {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  if (!/^https?:$/.test(parsed.protocol) || !/^(www\.)?github\.com$/i.test(parsed.hostname)) return null;
  const match = /^\/([\w.-]+)\/([\w.-]+)\/pull\/(\d+)(\/|$)/.exec(parsed.pathname);
  const number = match ? Number(match[3]) : 0;
  return match && number > 0 ? { repository: `${match[1]}/${match[2]}`, number } : null;
}

/** The page id in a Notion link (`notion.so`, `app.notion.com`, `*.notion.site`),
 * dashed the way the Notion API writes it; anything else is null. */
export function parseNotionPageId(url: string): string | null {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  if (!/^https?:$/.test(parsed.protocol)) return null;
  if (!/^((www\.)?notion\.so|app\.notion\.com|[\w-]+\.notion\.site)$/i.test(parsed.hostname)) return null;
  const segment = parsed.pathname.split('/').pop() ?? '';
  const match = /(?:^|-)([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i.exec(segment);
  if (!match) return null;
  const hex = match[1].replaceAll('-', '').toLowerCase();
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** In the desktop app, a Notion page that is a cached task opens on the Tasks
 * tab and any other Notion page opens in the Notion app. False elsewhere. */
async function openNotionPage(url: string, pageId: string): Promise<boolean> {
  const { isNativeTauriRuntime } = await import('../tauriSource.ts');
  if (!isNativeTauriRuntime()) return false;
  const { readCachedNotionTask } = await import('./notionTasks.ts');
  const task = await readCachedNotionTask(pageId);
  if (task) {
    if (notionTaskOpener) notionTaskOpener(task);
    else pendingNotionTask = task;
    showRightTab('tasks');
    return true;
  }
  const { openUrl } = await import('@tauri-apps/plugin-opener');
  await openUrl(url);
  return true;
}

export type AssemblyLink =
  | { kind: 'session'; ownedId: string }
  | { kind: 'task'; pageId: string }
  | { kind: 'open'; url: string }
  | { kind: 'file'; path: string }
  | { kind: 'notion-oauth'; state: string; code: string };

/** Read an `assembly://` link: `session/<ownedId>`, `task/<notionPageId>`,
 * `open?url=<http(s) url>`, `file?path=<absolute path>[&profile=<machine>]`
 * or `notion-oauth?state=<state>&code=<claim code>` from the Notion sign-in
 * page. Anything else is null. */
export function parseAssemblyLink(url: string): AssemblyLink | null {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  if (parsed.protocol !== 'assembly:') return null;
  const value = decodeURIComponent(parsed.pathname.replace(/^\//, ''));
  if (parsed.hostname === 'session') {
    return value && !value.includes('/') ? { kind: 'session', ownedId: value } : null;
  }
  if (parsed.hostname === 'task') {
    const pageId = parseNotionPageId(`https://app.notion.com/p/${value}`);
    return pageId ? { kind: 'task', pageId } : null;
  }
  if (parsed.hostname === 'open') {
    const target = parsed.searchParams.get('url') ?? '';
    return /^https?:\/\//i.test(target) ? { kind: 'open', url: target } : null;
  }
  if (parsed.hostname === 'file') {
    const path = parsed.searchParams.get('path') ?? '';
    const profile = parsed.searchParams.get('profile');
    if (!path.startsWith('/')) return null;
    return { kind: 'file', path: profile ? remoteWorkspacePath(profile, path) : path };
  }
  if (parsed.hostname === 'notion-oauth') {
    const state = parsed.searchParams.get('state');
    const code = parsed.searchParams.get('code');
    return state && code ? { kind: 'notion-oauth', state, code } : null;
  }
  return null;
}

/** Carry out an `assembly://` link. A link this app does not understand does nothing. */
export async function openAssemblyLink(url: string): Promise<void> {
  const link = parseAssemblyLink(url);
  if (link?.kind === 'session') await handlers.selectSession?.(link.ownedId);
  else if (link?.kind === 'task') await openUrlInBrowser({ url: `https://app.notion.com/p/${link.pageId.replaceAll('-', '')}` });
  else if (link?.kind === 'open') await openUrlInBrowser({ url: link.url });
  else if (link?.kind === 'file') openFileInEditor({ path: link.path });
  else if (link?.kind === 'notion-oauth') receiveNotionOAuthCode(link.state, link.code);
}

/** A link handed over by another app: an `assembly://` link, or a file opened from Finder. */
export async function openIncomingLink(url: string): Promise<void> {
  if (/^file:/i.test(url)) {
    openFileInEditor({ path: decodeURIComponent(new URL(url).pathname) });
    return;
  }
  await openAssemblyLink(url);
}

/** Point the browser at a URL. A browser tab is put on screen first: the
 * browser only loads while it is the tab in front. A GitHub pull request link
 * opens that pull request in the Pull requests tab instead, and a Notion link
 * opens its task (or the Notion app). */
export async function openUrlInBrowser(request: OpenUrlRequest): Promise<void> {
  if (/^assembly:/i.test(request.url)) {
    await openAssemblyLink(request.url);
    return;
  }
  const notionPageId = parseNotionPageId(request.url);
  if (notionPageId && (await openNotionPage(request.url, notionPageId))) return;
  const pullRequest = handlers.openPullRequest ? parseGithubPullRequestUrl(request.url) : null;
  if (pullRequest) {
    handlers.openPullRequest?.(pullRequest);
    showCenterTab('pull-requests');
    return;
  }
  showCenterTab('browser');
  const openUrl = handlers.openUrl;
  if (openUrl) {
    await openUrl(request);
  } else {
    pendingOpenUrl = request;
  }
}

/** Hand the composer an attachment and some text, then bring the session
 * forward so the reader can see what they were given. */
export async function focusComposerWith(handoff: ComposerHandoff): Promise<void> {
  await handlers.focusComposer?.(handoff);
  showCenterTab('session');
}

/**
 * Send one turn to a session that already exists, and bring it forward so the
 * reader watches it go. Unlike `focusComposerWith` this does not stop at the
 * draft: the panel that calls it has already asked for the send, and a message
 * left staged in a box the reader is not looking at reads as a send that did
 * nothing. Throws whatever the send throws, so the panel can say so.
 */
export async function sendToSession(request: SendToSessionRequest): Promise<void> {
  const send = handlers.sendToSession;
  if (!send) throw new Error('The session sender is not ready');
  await send(request);
  showCenterTab('session');
}

/** Start a session at `cwd` and send `prompt` as its first message. Answers
 * with the new session's id, or null when no session could be started. */
export async function startWorkbenchSession(
  request: StartSessionRequest
): Promise<string | null> {
  const start = handlers.startSession;
  if (!start) return null;
  return start(request);
}
