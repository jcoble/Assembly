/**
 * Browser presentation contracts shared by the model, backend adapter and
 * Svelte surfaces. This module is deliberately data-only: it has no DOM or
 * desktop imports, so the same rules can be exercised by Node tests.
 */

export type BrowserPresentationMode = 'docked' | 'floating' | 'maximized' | 'collapsed';

/**
 * What the pointer does over the page. `picking` is Select waiting for the
 * element under the pointer; `region` drags a rectangle; `drawing` is freehand;
 * `erasing` takes a mark away. The old `annotating` was `region` under another
 * name and has folded into it.
 */
export type BrowserInteractionMode = 'browse' | 'picking' | 'region' | 'drawing' | 'erasing';

/**
 * What the pointer does on the still of the page in the browser panel.
 * `element` asks the page what is under the pointer; the other three mark the
 * still by hand. This is the panel's own state and not the workspace's
 * interaction mode: marking never arms the native page picker, because the
 * still has to stay up for the marks and the prompt to stay on screen.
 */
export type BrowserPanelTool = 'browse' | 'element' | 'region' | 'drawing' | 'erasing';

export type BrowserViewportPreset =
  | 'responsive'
  | 'mobile-s'
  | 'mobile-m'
  | 'mobile-l'
  | 'tablet'
  | 'laptop'
  | 'laptop-l'
  | 'desktop'
  | 'custom';

export type BrowserLoadState = 'idle' | 'loading' | 'loaded' | 'error';

export type BrowserFeedbackKind = 'grab' | 'annotation' | 'markup';

export type BrowserFeedbackIntent = 'change' | 'question' | 'context';

export interface BrowserFloatingBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface BrowserViewport {
  preset: BrowserViewportPreset;
  width: number | null;
  height: number | null;
}

export interface BrowserRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Bounded page metadata returned by the native inspector. */
export interface BrowserElementMetadata {
  selector: string | null;
  /** The element's own ARIA role attribute, when it has one. */
  role: string | null;
  accessibleName: string | null;
  textSnippet: string | null;
  rect: BrowserRect | null;
  classes: string[];
  classCount: number;
}

/** An opaque profile label.  Page-owned data never belongs in this shape. */
export interface BrowserProfileSummary {
  id: string;
  label: string;
}

export interface BrowserTabState {
  id: string;
  workspaceId: string;
  url: string;
  inputUrl: string;
  title: string;
  loadState: BrowserLoadState;
  canGoBack: boolean;
  canGoForward: boolean;
  viewport: BrowserViewport;
  annotations: BrowserFeedbackAttachment[];
  generation: number;
  error: string | null;
}

/** The native view reports its final location after links and history moves. */
export interface BrowserTabNavigationEvent {
  workspaceId: string;
  tabId: string;
  generation: number;
  url: string;
  title: string;
  canGoBack: boolean;
  canGoForward: boolean;
}

export interface BrowserElementSelection {
  workspaceId: string;
  tabId: string;
  generation: number;
  url: string;
  title: string;
  selector: string | null;
  accessibleName: string | null;
  textSnippet: string | null;
  imageAttachmentId: string | null;
  rect: BrowserRect | null;
  classes: string[];
  classCount: number;
  sourceHash: string;
}

export interface BrowserMarkupCapture {
  mimeType: string;
  bytes: readonly number[];
  width: number;
  height: number;
  sourceHash: string;
}

export interface BrowserPendingMarkup {
  workspaceId: string;
  tabId: string;
  generation: number;
  capture: BrowserMarkupCapture;
  note: string | null;
  intent: BrowserFeedbackIntent;
}

/**
 * Feedback is a value object.  Consumers may retain it as a queue item or
 * hand it to the conversation bridge; no consumer is allowed to mutate it.
 */
export interface BrowserFeedbackAttachment {
  readonly id: string;
  readonly kind: BrowserFeedbackKind;
  readonly workspaceId: string;
  readonly tabId: string;
  readonly generation: number;
  readonly url: string;
  readonly title: string;
  readonly selector: string | null;
  readonly accessibleName: string | null;
  readonly textSnippet: string | null;
  readonly note: string | null;
  readonly intent: BrowserFeedbackIntent;
  readonly imageAttachmentId: string | null;
  readonly createdAt: string;
  readonly sourceHash: string;
}

export interface BrowserWorkspaceState {
  workspaceId: string;
  /** The conversation owner for this workspace, when one has been selected. */
  ownedId: string | null;
  tabs: Record<string, BrowserTabState>;
  tabOrder: string[];
  activeTabId: string | null;
  presentation: BrowserPresentationMode;
  previousPresentation: BrowserPresentationMode | null;
  floatingBounds: BrowserFloatingBounds;
  interaction: BrowserInteractionMode;
  pendingSelection: BrowserElementSelection | null;
  pendingSelectionKind?: 'grab' | 'annotation' | null;
  pendingMarkup: BrowserPendingMarkup | null;
  queue: BrowserFeedbackAttachment[];
  /** Native captures remain keyed to their queued image id until staging. */
  markupCaptures?: Record<string, BrowserMarkupCapture>;
  profileSummary: BrowserProfileSummary | null;
  activeGeneration: number;
  /** The highest generation this workspace has sent. The native registry
   * keeps the same watermark and refuses a new tab that does not pass it,
   * even after the tab that set it was selected away from, closed or released. */
  lastGeneration: number;
  activated: boolean;
  error: string | null;
}

