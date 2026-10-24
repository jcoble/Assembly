import { normalizeBrowserUrl } from './normalizeBrowserUrl.ts';
import {
  browserBoundsEqual,
  clampBrowserFloatingBounds,
  resolveBrowserViewport,
  type BrowserWindowSize
} from './browserBounds.ts';
import {
  BROWSER_MAX_ANNOTATIONS_PER_TAB,
  canAddBrowserAnnotation,
  createImmutableBrowserFeedbackAttachment,
  isExactBrowserTarget,
  pendingMarkupFromInput,
  selectionFromElementInput
} from './browserAnnotations.ts';
import {
  createInMemoryBrowserBackend,
  type BrowserBackend,
  type BrowserBackendTarget,
  type BrowserBackendResult
} from './browserBackend.ts';
import {
  isExpandedBrowserMode,
  restorePresentationTarget,
  transitionBrowserPresentation
} from './browserPresentation.ts';
import type {
  BrowserAnnotationInput,
  BrowserConversationAttachment,
  BrowserConversationBridge,
  BrowserConversationDraft,
  BrowserElementSelectionInput,
  BrowserFeedbackAttachment,
  BrowserFeedbackPreview,
  BrowserFeedbackStageResult,
  BrowserFloatingBounds,
  BrowserMarkupInput,
  BrowserMarkupCapture,
  BrowserModelContext,
  BrowserPendingMarkup,
  BrowserPresentationMode,
  BrowserTabState,
  BrowserViewport,
  BrowserViewportPreset,
  BrowserWorkspaceCapture,
  BrowserWorkspaceState
} from './browserTypes.ts';
import { createBrowserWorkspace } from './browserTypes.ts';

export interface CreateBrowserTabInput {
  tabId?: string;
  url?: string;
  title?: string;
  viewport?: BrowserViewport | BrowserViewportPreset | { preset?: string; width?: number | null; height?: number | null };
  generation?: number;
}

export interface ActivateBrowserWorkspaceInput {
  workspaceId?: string;
  ownedId?: string | null;
  initialUrl?: string;
  profileSummary?: { id: string; label: string } | null;
}

export interface BrowserPresentationOptions {
  bounds?: Partial<BrowserFloatingBounds>;
  window?: BrowserWindowSize;
  /**
   * The smallest the view may be. A browser floating over the shell has a
   * usable-size floor; one filling a panel has none, because a view wider than
   * the panel it fills is drawn over whatever is beside it.
   */
  minSize?: { minWidth?: number; minHeight?: number };
}

export interface BrowserCaptureOptions {
  forMarkup?: boolean;
  note?: string | null;
  intent?: 'change' | 'question' | 'context';
}

export interface BrowserFeedbackStageOptions {
  ownedId?: string;
  generation?: number;
  attachmentId?: string;
  bridge?: BrowserConversationBridge;
  conversation?: BrowserConversationBridge;
  /** The draft snapshot shown by the confirmation preview. */
  draftSnapshot?: string;
  draft?: string;
  /** When true, apply the preview immediately; otherwise return a preview. */
  confirm?: boolean;
}

export interface BrowserModelHandle {
  readonly workspace: BrowserWorkspaceState;
  readonly backend: BrowserBackend;
  activateBrowserWorkspace(input?: ActivateBrowserWorkspaceInput): BrowserWorkspaceState;
  deactivateBrowserWorkspace(): BrowserWorkspaceState;
  createBrowserTab(input?: CreateBrowserTabInput): BrowserTabState;
  selectBrowserTab(tabId: string): BrowserWorkspaceState;
  closeBrowserTab(tabId: string): BrowserWorkspaceState;
  navigateActiveBrowserTab(url: string): BrowserTabState | null;
  setBrowserViewport(viewport: CreateBrowserTabInput['viewport']): BrowserTabState | null;
  setBrowserPresentationMode(mode: BrowserPresentationMode, options?: BrowserPresentationOptions): BrowserWorkspaceState;
  expandBrowserFrom(mode?: BrowserPresentationMode): BrowserWorkspaceState;
  restoreBrowserToDock(): BrowserWorkspaceState;
  minimizeBrowserToPrevious(): BrowserWorkspaceState;
  collapseBrowserToControl(): BrowserWorkspaceState;
  beginBrowserElementPicker(kind?: 'grab' | 'annotation'): BrowserWorkspaceState;
  acceptBrowserElementSelection(input: BrowserElementSelectionInput): BrowserWorkspaceState;
  cancelBrowserAnnotation(): BrowserWorkspaceState;
  queueBrowserAnnotation(input?: BrowserAnnotationInput | BrowserMarkupInput): BrowserFeedbackAttachment | null;
  removeBrowserAnnotation(id: string): BrowserWorkspaceState;
  captureBrowserWorkspace(): BrowserWorkspaceCapture;
  captureBrowserMarkup(options?: Omit<BrowserCaptureOptions, 'forMarkup'>): Promise<BrowserMarkupCapture>;
  formatBrowserFeedback(attachment: BrowserFeedbackAttachment): string;
  stageBrowserFeedbackPreview(options?: BrowserFeedbackStageOptions): BrowserFeedbackPreview;
}

export class BrowserModelError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'BrowserModelError';
  }
}

let idSequence = 0;
const defaultWorkspace = createBrowserWorkspace();
const defaultBackend = createInMemoryBrowserBackend();
const defaultContext: BrowserModelContext = {
  workspace: defaultWorkspace,
  backend: defaultBackend,
  now: () => new Date().toISOString(),
  createId: (kind) => {
    idSequence += 1;
    return `browser-${kind}-${idSequence}`;
  }
};

