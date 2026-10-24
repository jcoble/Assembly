/**
 * sourceIntelligence.ts — everything the source editor asks the machine for.
 *
 * The editor knows how to draw code, but it knows nothing about this project:
 * every lookup arrives as a callback prop. This module is the other half — it
 * owns the small amount of
 * context a lookup needs (project root, the file on screen, that file's
 * current text, the scanned file list) and answers each callback.
 *
 * Ported from the old shell's page-local handlers
 * (`src/routes/+page.svelte:8446`, `:8486`, `:8548`, `:8607`, `:10104-10214`)
 * with the constitution's rules applied:
 *
 *  - **One native authority at a time.** With Supercharged enabled, the
 *    desktop editor gets semantic reference results from the language server.
 *    With it disabled, Peek References uses the backend's bounded plain-text
 *    project scan. The old shell had a third tier that answered from bundled
 *    demo files; /next never invents results.
 *  - **Every backend call is counted** with `countInvoke('<command name>')`
 *    immediately before it, so the dev counter tells the truth.
 *  - **Nothing runs on import.** The first backend call of any kind happens
 *    when the user opens a file.
 *
 * WHERE THE "N references" NUMBER COMES FROM (ruled 2026-07-30, replacing the
 * ruling of 2026-07-28 that this comment used to carry).
 *
 * In the desktop app it comes from the language server, progressively and a
 * few symbols at a time. The targets returned while counting are retained for
 * the current file view, so clicking a finished number does not ask the same
 * language-server question a second time and the number agrees with its list.
 *
 * In the browser preview it comes from a plain-text project scan for the
 * current name. A browser has no language server, so this is the fast
 * name-match approximation the old page used before extraction. It is never
 * allowed to populate a native semantic count.
 *
 * The budgets below are load-bearing for both — see
 * `countReferencesForCodeLens`.
 */
import {
  extractSourceSymbols,
  normalizeProjectPath,
  previewFromContent,
  sourceSupportsLanguageIntelligence,
  type SourceCompletionItem,
  type SourceDefinitionTarget,
  type SourceDiagnostic,
  type SourceDocumentHighlight,
  type SourceInlayHint,
  type SourceLspHover,
  type SourceLspStatus,
  type SourcePreview,
  type SourceRecord,
  type SourceReferenceTarget,
  type SourceSemanticToken,
  type SourceSignatureHelp,
  type SourceSymbol
} from '../../../sourceData.ts';
import {
  countSourceReferencesFromTauri,
  findSourceDefinitionsInRootFromTauri,
  findSourceReferencesInRootFromTauri,
  findSourceLspCompletionsFromTauri,
  findSourceLspDefinitionsFromTauri,
  findSourceLspDocumentHighlightsFromTauri,
  findSourceLspHoverFromTauri,
  findSourceLspInlayHintsFromTauri,
  findSourceLspReferencesFromTauri,
  findSourceLspSemanticTokensFromTauri,
  findSourceLspSignatureHelpFromTauri,
  isNativeTauriRuntime,
  readSourceFromTauri,
  readSourceLspDiagnosticsFromTauri,
  readSourceLspStatusFromTauri
} from '../../../tauriSource.ts';
import { hasBackendCapability } from '../../backendCapabilities.ts';
import { countInvoke } from '../../devInvokeCounter.svelte.ts';
import { activateEditor } from './editorStore.svelte.ts';
import {
  createReferenceCountBatcher,
  createReferenceCountStore,
  createSemanticReferenceCountScheduler,
  type CodeLensCount,
  type ReferenceCountStore
} from './referenceCountBatcher.ts';
import {
  statusMessageIsAboutThisFile,
  type LanguageServerStatusMessage
} from '../components/editor/languageServerStatus.ts';
import {
  setSourceIntelligenceDiagnostics,
  trackTauriListener,
  trackTauriSubscriber
} from '../../resourceDiagnostics.svelte.ts';
import { sourceRecordFromPath } from './sourceRecordFromPath.ts';

// ── Budgets (from the old shell, except where the margin counts changed) ─────

const sourceIntelligenceDev =
  (import.meta as ImportMeta & { env?: { DEV?: boolean } }).env?.DEV === true;

/** Most definitions one lookup will return. */
export const maxSourceDefinitionResults = 20;
/**
 * Most references one "find references" click will list. The margin counts do
 * NOT use this — see `maxSourceReferenceCountResults`.
 */
export const maxSourceReferenceResults = 50;
/**
 * Most references the language server is asked to list when all we want is the
 * number. Far higher than a peek window's ceiling because a peek is something a
 * person reads and a count is not: cutting it off at fifty would put "50" above
 * a symbol used two hundred times. Should a symbol somehow exceed even this,
 * the margin says "at least" rather than stating a number nobody reached.
 */
export const maxSourceReferenceCountResults = 1_000;
/**
 * How many "how many references?" questions the language server may be working
 * on at once.
 *
 * Small on purpose. A cold server takes seconds over a single one of these, and
 * a file with a hundred symbols in it used to send all hundred at once — which
 * is what made opening a C# file look like the app had died. Four keeps the
 * numbers arriving steadily down the page while leaving the server free to
 * answer the click the reader is actually waiting on.
 */
export const semanticReferenceCountMaxInFlight = 4;
/** Most completion items one lookup will return. */
export const maxSourceCompletionResults = 50;
/**
 * The most time that one request may spend reading the project before it
 * answers with whatever it counted so far. A ceiling, not a wait — a project
 * of about four thousand files is counted in roughly 400ms and answers then.
 * Nothing on screen is blocked while it runs.
 */
export const codeLensReferenceCountDeadlineMs = 1_500;
/**
 * How long a counted number stays good for. Project-wide counts move when
 * files elsewhere change, which is not something typing in the open file does
 * — so counts must NOT be thrown away on every keystroke. That is what used to
 * leave the margin blank while a big project was being typed in.
 */
export const codeLensReferenceCountCacheMs = 24 * 60 * 60 * 1_000;
/**
 * Where the plain-text counts are filed. They are counted by NAME across the
 * whole project, so the same name has the same number in every file and they
 * all belong in one drawer — unlike the language server's numbers, which are
 * about one symbol in one file and are filed under that file.
 */
export const wholeProjectCountsDrawer = 'the whole project';
/**
 * Most symbols one file gets margin numbers for. The same ceiling the editor
 * applies to the lens rows it draws.
 */
