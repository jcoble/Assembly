<script lang="ts">
  /**
   * ActivityFilesPanel.svelte — the "Files" activity-mode surface: the three
   * nested Dockview panes (file-content search, recent files, and the
   * virtualized project file tree with its scan-status header).
   *
   * Presentational only: it renders chrome + the three pane bodies and emits
   * every action through callbacks. It holds NO `$state` of its own. The page
   * owns the nested files-Dockview workspace, all scan/Tauri/navigation
   * handlers, the tree-virtualizer math, and every page `$derived` value; they
   * are passed in as props.
   *
   * It DOES import the shared `files` store directly — unlike EditorPanel /
   * ActivityGitPanel, which treat `files` as cross-domain. The files activity
   * mode is the `files` store's home surface (query, scan, selection, records,
   * expanded folders all belong to it), so `files.*` is read/bound directly
   * here rather than threaded through ~12 props.
   *
   * Teleport: the three pane `<div>`s keep `use:filesPanelAction={'search' |
   * 'recent' | 'files'}` and the shell keeps `hostAction`. Both actions stay in
   * the page (they wire the page-owned nested paneview workspace + its
   * element-registry sync) and are supplied as props — a dropped action = a
   * blank pane.
   */
  import type { Action } from 'svelte/action';
  import {
    Search,
    RefreshCw,
    FileCode2,
    History,
    FolderGit2,
    Copy,
    RotateCcw,
    FolderSearch,
    Plus,
    ChevronDown,
    ChevronRight,
    FolderOpen,
    Folder
  } from '@lucide/svelte';
  import SourceDockviewShell from '$lib/SourceDockviewShell.svelte';
  import { files } from '$lib/stores/filesStore.svelte';
  import {
    formatSourceContextRootLabel,
    type ProjectRoot,
    type SourceRecord,
    type SourceRecentRecord,
    type SourceSearchMatch,
    type SourceScanEvidence,
    type SourceScanRecovery,
    type SourceTreeNode,
    type SourceTreeRow,
    virtualizeSourceTreeRows
  } from '$lib/sourceData';
  import type { ProjectGitFileStatus } from '$lib/tauriSource';

  /** The three nested files-Dockview pane ids (page-local union, restated here). */
  type SourceFilesPaneID = 'files' | 'search' | 'recent';
  type VirtualizedTreeRows = ReturnType<typeof virtualizeSourceTreeRows>;

  interface Props {
    // ── teleport (page-owned, passed in) ──
    /** Dockview teleport action for each of the three pane bodies. */
    filesPanelAction: Action<HTMLElement, SourceFilesPaneID>;
    /** Dockview host action for the files paneview shell. */
    hostAction: Action<HTMLElement>;
    dockviewReady: boolean;
    dockviewError: string;

    // ── selected project (page $derived) ──
    selectedProject: ProjectRoot;

    // ── content-search pane (page $state + $derived) ──
    sourceSearchQuery: string;
    sourceSearchInput: HTMLInputElement | null;
    sourceSearchLoading: boolean;
    sourceSearchSummary: string;
    sourceSearchResults: SourceSearchMatch[];

    // ── recent pane (page $derived) ──
    projectRecentRecords: SourceRecentRecord[];

    // ── tree (page $derived; tree inputs stay in page) ──
    visibleTreeRows: SourceTreeRow[];
    virtualizedTreeRows: VirtualizedTreeRows;
    fileTreeElement: HTMLDivElement | null;

    // ── scan header (telemetry / recovery / runtime / git-root) — all page $derived ──
    sourceSidebarTreeStatusLabel: string;
    selectedProjectScanEvidence: SourceScanEvidence;
    sourceSidebarCompactStatusTitle: string;
    sourceSidebarIndexStatusLabel: string;
    sourceSidebarScanMetaLabel: string;
    sourceSidebarScanTelemetryExpanded: boolean;
    sourceScanStatsLabel: string;
    sourceSidebarScanTelemetryLabel: string;
    sourceRuntimeNoticeExpanded: boolean;
    sourceRuntimeNotice: string;
    selectedProjectGitRootSuggestion: string;
    sourceScanHealthNote: string;
    sourceScanRecovery: SourceScanRecovery;
    projectRootValidating: boolean;
    choosingProjectRoot: boolean;
    expandedSourceScanLimit: number;
    expandedSourceScanLimitShortLabel: string;

    // ── pure display helpers (stay page-side, passed in) ──
    gitStatusForSourceRecord: (record: SourceRecord) => ProjectGitFileStatus | null;
    isFolderExpanded: (node: SourceTreeNode) => boolean;

    // ── callbacks (all page-owned: Tauri / scan / selection / scroll) ──
    onGlobalSearchSubmit: (event: SubmitEvent) => void;
    onSelectSearchResult: (result: SourceSearchMatch) => void;
    onSelectRecentRecord: (record: SourceRecentRecord) => void;
    onCopyScanDiagnostic: () => void;
    onCopyTauriRunCommand: () => void;
    onUseValidatedGitRoot: () => void;
    onResetScanCache: (project: ProjectRoot) => void;
    onChooseProjectRoot: () => void;
    onScanMore: () => void;
    onFileTreeScroll: (event: Event) => void;
    onSelectTreeNode: (node: SourceTreeNode) => void;
    onTreeRowKeydown: (row: SourceTreeRow, event: KeyboardEvent) => void;
  }

  let {
    filesPanelAction,
    hostAction,
    dockviewReady,
    dockviewError,
    selectedProject,
    sourceSearchQuery = $bindable(),
    sourceSearchInput = $bindable(),
    sourceSearchLoading,
    sourceSearchSummary,
    sourceSearchResults,
    projectRecentRecords,
    visibleTreeRows,
    virtualizedTreeRows,
    fileTreeElement = $bindable(),
    sourceSidebarTreeStatusLabel,
    selectedProjectScanEvidence,
    sourceSidebarCompactStatusTitle,
    sourceSidebarIndexStatusLabel,
    sourceSidebarScanMetaLabel,
    sourceSidebarScanTelemetryExpanded,
    sourceScanStatsLabel,
    sourceSidebarScanTelemetryLabel,
    sourceRuntimeNoticeExpanded,
    sourceRuntimeNotice,
    selectedProjectGitRootSuggestion,
    sourceScanHealthNote,
    sourceScanRecovery,
    projectRootValidating,
    choosingProjectRoot,
    expandedSourceScanLimit,
    expandedSourceScanLimitShortLabel,
    gitStatusForSourceRecord,
    isFolderExpanded,
    onGlobalSearchSubmit,
    onSelectSearchResult,
    onSelectRecentRecord,
    onCopyScanDiagnostic,
    onCopyTauriRunCommand,
    onUseValidatedGitRoot,
    onResetScanCache,
    onChooseProjectRoot,
    onScanMore,
    onFileTreeScroll,
    onSelectTreeNode,
    onTreeRowKeydown
  }: Props = $props();
