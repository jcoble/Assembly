import type {
  BrowserAnnotationInput,
  BrowserElementSelection,
  BrowserFeedbackAttachment,
  BrowserFeedbackIntent,
  BrowserFeedbackKind,
  BrowserMarkupInput,
  BrowserPendingMarkup,
  BrowserTabState
} from './browserTypes.ts';

export const BROWSER_SELECTOR_MAX_LENGTH = 2_048;
export const BROWSER_SNIPPET_MAX_LENGTH = 500;
export const BROWSER_MAX_CLASSES = 32;
export const BROWSER_MAX_ANNOTATIONS_PER_TAB = 100;

function text(value: unknown): string {
  return typeof value === 'string' ? value.trim() : '';
}

export function boundSelector(value: string | null | undefined): string | null {
  const normalized = text(value);
  return normalized ? normalized.slice(0, BROWSER_SELECTOR_MAX_LENGTH) : null;
}

export function boundTextSnippet(value: string | null | undefined): string | null {
  const normalized = text(value);
  return normalized ? normalized.slice(0, BROWSER_SNIPPET_MAX_LENGTH) : null;
}

export function boundClassCount(value: number | null | undefined): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(BROWSER_MAX_CLASSES, Math.floor(value)));
}

/** A small stable digest for metadata-only captures. */
export function browserSourceHash(parts: readonly (string | number | null | undefined)[]): string {
  let hash = 2166136261;
  for (const part of parts) {
    const value = String(part ?? '');
    for (let index = 0; index < value.length; index += 1) {
      hash ^= value.charCodeAt(index);
      hash = Math.imul(hash, 16777619);
    }
    hash ^= 31;
    hash = Math.imul(hash, 16777619);
  }
  return `browser-${(hash >>> 0).toString(16).padStart(8, '0')}`;
}

function intentFor(kind: BrowserFeedbackKind, intent: BrowserFeedbackIntent | undefined): BrowserFeedbackIntent {
  if (kind === 'grab') return 'context';
  if (kind === 'annotation') return intent === 'question' ? 'question' : 'change';
  return intent === 'change' || intent === 'question' ? intent : 'context';
}

function immutable<T extends object>(value: T): T {
  return Object.freeze(value);
}

export function createImmutableBrowserFeedbackAttachment(input: {
  id: string;
  kind: BrowserFeedbackKind;
  workspaceId: string;
  tabId: string;
  generation: number;
  url: string;
  title: string;
  selector?: string | null;
  accessibleName?: string | null;
  textSnippet?: string | null;
  note?: string | null;
  intent?: BrowserFeedbackIntent;
  imageAttachmentId?: string | null;
  createdAt: string;
  sourceHash?: string;
}): BrowserFeedbackAttachment {
  const kind = input.kind;
  const note = text(input.note) || null;
  if (kind === 'annotation' && !note) throw new Error('An annotation note is required');
  if (kind === 'annotation' && input.intent !== 'change' && input.intent !== 'question') {
    throw new Error('An annotation intent must be Change or Question');
  }
  return immutable({
    id: text(input.id),
    kind,
    workspaceId: text(input.workspaceId),
    tabId: text(input.tabId),
    generation: Number.isFinite(input.generation) ? Math.max(0, Math.floor(input.generation)) : 0,
    url: text(input.url),
    title: text(input.title),
    selector: boundSelector(input.selector),
    accessibleName: text(input.accessibleName) || null,
    textSnippet: boundTextSnippet(input.textSnippet),
    note,
    intent: intentFor(kind, input.intent),
    imageAttachmentId: text(input.imageAttachmentId) || null,
    createdAt: text(input.createdAt),
    sourceHash:
      text(input.sourceHash) ||
      browserSourceHash([
        input.workspaceId,
        input.tabId,
        input.generation,
        input.url,
        input.selector,
        input.textSnippet,
        input.imageAttachmentId
      ])
  });
}