export const maxCodeLensSymbols = 120;

/** Whether a reference-count question should run, wait, or use plain text. */
export type CountingReadiness = 'ask-it' | 'wait-for-it' | 'no-server';

/**
 * Decide what a reported server state means for one reference-count question.
 * Waiting is allowed only when the caller knows both that this server can be
 * woken and that its later status changes will reach the editor.
 */
export function countingReadinessForStatus(
  state: string | undefined,
  available: boolean | undefined,
  canWait: boolean
): CountingReadiness {
  if (state === 'ready') return 'ask-it';
  if (state === 'starting' || state === 'indexing') {
    return canWait ? 'wait-for-it' : 'no-server';
  }
  if (state === 'not-running' && available === true && canWait) return 'wait-for-it';
  return 'no-server';
}

// ── The shapes the editor hands us ───────────────────────────────────────────

/** What the editor knows about the spot the user is asking about. */
export interface SourceLookupRequest {
  symbolName: string;
  /** Whether this request came from an editor with Supercharged active. */
  languageServerEnabled?: boolean;
  /** 1-based. */
  line: number;
  /** 1-based. */
  column: number;
  /**
   * Which file the spot is in, when the editor knows. Only the margin's row
   * spots carry it, and only so that a number counted against one file can
   * never be drawn above another one; every other lookup leaves it out and is
   * resolved against the file on screen as before.
   */
  filePath?: string;
}

/** The visible range the editor wants inline hints for. */
export interface SourceInlayHintRequest {
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
}

/** The callback set handed straight to the source editor as props. */
export interface SourceIntelligenceCallbacks {
  onDefinitionLookup(request: SourceLookupRequest): Promise<SourceDefinitionTarget[]>;
  onReferenceLookup(request: SourceLookupRequest): Promise<SourceReferenceTarget[]>;
  onReferenceCountLookup(
    request: SourceLookupRequest
  ): CodeLensCount | null | Promise<CodeLensCount | null>;
  /**
   * The symbols a file's margin rows sit above, when the language server can
   * name them better than the editor's own reader of the text can. Answers
   * `null` when it cannot — the editor then falls back to reading the text,
   * which is all the browser preview has ever done.
   */
  onCodeLensAnchorLookup(preview: SourcePreview): Promise<SourceSymbol[] | null>;
  /**
   * This file was edited or saved, so the numbers counted for it are out of
   * date. Only that file's numbers are let go; every other file's stand.
   */
  onReferenceCountsOutOfDate(filePath: string): void;
  onExternalPreviewLookup(record: SourceRecord): Promise<SourcePreview | null>;
  onHoverLookup(request: SourceLookupRequest): Promise<SourceLspHover | null>;
  onCompletionLookup(request: SourceLookupRequest): Promise<SourceCompletionItem[]>;
  onDocumentHighlightLookup(request: SourceLookupRequest): Promise<SourceDocumentHighlight[]>;
  onSignatureHelpLookup(request: SourceLookupRequest): Promise<SourceSignatureHelp | null>;
  onInlayHintLookup(request: SourceInlayHintRequest): Promise<SourceInlayHint[]>;
  onSemanticTokensLookup(
    preview: SourcePreview,
    stopSignal?: AbortSignal
  ): Promise<SourceSemanticToken[]>;
}

/** The service the editor panel drives. */
export interface SourceIntelligence {
  /** Stop event subscriptions owned by this service. */
  dispose(): void;
  /** Subscribe to pushed language-server status updates. */
  subscribeToLanguageServerStatus(
    listener: (status: LanguageServerStatusMessage) => void
  ): () => void;
  /** Subscribe to semantic reference counts that land after their first paint. */
  subscribeToReferenceCountUpdates(listener: (filePath: string) => void): () => void;
  /** Project the open files belong to; language-server lookups need it. */
  setProjectRoot(projectRoot: string | null): void;
  /** The file on screen (its contents are what lookups are resolved against). */
  setActivePreview(preview: SourcePreview | null): void;
  /** The file on screen's current text — the same as the preview while read-only. */
  setDraftContent(content: string): void;
  /** Forget the remembered contents of one file (call after it is written). */
  invalidatePreview(path: string): void;
  /** Drop only a closed file's preview; its still-valid count remains warm. */
  releasePreview(path: string): void;
  /** Forget every remembered file (call when the project changes). */
  invalidateAllPreviews(): void;
  /**
   * What the language server says is wrong with the file on screen.
   *
   * PUSHED, not pulled: Monaco has no diagnostics callback, so this is not one
   * of `callbacks` — the editor panel asks for it and hands the answer down as
   * `externalDiagnostics`. Empty when there is no language server for this file,
   * when there is no file, or when the read fails.
   */
  loadActiveFileDiagnostics(): Promise<SourceDiagnostic[]>;
  /** Read-only view of the current context, for status lines and tests. */
  readonly projectRoot: string | null;
  /** The callbacks to spread onto `CodeMirrorSourceEditor`. */
  readonly callbacks: SourceIntelligenceCallbacks;
}

/**
 * Can whatever is behind this page count references in one pass?
 *
 * In the desktop app the answer is a promise the backend makes by name, because
 * a desktop build older than this page would quietly answer nothing at all
 * rather than failing — see `backendCapabilities.ts`. In the browser preview
 * there is nothing to check: the counting is served by the same dev server that
 * served this page, so the two are never out of step.
 *
 * When the answer is no, margin counts are simply not drawn. The way they were
 * worked out before — one reading of the whole project per symbol, a hundred or
 * more of them as a file opened — is what made the editor stop responding for
 * ten seconds, so it is not somewhere worth falling back to.
 */
async function backendCanCountReferences(): Promise<boolean> {
  if (!isNativeTauriRuntime()) return true;
  return hasBackendCapability('referenceCounts');
}

/**
 * Build a source-intelligence service. One per editor — and there is only
 * ever ONE editor, because Monaco registers its providers for the whole page:
 * a second editor would silently take over every lookup.
 */
