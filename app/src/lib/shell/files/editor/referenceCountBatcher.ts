/**
 * referenceCountBatcher.ts — how the editor asks "how many references?".
 *
 * There are two different machines that can answer that question, and this
 * module holds the ordering rules for both. Neither makes a backend call and
 * neither holds Svelte state — the caller supplies the way to ask and, in
 * tests, the clock. `sourceIntelligence.ts` wires them up.
 *
 * **The plain-text search over the project.** This is what the browser preview
 * has, and it is all it has: there is no language server behind a page served
 * by the dev server. A project-wide count changes when files elsewhere change,
 * not when the reader types, so answers are kept for a stretch instead of being
 * thrown away on every keystroke.
 *
 * **The language server.** This is what the desktop app has, and its answer is
 * the better one: it counts the uses of the symbol the reader is looking at
 * rather than every line that happens to contain that word. But it answers
 * about one spot at a time, so nothing can be shared between questions, and a
 * hundred questions fired off together is exactly the storm that made opening a
 * C# file take ten seconds. `createSemanticReferenceCountScheduler` holds the
 * rule that fixes that: only a few questions are allowed to be outstanding at
 * once, they go out in the order the editor asked (which is the order the
 * reader will read them in), and closing the file drops the rest.
 *
 * `createReferenceCountStore` is the one place a counted number is remembered,
 * for either machine. The source-intelligence owner clears it when the active
 * file or project changes, so no answer survives the view that owns it.
 */
import type { SourceReferenceCountResult } from '../../../sourceData.ts';

/**
 * One symbol's number for the margin. `atLeast` is true when the pass that
 * produced it did not read every file — it ran out of time, the project had
 * more files than one walk collects, or some files could not be read — so the
 * real number can only be this or higher, and the margin must say so. A number
 * from the language server is always exact, so it never sets this.
 */
export interface CodeLensCount {
  count: number;
  atLeast: boolean;
}

/** A count that has already been worked out, and when. */
export interface RememberedCount {
  value: CodeLensCount;
  countedAt: number;
}

/** Counts kept for a while, then let go. */
export interface CountMemory {
  /** The remembered count, or `undefined` when there is nothing worth using. */
  get(key: string): CodeLensCount | undefined;
  remember(key: string, value: CodeLensCount): void;
  forget(): void;
  readonly size: number;
}

export interface CountMemoryOptions {
  /** How long a remembered count stays good for, in milliseconds. */
  cacheMs: number;
  /** The clock, so tests can move time without waiting. */
  now?: () => number;
}

export function createCountMemory(options: CountMemoryOptions): CountMemory {
  const now = options.now ?? Date.now;
  const remembered = new Map<string, RememberedCount>();

  return {
    get(key: string): CodeLensCount | undefined {
      const entry = remembered.get(key);
      if (!entry) return undefined;
      if (now() - entry.countedAt >= options.cacheMs) {
        remembered.delete(key);
        return undefined;
      }
      return entry.value;
    },
    remember(key: string, value: CodeLensCount): void {
      remembered.set(key, { value, countedAt: now() });
    },
    forget(): void {
      remembered.clear();
    },
    get size(): number {
      return remembered.size;
    }
  };
}

export interface ReferenceCountBatcherOptions {
  /**
   * Count every one of these symbols across the project in one pass. Answering
   * with `null` means the count could not be taken at all; symbols missing from
   * a result that says it is approximate were not reached in time.
   */
  countReferences(symbolNames: string[]): Promise<SourceReferenceCountResult | null>;
  /**
   * Where a number goes once it has been counted. Handed in rather than kept
   * here so the app has exactly one place numbers are remembered — see
   * `createReferenceCountStore`, which hands out a drawer of itself for this.
   */
  memory: CountMemory;
  /** Counts at or above this are reported as this, matching the margin's "50+". */
  maxCount: number;
}

export interface ReferenceCountBatcher {
  /** How many lines mention this symbol, or `null` when that is not known. */
  count(symbolName: string): Promise<CodeLensCount | null>;
  /** Drop every remembered count (the project changed underneath us). */
  forget(): void;
}

