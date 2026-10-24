/**
 * themeService.ts — picking a theme, and making the whole app follow it.
 *
 * Three surfaces have to be repainted, and only one of them reads CSS:
 *
 *   the chrome    every panel, dialog, button and dock divider is painted from
 *                 `--color-…` custom properties, so setting those on the page
 *                 carries the entire shadcn/dockview surface for free.
 *   the editor    Monaco keeps its own registry of themes by name. It has to be
 *                 told to define the theme and then to switch to it.
 *   the terminal  Ghostty keeps a theme per mounted terminal view, so the
 *                 workspace view receives changes while it is open.
 *
 * The last two live in files this module must not import (the editor component
 * is 2000+ lines of Monaco wiring; the terminal factory is the Ghostty
 * seam). So instead of reaching into them, this module offers a place for them
 * to hand in a function: `registerMonacoApplier` and `registerTerminalApplier`.
 * Whoever registers is called straight away with the theme in force, so a code
 * editor that mounts ten minutes after the theme was picked still comes up in
 * the right colors.
 *
 * Where the colors are written matters. `nextTokens.css` sets the same names on
 * BOTH the shell element and the document root, and a value set on an element
 * beats one inherited from its parent — so writing the theme only on the
 * document root would leave the shell itself on the stylesheet's colors, and
 * writing it only on the shell would leave the menus and dialogs behind (they
 * are moved to the end of the page when they open). This writes both.
 */
import { settings, updateSettings } from '../settingsStore.svelte';

import {
  DEFAULT_THEME_ID,
  getTheme,
  listThemes,
  resolveThemeId,
  type ShellTheme
} from './themeRegistry';

/** A function that repaints one surface. */
export type ThemeApplier = (theme: ShellTheme) => void;

/** What {@link apply} was asked to do, for anything that wants to react. */
export type ApplyOptions = {
  /**
   * The shell element to paint. Left out, the service looks for `.next-shell`
   * itself — which is right in every case except a test with its own DOM.
   */
  root?: HTMLElement | null;
  /**
   * Whether to remember the choice. True for a user picking a theme; false when
   * simply applying the theme that was already remembered.
   */
  persist?: boolean;
};

let current: ShellTheme = getTheme(DEFAULT_THEME_ID);

const monacoAppliers = new Set<ThemeApplier>();
const terminalAppliers = new Set<ThemeApplier>();

/** The theme in force right now. */
export function currentTheme(): ShellTheme {
  return current;
}

/** The id of the theme in force right now. */
export function currentThemeId(): string {
  return current.id;
}

/** The roster, ready for a chooser: `{ value, label }` per theme. */
export function themeChoices(): Array<{ value: string; label: string; description: string }> {
  return listThemes().map((theme) => ({
    value: theme.id,
    label: theme.label,
    description: theme.description
  }));
}

/**
 * Run one applier without letting it take the others down with it. A code
 * editor that has been torn down mid-switch is not a reason for the terminals
 * to keep the old colors.
 */
function runApplier(applier: ThemeApplier, theme: ShellTheme, surface: string): void {
  try {
    applier(theme);
  } catch (error) {
    console.warn(`Could not repaint the ${surface} for the ${theme.label} theme.`, error);
  }
}

/**
 * Hand in the function that repaints the code editor. It is called immediately
 * with the theme in force, and on every switch after that. The returned
 * function unregisters it.
 */
export function registerMonacoApplier(applier: ThemeApplier): () => void {
  monacoAppliers.add(applier);
  runApplier(applier, current, 'code editor');
  return () => {
    monacoAppliers.delete(applier);
  };
}

/**
 * Hand in the function that repaints the terminals. Same contract as
 * {@link registerMonacoApplier}: called at once, then on every switch.
 */
export function registerTerminalApplier(applier: ThemeApplier): () => void {
  terminalAppliers.add(applier);
  runApplier(applier, current, 'terminals');
  return () => {
    terminalAppliers.delete(applier);
  };
}

/** Write a theme's colors onto one element as inline custom properties. */
function paint(element: HTMLElement, theme: ShellTheme): void {
  for (const [name, value] of Object.entries(theme.tokens)) {
    element.style.setProperty(name, value);
  }
}

/** The elements that have to carry the colors. See the note at the top. */
function paintTargets(root: HTMLElement | null | undefined): HTMLElement[] {
  if (typeof document === 'undefined') return [];
  const targets: HTMLElement[] = [document.documentElement];
  const shell = root ?? document.querySelector<HTMLElement>('.next-shell');
  if (shell && shell !== document.documentElement) targets.push(shell);
  return targets;
}

/**
 * Remove every inline color this service ever painted.
 *
 * Painting writes inline properties onto `<html>`, which outranks the
 * route-class scoping that keeps the old shell on its own palette —
 * and inline styles outlive the shell unless something takes them off. The
 * /next page calls this from its unmount cleanup so leaving the shell leaves
 * the document exactly as it was found. Walks the SAME token names paint()
 * writes (the registry test pins that the two lists cannot drift apart).
 */
export function clearTheme(root?: HTMLElement | null): void {
  for (const target of paintTargets(root)) {
    for (const name of Object.keys(current.tokens)) {
      target.style.removeProperty(name);
    }
  }
}

/**
 * Switch to a theme: paint the chrome, tell the editor and the terminals, and
 * remember the choice. An unknown id lands on the theme the app ships with, so
 * this never fails on a stale or hand-edited settings file.
 *
 * @returns the theme that ended up in force.
 */
export function apply(themeId: unknown, options: ApplyOptions = {}): ShellTheme {
  const theme = getTheme(themeId);
  current = theme;

  for (const target of paintTargets(options.root)) paint(target, theme);
  for (const applier of monacoAppliers) runApplier(applier, theme, 'code editor');
  for (const applier of terminalAppliers) runApplier(applier, theme, 'terminals');

  if (options.persist !== false && settings.appearance.themeId !== theme.id) {
    updateSettings('appearance', { themeId: theme.id });
  }

  return theme;
}

/**
 * Apply the theme that was remembered last time. Call this once, where the
 * shell mounts — before that, the chrome is painted by the stylesheets, which
 * carry the shipped theme's colors.
 *
 * The remembered choice is not rewritten here: a settings file that still says
 * `dark` renders as the default theme and stays as it is until the user picks something.
 */
export function applyStoredTheme(root?: HTMLElement | null): ShellTheme {
  return apply(resolveThemeId(settings.appearance.themeId), { root, persist: false });
}

/** Forget every registered applier. For tests and hot reload; rarely needed. */
export function resetThemeAppliers(): void {
  monacoAppliers.clear();
  terminalAppliers.clear();
}