</script>

<div class="source-browser-stack">
  <SourceDockviewShell
    shellClass="source-dockview-files-shell"
    hostClass="source-dockview-files-host"
    errorClass="source-dockview-files-error"
    enabled={true}
    ready={dockviewReady}
    error={dockviewError}
    {hostAction}
  >
  <div
    id="source-files-search-pane"
    class="source-files-pane source-files-search-pane"
    role="tabpanel"
    aria-label="Search source files"
    use:filesPanelAction={'search'}
  >
  <label class="search-box">
    <Search size={16} strokeWidth={1.8} />
    <input bind:value={files.query} placeholder="Filter source files" />
  </label>

  <form class="global-search-panel" onsubmit={onGlobalSearchSubmit}>
    <div class="global-search-box">
      <Search size={15} strokeWidth={1.8} />
      <input bind:this={sourceSearchInput} bind:value={sourceSearchQuery} placeholder="Search file contents" />
      <button
        class="source-search-submit"
        type="submit"
        aria-label="Search source contents"
        title="Search source contents"
        disabled={sourceSearchLoading || sourceSearchQuery.trim().length === 0}
      >
        {#if sourceSearchLoading}
          <RefreshCw size={13} strokeWidth={1.8} />
        {:else}
          <Search size={13} strokeWidth={1.8} />
        {/if}
      </button>
    </div>

    {#if sourceSearchSummary}
      <div class="source-search-summary" title={sourceSearchSummary}>{sourceSearchSummary}</div>
    {/if}

    {#if sourceSearchResults.length > 0}
      <div class="source-search-results" aria-label="Source content search results">
        {#each sourceSearchResults as result (`${result.path}:${result.line}:${result.column}`)}
          <button
            type="button"
            title={`${result.relativePath}:${result.line}:${result.column}`}
            onclick={() => onSelectSearchResult(result)}
          >
            <span>
              <strong>{result.fileName}</strong>
              <small>{result.relativePath}:{result.line}:{result.column}</small>
            </span>
            <code>{result.excerpt}</code>
          </button>
        {/each}
      </div>
    {/if}
  </form>
  </div>

  <div
    id="source-files-recent-pane"
    class="source-files-pane source-files-recent-pane"
    role="tabpanel"
    aria-label="Recent source files"
    use:filesPanelAction={'recent'}
  >
  {#if projectRecentRecords.length > 0}
    <div class="recent-panel" aria-label="Recent source files">
      <div class="recent-list">
        {#each projectRecentRecords as recentRecord (recentRecord.path)}
          <button
            class:active={recentRecord.path === files.selectedRecord?.path}
            type="button"
            title={recentRecord.relativePath}
            onclick={() => onSelectRecentRecord(recentRecord)}
          >
            <span class="recent-file-icon">
              <FileCode2 size={14} strokeWidth={1.8} />
            </span>
            <span>
              <strong>{recentRecord.fileName}</strong>
              <small>{recentRecord.relativePath}</small>
            </span>
          </button>
        {/each}
      </div>
    </div>
  {:else}
    <div class="empty-tree">
      <History size={17} strokeWidth={1.8} />
      <span>No recent source files</span>
    </div>
  {/if}
  </div>

  <div
    id="source-files-tree-pane"
    class="source-list-panel source-files-pane"
    role="tabpanel"
    aria-label="Files in selected project"
    use:filesPanelAction={'files'}
  >
    <div class="tree-panel-header">
      <div class="source-section-toolbar">
        <div class="source-section-header tree-heading source-section-title" title={sourceSidebarTreeStatusLabel}>
          <FolderGit2 size={15} strokeWidth={1.8} />
          <span>{selectedProject.name}</span>
        </div>
        <button
          class="scan-diagnostic-button"
          type="button"
          aria-label="Copy source scan diagnostic"
          title="Copy source scan diagnostic"
          onclick={onCopyScanDiagnostic}
        >
          <Copy size={12} strokeWidth={1.9} />
        </button>
      </div>
      <div class="scan-summary-row">
        <div
          class="scan-summary"
          data-tone={selectedProjectScanEvidence.tone}
          title={sourceSidebarCompactStatusTitle}
        >
          <span class="index-summary" data-tone={selectedProjectScanEvidence.tone}>
            {sourceSidebarIndexStatusLabel}
          </span>
          <span class="scan-evidence" data-tone={selectedProjectScanEvidence.tone}>
            {sourceSidebarScanMetaLabel}
          </span>
        </div>
      </div>
      {#if sourceSidebarScanTelemetryExpanded}
        <div class="scan-stats" title={sourceScanStatsLabel}>{sourceSidebarScanTelemetryLabel}</div>
      {/if}
      {#if sourceRuntimeNoticeExpanded}
        <div class="scan-runtime-note" title={sourceRuntimeNotice}>
          <span>{sourceRuntimeNotice}</span>
          <button
            type="button"
            aria-label="Copy Tauri run command"
            title="Copy Tauri run command"
            onclick={onCopyTauriRunCommand}
          >
            Tauri
          </button>
        </div>
      {/if}
      {#if selectedProjectGitRootSuggestion}
        <div class="scan-root-correction" title={`Detected Git root: ${selectedProjectGitRootSuggestion}`}>
          <span>
            <strong>Nested root</strong>
            Use {formatSourceContextRootLabel(selectedProjectGitRootSuggestion)}
          </span>
          <button
            type="button"
            aria-label="Use detected Git root"
            title={selectedProjectGitRootSuggestion}
            disabled={projectRootValidating || files.scan.scanning}
            onclick={onUseValidatedGitRoot}
          >
            Use root
          </button>
        </div>
      {/if}
      {#if sourceScanHealthNote}
        <div class="scan-health-note" title={sourceScanHealthNote}>
          <span>{sourceScanHealthNote}</span>
          <button
            type="button"
            aria-label={`Reset index and scan up to ${expandedSourceScanLimit.toLocaleString()} files`}
            title={`Reset index and scan up to ${expandedSourceScanLimit.toLocaleString()} files`}
            onclick={() => onResetScanCache(selectedProject)}
          >
            Reset
          </button>
        </div>
      {/if}
      {#if sourceScanRecovery.visible}
        <div class="scan-recovery-panel" title={sourceScanRecovery.detail}>
          <span>
            <strong>{sourceScanRecovery.title}</strong>
            {sourceScanRecovery.detail}
          </span>
          <div class="scan-recovery-actions">
            <button
              type="button"
              aria-label="Reset source index"
              title="Reset source index"
              onclick={() => onResetScanCache(selectedProject)}
            >
              <RotateCcw size={12} strokeWidth={2} />
            </button>
            <button
              type="button"
              aria-label="Choose project root"
              title="Choose project root"
              disabled={choosingProjectRoot || projectRootValidating}
              onclick={onChooseProjectRoot}
            >
              <FolderSearch size={12} strokeWidth={2} />
            </button>
            <button
              type="button"
              aria-label="Copy source scan diagnostic"
              title="Copy source scan diagnostic"
              onclick={onCopyScanDiagnostic}
            >
              <Copy size={12} strokeWidth={2} />
            </button>
          </div>
        </div>
      {/if}
      {#if files.scan.limitReached && !files.scan.scanning}
        <button
          class="scan-more-button"
          type="button"
          aria-label={`Scan up to ${expandedSourceScanLimit.toLocaleString()} source files`}
          title={`Scan up to ${expandedSourceScanLimit.toLocaleString()} source files`}
          onclick={onScanMore}
        >
          <Plus size={13} strokeWidth={2} />
          <span>Scan {expandedSourceScanLimitShortLabel}</span>
        </button>
      {/if}
    </div>

    <div
      class="file-tree"
      bind:this={fileTreeElement}
      onscroll={onFileTreeScroll}
      aria-label="Files in selected project"
    >
      {#if files.scan.scanning && files.records.length === 0}
        {#each Array.from({ length: 8 }) as _, index}
          <div class="tree-skeleton" style={`--line-width: ${index % 3 === 0 ? 72 : index % 2 === 0 ? 54 : 86}%`}></div>
        {/each}
      {:else if visibleTreeRows.length === 0}
        <div class="empty-tree">
          <FileCode2 size={17} strokeWidth={1.8} />
          <span>No source files found</span>
        </div>
      {:else}
        <div
          class="tree-virtual-spacer"
          aria-hidden="true"
          style={`--tree-spacer-height: ${virtualizedTreeRows.topSpacerHeight}px`}
        ></div>
        {#each virtualizedTreeRows.rows as row, virtualTreeRowIndex (row.node.id)}
          {@const node = row.node}
          {@const isFolder = node.file === null}
          {@const isExpanded = isFolderExpanded(node)}
          {@const gitStatus = node.file ? gitStatusForSourceRecord(node.file) : null}
          <button
            class:active={!isFolder && node.file?.path === files.selectedRecord?.path}
            class:folder-row={isFolder}
            class:file-row={!isFolder}
            type="button"
            style={`--tree-level: ${row.level}`}
            title={node.relativePath}
            aria-expanded={isFolder ? isExpanded : undefined}
            data-tree-row-index={virtualizedTreeRows.startIndex + virtualTreeRowIndex}
            onclick={() => onSelectTreeNode(node)}
            onkeydown={(event) => onTreeRowKeydown(row, event)}
          >
            <span class="tree-indent"></span>
            <span class="tree-chevron">
              {#if isFolder}
                {#if isExpanded}
                  <ChevronDown size={13} strokeWidth={2} />
                {:else}
                  <ChevronRight size={13} strokeWidth={2} />
                {/if}
              {/if}
            </span>
            <span class="tree-icon">
              {#if isFolder}
                {#if isExpanded}
                  <FolderOpen size={15} strokeWidth={1.8} />
                {:else}
                  <Folder size={15} strokeWidth={1.8} />
                {/if}
              {:else}
                <FileCode2 size={15} strokeWidth={1.8} />
              {/if}
            </span>
            <strong>{node.name}</strong>
            {#if gitStatus}
              <span class="git-status-badge" title={gitStatus.status}>{gitStatus.badge}</span>
            {/if}
            <small>{isFolder ? node.children.length : node.file?.language}</small>
          </button>
        {/each}
        <div
          class="tree-virtual-spacer"
          aria-hidden="true"
          style={`--tree-spacer-height: ${virtualizedTreeRows.bottomSpacerHeight}px`}
        ></div>
      {/if}
    </div>
  </div>
  </SourceDockviewShell>
</div>

<style>
  .source-browser-stack {
    display: flex;
    flex-direction: column;
    flex: 1 1 auto;
    gap: 8px;
    height: 100%;
    min-height: 0;
    overflow: hidden;
  }

  .source-files-pane {
    flex: 1 1 auto;
    min-height: 0;
    overflow: hidden;
  }

  .source-files-search-pane {
    display: grid;
    grid-template-rows: auto minmax(0, 1fr);
    gap: 8px;
  }

  .source-files-search-pane .global-search-panel {
    grid-template-rows: auto auto minmax(0, 1fr);
    min-height: 0;
    padding-bottom: 0;
    margin-bottom: 0;
    border-bottom: 0;
  }

  .source-files-search-pane .source-search-results {
    max-height: none;
  }

  .source-files-recent-pane {
    display: grid;
    min-height: 0;
  }

  .source-files-recent-pane .recent-panel {
    min-height: 0;
    padding-bottom: 0;
    margin-bottom: 0;
    border-bottom: 0;
    overflow: hidden;
  }

  .source-files-recent-pane .recent-list {
    min-height: 0;
    overflow-x: hidden;
    overflow-y: auto;
    padding-right: 2px;
    scrollbar-color: rgba(174, 184, 181, 0.5) rgba(255, 255, 255, 0.045);
    scrollbar-width: thin;
  }

  .search-box {
    display: grid;
    grid-template-columns: 18px minmax(0, 1fr);
    align-items: center;
    gap: 8px;
    height: 38px;
    padding: 0 12px;
    margin-bottom: 12px;
    border: 1px solid rgba(255, 255, 255, 0.1);
    border-radius: 9px;
    color: #9aa5a1;
    background: rgba(255, 255, 255, 0.045);
  }

  .search-box:focus-within {
    border-color: rgba(92, 226, 207, 0.58);
    box-shadow: 0 0 0 3px rgba(92, 226, 207, 0.13);
  }

  .global-search-panel {
    display: grid;
    gap: 7px;
    padding-bottom: 12px;
    margin-bottom: 12px;
    border-bottom: 1px solid rgba(255, 255, 255, 0.08);
  }

  .global-search-box {
    display: grid;
    grid-template-columns: 17px minmax(0, 1fr) 30px;
    align-items: center;
    gap: 7px;
    height: 34px;
    padding: 0 4px 0 10px;
    color: #9aa5a1;
    border: 1px solid rgba(255, 255, 255, 0.1);
    border-radius: 9px;
    background: rgba(255, 255, 255, 0.035);
  }

  .global-search-box:focus-within {
    border-color: rgba(92, 226, 207, 0.48);
    box-shadow: 0 0 0 3px rgba(92, 226, 207, 0.1);
  }

  .source-search-submit {
    display: grid;
    place-items: center;
    width: 26px;
    height: 26px;
    color: #071b18;
    border: 0;
    border-radius: 7px;
    background: #6fdfcf;
    cursor: pointer;
  }

  .source-search-submit:disabled {
    color: #8d9995;
    background: rgba(255, 255, 255, 0.08);
    cursor: default;
  }

  .source-search-summary {
    min-width: 0;
    overflow: hidden;
    color: #8fd8cf;
    font-size: 10px;
    font-weight: 760;
    line-height: 1.2;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .source-search-results {
    display: grid;
    gap: 4px;
    max-height: 184px;
    min-height: 0;
    overflow-x: hidden;
    overflow-y: auto;
    padding-right: 2px;
    scrollbar-color: rgba(174, 184, 181, 0.5) rgba(255, 255, 255, 0.045);
    scrollbar-width: thin;
  }

  .source-search-results button {
    display: grid;
    gap: 3px;
    width: 100%;
    min-width: 0;
    padding: 7px 8px;
    color: #cbd3d1;
    text-align: left;
    border: 0;
    border-radius: 8px;
    background: rgba(255, 255, 255, 0.045);
    cursor: pointer;
  }

  .source-search-results button:hover {
    color: #f2f6f5;
    background: rgba(92, 226, 207, 0.11);
  }

  .source-search-results span {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: center;
    gap: 7px;
    min-width: 0;
  }

  .source-search-results strong,
  .source-search-results small,
  .source-search-results code {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .source-search-results strong {
    font-size: 11px;
    font-weight: 780;
  }

  .source-search-results small {
    color: #8d9995;
    font-size: 10px;
    font-weight: 700;
  }

  .source-search-results code {
    color: #d7dddb;
    font-family: var(--font-mono);
    font-size: 10px;
  }

  input {
    width: 100%;
    min-width: 0;
    color: #f3f5f4;
    border: 0;
    outline: 0;
    background: transparent;
  }

  input::placeholder {
    color: #6f7976;
  }

  .recent-panel {
    display: grid;
    gap: 6px;
    padding-bottom: 8px;
    margin-bottom: 8px;
    border-bottom: 1px solid rgba(255, 255, 255, 0.08);
  }

  .source-section-toolbar {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 22px;
    align-items: center;
    gap: 5px;
    min-width: 0;
  }

  .source-section-header {
    display: grid;
    grid-template-columns: 14px 18px minmax(0, 1fr) auto;
    align-items: center;
    gap: 6px;
    width: 100%;
    min-width: 0;
    height: 26px;
    padding: 0 5px;
    color: #aeb8b5;
    text-align: left;
    border: 0;
    border-radius: 6px;
    background: transparent;
    font-size: 12px;
    font-weight: 700;
    cursor: pointer;
  }

  .source-section-header:hover,
  .source-section-header:focus-visible {
    color: #eef6f3;
    outline: 0;
    background: rgba(255, 255, 255, 0.055);
  }

  .source-section-header span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .source-section-title {
    grid-template-columns: 18px minmax(0, 1fr);
    cursor: default;
  }

  .source-section-title:hover,
  .source-section-title:focus-visible {
    color: #aeb8b5;
    background: transparent;
  }

  .recent-file-icon {
    display: grid;
    place-items: center;
    min-width: 0;
  }

  .recent-list {
    display: grid;
    gap: 4px;
  }

  .recent-list button {
    display: grid;
    grid-template-columns: 18px minmax(0, 1fr);
    align-items: center;
    gap: 8px;
    width: 100%;
    height: 38px;
    padding: 0 8px;
    color: #cbd3d1;
    text-align: left;
    border-radius: 8px;
    background: transparent;
    cursor: pointer;
  }

  .recent-list button:hover,
  .recent-list button.active {
    color: #f2f6f5;
    background: rgba(92, 226, 207, 0.11);
  }

  .recent-file-icon {
    color: #8d9995;
  }

  .recent-list button.active .recent-file-icon {
    color: #6fdfcf;
  }

  .recent-list span {
    display: grid;
    min-width: 0;
  }

  .recent-list strong,
  .recent-list small {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .recent-list strong {
    font-size: 12px;
    line-height: 1.15;
  }

  .recent-list small {
    color: #7f8b87;
    font-size: 10px;
    font-weight: 700;
    line-height: 1.25;
  }

  .source-list-panel {
    display: grid;
    grid-template-rows: auto minmax(0, 1fr);
    flex: 1;
    min-height: 0;
    overflow: hidden;
  }

  .tree-panel-header {
    min-width: 0;
  }

  .tree-heading {
    height: 27px;
  }

  .scan-summary-row {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 22px;
    align-items: center;
    gap: 5px;
    margin: -3px 0 7px;
  }

  .scan-summary {
    display: flex;
    align-items: baseline;
    gap: 6px;
    min-width: 0;
    overflow: hidden;
    color: #7f8b87;
    font-size: 10px;
    font-weight: 720;
    line-height: 1.2;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .scan-summary .index-summary,
  .scan-summary .scan-evidence {
    min-width: 0;
    margin: 0;
    line-height: 1.2;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .scan-summary .index-summary {
    flex: 0 1 auto;
    max-width: min(42%, 150px);
  }

  .scan-summary .scan-evidence {
    flex: 1 1 0;
  }

  .scan-diagnostic-button {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 22px;
    height: 20px;
    border: 1px solid rgba(143, 216, 207, 0.14);
    border-radius: 5px;
    padding: 0;
    background: rgba(143, 216, 207, 0.06);
    color: #8a9693;
  }

  .scan-diagnostic-button:hover {
    border-color: rgba(143, 216, 207, 0.34);
    background: rgba(143, 216, 207, 0.12);
    color: #b8d6d1;
  }

  .index-summary {
    min-width: 0;
    margin: -4px 0 4px;
    overflow: hidden;
    color: #8fd8cf;
    font-size: 10px;
    font-weight: 760;
    line-height: 1.2;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .index-summary[data-tone='warning'] {
    color: #d8cba8;
  }

  .index-summary[data-tone='error'] {
    color: #ff9a9a;
  }

  .index-summary[data-tone='muted'] {
    color: #8a9693;
  }

  .scan-evidence {
    min-width: 0;
    margin: 0 0 7px;
    overflow: hidden;
    color: #6f7b78;
    font-size: 9.5px;
    font-weight: 720;
    line-height: 1.2;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .scan-evidence[data-tone='active'] {
    color: #8fd8cf;
  }

  .scan-evidence[data-tone='warning'] {
    color: #d8cba8;
  }

  .scan-evidence[data-tone='error'] {
    color: #ff9a9a;
  }

  .scan-stats {
    min-width: 0;
    margin: -5px 0 8px;
    overflow: hidden;
    color: #6f7b78;
    font-size: 9.5px;
    font-weight: 720;
    line-height: 1.2;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .scan-health-note {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: center;
    gap: 6px;
    margin: -1px 0 8px;
    border: 1px solid rgba(216, 170, 85, 0.24);
    border-radius: 6px;
    padding: 6px 7px;
    background: rgba(216, 170, 85, 0.08);
    color: #d8cba8;
    font-size: 10px;
    font-weight: 720;
    line-height: 1.25;
  }

  .scan-runtime-note {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: center;
    gap: 6px;
    margin: -1px 0 8px;
    border: 1px solid rgba(143, 216, 207, 0.2);
    border-radius: 6px;
    padding: 6px 7px;
    background: rgba(143, 216, 207, 0.07);
    color: #a8c8c3;
    font-size: 10px;
    font-weight: 720;
    line-height: 1.25;
  }

  .scan-runtime-note span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .scan-runtime-note button {
    height: 20px;
    border: 1px solid rgba(143, 216, 207, 0.24);
    border-radius: 5px;
    padding: 0 7px;
    background: rgba(143, 216, 207, 0.1);
    color: #bce8e2;
    font-size: 10px;
    font-weight: 820;
  }

  .scan-root-correction {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: center;
    gap: 6px;
    margin: -1px 0 8px;
    border: 1px solid rgba(216, 170, 85, 0.22);
    border-radius: 6px;
    padding: 5px 7px;
    background: rgba(216, 170, 85, 0.07);
    color: #d8cba8;
    font-size: 10px;
    font-weight: 720;
    line-height: 1.25;
  }

  .scan-root-correction span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .scan-root-correction strong {
    margin-right: 5px;
    color: #f0c978;
    font-weight: 840;
  }

  .scan-root-correction button {
    height: 20px;
    border: 1px solid rgba(216, 170, 85, 0.28);
    border-radius: 5px;
    padding: 0 7px;
    background: rgba(216, 170, 85, 0.12);
    color: #f1d99b;
    font-size: 10px;
    font-weight: 820;
  }

  .scan-root-correction button:hover:not(:disabled) {
    border-color: rgba(216, 170, 85, 0.44);
    background: rgba(216, 170, 85, 0.18);
    color: #ffe5a8;
  }

  .scan-root-correction button:disabled {
    opacity: 0.48;
  }

  .scan-health-note span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .scan-health-note button {
    height: 20px;
    border: 1px solid rgba(216, 170, 85, 0.28);
    border-radius: 5px;
    padding: 0 7px;
    background: rgba(216, 170, 85, 0.12);
    color: #f1d99b;
    font-size: 10px;
    font-weight: 820;
  }

  .scan-recovery-panel {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: center;
    gap: 7px;
    margin: -2px 0 8px;
    border: 1px solid rgba(111, 223, 207, 0.18);
    border-radius: 6px;
    padding: 6px 7px;
    background: rgba(111, 223, 207, 0.07);
    color: #a8c8c3;
    font-size: 10px;
    font-weight: 690;
    line-height: 1.25;
  }

  .scan-recovery-panel > span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .scan-recovery-panel strong {
    margin-right: 6px;
    color: #6fdfcf;
    font-weight: 820;
  }

  .scan-recovery-actions {
    display: inline-flex;
    align-items: center;
    gap: 4px;
  }

  .scan-recovery-actions button {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 21px;
    height: 20px;
    border: 1px solid rgba(143, 216, 207, 0.18);
    border-radius: 5px;
    padding: 0;
    background: rgba(143, 216, 207, 0.08);
    color: #9fc5bf;
  }

  .scan-recovery-actions button:hover:not(:disabled) {
    border-color: rgba(143, 216, 207, 0.38);
    background: rgba(143, 216, 207, 0.14);
    color: #d6f2ed;
  }

  .scan-recovery-actions button:disabled {
    opacity: 0.44;
  }

  .scan-more-button {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    max-width: 100%;
    min-height: 24px;
    margin: 0 0 8px;
    border: 1px solid rgba(111, 223, 207, 0.22);
    border-radius: 6px;
    padding: 0 8px;
    overflow: hidden;
    background: rgba(111, 223, 207, 0.09);
    color: #8fd8cf;
    font-size: 10px;
    font-weight: 800;
    white-space: nowrap;
  }

  .scan-more-button:hover {
    border-color: rgba(111, 223, 207, 0.42);
    background: rgba(111, 223, 207, 0.15);
  }

  .file-tree {
    min-height: 0;
    overflow-x: hidden;
    overflow-y: auto;
    overscroll-behavior: contain;
    padding: 4px 4px 4px 0;
    scrollbar-color: rgba(174, 184, 181, 0.58) rgba(255, 255, 255, 0.055);
    scrollbar-gutter: stable;
    scrollbar-width: thin;
  }

  .file-tree::-webkit-scrollbar {
    width: 10px;
  }

  .file-tree::-webkit-scrollbar-track {
    border-radius: 999px;
    background: rgba(255, 255, 255, 0.055);
  }

  .file-tree::-webkit-scrollbar-thumb {
    border: 2px solid rgba(19, 21, 21, 0.94);
    border-radius: 999px;
    background: rgba(174, 184, 181, 0.58);
  }

  .file-tree::-webkit-scrollbar-thumb:hover {
    background: rgba(218, 225, 223, 0.72);
  }

  .tree-virtual-spacer {
    height: var(--tree-spacer-height);
    min-height: var(--tree-spacer-height);
    pointer-events: none;
  }

  .file-tree button {
    display: grid;
    grid-template-columns: calc(var(--tree-level, 0) * 14px) 14px 18px minmax(0, 1fr) auto auto;
    align-items: center;
    gap: 7px;
    width: 100%;
    height: 30px;
    padding: 0 7px;
    color: #cbd3d1;
    text-align: left;
    border-radius: 7px;
    background: transparent;
    transition:
      background 150ms ease,
      color 150ms ease,
      transform 150ms ease;
  }

  .file-tree button:hover,
  .file-tree button.active {
    color: #f2f6f5;
    background: rgba(92, 226, 207, 0.12);
  }

  .file-tree button:active {
    transform: translateY(1px);
  }

  .file-tree button.folder-row {
    color: #d7dddb;
    font-weight: 700;
  }

  .tree-chevron,
  .tree-icon {
    display: grid;
    place-items: center;
    width: 16px;
    min-width: 0;
    color: #8d9995;
  }

  .tree-icon {
    color: #5fa7e8;
  }

  .folder-row .tree-icon {
    color: #d0a94f;
  }

  .file-tree strong,
  .file-tree small {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .file-tree strong {
    font-size: 12px;
  }

  .file-tree small {
    color: #7f8b87;
    font-size: 10px;
    font-weight: 700;
  }

  .file-tree button.file-row small {
    display: none;
  }

  .git-status-badge {
    display: inline-grid;
    place-items: center;
    min-width: 16px;
    height: 16px;
    padding: 0 4px;
    color: #071b18;
    border-radius: 5px;
    background: #d8aa55;
    font-family: var(--font-mono);
    font-size: 10px;
    font-weight: 850;
    line-height: 1;
  }

  .tree-skeleton {
    width: var(--line-width);
    height: 22px;
    margin: 5px 8px;
    border-radius: 7px;
    background: rgba(255, 255, 255, 0.07);
  }

  .empty-tree {
    display: grid;
    place-items: center;
    gap: 8px;
    min-height: 128px;
    color: #9aa5a1;
    text-align: center;
    font-size: 12px;
    font-weight: 700;
  }

  @container (max-width: 330px) {
    .search-box {
      grid-template-columns: 15px minmax(0, 1fr);
      gap: 6px;
      height: 32px;
      padding: 0 8px;
      margin-bottom: 8px;
    }

    .global-search-panel {
      gap: 5px;
      padding-bottom: 8px;
      margin-bottom: 8px;
    }

    .global-search-box {
      grid-template-columns: 15px minmax(0, 1fr) 24px;
      gap: 5px;
      height: 30px;
      padding: 0 3px 0 8px;
    }

    .source-search-submit {
      width: 22px;
      height: 22px;
      border-radius: 6px;
    }
  }
</style>
