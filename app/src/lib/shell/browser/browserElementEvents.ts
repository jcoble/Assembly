/**
 * browserElementEvents.ts — receiving the element the person picked.
 *
 * Arming the picker is a command and returns nothing; the pick itself comes
 * back later on an event, because the page has to be clicked first. Nothing in
 * the app was listening for that event, so `acceptBrowserElementSelection` in
 * the model had no caller and Select could never finish. This is the missing
 * half.
 *
 * The event name and payload are the shell's (`src-tauri/src/browser/mod.rs`:
 * `BROWSER_ELEMENT_SELECTED_EVENT`, `BrowserElementSelectedEvent`). Outside the
 * desktop build there is no event source, so subscribing is a no-op that
 * answers with a no-op unsubscribe — a caller never has to ask which build it
 * is in.
 */
import { isTauriRuntime } from '../../tauriSource.ts';
import {
  trackTauriListener,
  trackTauriSubscriber
} from '../resourceDiagnostics.svelte.ts';
import type { BrowserRect } from './browserTypes.ts';

export const BROWSER_ELEMENT_SELECTED_EVENT = 'browser-element-selected';

/** `selected` carries an element; `unavailable` says the page would not give one. */
export type BrowserPickerStatus = 'selected' | 'unavailable';

export interface BrowserElementSelectedEvent {
  workspaceId: string;
  tabId: string;
  generation: number;
  status: BrowserPickerStatus;
  reason: string | null;
  url: string | null;
  title: string | null;
  selector: string | null;
  accessibleName: string | null;
  textSnippet: string | null;
  rect: BrowserRect | null;
  classes: string[];
  classCount: number;
  sourceHash: string;
}

export type UnsubscribeFromBrowserElements = () => void;

const browserElementSubscribers = new Set<
  (event: BrowserElementSelectedEvent) => void
>();
let browserElementUnlisten: (() => void) | null = null;
let browserElementSetup: Promise<void> | null = null;
let browserElementGeneration = 0;

/**
 * Subscribe to picked elements. The returned promise resolves once the
 * subscription is live; call what it answers with to stop listening.
 */
export async function listenToBrowserElementSelected(
  handler: (event: BrowserElementSelectedEvent) => void
): Promise<UnsubscribeFromBrowserElements> {
  if (!isTauriRuntime()) return () => undefined;
  browserElementSubscribers.add(handler);
  await ensureBrowserElementListener();
  return trackTauriSubscriber(() => {
    browserElementSubscribers.delete(handler);
    if (browserElementSubscribers.size === 0) stopBrowserElementListener();
  });
}

async function ensureBrowserElementListener(): Promise<void> {
  if (!isTauriRuntime() || browserElementUnlisten) return;
  if (browserElementSetup) {
    await browserElementSetup;
    return;
  }

  const generation = browserElementGeneration;
  const setup = setupBrowserElementListener(generation);
  browserElementSetup = setup;
  void clearBrowserElementSetup(setup);
  await setup;
}

async function setupBrowserElementListener(generation: number): Promise<void> {
  try {
    const { listen } = await import('@tauri-apps/api/event');
    const stopNative = await listen<BrowserElementSelectedEvent>(
      BROWSER_ELEMENT_SELECTED_EVENT,
      (event) => {
        for (const subscriber of browserElementSubscribers) subscriber(event.payload);
      }
    );
    const stop = trackTauriListener(stopNative);
    if (generation !== browserElementGeneration || browserElementSubscribers.size === 0) {
      stop();
      return;
    }
    browserElementUnlisten = stop;
  } catch {
    // Older controllers have no picker event; callers still get a no-op path.
  }
}

async function clearBrowserElementSetup(setup: Promise<void>): Promise<void> {
  await setup;
  if (browserElementSetup === setup) browserElementSetup = null;
}

function stopBrowserElementListener(): void {
  browserElementGeneration += 1;
  browserElementUnlisten?.();
  browserElementUnlisten = null;
  browserElementSetup = null;
}