export function createSourceIntelligence(): SourceIntelligence {
  let projectRoot: string | null = null;
  let projectRootGeneration = 0;
  let externalPreviewGeneration = 0;
  let activePreview: SourcePreview | null = null;
  let draftContent = '';
  /**
   * Remembered contents per file. A peek window resolves the same file once
   * per group; without this the lazy preview resolver re-reads it every time.
   * Dropped for a file when that file is written, and wholesale on a project
   * change — a stale preview after an edit would be a correctness bug.
   *
   * Each entry holds a whole file's text, and files pulled in by a peek window
   * or a reference search never become tabs, so nothing else releases them:
   * only the most recently used ones are kept.
   */
  const externalPreviewCache = new Map<string, SourcePreview>();
  const externalPreviewCacheLimit = 32;

  /**
   * Remember `preview` as the most recently used and drop the oldest beyond the
   * cap. `Map.set` on a key that is already there does not move it, so the
   * delete comes first — on a cache hit as well as on a fresh read.
   */
  function rememberExternalPreview(path: string, preview: SourcePreview): void {
    externalPreviewCache.delete(path);
    externalPreviewCache.set(path, preview);
    while (externalPreviewCache.size > externalPreviewCacheLimit) {
      const oldest = externalPreviewCache.keys().next().value;
      if (oldest === undefined) break;
      externalPreviewCache.delete(oldest);
    }
  }

  /** Does the file on screen have a language server behind it? */
  function languageIntelligenceAvailable(): boolean {
    return activePreview ? sourceSupportsLanguageIntelligence(activePreview.language) : false;
  }

  /** The file on screen, carrying its current text. */
  function previewWithDraft(): SourcePreview | null {
    return activePreview ? { ...activePreview, content: draftContent } : null;
  }

  function lookupRoot(): string {
    return projectRoot ?? '';
  }

  // ── Tier 1: the language server ────────────────────────────────────────────

  async function lspDefinitions(
    request: SourceLookupRequest
  ): Promise<SourceDefinitionTarget[] | null> {
    const preview = previewWithDraft();
    if (request.languageServerEnabled === false || !preview || !languageIntelligenceAvailable()) {
      return null;
    }
    countInvoke('find_source_lsp_definitions');
    try {
      return await findSourceLspDefinitionsFromTauri(preview, {
        root: lookupRoot(),
        line: request.line,
        column: request.column,
        limit: maxSourceDefinitionResults
      });
    } catch {
      return null;
    }
  }

  /** See `SourceIntelligence.loadActiveFileDiagnostics`. */
  async function loadActiveFileDiagnostics(): Promise<SourceDiagnostic[]> {
    const preview = previewWithDraft();
    if (!preview || !languageIntelligenceAvailable()) return [];
    try {
      countInvoke('read_source_lsp_diagnostics');
      return (
        (await readSourceLspDiagnosticsFromTauri(preview, {
          root: lookupRoot(),
          line: 1,
          column: 1
        })) ?? []
      );
    } catch {
      return [];
    }
  }

  // ── The callbacks ──────────────────────────────────────────────────────────

  async function findDefinitions(
    request: SourceLookupRequest
  ): Promise<SourceDefinitionTarget[]> {
    const symbolName = request.symbolName.trim();
    if (!symbolName) return [];
    try {
      const lspTargets = await lspDefinitions(request);
      if (lspTargets?.length) return lspTargets;
      if (!isNativeTauriRuntime() || !projectRoot) return [];
      countInvoke('find_source_definitions_in_root');
      return (
        (await findSourceDefinitionsInRootFromTauri(
          projectRoot,
          symbolName,
          maxSourceDefinitionResults
        )) ?? []
      );
    } catch {
      return [];
    }
  }

  async function findReferences(request: SourceLookupRequest): Promise<SourceReferenceTarget[]> {
    const symbolName = request.symbolName.trim();
    if (!symbolName) return [];
    try {
      if (request.languageServerEnabled === false) {
        if (!isNativeTauriRuntime() || !projectRoot) return [];
        countInvoke('find_source_references_in_root');
        return (
          (await findSourceReferencesInRootFromTauri(
            projectRoot,
            symbolName,
            maxSourceReferenceResults
          )) ?? []
        );
      }

      // A finished semantic count already obtained the exact targets for this
      // spot. Reuse them instead of sending the same request to the language
      // server again — this is what makes a displayed count clickable now.
      if (request.filePath) {
        const cached = semanticTargetsFor(request);
        if (cached) return cached;
      }

      const preview = previewWithDraft();
      const root = projectRoot;
      if (preview && languageIntelligenceAvailable()) {
        // A CodeLens click and its margin count are the same semantic question.
        // Share the one references response instead of asking Roslyn once
        // for the number and again for the Peek rows.
        const semanticRequest = resolveSemanticReferences(preview, request, root);
        const semantic = await semanticRequest;
        if (semantic) return semantic.targets;
      }

      if (isNativeTauriRuntime() && root) {
        countInvoke('find_source_references_in_root');
        return (
          (await findSourceReferencesInRootFromTauri(
            root,
            symbolName,
            maxSourceReferenceResults
          )) ?? []
        );
      }
      return [];
    } catch {
      return [];
    }
  }

  // ── Margin counts ──────────────────────────────────────────────────────────
  //
  // Two machines can answer "how many references?", and which one does depends
  // entirely on whether there is a language server behind this page. See the
  // note at the top of the file for the ruling. Both keep their live answer in
  // the one in-memory store below while this file owns the editor view.

  /**
   * The one place a counted number is remembered, for either machine. The
   * store is cleared when the active file or project changes, so no answer
   * outlives the view that owns it.
   */
  const countStore: ReferenceCountStore = createReferenceCountStore({
    cacheMs: codeLensReferenceCountCacheMs
  });

  /** The plain-text search over the project: one immediate pass per name. */
  const referenceCountBatcher = isNativeTauriRuntime()
    ? null
    : createReferenceCountBatcher({
        memory: countStore.drawer(() => projectRoot, wholeProjectCountsDrawer),
        // Uncapped: the count is an exact number the backend worked out in one
        // pass, so there is no reason to round it off to "50+". The old ceiling
        // existed because each count cost its own reading of the project and was
        // stopped early to keep that affordable.
        maxCount: Number.POSITIVE_INFINITY,
        async countReferences(symbolNames: string[]) {
          if (!projectRoot) return null;
          if (!(await backendCanCountReferences())) return null;
          countInvoke('count_source_references');
          const startedAt = Date.now();
          const result = await countSourceReferencesFromTauri(
            projectRoot,
            symbolNames,
            codeLensReferenceCountDeadlineMs
          );
          reportLensTiming(
            `read the project for ${symbolNames.length} name(s) in ${Date.now() - startedAt}ms`
          );
          return result;
        }
      });

  /**
   * A symbol whose number the language server has been, or is about to be,
   * asked for. It stays here until the answer lands or the reader moves on.
   */
  interface WaitingSpot {
    request: SourceLookupRequest;
    /** The file as it was when the question was asked. */
    preview: SourcePreview;
    /** The project it belonged to, even if the reader switches before it lands. */
    projectRoot: string | null;
    askedAt: number;
  }

  const waitingSpots = new Map<string, WaitingSpot>();
  const referenceCountSubscribers = new Set<(filePath: string) => void>();
  /** Exact semantic targets retained by the count that produced the number. */
  const rememberedSemanticTargets = new Map<string, SourceReferenceTarget[]>();
  /** One in-flight semantic answer shared by the margin and a Peek click. */
  const semanticReferenceRequests = new Map<
    string,
    Promise<{ count: CodeLensCount; targets: SourceReferenceTarget[] } | null>
  >();
  /** Bumps when the active file/view changes so late answers cannot repopulate it. */
  let referenceAnswerGeneration = 0;

  /**
   * Symbols whose questions are held back because the language server is still
   * starting up or still reading the project. They go out the moment it says it
   * is ready; a finished count then asks the active editor to repaint from the
   * remembered answer.
   */
  let heldUntilServerIsReady: string[] = [];

  function publishResourceDiagnostics(): void {
    if (!sourceIntelligenceDev) return;
    setSourceIntelligenceDiagnostics({
      semanticWaitingSpots: waitingSpots.size,
      semanticSchedulerWaiting: semanticScheduler.waiting,
      semanticSchedulerInFlight: semanticScheduler.inFlight,
      semanticReferenceRequests: semanticReferenceRequests.size,
      semanticRetryTimers: 0,
      semanticHeldUntilReady: heldUntilServerIsReady.length,
      semanticRememberedTargets: rememberedSemanticTargets.size,
      sourcePreviewCacheEntries: externalPreviewCache.size,
      rememberedReferenceCountEntries: countStore.entries().length
    });
  }

  const semanticScheduler = createSemanticReferenceCountScheduler({
    maxInFlight: semanticReferenceCountMaxInFlight,
    async countFor(key: string) {
      const spot = waitingSpots.get(key);
      if (!spot) return null;
      return askLanguageServerToCount(spot);
    },
    onCounted(key: string, count: CodeLensCount | null) {
      const spot = waitingSpots.get(key);
      if (!spot) return;
      waitingSpots.delete(key);
      publishResourceDiagnostics();
      reportLensTiming(
        `${spot.request.symbolName}: ${
          count ? `${count.count} reference(s)` : 'no answer'
        } after ${Date.now() - spot.askedAt}ms`
      );
      if (count) publishReferenceCountUpdate(spot.preview.path);
    }
  });

  function publishReferenceCountUpdate(filePath: string): void {
    for (const listener of referenceCountSubscribers) listener(filePath);
  }

  /**
   * Which line, which name. The file is not in here because the store already
   * files a number under its file, and the column is left out because text
   * typed earlier on the line moves the symbol sideways without changing how
   * many places use it.
   */
  function countKeyFor(request: SourceLookupRequest): string {
    return `${request.line}:${request.symbolName}`;
  }

  function semanticTargetKey(
    request: SourceLookupRequest,
    root: string | null = projectRoot
  ): string {
    const filePath = normalizeProjectPath(request.filePath ?? activePreview?.path ?? '');
    return `${root ?? ''}|${filePath}|${countKeyFor(request)}`;
  }

  function semanticTargetsFor(request: SourceLookupRequest): SourceReferenceTarget[] | null {
    return rememberedSemanticTargets.get(semanticTargetKey(request)) ?? null;
  }

  async function readSemanticReferencesAnswer(
    generation: number,
    preview: SourcePreview,
    request: SourceLookupRequest,
    root: string | null,
    filePath: string,
    resultKey: string
  ): Promise<{ count: CodeLensCount; targets: SourceReferenceTarget[] } | null> {
    try {
      let targets: SourceReferenceTarget[] | null;
      const language = preview.language.toLowerCase();
      if (language === 'csharp' || language === 'c#') {
        const { findCsharpReferenceLocations } = await import('./csharpLanguageClient.ts');
        const locations = await findCsharpReferenceLocations(
          root ?? '',
          preview.path,
          request.line,
          request.column,
          maxSourceReferenceCountResults
        );
        const previewPath = normalizeProjectPath(preview.path);
        const previewLines = preview.content.split('\n');
        targets = locations?.map((location) => ({
          ...sourceRecordFromPath(root, location.path),
          symbolName: request.symbolName,
          line: location.line,
          column: location.column,
          excerpt: normalizeProjectPath(location.path) === previewPath
            ? previewLines[location.line - 1] ?? ''
            : ''
        })) ?? null;
      } else {
        countInvoke('find_source_lsp_references');
        try {
          targets = await findSourceLspReferencesFromTauri(preview, {
            root: root ?? '',
            line: request.line,
            column: request.column,
            limit: maxSourceReferenceCountResults
          });
        } catch {
          targets = null;
        }
      }
      if (!targets) return null;
      if (generation !== referenceAnswerGeneration) return null;

      const uniqueUses = new Map<string, SourceReferenceTarget>();
      for (const target of targets) {
        const targetPath = normalizeProjectPath(target.path);
        if (target.line === request.line && targetPath === filePath) continue;
        const locationKey = `${targetPath}:${target.line}:${target.column}`;
        if (!uniqueUses.has(locationKey)) uniqueUses.set(locationKey, target);
      }

      const uses = [...uniqueUses.values()];
      const count = {
        count: uses.length,
        atLeast: targets.length >= maxSourceReferenceCountResults
      };
      const peekTargets = uses.slice(0, maxSourceReferenceResults);

      // Publish the number and its locations together. Any later CodeLens
      // repaint or Peek click now observes one coherent language-server answer.
      rememberedSemanticTargets.set(resultKey, peekTargets);
      countStore.remember(root, filePath, countKeyFor(request), count);
      return { count, targets: peekTargets };
    } finally {
      if (generation === referenceAnswerGeneration) {
        semanticReferenceRequests.delete(resultKey);
        publishResourceDiagnostics();
      }
    }
  }

  /**
   * Resolve one symbol through the language server exactly once.
   *
   * `textDocument/references` is a superset of a resolved CodeLens answer: the
   * returned locations give us both the number to paint and the rows to open.
   * A resolved server CodeLens only gives us the title; asking
   * `textDocument/references` once gives both the exact count and the Peek rows.
   */
  async function resolveSemanticReferences(
    preview: SourcePreview,
    request: SourceLookupRequest,
    root: string | null
  ): Promise<{ count: CodeLensCount; targets: SourceReferenceTarget[] } | null> {
    const generation = referenceAnswerGeneration;
    const filePath = normalizeProjectPath(preview.path);
    const requestWithFile = { ...request, filePath: preview.path };
    const resultKey = semanticTargetKey(requestWithFile, root);
    const rememberedTargets = rememberedSemanticTargets.get(resultKey);
    const rememberedCount = countStore.get(root, filePath, countKeyFor(request));
    if (rememberedTargets && rememberedCount) {
      return { count: rememberedCount, targets: rememberedTargets };
    }

    const pending = semanticReferenceRequests.get(resultKey);
    if (pending) return await pending;

    const answer = readSemanticReferencesAnswer(
      generation,
      preview,
      request,
      root,
      filePath,
      resultKey
    );

    semanticReferenceRequests.set(resultKey, answer);
    publishResourceDiagnostics();
    return await answer;
  }

  /**
   * Ask the language server how many places use this exact symbol, and turn the
   * list it answers with into a number.
   *
   * The declaration itself is dropped when the server includes it, so the
   * number means "used in N places" the way it does in every other editor. A
   * symbol that calls itself keeps those uses: only the line the margin row
   * sits on is taken out.
   */
  async function askLanguageServerToCount(spot: WaitingSpot): Promise<CodeLensCount | null> {
    const answer = await resolveSemanticReferences(
      spot.preview,
      spot.request,
      spot.projectRoot
    );
    return answer?.count ?? null;
  }

  /**
   * Is the language server for the file on screen in a position to count?
   *
   *  - `ask-it`      — it is running and has finished reading the project.
   *  - `wait-for-it` — it is still starting up or still reading. The margin
   *                    keeps saying it is counting, and the questions go out
   *                    when it tells us it is ready.
   *  - `no-server`   — there is none, it is switched off, or this is the
   *                    browser preview. Only the browser uses plain text;
   *                    native leaves the count unanswered.
   *
   * A desktop build too old to say what state it is in is treated as
   * `no-server` on purpose rather than silently substituting name matches for
   * semantic results.
   */
  /**
   * The last answer per project and language, and when it was given.
   *
   * A file's margin rows are asked about in a burst — twenty at once is
   * ordinary — and asking the app what the language server is doing twenty
   * times over would be its own little storm. One answer serves the burst, and
   * a status change throws it away immediately.
   */
  const rememberedReadiness = new Map<string, { at: number; answer: Promise<CountingReadiness> }>();
  /** How long one reading of the server's state is reused for. */
  const readinessMemoryMs = 3_000;

  async function languageServerCountingReadiness(
    preview: SourcePreview
  ): Promise<CountingReadiness> {
    if (!isNativeTauriRuntime()) return 'no-server';
    if (!sourceSupportsLanguageIntelligence(preview.language)) return 'no-server';
    if (!projectRoot) return 'no-server';

    const memoKey = `${projectRoot}|${preview.language}`;
    const remembered = rememberedReadiness.get(memoKey);
    if (remembered) {
      if (Date.now() - remembered.at < readinessMemoryMs) return remembered.answer;
      rememberedReadiness.delete(memoKey);
    }

    const answer = readLanguageServerCountingReadiness(projectRoot, preview.language);
    rememberedReadiness.set(memoKey, { at: Date.now(), answer });
    return answer;
  }

  async function readLanguageServerCountingReadiness(
    root: string,
    language: SourcePreview['language']
  ): Promise<CountingReadiness> {
    countInvoke('read_source_lsp_status');
    let status: SourceLspStatus | null;
    try {
      status = await readSourceLspStatusFromTauri(root, language);
    } catch {
      status = null;
    }
    const reported = status as
      | (SourceLspStatus & { state?: unknown; available?: unknown })
      | null;
    const state = typeof reported?.state === 'string' ? reported.state : undefined;
    const available =
      typeof reported?.available === 'boolean' ? reported.available : undefined;
    const mightWait =
      state === 'starting' ||
      state === 'indexing' ||
      (state === 'not-running' && available === true);
    if (!mightWait) return countingReadinessForStatus(state, available, false);

    // Waiting is honest only because the document-symbols request for this same
    // file is already on its way to start or move the server, and a later status
    // update will release these questions. Without either ability, the margin
    // would stay at the zero placeholder for the rest of the session.
    const canWatch = await watchLanguageServerStatus();
    const canWake = canWatch && (await hasBackendCapability('lspDocumentSymbols'));
    return countingReadinessForStatus(state, available, canWake);
  }

  /** Set up once we first need it; answers whether the app can tell us. */
  let statusWatch: Promise<boolean> | null = null;
  let stopStatusWatch: (() => void) | null = null;
  let statusWatchGeneration = 0;
  const statusSubscribers = new Set<(status: LanguageServerStatusMessage) => void>();

  async function startLanguageServerStatusWatch(generation: number): Promise<boolean> {
    if (!(await hasBackendCapability('lspStatusEvents'))) return false;
    try {
      const { listen } = await import('@tauri-apps/api/event');
      const stopStatusEvents = await listen<LanguageServerStatusMessage>(
        'source-lsp-status-changed',
        (event) => {
          // Whatever we last worked out about the server is now out of date.
          rememberedReadiness.clear();
          if (
            event.payload?.state === 'ready' &&
            statusMessageIsAboutThisFile(
              event.payload,
              projectRoot,
              activePreview?.language ?? null
            )
          ) {
            releaseHeldQuestions();
          }
          for (const subscriber of statusSubscribers) subscriber(event.payload);
        }
      );
      const stop = trackTauriListener(stopStatusEvents);
      if (generation !== statusWatchGeneration) {
        stop();
        return false;
      }
      stopStatusWatch = stop;
      return true;
    } catch {
      return false;
    }
  }

  async function watchLanguageServerStatus(): Promise<boolean> {
    const generation = statusWatchGeneration;
    statusWatch ??= startLanguageServerStatusWatch(generation);
    return await statusWatch;
  }

  function releaseHeldQuestions(): void {
    if (heldUntilServerIsReady.length === 0) return;
    const released = heldUntilServerIsReady;
    heldUntilServerIsReady = [];
    reportLensTiming(`the language server is ready — asking about ${released.length} symbol(s)`);
    semanticScheduler.request(released);
  }

  /**
   * The "N references" number drawn above a symbol.
   *
   * `null` means we cannot count this one right now. Semantic counts publish a
   * repaint when their remembered answer lands.
   */
  async function countReferencesForCodeLens(
    request: SourceLookupRequest
  ): Promise<CodeLensCount | null> {
    const symbolName = request.symbolName.trim();
    if (!symbolName) return null;

    const preview = previewWithDraft();
    const spotRequest: SourceLookupRequest = { ...request, symbolName };
    const key = countKeyFor(spotRequest);

    if (preview) {
      // The spot belongs to a file other than the one on screen — which is the
      // only file anything here can count against. Better to show no number
      // than one worked out for the wrong file.
      if (
        request.filePath &&
        normalizeProjectPath(request.filePath) !== normalizeProjectPath(preview.path)
      ) {
        return null;
      }

      const filePath = normalizeProjectPath(preview.path);
      const remembered = countStore.get(projectRoot, filePath, key);
      if (remembered) return remembered;
    }

    if (preview) {
      return await countUnrememberedSemanticReferences(key, spotRequest, preview);
    }

    // Browser preview: one project scan answers every name in the file. The
    // batcher is deliberately absent from native editor instances.
    return referenceCountBatcher ? await referenceCountBatcher.count(symbolName) : null;
  }

  async function countUnrememberedSemanticReferences(
    key: string,
    spotRequest: SourceLookupRequest,
    preview: SourcePreview
  ): Promise<CodeLensCount | null> {
    const readiness = await languageServerCountingReadiness(preview);
    if (readiness !== 'no-server') {
      // The server turned ready between one symbol and the next: whatever was
      // held back while it was starting goes out now, so the whole file's rows
      // fill in rather than the ones asked about after it woke up.
      if (readiness === 'ask-it') releaseHeldQuestions();
      return countBySemantics(key, spotRequest, preview, readiness === 'wait-for-it');
    }

    // Browser preview: one project scan answers every name in the file.
    return referenceCountBatcher ? await referenceCountBatcher.count(spotRequest.symbolName) : null;
  }

  /** Put this symbol in the language server's queue; repaint follows by subscription. */
  function countBySemantics(
    key: string,
    request: SourceLookupRequest,
    preview: SourcePreview,
    holdBack: boolean
  ): CodeLensCount | null {
    if (waitingSpots.has(key)) return null;

    waitingSpots.set(key, {
      request,
      preview,
      projectRoot,
      askedAt: Date.now()
    });

    if (holdBack) {
      heldUntilServerIsReady.push(key);
    } else {
      semanticScheduler.request([key]);
    }
    publishResourceDiagnostics();
    return null;
  }

  /**
   * Stop counting for the file that was on screen. Everything still queued is
   * dropped; live answers and targets are cleared so none can outlive the view.
   */
  function stopCountingForTheOldFile(): void {
    referenceAnswerGeneration += 1;
    semanticScheduler.clear();
    referenceCountBatcher?.forget();
    heldUntilServerIsReady = [];
    waitingSpots.clear();
    semanticReferenceRequests.clear();
    rememberedSemanticTargets.clear();
    countStore.forgetEverything();
    publishResourceDiagnostics();
  }

  /** Let go of every live answer when the source projection resets. */
  function forgetReferenceCounts(): void {
    referenceAnswerGeneration += 1;
    referenceCountBatcher?.forget();
    semanticReferenceRequests.clear();
    countStore.forgetEverything();
    rememberedSemanticTargets.clear();
    publishResourceDiagnostics();
  }

  /**
   * One file was edited or saved, so its numbers are counted again.
   *
   * Only the language server's numbers, and only for that file: they are about
   * this file's symbols and editing it can genuinely change them. The
   * plain-text counts are left alone on purpose. They count a NAME across the
   * project's saved files, which typing in the open buffer does not change, and
   * throwing them away every time the reader paused would put the browser
   * preview back to reading the whole project every couple of seconds.
   */
  function forgetFileReferenceCounts(filePath: string): void {
    if (!filePath) return;
    const normalizedPath = normalizeProjectPath(filePath);
    if (normalizeProjectPath(activePreview?.path ?? '') === normalizedPath) {
      referenceAnswerGeneration += 1;
      semanticReferenceRequests.clear();
    }
    countStore.forgetFile(projectRoot, normalizedPath);
    const prefix = `${projectRoot ?? ''}|${normalizedPath}|`;
    for (const key of rememberedSemanticTargets.keys()) {
      if (key.startsWith(prefix)) rememberedSemanticTargets.delete(key);
    }
    publishResourceDiagnostics();
  }

  /**
   * The symbols a file's margin rows sit above.
   *
   * The editor can find these itself by reading the text, but it only knows how
   * to read TypeScript, JavaScript and C#. The language server knows the shape
   * of whatever it is looking at, which is what puts margin rows on Rust and
   * Svelte files for the first time. When the desktop app is too old to answer
   * this question, or answers with nothing, we say so and the editor reads the
   * text as before.
   */
  async function lookupCodeLensAnchors(asked: SourcePreview): Promise<SourceSymbol[] | null> {
    if (!isNativeTauriRuntime() || !projectRoot) return null;

    // Ask about the file on screen, not about the copy the editor handed us.
    // The preview is the authoritative source of the workspace language id.
    const preview = activePreview;
    if (!preview) return null;
    if (normalizeProjectPath(asked.path) !== normalizeProjectPath(preview.path)) return null;
    if (!sourceSupportsLanguageIntelligence(preview.language)) return null;
    const language = preview.language.toLowerCase();
    if (language === 'csharp' || language === 'c#') {
      return extractSourceSymbols(asked, asked.content).slice(0, maxCodeLensSymbols);
    }
    if (!(await hasBackendCapability('lspDocumentSymbols'))) return null;

    try {
      const { invoke } = await import('@tauri-apps/api/core');
      countInvoke('find_source_lsp_document_symbols');
      const startedAt = Date.now();
      const symbols = await invoke<
        { name: string; kind: string; line: number; character: number }[]
      >('find_source_lsp_document_symbols', {
        root: projectRoot,
        language: preview.language,
        path: preview.path
      });
      if (!Array.isArray(symbols) || symbols.length === 0) return null;

      reportLensTiming(
        `the language server named ${symbols.length} symbol(s) in ${preview.fileName} in ${
          Date.now() - startedAt
        }ms`
      );
      // The backend counts lines and characters from zero; everything on this
      // side of the wire counts from one.
      return symbols.slice(0, maxCodeLensSymbols).map((symbol) => ({
        name: symbol.name,
        kind: String(symbol.kind).toLowerCase(),
        line: symbol.line + 1,
        column: symbol.character + 1,
        detail: symbol.name
      }));
    } catch {
      return null;
    }
  }

  /**
   * One line about how the margin numbers are getting on, when somebody has
   * asked to see them. Switch it on from the browser console with
   * `window.mcbShowLensTiming = true`; it is off for everyone else.
   */
  function reportLensTiming(sentence: string): void {
    if ((globalThis as { mcbShowLensTiming?: unknown }).mcbShowLensTiming !== true) return;
    console.info(`Reference counts: ${sentence}`);
  }

  /**
   * Contents of some OTHER file — what a peek window shows for each hit, and
   * what "jump to definition" previews. Answers from the file on screen or
   * from memory when it can, and reads from disk at most once per file.
   */
  async function loadExternalPreview(record: SourceRecord): Promise<SourcePreview | null> {
    const requestRoot = projectRoot;
    const generation = projectRootGeneration;
    const previewGeneration = externalPreviewGeneration;
    const sourceRecord = sourceRecordFromPath(requestRoot, record.path);

    if (activePreview?.path === sourceRecord.path) {
      return previewFromContent(
        { ...sourceRecord, byteCount: new TextEncoder().encode(draftContent).length },
        draftContent
      );
    }

    const cached = externalPreviewCache.get(sourceRecord.path);
    if (cached) {
      rememberExternalPreview(sourceRecord.path, cached);
      return cached;
    }

    countInvoke('read_source_file');
    let preview: SourcePreview | null;
    try {
      preview = await readSourceFromTauri(sourceRecord);
    } catch {
      preview = null;
    }
    if (
      generation !== projectRootGeneration
      || previewGeneration !== externalPreviewGeneration
      || projectRoot !== requestRoot
    ) return null;
    if (preview) rememberExternalPreview(sourceRecord.path, preview);
    return preview;
  }

  async function lookupHover(request: SourceLookupRequest): Promise<SourceLspHover | null> {
    const preview = previewWithDraft();
    if (!preview || !languageIntelligenceAvailable()) return null;
    try {
      countInvoke('find_source_lsp_hover');
      return await findSourceLspHoverFromTauri(preview, {
        root: lookupRoot(),
        line: request.line,
        column: request.column
      });
    } catch {
      return null;
    }
  }

  async function lookupCompletions(
    request: SourceLookupRequest
  ): Promise<SourceCompletionItem[]> {
    const preview = previewWithDraft();
    if (!preview || !languageIntelligenceAvailable()) return [];
    try {
      countInvoke('find_source_lsp_completions');
      return (
        (await findSourceLspCompletionsFromTauri(preview, {
          root: lookupRoot(),
          line: request.line,
          column: request.column,
          limit: maxSourceCompletionResults
        })) ?? []
      );
    } catch {
      return [];
    }
  }

  async function lookupDocumentHighlights(
    request: SourceLookupRequest
  ): Promise<SourceDocumentHighlight[]> {
    const preview = previewWithDraft();
    if (!preview || !languageIntelligenceAvailable()) return [];
    try {
      countInvoke('find_source_lsp_document_highlights');
      return (
        (await findSourceLspDocumentHighlightsFromTauri(preview, {
          root: lookupRoot(),
          line: request.line,
          column: request.column,
          limit: 100
        })) ?? []
      );
    } catch {
      return [];
    }
  }

  async function lookupSignatureHelp(
    request: SourceLookupRequest
  ): Promise<SourceSignatureHelp | null> {
    const preview = previewWithDraft();
    if (!preview || !languageIntelligenceAvailable()) return null;
    try {
      countInvoke('find_source_lsp_signature_help');
      return await findSourceLspSignatureHelpFromTauri(preview, {
        root: lookupRoot(),
        line: request.line,
        column: request.column
      });
    } catch {
      return null;
    }
  }

  async function lookupInlayHints(request: SourceInlayHintRequest): Promise<SourceInlayHint[]> {
    const preview = previewWithDraft();
    if (!preview || !languageIntelligenceAvailable()) return [];
    try {
      countInvoke('find_source_lsp_inlay_hints');
      return (
        (await findSourceLspInlayHintsFromTauri(preview, {
          root: lookupRoot(),
          line: request.startLine,
          column: request.startColumn,
          limit: 200
        })) ?? []
      );
    } catch {
      return [];
    }
  }

  /** Colouring for the file the editor is about to paint (its own copy of it). */
  async function lookupSemanticTokens(
    preview: SourcePreview,
    stopSignal?: AbortSignal
  ): Promise<SourceSemanticToken[]> {
    if (stopSignal?.aborted || !sourceSupportsLanguageIntelligence(preview.language)) return [];
    try {
      countInvoke('find_source_lsp_semantic_tokens');
      const tokens = (
        (await findSourceLspSemanticTokensFromTauri(preview, {
          root: lookupRoot(),
          line: 1,
          column: 1,
          limit: 5000
        })) ?? []
      );
      return stopSignal?.aborted ? [] : tokens;
    } catch {
      return [];
    }
  }

  const callbacks: SourceIntelligenceCallbacks = {
    onDefinitionLookup: findDefinitions,
    onReferenceLookup: findReferences,
    onReferenceCountLookup: countReferencesForCodeLens,
    onCodeLensAnchorLookup: lookupCodeLensAnchors,
    onReferenceCountsOutOfDate: forgetFileReferenceCounts,
    onExternalPreviewLookup: loadExternalPreview,
    onHoverLookup: lookupHover,
    onCompletionLookup: lookupCompletions,
    onDocumentHighlightLookup: lookupDocumentHighlights,
    onSignatureHelpLookup: lookupSignatureHelp,
    onInlayHintLookup: lookupInlayHints,
    onSemanticTokensLookup: lookupSemanticTokens
  };

  return {
    dispose(): void {
      statusWatchGeneration += 1;
      stopStatusWatch?.();
      stopStatusWatch = null;
      statusWatch = null;
      statusSubscribers.clear();
      referenceCountSubscribers.clear();
      projectRootGeneration += 1;
      externalPreviewGeneration += 1;
      rememberedReadiness.clear();
      externalPreviewCache.clear();
      stopCountingForTheOldFile();
    },
    subscribeToLanguageServerStatus(
      listener: (status: LanguageServerStatusMessage) => void
    ): () => void {
      statusSubscribers.add(listener);
      if (isNativeTauriRuntime()) void watchLanguageServerStatus();
      return trackTauriSubscriber(() => statusSubscribers.delete(listener));
    },
    subscribeToReferenceCountUpdates(listener: (filePath: string) => void): () => void {
      referenceCountSubscribers.add(listener);
      return trackTauriSubscriber(() => referenceCountSubscribers.delete(listener));
    },
    setProjectRoot(nextProjectRoot: string | null): void {
      const normalized =
        nextProjectRoot && nextProjectRoot.trim().length > 0 ? nextProjectRoot : null;
      if (normalized === projectRoot) return;
      projectRootGeneration += 1;
      projectRoot = normalized;
      rememberedReadiness.clear();
      // The old project's live answers belong to the view that just left it.
      // Stop that work and release those answers before pointing at the new root.
      externalPreviewCache.clear();
      stopCountingForTheOldFile();
    },
    setActivePreview(preview: SourcePreview | null): void {
      if (preview?.path !== activePreview?.path) stopCountingForTheOldFile();
      activePreview = preview;
      draftContent = preview?.content ?? '';
    },
    setDraftContent(content: string): void {
      draftContent = content;
    },
    invalidatePreview(path: string): void {
      externalPreviewGeneration += 1;
      externalPreviewCache.delete(path);
      forgetFileReferenceCounts(path);
    },
    releasePreview(path: string): void {
      externalPreviewGeneration += 1;
      externalPreviewCache.delete(path);
    },
    invalidateAllPreviews(): void {
      externalPreviewGeneration += 1;
      externalPreviewCache.clear();
      forgetReferenceCounts();
    },
    loadActiveFileDiagnostics,
    get projectRoot(): string | null {
      return projectRoot;
    },
    callbacks
  };
}