export function createReferenceCountBatcher(
  options: ReferenceCountBatcherOptions
): ReferenceCountBatcher {
  const memory = options.memory;
  const pending = new Map<string, Promise<CodeLensCount | null>>();
  let generation = 0;

  async function askForSymbol(symbolName: string): Promise<CodeLensCount | null> {
    const startedIn = generation;

    let result: SourceReferenceCountResult | null;
    try {
      result = await options.countReferences([symbolName]);
    } catch {
      result = null;
    }

    if (startedIn !== generation) {
      return null;
    }
    const count = countFromResult(result, symbolName, options.maxCount);
    if (count !== null) memory.remember(symbolName, count);
    return count;
  }

  async function trackPendingSymbolRequest(symbolName: string): Promise<CodeLensCount | null> {
    try {
      return await askForSymbol(symbolName);
    } finally {
      pending.delete(symbolName);
    }
  }

  return {
    async count(symbolName: string): Promise<CodeLensCount | null> {
      const remembered = memory.get(symbolName);
      if (remembered !== undefined) return remembered;

      const existing = pending.get(symbolName);
      if (existing) return await existing;
      const request = trackPendingSymbolRequest(symbolName);
      pending.set(symbolName, request);
      return await request;
    },
    forget(): void {
      generation += 1;
      pending.clear();
      memory.forget();
    }
  };
}

/**
 * What one symbol's number should be, given the pass's result.
 *
 * A pass that ran out of time may never have opened the files a symbol lives
 * in, so a zero from it means "not counted", and the margin is better left
 * blank than told there are no references. A non-zero tally from such a pass
 * is real but incomplete — it comes back marked `atLeast` so the margin says
 * "at least N" instead of stating a number nobody actually counted.
 */
export function countFromResult(
  result: SourceReferenceCountResult | null,
  symbolName: string,
  maxCount: number
): CodeLensCount | null {
  const counted = result?.counts?.[symbolName];
  if (typeof counted !== 'number') return null;
  const partial = result?.approximate === true;
  if (partial && counted === 0) return null;
  const shown = Math.min(counted, maxCount);
  return { count: shown, atLeast: partial || shown < counted };
}

// ── Asking the language server, a few questions at a time ────────────────────

export interface SemanticReferenceCountSchedulerOptions {
  /**
   * Ask the language server about one spot. `null` means it could not say —
   * the margin is left without a number rather than told a made-up one.
   */
  countFor(key: string): Promise<CodeLensCount | null>;
  /** An answer arrived. Called once per spot, in the order the answers land. */
  onCounted(key: string, count: CodeLensCount | null): void;
  /**
   * How many questions may be outstanding at once. Small on purpose: a cold
   * language server takes seconds over each one, and a hundred of them fired
   * off together is what made opening a file feel broken.
   */
  maxInFlight: number;
}

export interface SemanticReferenceCountScheduler {
  /**
   * Ask about these spots, in this order — which is the order the reader is
   * about to read them in. A spot already asked about keeps its place in the
   * line rather than being asked about twice.
   */
  request(keys: string[]): void;
  /**
   * The reader moved on. Everything still queued is dropped, and the few
   * questions already sent are left to finish quietly: their answers are about
   * a file that is no longer on screen, so they are not reported.
   */
  clear(): void;
  /** How many questions are outstanding right now. */
  readonly inFlight: number;
  /** How many spots are still waiting their turn. */
  readonly waiting: number;
}

export function createSemanticReferenceCountScheduler(
  options: SemanticReferenceCountSchedulerOptions
): SemanticReferenceCountScheduler {
  /**
   * Bumped by `clear`. Every question remembers the number that was current
   * when it went out, and an answer carrying an old number is dropped — that is
   * how an answer about the file the reader just left is kept off the screen.
   */
  let generation = 0;
  let queue: string[] = [];
  let asked = new Set<string>();
  let running = 0;

  async function runSemanticCount(key: string, startedIn: number): Promise<void> {
    let count: CodeLensCount | null;
    try {
      count = await options.countFor(key);
    } catch {
      count = null;
    }
    try {
      running -= 1;
      if (startedIn === generation) {
        asked.delete(key);
        options.onCounted(key, count);
      }
    } finally {
      startWhatWeCan();
    }
  }

  function startWhatWeCan(): void {
    while (running < options.maxInFlight && queue.length > 0) {
      const key = queue.shift();
      if (key === undefined) return;
      const startedIn = generation;
      running += 1;

      void runSemanticCount(key, startedIn);
    }
  }

  return {
    request(keys: string[]): void {
      for (const key of keys) {
        if (asked.has(key)) continue;
        asked.add(key);
        queue.push(key);
      }
      startWhatWeCan();
    },
    clear(): void {
      generation += 1;
      queue = [];
      asked = new Set();
    },
    get inFlight(): number {
      return running;
    },
    get waiting(): number {
      return queue.length;
    }
  };
}