export const createBrowserFeedbackAttachment = createImmutableBrowserFeedbackAttachment;
export const sanitizeBrowserSelector = boundSelector;
export const sanitizeBrowserSnippet = boundTextSnippet;

export function selectionFromElementInput(
  tab: BrowserTabState,
  input: {
    selector?: string | null;
    accessibleName?: string | null;
    textSnippet?: string | null;
    imageAttachmentId?: string | null;
    rect?: { x: number; y: number; width: number; height: number } | null;
    classCount?: number;
    classes?: readonly string[];
    sourceHash?: string;
    url?: string;
    title?: string;
    generation?: number;
  }
): BrowserElementSelection {
  const selector = boundSelector(input.selector);
  const textSnippet = boundTextSnippet(input.textSnippet);
  const classes = Array.isArray(input.classes)
    ? input.classes
        .filter((value): value is string => typeof value === 'string' && value.trim().length > 0)
        .slice(0, 32)
        .map((value) => value.trim())
    : [];
  const classCount = boundClassCount(input.classCount ?? classes.length);
  const rect = input.rect
    ? {
        x: Number.isFinite(input.rect.x) ? input.rect.x : 0,
        y: Number.isFinite(input.rect.y) ? input.rect.y : 0,
        width: Math.max(0, Number.isFinite(input.rect.width) ? input.rect.width : 0),
        height: Math.max(0, Number.isFinite(input.rect.height) ? input.rect.height : 0)
      }
    : null;
  const generation = Number.isFinite(input.generation) ? Math.floor(input.generation as number) : tab.generation;
  return {
    workspaceId: tab.workspaceId,
    tabId: tab.id,
    generation,
    url: text(input.url) || tab.url,
    title: text(input.title) || tab.title,
    selector,
    accessibleName: text(input.accessibleName) || null,
    textSnippet,
    imageAttachmentId: text(input.imageAttachmentId) || null,
    rect,
    classes,
    classCount,
    sourceHash:
      text(input.sourceHash) ||
      browserSourceHash([tab.workspaceId, tab.id, generation, tab.url, selector, textSnippet, classes.join('.')])
  };
}

export function pendingMarkupFromInput(
  tab: BrowserTabState,
  input: BrowserMarkupInput
): BrowserPendingMarkup {
  if (!input.capture || !Array.isArray(input.capture.bytes)) {
    throw new Error('A bounded viewport capture is required');
  }
  return {
    workspaceId: tab.workspaceId,
    tabId: tab.id,
    generation: tab.generation,
    capture: {
      mimeType: text(input.capture.mimeType) || 'image/png',
      bytes: Object.freeze([...input.capture.bytes]),
      width: Math.max(0, Math.floor(input.capture.width)),
      height: Math.max(0, Math.floor(input.capture.height)),
      sourceHash: text(input.capture.sourceHash) || browserSourceHash([tab.id, tab.generation, input.capture.width, input.capture.height])
    },
    note: text(input.note) || null,
    intent: intentFor('markup', input.intent)
  };
}

export function canAddBrowserAnnotation(tab: BrowserTabState): boolean {
  return tab.annotations.length < BROWSER_MAX_ANNOTATIONS_PER_TAB;
}

export function annotationKindRequiresNote(kind: BrowserFeedbackKind): boolean {
  return kind === 'annotation';
}

export function isExactBrowserTarget(
  attachment: Pick<BrowserFeedbackAttachment, 'workspaceId' | 'tabId' | 'generation'>,
  workspaceId: string,
  tabId: string,
  generation: number
): boolean {
  return (
    attachment.workspaceId === workspaceId &&
    attachment.tabId === tabId &&
    attachment.generation === generation
  );
}

export function cloneBrowserFeedbackAttachment(
  attachment: BrowserFeedbackAttachment
): BrowserFeedbackAttachment {
  return immutable({ ...attachment });
}
