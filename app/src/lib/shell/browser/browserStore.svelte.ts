/**
 * Browser store compatibility facade.
 *
 * The old /next callers still receive `browser`, `activateBrowser`,
 * `setBrowserUrl`, `reloadBrowserFrame`, `captureBrowserState` and
 * `restoreBrowserState`.  New surfaces use the same reactive workspace and
 * the pure browser model underneath. Durable restoration belongs to the active
 * session's SQLite workspace snapshot, not browser storage.
 */
import type {
  SessionBrowserTabWorkspace,
  SessionBrowserWorkspace
} from '../sessionWorkspaces.ts';
import {
  saveConversationClipboardImage
} from '../conversation/conversationService.ts';
import {
  getConversationSession,
  setConversationAttachments,
  setConversationDraft
} from '../conversation/conversationStore.svelte.ts';
import {
  activateBrowserWorkspace,
  BrowserModelError,
  captureBrowserMarkup,
  captureBrowserWorkspace,
  closeBrowserTab,
  createBrowserTab,
  deactivateBrowserWorkspace as deactivateBrowserWorkspaceModel,
  navigateActiveBrowserTab,
  selectBrowserTab,
  setBrowserPresentationMode,
  setBrowserViewport,
  stageBrowserFeedbackPreview,
  type BrowserCaptureOptions,
  type BrowserFeedbackStageOptions
} from './browserModel.ts';
import {
  createBrowserBackend,
  type BrowserBackend
} from './browserBackend.ts';
import {
  createBrowserWorkspace,
  type BrowserConversationAttachment,
  type BrowserConversationBridge,
  type BrowserFeedbackPreview,
  type BrowserMarkupCapture,
  type BrowserModelContext,
  type BrowserPresentationMode,
  type BrowserTabNavigationEvent,
  type BrowserViewportPreset,
  type BrowserWorkspaceState
} from './browserTypes.ts';
import { normalizeBrowserUrl } from './normalizeBrowserUrl.ts';

export const INVALID_URL_MESSAGE = 'Enter an http, https, or file address';

export interface BrowserCompatibilityState {
  url: string;
  inputUrl: string;
  frameKey: number;
  error: string;
  activated: boolean;
  workspace: BrowserWorkspaceState;
  backend: BrowserBackend;
}

const workspace = createBrowserWorkspace({ workspaceId: 'next-browser' });
// The native registry outlives this page and refuses a new tab at or below the
// highest generation it has seen, so after a reload counting from zero again
// left every new tab without a page. The clock keeps generations rising.
workspace.lastGeneration = Date.now();
const backend = createBrowserBackend();
/** Pages remembered while no native view exists (after a session switch or
 * leaving the browser tab). Reactive so the top tab row still lists them. */
const restored = $state<{ tabs: SessionBrowserTabWorkspace[]; activeTabId: string | null }>({
  tabs: [],
  activeTabId: null
});

const conversationBridge = {
  read(ownedId: string) {
    const current = getConversationSession(ownedId);
    if (!current) return null;
    return {
      ownedId,
      generation: current.generation,
      draft: current.draft,
      attachments: current.attachments.map((attachment) => ({ ...attachment }))
    };
  },
  setDraft(ownedId: string, draft: string): void {
    setConversationDraft(ownedId, draft);
  },
  setAttachments(ownedId: string, attachments: BrowserConversationAttachment[]): void {
    setConversationAttachments(ownedId, attachments);
  },
  async saveAttachment(input: { ownedId: string; name: string; mimeType: string; bytes: readonly number[] }) {
    const file = new File([Uint8Array.from(input.bytes)], input.name, { type: input.mimeType });
    return saveConversationClipboardImage(input.ownedId, file);
  }
};

export const browser = $state<BrowserCompatibilityState>({
  url: '',
  inputUrl: '',
  frameKey: 0,
  error: '',
  activated: false,
  workspace,
  backend
});

/** New browser surfaces can consume the model state directly. */
export const browserWorkspace = browser.workspace;
export const browserBackend = browser.backend;

function modelContext(): BrowserModelContext {
  return { workspace: browser.workspace, backend: browser.backend };
}

function syncLegacy(nextUrl?: string, bumpFrame = false): void {
  const active = browser.workspace.activeTabId
    ? browser.workspace.tabs[browser.workspace.activeTabId] ?? null
    : null;
  const url = nextUrl ?? active?.url ?? '';
  if (bumpFrame && browser.url === url && url) browser.frameKey += 1;
  else if (bumpFrame && browser.url !== url) browser.frameKey += 1;
  browser.url = url;
  browser.inputUrl = active?.inputUrl ?? url;
  browser.activated = browser.workspace.activated;
  browser.error = browser.workspace.error ?? '';
}

