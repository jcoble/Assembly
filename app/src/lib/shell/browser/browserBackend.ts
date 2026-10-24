import type {
  BrowserElementMetadata,
  BrowserFloatingBounds,
  BrowserMarkupCapture,
  BrowserRect,
  BrowserTabNavigationEvent,
  BrowserViewport
} from './browserTypes.ts';
import { invokeBrowserCommandFromTauri, isTauriRuntime } from '../../tauriSource.ts';
import { trackTauriListener, trackTauriSubscriber } from '../resourceDiagnostics.svelte.ts';

export type BrowserBackendResult<T> = T | Promise<T>;

export interface BrowserBackendTabInput {
  workspaceId: string;
  tabId: string;
  generation: number;
  url: string;
  bounds?: BrowserFloatingBounds;
  viewport?: BrowserViewport;
  profileId?: string | null;
}

export interface BrowserBackendTabResult {
  tabId: string;
  generation: number;
}

export interface BrowserBackendNavigationInput {
  workspaceId: string;
  tabId: string;
  generation: number;
  url: string;
}

export interface BrowserBackendTarget {
  workspaceId: string;
  tabId: string;
  generation: number;
}

export interface BrowserBackendBoundsInput extends BrowserBackendTarget {
  bounds: BrowserFloatingBounds;
}

export interface BrowserBackendViewportInput extends BrowserBackendTarget {
  viewport: BrowserViewport;
}

export interface BrowserBackendPickerInput extends BrowserBackendTarget {
  mode: 'grab' | 'annotation';
}

export interface BrowserBackendRectInput extends BrowserBackendTarget {
  rect: BrowserRect;
}

export interface BrowserBackend {
  create_browser_tab(input: BrowserBackendTabInput): BrowserBackendResult<BrowserBackendTabResult>;
  set_browser_tab_bounds(input: BrowserBackendBoundsInput): BrowserBackendResult<void>;
  set_browser_tab_viewport?(input: BrowserBackendViewportInput): BrowserBackendResult<void>;
  show_browser_tab(input: BrowserBackendTarget): BrowserBackendResult<void>;
  hide_browser_workspace(input: { workspaceId: string }): BrowserBackendResult<void>;
  navigate_browser_tab(input: BrowserBackendNavigationInput): BrowserBackendResult<void>;
  reload_browser_tab(input: BrowserBackendTarget): BrowserBackendResult<void>;
  go_back_browser_tab(input: BrowserBackendTarget): BrowserBackendResult<void>;
  go_forward_browser_tab(input: BrowserBackendTarget): BrowserBackendResult<void>;
  close_browser_tab(input: BrowserBackendTarget): BrowserBackendResult<void>;
  arm_browser_element_picker(input: BrowserBackendPickerInput): BrowserBackendResult<void>;
  cancel_browser_element_picker(input: BrowserBackendTarget): BrowserBackendResult<void>;
  inspect_browser_rect(input: BrowserBackendRectInput): BrowserBackendResult<BrowserElementMetadata | null>;
  capture_browser_viewport(input: BrowserBackendTarget): BrowserBackendResult<BrowserMarkupCapture>;
  open_browser_tab_devtools?(input: BrowserBackendTarget): BrowserBackendResult<void>;
  open_browser_tab_external?(input: BrowserBackendTarget): BrowserBackendResult<void>;
}

export interface BrowserBackendCall {
  command: string;
  input: unknown;
}

export type BrowserNavigationSubscriber = (event: BrowserTabNavigationEvent) => void;

const browserNavigationSubscribers = new Set<BrowserNavigationSubscriber>();
let browserNavigationUnlisten: (() => void) | null = null;
let browserNavigationSetup: Promise<void> | null = null;
let browserNavigationGeneration = 0;

export function subscribeToBrowserNavigation(
  subscriber: BrowserNavigationSubscriber
): () => void {
  browserNavigationSubscribers.add(subscriber);
  ensureBrowserNavigationListener();
  return trackTauriSubscriber(() => {
    browserNavigationSubscribers.delete(subscriber);
    if (browserNavigationSubscribers.size === 0) stopBrowserNavigationListener();
  });
}

