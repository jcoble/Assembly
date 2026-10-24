/**
 * themeRegistry.ts — the app's themes, each one complete.
 *
 * A theme here is all three of the things that have to change together for the
 * app to actually look different:
 *
 *   tokens    the `--color-…` custom properties every panel, dialog, button and
 *             dock divider is painted from (the same names `nextTokens.css`
 *             sets, plus a few extra the dock needs and the shared token file
 *             has no word for).
 *   monaco    the code editor's own theme object. Monaco does not read CSS
 *             custom properties; it wants a theme registered by name.
 *   terminal  Ghostty's authoritative palette (in `terminalPalettes.ts`),
 *             applied to the terminal instance.
 *
 * Assembly is the default: its colors are copied value for value out of
 * `nextTokens.css` and `themeChrome.css`, which paint the first frame before
 * any theme is applied, so picking it can never change a pixel. Houston,
 * Dracula, Tokyo Night and Graphite set the same names (TSK-1389's theme set).
 * The additional CodeMirror catalog entries keep the Assembly shell palette
 * and swap only the visible editor's syntax extension.
 *
 * The copying is the risk. `scripts/themeRegistry.test.ts` reads the original
 * files and fails if the default theme stops agreeing with them, so the
 * shipped palette cannot drift apart quietly.
 *
 * This module is deliberately plain data; its only import is the terminal
 * palettes beside it: no DOM, no store, no Svelte runes. Applying a theme is
 * `themeService.ts`'s job.
 */

import {
  ASSEMBLY_TERMINAL,
  DRACULA_TERMINAL,
  GRAPHITE_TERMINAL,
  HOUSTON_TERMINAL,
  TOKYO_NIGHT_TERMINAL
} from './terminalPalettes.ts';

// ── Shapes ──────────────────────────────────────────────────────────────────

/** One syntax-colouring rule in a Monaco theme. Colors are bare hex, no `#`. */
export type MonacoTokenRule = {
  token: string;
  foreground?: string;
  background?: string;
  fontStyle?: string;
};

/**
 * A Monaco theme, shaped exactly like the object
 * `src/lib/sourcePreviewAppearance.ts` already hands to `defineTheme`.
 */
export type MonacoThemeDefinition = {
  id: string;
  base: 'vs' | 'vs-dark' | 'hc-black' | 'hc-light';
  inherit: boolean;
  rules: MonacoTokenRule[];
  encodedTokensColors?: string[];
  colors: Record<string, string>;
};

/**
 * Ghostty's theme, written out by hand rather than imported from `ghostty-web`
 * so this file stays free of dependencies and can be read by a plain Node test.
 * The slots are the ones the terminal factory already sets.
 */
export type TerminalTheme = {
  background: string;
  foreground: string;
  cursor: string;
  cursorAccent: string;
  selectionBackground: string;
  selectionForeground: string;
  black: string;
  red: string;
  green: string;
  yellow: string;
  blue: string;
  magenta: string;
  cyan: string;
  white: string;
  brightBlack: string;
  brightRed: string;
  brightGreen: string;
  brightYellow: string;
  brightBlue: string;
  brightMagenta: string;
  brightCyan: string;
  brightWhite: string;
};

/** The custom property names a theme sets, mapped to their colors. */
export type ThemeTokens = Record<string, string>;

/** One complete theme. */
export type ShellTheme = {
  id: string;
  /** What the chooser calls it. */
  label: string;
  /** One line under the name, in plain words. */
  description: string;
  tokens: ThemeTokens;
  monaco: MonacoThemeDefinition;
  terminal: TerminalTheme;
};

// ── The names every theme must set ──────────────────────────────────────────

/**
 * The palette `nextTokens.css` defines. Every one of these names is read by the
 * shared components, so a theme that leaves one out would keep whatever colour
 * the previous theme painted there.
 */
