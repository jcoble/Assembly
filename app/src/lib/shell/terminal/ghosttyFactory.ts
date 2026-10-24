/** Ghostty Web renderer seam for the workspace terminal. */
import type { TerminalView } from '../../liveConversationTerminals';
import { settings } from '../settings/settingsStore.svelte';
import { addTerminalView } from '../resourceDiagnostics.svelte.ts';
import { terminalFontStack } from '../settings/themes/fontRegistry';
import { registerTerminalApplier } from '../settings/themes/themeService';
import { getTheme } from '../settings/themes/themeRegistry';

const liveTerminals = new Map<import('ghostty-web').Terminal, () => void>();

registerTerminalApplier((theme) => {
  for (const terminal of liveTerminals.keys()) terminal.options.theme = { ...theme.terminal };
});

export type GhosttyModules = Awaited<ReturnType<typeof loadGhosttyModules>>;

export type ViewHooks = {
  onData(data: string): void;
  onResize(cols: number, rows: number): void;
};

function terminalAppearance(): {
  fontFamily: string;
  fontSize: number;
  lineHeight: number;
  cursorBlink: boolean;
} {
  const current = settings.terminal;
  return {
    fontFamily: terminalFontStack(current.fontFamily),
    fontSize: current.fontSize,
    lineHeight: current.lineHeight,
    cursorBlink: current.cursorBlink
  };
}

export function applyTerminalSettings(): void {
  const appearance = terminalAppearance();
  for (const [terminal, fit] of liveTerminals) {
    terminal.options.fontFamily = appearance.fontFamily;
    terminal.options.fontSize = appearance.fontSize;
    terminal.options.lineHeight = appearance.lineHeight;
    terminal.options.cursorBlink = appearance.cursorBlink;
    fit();
  }
}

export async function loadGhosttyModules(): Promise<typeof import('ghostty-web')> {
  const modules = await import('ghostty-web');
  await modules.init();
  return modules;
}

export function makeTerminalView(
  modules: GhosttyModules,
  host: HTMLElement,
  hooks: ViewHooks
): TerminalView {
  const appearance = terminalAppearance();
  const terminal = new modules.Terminal({
    convertEol: true,
    cursorBlink: appearance.cursorBlink,
    cursorStyle: 'block',
    fontFamily: appearance.fontFamily,
    fontSize: appearance.fontSize,
    lineHeight: appearance.lineHeight,
    scrollback: 8000,
    theme: { ...getTheme(settings.appearance.themeId).terminal }
  });
  const fitAddon = new modules.FitAddon();
  terminal.loadAddon(fitAddon);
  terminal.open(host);
  liveTerminals.set(terminal, fit);
  addTerminalView(1);

  const inputDisposable = terminal.onData((data) => hooks.onData(data));
  let disposed = false;

  function fit(): void {
    if (disposed) return;
    try {
      fitAddon.fit();
    } catch {
      // Hidden or zero-sized host: keep the last known geometry.
    }
    hooks.onResize(terminal.cols, terminal.rows);
  }

  function handleFontsLoaded(): void {
    fit();
  }

  document.fonts?.addEventListener('loadingdone', handleFontsLoaded);

  return {
    write(data: string): void {
      terminal.write(data);
    },

    fit,

    resize(cols: number, rows: number): void {
      if (disposed || !Number.isFinite(cols) || !Number.isFinite(rows)) return;
      if (cols < 1 || rows < 1) return;
      terminal.resize(Math.trunc(cols), Math.trunc(rows));
    },

    focus(): void {
      terminal.focus();
    },

    setVisible(visible: boolean): void {
      if (disposed) return;
      host.style.display = visible ? 'block' : 'none';
      terminal.setVisible(visible);
      if (visible) fit();
    },

    dispose(): void {
      if (disposed) return;
      disposed = true;
      document.fonts?.removeEventListener('loadingdone', handleFontsLoaded);
      liveTerminals.delete(terminal);
      addTerminalView(-1);
      inputDisposable.dispose();
      fitAddon.dispose();
      terminal.dispose();
    }
  };
}
