/**
 * terminalPalettes.ts — each app theme's terminal colours, applied by
 * `ghosttyFactory.ts`.
 *
 * Every palette follows the theme it belongs to: `background` and
 * `cursorAccent` are the theme's `--color-surface` (the Terminal tab's panel),
 * `foreground` is `--color-text`, and `selectionBackground` is
 * `--color-text-selection`, so selected terminal text looks like selected
 * text anywhere else in the app. `selectionForeground` is `--color-text`, or
 * white where that falls short of 4.5:1 on the highlighter.
 *
 * Every slot except `selectionBackground` goes into Ghostty's WASM terminal
 * through `parseColorToHex`, which reads `#hex` and `rgb()` only and turns any
 * other form into black, so those slots must be `#rrggbb`.
 * `selectionBackground` is only a canvas fill, so it may be an `rgba()` value.
 */

import type { TerminalTheme } from './themeRegistry.ts';

export const ASSEMBLY_TERMINAL: TerminalTheme = {
  background: '#121212',
  foreground: '#f5f5f5',
  cursor: '#1ed760',
  cursorAccent: '#121212',
  selectionBackground: 'rgba(255, 56, 152, 0.6)',
  selectionForeground: '#f5f5f5',
  black: '#282828',
  red: '#f15e6c',
  green: '#1ed760',
  yellow: '#f3c969',
  blue: '#6fb6ff',
  magenta: '#a371f7',
  cyan: '#56d4dd',
  white: '#a7a7a7',
  brightBlack: '#6e6e6e',
  brightRed: '#f15e6c',
  brightGreen: '#1ed760',
  brightYellow: '#f3c969',
  brightBlue: '#6fb6ff',
  brightMagenta: '#a371f7',
  brightCyan: '#56d4dd',
  brightWhite: '#ffffff'
};

export const HOUSTON_TERMINAL: TerminalTheme = {
  background: '#17191e',
  foreground: '#eef0f9',
  cursor: '#4bf3c8',
  cursorAccent: '#17191e',
  selectionBackground: 'rgba(84, 185, 255, 0.5)',
  selectionForeground: '#eef0f9',
  black: '#2a2d34',
  red: '#ff6d91',
  green: '#4bf3c8',
  yellow: '#ffd493',
  blue: '#54b9ff',
  magenta: '#b7afff',
  cyan: '#00daef',
  white: '#a0a6b4',
  brightBlack: '#6c707b',
  brightRed: '#ff6d91',
  brightGreen: '#4bf3c8',
  brightYellow: '#ffd493',
  brightBlue: '#54b9ff',
  brightMagenta: '#b7afff',
  brightCyan: '#00daef',
  brightWhite: '#ffffff'
};

/** Dracula's official palette; its background already equals its surface. */
export const DRACULA_TERMINAL: TerminalTheme = {
  background: '#282a36',
  foreground: '#f8f8f2',
  cursor: '#f8f8f2',
  cursorAccent: '#282a36',
  selectionBackground: 'rgba(255, 121, 198, 0.6)',
  selectionForeground: '#ffffff',
  black: '#000000',
  red: '#ff5555',
  green: '#50fa7b',
  yellow: '#f1fa8c',
  blue: '#bd93f9',
  magenta: '#ff79c6',
  cyan: '#8be9fd',
  white: '#bbbbbb',
  brightBlack: '#6272a4',
  brightRed: '#ff5555',
  brightGreen: '#50fa7b',
  brightYellow: '#f1fa8c',
  brightBlue: '#caa9fa',
  brightMagenta: '#ff79c6',
  brightCyan: '#8be9fd',
  brightWhite: '#ffffff'
};

export const TOKYO_NIGHT_TERMINAL: TerminalTheme = {
  background: '#1a1b26',
  foreground: '#c0caf5',
  cursor: '#7aa2f7',
  cursorAccent: '#1a1b26',
  selectionBackground: 'rgba(187, 154, 247, 0.55)',
  selectionForeground: '#ffffff',
  black: '#15161e',
  red: '#f7768e',
  green: '#9ece6a',
  yellow: '#e0af68',
  blue: '#7aa2f7',
  magenta: '#bb9af7',
  cyan: '#7dcfff',
  white: '#a9b1d6',
  brightBlack: '#737aa2',
  brightRed: '#f7768e',
  brightGreen: '#9ece6a',
  brightYellow: '#e0af68',
  brightBlue: '#7aa2f7',
  brightMagenta: '#bb9af7',
  brightCyan: '#7dcfff',
  brightWhite: '#c0caf5'
};

export const GRAPHITE_TERMINAL: TerminalTheme = {
  background: '#141416',
  foreground: '#ededed',
  cursor: '#ededed',
  cursorAccent: '#141416',
  selectionBackground: 'rgba(232, 194, 104, 0.45)',
  selectionForeground: '#ededed',
  black: '#222225',
  red: '#f0616d',
  green: '#5fd38d',
  yellow: '#e8c268',
  blue: '#7ab8f5',
  magenta: '#a371f7',
  cyan: '#6cc4d0',
  white: '#8f9096',
  brightBlack: '#6a6b70',
  brightRed: '#f0616d',
  brightGreen: '#5fd38d',
  brightYellow: '#e8c268',
  brightBlue: '#7ab8f5',
  brightMagenta: '#a371f7',
  brightCyan: '#6cc4d0',
  brightWhite: '#ffffff'
};