export const PALETTE_TOKEN_NAMES = [
  '--color-bg',
  '--color-surface',
  '--color-elevated',
  '--color-rail',
  '--color-overlay',
  '--color-text',
  '--color-text-2',
  '--color-text-3',
  '--color-disabled-text',
  '--color-accent',
  '--color-border',
  '--color-field-border',
  '--color-focus',
  '--color-focus-solid',
  '--color-selected',
  '--color-selected-border',
  '--color-hover',
  '--color-live',
  '--color-good',
  '--color-bad',
  '--color-attention',
  '--color-idle',
  '--color-status-idle',
  '--color-merged',
  '--color-on-accent',
  '--color-live-bg',
  '--color-good-bg',
  '--color-bad-bg',
  '--color-bad-bg-strong',
  '--color-attention-bg',
  '--color-scrim',
  '--color-text-selection'
] as const;

/**
 * Four colors the dock needs that the shared palette has no name for: the
 * sidebar's section-header text, the ring dockview draws around the section you
 * are working in, and the two tones a visible tab takes when its group is not
 * the focused one. Their default (Assembly) values live in
 * `src/lib/shell/styles/themeChrome.css` so the dock still looks right on the
 * very first frame, before any theme has been applied.
 */
export const CHROME_TOKEN_NAMES = [
  '--color-section-header-text',
  '--color-section-focus-ring',
  '--color-tab-unfocused-surface',
  '--color-tab-unfocused-text'
] as const;

/** Every custom property name a theme is responsible for. */
export const TOKEN_NAMES: string[] = [...PALETTE_TOKEN_NAMES, ...CHROME_TOKEN_NAMES];

// ── Houston ─────────────────────────────────────────────────────────────────