function isWorkspace(value: unknown): value is BrowserWorkspaceState {
  return Boolean(
    value &&
      typeof value === 'object' &&
      'tabs' in value &&
      'tabOrder' in value &&
      'activeTabId' in value &&
      'workspaceId' in value
  );
}

function isContext(value: unknown): value is BrowserModelContext {
  return Boolean(value && typeof value === 'object' && 'workspace' in value && isWorkspace((value as { workspace?: unknown }).workspace));
}

function isBackend(value: unknown): value is BrowserBackend {
  return Boolean(value && typeof value === 'object' && typeof (value as BrowserBackend).create_browser_tab === 'function');
}

function contextFor(value?: unknown, secondary?: unknown): BrowserModelContext {
  if (isContext(value)) return value;
  if (isWorkspace(value)) {
    return {
      ...defaultContext,
      workspace: value,
      backend: isBackend(secondary) ? secondary : defaultContext.backend
    };
  }
  if (isBackend(value)) return { ...defaultContext, backend: value };
  return defaultContext;
}

function idFor(context: BrowserModelContext, kind: 'tab' | 'feedback' | 'attachment'): string {
  if (context.createId) return context.createId(kind);
  idSequence += 1;
  return `browser-${kind}-${idSequence}`;
}

function nowFor(context: BrowserModelContext, explicit?: string): string {
  return explicit?.trim() || context.now?.() || new Date().toISOString();
}

/** A backend rejection is a plain `{ code, message }` object, not an Error;
 * stringifying it raw reads "[object Object]" in the panel. */
export function describeBrowserError(error: unknown): string {
  if (error instanceof Error) return error.message;
  if (error && typeof error === 'object') {
    const { message, code } = error as { message?: unknown; code?: unknown };
    if (typeof message === 'string' && message) return message;
    if (typeof code === 'string' && code) return code;
  }
  return String(error);
}

function describeError(error: unknown): string {
  return describeBrowserError(error);
}

function isPromiseLike<T>(value: BrowserBackendResult<T>): value is Promise<T> {
  if (!value || typeof value !== 'object') return false;
  return typeof Reflect.get(value, 'then') === 'function';
}

async function reportAsyncBackendError<T>(
  result: Promise<T>,
  onAsyncError?: (error: unknown) => void,
  isCurrent: () => boolean = () => true
): Promise<void> {
  try {
    await result;
  } catch (error) {
    if (!isCurrent()) return;
    onAsyncError?.(error);
  }
}

function callBackend<T>(
  context: BrowserModelContext,
  call: () => BrowserBackendResult<T>,
  onAsyncError?: (error: unknown) => void,
  isCurrent?: () => boolean
): BrowserBackendResult<T> | undefined {
  try {
    const result = call();
    if (isPromiseLike(result)) {
      void reportAsyncBackendError(result, onAsyncError, isCurrent);
    }
    return result;
  } catch (error) {
    onAsyncError?.(error);
    return undefined;
  }
}

function ensureSafeBrowserUrl(value: string): string {
  const normalized = normalizeBrowserUrl(value);
  if (!normalized) throw new BrowserModelError('Enter an http, https, or file address');
  const parsed = new URL(normalized);
  if (!['http:', 'https:', 'file:'].includes(parsed.protocol)) {
    throw new BrowserModelError('Only http, https, and file addresses are supported');
  }
  return parsed.toString();
}

function activeTab(context: BrowserModelContext): BrowserTabState | null {
  const { workspace } = context;
  return workspace.activeTabId ? workspace.tabs[workspace.activeTabId] ?? null : null;
}

function backendTarget(tab: BrowserTabState): BrowserBackendTarget {
  return { workspaceId: tab.workspaceId, tabId: tab.id, generation: tab.generation };
}

function isCurrentBackendTarget(context: BrowserModelContext, target: BrowserBackendTarget): boolean {
  const tab = context.workspace.tabs[target.tabId];
  return Boolean(tab && isExactBrowserTarget(target, tab.workspaceId, tab.id, tab.generation));
}

function isCurrentBrowserWorkspace(context: BrowserModelContext, workspaceId: string): boolean {
  return context.workspace.workspaceId === workspaceId;
}

function setWorkspaceError(context: BrowserModelContext, error: unknown): void {
  context.workspace.error = describeError(error);
}

function updateActiveGeneration(context: BrowserModelContext, tab: BrowserTabState | null): void {
  context.workspace.activeGeneration = tab?.generation ?? 0;
}

function parseWorkspaceAndInput<T>(first: unknown, second: unknown, fallback: T): {
  context: BrowserModelContext;
  input: T;
} {
  if (isContext(first)) return { context: first, input: (second as T | undefined) ?? fallback };
  if (isWorkspace(first)) return { context: contextFor(first, second), input: (second as T | undefined) ?? fallback };
  return { context: contextFor(), input: (first as T | undefined) ?? fallback };
}

function cloneTab(tab: BrowserTabState): BrowserTabState {
  return {
    ...tab,
    viewport: { ...tab.viewport },
    annotations: [...tab.annotations]
  };
}

