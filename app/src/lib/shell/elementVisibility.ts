/**
 * Tells an element whether it is on screen, and stops its looping motion when
 * it is not.
 *
 * An animation that runs off screen costs exactly what an on-screen one costs.
 * The browser keeps recalculating the element's style and, for anything that
 * is not a plain transform or opacity, repainting it sixty times a second,
 * even though none of it reaches a person's eye. A handful of scrolled-away
 * spinners and placeholder bars is enough to hold a core busy while the app is
 * doing nothing at all, which is the bug this exists to prevent.
 *
 * One observer watches every element rather than one observer per element.
 */
import { setElementVisibilityDiagnostics } from './resourceDiagnostics.svelte.ts';

export type ElementVisibilityListener = (visible: boolean) => void;

const listeners = new Map<Element, ElementVisibilityListener>();
let observer: IntersectionObserver | null = null;

function handle(entries: IntersectionObserverEntry[]): void {
  for (const entry of entries) {
    listeners.get(entry.target)?.(entry.isIntersecting);
  }
}

function publishDiagnostics(): void {
  setElementVisibilityDiagnostics(listeners.size, observer !== null);
}

/**
 * Watch one element. The listener is called whenever it enters or leaves the
 * screen. Where the browser has no observer the element counts as visible,
 * which keeps a live indicator honest rather than silently frozen.
 */
export function observeElementVisibility(
  element: Element,
  listener: ElementVisibilityListener
): () => void {
  if (typeof IntersectionObserver === 'undefined') {
    listener(true);
    return () => {};
  }

  observer ??= new IntersectionObserver(handle);
  listeners.set(element, listener);
  observer.observe(element);
  publishDiagnostics();

  return () => {
    listeners.delete(element);
    observer?.unobserve(element);
    if (listeners.size === 0) {
      observer?.disconnect();
      observer = null;
    }
    publishDiagnostics();
  };
}

/** Whether any element is currently being watched. */
export function elementVisibilityWatching(): boolean {
  return listeners.size > 0;
}

/**
 * For an element whose only need is to stop animating off screen.
 *
 * It sets `--motion-state` on the element, which that element's own stylesheet
 * reads through `animation-play-state`. A custom property inherits, so putting
 * the action on a container also covers whatever it animates inside itself,
 * and a stylesheet that never reads the property is simply unaffected.
 */
export function animateWhenVisible(element: HTMLElement): { destroy(): void } {
  element.style.setProperty('--motion-state', 'running');
  const stop = observeElementVisibility(element, (visible) => {
    element.style.setProperty('--motion-state', visible ? 'running' : 'paused');
  });
  return { destroy: stop };
}