const HOUSTON: ShellTheme = {
  id: 'houston',
  label: 'Houston',
  description: 'Astro’s Houston palette: cool greys and mint.',
  tokens: {
    // TSK-1389 (Fable C) Houston: Astro's Houston palette on the shared ladder.
    '--color-bg': '#101216',
    '--color-surface': '#17191e',
    '--color-elevated': '#1e2127',
    '--color-rail': '#17191e',
    '--color-overlay': '#2a2d34',
    '--color-text': '#eef0f9',
    '--color-text-2': '#a0a6b4',
    '--color-text-3': '#6c707b',
    '--color-disabled-text': '#6c707b',
    '--color-accent': '#4bf3c8',
    '--color-border': 'rgba(238, 240, 249, 0.08)',
    '--color-field-border': 'rgba(238, 240, 249, 0.11)',
    '--color-focus': 'rgba(75, 243, 200, 0.45)',
    '--color-focus-solid': '#4bf3c8',
    '--color-selected': '#2f333c',
    '--color-selected-border': '#4bf3c8',
    '--color-hover': '#23262d',
    '--color-live': '#54b9ff',
    '--color-good': '#4bf3c8',
    '--color-bad': '#ff6d91',
    '--color-attention': '#ffd493',
    '--color-idle': '#a0a6b4',
    '--color-status-idle': '#a0a6b4',
    '--color-merged': '#a371f7',
    '--color-on-accent': '#0e1013',
    '--color-live-bg': 'rgba(84, 185, 255, 0.12)',
    '--color-good-bg': 'rgba(75, 243, 200, 0.12)',
    '--color-bad-bg': 'rgba(255, 109, 145, 0.12)',
    '--color-bad-bg-strong': 'rgba(255, 109, 145, 0.2)',
    '--color-attention-bg': 'rgba(255, 212, 147, 0.12)',
    '--color-scrim': 'rgba(0, 0, 0, 0.6)',
    '--color-text-selection': 'rgba(84, 185, 255, 0.5)',
    '--color-section-header-text': '#a0a6b4',
    '--color-section-focus-ring': '#4bf3c8',
    '--color-tab-unfocused-surface': '#17191e',
    '--color-tab-unfocused-text': '#a0a6b4'
  },
  monaco: {
    // Copied from src/lib/sourcePreviewAppearance.ts.
    id: 'houston',
    base: 'vs-dark',
    inherit: true,
    rules: [
      { token: 'comment', foreground: '8f9199', fontStyle: 'italic' },
      { token: 'comment.doc', foreground: 'a3a6af', fontStyle: 'italic' },
      { token: 'constant', foreground: '54b9ff' },
      { token: 'class', foreground: '00daef' },
      { token: 'delimiter', foreground: 'eef0f9' },
      { token: 'delimiter.angle', foreground: 'b7afff' },
      { token: 'delimiter.bracket', foreground: 'eef0f9' },
      { token: 'delimiter.curly', foreground: 'eef0f9' },
      { token: 'delimiter.parenthesis', foreground: 'eef0f9' },
      { token: 'delimiter.square', foreground: 'eef0f9' },
      { token: 'enum', foreground: 'b7afff' },
      { token: 'function', foreground: '00daef' },
      { token: 'identifier', foreground: '4bf3c8' },
      { token: 'interface', foreground: 'b7afff' },
      { token: 'invalid', foreground: 'f06788' },
      { token: 'keyword.async', foreground: '54b9ff', fontStyle: 'bold' },
      { token: 'keyword.await', foreground: '54b9ff', fontStyle: 'bold' },
      { token: 'keyword.class', foreground: 'ff6d91', fontStyle: 'bold' },
      { token: 'keyword.const', foreground: '54b9ff', fontStyle: 'bold' },
      { token: 'keyword.function', foreground: 'ff6d91', fontStyle: 'bold' },
      { token: 'keyword.interface', foreground: 'ff6d91', fontStyle: 'bold' },
      { token: 'keyword.namespace', foreground: 'ff6d91', fontStyle: 'bold' },
      { token: 'keyword.new', foreground: 'ff6d91', fontStyle: 'bold' },
      { token: 'keyword.private', foreground: '54b9ff', fontStyle: 'bold' },
      { token: 'keyword.public', foreground: '54b9ff', fontStyle: 'bold' },
      { token: 'keyword.return', foreground: '54b9ff', fontStyle: 'bold' },
      { token: 'keyword.static', foreground: '54b9ff', fontStyle: 'bold' },
      { token: 'keyword.type', foreground: 'ff6d91', fontStyle: 'bold' },
      { token: 'keyword.using', foreground: 'ff6d91', fontStyle: 'bold' },
      { token: 'keyword.var', foreground: '54b9ff', fontStyle: 'bold' },
      { token: 'keyword', foreground: '54b9ff' },
      { token: 'keyword.control', foreground: '54b9ff' },
      { token: 'keyword.operator', foreground: 'eef0f9' },
      { token: 'method', foreground: '00daef' },
      { token: 'namespace', foreground: 'acafff' },
      { token: 'number', foreground: 'ffd493' },
      { token: 'number.binary', foreground: 'ffd493' },
      { token: 'number.float', foreground: 'ffd493' },
      { token: 'number.hex', foreground: 'ffd493' },
      { token: 'operator', foreground: 'eef0f9' },
      { token: 'regexp', foreground: 'eef0f9' },
      { token: 'regexp.escape', foreground: 'ffd493' },
      { token: 'regexp.escape.control', foreground: 'ff6d91' },
      { token: 'string', foreground: 'ffd493' },
      { token: 'string.escape', foreground: '54b9ff' },
      { token: 'string.invalid', foreground: 'f06788' },
      { token: 'string.quote', foreground: 'ffd493' },
      { token: 'type', foreground: 'acafff' },
      { token: 'type.identifier', foreground: 'acafff' },
      { token: 'variable', foreground: '4bf3c8' },
      { token: 'variable.predefined', foreground: 'acafff' }
    ],
    encodedTokensColors: [
      '#eef0f9',
      '#8f9199',
      '#54b9ff',
      '#ff6d91',
      '#00daef',
      '#4bf3c8',
      '#acafff',
      '#b7afff',
      '#ffd493',
      '#f06788'
    ],
    colors: {
      'editor.background': '#17191e',
      'editor.findMatchBackground': '#515c6a',
      'editor.findMatchBorder': '#74879f',
      'editor.findMatchHighlightBackground': '#ea5c0055',
      'editor.findMatchHighlightBorder': '#ffffff00',
      'editor.findRangeHighlightBackground': '#23262d',
      'editor.findRangeHighlightBorder': '#b2434300',
      'editor.foldBackground': '#ad5dca26',
      'editor.foreground': '#eef0f9',
      'editor.hoverHighlightBackground': '#5495d740',
      'editor.inactiveSelectionBackground': '#2a2d34',
      'editor.lineHighlightBackground': '#23262d',
      'editor.lineHighlightBorder': '#ffffff00',
      'editor.rangeHighlightBackground': '#ffffff0b',
      'editor.rangeHighlightBorder': '#ffffff00',
      'editor.selectionBackground': '#ad5dca44',
      'editor.selectionHighlightBackground': '#add6ff34',
      'editor.selectionHighlightBorder': '#495f77',
      'editor.wordHighlightBackground': '#494949b8',
      'editor.wordHighlightStrongBackground': '#004972b8',
      'editorBracketMatch.background': '#545864',
      'editorBracketMatch.border': '#ffffff00',
      'editorCursor.background': '#000000',
      'editorCursor.foreground': '#aeafad',
      'editorGutter.addedBackground': '#4bf3c8',
      'editorGutter.background': '#17191e',
      'editorGutter.commentRangeForeground': '#545864',
      'editorGutter.deletedBackground': '#f06788',
      'editorGutter.foldingControlForeground': '#545864',
      'editorGutter.modifiedBackground': '#54b9ff',
      'editorLineNumber.activeForeground': '#858b98',
      'editorLineNumber.foreground': '#545864',
      'editorSuggestWidget.background': '#252526',
      'editorSuggestWidget.border': '#454545',
      'editorSuggestWidget.foreground': '#d4d4d4',
      'editorSuggestWidget.highlightForeground': '#0097fb',
      'editorSuggestWidget.selectedBackground': '#062f4a',
      'editorWidget.background': '#343841',
      'editorWidget.foreground': '#ffffff',
      'editorWidget.resizeBorder': '#cc75f4',
      'minimap.background': '#17191e',
      'scrollbarSlider.activeBackground': '#54b9ff66',
      'scrollbarSlider.background': '#54586466',
      'scrollbarSlider.hoverBackground': '#545864B3'
    }
  },
  terminal: HOUSTON_TERMINAL
};