/** Open the owned browser workspace without creating a page until requested. */
export function activateBrowserWorkspace(
  first?: BrowserWorkspaceState | BrowserModelContext | ActivateBrowserWorkspaceInput,
  second?: BrowserModelContext | BrowserBackend | ActivateBrowserWorkspaceInput
): BrowserWorkspaceState {
  let context: BrowserModelContext;
  let input: ActivateBrowserWorkspaceInput;
  if (isContext(first)) {
    context = first;
    input = second && !isContext(second) && !isBackend(second)
      ? (second as ActivateBrowserWorkspaceInput)
      : {};
  } else if (isWorkspace(first)) {
    context = contextFor(first, second);
    input = {};
  } else {
    context = isContext(second) || isBackend(second) ? contextFor(second) : contextFor();
    input = first ?? {};
  }
  const workspace = context.workspace;
  if (input.workspaceId?.trim() && Object.keys(workspace.tabs).length === 0) workspace.workspaceId = input.workspaceId.trim();
  if (input.ownedId !== undefined) workspace.ownedId = input.ownedId?.trim() || null;
  if (input.profileSummary !== undefined) {
    workspace.profileSummary = input.profileSummary ? { ...input.profileSummary } : null;
  }
  // A cold launch always starts in the dock, even if a stale serialized value
  // happened to contain the maximized mode.
  if (!workspace.activated && workspace.presentation === 'maximized') {
    workspace.presentation = 'docked';
    workspace.previousPresentation = null;
  }
  workspace.activated = true;
  workspace.error = null;
  if (input.initialUrl?.trim() && !workspace.activeTabId) {
    createBrowserTab(context, { url: input.initialUrl });
  }
  const tab = activeTab(context);
  if (tab) {
    const target = backendTarget(tab);
    callBackend(context, () => context.backend!.show_browser_tab(target), (error) => setWorkspaceError(context, error), () => isCurrentBackendTarget(context, target));
  }
  return workspace;
}

export function deactivateBrowserWorkspace(
  target?: BrowserWorkspaceState | BrowserModelContext
): BrowserWorkspaceState {
  const context = contextFor(target);
  const workspaceId = context.workspace.workspaceId;
  callBackend(context, () => context.backend!.hide_browser_workspace({ workspaceId }), (error) => setWorkspaceError(context, error), () => isCurrentBrowserWorkspace(context, workspaceId));
  context.workspace.activated = false;
  context.workspace.interaction = 'browse';
  context.workspace.pendingSelection = null;
  context.workspace.pendingMarkup = null;
  return context.workspace;
}

export function createBrowserTab(
  first?: string | BrowserWorkspaceState | BrowserModelContext | CreateBrowserTabInput,
  second?: CreateBrowserTabInput | BrowserBackend
): BrowserTabState {
  const parsed = parseWorkspaceAndInput<CreateBrowserTabInput>(first, second, {});
  const context = parsed.context;
  const input = typeof first === 'string' ? { url: first } : parsed.input;
  const workspace = context.workspace;
  const tabId = input.tabId?.trim() || idFor(context, 'tab');
  // Restored tab identities reserve their sequence before a new tab is generated.
  idSequence = Math.max(idSequence, Number(tabId.match(/^browser-tab-(\d+)$/)?.[1] ?? 0));
  const existing = workspace.tabs[tabId];
  if (existing) {
    workspace.activeTabId = existing.id;
    workspace.activated = true;
    return existing;
  }
  const url = input.url?.trim() ? ensureSafeBrowserUrl(input.url) : '';
  const viewportInput = input.viewport;
  const viewport: BrowserViewport =
    typeof viewportInput === 'object' && viewportInput !== null && 'preset' in viewportInput
      ? resolveBrowserViewport(viewportInput as { preset?: string; width?: number | null; height?: number | null })
      : resolveBrowserViewport((viewportInput as BrowserViewportPreset | undefined) ?? 'responsive');
  const generation = Math.max(workspace.lastGeneration + 1, input.generation ?? 1);
  const tab: BrowserTabState = {
    id: tabId,
    workspaceId: workspace.workspaceId,
    url,
    inputUrl: url,
    title: input.title?.trim() || (url ? new URL(url).hostname : 'New browser tab'),
    loadState: url ? 'loading' : 'idle',
    canGoBack: false,
    canGoForward: false,
    viewport,
    annotations: [],
    generation,
    error: null
  };
  workspace.tabs[tabId] = tab;
  workspace.tabOrder = [...workspace.tabOrder, tabId];
  workspace.activeTabId = tabId;
  workspace.activeGeneration = generation;
  workspace.lastGeneration = generation;
  workspace.activated = true;
  workspace.error = null;
  const target = backendTarget(tab);
  callBackend(
    context,
    () => context.backend!.create_browser_tab({
      ...target,
      url,
      bounds: workspace.floatingBounds,
      viewport,
      profileId: workspace.profileSummary?.id ?? null
    }),
    (error) => {
      tab.error = describeError(error);
      tab.loadState = 'error';
      setWorkspaceError(context, error);
    },
    () => isCurrentBackendTarget(context, target)
  );
  return tab;
}

export function selectBrowserTab(
  first: string | BrowserWorkspaceState | BrowserModelContext,
  second?: string | BrowserModelContext
): BrowserWorkspaceState {
  const context = isWorkspace(first) || isContext(first) ? contextFor(first, second) : contextFor(second);
  const tabId = typeof first === 'string' ? first : typeof second === 'string' ? second : '';
  if (!tabId || !context.workspace.tabs[tabId]) throw new BrowserModelError('That browser tab is no longer available');
  context.workspace.activeTabId = tabId;
  updateActiveGeneration(context, context.workspace.tabs[tabId]);
  context.workspace.error = null;
  const tab = context.workspace.tabs[tabId];
  // Only a page meant to be on screen is shown. Choosing a tab while the view
  // is put away (an editor in front, a tab closed behind it) must not bring the
  // page back over whatever is in front; the panel shows it when it places it.
  if (context.workspace.activated && isExpandedBrowserMode(context.workspace.presentation)) {
    const target = backendTarget(tab);
    callBackend(context, () => context.backend!.show_browser_tab(target), (error) => setWorkspaceError(context, error), () => isCurrentBackendTarget(context, target));
  }
  return context.workspace;
}

