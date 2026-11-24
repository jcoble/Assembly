/**
 * Bridges SvelteKit's transparent `#native-chat-slot` and floating Glass overlays
 * to Assembly's GPUIKit `PassthroughSurface` via `window.ipc.postMessage(...)`.
 */

import type { SlotRect } from '$lib/domain/types';

declare global {
  interface Window {
    ipc?: {
      postMessage: (message: string) => void;
    };
  }
}

const OCCLUDER_SELECTOR = [
  '[data-occludes-chat]',
  '[role="dialog"]',
  '[role="alertdialog"]',
  '[role="menu"]',
  '[role="listbox"]',
  '[data-bits-floating-content-wrapper]',
  '[data-popover-content]'
].join(',');

const DRAG_REGION_SELECTOR = '[data-window-drag-region]';

function toSlotRect(rect: DOMRect): SlotRect | null {
  if (rect.width <= 0 || rect.height <= 0) return null;
  return {
    x: Math.round(rect.left * 100) / 100,
    y: Math.round(rect.top * 100) / 100,
    width: Math.round(rect.width * 100) / 100,
    height: Math.round(rect.height * 100) / 100
  };
}

function collectRects(selector: string): SlotRect[] {
  const results: SlotRect[] = [];
  const nodes = document.querySelectorAll<HTMLElement>(selector);
  for (const el of nodes) {
    const style = getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden' || style.pointerEvents === 'none') {
      continue;
    }
    const rect = toSlotRect(el.getBoundingClientRect());
    if (rect) results.push(rect);
  }
  return results;
}

export function postHybridIpc(payload: Record<string, unknown>): void {
  if (typeof window === 'undefined' || !window.ipc?.postMessage) return;
  window.ipc.postMessage(JSON.stringify(payload));
}

export function selectNativeChatSession(id: string, machine?: string | null): void {
  postHybridIpc({
    t: 'select_session',
    id,
    machine: machine ?? null
  });
}

export function openNativeNewSession(): void {
  postHybridIpc({ t: 'new_session' });
}

export interface NativeSlotBridge {
  setChatSlotElement: (el: HTMLElement | null) => void;
  syncNow: () => void;
  dispose: () => void;
}

export function createNativeSlotBridge(): NativeSlotBridge {
  let slotEl: HTMLElement | null = null;
  let lastPayload = '';
  let rafId: number | null = null;
  let resizeObserver: ResizeObserver | null = null;
  let mutationObserver: MutationObserver | null = null;

  function emit(): void {
    const hole = slotEl ? toSlotRect(slotEl.getBoundingClientRect()) : null;
    const occluders = collectRects(OCCLUDER_SELECTOR);
    const drag_rects = collectRects(DRAG_REGION_SELECTOR);
    const payload = JSON.stringify({ t: 'slot', hole, occluders, drag_rects });
    if (payload === lastPayload) return;
    lastPayload = payload;
    if (typeof window !== 'undefined' && window.ipc?.postMessage) {
      window.ipc.postMessage(payload);
    }
  }

  function scheduleEmit(): void {
    if (rafId !== null) return;
    rafId = requestAnimationFrame(() => {
      rafId = null;
      emit();
    });
  }

  const onWindowResize = () => scheduleEmit();

  if (typeof window !== 'undefined') {
    window.addEventListener('resize', onWindowResize);
    mutationObserver = new MutationObserver(() => scheduleEmit());
    mutationObserver.observe(document.body, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ['style', 'class', 'hidden', 'data-state', 'data-occludes-chat', 'open']
    });
  }

  return {
    setChatSlotElement(el: HTMLElement | null) {
      if (slotEl === el) return;
      resizeObserver?.disconnect();
      resizeObserver = null;
      slotEl = el;
      if (el && typeof ResizeObserver !== 'undefined') {
        resizeObserver = new ResizeObserver(() => scheduleEmit());
        resizeObserver.observe(el);
      }
      emit();
    },
    syncNow() {
      emit();
    },
    dispose() {
      if (rafId !== null) {
        cancelAnimationFrame(rafId);
        rafId = null;
      }
      resizeObserver?.disconnect();
      mutationObserver?.disconnect();
      if (typeof window !== 'undefined') {
        window.removeEventListener('resize', onWindowResize);
      }
      postHybridIpc({ t: 'slot', hole: null, occluders: [], drag_rects: [] });
    }
  };
}