// ── Dracula ─────────────────────────────────────────────────────────────────

const DRACULA: ShellTheme = {
  id: 'dracula',
  label: 'Dracula',
  description: 'Dracula’s purple and pink, across the whole app.',
  tokens: {
    // TSK-1389 (Fable C) Dracula: the official draculatheme.com palette.
    '--color-bg': '#191a21',
    '--color-surface': '#282a36',
    '--color-elevated': '#30323f',
    '--color-rail': '#21222c',
    '--color-overlay': '#343746',
    '--color-text': '#f8f8f2',
    '--color-text-2': '#a3a8c6',
    '--color-text-3': '#74788f',
    '--color-disabled-text': '#74788f',
    '--color-accent': '#bd93f9',
    '--color-border': 'rgba(98, 114, 164, 0.3)',
    '--color-field-border': 'rgba(98, 114, 164, 0.4)',
    '--color-focus': 'rgba(189, 147, 249, 0.45)',
    '--color-focus-solid': '#bd93f9',
    '--color-selected': '#44475a',
    '--color-selected-border': '#bd93f9',
    '--color-hover': '#343746',
    '--color-live': '#8be9fd',
    '--color-good': '#50fa7b',
    '--color-bad': '#ff5555',
    '--color-attention': '#f1fa8c',
    '--color-idle': '#a3a8c6',
    '--color-status-idle': '#a3a8c6',
    '--color-merged': '#a371f7',
    '--color-on-accent': '#282a36',
    '--color-live-bg': 'rgba(139, 233, 253, 0.12)',
    '--color-good-bg': 'rgba(80, 250, 123, 0.12)',
    '--color-bad-bg': 'rgba(255, 85, 85, 0.12)',
    '--color-bad-bg-strong': 'rgba(255, 85, 85, 0.2)',
    '--color-attention-bg': 'rgba(241, 250, 140, 0.12)',
    '--color-scrim': 'rgba(0, 0, 0, 0.6)',
    '--color-text-selection': 'rgba(255, 121, 198, 0.6)',
    '--color-section-header-text': '#a3a8c6',
    '--color-section-focus-ring': '#bd93f9',
    '--color-tab-unfocused-surface': '#282a36',
    '--color-tab-unfocused-text': '#a3a8c6'
  },
  monaco: {
    id: 'dracula',
    base: 'vs-dark',
    inherit: true,
    rules: [
      { token: 'comment', foreground: '6272a4', fontStyle: 'italic' },
      { token: 'comment.doc', foreground: '6272a4', fontStyle: 'italic' },
      { token: 'constant', foreground: 'bd93f9' },
      { token: 'class', foreground: '50fa7b' },
      { token: 'delimiter', foreground: 'f8f8f2' },
      { token: 'delimiter.angle', foreground: '8be9fd' },
      { token: 'delimiter.bracket', foreground: 'f8f8f2' },
      { token: 'delimiter.curly', foreground: 'f8f8f2' },
      { token: 'delimiter.parenthesis', foreground: 'f8f8f2' },
      { token: 'delimiter.square', foreground: 'f8f8f2' },
      { token: 'enum', foreground: '8be9fd' },
      { token: 'function', foreground: '50fa7b' },
      { token: 'identifier', foreground: 'f8f8f2' },
      { token: 'interface', foreground: '8be9fd' },
      { token: 'invalid', foreground: 'ff5555' },
      { token: 'keyword.async', foreground: 'bd93f9', fontStyle: 'bold' },
      { token: 'keyword.await', foreground: 'bd93f9', fontStyle: 'bold' },
      { token: 'keyword.class', foreground: 'ff79c6', fontStyle: 'bold' },
      { token: 'keyword.const', foreground: 'bd93f9', fontStyle: 'bold' },
      { token: 'keyword.function', foreground: 'ff79c6', fontStyle: 'bold' },
      { token: 'keyword.interface', foreground: 'ff79c6', fontStyle: 'bold' },
      { token: 'keyword.namespace', foreground: 'ff79c6', fontStyle: 'bold' },
      { token: 'keyword.new', foreground: 'ff79c6', fontStyle: 'bold' },
      { token: 'keyword.private', foreground: 'bd93f9', fontStyle: 'bold' },
      { token: 'keyword.public', foreground: 'bd93f9', fontStyle: 'bold' },
      { token: 'keyword.return', foreground: 'bd93f9', fontStyle: 'bold' },
      { token: 'keyword.static', foreground: 'bd93f9', fontStyle: 'bold' },
      { token: 'keyword.type', foreground: 'ff79c6', fontStyle: 'bold' },
      { token: 'keyword.using', foreground: 'ff79c6', fontStyle: 'bold' },
      { token: 'keyword.var', foreground: 'bd93f9', fontStyle: 'bold' },
      { token: 'keyword', foreground: 'bd93f9' },
      { token: 'keyword.control', foreground: 'bd93f9' },
      { token: 'keyword.operator', foreground: 'ff79c6' },
      { token: 'method', foreground: '50fa7b' },
      { token: 'namespace', foreground: '8be9fd' },
      { token: 'number', foreground: 'bd93f9' },
      { token: 'number.binary', foreground: 'bd93f9' },
      { token: 'number.float', foreground: 'bd93f9' },
      { token: 'number.hex', foreground: 'bd93f9' },
      { token: 'operator', foreground: 'ff79c6' },
      { token: 'regexp', foreground: 'f1fa8c' },
      { token: 'regexp.escape', foreground: 'ff79c6' },
      { token: 'regexp.escape.control', foreground: 'ff79c6' },
      { token: 'string', foreground: 'f1fa8c' },
      { token: 'string.escape', foreground: 'ff79c6' },
      { token: 'string.invalid', foreground: 'ff5555' },
      { token: 'string.quote', foreground: 'f1fa8c' },
      { token: 'type', foreground: '8be9fd' },
      { token: 'type.identifier', foreground: '8be9fd' },
      { token: 'variable', foreground: 'f8f8f2' },
      { token: 'variable.predefined', foreground: 'bd93f9' }
    ],
    encodedTokensColors: [
      '#f8f8f2',
      '#6272a4',
      '#bd93f9',
      '#ff79c6',
      '#50fa7b',
      '#f8f8f2',
      '#8be9fd',
      '#8be9fd',
      '#f1fa8c',
      '#ff5555'
    ],
    colors: {
      'editor.background': '#282a36',
      'editor.findMatchBackground': '#44475a',
      'editor.findMatchBorder': '#6272a4',
      'editor.findMatchHighlightBackground': '#ffb86c55',
      'editor.findMatchHighlightBorder': '#ffffff00',
      'editor.findRangeHighlightBackground': '#343746',
      'editor.findRangeHighlightBorder': '#b2434300',
      'editor.foldBackground': '#44475a55',
      'editor.foreground': '#f8f8f2',
      'editor.hoverHighlightBackground': '#6272a466',
      'editor.inactiveSelectionBackground': '#3a3d4d',
      'editor.lineHighlightBackground': '#343746',
      'editor.lineHighlightBorder': '#ffffff00',
      'editor.rangeHighlightBackground': '#ffffff0b',
      'editor.rangeHighlightBorder': '#ffffff00',
      'editor.selectionBackground': '#44475a99',
      'editor.selectionHighlightBackground': '#6272a444',
      'editor.selectionHighlightBorder': '#6272a4',
      'editor.wordHighlightBackground': '#44475ab8',
      'editor.wordHighlightStrongBackground': '#6272a4b8',
      'editorBracketMatch.background': '#6272a4',
      'editorBracketMatch.border': '#ffffff00',
      'editorCursor.background': '#000000',
      'editorCursor.foreground': '#f8f8f2',
      'editorGutter.addedBackground': '#50fa7b',
      'editorGutter.background': '#282a36',
      'editorGutter.commentRangeForeground': '#6272a4',
      'editorGutter.deletedBackground': '#ff5555',
      'editorGutter.foldingControlForeground': '#6272a4',
      'editorGutter.modifiedBackground': '#bd93f9',
      'editorLineNumber.activeForeground': '#f8f8f2',
      'editorLineNumber.foreground': '#6272a4',
      'editorSuggestWidget.background': '#282a36',
      'editorSuggestWidget.border': '#44475a',
      'editorSuggestWidget.foreground': '#f8f8f2',
      'editorSuggestWidget.highlightForeground': '#8be9fd',
      'editorSuggestWidget.selectedBackground': '#44475a',
      'editorWidget.background': '#343746',
      'editorWidget.foreground': '#ffffff',
      'editorWidget.resizeBorder': '#bd93f9',
      'minimap.background': '#282a36',
      'scrollbarSlider.activeBackground': '#bd93f966',
      'scrollbarSlider.background': '#6272a466',
      'scrollbarSlider.hoverBackground': '#6272a4b3'
    }
  },
  terminal: DRACULA_TERMINAL
};