export function closeBrowserTab(
  first: string | BrowserWorkspaceState | BrowserModelContext,
  second?: string | BrowserModelContext
): BrowserWorkspaceState {
  const context = isWorkspace(first) || isContext(first) ? contextFor(first, second) : contextFor(second);
  const tabId = typeof first === 'string' ? first : typeof second === 'string' ? second : '';
  const tab = context.workspace.tabs[tabId];
  if (!tab) return context.workspace;
  const target = backendTarget(tab);
  callBackend(context, () => context.backend!.close_browser_tab(target), (error) => setWorkspaceError(context, error), () => isCurrentBackendTarget(context, target));
  delete context.workspace.tabs[tabId];
  context.workspace.tabOrder = context.workspace.tabOrder.filter((id) => id !== tabId);
  if (context.workspace.activeTabId === tabId) {
    const nextId = context.workspace.tabOrder[Math.max(0, context.workspace.tabOrder.length - 1)] ?? null;
    context.workspace.activeTabId = nextId;
  }
  updateActiveGeneration(context, activeTab(context));
  if (!context.workspace.activeTabId) context.workspace.interaction = 'browse';
  return context.workspace;
}

export function navigateActiveBrowserTab(
  first: string | BrowserWorkspaceState | BrowserModelContext,
  second?: string | BrowserModelContext
): BrowserTabState | null {
  const context = typeof first === 'string' && (isWorkspace(second) || isContext(second)) ? contextFor(second) : contextFor(isWorkspace(first) || isContext(first) ? first : undefined);
  const url = typeof first === 'string' ? first : typeof second === 'string' ? second : '';
  const tab = activeTab(context);
  if (!tab) return null;
  const normalized = ensureSafeBrowserUrl(url);
  const generation = tab.generation + 1;
  tab.url = normalized;
  tab.inputUrl = normalized;
  const parsed = new URL(normalized);
  tab.title = parsed.protocol === 'file:'
    ? localFileTitle(parsed)
    : parsed.hostname;
  tab.generation = generation;
  tab.loadState = 'loading';
  tab.error = null;
  tab.canGoBack = true;
  tab.canGoForward = false;
  context.workspace.activeGeneration = generation;
  context.workspace.lastGeneration = Math.max(context.workspace.lastGeneration, generation);
  context.workspace.error = null;
  const target = backendTarget(tab);
  callBackend(context, () => context.backend!.navigate_browser_tab({ ...target, url: normalized }), (error) => {
    tab.error = describeError(error);
    tab.loadState = 'error';
    setWorkspaceError(context, error);
  }, () => isCurrentBackendTarget(context, target));
  return tab;
}

function localFileTitle(url: URL): string {
  const encodedName = url.pathname.split('/').pop() ?? '';
  try {
    return decodeURIComponent(encodedName);
  } catch {
    return encodedName;
  }
}

export function setBrowserViewport(
  first: BrowserViewport | BrowserViewportPreset | { preset?: string; width?: number | null; height?: number | null } | BrowserWorkspaceState | BrowserModelContext,
  second?: BrowserViewport | BrowserViewportPreset | { preset?: string; width?: number | null; height?: number | null } | BrowserModelContext
): BrowserTabState | null {
  const context = isWorkspace(first) || isContext(first) ? contextFor(first, second) : contextFor(second);
  const input = (isWorkspace(first) || isContext(first) ? second : first) as BrowserViewport | BrowserViewportPreset | { preset?: string; width?: number | null; height?: number | null };
  const tab = activeTab(context);
  if (!tab) return null;
  const viewport =
    typeof input === 'object' && input !== null && 'preset' in input
      ? resolveBrowserViewport(input as { preset?: string; width?: number | null; height?: number | null })
      : resolveBrowserViewport((input as BrowserViewportPreset | undefined) ?? 'responsive');
  tab.viewport = viewport;
  if (context.backend && 'set_browser_tab_viewport' in context.backend) {
    const method = (context.backend as BrowserBackend & {
      set_browser_tab_viewport?: (value: { workspaceId: string; tabId: string; generation: number; viewport: BrowserViewport }) => BrowserBackendResult<void>;
    }).set_browser_tab_viewport;
    if (method) {
      const target = backendTarget(tab);
      callBackend(context, () => method.call(context.backend, { ...target, viewport }), (error) => setWorkspaceError(context, error), () => isCurrentBackendTarget(context, target));
    }
  }
  return tab;
}

