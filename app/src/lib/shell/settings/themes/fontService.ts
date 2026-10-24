/**
 * fontService.ts — picking the interface faces, and making the app follow.
 *
 * The chrome reads `--font-ui` and `--font-mono` (tokens.css), so a face is
 * changed by writing those two properties. They are written in the same two
 * places the theme service paints — the document root and the shell element —
 * for the same reason: menus and dialogs are moved to the end of the page when
 * they open and would otherwise keep the stylesheet's face.
 *
 * The editor and the terminals are NOT covered here on purpose: each has its
 * own font setting already (Settings → Editor, Settings → Terminal).
 */
import { settings, updateSettings } from '../settingsStore.svelte';
import { getMonoFont, getUiFont, type ShellFont } from './fontRegistry';

const UI_TOKEN = '--font-ui';
const MONO_TOKEN = '--font-mono';

function paintTargets(root?: HTMLElement | null): HTMLElement[] {
  if (typeof document === 'undefined') return [];
  const targets: HTMLElement[] = [document.documentElement];
  const shell = root ?? document.querySelector<HTMLElement>('.next-shell');
  if (shell && shell !== document.documentElement) targets.push(shell);
  return targets;
}

/** Use `id` for reading, everywhere, and remember it. */
export function applyUiFont(id: unknown, options: { root?: HTMLElement | null; persist?: boolean } = {}): ShellFont {
  const font = getUiFont(id);
  for (const target of paintTargets(options.root)) target.style.setProperty(UI_TOKEN, font.stack);
  if (options.persist !== false && settings.appearance.uiFontId !== font.id) {
    updateSettings('appearance', { uiFontId: font.id });
  }
  return font;
}

/** Use `id` for code, everywhere, and remember it. */
export function applyMonoFont(id: unknown, options: { root?: HTMLElement | null; persist?: boolean } = {}): ShellFont {
  const font = getMonoFont(id);
  for (const target of paintTargets(options.root)) target.style.setProperty(MONO_TOKEN, font.stack);
  if (options.persist !== false && settings.appearance.monoFontId !== font.id) {
    updateSettings('appearance', { monoFontId: font.id });
  }
  return font;
}

/** Apply the faces remembered last time. Call once, where the shell mounts. */
export function applyStoredFonts(root?: HTMLElement | null): void {
  applyUiFont(settings.appearance.uiFontId, { root, persist: false });
  applyMonoFont(settings.appearance.monoFontId, { root, persist: false });
}

/** Take the inline faces off again, so leaving the shell leaves the page as found. */
export function clearFonts(root?: HTMLElement | null): void {
  for (const target of paintTargets(root)) {
    target.style.removeProperty(UI_TOKEN);
    target.style.removeProperty(MONO_TOKEN);
  }
}