// ── Assembly, Tokyo Night, Graphite ─────────────────────────────────────────
//
// These three have no editor palette of their own yet: the code editor is
// CodeMirror, which falls back to its base (Houston-coloured) look for any
// theme it has no extension for, so the Monaco entry simply reuses Houston's.

const ASSEMBLY: ShellTheme = {
  id: 'assembly',
  label: 'Assembly',
  description: 'Near-black surfaces with a green accent. The default.',
  tokens: {
    // Copied from src/lib/shell/styles/nextTokens.css and themeChrome.css. Do
    // not edit one without the other; the test reads those files and compares.
    '--color-bg': '#000000',
    '--color-surface': '#121212',
    '--color-elevated': '#1f1f1f',
    '--color-rail': '#121212',
    '--color-overlay': '#282828',
    '--color-text': '#f5f5f5',
    '--color-text-2': '#a7a7a7',
    '--color-text-3': '#6e6e6e',
    '--color-disabled-text': '#6e6e6e',
    '--color-accent': '#1ed760',
    '--color-border': 'rgba(255, 255, 255, 0.08)',
    '--color-field-border': 'rgba(255, 255, 255, 0.1)',
    '--color-focus': 'rgba(30, 215, 96, 0.45)',
    '--color-focus-solid': '#1ed760',
    '--color-selected': '#2f2f2f',
    '--color-selected-border': '#1ed760',
    '--color-hover': '#242424',
    '--color-live': '#6fb6ff',
    '--color-good': '#1ed760',
    '--color-bad': '#f15e6c',
    '--color-attention': '#f3c969',
    '--color-idle': '#a7a7a7',
    '--color-status-idle': '#a7a7a7',
    '--color-merged': '#a371f7',
    '--color-on-accent': '#06140b',
    '--color-live-bg': 'rgba(111, 182, 255, 0.12)',
    '--color-good-bg': 'rgba(30, 215, 96, 0.12)',
    '--color-bad-bg': 'rgba(241, 94, 108, 0.12)',
    '--color-bad-bg-strong': 'rgba(241, 94, 108, 0.2)',
    '--color-attention-bg': 'rgba(243, 201, 105, 0.12)',
    '--color-scrim': 'rgba(0, 0, 0, 0.6)',
    '--color-text-selection': 'rgba(255, 56, 152, 0.6)',
    '--color-section-header-text': '#a7a7a7',
    '--color-section-focus-ring': '#1ed760',
    '--color-tab-unfocused-surface': '#121212',
    '--color-tab-unfocused-text': '#a7a7a7'
  },
  monaco: { ...HOUSTON.monaco, id: 'assembly' },
  terminal: ASSEMBLY_TERMINAL
};