function ensureBrowserNavigationListener(): void {
  if (!isTauriRuntime() || browserNavigationUnlisten || browserNavigationSetup) return;
  const generation = browserNavigationGeneration;
  const setup = setupBrowserNavigationListener(generation);
  browserNavigationSetup = setup;
  void clearBrowserNavigationSetup(setup);
}

async function setupBrowserNavigationListener(generation: number): Promise<void> {
  try {
    const { listen } = await import('@tauri-apps/api/event');
    const stopNative = await listen<BrowserTabNavigationEvent>('browser-tab-navigation', (event) => {
      for (const current of browserNavigationSubscribers) current(event.payload);
    });
    const stop = trackTauriListener(stopNative);
    if (generation !== browserNavigationGeneration || browserNavigationSubscribers.size === 0) {
      stop();
      return;
    }
    browserNavigationUnlisten = stop;
  } catch {
    // Browser command reads remain authoritative when this event is unavailable.
  }
}

async function clearBrowserNavigationSetup(setup: Promise<void>): Promise<void> {
  await setup;
  if (browserNavigationSetup === setup) browserNavigationSetup = null;
}

function stopBrowserNavigationListener(): void {
  browserNavigationGeneration += 1;
  browserNavigationUnlisten?.();
  browserNavigationUnlisten = null;
  browserNavigationSetup = null;
}

interface FakeTabRecord extends BrowserBackendTarget {
  url: string;
  visible: boolean;
  bounds: BrowserFloatingBounds | null;
  viewport: BrowserViewport | null;
  history: string[];
  historyIndex: number;
}

function copy<T>(value: T): T {
  if (value && typeof value === 'object') return structuredClone(value);
  return value;
}

/**
 * Deterministic backend used by model tests and browser preview builds.  It
 * records command order and keeps tab history, but never creates a native view
 * or touches the network.
 */
export class InMemoryBrowserBackend implements BrowserBackend {
  readonly calls: BrowserBackendCall[] = [];
  readonly tabs = new Map<string, FakeTabRecord>();
  readonly captures = new Map<string, BrowserMarkupCapture>();

  private record(command: string, input: unknown): void {
    this.calls.push({ command, input: copy(input) });
    // The browser preview keeps one of these for the life of the page, and
    // resizing fires `set_browser_tab_bounds` continuously, so the list is
    // capped. Tests read the first handful of commands; 200 is far more than
    // any of them sends.
    if (this.calls.length > 200) this.calls.shift();
  }

  create_browser_tab(input: BrowserBackendTabInput): BrowserBackendTabResult {
    this.record('create_browser_tab', input);
    const current = this.tabs.get(input.tabId);
    if (current) return { tabId: current.tabId, generation: current.generation };
    const record: FakeTabRecord = {
      workspaceId: input.workspaceId,
      tabId: input.tabId,
      generation: input.generation,
      url: input.url,
      visible: true,
      bounds: input.bounds ? { ...input.bounds } : null,
      viewport: input.viewport ? { ...input.viewport } : null,
      history: input.url ? [input.url] : [],
      historyIndex: input.url ? 0 : -1
    };
    this.tabs.set(input.tabId, record);
    return { tabId: input.tabId, generation: input.generation };
  }

  set_browser_tab_bounds(input: BrowserBackendBoundsInput): void {
    this.record('set_browser_tab_bounds', input);
    const tab = this.tabs.get(input.tabId);
    if (tab) tab.bounds = { ...input.bounds };
  }

  set_browser_tab_viewport(input: BrowserBackendViewportInput): void {
    this.record('set_browser_tab_viewport', input);
    const tab = this.tabs.get(input.tabId);
    if (tab) tab.viewport = { ...input.viewport };
  }

  show_browser_tab(input: BrowserBackendTarget): void {
    this.record('show_browser_tab', input);
    const tab = this.tabs.get(input.tabId);
    if (tab) tab.visible = true;
  }

  hide_browser_workspace(input: { workspaceId: string }): void {
    this.record('hide_browser_workspace', input);
    for (const tab of this.tabs.values()) {
      if (tab.workspaceId === input.workspaceId) tab.visible = false;
    }
  }

