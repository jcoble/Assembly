/**
 * languageIntelligenceBar.svelte.ts — what the editor status bar needs to show the
 * project's language-server controls.
 *
 * The controls live in the editor status bar, but everything
 * they show and everything they do belongs to `EditorPanel.svelte`: it is the
 * panel that asks the desktop app for the server's status, listens for pushed
 * updates, remembers the per-project choice and starts or stops the server.
 *
 * Rules this module exists to enforce:
 *
 * 1. **One owner.** This is a mirror, not a second pipeline. Nothing here
 *    talks to the desktop app or subscribes to anything; the panel publishes
 *    what it already knows and the top strip reads it. Running the status
 *    pipeline twice would double the work this whole design exists to avoid.
 * 2. **Nothing to say means nothing on screen.** With no project open the
 *    strip shows what it always showed, so `hasProject` starts false and goes
 *    back to false when the panel goes away.
 */

/** Everything the bottom-rail controls paint themselves from. */
export interface LanguageIntelligenceBarState {
  /** The open file's language, e.g. `csharp`, or null when nothing is open. */
  language: string | null;
  /** The last thing the desktop app said about that language's server. */
  status: unknown;
  /** Is this project in full mode — language server allowed to run? */
  fullMode: boolean;
  /** True while a switch is being acted on, so it cannot be flipped twice. */
  busy: boolean;
  /** Is a project open at all? Nothing renders when it is not. */
  hasProject: boolean;
  /** The sentence on hover: what the mode means for this project. */
  title: string;
}

const EMPTY: LanguageIntelligenceBarState = {
  language: null,
  status: null,
  fullMode: false,
  busy: false,
  hasProject: false,
  title: ''
};

/** The single reactive copy. Read fields directly; write only through the
 * functions below. */
export const languageIntelligenceBar = $state<LanguageIntelligenceBarState>({ ...EMPTY });

/** Say what the controls should show. Called by the panel that owns the state. */
export function publishLanguageIntelligenceBar(next: LanguageIntelligenceBarState): void {
  languageIntelligenceBar.language = next.language;
  languageIntelligenceBar.status = next.status;
  languageIntelligenceBar.fullMode = next.fullMode;
  languageIntelligenceBar.busy = next.busy;
  languageIntelligenceBar.hasProject = next.hasProject;
  languageIntelligenceBar.title = next.title;
}

/** The panel has gone: show nothing rather than the last thing it said. */
export function clearLanguageIntelligenceBar(): void {
  publishLanguageIntelligenceBar({ ...EMPTY });
}
