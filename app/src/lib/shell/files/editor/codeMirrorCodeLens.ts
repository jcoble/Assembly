import { EditorState, RangeSet, RangeSetBuilder, StateEffect, StateField, type Extension } from '@codemirror/state';
import { Decoration, EditorView, GutterMarker, ViewPlugin, WidgetType, gutterLineClass, keymap, lineNumbers, type DecorationSet, type ViewUpdate } from '@codemirror/view';

import { formatSourceCodeLensTitle, sourceCodeLensCountKey } from '$lib/sourceCodeLensKeys';
import { dotnetWorkspaceLensTitles, type DotnetWorkspaceAction } from '$lib/workspaceCodeLens';
import type { SourceLookupRequest } from './sourceIntelligence';
import type { SourcePreview, SourceReferenceTarget, SourceSymbol } from '$lib/sourceData';

type Count = { count: number; atLeast: boolean };

export const showCodeMirrorReferences = StateEffect.define<SourceLookupRequest>();
const setCodeMirrorReferenceTargets = StateEffect.define<{
  request: SourceLookupRequest;
  targets: SourceReferenceTarget[];
}>();
const selectCodeMirrorReferenceTarget = StateEffect.define<SourceReferenceTarget>();
const setCodeMirrorReferencePreview = StateEffect.define<{
  target: SourceReferenceTarget;
  source: PeekSource | null;
}>();
const closeCodeMirrorReferences = StateEffect.define<void>();
const setCodeMirrorLensRows = StateEffect.define<LensRow[]>();

export interface CodeMirrorCodeLensOptions {
  preview: SourcePreview;
  enabled: boolean;
  onAnchorLookup?: (preview: SourcePreview) => Promise<SourceSymbol[] | null>;
  onCount?: (request: SourceLookupRequest) => Count | null | Promise<Count | null>;
  onReferences?: (request: SourceLookupRequest) => Promise<SourceReferenceTarget[]>;
  onPreview?: (target: SourceReferenceTarget) => Promise<{
    preview: SourcePreview;
    extensions: Extension[];
  } | null>;
  onOpenReference?: (target: SourceReferenceTarget) => void | Promise<void>;
  onDotnetAction?: (action: DotnetWorkspaceAction) => void | Promise<void>;
}

type LensRow = {
  key: string;
  request?: SourceLookupRequest;
  action?: DotnetWorkspaceAction;
  line: number;
  title: string;
  state: 'loading' | 'ready' | 'unavailable';
};

const CODE_LENS_KINDS = new Set(['class', 'interface', 'method', 'function', 'constructor', 'property', 'enum', 'struct', 'record', 'variable']);

function isCodeLensSymbol(symbol: SourceSymbol): boolean {
  if (!CODE_LENS_KINDS.has(symbol.kind)) return false;
  return symbol.kind !== 'variable' || /^(?:public|private|protected|internal|static|readonly|const|required|volatile|new)\b/.test(symbol.detail);
}

function requestFor(symbol: SourceSymbol, filePath: string): SourceLookupRequest {
  const nameOffset = symbol.detail.lastIndexOf(symbol.name);
  return {
    symbolName: symbol.name,
    line: symbol.line,
    column: nameOffset >= 0 ? symbol.column + nameOffset : symbol.column,
    filePath
  };
}

class LensLineWidget extends WidgetType {
  constructor(
    readonly rows: LensRow[],
    readonly run: (row: LensRow) => void
  ) { super(); }

  eq(other: LensLineWidget): boolean {
    return other.rows.length === this.rows.length && other.rows.every((row, index) => {
      const current = this.rows[index];
      return row.key === current.key && row.title === current.title && row.state === current.state;
    });
  }

  toDOM(): HTMLElement {
    const line = document.createElement('div');
    line.className = 'cm-code-lens-line';
    for (const row of this.rows) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'cm-code-lens';
      button.classList.toggle('loading', row.state === 'loading');
      button.textContent = row.title;
      button.disabled = row.state === 'unavailable';
      button.onclick = () => this.run(row);
      line.append(button);
    }
    return line;
  }

  ignoreEvent(): boolean { return true; }
}