const TOKYO_NIGHT: ShellTheme = {
  id: 'tokyo-night',
  label: 'Tokyo Night',
  description: 'Deep navy surfaces with a soft blue accent.',
  tokens: {
    '--color-bg': '#16161e',
    '--color-surface': '#1a1b26',
    '--color-elevated': '#20222f',
    '--color-rail': '#1a1b26',
    '--color-overlay': '#24283b',
    '--color-text': '#c0caf5',
    '--color-text-2': '#9aa5ce',
    '--color-text-3': '#69718e',
    '--color-disabled-text': '#69718e',
    '--color-accent': '#7aa2f7',
    '--color-border': 'rgba(169, 177, 214, 0.1)',
    '--color-field-border': 'rgba(169, 177, 214, 0.14)',
    '--color-focus': 'rgba(122, 162, 247, 0.45)',
    '--color-focus-solid': '#7aa2f7',
    '--color-selected': '#292e42',
    '--color-selected-border': '#7aa2f7',
    '--color-hover': '#242838',
    '--color-live': '#7dcfff',
    '--color-good': '#9ece6a',
    '--color-bad': '#f7768e',
    '--color-attention': '#e0af68',
    '--color-idle': '#9aa5ce',
    '--color-status-idle': '#9aa5ce',
    '--color-merged': '#a371f7',
    '--color-on-accent': '#16161e',
    '--color-live-bg': 'rgba(125, 207, 255, 0.12)',
    '--color-good-bg': 'rgba(158, 206, 106, 0.12)',
    '--color-bad-bg': 'rgba(247, 118, 142, 0.12)',
    '--color-bad-bg-strong': 'rgba(247, 118, 142, 0.2)',
    '--color-attention-bg': 'rgba(224, 175, 104, 0.12)',
    '--color-scrim': 'rgba(0, 0, 0, 0.6)',
    '--color-text-selection': 'rgba(187, 154, 247, 0.55)',
    '--color-section-header-text': '#9aa5ce',
    '--color-section-focus-ring': '#7aa2f7',
    '--color-tab-unfocused-surface': '#1a1b26',
    '--color-tab-unfocused-text': '#9aa5ce'
  },
  monaco: { ...HOUSTON.monaco, id: 'tokyo-night' },
  terminal: TOKYO_NIGHT_TERMINAL
};

