/**
 * languageServerStatus.ts — what the editor can say about the language server,
 * and when it should hold work back.
 *
 * A language server is the background program that knows what the code MEANS:
 * where a name is defined, what is wrong with a file, what the inline type
 * hints are. For a large C# solution it can spend the first half-minute after
 * start-up just reading the project, and everything asked of it in that window
 * either comes back empty or arrives very late. Until now the editor said
 * nothing about that, so a slow start looked like a broken feature.
 *
 * Two jobs live here, both kept out of the component so they can be tested
 * without a browser:
 *
 *  1. Turn the desktop app's answer into the words on the chip. The `state`
 *     and `detail` fields are ADDITIVE — a desktop build older than they are
 *     simply will not send them, and a browser tab has no desktop app behind it
 *     at all. Both cases must produce no chip rather than a guess, so every
 *     read here is defensive and returns null when it is not certain.
 *
 *  2. Hold back the extra lookups while the server is still getting started.
 *     `createLanguageServerGate` is a waiting room: register work with it, and
 *     it runs that work the moment the server is ready. The owner gets back an
 *     unsubscribe function for the exact wait it created.
 */

/** How far along the language server is, as the desktop app reports it. */
export type LanguageServerState = 'not-running' | 'starting' | 'indexing' | 'ready' | 'disabled';

const KNOWN_STATES: readonly string[] = [
  'not-running',
  'starting',
  'indexing',
  'ready',
  'disabled'
];

/** The chip's words and its colour family. */
export type LanguageServerChip = {
  /** What the chip shows, e.g. "C#: indexing…". */
  label: string;
  /** The longer sentence shown on hover. */
  tooltip: string;
  /** Which colour the chip is painted in. */
  tone: 'ready' | 'working' | 'off';
};

function fieldOf(status: unknown, name: string): unknown {
  if (!status || typeof status !== 'object') return undefined;
  return (status as Record<string, unknown>)[name];
}

/**
 * The reported state, or null when this build does not report one. Anything
 * unrecognised counts as "not reported": a name this app has never heard of
 * cannot be described honestly, so it is better to say nothing.
 */
export function readLanguageServerState(status: unknown): LanguageServerState | null {
  const state = fieldOf(status, 'state');
  if (typeof state !== 'string' || !KNOWN_STATES.includes(state)) return null;
  return state as LanguageServerState;
}

/** The server's own plain-English sentence, when it sent one worth showing. */
export function readLanguageServerDetail(status: unknown): string | null {
  const detail = fieldOf(status, 'detail');
  if (typeof detail !== 'string') return null;
  const trimmed = detail.trim();
  return trimmed.length > 0 ? trimmed : null;
}

/**
 * Names people actually write. The identifiers come from the file extension
 * mapping (`sourceRecordFromPath.ts`), which is all lower case, and "csharp"
 * on a chip reads like a bug.
 */
const LANGUAGE_NAMES: Record<string, string> = {
  csharp: 'C#',
  typescript: 'TypeScript',
  typescriptreact: 'TypeScript',
  javascript: 'JavaScript',
  javascriptreact: 'JavaScript',
  rust: 'Rust',
  svelte: 'Svelte',
  python: 'Python',
  go: 'Go',
  json: 'JSON',
  sql: 'SQL',
  html: 'HTML',
  css: 'CSS',
  yaml: 'YAML',
  markdown: 'Markdown'
};

/** How this language should be written in front of a person. */
export function languageDisplayName(language: string): string {
  const key = language.trim().toLowerCase();
  if (!key) return 'This file';
  return LANGUAGE_NAMES[key] ?? key.charAt(0).toUpperCase() + key.slice(1);
}

/**
 * The same names cut down to the two or three characters that fit inside a
 * control. The switch beside the centre pane's tabs has room for a badge and
 * nothing more, and a badge is enough: it only has to say WHICH language the
 * switch is currently about, not spell it out.
 */
const SHORT_LANGUAGE_LABELS: Record<string, string> = {
  csharp: 'C#',
  typescript: 'TS',
  typescriptreact: 'TS',
  javascript: 'JS',
  javascriptreact: 'JS',
  rust: 'RS',
  svelte: 'SV',
  python: 'PY',
  go: 'GO',
  json: 'JSN',
  sql: 'SQL',
  html: 'HT',
  css: 'CSS',
  yaml: 'YML',
  markdown: 'MD'
};

/** No file open means no language, and a dash says that without guessing. */
export const NO_LANGUAGE_LABEL = '—';

