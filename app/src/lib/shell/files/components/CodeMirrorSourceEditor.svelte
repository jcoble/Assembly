<script lang="ts">
  import {
    autocompletion,
    closeBrackets,
    type CompletionContext
  } from '@codemirror/autocomplete';
  import { history, redo, redoDepth, undo, undoDepth } from '@codemirror/commands';
  import {
    bracketMatching,
    codeFolding,
    defaultHighlightStyle,
    foldGutter,
    indentOnInput,
    syntaxHighlighting
  } from '@codemirror/language';
  import { forEachDiagnostic, lintGutter, setDiagnostics, type Diagnostic } from '@codemirror/lint';
  import { formatDocument, renameSymbol } from '@codemirror/lsp-client';
  import { highlightSelectionMatches, selectSelectionMatches } from '@codemirror/search';
  import {
    EditorState,
    Compartment,
    RangeSet,
    StateEffect,
    StateField,
    Transaction,
    type Extension
  } from '@codemirror/state';
  import {
    crosshairCursor,
    Decoration,
    drawSelection,
    dropCursor,
    EditorView,
    highlightActiveLine,
    highlightActiveLineGutter,
    highlightSpecialChars,
    hoverTooltip,
    keymap,
    lineNumbers,
    rectangularSelection,
    showTooltip,
    type Tooltip
  } from '@codemirror/view';
  import { vscodeKeymap } from '@replit/codemirror-vscode-keymap';
  import { onDestroy, onMount } from 'svelte';

  import * as ContextMenu from '$lib/components/ui/context-menu/index.js';
  import {
    addCodeMirrorEditorView,
    setCodeMirrorEditorStateCount,
    setCodeMirrorDocBytes,
    setCodeMirrorUndoDepth,
    textBytes
  } from '$lib/shell/resourceDiagnostics.svelte';
  import { loadCodeMirrorLanguage } from '$lib/shell/files/editor/codeMirrorLanguage';
  import {
    limitSavedEditorHistory,
    savedHistoryDepthLimit
  } from '$lib/shell/files/editor/codeMirrorHistory';
  import {
    codeMirrorCodeLens,
    showCodeMirrorReferences
  } from '$lib/shell/files/editor/codeMirrorCodeLens';
  import {
    codeMirrorSemanticTokens,
    semanticTokenDecorations,
    setCodeMirrorSemanticTokens
  } from '$lib/shell/files/editor/codeMirrorSemanticTokens';
  import {
    codeMirrorThemeForAppearance,
    loadCodeMirrorTheme
  } from '$lib/shell/files/editor/codeMirrorTheme';
  import {
    connectCodeMirrorCsharpClient,
    setNativeCsharpActiveRoot
  } from '$lib/shell/files/editor/csharpLanguageClient';
  import { settings } from '$lib/shell/settings/settingsStore.svelte';
  import type {
    SourceIntelligenceCallbacks,
    SourceLookupRequest
  } from '$lib/shell/files/editor/sourceIntelligence';
  import {
    extractSourceSemanticTokens,
    extractSourceSymbols,
    type SourceCodeAction,
    type SourceCodeActionDiagnostic,
    type SourceCodeActionLookupRequest,
    type SourceDiagnostic,
    type SourcePreview,
    type SourceSemanticToken,
    type SourceSymbol
  } from '$lib/sourceData';

  type StoredViewState = {
    anchor: number;
    head: number;
    scrollTop: number;
  };

  interface Props extends Partial<SourceIntelligenceCallbacks> {
    preview: SourcePreview;
    content?: string;
    editable?: boolean;
    visible?: boolean;
    languageServerRoot?: string | null;
    externalDiagnostics?: SourceDiagnostic[];
    restoredViewStates?: Record<string, object>;
    loading?: boolean;
    targetLine?: number | null;
    targetLineRequestId?: number;
    onContentChange?: (content: string) => void;
    onCodeActionLookup?: (
      request: SourceCodeActionLookupRequest
    ) => SourceCodeAction[] | Promise<SourceCodeAction[] | null> | null | undefined;
    onWorkspaceEditAction?: (action: SourceCodeAction) => void | Promise<void>;
    onRestoredViewStateConsumed?: (path: string) => void;
    onExternalNavigation?: (request: { path: string; line: number; column: number }) => void | Promise<void>;
    onLanguageServerReady?: () => void;
    onSaveRequest?: () => void;
    onSymbolsChange?: (symbols: SourceSymbol[]) => void;
    onDotnetBuildRequest?: () => void | Promise<void>;
    onDotnetTestRequest?: () => void | Promise<void>;
  }

  let {
    preview,
    content,
    editable = false,
    visible = true,
    languageServerRoot = null,
    externalDiagnostics = [],
    restoredViewStates = {},
    loading = false,
    targetLine = null,
    targetLineRequestId = 0,
    onContentChange,
    onCodeActionLookup,
    onRestoredViewStateConsumed,
    onCompletionLookup,
    onDefinitionLookup,
    onExternalNavigation,
    onExternalPreviewLookup,
    onLanguageServerReady,
    onHoverLookup,
    onCodeLensAnchorLookup,
    onReferenceLookup,
    onReferenceCountLookup,
    onReferenceCountsOutOfDate,
    onSemanticTokensLookup,
    onSaveRequest,
    onSymbolsChange,
    onWorkspaceEditAction,
    onDotnetBuildRequest,
    onDotnetTestRequest
  }: Props = $props();

  let host: HTMLDivElement;
  let view: EditorView | null = null;
  let editorMenuStatus = $state({
    canUndo: false,
    canRedo: false,
    canCut: false,
    canCopy: false,
    canPaste: false,
    canSelectAll: false,
    canChangeOccurrences: false,
    canFormat: false,
    canRename: false,
    canDefinition: false,
    canReferences: false
  });
  let editorMenuReferenceRequest: SourceLookupRequest | null = null;
  let currentPath = '';
  let applyingContent = false;
  let languageGeneration = 0;
  let requestedLanguageKey = '';
  let loadedLanguageKey = '';
  let lspGeneration = 0;
  let requestedLspKey = '';
  let loadedLspKey = '';
  let lspSession: ReturnType<typeof connectCodeMirrorCsharpClient> | null = null;
  const language = new Compartment();
  const intelligence = new Compartment();
  const themeExtension = new Compartment();
  const editing = new Compartment();
  const codeLens = new Compartment();
  const sessionViewStates = new Map<string, StoredViewState>();
  const sessionEditorStates = new Map<string, EditorState>();
  /** The disk revision of the file the view last loaded. */
  let shownRevision = '';
  /** How many tabs' editor states one session keeps; oldest go first. */
  const retainedEditorStateLimit = 24;
  let themeGeneration = 0;
  let requestedThemeKey = '';
  let loadedThemeKey = '';
  let codeActionGeneration = 0;
  let codeLensGeneration = 0;
  let semanticTokenGeneration = 0;
  let semanticTokenStopController: AbortController | null = null;

  function currentThemeKey(): string {
    const editor = settings.editor;
    return `${settings.appearance.themeId}\0${editor.fontFamily}\0${editor.fontSize}\0${editor.lineHeight}\0${editor.fontLigatures}`;
  }

  function currentEditorTheme(): Extension {
    return codeMirrorThemeForAppearance({
      fontFamily: settings.editor.fontFamily,
      fontSize: settings.editor.fontSize,
      lineHeight: settings.editor.lineHeight,
      fontLigatures: settings.editor.fontLigatures
    });
  }

  type CodeActionMenu = { path: string; root: string; pos: number; actions: SourceCodeAction[] };
  const setCodeActionMenu = StateEffect.define<CodeActionMenu | null>();
  const codeActionMenuState = StateField.define<CodeActionMenu | null>({
    create: () => null,
    update(menu, transaction) {
      for (const effect of transaction.effects) {
        if (effect.is(setCodeActionMenu)) menu = effect.value;
      }
      return transaction.docChanged || !transaction.startState.selection.eq(transaction.newSelection)
        ? null
        : menu;
    },
    provide: (field) => showTooltip.computeN([field], (state): readonly Tooltip[] => {
      const menu = state.field(field);
      if (!menu) return [];
      return [{
        pos: menu.pos,
        above: true,
        create: (editor) => {
          const dom = document.createElement('div');
          dom.className = 'cm-code-actions';
          dom.setAttribute('role', 'menu');
          dom.setAttribute('aria-label', 'Code actions');
          const listeners: Array<{
            button: HTMLButtonElement;
            preventMouseDown: (event: MouseEvent) => void;
            runAction: () => void;
          }> = [];
          for (const action of menu.actions) {
            const button = document.createElement('button');
            button.type = 'button';
            button.setAttribute('role', 'menuitem');
            button.textContent = action.title;
            const preventMouseDown = (event: MouseEvent): void => event.preventDefault();
            const runAction = (): void => applyCodeAction(editor, action);
            button.addEventListener('mousedown', preventMouseDown);
            button.addEventListener('click', runAction);
            listeners.push({ button, preventMouseDown, runAction });
            dom.append(button);
          }
          return {
            dom,
            destroy: () => {
              for (const { button, preventMouseDown, runAction } of listeners) {
                button.removeEventListener('mousedown', preventMouseDown);
                button.removeEventListener('click', runAction);
              }
              listeners.length = 0;
            }
          };
        }
      }];
    })
  });

  function clearCodeActions(): void {
    codeActionGeneration += 1;
    if (view) view.dispatch({ effects: setCodeActionMenu.of(null) });
  }

  function configureCodeLens(): void {
    if (!view || !currentPath || currentPath !== preview.path) return;
    if (!visible || !onReferenceLookup) {
      clearCodeLens();
      return;
    }
    const generation = ++codeLensGeneration;
    const extension = codeMirrorCodeLens({
      preview: { ...preview, content: view.state.doc.toString() },
      enabled: Boolean(languageServerRoot && onCodeLensAnchorLookup && onReferenceCountLookup),
      onAnchorLookup: onCodeLensAnchorLookup,
      onCount: onReferenceCountLookup,
      onReferences: onReferenceLookup,
      onPreview: async (target) => {
        const targetPreview = target.path === preview.path && view
          ? { ...preview, content: view.state.doc.toString() }
          : await onExternalPreviewLookup?.(target);
        if (!targetPreview) return null;
        return {
          preview: targetPreview,
          extensions: [
            currentEditorTheme(),
            await loadCodeMirrorLanguage(targetPreview.language)
          ]
        };
      },
      onOpenReference: async (target) => {
        if (!view || generation !== codeLensGeneration || currentPath !== preview.path) return;
        if (target.path === preview.path) {
          const at = linePosition(view.state, target.line, target.column);
          view.dispatch({ selection: { anchor: at }, effects: EditorView.scrollIntoView(at, { y: 'center' }) });
          view.focus();
        } else {
          await onExternalNavigation?.({ path: target.path, line: target.line, column: target.column });
        }
      },
      onDotnetAction: onDotnetBuildRequest && onDotnetTestRequest
        ? (action) => action === 'build' ? onDotnetBuildRequest() : onDotnetTestRequest()
        : undefined
    });
    view.dispatch({ effects: codeLens.reconfigure(extension) });
  }

  function clearCodeLens(): void {
    codeLensGeneration += 1;
    if (view) view.dispatch({ effects: codeLens.reconfigure([]) });
  }

  function replaceSemanticTokens(tokens: readonly SourceSemanticToken[]): void {
    if (!view) return;
    view.dispatch({
      effects: setCodeMirrorSemanticTokens.of(semanticTokenDecorations(view.state, tokens))
    });
  }

  function clearSemanticTokens(): void {
    semanticTokenGeneration += 1;
    semanticTokenStopController?.abort();
    semanticTokenStopController = null;
    replaceSemanticTokens([]);
  }

  async function refreshSemanticTokens(): Promise<void> {
    if (!view || !visible || !currentPath || currentPath !== preview.path) return;
    semanticTokenStopController?.abort();
    const stopController = new AbortController();
    semanticTokenStopController = stopController;
    const generation = ++semanticTokenGeneration;
    const path = currentPath;
    const content = view.state.doc.toString();
    const tokenPreview = { ...preview, content };

    replaceSemanticTokens(extractSourceSemanticTokens(tokenPreview, content));
    if (!languageServerRoot || !onSemanticTokensLookup || stopController.signal.aborted) return;

    try {
      const tokens = await onSemanticTokensLookup(tokenPreview, stopController.signal);
      if (
        stopController.signal.aborted || !view || generation !== semanticTokenGeneration ||
        currentPath !== path || preview.path !== path || view.state.doc.toString() !== content
      ) return;
      if (tokens.length > 0) replaceSemanticTokens(tokens);
    } catch {
      // The syntax-derived tokens already on screen remain the safe fallback.
    }
  }

  function desiredContent(): string {
    return content ?? preview.content;
  }

  function publishRetainedEditorDiagnostics(): void {
    setCodeMirrorEditorStateCount(sessionEditorStates.size);
    setCodeMirrorUndoDepth(
      [...sessionEditorStates.values()].reduce((total, state) => total + undoDepth(state), 0)
    );
  }

  /**
   * Record `state` for `path` as the most recently used, then drop the oldest
   * entries beyond the cap. Each entry is a whole document plus its undo
   * history, and a closed tab with unsaved edits keeps its state on purpose, so
   * without a cap one session's tabs grow without limit. `Map.set` on a key
   * that is already there does not move it, hence the delete first.
   */
  function rememberEditorState(path: string, state: EditorState, limitHistory = false): void {
    sessionEditorStates.delete(path);
    sessionEditorStates.set(
      path,
      limitHistory ? limitSavedEditorHistory(state, editorExtensions) : state
    );
    while (sessionEditorStates.size > retainedEditorStateLimit) {
      let oldest: string | null = null;
      for (const candidate of sessionEditorStates.keys()) {
        if (candidate === currentPath) continue;
        oldest = candidate;
        break;
      }
      if (oldest === null) break;
      // Removes the view state too, so the two maps stay in step.
      disposeTabModel(oldest);
    }
    publishRetainedEditorDiagnostics();
  }

  function editingExtensions(): Extension {
    return [EditorState.readOnly.of(!editable), EditorView.editable.of(editable)];
  }

  function languageKey(path: string, languageId: string): string {
    return `${path}\0${languageId}`;
  }

  function clearLanguageSupport(): void {
    languageGeneration += 1;
    requestedLanguageKey = '';
    loadedLanguageKey = '';
    if (view) view.dispatch({ effects: language.reconfigure([]) });
  }

  async function loadVisibleLanguageSupport(): Promise<void> {
    if (!view || !visible || !currentPath || currentPath !== preview.path) return;
    const path = currentPath;
    const languageId = preview.language;
    const key = languageKey(path, languageId);
    if (key === loadedLanguageKey || key === requestedLanguageKey) return;
    requestedLanguageKey = key;
    const generation = ++languageGeneration;
    try {
      const extension = await loadCodeMirrorLanguage(languageId);
      if (
        !view ||
        !visible ||
        generation !== languageGeneration ||
        currentPath !== path ||
        preview.path !== path ||
        preview.language !== languageId
      ) return;
      requestedLanguageKey = '';
      loadedLanguageKey = key;
      view.dispatch({ effects: language.reconfigure(extension) });
    } catch {
      if (generation === languageGeneration && requestedLanguageKey === key) requestedLanguageKey = '';
    }
  }

  function clearThemeSupport(): void {
    themeGeneration += 1;
    requestedThemeKey = '';
    loadedThemeKey = '';
    if (view) view.dispatch({ effects: themeExtension.reconfigure(currentEditorTheme()) });
  }

  async function loadVisibleThemeSupport(): Promise<void> {
    if (!view || !visible || !currentPath || currentPath !== preview.path) return;
    const themeId = settings.appearance.themeId;
    const themeKey = currentThemeKey();
    if (themeKey === loadedThemeKey || themeKey === requestedThemeKey) return;
    requestedThemeKey = themeKey;
    const generation = ++themeGeneration;
    try {
      const extension = await loadCodeMirrorTheme(themeId, {
        fontFamily: settings.editor.fontFamily,
        fontSize: settings.editor.fontSize,
        lineHeight: settings.editor.lineHeight,
        fontLigatures: settings.editor.fontLigatures
      });
      if (
        !view ||
        !visible ||
        generation !== themeGeneration ||
        currentThemeKey() !== themeKey ||
        currentPath !== preview.path
      ) return;
      requestedThemeKey = '';
      loadedThemeKey = themeKey;
      view.dispatch({ effects: themeExtension.reconfigure(extension) });
    } catch {
      if (generation === themeGeneration && requestedThemeKey === themeKey) requestedThemeKey = '';
    }
  }

  function restoredState(path: string): StoredViewState | null {
    const candidate = restoredViewStates[path] ?? sessionViewStates.get(path);
    if (!candidate || typeof candidate !== 'object') return null;
    const state = candidate as Partial<StoredViewState>;
    if (
      typeof state.anchor !== 'number' ||
      typeof state.head !== 'number' ||
      typeof state.scrollTop !== 'number'
    ) return null;
    return state as StoredViewState;
  }

  function rememberCurrentView(): void {
    if (!view || !currentPath) return;
    rememberEditorState(currentPath, view.state, true);
    const selection = view.state.selection.main;
    sessionViewStates.delete(currentPath);
    sessionViewStates.set(currentPath, {
      anchor: selection.anchor,
      head: selection.head,
      scrollTop: view.scrollDOM.scrollTop
    });
  }

  function linePosition(state: EditorState, lineNumber: number, column = 1): number {
    const line = state.doc.line(Math.max(1, Math.min(lineNumber, state.doc.lines)));
    return Math.min(line.to, line.from + Math.max(0, column - 1));
  }

  function lookupRequestAt(state: EditorState, position = state.selection.main.head): SourceLookupRequest | null {
    const line = state.doc.lineAt(position);
    const offset = position - line.from;
    const text = line.text;
    let start = offset;
    let end = offset;
    while (start > 0 && /[\w$]/.test(text[start - 1])) start -= 1;
    while (end < text.length && /[\w$]/.test(text[end])) end += 1;
    const symbolName = text.slice(start, end);
    if (!symbolName) return null;
    return {
      symbolName,
      line: line.number,
      column: start + 1,
      filePath: preview.path,
      languageServerEnabled: officialLspExpected()
    };
  }

  async function navigate(kind: 'definition' | 'references', position?: number): Promise<void> {
    if (!view) return;
    const request = lookupRequestAt(view.state, position);
    if (!request) return;
    const targets = kind === 'definition'
      ? await onDefinitionLookup?.(request)
      : await onReferenceLookup?.(request);
    const target = targets?.[0];
    if (!target) return;
    if (target.path === preview.path) {
      const at = linePosition(view.state, target.line, target.column);
      view.dispatch({
        selection: { anchor: at },
        effects: EditorView.scrollIntoView(at, { y: 'center' })
      });
      view.focus();
      return;
    }
    await onExternalNavigation?.({ path: target.path, line: target.line, column: target.column });
  }

  function runEditorCommand(command: (target: EditorView) => boolean): void {
    if (!view || !editable) return;
    command(view);
    view.focus();
  }

  function peekReferences(capturedRequest?: SourceLookupRequest | null): void {
    if (!view || !onReferenceLookup) return;
    const editor = view;
    const request = capturedRequest ?? lookupRequestAt(editor.state);
    if (!request) return;
    if (view !== editor || currentPath !== preview.path) return;
    editor.dispatch({ effects: showCodeMirrorReferences.of(request) });
    editor.focus();
  }

  async function writeClipboard(text: string): Promise<void> {
    if (!text) return;
    const { writeText } = await import('@tauri-apps/plugin-clipboard-manager');
    await writeText(text);
  }

  async function copySelection(): Promise<void> {
    if (!view) return;
    const { from, to } = view.state.selection.main;
    await writeClipboard(view.state.sliceDoc(from, to));
    view?.focus();
  }

  async function cutSelection(): Promise<void> {
    if (!view || !editable) return;
    const editor = view;
    const selection = editor.state.selection.main;
    if (selection.empty) return;
    const text = editor.state.sliceDoc(selection.from, selection.to);
    await writeClipboard(text);
    if (view !== editor || !editor.state.selection.main.eq(selection)) return;
    editor.dispatch({ changes: { from: selection.from, to: selection.to } });
    editor.focus();
  }

  async function pasteClipboard(): Promise<void> {
    if (!view || !editable) return;
    const { readText } = await import('@tauri-apps/plugin-clipboard-manager');
    const text = await readText();
    if (!view || !editable || !text) return;
    view.dispatch(view.state.replaceSelection(text));
    view.focus();
  }

  function selectAll(): void {
    if (!view) return;
    view.dispatch({ selection: { anchor: 0, head: view.state.doc.length } });
    view.focus();
  }

  async function completionSource(context: CompletionContext) {
    if (!onCompletionLookup) return null;
    const word = context.matchBefore(/[\w$]*/);
    if (!word || (word.from === word.to && !context.explicit)) return null;
    const request = lookupRequestAt(context.state, context.pos) ?? {
      symbolName: word.text,
      line: context.state.doc.lineAt(context.pos).number,
      column: context.pos - context.state.doc.lineAt(context.pos).from + 1,
      filePath: preview.path,
      languageServerEnabled: officialLspExpected()
    };
    const items = await onCompletionLookup(request);
    if (!items?.length) return null;
    return {
      from: word.from,
      options: items.map((item) => ({
        label: item.label,
        detail: item.detail || undefined,
        apply: item.insertText || item.label,
        type: item.kind.toLowerCase()
      })),
      validFor: /^[\w$]*$/
    };
  }

  const hover = hoverTooltip(async (editor, position) => {
    if (!onHoverLookup) return null;
    const request = lookupRequestAt(editor.state, position);
    if (!request) return null;
    const answer = await onHoverLookup(request);
    if (!answer?.contents.length) return null;
    const line = editor.state.doc.lineAt(position);
    const start = line.from + Math.max(0, request.column - 1);
    return {
      pos: start,
      end: Math.min(line.to, start + request.symbolName.length),
      above: true,
      create: () => {
        const dom = document.createElement('div');
        dom.className = 'cm-source-hover';
        dom.textContent = answer.contents.join('\n');
        return { dom };
      }
    };
  }, { hoverTime: 350, hideOnChange: true });

  const hoverRange = EditorView.decorations.from(hover.active, (tooltips) => {
    const mark = Decoration.mark({ class: 'cm-hover-range' });
    return RangeSet.of(
      tooltips
        .filter((tooltip): tooltip is Tooltip & { end: number } => typeof tooltip.end === 'number')
        .map((tooltip) => mark.range(tooltip.pos, tooltip.end)),
      true
    );
  });

  const callbackIntelligence: Extension = [
    autocompletion({ defaultKeymap: false, override: [completionSource], activateOnTypingDelay: 180 }),
    hover,
    hoverRange
  ];

  function codeActionDiagnostics(state: EditorState): SourceCodeActionDiagnostic[] {
    const selection = state.selection.main;
    const diagnostics: SourceCodeActionDiagnostic[] = [];
    forEachDiagnostic(state, (diagnostic, from, to) => {
      const intersects = selection.empty
        ? from <= selection.head && selection.head <= to
        : from <= selection.to && to >= selection.from;
      if (!intersects) return;
      const start = state.doc.lineAt(from);
      const end = state.doc.lineAt(to);
      diagnostics.push({
        severity: diagnostic.severity,
        message: diagnostic.message,
        startLine: start.number,
        startColumn: from - start.from + 1,
        endLine: end.number,
        endColumn: to - end.from + 1,
        source: diagnostic.source
      });
    });
    return diagnostics;
  }

  async function showCodeActions(editor: EditorView): Promise<void> {
    if (!onCodeActionLookup || !officialLspExpected() || !languageServerRoot) return;
    clearCodeActions();
    const generation = codeActionGeneration;
    const path = currentPath;
    const root = languageServerRoot;
    const selection = editor.state.selection.main;
    const start = editor.state.doc.lineAt(selection.from);
    const end = editor.state.doc.lineAt(selection.to);
    const actions = await onCodeActionLookup({
      startLine: start.number,
      startColumn: selection.from - start.from + 1,
      endLine: end.number,
      endColumn: selection.to - end.from + 1,
      diagnostics: codeActionDiagnostics(editor.state)
    });
    if (
      !view || view !== editor || !actions?.length ||
      generation !== codeActionGeneration || !officialLspExpected() ||
      currentPath !== path || languageServerRoot !== root
    ) return;
    const applicable = actions.filter((action) =>
      !action.disabledReason &&
      (action.kind.startsWith('quickfix') || action.kind.startsWith('refactor'))
    );
    if (!applicable.length) return;
    editor.dispatch({
      effects: setCodeActionMenu.of({ path, root, pos: selection.head, actions: applicable })
    });
  }

  function applyCodeAction(editor: EditorView, action: SourceCodeAction): void {
    const path = currentPath;
    const edits = action.files.find((file) => file.path === path)?.edits ?? [];
    const changes = edits
      .map((edit) => ({
        from: linePosition(editor.state, edit.startLine, edit.startColumn),
        to: linePosition(editor.state, edit.endLine, edit.endColumn),
        insert: edit.newText
      }))
      .sort((left, right) => left.from - right.from || left.to - right.to);
    clearCodeActions();
    if (changes.length) editor.dispatch({ changes });
    if (action.files.some((file) => file.path !== path && file.edits.length)) {
      void onWorkspaceEditAction?.(action);
    }
  }

  /**
   * On a file change, reconfiguring this compartment destroys the old document
   * plugin (which sends didClose) but keeps the one root client/socket. Nothing
   * here retains a document extension or its results after that reconfigure.
   */
  function clearLspSupport(disposeClient = true): void {
    clearCodeActions();
    lspGeneration += 1;
    requestedLspKey = '';
    loadedLspKey = '';
    if (disposeClient) {
      lspSession?.dispose();
      lspSession = null;
    }
    if (view) view.dispatch({ effects: intelligence.reconfigure(callbackIntelligence) });
  }

  function officialLspExpected(): boolean {
    const languageId = preview.language.toLowerCase();
    return Boolean(
      view && visible && languageServerRoot &&
      (languageId === 'csharp' || languageId === 'c#') &&
      currentPath && currentPath === preview.path
    );
  }

  function officialLspReady(): boolean {
    return Boolean(
      officialLspExpected() && languageServerRoot &&
      loadedLspKey === `${languageServerRoot}\0${currentPath}`
    );
  }

  async function loadVisibleLspSupport(): Promise<void> {
    const root = languageServerRoot;
    if (!officialLspExpected() || !view || !root) return;
    const key = `${root}\0${currentPath}`;
    if (key === loadedLspKey || key === requestedLspKey) return;

    clearLspSupport(false);
    requestedLspKey = key;
    const generation = ++lspGeneration;
    setNativeCsharpActiveRoot(root);
    const session = connectCodeMirrorCsharpClient(root, onLanguageServerReady);
    lspSession = session;
    view.dispatch({ effects: intelligence.reconfigure([]) });
    view.dispatch(setDiagnostics(view.state, []));
    try {
      const extension = await session.extension(currentPath);
      if (
        !view ||
        !visible ||
        generation !== lspGeneration ||
        requestedLspKey !== key ||
        currentPath !== preview.path ||
        languageServerRoot !== root
      ) {
        return;
      }
      requestedLspKey = '';
      loadedLspKey = key;
      view.dispatch({ effects: intelligence.reconfigure(extension) });
    } catch {
      if (generation !== lspGeneration || requestedLspKey !== key) return;
      requestedLspKey = '';
      if (!view) return;
      view.dispatch({ effects: intelligence.reconfigure(callbackIntelligence) });
      view.dispatch(setDiagnostics(view.state, diagnosticsFor(view.state)));
    }
  }

  function diagnosticsFor(state: EditorState): Diagnostic[] {
    return externalDiagnostics.map((diagnostic) => {
      const from = linePosition(state, diagnostic.line, diagnostic.column);
      return {
        from,
        to: Math.min(state.doc.length, from + 1),
        severity: diagnostic.severity === 'hint' ? 'info' : diagnostic.severity,
        message: diagnostic.message,
        source: diagnostic.source
      };
    });
  }

  const editorExtensions: Extension[] = [
    keymap.of([
      { key: 'Mod-s', run: () => { onSaveRequest?.(); return true; } },
      {
        key: 'Mod-.',
        run: (editor) => {
          if (!officialLspExpected() || !onCodeActionLookup) return false;
          void showCodeActions(editor);
          return true;
        }
      },
      {
        key: 'Escape',
        run: (editor) => {
          if (editor.state.field(codeActionMenuState, false)) {
            clearCodeActions();
            return true;
          }
          if (editor.state.selection.ranges.length < 2) return false;
          editor.dispatch({ selection: editor.state.selection.asSingle() });
          return true;
        }
      },
      {
        key: 'F12',
        run: () => {
          if (officialLspExpected()) return false;
          void navigate('definition');
          return true;
        }
      },
      {
        key: 'Shift-F12',
        run: () => {
          if (officialLspExpected()) return false;
          peekReferences();
          return true;
        }
      }
    ]),
    lineNumbers(),
    highlightActiveLineGutter(),
    highlightSpecialChars(),
    history({ minDepth: savedHistoryDepthLimit }),
    foldGutter(),
    codeFolding({ placeholderText: '⋯' }),
    drawSelection(),
    dropCursor(),
    EditorState.allowMultipleSelections.of(true),
    indentOnInput(),
    syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
    bracketMatching(),
    closeBrackets(),
    rectangularSelection(),
    crosshairCursor(),
    highlightActiveLine(),
    highlightSelectionMatches(),
    keymap.of(vscodeKeymap),
    themeExtension.of(currentEditorTheme()),
    lintGutter(),
    intelligence.of(callbackIntelligence),
    language.of([]),
    editing.of(editingExtensions()),
    codeActionMenuState,
    codeLens.of([]),
    codeMirrorSemanticTokens,
    EditorView.domEventHandlers({
      contextmenu: (event, editor) => {
        const position = editor.posAtCoords({ x: event.clientX, y: event.clientY });
        const clickedSelection = position !== null && editor.state.selection.ranges.some(
          (range) => !range.empty && position >= range.from && position <= range.to
        );
        if (position !== null && !clickedSelection) {
          editor.dispatch({ selection: { anchor: position } });
        }
        const hasSelection = editor.state.selection.ranges.some((range) => !range.empty);
        editorMenuReferenceRequest = lookupRequestAt(editor.state);
        const hasSymbol = Boolean(editorMenuReferenceRequest);
        const lspReady = officialLspReady();
        editorMenuStatus = {
          canUndo: editable && undoDepth(editor.state) > 0,
          canRedo: editable && redoDepth(editor.state) > 0,
          canCut: editable && hasSelection,
          canCopy: hasSelection,
          canPaste: editable,
          canSelectAll: editor.state.doc.length > 0,
          canChangeOccurrences: editable && hasSelection && editor.state.selection.ranges.length === 1,
          canFormat: editable && lspReady,
          canRename: editable && hasSymbol && lspReady,
          canDefinition: hasSymbol && Boolean(onDefinitionLookup),
          canReferences: hasSymbol && Boolean(onReferenceLookup)
        };
        return false;
      },
      mousedown: (event, editor) => {
        if (!(event.metaKey || event.ctrlKey) || event.button !== 0) return false;
        const position = editor.posAtCoords({ x: event.clientX, y: event.clientY });
        if (position === null) return false;
        event.preventDefault();
        void navigate('definition', position);
        return true;
      }
    }),
    EditorView.updateListener.of((update) => {
      if (update.docChanged || update.selectionSet) {
        codeActionGeneration += 1;
      }
      if (!update.docChanged || applyingContent) return;
      const next = update.state.doc.toString();
      semanticTokenGeneration += 1;
      semanticTokenStopController?.abort();
      semanticTokenStopController = null;
      replaceSemanticTokens(extractSourceSemanticTokens({ ...preview, content: next }, next));
      onContentChange?.(next);
      onSymbolsChange?.(extractSourceSymbols(preview, next));
      onReferenceCountsOutOfDate?.(preview.path);
    })
  ];

  function showFile(): void {
    if (!view) return;
    clearLspSupport(false);
    rememberCurrentView();
    currentPath = preview.path;
    const restored = restoredState(currentPath);
    const doc = desiredContent();
    const max = doc.length;
    const retained = sessionEditorStates.get(currentPath);
    sessionEditorStates.delete(currentPath);
    publishRetainedEditorDiagnostics();
    let nextState = retained;
    if (!nextState) {
      nextState = EditorState.create({
        doc,
        selection: restored
          ? { anchor: Math.min(max, restored.anchor), head: Math.min(max, restored.head) }
          : undefined,
        extensions: editorExtensions
      });
    } else if (nextState.doc.toString() !== doc) {
      const selection = restored ?? nextState.selection.main;
      nextState = EditorState.create({
        doc,
        selection: {
          anchor: Math.max(0, Math.min(max, selection.anchor)),
          head: Math.max(0, Math.min(max, selection.head))
        },
        extensions: editorExtensions
      });
    }
    applyingContent = true;
    view.setState(nextState);
    applyingContent = false;
    shownRevision = preview.revision;
    setCodeMirrorDocBytes(textBytes(doc));
    onSymbolsChange?.(extractSourceSymbols(preview, doc));
    view.dispatch(setDiagnostics(view.state, diagnosticsFor(view.state)));
    clearLanguageSupport();
    clearThemeSupport();
    void loadVisibleLanguageSupport();
    void loadVisibleThemeSupport();
    void loadVisibleLspSupport();
    void refreshSemanticTokens();
    configureCodeLens();
    if (restored && view && currentPath === preview.path) {
      view.scrollDOM.scrollTop = restored.scrollTop;
      if (restoredViewStates[currentPath]) onRestoredViewStateConsumed?.(currentPath);
    }
  }

  export function captureViewStates(paths: readonly string[]): Record<string, object> {
    rememberCurrentView();
    const allowed = new Set(paths);
    return Object.fromEntries([...sessionViewStates].filter(([path]) => allowed.has(path)));
  }

  export function disposeTabModel(path: string): boolean {
    const wasCurrent = currentPath === path;
    if (wasCurrent) {
      clearLspSupport(false);
      clearCodeLens();
      clearSemanticTokens();
      currentPath = '';
    }
    sessionEditorStates.delete(path);
    publishRetainedEditorDiagnostics();
    if (wasCurrent) {
      setCodeMirrorDocBytes(0);
    }
    return sessionViewStates.delete(path);
  }

  export function disposeAllTabModels(): void {
    clearLspSupport();
    clearCodeLens();
    clearSemanticTokens();
    currentPath = '';
    sessionViewStates.clear();
    sessionEditorStates.clear();
    publishRetainedEditorDiagnostics();
    setCodeMirrorDocBytes(0);
    setCodeMirrorUndoDepth(0);
  }

  export function refreshReferenceCounts(): void {
    configureCodeLens();
  }

  export function releaseSessionResources(): void {
    clearLspSupport();
    clearLanguageSupport();
    clearCodeLens();
    clearSemanticTokens();
    currentPath = '';
    sessionViewStates.clear();
    sessionEditorStates.clear();
    publishRetainedEditorDiagnostics();
    applyingContent = true;
    view?.setState(EditorState.create());
    applyingContent = false;
    setCodeMirrorDocBytes(0);
  }

  $effect(() => {
    const path = preview.path;
    if (view && path !== currentPath) showFile();
  });

  $effect(() => {
    const next = desiredContent();
    const revision = preview.revision;
    if (!view || preview.path !== currentPath) return;
    // A change made elsewhere to the same disk revision (a Preview checkbox
    // click) is an edit: it joins undo. Loading or rereading the file is not.
    const isEdit = revision === shownRevision;
    shownRevision = revision;
    const current = view.state.doc.toString();
    if (current === next) return;
    // Replace only the part that differs, so earlier undo steps still apply.
    let from = 0;
    const shorter = Math.min(current.length, next.length);
    while (from < shorter && current.charCodeAt(from) === next.charCodeAt(from)) from += 1;
    let same = 0;
    while (
      same < shorter - from
      && current.charCodeAt(current.length - 1 - same) === next.charCodeAt(next.length - 1 - same)
    ) same += 1;
    applyingContent = true;
    view.dispatch({
      changes: { from, to: current.length - same, insert: next.slice(from, next.length - same) },
      annotations: Transaction.addToHistory.of(isEdit)
    });
    applyingContent = false;
    setCodeMirrorDocBytes(textBytes(next));
  });

  $effect(() => {
    editable;
    if (view) view.dispatch({ effects: editing.reconfigure(editingExtensions()) });
  });

  $effect(() => {
    languageServerRoot;
    onSemanticTokensLookup;
    preview.path;
    preview.language;
    if (visible) {
      void loadVisibleLanguageSupport();
      if (officialLspExpected()) void loadVisibleLspSupport();
      else clearLspSupport();
      void refreshSemanticTokens();
    } else {
      clearLanguageSupport();
      clearLspSupport();
      clearSemanticTokens();
    }
    if (view && currentPath === preview.path) configureCodeLens();
  });

  $effect(() => {
    settings.appearance.themeId;
    settings.editor.fontFamily;
    settings.editor.fontSize;
    settings.editor.lineHeight;
    settings.editor.fontLigatures;
    if (visible) void loadVisibleThemeSupport();
    else clearThemeSupport();
  });

  $effect(() => {
    externalDiagnostics;
    if (view && !officialLspExpected()) {
      view.dispatch(setDiagnostics(view.state, diagnosticsFor(view.state)));
    }
  });

  function revealRequestedLine(): void {
    if (!view || loading || !targetLine || targetLine < 1) return;
    const position = linePosition(view.state, targetLine);
    view.dispatch({
      selection: { anchor: position },
      effects: EditorView.scrollIntoView(position, { y: 'center' })
    });
  }

  $effect(() => {
    targetLineRequestId;
    loading;
    revealRequestedLine();
  });

  onMount(() => {
    view = new EditorView({ parent: host, state: EditorState.create({ extensions: editorExtensions }) });
    addCodeMirrorEditorView(1);
    showFile();
    revealRequestedLine();
  });

  onDestroy(() => {
    clearLspSupport();
    clearCodeLens();
    clearSemanticTokens();
    languageGeneration += 1;
    themeGeneration += 1;
    view?.destroy();
    view = null;
    addCodeMirrorEditorView(-1);
    sessionEditorStates.clear();
    publishRetainedEditorDiagnostics();
    setCodeMirrorDocBytes(0);
  });