class LensGutterMarker extends GutterMarker {
  elementClass = 'cm-code-lens-gutter';
}

const lensGutterMarker = new LensGutterMarker();

type PeekSource = {
  target: SourceReferenceTarget;
  preview: SourcePreview;
  extensions: Extension[];
};

class PeekWidget extends WidgetType {
  private previewView: EditorView | null = null;

  constructor(
    readonly request: SourceLookupRequest,
    readonly targets: SourceReferenceTarget[] | null,
    readonly selected: SourceReferenceTarget | null,
    readonly source: PeekSource | null | undefined,
    readonly viewportWidth: number,
    readonly select: (target: SourceReferenceTarget) => void,
    readonly open: (target: SourceReferenceTarget) => void,
    readonly close: () => void
  ) { super(); }

  eq(other: PeekWidget): boolean {
    return other.request === this.request
      && other.targets === this.targets
      && other.selected === this.selected
      && other.source === this.source
      && other.viewportWidth === this.viewportWidth;
  }

  toDOM(): HTMLElement {
    const panel = document.createElement('div');
    panel.className = 'cm-code-lens-peek';
    panel.style.width = `${this.viewportWidth}px`;
    panel.style.maxWidth = `${this.viewportWidth}px`;
    const header = document.createElement('div');
    header.className = 'cm-code-lens-peek-header';
    const heading = document.createElement('strong');
    const count = this.targets?.length ?? 0;
    heading.textContent = this.targets === null
      ? `Finding ${this.request.symbolName} references…`
      : `${this.request.symbolName} — Locations (${count})`;
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'cm-code-lens-peek-close';
    close.title = 'Close Peek';
    close.setAttribute('aria-label', 'Close Peek');
    close.textContent = '×';
    close.onclick = this.close;
    header.append(heading, close);
    panel.append(header);

    if (this.targets === null) {
      const loading = document.createElement('div');
      loading.className = 'cm-code-lens-peek-message';
      loading.textContent = 'Asking the language server…';
      panel.append(loading);
      return panel;
    }

    if (this.targets.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'cm-code-lens-peek-message';
      empty.textContent = 'No references found.';
      panel.append(empty);
      return panel;
    }

    const body = document.createElement('div');
    body.className = 'cm-code-lens-peek-body';
    const source = document.createElement('div');
    source.className = 'cm-code-lens-peek-source';
    if (this.source && this.selected && this.source.target === this.selected) {
      const allLines = this.source.preview.content.split(/\r\n|\r|\n/);
      const startLine = Math.max(1, this.selected.line - 8);
      const endLine = Math.min(allLines.length, this.selected.line + 12);
      const documentText = allLines.slice(startLine - 1, endLine).join('\n');
      const selectedLine = this.selected.line - startLine + 1;
      const previewState = EditorState.create({
        doc: documentText,
        extensions: [
          EditorState.readOnly.of(true),
          EditorView.editable.of(false),
          lineNumbers({ formatNumber: (line) => String(line + startLine - 1) }),
          ...this.source.extensions,
          EditorView.decorations.compute([], (state) => Decoration.set([
            Decoration.line({ class: 'cm-code-lens-peek-active-line' })
              .range(state.doc.line(selectedLine).from)
          ])),
          EditorView.theme({
            '&': { height: '100%' },
            '.cm-scroller': { overflow: 'auto' },
            '.cm-content': { padding: '6px 0 18px' },
            '.cm-line': { padding: '0 10px' }
          })
        ]
      });
      this.previewView = new EditorView({ state: previewState, parent: source });
      const at = this.previewView.state.doc.line(selectedLine).from;
      this.previewView.dispatch({ effects: EditorView.scrollIntoView(at, { y: 'center' }) });
    } else {
      source.textContent = this.source === null ? 'Source preview unavailable.' : 'Loading source preview…';
      source.classList.add('loading');
    }

    const locations = document.createElement('div');
    locations.className = 'cm-code-lens-peek-locations';
    const sourceLines = this.source?.preview.content.split(/\r\n|\r|\n/);
    const groups = new Map<string, SourceReferenceTarget[]>();
    for (const target of this.targets) {
      const key = target.path;
      const group = groups.get(key) ?? [];
      group.push(target);
      groups.set(key, group);
    }
    for (const targets of groups.values()) {
      const group = document.createElement('section');
      const groupHeading = document.createElement('div');
      groupHeading.className = 'cm-code-lens-peek-group';
      const label = document.createElement('strong');
      label.textContent = targets[0].fileName || targets[0].relativePath || targets[0].path;
      const badge = document.createElement('span');
      badge.textContent = String(targets.length);
      groupHeading.append(label, badge);
      group.append(groupHeading);
      for (const target of targets) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'cm-code-lens-peek-location';
        button.classList.toggle('selected', target === this.selected);
        button.title = 'Select reference; double-click to open the file';
        const excerpt = this.source?.preview.path === target.path
          ? sourceLines?.[target.line - 1]?.trim()
          : target.excerpt.trim();
        button.textContent = excerpt || `Line ${target.line}`;
        button.onclick = () => this.select(target);
        button.ondblclick = () => this.open(target);
        group.append(button);
      }
      locations.append(group);
    }
    body.append(source, locations);
    panel.append(body);
    return panel;
  }

  ignoreEvent(): boolean { return true; }

  destroy(): void {
    this.previewView?.destroy();
    this.previewView = null;
  }
}

