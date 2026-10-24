import type { BrowserViewportPreset } from './browserTypes.ts';
import { BROWSER_VIEWPORT_PRESETS } from './browserBounds.ts';

export type BrowserToolbarActionId =
  | 'import'
  | 'grab'
  | 'annotate'
  | 'draw'
  | 'devtools'
  | 'external'
  | 'overflow';

export interface BrowserToolbarActionDefinition {
  readonly id: BrowserToolbarActionId;
  readonly label: string;
  readonly iconOnly: boolean;
}

/** The compact, stable action order used by both the browser dock and overlay. */
export const BROWSER_TOOLBAR_ACTIONS: readonly BrowserToolbarActionDefinition[] = [
  { id: 'import', label: 'Import', iconOnly: false },
  { id: 'grab', label: 'Pick element', iconOnly: true },
  { id: 'annotate', label: 'Annotate or comment', iconOnly: true },
  { id: 'draw', label: 'Draw screenshot', iconOnly: true },
  { id: 'devtools', label: 'Open developer tools', iconOnly: true },
  { id: 'external', label: 'Open in external browser', iconOnly: true },
  { id: 'overflow', label: 'More browser actions', iconOnly: true }
];

export type BrowserOverflowEntryId =
  | 'profile-default'
  | 'profile-new'
  | 'import-cookies'
  | 'viewport-size'
  | 'browser-settings';

export interface BrowserOverflowEntryDefinition {
  readonly id: BrowserOverflowEntryId;
  readonly label: string;
  readonly section: 'profiles' | 'browser';
}

/** Menu entries stay data-only so the UI cannot silently lose a required item. */
export const BROWSER_OVERFLOW_ENTRIES: readonly BrowserOverflowEntryDefinition[] = [
  { id: 'profile-default', label: 'Default', section: 'profiles' },
  { id: 'profile-new', label: 'New Profile…', section: 'profiles' },
  { id: 'import-cookies', label: 'Import Cookies', section: 'browser' },
  { id: 'viewport-size', label: 'Viewport Size', section: 'browser' },
  { id: 'browser-settings', label: 'Browser Settings…', section: 'browser' }
];

export interface BrowserViewportMenuEntry {
  readonly preset: BrowserViewportPreset;
  readonly label: string;
  readonly dimensions: string;
}

const VIEWPORT_LABELS: Readonly<Record<BrowserViewportPreset, string>> = {
  responsive: 'Responsive',
  'mobile-s': 'Mobile S',
  'mobile-m': 'Mobile M',
  'mobile-l': 'Mobile L',
  tablet: 'Tablet',
  laptop: 'Laptop',
  'laptop-l': 'Laptop L',
  desktop: 'Desktop',
  custom: 'Custom'
};

function dimensionsFor(preset: BrowserViewportPreset): string {
  const viewport = BROWSER_VIEWPORT_PRESETS[preset];
  return viewport.width === null || viewport.height === null
    ? 'full'
    : `${viewport.width} × ${viewport.height}`;
}

export const BROWSER_VIEWPORT_MENU_ENTRIES: readonly BrowserViewportMenuEntry[] = (
  [
    'responsive',
    'mobile-s',
    'mobile-m',
    'mobile-l',
    'tablet',
    'laptop',
    'laptop-l',
    'desktop'
  ] as BrowserViewportPreset[]
).map((preset) => ({
  preset,
  label: VIEWPORT_LABELS[preset],
  dimensions: dimensionsFor(preset)
}));

export function browserToolbarActionIds(): BrowserToolbarActionId[] {
  return BROWSER_TOOLBAR_ACTIONS.map(({ id }) => id);
}

export function browserOverflowEntryIds(): BrowserOverflowEntryId[] {
  return BROWSER_OVERFLOW_ENTRIES.map(({ id }) => id);
}