/** This language in two or three characters, for a badge inside a control. */
export function languageShortLabel(language: string | null): string {
  const key = language?.trim().toLowerCase() ?? '';
  if (!key) return NO_LANGUAGE_LABEL;
  return SHORT_LANGUAGE_LABELS[key] ?? key.slice(0, 2).toUpperCase();
}

/**
 * The chip for one language's server, or null when there is nothing truthful to
 * say — no desktop app, or a desktop build that does not report its state.
 */
export function describeLanguageServer(
  language: string,
  status: unknown
): LanguageServerChip | null {
  const state = readLanguageServerState(status);
  if (!state) return null;

  const name = languageDisplayName(language);
  const detail = readLanguageServerDetail(status);

  const wording: Record<LanguageServerState, { label: string; standby: string; tone: LanguageServerChip['tone'] }> = {
    ready: {
      label: `${name}: ready`,
      standby: `The ${name} language server is ready.`,
      tone: 'ready'
    },
    indexing: {
      label: `${name}: indexing…`,
      standby: `The ${name} language server is reading the project.`,
      tone: 'working'
    },
    starting: {
      label: `${name}: starting…`,
      standby: `The ${name} language server is starting up.`,
      tone: 'working'
    },
    'not-running': {
      label: `${name}: not running`,
      standby: `No ${name} language server is running for this project yet.`,
      tone: 'off'
    },
    disabled: {
      label: `${name} server is off`,
      standby: `The ${name} language server is switched off in Settings.`,
      tone: 'off'
    }
  };

  const { label, standby, tone } = wording[state];
  return { label, tooltip: detail ?? standby, tone };
}

/**
 * The desktop app pushes one of these every time a language server moves on:
 * `source-lsp-status-changed`. It carries the project and language it is about,
 * because several servers can be running at once.
 */
export type LanguageServerStatusMessage = {
  root: string;
  language: string;
  state: LanguageServerState;
  detail: string | null;
};

/**
 * The language name a server uses for this file. One TypeScript server serves
 * .ts and .tsx files alike, so messages about either kind say "typescript";
 * the JavaScript server treats .js and .jsx the same way.
 */
export function languageServerLanguageFor(language: string): string {
  if (language === 'tsx') return 'typescript';
  if (language === 'jsx') return 'javascript';
  return language;
}

/**
 * Does this pushed message describe the file currently on screen? Anything for
 * another project or another language belongs to a different server and must
 * not move this chip.
 */
export function statusMessageIsAboutThisFile(
  message: unknown,
  projectRoot: string | null,
  language: string | null
): boolean {
  if (!projectRoot || !language) return false;
  const messageRoot = fieldOf(message, 'root');
  const messageLanguage = fieldOf(message, 'language');
  if (typeof messageRoot !== 'string' || typeof messageLanguage !== 'string') return false;
  return (
    messageRoot === projectRoot &&
    languageServerLanguageFor(messageLanguage) === languageServerLanguageFor(language)
  );
}

/**
 * Is the server still getting itself going? Only then is it worth waiting.
 *
 * A server that is off, missing, or ready is as good as it is going to get, and
 * an unknown state means an older desktop build or a browser tab — both keep
 * today's behaviour, which is to ask straight away.
 */
export function languageServerIsBusy(state: LanguageServerState | null): boolean {
  return state === 'starting' || state === 'indexing';
}

export interface LanguageServerGate {
  /** The last state the gate was told about. */
  readonly state: LanguageServerState | null;
  /** Record a new state; anything waiting is let through if the wait is over. */
  setState(next: LanguageServerState | null): void;
  /** Is work being held back right now? */
  isBusy(): boolean;
  /** Run this callback as soon as it is worth asking the server anything. */
  onReady(callback: () => void): () => void;
  /** Let everything through at once — the panel is closing or the file changed. */
  releaseAll(): void;
}

export function createLanguageServerGate(): LanguageServerGate {
  let state: LanguageServerState | null = null;
  let waiting: Array<() => void> = [];

  function letEveryoneThrough(): void {
    const released = waiting;
    waiting = [];
    for (const resume of released) resume();
  }

  return {
    get state() {
      return state;
    },

    setState(next: LanguageServerState | null): void {
      state = next;
      if (!languageServerIsBusy(state)) letEveryoneThrough();
    },

    isBusy(): boolean {
      return languageServerIsBusy(state);
    },

    onReady(callback: () => void): () => void {
      if (!languageServerIsBusy(state)) {
        callback();
        return () => {};
      }

      let active = true;
      const release = () => {
        if (!active) return;
        active = false;
        callback();
      };
      waiting.push(release);
      return () => {
        if (!active) return;
        active = false;
        waiting = waiting.filter((candidate) => candidate !== release);
      };
    },

    releaseAll(): void {
      letEveryoneThrough();
    }
  };
}
