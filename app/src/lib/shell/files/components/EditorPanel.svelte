<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import { assertSaveAllowed, parseRemoteWorkspacePath, workspaceChangePath } from '$lib/workspacePaths';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte';
  /**
   * EditorPanel.svelte — the /next code-reading panel.
   *
   * Thin by construction. It owns three things and nothing else:
   *  1. the open-file actions the top tab row calls (select, close, timeline),
   *  2. reading a file when one is requested, and
   *  3. handing `CodeMirrorSourceEditor` the lookup callbacks from
   *     `sourceIntelligence`.
   *
   * Rules it exists to keep:
   *  - **One editor, ever.** Switching files swaps the document inside one
   *    CodeMirror view, so inactive tabs hold only their saved text and view state.
   *  - **Nothing loads at start-up.** The panel subscribes to open-file
   *    requests when it mounts (free, no backend), and CodeMirror itself is only
   *    downloaded once a file is on screen in front of the reader — not merely
   *    in the top row, because putting a session's files back fills the row out
   *    of sight. The language server is warmed on the first file opened per
   *    project, never before.
   *  - **Read mode is the default.** Opening a file colours it and stops
   *    there. A language server starts only for a project whose switch in the
   *    editor status bar has been turned on, and turning it off stops that server. The
   *    global choice is remembered between launches.
   *  - **No `$effect` reads a file.** Every read is started by a user action: a
   *    file-open request, or a click on a top-row tab. The one effect that starts
   *    anything starts the editor, and only for a file already on screen.
   */
  import { onMount } from 'svelte';
  import Save from '@lucide/svelte/icons/save';
  import X from '@lucide/svelte/icons/x';
  import Ellipsis from '@lucide/svelte/icons/ellipsis';
  import Folder from '@lucide/svelte/icons/folder';
  import * as AlertDialog from '$lib/components/ui/alert-dialog/index.js';
  import * as Breadcrumb from '$lib/components/ui/breadcrumb/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';

  import { upgradeUnknownLanguage } from './editor/editorLanguage.ts';
  import {
    createLanguageServerGate,
    readLanguageServerState,
    statusMessageIsAboutThisFile
  } from './editor/languageServerStatus.ts';
  import FileIcon from '../../components/explorer/FileIcon.svelte';
  import { canonicalPath } from '$lib/shell/files/explorer/explorerStore.svelte';
  import {
    isHtmlFile,
    isMarkdownFile,
    markdownPreviewDefault,
    rasterImageMimeType,
    type MarkdownView
  } from './editor/markdownPreview.ts';
  import { buttonVariants } from '$lib/components/ui/button/variants.js';
  import { cn } from '$lib/utils.js';
  import LanguageIntelligenceControls from '../../components/LanguageIntelligenceControls.svelte';
  import type SourceMarkdownPreview from '$lib/shell/files/components/SourceMarkdownPreview.svelte';
  import { SegmentedControl } from '$lib/components/ui/segmented-control/index.js';
  import { workspaceKey } from '$lib/shell/files/editor/languageIntelligenceMode';
  import {
    clearLanguageIntelligenceBar,
    publishLanguageIntelligenceBar
  } from '$lib/shell/files/editor/languageIntelligenceBar.svelte';
  import { onOpenFile, type OpenFileRequest } from '$lib/shell/openFileBus';
  import { onWorkspaceFileChange } from '$lib/shell/workspaceFileChangeBus.ts';
  import { countInvoke } from '$lib/shell/devInvokeCounter.svelte';
  import { setEditorSourceReadDiagnostics } from '$lib/shell/resourceDiagnostics.svelte';
  import {
    activateEditor,
    activeEditorFile,
    clearEditorFileLoading,
    clearEditorFileConflict,
    closeEditorFile,
    discardEditorFileDraft,
    editorFileFor,
    editorState,
    markEditorFileLoading,
    openEditorFile,
    resetEditorState,
    revealEditorLine,
    setActiveEditorFile,
    setEditorFileDraft,
    setEditorFileError,
    setEditorFileConflict,
    setEditorFilePreview,
    setEditorFileSaving,
    setEditorSymbols
  } from '$lib/shell/files/editor/editorStore.svelte';
  import { modelPathToDisposeOnClose, needsRead } from '$lib/shell/files/editor/editorStoreOps';
  import {
    sourceIntelligence,
    type SourceInlayHintRequest
  } from '$lib/shell/files/editor/sourceIntelligence';
  import { settings } from '$lib/shell/settings/settingsStore.svelte';
  import { sourceRecordFromPath } from '$lib/shell/files/editor/sourceRecordFromPath';
  import {
    dotnetWorkspaceSessionRequest,
    type DotnetWorkspaceAction,
    type WorkspaceCommandSessionRequest
  } from '$lib/workspaceCodeLens';
  import { openFileTimeline } from '$lib/shell/workbenchNavigation';
  import {
    findSourceLspCodeActionsFromTauri,
    cancelSourceFileReadsFromTauri,
    isNativeTauriRuntime,
    readSourceImageFromTauri,
    readSourceFromTauri,
    listSourceDirectoryFromTauri,
    readSourceLspStatusFromTauri,
    setWorkspaceLanguageIntelligenceFromTauri,
    warmSourceLspForRootFromTauri,
    writeSourceToTauri
  } from '$lib/tauriSource';
  import type CodeMirrorSourceEditor from '$lib/shell/files/components/CodeMirrorSourceEditor.svelte';
  import type {
    SourceCodeAction,
    SourceCodeActionLookupRequest,
    SourceDiagnostic,
    SourceInlayHint,
    SourcePreview,
    SourceRecord,
    SourceDirectoryEntry,
    SourceSymbol
  } from '$lib/sourceData';
  import { applySourceTextEdits } from '$lib/sourceData';

  /**
   * The code editor is a large download, so it is fetched with the first file
   * the user opens rather than with the shell. The import here is types only —
   * it adds nothing to the page — and the real module arrives in
   * `ensureCodeEditor`.
   */
  interface Props {
    /** Session whose live file-edit events may invalidate open documents. */
    ownedId?: string | null;
    sessionRoot?: string; // The selected session's own folder; a save outside it is refused while a running session works there.
    /** Called when an open-file request has become a real open, so the shell can
     * bring this panel's tab to the front. The panel itself stays unaware of the
     * tab area — it just says a file arrived. */
    onFileOpened?: () => void;
    /** Whether this panel is the center tab in front. Putting a session's files
     * back opens them out of sight, and the code editor is far too expensive to
     * start for a file nobody is looking at. */
    showing?: boolean;
    /** False when the selected session's checkout no longer exists. */
    rootAvailable?: boolean;
    /** Clears this session's saved editor strip after the visible models are released. */
    onCloseAllEditors?: () => void | Promise<boolean | void>;
    /** Starts a fixed .NET workspace action in the page-owned terminal rail. */
    onStartWorkspaceCommand?: (request: WorkspaceCommandSessionRequest) => void | Promise<void>;
  }
  let {
    ownedId = null,
    sessionRoot = '',
    onFileOpened,
    showing = false,
    rootAvailable = true,
    onCloseAllEditors,
    onStartWorkspaceCommand
  }: Props = $props();

  type CodeEditorComponent = typeof CodeMirrorSourceEditor;
  let CodeEditor = $state<CodeEditorComponent | null>(null);
  /** Loaded the first time a Markdown file is shown as Preview. */
  let MarkdownPreview = $state<typeof SourceMarkdownPreview | null>(null);
  let codeEditor = $state<{
    captureViewStates(paths: readonly string[]): Record<string, object>;
    disposeTabModel(path: string): boolean;
    disposeAllTabModels(): void;
    refreshReferenceCounts(): void;
    releaseSessionResources(): void;
  } | null>(null);
  let editorLoadError = $state<string | null>(null);
  let loadingEditorComponent = false;
  type CloseRequest = { kind: 'file'; path: string } | { kind: 'all' };
  let closeRequest = $state<CloseRequest | null>(null);
  let closeDialogOpen = $state(false);
  let closeActionBusy = $state(false);

  type EditorSourceRead = { byteCount: number; generation: number; token: object; work: Promise<void> };

  /** Native reads cannot be cancelled, so keep them owned until they settle. */
  const readsInFlight = new Map<string, EditorSourceRead>();

  function publishSourceReadDiagnostics(): void {
    setEditorSourceReadDiagnostics(
      readsInFlight.size,
      [...readsInFlight].filter(([path, read]) =>
        read.generation !== sessionResourceGeneration || path !== editorState.activePath
      ).length,
      [...readsInFlight.values()].reduce((total, read) => total + read.byteCount, 0)
    );
  }
  let sessionResourceGeneration = 0;
  let sessionStopController = new AbortController();
  let restoredViewStates = $state<Record<string, object>>({});
  /** Projects whose language server has already been pointed at the project. */
  const warmedProjectRoots = new Set<string>();

  /** What the desktop app last said about this project's mode. */
  let languageIntelligenceNote = $state<string | null>(null);
  let nativeCsharpRoot: string | null = null;
  let backendOwnerRoot = $state<string | null>(null);
  let requestedOwnerKey: string | null = null;
  let ownerSelectionGeneration = 0;
  let ownerTransitionTail: Promise<void> = Promise.resolve();
  let currentOwnerTransition: ReturnType<typeof setWorkspaceLanguageIntelligenceFromTauri> =
    Promise.resolve(null);
  /** Language-server processes the desktop app reports for this project. */
  let languageServerPids = $state<number[]>([]);
  let destroyed = false;

  const activeFile = $derived(activeEditorFile());
  const activeImageMimeType = $derived(rasterImageMimeType(activeFile?.fileName));
  const activePreview = $derived(activeFile?.preview ?? (activeFile
    ? {
        path: activeFile.path,
        relativePath: activeFile.relativePath,
        fileName: activeFile.fileName,
        language: activeFile.language,
        byteCount: 0,
        content: '',
        lineCount: 1,
        revision: ''
      }
    : null));
  const activeFileMissing = $derived(
    Boolean(activeFile?.error && /(?:os error 2|No such file)/i.test(activeFile.error))
  );
  /** Files outside the session's project open read-only until "Edit anyway",
   * keyed by path; the value is the root to inspect them from. */
  let readOnlyByPath = $state<Record<string, string>>({});
  const dropReadOnly = (path: string): void => { const { [path]: _dropped, ...rest } = readOnlyByPath; readOnlyByPath = rest; };
  const activeFileReadOnly = $derived(Boolean(activeFile && readOnlyByPath[activeFile.path]));
  /** The breadcrumb menus open right now, outermost first: depth 0 is a
   * segment's menu (or a folder row of the "…" menu), each deeper entry a
   * folder's submenu. Only open menus hold entries; closing one drops it and
   * everything below it, and a new file or root drops them all. */
  let breadcrumbChain = $state<{ directory: string; opening: number; entries: SourceDirectoryEntry[] | null; error: string | null }[]>([]);
  let breadcrumbMenuGeneration = 0;
  /** Numbers each menu opening, so a late answer for a folder that was closed
   * and opened again cannot fill the newer opening. */
  let breadcrumbOpenings = 0;
  let breadcrumbOwner = '';
  const breadcrumbRoot = $derived(activeFile
    ? (readOnlyByPath[activeFile.path] || editorState.projectRoot || '')
    : '');
  $effect(() => {
    const owner = `${breadcrumbRoot}\0${activeFile?.path ?? ''}`;
    if (owner === breadcrumbOwner) return;
    breadcrumbOwner = owner;
    breadcrumbChain = [];
    breadcrumbMenuGeneration += 1;
  });
  const breadcrumbPath = $derived(activeFile && breadcrumbRoot && activeFile.path.startsWith(`${breadcrumbRoot}/`)
    ? activeFile.path
    : '');
  /** Wide enough for long names; long folders scroll inside the menu. */
  const BREADCRUMB_MENU = 'w-auto min-w-56 max-w-[min(40rem,80vw)] max-h-[60vh] overflow-y-auto';
  /** Submenus are portaled out of their scrolling parent, so they take the
   * shell theme scope and the top-level menu's layer with them. */
  const BREADCRUMB_SUBMENU = `${BREADCRUMB_MENU} next-shell z-[250]`;
  const breadcrumbParts = $derived(breadcrumbPath
    ? [breadcrumbRoot, ...breadcrumbPath.slice(breadcrumbRoot.length + 1).split('/').map((_, index, parts) =>
        `${breadcrumbRoot}/${parts.slice(0, index + 1).join('/')}`)]
    : []);
  /** Deep paths fold their middle folders into one "…" menu, so the project
   * name and the file name both stay readable in the header. */
  const breadcrumbHidden = $derived(breadcrumbParts.length > 4 ? breadcrumbParts.slice(1, -2) : []);

  async function openBreadcrumbMenu(directory: string, depth: number): Promise<void> {
    if (!activeFile || !breadcrumbRoot || !rootAvailable) return;
    const file = activeFile.path;
    const root = breadcrumbRoot;
    // Bumped only when the file or root changes, so a sibling submenu opening
    // does not discard a parent menu's answer still on its way.
    const generation = breadcrumbMenuGeneration;
    const opening = ++breadcrumbOpenings;
    breadcrumbChain = [...breadcrumbChain.slice(0, depth), { directory, opening, entries: null, error: null }];
    const current = () => generation === breadcrumbMenuGeneration && activeFile?.path === file && breadcrumbRoot === root && breadcrumbChain[depth]?.opening === opening;
    let answer: { entries: SourceDirectoryEntry[]; error: string | null };
    try {
      answer = { entries: (await listSourceDirectoryFromTauri(root, directory)) ?? [], error: null };
    } catch (error) {
      answer = { entries: [], error: describeError(error) };
    }
    if (current()) breadcrumbChain = breadcrumbChain.map((menu, index) => (index === depth ? { directory, opening, ...answer } : menu));
  }

  /** Close only the menu that is still this directory's: a sibling submenu may
   * already have taken its depth. */
  function closeBreadcrumbMenu(directory: string, depth: number): void {
    if (breadcrumbChain[depth]?.directory === directory) breadcrumbChain = breadcrumbChain.slice(0, depth);
  }

  function openBreadcrumbFile(entry: SourceDirectoryEntry): void {
    if (!breadcrumbRoot) return;
    handleOpenFileRequest({ path: entry.path, projectRoot: breadcrumbRoot, readOnly: activeFileReadOnly });
  }
  const activeServerEnabled = $derived.by(() => {
    const language = activeFile?.language?.toLowerCase();
    if (language === 'csharp' || language === 'c#') return settings.intelligence.languageServerEnabled.csharp;
    if (language === 'typescript' || language === 'javascript' || language === 'tsx' || language === 'jsx') {
      return settings.intelligence.languageServerEnabled.typescript;
    }
    if (language === 'rust') return settings.intelligence.languageServerEnabled.rust;
    return null;
  });
  /** Is this project in full mode — language server allowed to run? Settings
   * can switch every server off at once, and then no project is, whatever its
   * own switch says: the switch reads Off, and its title says which one held. */
  const fullMode = $derived(
    !activeFileReadOnly
      && settings.intelligence.languageServers
      && activeServerEnabled === true
  );
  const activeLanguageServerRoot = $derived.by(() => {
    const root = activeLanguageRoot();
    return fullMode && root && backendOwnerRoot === workspaceKey(root) ? root : null;
  });
  /**
   * The sentence on hover: what the mode means for this project, and the
   * process numbers behind it so they can be found in the resource view.
   */
  const languageIntelligenceTitle = $derived.by(() => {
    if (!editorState.projectRoot) return 'Open a file in a project to switch this on.';
    if (!settings.intelligence.languageServers) {
      return 'Editor-only — Supercharged is switched off in Settings.';
    }
    if (activeServerEnabled === false) {
      return 'Editor-only — this language server is switched off in Settings.';
    }
    if (activeFile?.language && activeServerEnabled === null) {
      return `Editor-only — no language server for ${activeFile.language}.`;
    }
    const note =
      languageIntelligenceNote ??
      (fullMode
        ? 'Language intelligence is on for this project. One language server serves every session and view on it.'
        : 'Read mode: files open with colouring only, and no language server is started.');
    if (languageServerPids.length === 0) return note;
    const numbers = languageServerPids.join(', ');
    const noun = languageServerPids.length === 1 ? 'Process' : 'Processes';
    return `${note} ${noun} ${numbers}.`;
  });
  /**
   * Which view each Markdown file is on. Markdown opens source-first; the only
   * way to write `rendered` here is the active file's explicit Preview toggle.
   * Leaving a file releases that preview choice instead of keeping rendered DOM
   * logically hidden behind the strip.
   */
  let markdownViewByPath = $state<Record<string, MarkdownView>>({});
  const markdownScrollByPath = new Map<string, number>();
  let imagePreview = $state<{ path: string; url: string } | null>(null);
  const activeFileIsMarkdown = $derived(isMarkdownFile(activeFile?.fileName));
  const activeFileIsHtml = $derived(isHtmlFile(activeFile?.fileName));
  const markdownView = $derived(
    activeFile && (activeFileIsMarkdown || activeFileIsHtml)
      ? (markdownViewByPath[activeFile.path] ?? 'raw')
      : 'raw'
  );
  /** Preview covers the source editor, which stays mounted behind it so the
   *  file keeps its undo history and scroll position. */
  const markdownPreviewShown = $derived(
    Boolean(showing && activeFile?.preview && activeFileIsMarkdown && markdownView === 'rendered')
  );
  const MARKDOWN_VIEW_ITEMS = [
    { value: 'raw', label: 'Source' },
    { value: 'rendered', label: 'Preview' }
  ] as const;

  /** Record the first view for a file, without overwriting a person's choice. */
  function rememberMarkdownDefault(path: string, fileName: string, origin: 'jump' | 'strip'): void {
    if (!isMarkdownFile(fileName) || markdownViewByPath[path]) return;
    markdownViewByPath = {
      ...markdownViewByPath,
      [path]: markdownPreviewDefault(fileName, origin)
    };
  }

  function setMarkdownView(view: MarkdownView): void {
    const path = activeFile?.path;
    if (!path) return;
    markdownViewByPath = { ...markdownViewByPath, [path]: view };
  }

  function releaseMarkdownView(path: string): void {
    if (!markdownViewByPath[path]) return;
    const { [path]: _released, ...rest } = markdownViewByPath;
    markdownViewByPath = rest;
  }

  function releaseImagePreview(path?: string): void {
    if (!imagePreview || (path && imagePreview.path !== path)) return;
    URL.revokeObjectURL(imagePreview.url);
    imagePreview = null;
  }

  function imageNeedsRead(file: {
    path: string;
    fileName: string;
    loading: boolean;
    error: string | null;
  }): boolean {
    return Boolean(
      rasterImageMimeType(file.fileName)
      && !file.loading
      && file.error === null
      && imagePreview?.path !== file.path
    );
  }

  /**
   * What the language server says is wrong, per file. Kept per file rather than
   * for "the file on screen" because switching tabs must not show the last
   * file's squiggles on this one while a fresh read is still in flight.
   */
  let diagnosticsByPath = $state<Record<string, SourceDiagnostic[]>>({});
  /**
   * The one empty answer, reused. The fallback below must keep a stable
   * identity: a fresh `[]` per template evaluation reads as a changed prop
   * to the editor's effect and can spin it in a loop.
   */
  const NO_DIAGNOSTICS: SourceDiagnostic[] = [];
  /**
   * The last thing the desktop app said about the open file's language server —
   * either the answer to `read_source_lsp_status` or a pushed
   * pushed status message. Both carry the same `state` and
   * `detail` fields, so either one can be shown as-is.
   *
   * `null` means nobody has said anything: a browser tab (there is no language
   * server behind a browser) or a desktop build older than these fields. In
   * both cases no chip appears and the panel behaves exactly as it used to.
   */
  let languageServerStatus = $state<unknown>(null);
  /** Which project and language that answer was about, so a stale one is dropped. */
  let languageServerSubject: { root: string; language: string } | null = null;
  /**
   * Holds back work that a server which is still starting up or reading the
   * project could not answer anyway. It lets everything through the moment the
   * server says it is ready, or when this owner tears the wait down.
   */
  const languageServerGate = createLanguageServerGate();
  /** Counts inline-hint requests, so only the newest one survives a wait. */
  let inlayHintRequestCount = 0;
  let releaseDiagnosticsReadyWait: (() => void) | null = null;
  let releaseInlayReadyWait: (() => void) | null = null;

  /** The language of the file on screen, or null when nothing is open. */
  function activeFileLanguage(): string | null {
    return activeEditorFile()?.language ?? null;
  }

  /** Record what the desktop app just said, and let waiting work know. */
  function applyLanguageServerStatus(
    status: unknown,
    subject: { root: string; language: string } | null
  ): void {
    const sameSubject = Boolean(
      subject
        && languageServerSubject
        && subject.root === languageServerSubject.root
        && subject.language === languageServerSubject.language
    );
    const nextState = readLanguageServerState(status);
    if (
      sameSubject
        && readLanguageServerState(languageServerStatus) === 'ready'
        && (nextState === 'starting' || nextState === 'indexing')
    ) return;
    languageServerStatus = status;
    languageServerSubject = subject;
    languageServerGate.setState(readLanguageServerState(status));
  }

  /**
   * Ask what the language server for this file is doing. Called when a file
   * opens or the strip selection changes — never on a timer. Everything after
   * that arrives as a pushed message.
   */
  async function refreshLanguageServerStatus(): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    if (activeFileReadOnly) {
      if (languageServerStatus !== null || languageServerSubject !== null) {
        applyLanguageServerStatus(null, null);
      }
      return;
    }
    const root = editorState.projectRoot;
    const language = activeFileLanguage();

    // A different project or a different language means the last answer was
    // about someone else's server; drop it rather than show it against this file.
    if (
      languageServerSubject &&
      (languageServerSubject.root !== root || languageServerSubject.language !== language)
    ) {
      applyLanguageServerStatus(null, null);
    }

    if (!root || !language || !isNativeTauriRuntime()) return;

    countInvoke('read_source_lsp_status');
    let answer: unknown = null;
    try {
      if (stopSignal.aborted) return;
      answer = await readSourceLspStatusFromTauri(root, language);
      if (stopSignal.aborted) return;
    } catch {
      // No answer is not a state worth reporting, so the chip stays away.
      answer = null;
    }
    // Superseded: the user moved to another file while this was in flight.
    if (destroyed || editorState.projectRoot !== root || activeFileLanguage() !== language) return;
    applyLanguageServerStatus(answer, { root, language });
  }

  /** Apply status updates fanned out by the shared source-intelligence listener. */
  function handleLanguageServerStatus(status: unknown): void {
    if (activeFileReadOnly) return;
    const root = editorState.projectRoot;
    const language = activeFileLanguage();
    if (!root || !language) return;
    // Several servers can be running at once, so a message about another
    // project or another language must not move this file's chip.
    if (!statusMessageIsAboutThisFile(status, root, language)) return;
    applyLanguageServerStatus(status, { root, language });
  }

  /** The official CodeMirror client reports this only after Roslyn's ready
   * handshake succeeds, so do not let an older in-flight status read keep the
   * bottom switch yellow. */
  function handleLanguageServerReady(): void {
    const root = editorState.projectRoot;
    const language = activeFileLanguage();
    if (!root || !language) return;
    applyLanguageServerStatus(
      { root, language, state: 'ready', detail: null },
      { root, language }
    );
  }

  function handleReferenceCountUpdate(path: string): void {
    if (destroyed || editorState.activePath !== path) return;
    codeEditor?.refreshReferenceCounts();
  }

  /** Ask what is wrong with the file on screen, and remember it against that file. */
  async function loadDiagnosticsForActiveFile(): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    if (activeFileReadOnly) return;
    const path = editorState.activePath;
    if (!path) return;
    const generation = sessionResourceGeneration;
    if (stopSignal.aborted) return;
    const diagnostics = await sourceIntelligence.loadActiveFileDiagnostics();
    if (stopSignal.aborted) return;
    // Superseded: the user moved on while this was in flight, so this answer is
    // about a file that is no longer on screen.
    if (destroyed || generation !== sessionResourceGeneration || editorState.activePath !== path) return;
    diagnosticsByPath = { ...diagnosticsByPath, [path]: diagnostics };
  }

  function clearDiagnosticsReadyWait(): void {
    releaseDiagnosticsReadyWait?.();
    releaseDiagnosticsReadyWait = null;
  }

  /** Read diagnostics now, then again when a busy server reports ready. */
  function refreshDiagnosticsForActiveFile(): void {
    if (activeFileReadOnly) return;
    clearDiagnosticsReadyWait();
    void loadDiagnosticsForActiveFile();
    scheduleDiagnosticsWhenServerReady();
  }

  function scheduleDiagnosticsWhenServerReady(): void {
    if (!languageServerGate.isBusy()) return;
    const path = editorState.activePath;
    const generation = sessionResourceGeneration;
    releaseDiagnosticsReadyWait = languageServerGate.onReady(() => {
      releaseDiagnosticsReadyWait = null;
      void loadDiagnosticsAfterServerReady(path, generation);
    });
  }

  async function loadDiagnosticsAfterServerReady(
    path: string | null,
    generation: number
  ): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    if (destroyed || editorState.activePath !== path) return;
    if (generation !== sessionResourceGeneration) return;
    await loadDiagnosticsForActiveFile();
    if (stopSignal.aborted) return;
  }

  /**
   * Inline type hints, held back until the server can actually answer.
   *
   * The editor asks for these the instant a file appears. A server that is
   * still reading the project answers "no hints", the editor believes it, and
   * nothing asks again — which is why hints used to be missing for the rest of
   * the session on a cold start. Making the request WAIT instead of answering
   * it emptily means the same request is answered properly once the server is
   * ready. With nothing known about the server this passes straight through.
   */
  async function lookupInlayHintsWhenServerCanAnswer(
    request: SourceInlayHintRequest
  ): Promise<SourceInlayHint[]> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return [];
    if (activeFileReadOnly) return [];
    if (languageServerGate.isBusy()) {
      const path = editorState.activePath;
      const ticket = ++inlayHintRequestCount;
      clearInlayReadyWait();
      releaseInlayReadyWait = languageServerGate.onReady(() => {
        releaseInlayReadyWait = null;
        refreshInlayHintsAfterServerReady(path, ticket);
      });
      return [];
    }
    if (stopSignal.aborted) return [];
    const hints = await sourceIntelligence.callbacks.onInlayHintLookup(request);
    if (stopSignal.aborted) return [];
    return hints;
  }

  function clearInlayReadyWait(): void {
    releaseInlayReadyWait?.();
    releaseInlayReadyWait = null;
  }

  function refreshInlayHintsAfterServerReady(path: string | null, ticket: number): void {
    // Only the newest request survives the wait. The editor asks again for
    // every scroll, so answering a whole queue of stale ranges at once would
    // simply move the pile-up to the end of the wait instead of removing it.
    if (destroyed || ticket !== inlayHintRequestCount || editorState.activePath !== path) return;
    void refreshEditorIntelligenceForActiveFile();
  }

  function describeError(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
  }

  /**
   * The record for a path, with its language filled in when the usual mapping
   * did not recognise the file. SQL is the clearest case: `.sql` was not on the
   * old list, so those files opened as flat grey text even though the editor has
   * had the SQL colouring rules loaded all along. Files the old mapping already
   * knew are untouched.
   *
   * LOAD-BEARING ORDER: the record is what the read wrapper copies the language
   * back out of, so it has to be right before the file is read, not after.
   */
  function recordForPath(path: string, projectRoot = editorState.projectRoot): SourceRecord {
    const record = sourceRecordFromPath(projectRoot, path);
    const language = upgradeUnknownLanguage(record.path, record.language);
    return language === record.language ? record : { ...record, language };
  }

  /** Download the Markdown preview the first time a file is shown that way. */
  $effect(() => {
    if (!markdownPreviewShown || MarkdownPreview) return;
    const controller = new AbortController();
    void loadMarkdownPreview(controller.signal);
    return () => controller.abort();
  });

  async function loadMarkdownPreview(signal: AbortSignal): Promise<void> {
    try {
      const module = await import('$lib/shell/files/components/SourceMarkdownPreview.svelte');
      if (!signal.aborted) MarkdownPreview = module.default;
    } catch (error) {
      if (!signal.aborted) editorLoadError = `Could not start the Markdown preview: ${describeError(error)}`;
    }
  }

  /** Download the code editor the first time it is needed. */
  async function ensureCodeEditor(): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    if (loadingEditorComponent) return;
    loadingEditorComponent = true;
    try {
      const root = editorState.projectRoot;
      // Wait until the first editor can receive the real, path-bearing root.
      if (!root) return;
      if (!CodeEditor) {
        if (stopSignal.aborted) return;
        const module = await import('$lib/shell/files/components/CodeMirrorSourceEditor.svelte');
        if (stopSignal.aborted) return;
        if (!destroyed) CodeEditor = module.default;
      }
    } catch (error) {
      if (!stopSignal.aborted && !destroyed) editorLoadError = `Could not start the code editor: ${describeError(error)}`;
    } finally {
      loadingEditorComponent = false;
    }
  }

  async function runLanguageIntelligenceOwnerTransition(
    root: string | null,
    enabled: boolean,
    language: string | null
  ): ReturnType<typeof setWorkspaceLanguageIntelligenceFromTauri> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return null;
    try {
      if (stopSignal.aborted) return null;
      await ownerTransitionTail;
      if (stopSignal.aborted) return null;
    } catch {
      // A failed earlier owner transition must not block the latest owner.
    }
    if (!isNativeTauriRuntime()) return null;
    const commandRoot = root ?? backendOwnerRoot;
    if (!commandRoot) return null;
    if (backendOwnerRoot !== root) warmedProjectRoots.clear();
    countInvoke('set_workspace_language_intelligence');
    if (stopSignal.aborted) return null;
    const answer = await setWorkspaceLanguageIntelligenceFromTauri(
      commandRoot,
      Boolean(root && enabled),
      root && enabled ? language : null
    );
    if (stopSignal.aborted) return null;
    backendOwnerRoot = root && enabled ? root : null;
    return answer;
  }

  async function retainLanguageIntelligenceOwnerOrder(
    transition: ReturnType<typeof setWorkspaceLanguageIntelligenceFromTauri>
  ): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    try {
      await transition;
      if (stopSignal.aborted) return;
    } catch {
      // Keep later owner transitions ordered even when one command fails.
    }
  }

  function queueLanguageIntelligenceOwner(
    projectRoot: string | null,
    enabled: boolean,
    language: string | null = null,
    force = false
  ): ReturnType<typeof setWorkspaceLanguageIntelligenceFromTauri> {
    const root = projectRoot ? workspaceKey(projectRoot) : null;
    const ownerKey = `${root ?? ''}|${enabled}`;
    if (!force && requestedOwnerKey === ownerKey) return currentOwnerTransition;
    requestedOwnerKey = ownerKey;

    const transition = runLanguageIntelligenceOwnerTransition(root, enabled, language);
    ownerTransitionTail = retainLanguageIntelligenceOwnerOrder(transition);
    currentOwnerTransition = transition;
    return transition;
  }

  /**
   * Tell the language server which project this file belongs to before warming
   * the file's language.
   */
  async function warmLanguageServer(projectRoot: string | null): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    if (!projectRoot || activeServerEnabled !== true) return;
    if (destroyed || !settings.intelligence.languageServers) return;
    try {
      await queueLanguageIntelligenceOwner(projectRoot, true);
      if (stopSignal.aborted || destroyed) return;
    } catch (error) {
      if (!stopSignal.aborted && !destroyed) {
        languageIntelligenceNote = `This project's language server could not be selected: ${describeError(error)}`;
      }
      return;
    }
    if (warmedProjectRoots.has(projectRoot)) return;
    warmedProjectRoots.add(projectRoot);
    countInvoke('warm_source_lsp_for_root');
    try {
      if (stopSignal.aborted) return;
      await warmSourceLspForRootFromTauri(projectRoot);
      if (stopSignal.aborted) return;
    } catch (error) {
      // Warming only costs a cold first lookup, but a reader who has the switch
      // on is owed the reason rather than a project that quietly stays cold.
      if (!stopSignal.aborted && !destroyed) {
        languageIntelligenceNote = `This project's language server could not be warmed: ${describeError(error)}`;
      }
    }
  }

  /** A language service exists only for a visible, editable source document. */
  function activeLanguageRoot(): string | null {
    return showing && rootAvailable && editorState.activePath && !activeFileReadOnly && !parseRemoteWorkspacePath(editorState.projectRoot ?? '')
      ? editorState.projectRoot
      : null;
  }

  /** Keep the lookup service pointed at whatever is on screen. */
  function syncIntelligenceWithActiveFile(): void {
    if (activeFileReadOnly && (languageServerStatus !== null || languageServerSubject !== null)) {
      applyLanguageServerStatus(null, null);
    }
    // Plain-text lookup also works on remote workspaces; only LSP ownership is local.
    const root = showing && rootAvailable && editorState.activePath && !activeFileReadOnly
      ? editorState.projectRoot
      : null;
    sourceIntelligence.setProjectRoot(root);
    sourceIntelligence.setActivePreview(root ? activeEditorFile()?.preview ?? null : null);
  }

  async function applyLanguageIntelligenceOwnerForEffect(
    generation: number,
    root: string | null,
    languageServersEnabled: boolean
  ): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    if (stopSignal.aborted || destroyed || generation !== ownerSelectionGeneration) return;
    const enabled = Boolean(
      root
        && languageServersEnabled
        && activeServerEnabled === true
    );
    try {
      if (stopSignal.aborted) return;
      const answer = await queueLanguageIntelligenceOwner(root, enabled);
      if (
        stopSignal.aborted
        || destroyed
        || !answer
        || root !== activeLanguageRoot()
      ) return;
      languageServerPids = answer.serverPids;
    } catch {
      // An older desktop build leaves the selected project in read mode.
    }
  }

  async function publishNativeCsharpActiveRoot(root: string | null): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    try {
      if (stopSignal.aborted) return;
      const { setNativeCsharpActiveRoot } = await import('$lib/shell/files/editor/csharpLanguageClient');
      if (stopSignal.aborted) return;
      setNativeCsharpActiveRoot(root);
    } catch {
      // A missing optional client leaves the active root unset.
    }
  }

  // Session restore and project switching update the shared editor store
  // without calling this panel's open/select handlers. Keep the extracted
  // intelligence service attached to that state continuously; otherwise the
  // file can be visible while counts are asked with projectRoot = null.
  $effect(() => {
    showing;
    editorState.projectRoot;
    editorState.activePath;
    activeEditorFile()?.preview;
    syncIntelligenceWithActiveFile();
  });

  $effect(() => {
    const root = activeLanguageRoot();
    const languageServersEnabled = settings.intelligence.languageServers;
    const generation = ++ownerSelectionGeneration;
    void applyLanguageIntelligenceOwnerForEffect(generation, root, languageServersEnabled);
  });

  $effect(() => {
    const root = activeLanguageServerRoot;
    if (root === nativeCsharpRoot) return;
    nativeCsharpRoot = root;
    void publishNativeCsharpActiveRoot(root);
  });

  async function readFileIntoEditorForOwner(
    record: SourceRecord,
    generation: number,
    readOnly: boolean,
    projectRoot: string | null,
    token: object,
    externalChange: boolean
  ): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    try {
      // The record has to be built first: the read wrapper copies the relative
      // path, language and size back out of it onto the preview it returns.
      countInvoke('read_source_file');
      if (stopSignal.aborted) return;
      const imageMimeType = rasterImageMimeType(record.fileName);
      const imageBytes = imageMimeType ? await readSourceImageFromTauri(record.path, stopSignal) : null;
      const preview = imageMimeType
        ? {
            ...record,
            content: '',
            lineCount: 0,
            revision: ''
          }
        : await readSourceFromTauri(record);
      if (stopSignal.aborted) return;
      if (
        destroyed
        || generation !== sessionResourceGeneration
        || editorState.activePath !== record.path
        || (imageMimeType !== null && !showing)
        || (!readOnly && editorState.projectRoot !== projectRoot)
        || !editorFileFor(record.path)
      ) {
        if (!destroyed && generation === sessionResourceGeneration && editorFileFor(record.path)) {
          clearEditorFileLoading(record.path);
        }
        return;
      }
      if (preview) {
        if (imageMimeType && imageBytes) {
          const imageBuffer = new Uint8Array(imageBytes.byteLength);
          imageBuffer.set(imageBytes);
          releaseImagePreview();
          imagePreview = {
            path: record.path,
            url: URL.createObjectURL(new Blob([imageBuffer.buffer], { type: imageMimeType }))
          };
          setEditorFilePreview(record.path, preview, null, true);
        } else {
          setEditorFilePreview(record.path, preview, null, readOnly);
        }
      } else {
        setEditorFileError(record.path, 'This file could not be read from here.');
      }
    } catch (error) {
      if (!stopSignal.aborted && !destroyed && generation === sessionResourceGeneration && editorFileFor(record.path)) {
        const detail = describeError(error);
        setEditorFileError(
          record.path,
          externalChange
            ? `File changed outside Assembly and could not be refreshed: ${detail}`
            : `Could not read this file: ${detail}`
        );
      }
    } finally {
      if (readsInFlight.get(record.path)?.token === token) {
        readsInFlight.delete(record.path);
        publishSourceReadDiagnostics();
      }
      if (!stopSignal.aborted && !destroyed && generation === sessionResourceGeneration) {
        syncIntelligenceWithActiveFile();
        if (!readOnly) void refreshEditorIntelligenceForActiveFile();
      }
    }
  }

  /** EXPLICIT IO: read one file and show it. */
  async function readFileIntoEditor(record: SourceRecord, externalChange = false): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted || closeActionBusy || !rootAvailable) return;
    const existing = readsInFlight.get(record.path);
    if (existing) {
      if (stopSignal.aborted) return;
      await existing.work;
      if (stopSignal.aborted) return;
      const file = editorFileFor(record.path);
      if (!file || !needsRead(file)) return;
      return readFileIntoEditor(record, externalChange);
    }
    const generation = sessionResourceGeneration;
    const readOnly = Boolean(readOnlyByPath[record.path]);
    const projectRoot = editorState.projectRoot;
    markEditorFileLoading(record.path);
    if (!readOnly && !rasterImageMimeType(record.fileName)) void warmLanguageServer(editorState.projectRoot);
    const token = {};
    const work = readFileIntoEditorForOwner(record, generation, readOnly, projectRoot, token, externalChange);
    readsInFlight.set(record.path, { byteCount: record.byteCount, generation, token, work });
    publishSourceReadDiagnostics();
    return work;
  }

  function updateActiveDraft(content: string): void {
    if (closeActionBusy) return;
    const file = activeEditorFile();
    if (!file || readOnlyByPath[file.path]) return;
    setEditorFileDraft(file.path, content);
  }

  async function lookupCodeActions(
    request: SourceCodeActionLookupRequest
  ): Promise<SourceCodeAction[]> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return [];
    const file = activeEditorFile();
    const root = editorState.projectRoot;
    if (!fullMode || !file?.preview || !root || activeFileReadOnly) return [];
    const language = file.language.toLowerCase();
    if (language !== 'csharp' && language !== 'c#') return [];
    const generation = sessionResourceGeneration;
    const path = file.path;
    try {
      countInvoke('find_source_lsp_code_actions');
      if (stopSignal.aborted) return [];
      const actions = await findSourceLspCodeActionsFromTauri(
        { ...file.preview, content: file.draftContent ?? file.preview.content },
        { ...request, root, limit: 50 }
      );
      if (stopSignal.aborted) return [];
      if (
        destroyed || generation !== sessionResourceGeneration ||
        editorState.projectRoot !== root || editorState.activePath !== path || !fullMode
      ) return [];
      return actions ?? [];
    } catch {
      return [];
    }
  }

  async function applyExternalWorkspaceEdits(action: SourceCodeAction): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    const activePath = editorState.activePath;
    const root = editorState.projectRoot;
    const generation = sessionResourceGeneration;
    if (!activePath || !root || !fullMode) return;
    for (const fileEdit of action.files) {
      if (fileEdit.path === activePath || fileEdit.edits.length === 0) continue;
      let file = editorFileFor(fileEdit.path);
      if (!file?.preview) {
        const record = recordForPath(fileEdit.path, root);
        countInvoke('read_source_file');
        let preview: SourcePreview | null;
        try {
          if (stopSignal.aborted) return;
          preview = await readSourceFromTauri(record);
          if (stopSignal.aborted) return;
        } catch {
          preview = null;
        }
        if (
          !preview || destroyed || generation !== sessionResourceGeneration ||
          editorState.projectRoot !== root || editorState.activePath !== activePath || !fullMode
        ) return;
        openEditorFile(record);
        setEditorFilePreview(record.path, preview);
        setActiveEditorFile(activePath);
        file = editorFileFor(record.path);
      }
      if (!file?.preview) continue;
      const content = file.draftContent ?? file.preview.content;
      setEditorFileDraft(file.path, applySourceTextEdits(content, fileEdit.edits));
    }
  }

  async function saveEditorFile(path: string): Promise<boolean> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted || !rootAvailable) return false;
    const file = editorFileFor(path);
    if (!file?.dirty) return true;
    if (file.saving || readOnlyByPath[path] || file.draftContent === null || file.conflict) return false;
    const generation = sessionResourceGeneration;
    const content = file.draftContent;
    setEditorFileSaving(file.path, true);
    try {
      if (stopSignal.aborted || !file.preview) return false;
      assertSaveAllowed(file.path, sessionRoot, rail.owned.filter((session) => session.ownedId !== ownedId));
      const saved = await writeSourceToTauri(recordForPath(file.path), content, file.preview.revision);
      if (stopSignal.aborted) return false;
      if (!saved) throw new Error('The file could not be written from here.');
      if (destroyed || generation !== sessionResourceGeneration || !editorFileFor(file.path)) return false;
      setEditorFilePreview(file.path, saved, content);
      return true;
    } catch (error) {
      if (!stopSignal.aborted && !destroyed && generation === sessionResourceGeneration && editorFileFor(file.path)) {
        const detail = describeError(error);
        if (detail.includes('Source file changed on disk since it was opened.')) {
          setEditorFileSaving(file.path, false);
          await readFileIntoEditor(recordForPath(file.path), true);
        } else {
          setEditorFileSaving(file.path, false);
          setEditorFileError(file.path, `Could not save this file: ${detail}`);
        }
      }
      return false;
    }
  }

  async function saveActiveFile(): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted || closeActionBusy) return;
    const file = activeEditorFile();
    if (file) {
      if (stopSignal.aborted) return;
      await saveEditorFile(file.path);
      if (stopSignal.aborted) return;
    }
  }

  /**
   * Find out what the language server is doing, then ask it about the file.
   *
   * The order is the point: knowing the server is still reading the project is
   * what lets the panel hold back the work it could not answer yet, instead of
   * firing everything at it the instant a file lands.
   */
  async function refreshEditorIntelligenceForActiveFile(): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted || activeFileReadOnly) return;
    await refreshLanguageServerStatus();
    if (stopSignal.aborted || destroyed || activeFileReadOnly) return;
    refreshDiagnosticsForActiveFile();
  }

  /**
   * Open a file by path — the one entry point. Used by the open-file bus, by
   * the strip, and by "jump to definition" landing in another file. Opening a
   * file IS a user action, so it may load even if the panel has not been shown
   * yet; that is also what marks the editor as in use.
   *
   * Returns whether the file was actually taken on — a blank path opens nothing.
   */
  function openPath(
    path: string,
    line?: number | null,
    projectRoot?: string,
    origin: 'jump' | 'strip' = 'jump',
    readOnly = false,
    previewTab = false,
    pinTab = false
  ): boolean {
    if (closeActionBusy || !rootAvailable || !path.trim()) return false;
    if (editorState.activePath && editorState.activePath !== path) {
      releaseReadOnlyEditorModel(editorState.activePath);
      releaseMarkdownView(editorState.activePath);
      releaseImagePreview(editorState.activePath);
    }
    if (!readOnly) activateEditor(projectRoot);
    const record = recordForPath(path, projectRoot);
    const previewToReplace = editorState.openFiles.find(
      (file) => file.previewTab && file.path !== record.path
    );
    const replacedPreviewPath =
      previewTab && !pinTab ? previewToReplace?.path ?? null : null;
    const entry = openEditorFile(record, { preview: previewTab, pin: pinTab });
    if (replacedPreviewPath) {
      releaseMarkdownView(replacedPreviewPath);
      markdownScrollByPath.delete(replacedPreviewPath);
      codeEditor?.disposeTabModel(replacedPreviewPath);
      sourceIntelligence.releasePreview(replacedPreviewPath);
      const { [replacedPreviewPath]: _closed, ...rest } = diagnosticsByPath;
      diagnosticsByPath = rest;
      dropReadOnly(replacedPreviewPath);
    }
    rememberMarkdownDefault(record.path, entry.fileName, origin);
    if (typeof line === 'number' && line > 0) revealEditorLine(record.path, line);
    syncIntelligenceWithActiveFile();
    if (needsRead(entry) || imageNeedsRead(entry)) {
      void readFileIntoEditor(record);
    }
    return true;
  }

  function handleOpenFileRequest(request: OpenFileRequest): void {
    if (closeActionBusy) return;
    // Marked before the open, so the file is never editable for a frame. The
    // record's path is the key: it is what the strip and the editor hold.
    const record = recordForPath(request.path, request.projectRoot);
    if (request.readOnly) readOnlyByPath = { ...readOnlyByPath, [record.path]: request.projectRoot?.trim() || 'outside the session workspace' };
    else if (readOnlyByPath[record.path]) dropReadOnly(record.path);
    // A rendered Markdown document has no source-line coordinates. An explicit
    // jump (diff hunk, problem, definition) therefore has to reveal the source,
    // even when this tab was previously left on Preview.
    if (
      typeof request.line === 'number' &&
      request.line > 0 &&
      (isMarkdownFile(record.fileName) || isHtmlFile(record.fileName))
    ) {
      markdownViewByPath = { ...markdownViewByPath, [record.path]: 'raw' };
    }
    // Only once the file is in the strip. The read runs after this and may still
    // fail — the tab is the right place to show that, so it stays in front.
    if (
      openPath(
        request.path,
        request.line,
        request.projectRoot,
        'jump',
        Boolean(request.readOnly),
        Boolean(request.preview),
        Boolean(request.pin)
      )
    ) {
      onFileOpened?.();
    }
  }

  export function selectFile(path: string): void {
    if (closeActionBusy) return;
    if (editorState.activePath && editorState.activePath !== path) {
      releaseImagePreview(editorState.activePath);
      releaseReadOnlyEditorModel(editorState.activePath);
      releaseMarkdownView(editorState.activePath);
    }
    setActiveEditorFile(path);
    syncIntelligenceWithActiveFile();
    if (!activeFileReadOnly) void refreshEditorIntelligenceForActiveFile();
    const entry = editorFileFor(path);
    // A file restored into the strip never went through `openPath`, so this is
    // where it gets its first view. Picking a tab is editing, not reading.
    if (entry) rememberMarkdownDefault(path, entry.fileName, 'strip');
    if (entry && (needsRead(entry) || imageNeedsRead(entry))) {
      void readFileIntoEditor(recordForPath(path));
    }
  }

  /** Read-only tabs must not leave an editable CodeMirror history behind. */
  function releaseReadOnlyEditorModel(path: string | null): void {
    if (path && readOnlyByPath[path]) codeEditor?.disposeTabModel(path);
  }

  function closeFileNow(path: string, discard = false): void {
    const disposePath = discard ? path : modelPathToDisposeOnClose(editorFileFor(path));
    closeEditorFile(path);
    markdownScrollByPath.delete(path);
    releaseMarkdownView(path);
    releaseImagePreview(path);
    if (disposePath) codeEditor?.disposeTabModel(disposePath);
    sourceIntelligence.releasePreview(path);
    syncIntelligenceWithActiveFile();
    // Whatever is in front now may be a different language, with a different
    // server behind it — so the chip must not keep the closed file's answer.
    void refreshLanguageServerStatus();
    // A closed file stops holding its diagnostics; nothing can show them now.
    const { [path]: _closed, ...rest } = diagnosticsByPath;
    diagnosticsByPath = rest;
    // The read-only mark belongs to the request that opened the file. Keeping
    // it meant a later open of the same path arrived already locked.
    dropReadOnly(path);
  }

  /** "Edit anyway": the reader chose to change a file the session does not own. */
  function unlockActiveFile(file = activeEditorFile()): void {
    if (file?.preview) { dropReadOnly(file.path); setEditorFilePreview(file.path, file.preview); }
  }

  /** Close one file; a file with unsaved changes asks first. */
  export function requestCloseFile(path: string): void {
    if (closeActionBusy) return;
    if (editorFileFor(path)?.dirty) {
      closeRequest = { kind: 'file', path };
      closeDialogOpen = true;
      return;
    }
    closeFileNow(path);
  }

  export function closeOtherFiles(path: string): void {
    if (closeActionBusy) return;
    for (const file of editorState.openFiles) {
      if (file.path !== path && !file.dirty) closeFileNow(file.path);
    }
  }

  export function closeSavedFiles(): void {
    if (closeActionBusy) return;
    for (const file of editorState.openFiles) {
      if (!file.dirty) closeFileNow(file.path);
    }
  }

  async function closeAllOpenEditorsNow(): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    const generation = sessionResourceGeneration;
    closeActionBusy = true;
    try {
      if (stopSignal.aborted) return;
      const paths = editorState.openFiles.map((file) => file.path);
      for (const path of paths) sourceIntelligence.releasePreview(path);
      codeEditor?.disposeAllTabModels();
      resetEditorState();
      markdownScrollByPath.clear();
      releaseImagePreview();
      markdownViewByPath = {};
      diagnosticsByPath = {};
      readOnlyByPath = {};
      const cleared = await onCloseAllEditors?.();
      if (stopSignal.aborted || generation !== sessionResourceGeneration) return;
      if (cleared === false) {
        editorLoadError = 'Could not clear saved editor tabs.';
        return;
      }
    } catch (error) {
      if (!stopSignal.aborted && generation === sessionResourceGeneration) editorLoadError = `Could not clear saved editor tabs: ${describeError(error)}`;
    } finally {
      closeActionBusy = false;
    }
  }

  function closeAllOpenEditors(): void {
    if (closeActionBusy) return;
    if (editorState.openFiles.some((file) => file.dirty)) {
      closeRequest = { kind: 'all' };
      closeDialogOpen = true;
      return;
    }
    void closeAllOpenEditorsNow();
  }

  async function confirmCloseSave(): Promise<void> {
    const stopSignal = sessionStopController.signal;
    if (stopSignal.aborted) return;
    const request = closeRequest;
    if (!request) return;
    const generation = sessionResourceGeneration;
    closeActionBusy = true;
    if (request.kind === 'file') {
      if (stopSignal.aborted) return;
      const saved = await saveEditorFile(request.path);
      if (stopSignal.aborted || generation !== sessionResourceGeneration) {
        closeActionBusy = false;
        return;
      }
      closeActionBusy = false;
      closeDialogOpen = false;
      closeRequest = null;
      if (saved) closeFileNow(request.path);
      return;
    }
    const dirtyPaths = editorState.openFiles.filter((file) => file.dirty).map((file) => file.path);
    let saved = true;
    for (const path of dirtyPaths) {
      if (generation !== sessionResourceGeneration) {
        saved = false;
        break;
      }
      if (stopSignal.aborted) {
        saved = false;
        break;
      }
      const fileSaved = await saveEditorFile(path);
      if (stopSignal.aborted) {
        saved = false;
        break;
      }
      if (!fileSaved) {
        saved = false;
        break;
      }
    }
    if (generation !== sessionResourceGeneration) {
      closeActionBusy = false;
      return;
    }
    if (!saved) closeActionBusy = false;
    closeDialogOpen = false;
    closeRequest = null;
    if (saved) {
      closeActionBusy = false;
      if (stopSignal.aborted) return;
      await closeAllOpenEditorsNow();
      if (stopSignal.aborted) return;
    }
  }

  function confirmCloseDiscard(): void {
    const request = closeRequest;
    closeDialogOpen = false;
    closeRequest = null;
    if (!request) return;
    if (request.kind === 'file') closeFileNow(request.path, true);
    else void closeAllOpenEditorsNow();
  }

  function handleCloseDialogChange(open: boolean): void {
    closeDialogOpen = open;
    if (!open && !closeActionBusy) closeRequest = null;
  }

  export function requestCloseActive(): void {
    if (editorState.activePath) requestCloseFile(editorState.activePath);
  }

  export function openTimeline(path: string): void {
    const projectRoot = canonicalPath(editorState.projectRoot ?? '');
    const file = editorFileFor(path);
    if (!projectRoot || !file) return;
    void openFileTimeline({ projectRoot, relativePath: file.relativePath });
  }

  export function captureViewStates(paths: readonly string[]): Record<string, object> {
    return codeEditor?.captureViewStates(paths) ?? {};
  }

  /** Linked-worktree inspection tabs are live-only and never belong to SQLite. */
  export function workspaceOwnedPaths(): string[] {
    return editorState.openFiles
      .filter((file) => !readOnlyByPath[file.path] && !file.previewTab)
      .map((file) => file.path);
  }

  export function restoreViewStates(
    files: readonly { path: string; viewState?: object }[]
  ): void {
    const next: Record<string, object> = {};
    for (const file of files) {
      if (file.viewState) next[file.path] = file.viewState;
    }
    restoredViewStates = next;
  }

  function consumeRestoredViewState(path: string): void {
    delete restoredViewStates[path];
  }

  async function cancelSourceReadsForReleasedSession(stopSignal: AbortSignal): Promise<void> {
    if (stopSignal.aborted) return;
    try {
      if (stopSignal.aborted) return;
      await cancelSourceFileReadsFromTauri();
      if (stopSignal.aborted) return;
    } catch {
      // Frontend teardown still releases local ownership if native cannot cancel.
    }
  }

  export function releaseSessionResources(paths: readonly string[]): void {
    const stopSignal = sessionStopController.signal;
    sessionResourceGeneration += 1;
    if (readsInFlight.size > 0) {
      void cancelSourceReadsForReleasedSession(stopSignal);
    }
    sessionStopController.abort();
    sessionStopController = new AbortController();
    publishSourceReadDiagnostics();
    ownerSelectionGeneration += 1;
    inlayHintRequestCount += 1;
    clearDiagnosticsReadyWait();
    clearInlayReadyWait();
    languageServerGate.releaseAll();
    closeRequest = null;
    closeDialogOpen = false;
    sourceIntelligence.setActivePreview(null);
    releaseImagePreview();
    codeEditor?.releaseSessionResources();
    for (const path of paths) {
      sourceIntelligence.releasePreview(path);
      releaseMarkdownView(path);
    }
    const departing = new Set(paths);
    diagnosticsByPath = Object.fromEntries(
      Object.entries(diagnosticsByPath).filter(([path]) => !departing.has(path))
    );
    readOnlyByPath = Object.fromEntries(
      Object.entries(readOnlyByPath).filter(([path]) => !departing.has(path))
    );
    restoredViewStates = {};
    resetEditorState();
  }

  export function refreshOpenFiles(): void {
    for (const file of editorState.openFiles) {
      if (!rasterImageMimeType(file.fileName)) void readFileIntoEditor(recordForPath(file.path), true);
    }
  }

  function retryRead(path: string): void {
    if (closeActionBusy || !rootAvailable) return;
    void readFileIntoEditor(recordForPath(path));
  }

  function handleWorkspaceFileChange(change: { ownedId: string; path: string; recursive?: boolean }): void {
    if (!ownedId || change.ownedId !== ownedId) return;
    const root = editorState.projectRoot;
    if (!root) return;
    const path = workspaceChangePath(root, change.path);
    const paths = change.recursive
      ? editorState.openFiles.map((file) => file.path).filter((filePath) => filePath === path || filePath.startsWith(`${path.replace(/\/+$/, '')}/`))
      : [path];
    for (const changedPath of paths) {
      if (editorFileFor(changedPath)) void readFileIntoEditor(recordForPath(changedPath), true);
    }
  }

  async function reloadConflictedFile(): Promise<void> {
    const file = activeEditorFile();
    if (!file?.preview) return;
    const generation = sessionResourceGeneration;
    markEditorFileLoading(file.path);
    try {
      const preview = await readSourceFromTauri(recordForPath(file.path));
      if (!preview) throw new Error('The file could not be read from here.');
      if (destroyed || generation !== sessionResourceGeneration || !editorFileFor(file.path)) return;
      setEditorFilePreview(file.path, preview);
      discardEditorFileDraft(file.path);
    } catch (error) {
      if (!destroyed && generation === sessionResourceGeneration && editorFileFor(file.path)) {
        setEditorFileError(file.path, `Could not reload this file: ${describeError(error)}`);
      }
    }
  }

  async function overwriteConflictedFile(): Promise<void> {
    const file = activeEditorFile();
    if (!file?.preview || !file.dirty || !file.conflict) return;
    clearEditorFileConflict(file.path);
    await saveEditorFile(file.path);
  }

  /** The code editor followed a definition or a reference into another file. */
  function navigateToExternalSource(request: {
    path: string;
    line: number;
    column: number;
  }): void {
    if (activeFileReadOnly) return;
    openPath(request.path, request.line);
  }

  function handleSymbolsChange(symbols: SourceSymbol[]): void {
    setEditorSymbols(symbols);
  }

  function runDotnetWorkspace(action: DotnetWorkspaceAction): void | Promise<void> {
    const root = editorState.projectRoot;
    if (!root || activeFileReadOnly || !onStartWorkspaceCommand) return;
    return onStartWorkspaceCommand(dotnetWorkspaceSessionRequest(action, root));
  }

  /**
   * Fetch and start the code editor for the file on screen.
   *
   * This is the only route to `ensureCodeEditor` other than the language switch,
   * and it is deliberately tied to the panel being in front rather than to a
   * file arriving in the strip. Even the lightweight editor is kept off the
   * startup path when a restored file is not visible.
   */
  $effect(() => {
    if (!showing) {
      releaseImagePreview();
      return;
    }
    if (!editorState.activePath) return;
    const entry = activeEditorFile();
    if (
      entry
      && (needsRead(entry) || imageNeedsRead(entry))
    ) {
      void readFileIntoEditor(recordForPath(entry.path));
    }
    if (rasterImageMimeType(entry?.fileName)) return;
    void ensureCodeEditor();
  });

  /**
   * Keep the top strip's copy of the language-server controls in step.
   *
   * It calls nothing: it copies state the panel already holds into the shared
   * module the top strip reads, so the chip and the switch can sit beside the
   * run button while this panel stays their only owner. The status pipeline is
   * not run twice.
   */
  $effect(() => {
    publishLanguageIntelligenceBar({
      language: activeFile?.language ?? null,
      status: languageServerStatus,
      fullMode,
      busy: false,
      hasProject: Boolean(editorState.projectRoot) && !activeFileReadOnly,
      title: languageIntelligenceTitle
    });
  });

  onMount(() => {
    // Subscribing costs nothing and loads nothing; it just means a click in the
    // explorer made before this panel was ever shown still opens its file.
    const unsubscribe = onOpenFile(handleOpenFileRequest);
    const unsubscribeWorkspaceFileChanges = onWorkspaceFileChange(handleWorkspaceFileChange);

    // Listening for status updates is likewise free, and it is the only way the
    // chip ever changes after a file opens — nothing here polls.
    const unsubscribeStatus = sourceIntelligence.subscribeToLanguageServerStatus(
      handleLanguageServerStatus
    );
    const unsubscribeReferenceCounts = sourceIntelligence.subscribeToReferenceCountUpdates(
      handleReferenceCountUpdate
    );

    return () => {
      destroyed = true;
      sessionStopController.abort();
      releaseImagePreview();
      unsubscribeReferenceCounts();
      unsubscribeStatus();
      // Anything still waiting on the server has nowhere to go now.
      clearDiagnosticsReadyWait();
      clearInlayReadyWait();
      languageServerGate.releaseAll();
      // With no panel there is nothing truthful to show in the top strip.
      clearLanguageIntelligenceBar();
      unsubscribe();
      unsubscribeWorkspaceFileChanges();
      resetEditorState();
    };
  });
