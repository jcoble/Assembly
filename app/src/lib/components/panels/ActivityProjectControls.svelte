<script lang="ts">
  /**
   * ActivityProjectControls.svelte — the activity sidebar's project header.
   *
   * Renders the project `<select>` picker, the add/quick-open/scan button row,
   * the project path + remove control, the scan/index setup notice, the live
   * scan-progress readout, and the manual "add project" form.
   *
   * Presentational: every action is a callback and every value (including the
   * add-form's open/error/validating "spine" flags) is a prop the PAGE owns —
   * those flags are wired into the page's command palette + git-root/repair
   * flows, so they stay page-side and this component only renders them. The two
   * genuinely-transient form inputs (`nameInput` / `pathInput`) are `$bindable`
   * so the page's `startAddingProject()` reset still drives them.
   *
   * Renders inline in the activity sidebar (no teleport). Pattern mirrors
   * `ActivityClipboardPanel.svelte`. `.icon-button` chrome is shared and lives
   * once in `src/app.css` as a `:global` rule; this component inherits it.
   */
  import { Plus, RefreshCw, Save, Search, Trash2, X } from '@lucide/svelte';
  import Chip from '$lib/components/Chip.svelte';
  import type { ProjectRoot } from '$lib/sourceData';
  import type { NativeSourceScanProgress } from '$lib/tauriSource';

  interface Props {
    /** Bindable selected project id (page owns `projectStore.selectedID`). */
    selectedID?: string;
    /** Selectable projects (= page `projectOptions`). Read-only. */
    projectOptions: ProjectRoot[];
    /** Currently selected project (= page `selectedProject`). Read-only. */
    selectedProject: ProjectRoot;
    /** Whether the selected project is a removable custom root. */
    selectedProjectIsCustom: boolean;
    /** Whether a source scan is currently running (= page `files.scan.scanning`). */
    scanning: boolean;
    /** Live scan progress (= page `files.scan.progress`), shown while scanning. */
    scanProgress: NativeSourceScanProgress | null;
    /** Upper bound for a full source scan (= page `expandedSourceScanLimit`). */
    expandedSourceScanLimit: number;
    /** Short "12K"-style scan limit label (= page `expandedSourceScanLimitShortLabel`). */
    expandedSourceScanLimitShortLabel: string;
    /** Native scan-progress DOM event name (= page `sourceScanProgressEventName`). */
    sourceScanProgressEventName: string;
    /** Setup/index notice segments joined by " · " (= page `projectSetupNoticeText()`). */
    setupNoticeText: string;
    /** Full setup notice tooltip (= page `projectSetupNoticeTitle()`). */
    setupNoticeTitle: string;
    /** Whether the manual add-project form is open (page owns `addingProject`). */
    addingProject: boolean;
    /** Add-form error text (page owns `projectFormError`). */
    projectFormError: string;
    /** Whether a project-root validation is in flight (page owns `projectRootValidating`). */
    projectRootValidating: boolean;
    /** Whether the native folder picker is open (page owns `choosingProjectRoot`). */
    choosingProjectRoot: boolean;
    /** Bindable add-form name input (page owns `projectNameInput`). */
    nameInput?: string;
    /** Bindable add-form path input (page owns `projectPathInput`). */
    pathInput?: string;
    /** Project `<select>` change handler (page persists the selection). */
    onSelectProject: () => void;
    /** Remove the selected custom project root. */
    onRemoveProject: () => void;
    /** Open the native folder picker (the `+` button). */
    onChooseProjectRoot: () => void;
    /** Open the quick-open / file search overlay. */
    onOpenQuickOpen: () => void;
    /** Start a scan, or stop the running one. */
    onToggleScan: () => void;
    /** Submit the manual add-project form (page validates + persists). */
    onSubmitProject: (name: string, path: string) => void;
    /** Cancel / close the manual add-project form. */
    onCancelAddingProject: () => void;
  }

  let {
    selectedID = $bindable(''),
    projectOptions,
    selectedProject,
    selectedProjectIsCustom,
    scanning,
    scanProgress,
    expandedSourceScanLimit,
    expandedSourceScanLimitShortLabel,
    sourceScanProgressEventName,
    setupNoticeText,
    setupNoticeTitle,
    addingProject,
    projectFormError,
    projectRootValidating,
    choosingProjectRoot,
    nameInput = $bindable(''),
    pathInput = $bindable(''),
    onSelectProject,
    onRemoveProject,
    onChooseProjectRoot,
    onOpenQuickOpen,
    onToggleScan,
    onSubmitProject,
    onCancelAddingProject,
  }: Props = $props();

  function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    onSubmitProject(nameInput, pathInput);
  }