  navigate_browser_tab(input: BrowserBackendNavigationInput): void {
    this.record('navigate_browser_tab', input);
    const tab = this.tabs.get(input.tabId);
    if (!tab) return;
    tab.url = input.url;
    tab.history = tab.history.slice(0, tab.historyIndex + 1);
    tab.history.push(input.url);
    tab.historyIndex = tab.history.length - 1;
  }

  reload_browser_tab(input: BrowserBackendTarget): void {
    this.record('reload_browser_tab', input);
  }

  go_back_browser_tab(input: BrowserBackendTarget): void {
    this.record('go_back_browser_tab', input);
    const tab = this.tabs.get(input.tabId);
    if (tab && tab.historyIndex > 0) {
      tab.historyIndex -= 1;
      tab.url = tab.history[tab.historyIndex];
    }
  }

  go_forward_browser_tab(input: BrowserBackendTarget): void {
    this.record('go_forward_browser_tab', input);
    const tab = this.tabs.get(input.tabId);
    if (tab && tab.historyIndex < tab.history.length - 1) {
      tab.historyIndex += 1;
      tab.url = tab.history[tab.historyIndex];
    }
  }

  close_browser_tab(input: BrowserBackendTarget): void {
    this.record('close_browser_tab', input);
    this.tabs.delete(input.tabId);
  }

  arm_browser_element_picker(input: BrowserBackendPickerInput): void {
    this.record('arm_browser_element_picker', input);
  }

  cancel_browser_element_picker(input: BrowserBackendTarget): void {
    this.record('cancel_browser_element_picker', input);
  }

  /**
   * A page without a page. The answer is the cell of a coarse grid the asked-for
   * rectangle starts in, so the same spot always describes the same "element"
   * and the preview build can be marked up the way the desktop one is.
   */
  inspect_browser_rect(input: BrowserBackendRectInput): BrowserElementMetadata {
    this.record('inspect_browser_rect', input);
    const CELL_WIDTH = 180;
    const CELL_HEIGHT = 44;
    const column = Math.floor(input.rect.x / CELL_WIDTH);
    const row = Math.floor(input.rect.y / CELL_HEIGHT);
    const kinds = ['button', 'combobox', 'link', 'heading'] as const;
    const kind = kinds[Math.abs(column + row) % kinds.length];
    return {
      selector: `main > section:nth-of-type(${row + 1}) > div.cell:nth-of-type(${column + 1})`,
      role: kind,
      accessibleName: `Preview ${kind} ${row + 1}.${column + 1}`,
      textSnippet: `Row ${row + 1}, column ${column + 1}`,
      rect: {
        x: column * CELL_WIDTH,
        y: row * CELL_HEIGHT,
        width: CELL_WIDTH,
        height: CELL_HEIGHT
      },
      classes: ['cell', kind],
      classCount: 2
    };
  }

  capture_browser_viewport(input: BrowserBackendTarget): BrowserMarkupCapture {
    this.record('capture_browser_viewport', input);
    const existing = this.captures.get(input.tabId);
    if (existing) return copy(existing);
    const capture: BrowserMarkupCapture = {
      mimeType: 'image/png',
      // A real, if very small, picture: the preview build draws it, and an
      // image that cannot be decoded is a marking surface with nothing on it.
      bytes: [
        137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 2, 0, 0, 0, 2, 8, 2,
        0, 0, 0, 253, 212, 154, 115, 0, 0, 0, 14, 73, 68, 65, 84, 120, 156, 99, 248, 5, 6, 12, 16,
        10, 0, 82, 22, 11, 185, 152, 58, 126, 116, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130
      ],
      width: 2,
      height: 2,
      sourceHash: `fake-capture:${input.tabId}:${input.generation}`
    };
    this.captures.set(input.tabId, capture);
    return copy(capture);
  }

  open_browser_tab_devtools(input: BrowserBackendTarget): void {
    this.record('open_browser_tab_devtools', input);
  }

  open_browser_tab_external(input: BrowserBackendTarget): void {
    this.record('open_browser_tab_external', input);
  }
}

