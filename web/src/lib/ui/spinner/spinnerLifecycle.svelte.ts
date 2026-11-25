/**
 * Shared visibility & viewport lifecycle for `WorkingSpinner`.
 *
 * Keeps at most ONE `visibilitychange` listener and ONE `IntersectionObserver`
 * for the entire app, tearing both down automatically when the last spinner
 * unmounts so an idle app costs zero listeners and zero CPU.
 */

let mountedCount = 0;
let pageHidden = $state(typeof document !== 'undefined' ? document.hidden : false);
let observer: IntersectionObserver | null = null;
const visibilityCallbacks = new WeakMap<Element, (visible: boolean) => void>();

function onVisibilityChange(): void {
  pageHidden = typeof document !== 'undefined' ? document.hidden : false;
}

function acquireGlobals(): void {
  mountedCount += 1;
  if (mountedCount === 1 && typeof document !== 'undefined') {
    pageHidden = document.hidden;
    document.addEventListener('visibilitychange', onVisibilityChange);
    if (typeof IntersectionObserver !== 'undefined') {
      observer = new IntersectionObserver(
        (entries) => {
          for (const entry of entries) {
            visibilityCallbacks.get(entry.target)?.(entry.isIntersecting);
          }
        },
        { threshold: 0 }
      );
    }
  }
}

function releaseGlobals(): void {
  mountedCount = Math.max(0, mountedCount - 1);
  if (mountedCount === 0) {
    if (typeof document !== 'undefined') {
      document.removeEventListener('visibilitychange', onVisibilityChange);
    }
    observer?.disconnect();
    observer = null;
  }
}

export function isPageHidden(): boolean {
  return pageHidden;
}

/**
 * Registers a spinner element with the shared visibility + viewport observer.
 * Returns a cleanup callback that unregisters the element and tears down shared
 * listeners when no spinners remain mounted.
 */
export function observeSpinnerElement(
  element: Element,
  onIntersecting: (visible: boolean) => void
): () => void {
  acquireGlobals();
  visibilityCallbacks.set(element, onIntersecting);
  if (observer) {
    observer.observe(element);
  } else {
    onIntersecting(true);
  }

  let released = false;
  return () => {
    if (released) return;
    released = true;
    observer?.unobserve(element);
    visibilityCallbacks.delete(element);
    releaseGlobals();
  };
}