export function captureBrowserState(): SessionBrowserWorkspace {
  const tabs = browser.workspace.tabOrder
    .map((id) => browser.workspace.tabs[id])
    .filter((tab) => Boolean(tab))
    .map((tab) => ({
      id: tab.id,
      url: tab.url,
      inputUrl: tab.inputUrl,
      title: tab.title
    }));
  return {
    tabs: tabs.length > 0 ? tabs : restored.tabs.map((tab) => ({ ...tab })),
    activeTabId: browser.workspace.activeTabId ?? restored.activeTabId
  };
}

export function restoreBrowserState(snapshot: SessionBrowserWorkspace | null | undefined): void {
  restored.tabs = snapshot?.tabs.map((tab) => ({ ...tab })) ?? [];
  restored.activeTabId = snapshot?.activeTabId ?? restored.tabs.at(-1)?.id ?? null;
  const restoredActive = restored.tabs.find((tab) => tab.id === restored.activeTabId) ?? null;
  browser.workspace.activated = false;
  browser.workspace.error = null;
  browser.url = restoredActive?.url ?? '';
  browser.inputUrl = restoredActive?.inputUrl ?? browser.url;
  browser.activated = false;
  browser.error = '';
}

export function activateBrowser(): void {
  if (browser.workspace.activated) return;
  const savedUrl = normalizeBrowserUrl(browser.url);
  try {
    activateBrowserWorkspace(modelContext());
    if (restored.tabs.length > 0 && !browser.workspace.activeTabId) {
      for (const remembered of restored.tabs) {
        createBrowserTab(modelContext(), {
          tabId: remembered.id,
          url: remembered.url,
          title: remembered.title
        });
        const created = browser.workspace.tabs[remembered.id];
        if (created) created.inputUrl = remembered.inputUrl;
      }
      if (restored.activeTabId && browser.workspace.tabs[restored.activeTabId]) {
        selectBrowserTab(modelContext(), restored.activeTabId);
      }
      restored.tabs = [];
      restored.activeTabId = null;
    } else if (savedUrl && !browser.workspace.activeTabId) {
      createBrowserTab(modelContext(), { url: savedUrl });
    }
    syncLegacy();
  } catch (error) {
    browser.workspace.error = error instanceof Error ? error.message : String(error);
    syncLegacy();
  }
}

export function deactivateBrowserWorkspace(): void {
  try {
    deactivateBrowserWorkspaceModel(modelContext());
    syncLegacy();
  } catch (error) {
    browser.workspace.error = error instanceof Error ? error.message : String(error);
    syncLegacy();
  }
}

/** Release native browser views while keeping only the compact address metadata. */
export function releaseBrowserWorkspace(): void {
  const url = browser.url;
  const inputUrl = browser.inputUrl;
  const liveTabs = browser.workspace.tabOrder
    .map((id) => browser.workspace.tabs[id])
    .filter((tab) => Boolean(tab))
    .map((tab) => ({
      id: tab.id,
      url: tab.url,
      inputUrl: tab.inputUrl,
      title: tab.title
    }));
  if (liveTabs.length > 0) {
    restored.tabs = liveTabs;
    restored.activeTabId = browser.workspace.activeTabId;
  }
  // Hide the whole native workspace first. Closing its tabs is asynchronous,
  // so relying on close alone can leave the child view intercepting the shell
  // after the Svelte panel that owned it has already been removed.
  deactivateBrowserWorkspaceModel(modelContext());
  for (const tabId of Object.keys(browser.workspace.tabs)) {
    closeBrowserTab(modelContext(), tabId);
  }
  browser.workspace.tabs = {};
  browser.workspace.tabOrder = [];
  browser.workspace.activeTabId = null;
  browser.workspace.activated = false;
  browser.workspace.interaction = 'browse';
  browser.workspace.pendingSelection = null;
  browser.workspace.pendingSelectionKind = null;
  browser.workspace.pendingMarkup = null;
  browser.workspace.queue = [];
  browser.workspace.markupCaptures = {};
  browser.workspace.error = null;
  browser.url = url;
  browser.inputUrl = inputUrl;
  browser.activated = false;
  browser.error = '';
}

export function hasRestoredBrowserTabs(): boolean {
  return restored.tabs.length > 0;
}

/** Remember a new page without creating a native view; it opens when its
 * tab is shown. Returns the new tab id. */
export function queueBrowserPageTab(url = ''): string {
  const id = `tab-${crypto.randomUUID()}`;
  restored.tabs = [...restored.tabs, { id, url, inputUrl: url, title: 'New browser tab' }];
  restored.activeTabId = id;
  return id;
}

/** Make a page the active one, live or remembered. */
export function selectBrowserPageTab(id: string): void {
  if (browser.workspace.tabs[id]) syncBrowserTab(id);
  else if (restored.tabs.some((tab) => tab.id === id)) restored.activeTabId = id;
}