export function setBrowserPresentationMode(
  first: BrowserPresentationMode | BrowserWorkspaceState | BrowserModelContext,
  second?: BrowserPresentationMode | BrowserModelContext,
  third?: BrowserPresentationOptions
): BrowserWorkspaceState {
  const context = isWorkspace(first) || isContext(first) ? contextFor(first, second) : contextFor(second);
  const mode = (typeof first === 'string' ? first : typeof second === 'string' ? second : 'docked') as BrowserPresentationMode;
  if (!['docked', 'floating', 'maximized', 'collapsed'].includes(mode)) {
    throw new BrowserModelError('Unknown browser presentation mode');
  }
  const options = third ?? {};
  if (options.bounds || options.window) {
    const next = clampBrowserFloatingBounds(
      { ...context.workspace.floatingBounds, ...(options.bounds ?? {}) },
      options.window ?? {
        width: Math.max(1, context.workspace.floatingBounds.x + context.workspace.floatingBounds.width),
        height: Math.max(1, context.workspace.floatingBounds.y + context.workspace.floatingBounds.height)
      },
      options.minSize
    );
    // Only write when the rectangle actually moved. The workspace is reactive
    // state, and a caller that re-measures on every layout change would other-
    // wise invalidate itself with an equal-but-new object on every pass.
    if (!browserBoundsEqual(context.workspace.floatingBounds, next)) {
      context.workspace.floatingBounds = next;
    }
  }
  const transition = transitionBrowserPresentation(
    context.workspace.presentation,
    context.workspace.previousPresentation,
    mode
  );
  context.workspace.presentation = transition.presentation;
  context.workspace.previousPresentation = transition.previousPresentation;
  const tab = activeTab(context);
  if (tab && isExpandedBrowserMode(mode)) {
    const target = backendTarget(tab);
    callBackend(context, () => context.backend!.show_browser_tab(target), (error) => setWorkspaceError(context, error), () => isCurrentBackendTarget(context, target));
    callBackend(context, () => context.backend!.set_browser_tab_bounds({ ...target, bounds: context.workspace.floatingBounds }), (error) => setWorkspaceError(context, error), () =>
      isCurrentBackendTarget(context, target)
    );
  } else if (mode === 'collapsed') {
    const workspaceId = context.workspace.workspaceId;
    callBackend(context, () => context.backend!.hide_browser_workspace({ workspaceId }), (error) => setWorkspaceError(context, error), () => isCurrentBrowserWorkspace(context, workspaceId));
  }
  return context.workspace;
}

export function expandBrowserFrom(
  first?: BrowserPresentationMode | BrowserWorkspaceState | BrowserModelContext,
  second?: BrowserPresentationMode | BrowserModelContext
): BrowserWorkspaceState {
  const context = isWorkspace(first) || isContext(first) ? contextFor(first, second) : contextFor(second);
  const requested = typeof first === 'string' ? first : typeof second === 'string' ? second : undefined;
  const from = requested && ['docked', 'collapsed', 'floating', 'maximized'].includes(requested)
    ? (requested as BrowserPresentationMode)
    : context.workspace.presentation;
  const target = from === 'maximized' ? 'maximized' : 'floating';
  context.workspace.previousPresentation = from === 'collapsed' ? 'docked' : from;
  return setBrowserPresentationMode(context, target);
}

export function restoreBrowserToDock(target?: BrowserWorkspaceState | BrowserModelContext): BrowserWorkspaceState {
  return setBrowserPresentationMode(contextFor(target), 'docked');
}

export function minimizeBrowserToPrevious(target?: BrowserWorkspaceState | BrowserModelContext): BrowserWorkspaceState {
  const context = contextFor(target);
  const targetMode = restorePresentationTarget(context.workspace.presentation, context.workspace.previousPresentation);
  const result = setBrowserPresentationMode(context, targetMode);
  if (targetMode === 'collapsed') {
    const workspaceId = context.workspace.workspaceId;
    callBackend(context, () => context.backend!.hide_browser_workspace({ workspaceId }), (error) => setWorkspaceError(context, error), () => isCurrentBrowserWorkspace(context, workspaceId));
  }
  return result;
}

export function collapseBrowserToControl(target?: BrowserWorkspaceState | BrowserModelContext): BrowserWorkspaceState {
  return setBrowserPresentationMode(contextFor(target), 'collapsed');
}

export function beginBrowserElementPicker(
  first?: BrowserWorkspaceState | BrowserModelContext | 'grab' | 'annotation',
  second?: 'grab' | 'annotation' | BrowserModelContext
): BrowserWorkspaceState {
  const context = isWorkspace(first) || isContext(first) ? contextFor(first, second) : contextFor(second);
  const kind = (typeof first === 'string' ? first : typeof second === 'string' ? second : 'annotation') as 'grab' | 'annotation';
  const tab = activeTab(context);
  if (!tab) throw new BrowserModelError('Open a browser tab before selecting an element');
  context.workspace.interaction = 'picking';
  context.workspace.pendingSelection = null;
  context.workspace.pendingSelectionKind = kind;
  context.workspace.pendingMarkup = null;
  const target = backendTarget(tab);
  callBackend(context, () => context.backend!.arm_browser_element_picker({ ...target, mode: kind }), (error) => setWorkspaceError(context, error), () =>
    isCurrentBackendTarget(context, target)
  );
  return context.workspace;
}

export function acceptBrowserElementSelection(
  first: BrowserElementSelectionInput | BrowserWorkspaceState | BrowserModelContext,
  second?: BrowserElementSelectionInput | BrowserModelContext
): BrowserWorkspaceState {
  const context = isWorkspace(first) || isContext(first) ? contextFor(first, second) : contextFor(second);
  const input = (isWorkspace(first) || isContext(first) ? second : first) as BrowserElementSelectionInput;
  const tab = activeTab(context);
  if (!tab) throw new BrowserModelError('Open a browser tab before accepting a selection');
  const selection = selectionFromElementInput(tab, input ?? {});
  if (selection.generation !== tab.generation) throw new BrowserModelError('The selected page is stale; select the element again');
  context.workspace.pendingSelection = selection;
  // Preserve whether this picker was Grab or Annotate until the card is queued.
  context.workspace.pendingSelectionKind ??= 'annotation';
  context.workspace.pendingMarkup = null;
  context.workspace.interaction = 'region';
  return context.workspace;
}