/**
 * The shell's one source-intelligence service. It is a module value because
 * there is exactly one code editor in the shell (Monaco's providers are
 * page-wide), and because the integrator needs a stable handle to call
 * `activate` on before the editor panel has ever been shown.
 */
export const sourceIntelligence = createSourceIntelligence();

/**
 * Integration seam: called when the editor tab is first shown, with the
 * project root of the active session. Idempotent, and does NO backend work —
 * it only tells the panel and the lookups which project to ask about. Reading
 * a file happens when the user opens one.
 */
export function activate(projectRoot?: string | null): void {
  sourceIntelligence.setProjectRoot(projectRoot ?? null);
  activateEditor(projectRoot ?? null);
}

/** Forget one file's remembered contents (call after that file is written). */
export function invalidatePreview(path: string): void {
  sourceIntelligence.invalidatePreview(path);
}

/** What came back from turning the C# language server off or on. */
export interface CsharpLanguageServerToggleResult {
  /** Whether the server is allowed to run now. */
  enabled: boolean;
  /** How many running servers were stopped (0 when it was not running). */
  stoppedServers: number;
  /** A whole sentence to show the reader. */
  message: string;
  /** False when this build of the app has no such switch; `message` says so. */
  supported: boolean;
}

export type LanguageServerId = 'csharp' | 'typescript' | 'rust';