export function setBrowserUrl(value: string): boolean {
  try {
    activateBrowser();
    const active = browser.workspace.activeTabId
      ? browser.workspace.tabs[browser.workspace.activeTabId] ?? null
      : null;
    if (!active) createBrowserTab(modelContext(), { url: value });
    else navigateActiveBrowserTab(modelContext(), value);
    browser.workspace.error = null;
    syncLegacy(undefined, true);
    return true;
  } catch (error) {
    browser.error = error instanceof BrowserModelError ? error.message : INVALID_URL_MESSAGE;
    browser.workspace.error = browser.error;
    return false;
  }
}

export function openBrowserUrl(url: string): boolean {
  return setBrowserUrl(url);
}

export function reloadBrowserFrame(): void {
  const active = browser.workspace.activeTabId
    ? browser.workspace.tabs[browser.workspace.activeTabId] ?? null
    : null;
  if (!active) return;
  try {
    browser.backend.reload_browser_tab({
      workspaceId: active.workspaceId,
      tabId: active.id,
      generation: active.generation
    });
    browser.frameKey += 1;
    browser.workspace.error = null;
  } catch (error) {
    browser.workspace.error = error instanceof Error ? error.message : String(error);
  }
  syncLegacy();
}

export function clearBrowserUrl(): void {
  const active = browser.workspace.activeTabId
    ? browser.workspace.tabs[browser.workspace.activeTabId] ?? null
    : null;
  if (active) closeBrowserTab(modelContext(), active.id);
  browser.workspace.error = null;
  browser.url = '';
  browser.inputUrl = '';
  browser.activated = browser.workspace.activated;
}

export function clearBrowserError(): void {
  browser.error = '';
  browser.workspace.error = null;
}

export function createBrowserPageTab(): string | null {
  try {
    activateBrowser();
    const tab = createBrowserTab(modelContext());
    syncLegacy();
    return tab.id;
  } catch (error) {
    browser.workspace.error = error instanceof Error ? error.message : String(error);
    syncLegacy();
    return null;
  }
}

export function closeBrowserPageTab(tabId: string): void {
  restored.tabs = restored.tabs.filter((tab) => tab.id !== tabId);
  if (restored.activeTabId === tabId) restored.activeTabId = restored.tabs.at(-1)?.id ?? null;
  try {
    closeBrowserTab(modelContext(), tabId);
    if (browser.workspace.activeTabId) {
      selectBrowserTab(modelContext(), browser.workspace.activeTabId);
    }
    syncLegacy();
  } catch (error) {
    browser.workspace.error = error instanceof Error ? error.message : String(error);
    syncLegacy();
  }
}

// Small explicit adapters used by the new browser components.  They keep the
// compatibility store as the one reactive source without adding a second model.
export function setBrowserPresentation(mode: BrowserPresentationMode): void {
  try {
    setBrowserPresentationMode(modelContext(), mode);
    syncLegacy();
  } catch (error) {
    browser.workspace.error = error instanceof Error ? error.message : String(error);
    syncLegacy();
  }
}

export function setBrowserViewportPreset(value: BrowserViewportPreset): void {
  try {
    setBrowserViewport(modelContext(), value);
    syncLegacy();
  } catch (error) {
    browser.workspace.error = error instanceof Error ? error.message : String(error);
  }
}

export function captureBrowserView(): ReturnType<typeof captureBrowserWorkspace> {
  return captureBrowserWorkspace(modelContext());
}

export async function captureBrowserMarkupView(
  options?: Omit<BrowserCaptureOptions, 'forMarkup'>
): Promise<BrowserMarkupCapture> {
  return await captureBrowserMarkup(modelContext(), options);
}

export function stageBrowserFeedback(options?: BrowserFeedbackStageOptions): BrowserFeedbackPreview {
  return stageBrowserFeedbackPreview(browserModelContext(options?.bridge), options);
}

export function browserModelContext(conversation?: BrowserConversationBridge): BrowserModelContext {
  return { ...modelContext(), conversation: conversation ?? conversationBridge };
}

export function syncBrowserTab(tabId: string): void {
  try {
    selectBrowserTab(modelContext(), tabId);
    syncLegacy();
  } catch (error) {
    browser.workspace.error = error instanceof Error ? error.message : String(error);
  }
}

export function syncBrowserNavigation(event: BrowserTabNavigationEvent): void {
  const tab = browser.workspace.tabs[event.tabId];
  if (
    !tab
    || event.workspaceId !== browser.workspace.workspaceId
    || event.generation !== tab.generation
  ) return;
  tab.url = event.url;
  tab.inputUrl = event.url;
  tab.title = event.title;
  tab.canGoBack = event.canGoBack;
  tab.canGoForward = event.canGoForward;
  tab.loadState = 'loaded';
  syncLegacy();
}
