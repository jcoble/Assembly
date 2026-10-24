import type {
  BrowserFloatingBounds,
  BrowserViewport,
  BrowserViewportPreset
} from './browserTypes.ts';

export interface BrowserWindowSize {
  width: number;
  height: number;
}

export const BROWSER_VIEWPORT_PRESETS: Readonly<
  Record<BrowserViewportPreset, BrowserViewport>
> = {
  responsive: { preset: 'responsive', width: null, height: null },
  'mobile-s': { preset: 'mobile-s', width: 320, height: 568 },
  'mobile-m': { preset: 'mobile-m', width: 375, height: 667 },
  'mobile-l': { preset: 'mobile-l', width: 425, height: 812 },
  tablet: { preset: 'tablet', width: 768, height: 1024 },
  laptop: { preset: 'laptop', width: 1024, height: 768 },
  'laptop-l': { preset: 'laptop-l', width: 1440, height: 900 },
  desktop: { preset: 'desktop', width: 1920, height: 1080 },
  custom: { preset: 'custom', width: null, height: null }
};

export const BROWSER_VIEWPORTS = BROWSER_VIEWPORT_PRESETS;

export const BROWSER_MIN_FLOATING_WIDTH = 360;
export const BROWSER_MIN_FLOATING_HEIGHT = 240;
export const BROWSER_FLOATING_EDGE_GAP = 24;

function finite(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function positive(value: unknown, fallback: number): number {
  const candidate = finite(value, fallback);
  return candidate > 0 ? candidate : fallback;
}

function integer(value: unknown, fallback: number): number {
  return Math.round(positive(value, fallback));
}

export function browserViewportPreset(preset: BrowserViewportPreset): BrowserViewport {
  const value = BROWSER_VIEWPORT_PRESETS[preset] ?? BROWSER_VIEWPORT_PRESETS.responsive;
  return { ...value };
}

export function resolveBrowserViewport(input: {
  preset?: BrowserViewportPreset | string;
  width?: number | null;
  height?: number | null;
} | BrowserViewportPreset): BrowserViewport {
  if (typeof input === 'string') {
    return browserViewportPreset(isViewportPreset(input) ? input : 'responsive');
  }
  const preset = isViewportPreset(input.preset) ? input.preset : 'custom';
  if (preset !== 'custom') return browserViewportPreset(preset);
  const width = input.width == null ? null : integer(input.width, 800);
  const height = input.height == null ? null : integer(input.height, 600);
  return {
    preset: 'custom',
    width: width == null ? null : Math.min(4096, Math.max(200, width)),
    height: height == null ? null : Math.min(4096, Math.max(160, height))
  };
}

export function isViewportPreset(value: unknown): value is BrowserViewportPreset {
  return (
    value === 'responsive' ||
    value === 'mobile-s' ||
    value === 'mobile-m' ||
    value === 'mobile-l' ||
    value === 'tablet' ||
    value === 'laptop' ||
    value === 'laptop-l' ||
    value === 'desktop' ||
    value === 'custom'
  );
}

/**
 * Keep a floating browser reachable after a resize.  The window may be smaller
 * than the preferred composition (for example in a compact preview), so the
 * requested size is reduced before its origin is clamped.
 */
export function clampBrowserFloatingBounds(
  bounds: Partial<BrowserFloatingBounds> | null | undefined,
  window: BrowserWindowSize,
  options: { minWidth?: number; minHeight?: number; edgeGap?: number } = {}
): BrowserFloatingBounds {
  const viewportWidth = Math.max(1, Math.floor(finite(window.width, 1)));
  const viewportHeight = Math.max(1, Math.floor(finite(window.height, 1)));
  const gap = Math.max(0, Math.floor(finite(options.edgeGap, BROWSER_FLOATING_EDGE_GAP)));
  const minWidth = Math.max(1, Math.floor(finite(options.minWidth, BROWSER_MIN_FLOATING_WIDTH)));
  const minHeight = Math.max(1, Math.floor(finite(options.minHeight, BROWSER_MIN_FLOATING_HEIGHT)));
  const widthFloor = Math.min(minWidth, viewportWidth);
  const heightFloor = Math.min(minHeight, viewportHeight);
  const width = Math.min(viewportWidth, Math.max(widthFloor, integer(bounds?.width, 720)));
  const height = Math.min(viewportHeight, Math.max(heightFloor, integer(bounds?.height, 520)));
  const xMin = 0;
  const yMin = 0;
  const maxX = Math.max(xMin, viewportWidth - width);
  const maxY = Math.max(yMin, viewportHeight - height);
  const x = Math.min(maxX, Math.max(xMin, Math.round(finite(bounds?.x, gap))));
  const y = Math.min(maxY, Math.max(yMin, Math.round(finite(bounds?.y, gap))));
  return { x, y, width, height };
}

export const clampFloatingBounds = clampBrowserFloatingBounds;

export function browserBoundsEqual(a: BrowserFloatingBounds, b: BrowserFloatingBounds): boolean {
  return a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height;
}