export async function setLanguageServerEnabled(
  language: LanguageServerId,
  enabled: boolean
): Promise<CsharpLanguageServerToggleResult> {
  const unsupported: CsharpLanguageServerToggleResult = {
    enabled: !enabled,
    stoppedServers: 0,
    supported: false,
    message: 'This build of the app cannot configure individual language servers yet.'
  };
  if (!isNativeTauriRuntime()) return unsupported;
  try {
    const { invoke } = await import('@tauri-apps/api/core');
    countInvoke('set_language_server_enabled');
    const result = await invoke<Omit<CsharpLanguageServerToggleResult, 'supported'>>(
      'set_language_server_enabled',
      { language, enabled }
    );
    return { ...result, supported: true };
  } catch {
    return unsupported;
  }
}

/**
 * Turn every language server off or on. Off stops each one that is running,
 * every language and every workspace. Call it on start-up with the saved
 * setting as well as when the switch is flipped: the desktop app forgets
 * between launches and starts with servers allowed.
 */
export async function setLanguageServersEnabled(
  enabled: boolean
): Promise<CsharpLanguageServerToggleResult> {
  const unsupported: CsharpLanguageServerToggleResult = {
    enabled: true,
    stoppedServers: 0,
    supported: false,
    message:
      'This build of the app cannot do this yet — restart the desktop app after updating.'
  };
  if (!isNativeTauriRuntime()) return unsupported;
  if (!(await hasBackendCapability('languageServersToggle'))) return unsupported;
  try {
    const { invoke } = await import('@tauri-apps/api/core');
    countInvoke('set_language_servers_enabled');
    const result = await invoke<Omit<CsharpLanguageServerToggleResult, 'supported'>>(
      'set_language_servers_enabled',
      { enabled }
    );
    return { ...result, supported: true };
  } catch {
    return {
      enabled: !enabled,
      stoppedServers: 0,
      supported: false,
      message: 'The language servers setting could not be changed just now. Please try again.'
    };
  }
}

/**
 * Turn the C# language server off or on.
 *
 * The C# server is by far the most expensive thing the app starts — around
 * 800MB once it has read a large solution — and a reader who is not writing C#
 * gets nothing back for it. Switching it off keeps the margin counts and the
 * project-wide search working, because those read the files directly; what goes
 * away is the squiggles under mistakes and the precision of go-to-definition on
 * an overloaded name.
 *
 * Call this on start-up with the reader's saved setting as well as when the
 * switch is flipped: the desktop app forgets between launches and starts the
 * server allowed.
 */