export function cancelBrowserAnnotation(target?: BrowserWorkspaceState | BrowserModelContext): BrowserWorkspaceState {
  const context = contextFor(target);
  const tab = activeTab(context);
  if (tab) {
    const backend = backendTarget(tab);
    callBackend(context, () => context.backend!.cancel_browser_element_picker(backend), (error) => setWorkspaceError(context, error), () => isCurrentBackendTarget(context, backend));
  }
  context.workspace.pendingSelection = null;
  context.workspace.pendingSelectionKind = null;
  context.workspace.pendingMarkup = null;
  context.workspace.interaction = 'browse';
  return context.workspace;
}

function queueAttachment(
  context: BrowserModelContext,
  input: BrowserAnnotationInput = {}
): BrowserFeedbackAttachment {
  const tab = activeTab(context);
  if (!tab) throw new BrowserModelError('Open a browser tab before adding feedback');
  if (!canAddBrowserAnnotation(tab)) throw new BrowserModelError(`A tab can have at most ${BROWSER_MAX_ANNOTATIONS_PER_TAB} annotations`);
  const selection = context.workspace.pendingSelection;
  if (!selection) throw new BrowserModelError('Select a page element before adding feedback');
  if (!isExactBrowserTarget(selection, tab.workspaceId, tab.id, tab.generation)) {
    throw new BrowserModelError('The selected page is stale; select the element again');
  }
  const kind = input.kind ?? context.workspace.pendingSelectionKind ?? 'annotation';
  const id = input.id?.trim() || idFor(context, 'feedback');
  const attachment = createImmutableBrowserFeedbackAttachment({
    id,
    kind,
    workspaceId: tab.workspaceId,
    tabId: tab.id,
    generation: tab.generation,
    url: selection.url,
    title: selection.title,
    selector: selection.selector,
    accessibleName: selection.accessibleName,
    textSnippet: selection.textSnippet,
    note: kind === 'grab' ? null : input.note,
    intent: kind === 'grab' ? 'context' : input.intent,
    imageAttachmentId: input.imageAttachmentId ?? selection.imageAttachmentId,
    createdAt: nowFor(context, input.createdAt),
    sourceHash: input.sourceHash ?? selection.sourceHash
  });
  tab.annotations = [...tab.annotations, attachment];
  context.workspace.queue = [...context.workspace.queue, attachment];
  context.workspace.pendingSelection = null;
  context.workspace.pendingSelectionKind = null;
  context.workspace.interaction = 'browse';
  return attachment;
}

function queueMarkup(
  context: BrowserModelContext,
  input: BrowserMarkupInput
): BrowserFeedbackAttachment {
  const tab = activeTab(context);
  if (!tab) throw new BrowserModelError('Open a browser tab before adding feedback');
  if (!canAddBrowserAnnotation(tab)) throw new BrowserModelError(`A tab can have at most ${BROWSER_MAX_ANNOTATIONS_PER_TAB} annotations`);
  const pending = pendingMarkupFromInput(tab, input);
  const id = input.id?.trim() || idFor(context, 'feedback');
  const imageAttachmentId = `browser-image:${id}`;
  const attachment = createImmutableBrowserFeedbackAttachment({
    id,
    kind: 'markup',
    workspaceId: tab.workspaceId,
    tabId: tab.id,
    generation: tab.generation,
    url: tab.url,
    title: tab.title,
    note: input.note,
    intent: input.intent,
    imageAttachmentId,
    createdAt: nowFor(context, input.createdAt),
    sourceHash: pending.capture.sourceHash
  });
  tab.annotations = [...tab.annotations, attachment];
  context.workspace.queue = [...context.workspace.queue, attachment];
  context.workspace.pendingMarkup = null;
  context.workspace.interaction = 'browse';
  context.workspace.markupCaptures ??= {};
  context.workspace.markupCaptures[imageAttachmentId] = pending.capture;
  return attachment;
}

export function queueBrowserAnnotation(
  first?: BrowserWorkspaceState | BrowserModelContext | BrowserAnnotationInput | BrowserMarkupInput,
  second?: BrowserAnnotationInput | BrowserMarkupInput | BrowserModelContext
): BrowserFeedbackAttachment | null {
  const context = isWorkspace(first) || isContext(first) ? contextFor(first, second) : contextFor(second);
  const input = (isWorkspace(first) || isContext(first) ? second : first) as BrowserAnnotationInput | BrowserMarkupInput | undefined;
  if (!input && context.workspace.pendingMarkup) {
    return queueMarkup(context, {
      capture: context.workspace.pendingMarkup.capture,
      note: context.workspace.pendingMarkup.note,
      intent: context.workspace.pendingMarkup.intent
    });
  }
  if (input && 'capture' in input && input.capture) return queueMarkup(context, input as BrowserMarkupInput);
  return queueAttachment(context, (input as BrowserAnnotationInput | undefined) ?? {});
}

export function removeBrowserAnnotation(
  first: string | BrowserWorkspaceState | BrowserModelContext,
  second?: string | BrowserModelContext
): BrowserWorkspaceState {
  const context = isWorkspace(first) || isContext(first) ? contextFor(first, second) : contextFor(second);
  const id = typeof first === 'string' ? first : typeof second === 'string' ? second : '';
  context.workspace.queue = context.workspace.queue.filter((item) => item.id !== id);
  for (const tab of Object.values(context.workspace.tabs)) {
    tab.annotations = tab.annotations.filter((item) => item.id !== id);
  }
  return context.workspace;
}