</script>

<ContextMenu.Root>
  <ContextMenu.Trigger>
    {#snippet child({ props })}
      <div {...props} class="codemirror-host" bind:this={host} aria-label={`Editor for ${preview.fileName}`}></div>
    {/snippet}
  </ContextMenu.Trigger>
  <ContextMenu.Content side="left" class="w-[220px]" aria-label="Editor actions">
    <ContextMenu.Item disabled={!editorMenuStatus.canUndo} onSelect={() => runEditorCommand(undo)}>Undo</ContextMenu.Item>
    <ContextMenu.Item disabled={!editorMenuStatus.canRedo} onSelect={() => runEditorCommand(redo)}>Redo</ContextMenu.Item>
    <ContextMenu.Separator />
    <ContextMenu.Item disabled={!editorMenuStatus.canCut} onSelect={() => void cutSelection()}>Cut</ContextMenu.Item>
    <ContextMenu.Item disabled={!editorMenuStatus.canCopy} onSelect={() => void copySelection()}>Copy</ContextMenu.Item>
    <ContextMenu.Item disabled={!editorMenuStatus.canPaste} onSelect={() => void pasteClipboard()}>Paste</ContextMenu.Item>
    <ContextMenu.Item disabled={!editorMenuStatus.canSelectAll} onSelect={selectAll}>Select All</ContextMenu.Item>
    <ContextMenu.Item disabled={!editorMenuStatus.canChangeOccurrences} onSelect={() => runEditorCommand(selectSelectionMatches)}>Change All Occurrences</ContextMenu.Item>
    <ContextMenu.Separator />
    <ContextMenu.Item disabled={!editorMenuStatus.canFormat} onSelect={() => runEditorCommand(formatDocument)}>Format Document</ContextMenu.Item>
    <ContextMenu.Item disabled={!editorMenuStatus.canRename} onSelect={() => runEditorCommand(renameSymbol)}>Rename Symbol</ContextMenu.Item>
    <ContextMenu.Separator />
    <ContextMenu.Item disabled={!editorMenuStatus.canDefinition} onSelect={() => void navigate('definition')}>Go to Definition</ContextMenu.Item>
    <ContextMenu.Item disabled={!editorMenuStatus.canReferences} onSelect={() => peekReferences(editorMenuReferenceRequest)}>Peek References</ContextMenu.Item>
  </ContextMenu.Content>
</ContextMenu.Root>

<style>
  .codemirror-host {
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
    background: var(--color-surface, #17191e);
  }

  .codemirror-host :global(.cm-scroller) {
    scrollbar-width: thin;
    scrollbar-color: rgb(238 240 249 / 30%) transparent;
    scrollbar-gutter: stable;
  }

  .codemirror-host :global(.cm-scroller::-webkit-scrollbar) {
    width: 8px !important;
    height: 8px !important;
  }

  .codemirror-host :global(.cm-scroller::-webkit-scrollbar-thumb) {
    background-color: rgb(238 240 249 / 30%) !important;
    background-clip: padding-box;
    border: 2px solid transparent;
    border-radius: var(--radius-pill);
  }

  .codemirror-host :global(.cm-scroller::-webkit-scrollbar-thumb:hover) {
    background-color: rgb(238 240 249 / 48%) !important;
  }

  :global(.cm-source-hover) {
    max-width: min(680px, 70vw);
    padding: 7px 9px;
    white-space: pre-wrap;
    font-family: var(--font-mono);
    font-size: 12px;
    line-height: 1.45;
  }

  :global(.cm-hover-range) {
    background: color-mix(in srgb, var(--color-accent) 18%, transparent);
  }

  .codemirror-host :global(.cm-matchingBracket) {
    color: #eaffc7 !important;
    background: rgb(184 255 90 / 22%) !important;
    outline: 1px solid #b8ff5a;
    border-radius: 2px;
    text-shadow: 0 0 7px rgb(184 255 90 / 85%);
  }

  .codemirror-host :global(.cm-nonmatchingBracket) {
    color: #fff0f3 !important;
    background: rgb(255 70 104 / 28%) !important;
    outline: 1px solid #ff4668;
    border-radius: 2px;
    text-shadow: 0 0 7px rgb(255 70 104 / 80%);
  }

  :global(.cm-code-actions) {
    display: grid;
    min-width: 240px;
    padding: 4px;
    border: 1px solid var(--color-border);
    border-radius: 6px;
    background: var(--color-elevated);
    box-shadow: var(--shadow-lg);
  }

  :global(.cm-code-actions button) {
    padding: 6px 8px;
    border: 0;
    border-radius: 4px;
    color: var(--color-text);
    background: transparent;
    text-align: left;
    font: inherit;
    cursor: pointer;
  }

  :global(.cm-code-actions button:hover),
  :global(.cm-code-actions button:focus-visible) {
    outline: none;
    background: var(--color-hover);
  }
</style>