/**
 * Native adapter for the browser command surface registered by the Tauri
 * shell. The same command names are deliberately kept in one place here so
 * the model remains usable with the deterministic in-memory backend in web
 * preview and Node tests.
 */
export class TauriBrowserBackend implements BrowserBackend {
  /**
   * Tauri commands cross an async boundary, but the browser model deliberately
   * updates its local state immediately. Preserve the order in which the model
   * issued native commands so create cannot be overtaken by navigate/bounds,
   * and a reopened page cannot race the previous view's hide/close teardown.
   */
  private commandTail: Promise<void> = Promise.resolve();

  private async runAfter<T>(previous: Promise<void>, command: () => Promise<T>): Promise<T> {
    try {
      await previous;
    } catch {
      // A failed native command must not block the commands queued behind it.
    }
    return await command();
  }

  private async settle<T>(result: Promise<T>): Promise<void> {
    try {
      await result;
    } catch {
      // The caller receives the failure; the queue only tracks completion.
    }
  }

  private async enqueue<T>(command: () => Promise<T>): Promise<T> {
    const result = this.runAfter(this.commandTail, command);
    this.commandTail = this.settle(result);
    return await result;
  }

  async create_browser_tab(input: BrowserBackendTabInput): Promise<BrowserBackendTabResult> {
    return await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<BrowserBackendTabResult>('create_browser_tab', { input })
    );
  }

  async set_browser_tab_bounds(input: BrowserBackendBoundsInput): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('set_browser_tab_bounds', { input })
    );
  }

  async set_browser_tab_viewport(input: BrowserBackendViewportInput): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('set_browser_tab_viewport', { input })
    );
  }

  async show_browser_tab(input: BrowserBackendTarget): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('show_browser_tab', { input })
    );
  }

  async hide_browser_workspace(input: { workspaceId: string }): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('hide_browser_workspace', { input })
    );
  }

  async navigate_browser_tab(input: BrowserBackendNavigationInput): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('navigate_browser_tab', { input })
    );
  }

  async reload_browser_tab(input: BrowserBackendTarget): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('reload_browser_tab', { input })
    );
  }

  async go_back_browser_tab(input: BrowserBackendTarget): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('go_back_browser_tab', { input })
    );
  }

  async go_forward_browser_tab(input: BrowserBackendTarget): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('go_forward_browser_tab', { input })
    );
  }

  async close_browser_tab(input: BrowserBackendTarget): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('close_browser_tab', { input })
    );
  }

  async arm_browser_element_picker(input: BrowserBackendPickerInput): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('arm_browser_element_picker', { input })
    );
  }

  async cancel_browser_element_picker(input: BrowserBackendTarget): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('cancel_browser_element_picker', { input })
    );
  }

  async inspect_browser_rect(input: BrowserBackendRectInput): Promise<BrowserElementMetadata | null> {
    return await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<BrowserElementMetadata | null>('inspect_browser_rect', { input })
    );
  }

  async capture_browser_viewport(input: BrowserBackendTarget): Promise<BrowserMarkupCapture> {
    return await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<BrowserMarkupCapture>('capture_browser_viewport', { input })
    );
  }

  async open_browser_tab_devtools(input: BrowserBackendTarget): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('open_browser_tab_devtools', { input })
    );
  }

  async open_browser_tab_external(input: BrowserBackendTarget): Promise<void> {
    await this.enqueue(async () =>
      await invokeBrowserCommandFromTauri<void>('open_browser_tab_external', { input })
    );
  }
}

export const FakeBrowserBackend = InMemoryBrowserBackend;

export function createInMemoryBrowserBackend(): InMemoryBrowserBackend {
  return new InMemoryBrowserBackend();
}

/** Select the native command adapter only inside the rebuilt Tauri desktop. */
export function createBrowserBackend(): BrowserBackend {
  return isTauriRuntime() ? new TauriBrowserBackend() : createInMemoryBrowserBackend();
}

/** Alias kept obvious for test authors and future browser preview callers. */
export const createFakeBrowserBackend = createInMemoryBrowserBackend;
