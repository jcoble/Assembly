import type { BrowserPresentationMode } from './browserTypes.ts';

export interface BrowserPresentationTransition {
  presentation: BrowserPresentationMode;
  previousPresentation: BrowserPresentationMode | null;
}

/**
 * Resolve a mode change without touching tabs.  `previousPresentation` is the
 * explicit restore target for the floating/maximized and collapsed controls;
 * entering the dock is a committed restore and therefore clears it.
 */
export function transitionBrowserPresentation(
  current: BrowserPresentationMode,
  previous: BrowserPresentationMode | null,
  next: BrowserPresentationMode
): BrowserPresentationTransition {
  if (next === current) return { presentation: current, previousPresentation: previous };
  if (next === 'docked') return { presentation: 'docked', previousPresentation: null };

  if (next === 'collapsed') {
    return {
      presentation: 'collapsed',
      previousPresentation: current === 'collapsed' ? previous : current
    };
  }

  if (current === 'collapsed') {
    return {
      presentation: next,
      previousPresentation: previous && previous !== 'collapsed' ? previous : 'docked'
    };
  }

  return {
    presentation: next,
    previousPresentation: next === 'floating' || next === 'maximized' ? current : previous
  };
}

export function restorePresentationTarget(
  current: BrowserPresentationMode,
  previous: BrowserPresentationMode | null
): BrowserPresentationMode {
  if (previous && previous !== current && previous !== 'maximized') return previous;
  if (current === 'collapsed') return 'docked';
  return 'docked';
}

export function isExpandedBrowserMode(mode: BrowserPresentationMode): boolean {
  return mode === 'floating' || mode === 'maximized';
}

export function isBrowserOverlayMode(mode: BrowserPresentationMode): boolean {
  return mode === 'floating' || mode === 'maximized' || mode === 'collapsed';
}