const GRAPHITE: ShellTheme = {
  id: 'graphite',
  label: 'Graphite',
  description: 'Neutral near-black with a monochrome accent.',
  tokens: {
    '--color-bg': '#09090b',
    '--color-surface': '#141416',
    '--color-elevated': '#1c1c1f',
    '--color-rail': '#111113',
    '--color-overlay': '#222225',
    '--color-text': '#ededed',
    '--color-text-2': '#8f9096',
    '--color-text-3': '#6a6b70',
    '--color-disabled-text': '#6a6b70',
    '--color-accent': '#ededed',
    '--color-border': 'rgba(255, 255, 255, 0.08)',
    '--color-field-border': 'rgba(255, 255, 255, 0.11)',
    '--color-focus': 'rgba(237, 237, 237, 0.35)',
    '--color-focus-solid': '#ededed',
    '--color-selected': '#2a2a2e',
    '--color-selected-border': '#ededed',
    '--color-hover': '#1f1f23',
    '--color-live': '#7ab8f5',
    '--color-good': '#5fd38d',
    '--color-bad': '#f0616d',
    '--color-attention': '#e8c268',
    '--color-idle': '#8f9096',
    '--color-status-idle': '#8f9096',
    '--color-merged': '#a371f7',
    '--color-on-accent': '#09090b',
    '--color-live-bg': 'rgba(122, 184, 245, 0.12)',
    '--color-good-bg': 'rgba(95, 211, 141, 0.12)',
    '--color-bad-bg': 'rgba(240, 97, 109, 0.12)',
    '--color-bad-bg-strong': 'rgba(240, 97, 109, 0.2)',
    '--color-attention-bg': 'rgba(232, 194, 104, 0.12)',
    '--color-scrim': 'rgba(0, 0, 0, 0.6)',
    '--color-text-selection': 'rgba(232, 194, 104, 0.45)',
    '--color-section-header-text': '#8f9096',
    '--color-section-focus-ring': '#ededed',
    '--color-tab-unfocused-surface': '#141416',
    '--color-tab-unfocused-text': '#8f9096'
  },
  monaco: { ...HOUSTON.monaco, id: 'graphite' },
  terminal: GRAPHITE_TERMINAL
};