const codeLensBaseTheme = EditorView.baseTheme({
  '.cm-code-lens-host': { position: 'relative', paddingTop: '18px' },
  '.cm-code-lens-gutter': { paddingTop: '18px' },
  '.cm-code-lens-line': { position: 'absolute', top: '0', left: '4px', display: 'flex', alignItems: 'center', height: '18px' },
  '.cm-code-lens': { border: '0', background: 'transparent', color: '#8aa9d6', cursor: 'pointer', padding: '0 8px 0 0', font: 'inherit', fontSize: '11px', lineHeight: '18px' },
  '.cm-code-lens.loading': { color: '#8b909b', cursor: 'progress' },
  '.cm-code-lens-peek': { boxSizing: 'border-box', width: '100%', height: '340px', borderBlock: '1px solid var(--color-live, #54b9ff)', background: 'var(--color-surface, #17191e)', color: 'var(--color-text, #eef0f9)', fontSize: '12px' },
  '.cm-code-lens-peek-header': { display: 'flex', alignItems: 'center', justifyContent: 'space-between', height: '36px', padding: '0 8px 0 12px', borderBottom: '1px solid var(--color-live, #54b9ff)', background: 'var(--color-elevated, #1c1c1c)' },
  '.cm-code-lens-peek-header strong': { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: '600' },
  '.cm-code-lens-peek-close': { width: '28px', height: '28px', border: '0', background: 'transparent', color: 'var(--color-text-2, #b3b7bf)', cursor: 'pointer', font: 'inherit', fontSize: '22px', lineHeight: '24px' },
  '.cm-code-lens-peek-body': { position: 'relative', width: '100%', maxWidth: '100%', height: 'calc(100% - 37px)', minHeight: '0', overflow: 'hidden' },
  '.cm-code-lens-peek-source': { position: 'absolute', inset: '0 45% 0 0', width: '55%', minWidth: '0', maxWidth: '55%', minHeight: '0', overflow: 'hidden', borderRight: '1px solid var(--color-border, #343944)' },
  '.cm-code-lens-peek-source .cm-editor, .cm-code-lens-peek-source .cm-scroller': { width: '100%', minWidth: '0', maxWidth: '100%' },
  '.cm-code-lens-peek-source.loading, .cm-code-lens-peek-message': { display: 'grid', placeItems: 'center', color: 'var(--color-text-3, #8b919b)' },
  '.cm-code-lens-peek-message': { height: 'calc(100% - 37px)' },
  '.cm-code-lens-peek-active-line': { background: 'color-mix(in srgb, var(--color-live, #54b9ff) 16%, transparent)' },
  '.cm-code-lens-peek-locations': { position: 'absolute', inset: '0 0 0 55%', width: '45%', minWidth: '0', maxWidth: '45%', minHeight: '0', overflow: 'auto', padding: '6px 0 14px', background: 'var(--color-elevated, #1c1c1c)' },
  '.cm-code-lens-peek-group': { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px', padding: '5px 12px 4px', color: 'var(--color-text, #eef0f9)' },
  '.cm-code-lens-peek-group span': { display: 'grid', placeItems: 'center', minWidth: '22px', height: '22px', padding: '0 5px', borderRadius: '999px', background: 'var(--color-hover, #232323)', color: 'var(--color-text-2, #b3b7bf)' },
  '.cm-code-lens-peek-location': { display: 'block', width: '100%', overflow: 'hidden', padding: '4px 14px 4px 28px', border: '0', background: 'transparent', color: 'var(--color-text-2, #b3b7bf)', cursor: 'pointer', fontFamily: 'inherit', fontSize: '12px', lineHeight: '18px', textAlign: 'left', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  '.cm-code-lens-peek-location:hover': { background: 'var(--color-hover, #232323)', color: 'var(--color-text, #eef0f9)' },
  '.cm-code-lens-peek-location.selected': { background: 'color-mix(in srgb, var(--color-live, #54b9ff) 18%, transparent)', color: 'var(--color-text, #eef0f9)', outline: '1px solid var(--color-live, #54b9ff)', outlineOffset: '-1px' },
  '@media (max-width: 760px)': {
    '.cm-code-lens-peek-source': { display: 'none' },
    '.cm-code-lens-peek-locations': { position: 'static', width: '100%', maxWidth: '100%', height: '100%' }
  }
});

export function codeMirrorCodeLens(options: CodeMirrorCodeLensOptions): Extension {
  const generation = Symbol(options.preview.path);
  let activeView: EditorView | null = null;
  let peekPreviewGeneration = 0;

  type PeekState = {
    request: SourceLookupRequest;
    targets: SourceReferenceTarget[] | null;
    selected: SourceReferenceTarget | null;
    source: PeekSource | null | undefined;
    decorations: DecorationSet;
  };

  function sameTarget(left: SourceReferenceTarget | null, right: SourceReferenceTarget): boolean {
    return Boolean(left && left.path === right.path && left.line === right.line && left.column === right.column);
  }

  type LensDecorations = {
    widgets: DecorationSet;
    gutters: RangeSet<GutterMarker>;
  };

  function lensDecorations(state: EditorState, rows: LensRow[]): LensDecorations {
    const widgetBuilder = new RangeSetBuilder<Decoration>();
    const gutterBuilder = new RangeSetBuilder<GutterMarker>();
    const rowsByLine = new Map<number, LensRow[]>();
    for (const row of rows) {
      const lineNumber = Math.max(1, Math.min(row.line, state.doc.lines));
      const lineRows = rowsByLine.get(lineNumber) ?? [];
      lineRows.push(row);
      rowsByLine.set(lineNumber, lineRows);
    }
    for (const [lineNumber, lineRows] of [...rowsByLine].sort(([left], [right]) => left - right)) {
      const line = state.doc.line(lineNumber);
      widgetBuilder.add(line.from, line.from, Decoration.line({ class: 'cm-code-lens-host' }));
      widgetBuilder.add(line.from, line.from, Decoration.widget({
        widget: new LensLineWidget(lineRows, runLensRow),
        side: -1
      }));
      gutterBuilder.add(line.from, line.from, lensGutterMarker);
    }
    return { widgets: widgetBuilder.finish(), gutters: gutterBuilder.finish() };
  }

  const initialRows: LensRow[] = [];
  if (options.preview.language.toLowerCase() === 'csharp' && options.onDotnetAction) {
    initialRows.push(
      { key: 'dotnet-build', action: 'build', line: 1, title: dotnetWorkspaceLensTitles.build, state: 'ready' },
      { key: 'dotnet-test', action: 'test', line: 1, title: dotnetWorkspaceLensTitles.test, state: 'ready' }
    );
  }
  if (options.enabled && options.onAnchorLookup && options.onCount) {
    initialRows.push({ key: 'reference-anchors', line: 1, title: 'Loading references…', state: 'loading' });
  }

  function runLensRow(row: LensRow): void {
    if (row.action) {
      void options.onDotnetAction?.(row.action);
      return;
    }
    if (!row.request || !options.onReferences || !activeView) return;
    activeView.dispatch({ effects: showCodeMirrorReferences.of(row.request) });
  }

  function peekDecorations(state: EditorState, value: Omit<PeekState, 'decorations'>): DecorationSet {
    const line = state.doc.line(Math.max(1, Math.min(value.request.line, state.doc.lines)));
    const viewportWidth = activeView
      ? Math.max(240, Math.floor(
          activeView.scrollDOM.getBoundingClientRect().right
          - activeView.contentDOM.getBoundingClientRect().left
        ))
      : 640;
    return Decoration.set([Decoration.widget({
      widget: new PeekWidget(
        value.request,
        value.targets,
        value.selected,
        value.source,
        viewportWidth,
        selectPeekTarget,
        (target) => void options.onOpenReference?.(target),
        closePeek
      ),
      side: 1,
      block: true
    }).range(line.to)]);
  }

  function selectPeekTarget(target: SourceReferenceTarget): void {
    if (!activeView) return;
    activeView.dispatch({ effects: selectCodeMirrorReferenceTarget.of(target) });
    void loadPeekPreview(target);
  }

  function closePeek(): void {
    peekPreviewGeneration += 1;
    activeView?.dispatch({ effects: closeCodeMirrorReferences.of() });
  }

  async function loadPeekPreview(target: SourceReferenceTarget): Promise<void> {
    const load = ++peekPreviewGeneration;
    let source: PeekSource | null = null;
    try {
      const loaded = await options.onPreview?.(target);
      source = loaded ? { target, ...loaded } : null;
    } catch {
      source = null;
    }
    const editor = activeView;
    if (!editor || load !== peekPreviewGeneration) return;
    const peek = editor.state.field(peekState, false);
    if (!peek || !sameTarget(peek.selected, target)) return;
    editor.dispatch({ effects: setCodeMirrorReferencePreview.of({ target, source }) });
  }

  const peekState = StateField.define<{
    request: SourceLookupRequest;
    targets: SourceReferenceTarget[] | null;
    selected: SourceReferenceTarget | null;
    source: PeekSource | null | undefined;
    decorations: DecorationSet;
  } | null>({
    create: () => null,
    update(value, transaction) {
      if (transaction.docChanged) {
        peekPreviewGeneration += 1;
        value = null;
      }
      for (const effect of transaction.effects) {
        if (effect.is(showCodeMirrorReferences)) {
          const request = effect.value;
          const next = {
            request,
            targets: null,
            selected: null,
            source: undefined
          };
          value = { ...next, decorations: peekDecorations(transaction.state, next) };
        } else if (effect.is(setCodeMirrorReferenceTargets) && value?.request === effect.value.request) {
          const { request, targets } = effect.value;
          const next = {
            request,
            targets,
            selected: targets[0] ?? null,
            source: undefined
          };
          value = { ...next, decorations: peekDecorations(transaction.state, next) };
        } else if (effect.is(selectCodeMirrorReferenceTarget) && value) {
          const next = { ...value, selected: effect.value, source: undefined };
          value = { ...next, decorations: peekDecorations(transaction.state, next) };
        } else if (
          effect.is(setCodeMirrorReferencePreview)
          && value
          && sameTarget(value.selected, effect.value.target)
        ) {
          const next = { ...value, source: effect.value.source };
          value = { ...next, decorations: peekDecorations(transaction.state, next) };
        } else if (effect.is(closeCodeMirrorReferences)) {
          value = null;
        }
      }
      return value;
    },
    provide: (field) => EditorView.decorations.from(field, (value) => value?.decorations ?? Decoration.none)
  });

  const lensState = StateField.define<LensDecorations>({
    create: (state) => lensDecorations(state, initialRows),
    update(value, transaction) {
      if (transaction.docChanged) value = { widgets: Decoration.none, gutters: RangeSet.empty };
      for (const effect of transaction.effects) {
        if (effect.is(setCodeMirrorLensRows)) value = lensDecorations(transaction.state, effect.value);
      }
      return value;
    },
    provide: (field) => [
      EditorView.decorations.from(field, (value) => value.widgets),
      gutterLineClass.from(field, (value) => value.gutters)
    ]
  });

  return [
    peekState,
    lensState,
    ViewPlugin.fromClass(class {
      private rows: LensRow[] = [...initialRows];
      private alive = true;
      private loadGeneration = 0;

      constructor(readonly view: EditorView) {
        activeView = view;
        if (options.enabled && options.onAnchorLookup && options.onCount) void this.loadReferences(generation);
      }

      update(update: ViewUpdate): void {
        for (const effect of update.transactions.flatMap((transaction) => transaction.effects)) {
          if (effect.is(showCodeMirrorReferences)) {
            const request = effect.value;
            if (this.alive) void this.openReferences(request);
          }
        }
        if (update.docChanged) {
          this.loadGeneration += 1;
          this.rows = this.rows.filter((row) => Boolean(row.action));
          if (options.enabled && options.onAnchorLookup && options.onCount) {
            this.rows.push({ key: 'reference-anchors', line: 1, title: 'Loading references…', state: 'loading' });
          }
          if (options.enabled && options.onAnchorLookup && options.onCount) void this.loadReferences(generation);
        }
      }

      destroy(): void {
        this.alive = false;
        this.loadGeneration += 1;
        peekPreviewGeneration += 1;
        if (activeView === this.view) activeView = null;
      }

      private async loadReferences(token: symbol): Promise<void> {
        const load = ++this.loadGeneration;
        let symbols: SourceSymbol[] | null | undefined;
        try {
          symbols = await options.onAnchorLookup?.({ ...options.preview, content: this.view.state.doc.toString() });
        } catch {
          symbols = null;
        }
        if (!this.alive || token !== generation || load !== this.loadGeneration) return;
        if (!symbols) {
          this.rows = this.rows.filter((row) => row.key !== 'reference-anchors');
          this.paint();
          return;
        }
        const requests = symbols.filter(isCodeLensSymbol).slice(0, 120)
          .map((symbol) => requestFor(symbol, options.preview.path));
        this.rows = [
          ...this.rows.filter((row) => Boolean(row.action)),
          ...requests.map((request) => ({
            key: sourceCodeLensCountKey(options.preview.path, request),
            request,
            line: request.line,
            title: 'Loading references…',
            state: 'loading' as const
          }))
        ];
        this.paint();
        await Promise.all(requests.map(async (request) => {
          const key = sourceCodeLensCountKey(options.preview.path, request);
          let count: Count | null | undefined;
          try {
            count = await options.onCount?.(request);
          } catch {
            count = null;
          }
          if (!this.alive || token !== generation || load !== this.loadGeneration) return;
          const index = this.rows.findIndex((candidate) => candidate.key === key);
          if (index < 0) return;
          this.rows[index] = {
            ...this.rows[index],
            state: count ? 'ready' : 'unavailable',
            title: count ? formatSourceCodeLensTitle(count.count, count.atLeast) : 'References unavailable'
          };
          this.paint();
        }));
      }

      private async openReferences(request: SourceLookupRequest): Promise<void> {
        let targets: SourceReferenceTarget[] = [];
        try {
          targets = (await options.onReferences?.(request)) ?? [];
        } catch {
          targets = [];
        }
        if (!this.alive) return;
        this.view.dispatch({ effects: setCodeMirrorReferenceTargets.of({ request, targets }) });
        if (targets[0]) void loadPeekPreview(targets[0]);
      }

      private paint(): void {
        if (!this.alive) return;
        this.view.dispatch({ effects: setCodeMirrorLensRows.of(this.rows) });
      }
    }),
    keymap.of([{
      key: 'Escape',
      run: (view) => {
        if (!view.state.field(peekState, false)) return false;
        closePeek();
        return true;
      }
    }]),
    codeLensBaseTheme
  ];
}
