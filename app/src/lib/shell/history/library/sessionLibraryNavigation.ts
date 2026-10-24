/**
 * Small bridge between the left-rail entry point and the center tab.
 *
 * The left rail and the center dock are siblings owned by the page, so passing
 * a callback through the frame would widen the frame contract. The one mounted
 * Session History workspace registers the page's existing tab-activation
 * callback here; the left entry point can then request it without opening a
 * second finder or popover.
 */
type OpenHandler = () => void;

let openHandler: OpenHandler | null = null;

export function registerSessionLibraryOpenHandler(handler: OpenHandler): () => void {
  openHandler = handler;
  return () => {
    if (openHandler === handler) openHandler = null;
  };
}

export function openSessionLibrary(): void {
  openHandler?.();
}