// ── The roster ──────────────────────────────────────────────────────────────

/** The theme the app opens with, and the one every unknown name falls back to. */
export const DEFAULT_THEME_ID = 'assembly';

/** ThemeMirror entries keep the Assembly shell and swap only the editor's syntax colors. */
const THEMEMIRROR_THEMES: ShellTheme[] = [
  ['amy', 'Amy'],
  ['ayu-light', 'Ayu Light'],
  ['cobalt', 'Cobalt'],
  ['rose-pine-dawn', 'Rose Pine Dawn'],
  ['tomorrow', 'Tomorrow']
].map(([id, label]) => ({
  ...ASSEMBLY,
  id,
  label,
  description: `${label} CodeMirror syntax theme.`,
  monaco: { ...ASSEMBLY.monaco, id }
}));

/** Every theme, in the order the chooser should list them. */
export const THEMES: ShellTheme[] = [
  ASSEMBLY,
  HOUSTON,
  DRACULA,
  TOKYO_NIGHT,
  GRAPHITE,
  ...THEMEMIRROR_THEMES
];

const BY_ID = new Map<string, ShellTheme>(THEMES.map((theme) => [theme.id, theme]));

/** Every theme, for a chooser or a palette command. */
export function listThemes(): ShellTheme[] {
  return [...THEMES];
}

/** True when `id` names a theme that exists. */
export function isThemeId(id: unknown): boolean {
  return typeof id === 'string' && BY_ID.has(id);
}

/**
 * The theme `id` means. A settings file written before themes existed says
 * `dark`, and `light` was offered but never built, so anything unrecognised
 * lands on the theme the app ships with rather than on nothing at all.
 */
export function resolveThemeId(id: unknown): string {
  return isThemeId(id) ? (id as string) : DEFAULT_THEME_ID;
}

/** The theme `id` names, or the default one. Never throws. */
export function getTheme(id: unknown): ShellTheme {
  return BY_ID.get(resolveThemeId(id)) as ShellTheme;
}