</script>

<div class="editor-panel">
  {#if editorState.openFiles.length === 0}
    <div class="editor-empty">
      <p class="empty-title">No file open</p>
      <p class="empty-hint">Open a file from the explorer or palette.</p>
    </div>
  {:else}
    <div class="editor-header">
      <!-- The open file's path on the left, its own controls on the right. The
           language-server switch sits in this editor's status bar. -->
      {#if activeFile && breadcrumbParts.length > 0}
        <!-- One folder's folders and files, at one depth of the open menu chain.
             A folder row opens its own submenu, so any depth is reachable
             without leaving the menu; a file row opens the file. -->
        {#snippet folderEntries(directory: string, depth: number)}
          {@const menu = breadcrumbChain[depth]}
          {#if menu?.directory !== directory || menu.entries === null}
            <DropdownMenu.Item disabled><WorkingSpinner size={12} />Loading…</DropdownMenu.Item>
          {:else if menu.error}
            <DropdownMenu.Item disabled>{menu.error}</DropdownMenu.Item>
          {:else if menu.entries.every((entry) => entry.excluded)}
            <DropdownMenu.Item disabled>No files or folders</DropdownMenu.Item>
          {:else}
            {#each menu.entries.filter((entry) => !entry.excluded) as entry (entry.path)}
              {#if entry.isDirectory}
                <DropdownMenu.Sub onOpenChange={(open) => { if (open) void openBreadcrumbMenu(entry.path, depth + 1); else closeBreadcrumbMenu(entry.path, depth + 1); }}>
                  <DropdownMenu.SubTrigger>
                    <Folder class="size-[14px]" />
                    <span class="truncate">{entry.name}</span>
                  </DropdownMenu.SubTrigger>
                  <DropdownMenu.Portal>
                    <DropdownMenu.SubContent class={BREADCRUMB_SUBMENU}>
                      {@render folderEntries(entry.path, depth + 1)}
                    </DropdownMenu.SubContent>
                  </DropdownMenu.Portal>
                </DropdownMenu.Sub>
              {:else}
                <DropdownMenu.Item onSelect={() => openBreadcrumbFile(entry)}>
                  <FileIcon fileName={entry.name} size={14} />
                  <span class="truncate">{entry.name}</span>
                </DropdownMenu.Item>
              {/if}
            {/each}
          {/if}
        {/snippet}
        <div class="editor-breadcrumb-row">
          <Breadcrumb.Root>
            <Breadcrumb.List class="m-0 list-none flex-nowrap gap-1 p-0 text-[13px]">
              {#each breadcrumbParts as part, index (part)}
                {#if breadcrumbHidden.includes(part)}
                  {#if part === breadcrumbHidden[0]}
                    <Breadcrumb.Separator class="flex shrink-0 items-center" />
                    <Breadcrumb.Item class="shrink-0">
                      <DropdownMenu.Root>
                        <DropdownMenu.Trigger class="breadcrumb-trigger" aria-label="Show hidden folders">…</DropdownMenu.Trigger>
                        <DropdownMenu.Content class={BREADCRUMB_MENU} align="start">
                          {#each breadcrumbHidden as hidden (hidden)}
                            <DropdownMenu.Sub onOpenChange={(open) => { if (open) void openBreadcrumbMenu(hidden, 0); else closeBreadcrumbMenu(hidden, 0); }}>
                              <DropdownMenu.SubTrigger>
                                <Folder class="size-[14px]" />
                                <span class="truncate">{hidden.split('/').at(-1)}</span>
                              </DropdownMenu.SubTrigger>
                              <DropdownMenu.Portal>
                                <DropdownMenu.SubContent class={BREADCRUMB_SUBMENU}>
                                  {@render folderEntries(hidden, 0)}
                                </DropdownMenu.SubContent>
                              </DropdownMenu.Portal>
                            </DropdownMenu.Sub>
                          {/each}
                        </DropdownMenu.Content>
                      </DropdownMenu.Root>
                    </Breadcrumb.Item>
                  {/if}
                {:else}
                  {@const directory = part === activeFile.path ? part.slice(0, part.lastIndexOf('/')) : part}
                  <!-- The file's own segment browses the folder it sits in. -->
                  {#if index > 0}<Breadcrumb.Separator class="flex shrink-0 items-center" />{/if}
                  <!-- The project name and the file name give way first; the
                       folders between them keep their width. -->
                  <Breadcrumb.Item class={index === 0 ? 'min-w-0 max-w-36 shrink' : index === breadcrumbParts.length - 1 ? 'min-w-0 shrink' : 'shrink-0'}>
                    <DropdownMenu.Root onOpenChange={(open) => { if (open) void openBreadcrumbMenu(directory, 0); else closeBreadcrumbMenu(directory, 0); }}>
                      <DropdownMenu.Trigger class="breadcrumb-trigger" aria-label={`Browse ${part.split('/').at(-1)}`}>
                        {part.split('/').at(-1)}
                      </DropdownMenu.Trigger>
                      <DropdownMenu.Content class={BREADCRUMB_MENU} align="start">
                        {@render folderEntries(directory, 0)}
                      </DropdownMenu.Content>
                    </DropdownMenu.Root>
                  </Breadcrumb.Item>
                {/if}
              {/each}
            </Breadcrumb.List>
          </Breadcrumb.Root>
        </div>
      {/if}
      <div class="editor-controls">
        <!-- Markdown and HTML read two ways, so the file says which one it is on.
             Source is the ordinary editor; Preview is the same document rendered. -->
        {#if activeFileIsMarkdown || activeFileIsHtml}
          <span data-testid="markdown-view-toggle" class="shrink-0">
            <SegmentedControl
              size="sm"
              items={MARKDOWN_VIEW_ITEMS}
              value={markdownView}
              aria-label={activeFileIsHtml ? 'HTML view' : 'Markdown view'}
              onValueChange={(value) => setMarkdownView(value as MarkdownView)}
            />
          </span>
        {/if}
        <!-- Save and close-all live behind one quiet "more" button; the open
             files' own close buttons are on their tabs in the top row. -->
        <DropdownMenu.Root>
          <DropdownMenu.Trigger
            class={buttonVariants({ variant: 'ghost', size: 'icon-sm' })}
            aria-label="More editor actions"
          >
            <Ellipsis class="size-[14px]" aria-hidden="true" />
          </DropdownMenu.Trigger>
          <DropdownMenu.Content align="end">
            <DropdownMenu.Item
              disabled={!activeFile?.dirty || Boolean(activeFile?.conflict) || activeFileReadOnly}
              onSelect={() => void saveActiveFile()}
            >
              <Save class="size-3.5" aria-hidden="true" />
              Save active file
            </DropdownMenu.Item>
            <DropdownMenu.Item onSelect={closeAllOpenEditors}>
              <X class="size-3.5" aria-hidden="true" />
              Close all open editors
            </DropdownMenu.Item>
          </DropdownMenu.Content>
        </DropdownMenu.Root>
      </div>
    </div>

    <div class="editor-canvas">
      {#if activeFile?.conflict}
        <div class="editor-conflict" role="alert">
          <span>{activeFile.conflict}</span>
          <button type="button" class="retry" onclick={() => void reloadConflictedFile()}>Reload from disk</button>
          <button type="button" class="retry" onclick={() => void overwriteConflictedFile()}>Overwrite</button>
        </div>
      {/if}
      {#if activeFileReadOnly && activeFile?.preview && !activeImageMimeType}
        <div class="editor-conflict" role="status"><span>Outside this session's project, so it opened read-only.</span><button type="button" class="retry" onclick={() => unlockActiveFile()}>Edit anyway</button></div>
      {/if}
      {#if activeFile?.error && activeFile.preview}
        <div class="editor-conflict" role="alert">
          <span>{activeFile.error}</span>
          <button type="button" class="retry" onclick={() => retryRead(activeFile.path)}>Try again</button>
        </div>
      {/if}
      {#if editorLoadError}
        <p class="canvas-message error">{editorLoadError}</p>
      {:else if activeFile?.error && activeFileMissing}
        <p class="canvas-message">File no longer exists at {activeFile.path}</p>
      {:else if activeFile?.error && !activeFile.preview}
        <div class="canvas-message error">
          <p>{activeFile.error}</p>
          <button
            type="button"
            class="retry"
            onclick={() => activeFile && retryRead(activeFile.path)}
          >
            Try again
          </button>
        </div>
      {:else if !rootAvailable}
        <p class="canvas-message">Checkout/Worktree deleted.</p>
      {:else if activeFile && activeImageMimeType}
        {#if imagePreview?.path === activeFile.path}
          <div class="image-preview">
            <img src={imagePreview.url} alt={activeFile.fileName} />
          </div>
        {:else}
          <p class="canvas-message">Reading {activeFile.fileName}…</p>
        {/if}
      {:else if showing && activeFile?.preview && activeFileIsHtml && markdownView === 'rendered'}
        <!-- Scripts run so script-drawn pages render, but the frame keeps an
             opaque origin: never pair allow-scripts with allow-same-origin, or
             the page could reach this app. It shows the unsaved draft too. -->
        <iframe
          class="html-preview"
          title={`Preview of ${activeFile.fileName}`}
          sandbox="allow-scripts"
          srcdoc={activeFile.draftContent ?? activeFile.preview.content}
        ></iframe>
      {:else if activeFile && activePreview}
        <div class="code-editor-slot" class:behind-preview={markdownPreviewShown}>
        {#if CodeEditor}
          <CodeEditor
            bind:this={codeEditor}
            {...sourceIntelligence.callbacks}
            onInlayHintLookup={activeFileReadOnly ? undefined : lookupInlayHintsWhenServerCanAnswer}
            onCodeActionLookup={fullMode && !activeFileReadOnly ? lookupCodeActions : undefined}
            onWorkspaceEditAction={fullMode && !activeFileReadOnly ? applyExternalWorkspaceEdits : undefined}
            preview={activePreview}
            content={activeFile.draftContent ?? activeFile.preview?.content ?? ''}
            editable={rootAvailable && !activeFileReadOnly && !closeActionBusy}
            visible={showing}
            languageServerRoot={activeLanguageServerRoot}
            loading={activeFile.loading}
            targetLine={activeFile.targetLine}
            targetLineRequestId={activeFile.targetLineRequestId}
            externalDiagnostics={activeFileReadOnly ? NO_DIAGNOSTICS : diagnosticsByPath[activeFile.path] ?? NO_DIAGNOSTICS}
            {restoredViewStates}
            onExternalNavigation={activeFileReadOnly ? undefined : navigateToExternalSource}
            onLanguageServerReady={handleLanguageServerReady}
            onContentChange={activeFileReadOnly ? undefined : updateActiveDraft}
            onRestoredViewStateConsumed={consumeRestoredViewState}
            onSaveRequest={() => void saveActiveFile()}
            onSymbolsChange={handleSymbolsChange}
            onDotnetBuildRequest={onStartWorkspaceCommand ? () => runDotnetWorkspace('build') : undefined}
            onDotnetTestRequest={onStartWorkspaceCommand ? () => runDotnetWorkspace('test') : undefined}
          />
        {:else}
          <p class="canvas-message"><WorkingSpinner size={14} /> Starting the code editor…</p>
        {/if}
        </div>
        {#if markdownPreviewShown && activeFile.preview && MarkdownPreview}
          {#key activeFile.path}
            <MarkdownPreview
              path={activeFile.path}
              projectRoot={editorState.projectRoot}
              content={activeFile.draftContent ?? activeFile.preview.content}
              fileName={activeFile.fileName}
              readOnly={activeFileReadOnly}
              scrollTop={markdownScrollByPath.get(activeFile.path) ?? 0}
              onScroll={(top) => { if (showing) markdownScrollByPath.set(activeFile.path, top); }}
              onChange={updateActiveDraft}
              onSave={() => void saveActiveFile()}
            />
          {/key}
        {:else if markdownPreviewShown}
          <p class="canvas-message">Starting the preview…</p>
        {/if}
      {:else}
        <p class="canvas-message"><WorkingSpinner size={14} /> Reading {activeFile?.fileName ?? 'file'}…</p>
      {/if}
    </div>

    <div class="editor-status">
      <!-- One quiet row: the language and what it knows on the left, the
           Supercharged switch and the file's state on the right. Only the
           symbol count gives way when the pane is narrow. -->
      <b class="status-language">{activeFile?.language ?? ''}</b>
      {#if editorState.symbols.length > 0}
        <span class="status-symbols">
          {editorState.symbols.length}
          {editorState.symbols.length === 1 ? 'symbol' : 'symbols'}
        </span>
      {/if}
      <span class="status-spacer"></span>
      <LanguageIntelligenceControls />
      <span class="status-state" title={activeFile?.conflict ?? undefined}>
        {#if activeFileReadOnly}
          outside project
        {:else if activeImageMimeType}
          preview
        {:else if activeFile?.saving}
          saving
        {:else if activeFile?.conflict}
          conflict
        {:else if activeFile?.dirty}
          unsaved
        {:else}
          editable
        {/if}
      </span>
    </div>
  {/if}
</div>

{#if closeRequest}
  <AlertDialog.Root open={closeDialogOpen} onOpenChange={handleCloseDialogChange}>
    <AlertDialog.Content>
      <AlertDialog.Header>
        <AlertDialog.Title>Unsaved changes</AlertDialog.Title>
        <AlertDialog.Description>
          {closeRequest.kind === 'all'
            ? 'Some open files have unsaved drafts. Save all before closing them?'
            : 'This file has an unsaved draft. Save it before closing?'}
        </AlertDialog.Description>
      </AlertDialog.Header>
      <AlertDialog.Footer>
        <AlertDialog.Cancel disabled={closeActionBusy}>Cancel</AlertDialog.Cancel>
        <AlertDialog.Action
          disabled={closeActionBusy || !rootAvailable}
          onclick={() => void confirmCloseSave()}
        >{closeRequest.kind === 'all' ? 'Save All' : 'Save'}</AlertDialog.Action>
        <AlertDialog.Action
          variant="destructive"
          disabled={closeActionBusy}
          onclick={confirmCloseDiscard}
        >{closeRequest.kind === 'all' ? 'Discard All' : 'Discard'}</AlertDialog.Action>
      </AlertDialog.Footer>
    </AlertDialog.Content>
  </AlertDialog.Root>
{/if}

<style>
  .editor-panel {
    display: flex;
    flex-direction: column;
    height: 100%;
    width: 100%;
    min-width: 0;
    overflow: hidden;
    /* No fill of its own. This is a panel body in normal flow covering nothing,
       and filling it with the backdrop colour painted a black rectangle over
       the card underneath — which carries the surface colour and the light
       gradient down its top edge. Most visible with no file open, where the
       body IS the whole panel. The header and status rows still state their own
       surface, because those are bands rather than the body. */
    color: var(--color-text);
    font-family: ui-sans-serif, -apple-system, system-ui, sans-serif;
  }

  .editor-empty {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 4px;
    height: 100%;
    color: var(--color-text-2);
    font-size: 13px;
    user-select: none;
  }

  .editor-empty p {
    margin: 0;
  }

  .empty-hint {
    color: var(--color-text-3);
    font-size: 12px;
  }

  /* The editor's header band: the open file's path on the left, its own
   * controls pinned to the right. The open files themselves are tabs in the top
   * row. 44px, the same band as the rail and drawer headers; no rule under it,
   * the panel surface carries on into the body. */
  .editor-header {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 8px;
    flex: 0 0 auto;
    box-sizing: border-box;
    width: 100%;
    min-width: 0;
    height: 44px;
    overflow: hidden;
    background: var(--color-surface);
    padding: 0 8px 0 10px;
  }

  /* The trail never scrolls: deep paths fold into "…", and what is left
   * truncates at the project name and the file name. */
  .editor-breadcrumb-row {
    flex: 1 1 auto;
    min-width: 0;
    overflow: hidden;
  }

  .editor-breadcrumb-row :global(.breadcrumb-trigger) {
    min-height: 24px;
    min-width: 0;
    max-width: 240px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    border-radius: 9999px;
    padding: 0 6px;
    color: var(--color-text-2);
    font-size: 13px;
  }

  .editor-breadcrumb-row :global(li:last-child .breadcrumb-trigger) {
    color: var(--color-text);
    font-weight: 500;
  }

  .editor-breadcrumb-row :global(.breadcrumb-trigger:hover),
  .editor-breadcrumb-row :global(.breadcrumb-trigger[data-state='open']) {
    background: var(--color-elevated);
    color: var(--color-text);
  }

  .editor-breadcrumb-row :global(.breadcrumb-trigger:focus-visible) {
    outline: 2px solid var(--color-focus);
    outline-offset: -2px;
  }

  .editor-controls {
    display: flex;
    align-items: center;
    gap: 8px;
    flex: 0 0 auto;
    min-width: max-content;
  }

  .editor-canvas {
    position: relative;
    flex: 1 1 auto;
    min-height: 0;
    overflow: hidden;
  }

  .code-editor-slot {
    display: contents;
  }

  /* Kept laid out (so its scroll position survives) but not painted and not
     reachable by pointer or keyboard while Preview covers it. */
  .code-editor-slot.behind-preview {
    position: absolute;
    inset: 0;
    display: block;
    visibility: hidden;
  }

  .editor-conflict {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 12px;
    color: var(--color-danger);
    background: var(--color-surface);
    border-bottom: 1px solid var(--color-border);
  }

  .editor-conflict span { flex: 1; min-width: 0; }

  .canvas-message {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 8px;
    height: 100%;
    margin: 0;
    color: var(--color-text-2);
    font-size: 13px;
    text-align: center;
    padding: 0 16px;
  }

  .canvas-message p {
    margin: 0;
  }

  .canvas-message.error {
    color: var(--color-bad);
  }

  .image-preview {
    display: grid;
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: auto;
    place-items: center;
    padding: 24px;
    background:
      linear-gradient(45deg, rgb(255 255 255 / 3%) 25%, transparent 25%),
      linear-gradient(-45deg, rgb(255 255 255 / 3%) 25%, transparent 25%),
      linear-gradient(45deg, transparent 75%, rgb(255 255 255 / 3%) 75%),
      linear-gradient(-45deg, transparent 75%, rgb(255 255 255 / 3%) 75%);
    background-position: 0 0, 0 8px, 8px -8px, -8px 0;
    background-size: 16px 16px;
  }

  .html-preview {
    display: block;
    width: 100%;
    height: 100%;
    border: 0;
    /* The page paints its own background; an unstyled one reads as white. */
    background: #ffffff;
  }

  .image-preview img {
    display: block;
    max-width: 100%;
    max-height: 100%;
    object-fit: contain;
  }

  .retry {
    background: var(--color-elevated);
    border: 0;
    border-radius: 5px;
    color: var(--color-text-2);
    cursor: pointer;
    font-family: var(--font-mono);
    font-size: 12px;
    padding: 2px 7px;
  }

  .retry:hover {
    color: var(--color-text);
    background: var(--color-hover);
  }

  /* The footer: 12px muted text in one row that never wraps. Everything but
   * the symbol count keeps its width; the count is cut with an ellipsis. */
  .editor-status {
    display: flex;
    align-items: center;
    gap: 12px;
    flex: 0 0 auto;
    box-sizing: border-box;
    height: 32px;
    min-width: 0;
    overflow: hidden;
    background: var(--color-surface);
    color: var(--color-text-3);
    font-size: 12px;
    white-space: nowrap;
    padding: 0 16px;
  }

  .editor-status > * {
    flex: 0 0 auto;
  }

  .status-language {
    color: var(--color-text-2);
    font-weight: 500;
    text-transform: capitalize;
  }

  .editor-status > .status-symbols {
    flex: 0 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .editor-status > .status-spacer {
    flex: 1 1 0;
    min-width: 0;
  }
</style>