export function captureBrowserWorkspace(
  first?: BrowserWorkspaceState | BrowserModelContext
): BrowserWorkspaceCapture {
  const context = contextFor(first);
  const tabs = context.workspace.tabOrder.map((id) => context.workspace.tabs[id]).filter(Boolean).map(cloneTab);
  return {
    workspaceId: context.workspace.workspaceId,
    ownedId: context.workspace.ownedId,
    activeTabId: context.workspace.activeTabId,
    presentation: context.workspace.presentation,
    previousPresentation: context.workspace.previousPresentation,
    floatingBounds: { ...context.workspace.floatingBounds },
    interaction: context.workspace.interaction,
    pendingSelection: context.workspace.pendingSelection ? { ...context.workspace.pendingSelection } : null,
    pendingMarkup: context.workspace.pendingMarkup
      ? {
          ...context.workspace.pendingMarkup,
          capture: {
            ...context.workspace.pendingMarkup.capture,
            bytes: [...context.workspace.pendingMarkup.capture.bytes]
          }
        }
      : null,
    queue: [...context.workspace.queue],
    profileSummary: context.workspace.profileSummary ? { ...context.workspace.profileSummary } : null,
    activated: context.workspace.activated,
    tabs,
    activeGeneration: context.workspace.activeGeneration
  };
}

export async function captureBrowserMarkup(
  first?: BrowserWorkspaceState | BrowserModelContext | Omit<BrowserCaptureOptions, 'forMarkup'>,
  second?: Omit<BrowserCaptureOptions, 'forMarkup'>
): Promise<BrowserMarkupCapture> {
  const optionsFromFirst = first && !isWorkspace(first) && !isContext(first) ? first : undefined;
  const context = contextFor(optionsFromFirst ? undefined : first);
  const options = second ?? optionsFromFirst ?? {};
  const tab = activeTab(context);
  if (!tab) throw new BrowserModelError('Open a browser tab before capturing the viewport');

  const target = backendTarget(tab);
  const result = callBackend(
    context,
    () => context.backend!.capture_browser_viewport(target),
    (error) => setWorkspaceError(context, error),
    () => isCurrentBackendTarget(context, target)
  );
  const capture = await result;
  if (!capture) throw new BrowserModelError('The browser viewport could not be captured');

  const currentTab = context.workspace.tabs[tab.id];
  if (
    !currentTab ||
    currentTab.workspaceId !== tab.workspaceId ||
    currentTab.generation !== tab.generation ||
    context.workspace.activeTabId !== tab.id
  ) {
    return capture;
  }
  const pending: BrowserPendingMarkup = {
    workspaceId: tab.workspaceId,
    tabId: tab.id,
    generation: tab.generation,
    capture,
    note: options.note?.trim() || null,
    intent: options.intent ?? 'context'
  };
  context.workspace.pendingMarkup = pending;
  context.workspace.interaction = 'drawing';
  return capture;
}

export function formatBrowserFeedback(attachment: BrowserFeedbackAttachment): string {
  const lines = [
    `${attachment.kind === 'grab' ? 'Grabbed' : attachment.kind === 'markup' ? 'Marked up' : 'Annotated'} browser page`,
    `URL: ${attachment.url || 'No URL'}`,
    `Title: ${attachment.title || 'Untitled'}`,
    `Intent: ${attachment.intent}`
  ];
  if (attachment.selector) lines.push(`Selector: ${attachment.selector}`);
  if (attachment.accessibleName) lines.push(`Accessible name: ${attachment.accessibleName}`);
  if (attachment.textSnippet) lines.push(`Text: ${attachment.textSnippet}`);
  if (attachment.note) lines.push(`Note: ${attachment.note}`);
  if (attachment.imageAttachmentId) lines.push(`Image attachment: ${attachment.imageAttachmentId}`);
  return lines.join('\n');
}

function readConversation(bridge: BrowserConversationBridge | undefined, ownedId: string): BrowserConversationDraft | null {
  return bridge?.read?.(ownedId) ?? bridge?.getDraft?.(ownedId) ?? null;
}

function attachmentBytesFor(context: BrowserModelContext, attachment: BrowserFeedbackAttachment): readonly number[] | null {
  const capture = context.workspace.markupCaptures?.[attachment.imageAttachmentId ?? ''];
  return capture?.bytes ?? null;
}

function mergedDraft(currentDraft: string, attachment: BrowserFeedbackAttachment): string {
  const formatted = formatBrowserFeedback(attachment);
  return currentDraft.trim() ? `${currentDraft.trim()}\n\n${formatted}` : formatted;
}

function stageResult(
  status: BrowserFeedbackStageResult['status'],
  queueRetained: boolean,
  attachment: BrowserFeedbackAttachment | null
): BrowserFeedbackStageResult {
  return { status, queueRetained, attachment };
}

/**
 * Build a confirmation preview, then commit only when the same owner,
 * generation and draft snapshot still exist.  Queue removal is the final
 * mutation, so every rejected path keeps feedback available to retry.
 */