export interface BrowserWorkspaceCapture {
  workspaceId: string;
  ownedId: string | null;
  activeTabId: string | null;
  presentation: BrowserPresentationMode;
  previousPresentation: BrowserPresentationMode | null;
  floatingBounds: BrowserFloatingBounds;
  interaction: BrowserInteractionMode;
  pendingSelection: BrowserElementSelection | null;
  pendingMarkup: BrowserPendingMarkup | null;
  queue: BrowserFeedbackAttachment[];
  profileSummary: BrowserProfileSummary | null;
  activated: boolean;
  tabs: BrowserTabState[];
  activeGeneration: number;
}

export interface BrowserConversationAttachment {
  id: string;
  name: string;
  mimeType: string;
  path: string;
  previewUrl: string;
  originalUrl?: string;
  thumbnailMimeType?: string | null;
  thumbnailPath?: string | null;
  thumbnailByteLength?: number | null;
}

export interface BrowserConversationDraft {
  ownedId: string;
  generation: number;
  draft: string;
  attachments: BrowserConversationAttachment[];
}

export interface BrowserConversationBridge {
  read?(ownedId: string): BrowserConversationDraft | null;
  getDraft?(ownedId: string): BrowserConversationDraft | null;
  setDraft?(ownedId: string, draft: string): void | Promise<void>;
  setAttachments?(ownedId: string, attachments: BrowserConversationAttachment[]): void | Promise<void>;
  saveAttachment?(input: {
    ownedId: string;
    name: string;
    mimeType: string;
    bytes: readonly number[];
  }): BrowserConversationAttachment | Promise<BrowserConversationAttachment>;
}

export interface BrowserFeedbackPreview {
  status: 'ready' | 'empty' | 'stale-target' | 'draft-conflict' | 'attachment-error';
  queueRetained: boolean;
  attachment: BrowserFeedbackAttachment | null;
  currentDraft: string;
  mergedDraft: string;
  currentAttachments: BrowserConversationAttachment[];
  /** Apply the preview.  It never sends a conversation turn. */
  commit(): Promise<BrowserFeedbackStageResult>;
}

export interface BrowserFeedbackStageResult {
  status: 'staged' | 'empty' | 'stale-target' | 'draft-conflict' | 'attachment-error';
  queueRetained: boolean;
  attachment: BrowserFeedbackAttachment | null;
}

export interface BrowserElementSelectionInput {
  selector?: string | null;
  accessibleName?: string | null;
  textSnippet?: string | null;
  imageAttachmentId?: string | null;
  rect?: BrowserRect | null;
  classCount?: number;
  classes?: readonly string[];
  sourceHash?: string;
  url?: string;
  title?: string;
  generation?: number;
}

export interface BrowserAnnotationInput {
  kind?: BrowserFeedbackKind;
  note?: string | null;
  intent?: BrowserFeedbackIntent;
  imageAttachmentId?: string | null;
  createdAt?: string;
  sourceHash?: string;
  id?: string;
}

export interface BrowserMarkupInput {
  capture: BrowserMarkupCapture;
  note?: string | null;
  intent?: BrowserFeedbackIntent;
  id?: string;
  createdAt?: string;
}

export interface BrowserModelContext {
  workspace: BrowserWorkspaceState;
  backend?: import('./browserBackend.ts').BrowserBackend;
  now?: () => string;
  createId?: (kind: 'tab' | 'feedback' | 'attachment') => string;
  conversation?: BrowserConversationBridge;
}

export const DEFAULT_BROWSER_FLOATING_BOUNDS: BrowserFloatingBounds = {
  x: 72,
  y: 72,
  width: 720,
  height: 520
};

export function createBrowserWorkspace(input: {
  workspaceId?: string;
  ownedId?: string | null;
  profileSummary?: BrowserProfileSummary | null;
  floatingBounds?: BrowserFloatingBounds;
} = {}): BrowserWorkspaceState {
  return {
    workspaceId: input.workspaceId?.trim() || 'browser-workspace',
    ownedId: input.ownedId?.trim() || null,
    tabs: {},
    tabOrder: [],
    activeTabId: null,
    presentation: 'docked',
    previousPresentation: null,
    floatingBounds: { ...(input.floatingBounds ?? DEFAULT_BROWSER_FLOATING_BOUNDS) },
    interaction: 'browse',
    pendingSelection: null,
    pendingSelectionKind: null,
    pendingMarkup: null,
    queue: [],
    markupCaptures: {},
    profileSummary: input.profileSummary ? { ...input.profileSummary } : null,
    activeGeneration: 0,
    lastGeneration: 0,
    activated: false,
    error: null
  };
}