// ── Where a counted number is remembered ─────────────────────────────────────

export interface ReferenceCountStoreOptions {
  /** How long a remembered number stays good for, in milliseconds. */
  cacheMs: number;
  /** The clock, so tests can move time without waiting. */
  now?: () => number;
}

export interface StoredReferenceCount {
  project: string | null;
  file: string;
  key: string;
  value: CodeLensCount;
  countedAt: number;
}

export interface ReferenceCountStore {
  /** The remembered number, or `undefined` when there is none worth using. */
  get(project: string | null, file: string, key: string): CodeLensCount | undefined;
  remember(project: string | null, file: string, key: string, value: CodeLensCount): void;
  /** This one file was edited or saved, so only its numbers are counted again. */
  forgetFile(project: string | null, file: string): void;
  /** Every file in this project moved underneath us. */
  forgetProject(project: string | null): void;
  forgetEverything(): void;
  /** Live entries used by development resource diagnostics. */
  entries(): StoredReferenceCount[];
  /**
   * One drawer of this store, seen through the flat interface the plain-text
   * batcher wants. `whichProject` is asked each time rather than fixed, so the
   * batcher follows the reader from one project to the next without being
   * rebuilt — and without the first project's numbers being thrown away.
   */
  drawer(whichProject: () => string | null, file: string): CountMemory;
}

/**
 * Numbers filed under project, then file, then symbol.
 *
 * The project comes first so that opening another project puts its numbers in
 * their own drawer instead of emptying the first one. Coming back finds the
 * first project's numbers where they were left, which is the difference between
 * a workspace switch that repaints instantly and one that counts everything
 * again. A file is the next level down because editing a file is the one thing
 * that can change the numbers inside it and nothing else.
 */
export function createReferenceCountStore(
  options: ReferenceCountStoreOptions
): ReferenceCountStore {
  const now = options.now ?? Date.now;
  /** No project open is a drawer of its own, not a missing one. */
  const noProject = 'no project';
  const projects = new Map<string, Map<string, Map<string, RememberedCount>>>();

  const drawerFor = (project: string | null) => project ?? noProject;
  const projectFor = (drawer: string) => (drawer === noProject ? null : drawer);

  const store: ReferenceCountStore = {
    get(project: string | null, file: string, key: string): CodeLensCount | undefined {
      const files = projects.get(drawerFor(project));
      const symbols = files?.get(file);
      const entry = symbols?.get(key);
      if (!entry) return undefined;
      if (now() - entry.countedAt >= options.cacheMs) {
        symbols?.delete(key);
        return undefined;
      }
      return entry.value;
    },
    remember(project: string | null, file: string, key: string, value: CodeLensCount): void {
      const drawer = drawerFor(project);
      let files = projects.get(drawer);
      if (!files) {
        files = new Map();
        projects.set(drawer, files);
      }
      let symbols = files.get(file);
      if (!symbols) {
        symbols = new Map();
        files.set(file, symbols);
      }
      symbols.set(key, { value, countedAt: now() });
    },
    forgetFile(project: string | null, file: string): void {
      projects.get(drawerFor(project))?.delete(file);
    },
    forgetProject(project: string | null): void {
      projects.delete(drawerFor(project));
    },
    forgetEverything(): void {
      projects.clear();
    },
    entries(): StoredReferenceCount[] {
      const entries: StoredReferenceCount[] = [];
      const expired: Array<{ symbols: Map<string, RememberedCount>; key: string }> = [];
      for (const [project, files] of projects) {
        for (const [file, symbols] of files) {
          for (const [key, remembered] of symbols) {
            if (now() - remembered.countedAt >= options.cacheMs) {
              expired.push({ symbols, key });
              continue;
            }
            entries.push({
              project: projectFor(project),
              file,
              key,
              value: remembered.value,
              countedAt: remembered.countedAt
            });
          }
        }
      }
      for (const entry of expired) entry.symbols.delete(entry.key);
      return entries;
    },
    drawer(whichProject: () => string | null, file: string): CountMemory {
      return {
        get: (key: string) => store.get(whichProject(), file, key),
        remember: (key: string, value: CodeLensCount) =>
          store.remember(whichProject(), file, key, value),
        forget: () => store.forgetFile(whichProject(), file),
        get size(): number {
          return projects.get(drawerFor(whichProject()))?.get(file)?.size ?? 0;
        }
      };
    }
  };

  return store;
}