</script>

<div class="project-controls">
  <div class="project-row">
    <select bind:value={selectedID} onchange={onSelectProject} aria-label="Project">
      {#each projectOptions as project}
        <option value={project.id}>{project.name}</option>
      {/each}
    </select>
    <button
      class="icon-button"
      type="button"
      aria-label="Choose project folder"
      title="Choose project folder"
      disabled={choosingProjectRoot || projectRootValidating}
      onclick={onChooseProjectRoot}
    >
      <Plus size={16} strokeWidth={2} />
    </button>
    <button
      class="icon-button quick-open-trigger"
      type="button"
      aria-label="Open source file"
      title="Open source file"
      onclick={onOpenQuickOpen}
    >
      <Search size={16} strokeWidth={1.9} />
    </button>
    <button
      class="scan-button"
      type="button"
      aria-label={scanning ? 'Stop source scan' : `Scan up to ${expandedSourceScanLimit.toLocaleString()} source files`}
      title={scanning ? 'Stop source scan' : `Scan up to ${expandedSourceScanLimit.toLocaleString()} source files`}
      onclick={onToggleScan}
    >
      {#if scanning}
        <X size={15} strokeWidth={2} />
      {:else}
        <RefreshCw size={15} strokeWidth={1.8} />
      {/if}
      <span>{scanning ? 'Stop' : `Scan ${expandedSourceScanLimitShortLabel}`}</span>
    </button>
  </div>

  <div class="project-path-row">
    <span class="project-path" title={selectedProject.path}>{selectedProject.path}</span>
    {#if selectedProjectIsCustom}
      <button class="icon-button danger" type="button" aria-label="Remove project root" title="Remove project root" onclick={onRemoveProject}>
        <Trash2 size={14} strokeWidth={1.9} />
      </button>
    {/if}
  </div>
  <div class="project-setup-row" title={setupNoticeTitle} aria-live={scanning ? 'polite' : 'off'}>
    {#each setupNoticeText.split(' · ') as setupSegment, setupIndex (setupIndex)}
      <Chip size="xs" tone="muted">{setupSegment}</Chip>
    {/each}
  </div>

  {#if scanning && scanProgress}
    <div class="scan-progress" aria-live="polite" data-progress-event={sourceScanProgressEventName}>
      <span>{scanProgress.matchedFiles.toLocaleString()} files</span>
      <span>{scanProgress.visitedEntries.toLocaleString()} entries checked</span>
    </div>
  {/if}

  {#if addingProject}
    <form class="project-form" onsubmit={handleSubmit}>
      <label>
        <span>Name</span>
        <input bind:value={nameInput} autocomplete="off" />
      </label>
      <label>
        <span>Path</span>
        <input bind:value={pathInput} autocomplete="off" placeholder="/Users/blackcolours/dev/work/project" />
      </label>
      {#if projectFormError}
        <p class="project-form-error">{projectFormError}</p>
      {/if}
      <div class="form-actions">
        <button class="form-button" type="button" disabled={projectRootValidating} onclick={onCancelAddingProject}>
          <X size={14} strokeWidth={2} />
          <span>Cancel</span>
        </button>
        <button class="form-button primary" type="submit" disabled={projectRootValidating}>
          <Save size={14} strokeWidth={2} />
          <span>{projectRootValidating ? 'Checking' : 'Save'}</span>
        </button>
      </div>
    </form>
  {/if}
</div>

<style>
  /*
   * `.icon-button` base/hover/focus/danger + its narrow-container arm live once
   * in `src/app.css` as `:global` (shared base, used here for the +/search/remove
   * buttons); this component inherits them. Everything below is project-controls-
   * exclusive and was moved verbatim from the `+page.svelte` `<style>` block, plus
   * a local `select` base/focus copy (the page's scoped `select` rule no longer
   * reaches this moved markup). The `@container` block queries the page-owned
   * `.sidebar` ancestor (container-type: inline-size), so the responsive arms keep
   * working from inside this child component.
   */
  .project-controls {
    min-width: 0;
    max-width: 100%;
    overflow: hidden;
    margin-bottom: 12px;
  }

  .project-row {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 36px 36px 96px;
    align-items: center;
    gap: var(--space-2);
    min-width: 0;
    max-width: 100%;
    margin-bottom: var(--space-3);
  }

  select,
  .scan-button,
  .form-button {
    height: 36px;
    min-width: 0;
    color: var(--color-text);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    background: var(--color-surface);
  }

  select {
    width: 100%;
    padding: 0 var(--space-3);
    outline: 0;
  }

  select:focus,
  .scan-button:focus-visible,
  .form-button:focus-visible {
    border-color: var(--color-focus);
    box-shadow: var(--focus-ring);
  }

  .scan-button {
    display: grid;
    grid-template-columns: 16px minmax(0, 1fr);
    align-items: center;
    gap: var(--space-2);
    padding: 0 var(--space-3);
    color: var(--color-text-2);
    font-size: var(--text-sm);
    font-weight: var(--weight-medium);
    cursor: pointer;
  }

  .scan-button:disabled {
    cursor: default;
    opacity: 0.58;
  }

  .scan-button span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .project-path-row {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: center;
    gap: var(--space-2);
    min-height: 28px;
    margin-bottom: var(--space-1);
    color: var(--color-text-3);
    font-family: var(--font-mono);
    font-size: var(--text-xs);
    font-weight: var(--weight-normal);
  }

  .project-path {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .project-setup-row {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--space-1);
    min-height: 18px;
    margin: var(--space-1) 0 var(--space-2);
    line-height: 1.25;
  }

  .scan-progress {
    display: flex;
    flex-wrap: wrap;
    gap: var(--space-1) var(--space-3);
    min-height: 18px;
    margin: var(--space-1) 0;
    color: var(--color-live);
    font-family: var(--font-mono);
    font-size: var(--text-xs);
    font-weight: var(--weight-medium);
  }

  .scan-progress span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .project-form {
    display: grid;
    gap: 8px;
    padding: 10px;
    margin-top: 8px;
    border: 1px solid rgba(255, 255, 255, 0.1);
    border-radius: 10px;
    background: rgba(255, 255, 255, 0.04);
  }

  .project-form label {
    display: grid;
    gap: 5px;
  }

  .project-form label span {
    color: #9aa5a1;
    font-size: 11px;
    font-weight: 760;
  }

  .project-form input {
    height: 32px;
    padding: 0 9px;
    color: #f3f5f4;
    border: 1px solid rgba(255, 255, 255, 0.1);
    border-radius: 8px;
    outline: 0;
    background: rgba(0, 0, 0, 0.18);
  }

  .project-form input:focus {
    border-color: rgba(92, 226, 207, 0.58);
    box-shadow: 0 0 0 3px rgba(92, 226, 207, 0.13);
  }

  .project-form-error {
    margin: 0;
    color: #f1a9a0;
    font-size: 11px;
    font-weight: 700;
  }

  .form-actions {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 8px;
  }

  .form-button {
    display: grid;
    grid-template-columns: 15px minmax(0, 1fr);
    align-items: center;
    gap: 6px;
    padding: 0 9px;
    color: #cbd3d1;
    font-size: 12px;
    font-weight: 760;
    cursor: pointer;
  }

  .form-button.primary {
    color: #071b18;
    border-color: rgba(111, 223, 207, 0.72);
    background: #6fdfcf;
  }

  .form-button span {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  @container (max-width: 330px) {
    .project-controls {
      margin-bottom: 8px;
    }

    .project-row {
      grid-template-columns: minmax(74px, 1fr) 30px 30px 34px;
      gap: 5px;
      margin-bottom: 6px;
    }

    select,
    .scan-button,
    .form-button {
      height: 30px;
      border-radius: 8px;
    }

    select {
      padding: 0 8px;
    }

    .scan-button {
      grid-template-columns: 1fr;
      place-items: center;
      gap: 0;
      padding: 0;
    }

    .scan-button span {
      position: absolute;
      width: 1px;
      height: 1px;
      padding: 0;
      margin: -1px;
      overflow: hidden;
      clip: rect(0, 0, 0, 0);
      white-space: nowrap;
      border: 0;
    }

    .project-path-row,
    .project-setup-row {
      font-size: 9px;
    }
  }
</style>