export function stageBrowserFeedbackPreview(
  first?: BrowserWorkspaceState | BrowserModelContext | BrowserFeedbackStageOptions,
  second?: BrowserFeedbackStageOptions | BrowserModelContext
): BrowserFeedbackPreview {
  const context = isWorkspace(first) || isContext(first) ? contextFor(first, second) : contextFor();
  const options = (isWorkspace(first) || isContext(first) ? second : first) as BrowserFeedbackStageOptions | undefined;
  const bridge = options?.bridge ?? options?.conversation ?? context.conversation;
  const attachment = context.workspace.queue.find((item) => !options?.attachmentId || item.id === options.attachmentId) ?? null;
  const ownedId = options?.ownedId ?? context.workspace.ownedId;
  const generation = options?.generation ?? context.workspace.activeGeneration;
  const conversation = ownedId ? readConversation(bridge, ownedId) : null;
  const currentDraft = conversation?.draft ?? '';
  const currentAttachments = conversation?.attachments ? [...conversation.attachments] : [];
  const expectedDraft = options?.draftSnapshot ?? options?.draft;
  const stale =
    !attachment ||
    !ownedId ||
    !bridge ||
    !conversation ||
    ownedId !== context.workspace.ownedId ||
    generation !== context.workspace.activeGeneration ||
    conversation.generation !== generation;
  const conflict = !stale && expectedDraft !== undefined && expectedDraft !== currentDraft;
  const status: BrowserFeedbackPreview['status'] = !attachment
    ? 'empty'
    : stale
      ? 'stale-target'
      : conflict
        ? 'draft-conflict'
        : 'ready';
  const preview: BrowserFeedbackPreview = {
    status,
    queueRetained: true,
    attachment,
    currentDraft,
    mergedDraft: attachment && status === 'ready' ? mergedDraft(currentDraft, attachment) : currentDraft,
    currentAttachments,
    commit: async () => {
      if (!attachment || status !== 'ready') {
        return stageResult(status === 'ready' ? 'empty' : status, true, attachment);
      }
      if (!bridge) return stageResult('attachment-error', true, attachment);
      const latestConversation = ownedId ? readConversation(bridge, ownedId) : null;
      if (!ownedId || !latestConversation || ownedId !== context.workspace.ownedId || generation !== context.workspace.activeGeneration || latestConversation.generation !== generation) {
        return stageResult('stale-target', true, attachment);
      }
      if (expectedDraft !== undefined && latestConversation?.draft !== expectedDraft) {
        return stageResult('draft-conflict', true, attachment);
      }
      try {
        let nextAttachments = [...(latestConversation?.attachments ?? currentAttachments)];
        const bytes = attachmentBytesFor(context, attachment);
        if (bytes && bridge?.saveAttachment && attachment.imageAttachmentId) {
          const saved = await bridge.saveAttachment({
            ownedId,
            name: `${attachment.id}.png`,
            mimeType: 'image/png',
            bytes
          });
          nextAttachments = [...nextAttachments, saved];
        }
        if (bridge?.setDraft) await bridge.setDraft(ownedId, mergedDraft(latestConversation?.draft ?? currentDraft, attachment));
        if (bridge?.setAttachments) await bridge.setAttachments(ownedId, nextAttachments);
        context.workspace.queue = context.workspace.queue.filter((item) => item.id !== attachment.id);
        const tab = context.workspace.tabs[attachment.tabId];
        if (tab) tab.annotations = tab.annotations.filter((item) => item.id !== attachment.id);
        if (attachment.imageAttachmentId && context.workspace.markupCaptures) {
          delete context.workspace.markupCaptures[attachment.imageAttachmentId];
        }
        return stageResult('staged', false, attachment);
      } catch (error) {
        context.workspace.error = describeError(error);
        return stageResult('attachment-error', true, attachment);
      }
    }
  };
  if (options?.confirm) void preview.commit();
  return preview;
}

export function createBrowserModel(input: {
  workspace?: BrowserWorkspaceState;
  backend?: BrowserBackend;
  conversation?: BrowserConversationBridge;
  now?: () => string;
  createId?: BrowserModelContext['createId'];
} = {}): BrowserModelHandle {
  const modelBackend = input.backend ?? createInMemoryBrowserBackend();
  const context: BrowserModelContext = {
    workspace: input.workspace ?? createBrowserWorkspace(),
    backend: modelBackend,
    conversation: input.conversation,
    now: input.now ?? (() => new Date().toISOString()),
    createId: input.createId
  };
  return {
    workspace: context.workspace,
    backend: context.backend!,
    activateBrowserWorkspace: (value) => activateBrowserWorkspace(context, value),
    deactivateBrowserWorkspace: () => deactivateBrowserWorkspace(context),
    createBrowserTab: (value) => createBrowserTab(context, value),
    selectBrowserTab: (tabId) => selectBrowserTab(context, tabId),
    closeBrowserTab: (tabId) => closeBrowserTab(context, tabId),
    navigateActiveBrowserTab: (url) => navigateActiveBrowserTab(context, url),
    setBrowserViewport: (viewport) => setBrowserViewport(context, viewport),
    setBrowserPresentationMode: (mode, options) => setBrowserPresentationMode(context, mode, options),
    expandBrowserFrom: (mode) => expandBrowserFrom(context, mode),
    restoreBrowserToDock: () => restoreBrowserToDock(context),
    minimizeBrowserToPrevious: () => minimizeBrowserToPrevious(context),
    collapseBrowserToControl: () => collapseBrowserToControl(context),
    beginBrowserElementPicker: (kind) => beginBrowserElementPicker(context, kind),
    acceptBrowserElementSelection: (value) => acceptBrowserElementSelection(context, value),
    cancelBrowserAnnotation: () => cancelBrowserAnnotation(context),
    queueBrowserAnnotation: (value) => queueBrowserAnnotation(context, value),
    removeBrowserAnnotation: (id) => removeBrowserAnnotation(context, id),
    captureBrowserWorkspace: () => captureBrowserWorkspace(context),
    captureBrowserMarkup: (options) => captureBrowserMarkup(context, options),
    formatBrowserFeedback,
    stageBrowserFeedbackPreview: (options) => stageBrowserFeedbackPreview(context, options)
  };
}
