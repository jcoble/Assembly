<script lang="ts">
  /**
   * ChangedFileRow.svelte — one changed file in the Source control panel.
   *
   * The row is the kit's `ListRow`: a status letter, the file's name, the folder
   * it sits in, and what changed about it. Clicking it shows the file's changes
   * in the middle of the shell; right-clicking offers the same thing plus the
   * three ways of changing this one file and the four ways of getting at it
   * outside this panel.
   *
   * The three actions worth a press without opening a menu — stage or unstage,
   * discard, and open the file in the editor — also sit in the row's hover
   * cluster, because putting a commit together is per file work: a working copy
   * usually holds two or three unrelated changes and "Stage All" can only make
   * one commit out of them, and reading a change usually means opening it.
   *
   * NOTHING IS THROWN AWAY FROM HERE. Discard hands the request up to the panel,
   * which asks first; this row never reaches the service for it.
   */
  import { HoverActionButton } from '$lib/components/ui/hover-actions/index.js';
  import { ListRow } from '$lib/components/ui/list-row/index.js';
  import * as ContextMenu from '$lib/components/ui/context-menu/index.js';
  import FileIcon from '$lib/shell/components/explorer/FileIcon.svelte';
  import FileSymlink from '@lucide/svelte/icons/file-symlink';
  import Minus from '@lucide/svelte/icons/minus';
  import Plus from '@lucide/svelte/icons/plus';
  import Undo2 from '@lucide/svelte/icons/undo-2';
  import { absolutePathWithin } from '$lib/shell/git/gitService';
  import { describeGitFileChange, gitFileTitle } from '$lib/shell/git/gitPanelStore.svelte';
  import { openDiffForFile, openFileInEditor } from '$lib/shell/workbenchNavigation';
  import { revealPathFromTauri } from '$lib/tauriSource';
  import { nativeWorkspacePath } from '$lib/workspacePaths.ts';
  import type { ProjectGitFileStatus } from '$lib/tauriSource';
  import { splitRepositoryPath } from '$lib/shell/git/gitCommitFilesStore.svelte';

  import {
    sourceControlFileActions,
    type SourceControlFileActionId
  } from './sourceControlFileMenu.ts';

  interface Props {
    file: ProjectGitFileStatus;
    /** The repository folder the panel is pointed at. '' before there is one. */
    root: string;
    selected?: boolean;
    /** Can this page change the repository at all? */
    canWrite: boolean;
    /** Why not, when it cannot. */
    readOnlyReason: string;
    /** True while another source-control action is still running. */
    busy: boolean;
    /** False while reading a linked worktree, so editor ownership stays with the session. */
    canOpenInEditor?: boolean;
    onStage(file: ProjectGitFileStatus): void;
    onUnstage(file: ProjectGitFileStatus): void;
    /** Ask for this file's changes to be thrown away. The panel asks the person. */
    onRequestDiscard(file: ProjectGitFileStatus): void;
  }
  let {
    file,
    root,
    selected = false,
    canWrite,
    readOnlyReason,
    busy,
    canOpenInEditor = true,
    onStage,
    onUnstage,
    onRequestDiscard
  }: Props = $props();

  const parts = $derived(splitRepositoryPath(file.relativePath));
  const actions = $derived(
    sourceControlFileActions(file, root, { canWrite, readOnlyReason, busy, canOpenInEditor })
  );
  const absolutePath = $derived(root === '' ? '' : absolutePathWithin(root, file.relativePath));

  /** One action by id, for the two the hover cluster draws as buttons. */
  function actionById(id: SourceControlFileActionId) {
    return actions.find((entry) => entry.id === id);
  }

  const stageAction = $derived(actionById('stage'));
  const unstageAction = $derived(actionById('unstage'));
  const discardAction = $derived(actionById('discard'));
  const openAction = $derived(actionById('open-in-editor'));

  /**
   * The hover cluster shows the one staging direction this file has left to go.
   * A file with both staged and unstaged work can go either way, and then the
   * unstaged half is the one a press is usually meant for.
   */
  const stageDirection = $derived(stageAction?.enabled ? 'stage' : 'unstage');

  /** What a hover button says: what it will do, or why it will not. */
  function hint(action: { label: string; enabled: boolean; disabledReason: string | null }): string {
    return action.enabled ? `${action.label} — ${parts.name}` : (action.disabledReason ?? action.label);
  }

  /** The letter's color sorts the list by eye before anyone reads a path. */
  const badgeTone = $derived.by(() => {
    if (file.badge === 'A' || file.badge === '?') return 'text-[var(--color-good)]';
    if (file.badge === 'D') return 'text-[var(--color-bad)]';
    if (file.badge === 'R' || file.badge === 'C') return 'text-[var(--color-accent)]';
    return 'text-[var(--color-attention)]';
  });

  /**
   * What the row says about the change, when there is anything left to say.
   *
   * The letter at the head of the row already names the state, and the section
   * the row sits under names it a second time, so printing the word as well put
   * "untracked" three times on one line and made a wall of the right edge. The
   * word earns its place only where the letter cannot carry the whole answer:
   * a file staged one way and changed another has two states and one letter.
   * The full description stays in the row's title for a hover.
   */
  const changeWord = $derived.by(() => {
    const { indexStatus, worktreeStatus } = file;
    if (indexStatus && worktreeStatus && indexStatus !== worktreeStatus) {
      return `${indexStatus} + ${worktreeStatus}`;
    }
    return file.badge === '' ? describeGitFileChange(file) : '';
  });

  function copy(text: string): void {
    if (typeof navigator === 'undefined' || !navigator.clipboard) return;
    void navigator.clipboard.writeText(text);
  }

  function showChanges(): void {
    if (root === '') return;
    void openDiffForFile({ projectRoot: root, relativePath: file.relativePath });
  }

  function run(id: SourceControlFileActionId): void {
    if (id === 'view') showChanges();
    else if (id === 'stage') onStage(file);
    else if (id === 'unstage') onUnstage(file);
    else if (id === 'discard') onRequestDiscard(file);
    else if (id === 'copy-path') copy(nativeWorkspacePath(absolutePath));
    else if (id === 'copy-relative-path') copy(file.relativePath);
    else if (id === 'open-in-editor') openFileInEditor({ path: absolutePath, projectRoot: root });
    else if (id === 'reveal-in-finder') void revealPathFromTauri(absolutePath);
  }

  let menuOpen = $state(false);
  /** Pointer or focus is on the row: only then are its hover buttons mounted. */
  let hot = $state(false);
</script>

<!-- The menu is mounted only while open: a closed one still leaves an empty
     portal in document.body, and a thousand rows of those froze WebKit. -->
<ContextMenu.Root bind:open={menuOpen}>
  <ContextMenu.Trigger>
    {#snippet child({ props })}
      <div
        {...props}
        class="min-w-0"
        onpointerenter={() => (hot = true)}
        onpointerleave={(event) => (hot = event.currentTarget.contains(document.activeElement))}
        onfocusin={() => (hot = true)}
        onfocusout={(event) => (hot = event.currentTarget.contains(event.relatedTarget as Node | null))}
      >
        <ListRow
          {selected}
          onclick={showChanges}
          actionsLabel="File actions"
          data-testid={`source-control-file-${file.relativePath}`}
          class="min-w-0"
        >
          <span
            class={`w-3 shrink-0 text-center font-mono text-sm leading-none ${badgeTone}`}
            aria-hidden="true">{file.badge}</span
          >
          <FileIcon fileName={parts.name} size={14} />
          <span class="min-w-0 flex-1 truncate" title={gitFileTitle(file)}>
            {parts.name}
            {#if parts.folder !== ''}
              <span class="text-sm text-muted-foreground">{parts.folder}</span>
            {/if}
          </span>
          <!-- The stage and discard buttons appear at the right edge on hover —
               exactly where this word sits — and their discs are see-through,
               so "modified" was reading straight through the glyphs. The word
               steps aside the moment the row is hovered or focused, the way
               a worktree row's age does; the buttons take its place. -->
          {#if changeWord !== ''}
            <span class="shrink-0 text-sm text-muted-foreground transition-opacity group-hover:opacity-0 group-focus-within:opacity-0">
              {changeWord}
            </span>
          {/if}

          {#snippet actions()}
            <!-- Built on hover or focus: a thousand rows of tooltip buttons took seconds to draw. -->
            {#if hot}
              {#if stageDirection === 'stage' && stageAction}
                <HoverActionButton
                  label={hint(stageAction)}
                  disabled={!stageAction.enabled}
                  onclick={() => run('stage')}
                >
                  <Plus aria-hidden="true" />
                </HoverActionButton>
              {:else if unstageAction}
                <HoverActionButton
                  label={hint(unstageAction)}
                  disabled={!unstageAction.enabled}
                  onclick={() => run('unstage')}
                >
                  <Minus aria-hidden="true" />
                </HoverActionButton>
              {/if}
              {#if discardAction}
                <HoverActionButton
                  label={hint(discardAction)}
                  disabled={!discardAction.enabled}
                  onclick={() => run('discard')}
                >
                  <Undo2 aria-hidden="true" />
                </HoverActionButton>
              {/if}
              {#if openAction}
                <HoverActionButton
                  label={hint(openAction)}
                  disabled={!openAction.enabled}
                  onclick={() => run('open-in-editor')}
                >
                  <FileSymlink aria-hidden="true" />
                </HoverActionButton>
              {/if}
            {/if}
          {/snippet}
        </ListRow>
      </div>
    {/snippet}
  </ContextMenu.Trigger>

  {#if menuOpen}
    <!-- No scroll lock: it restyles <body>, which took WebKit a quarter second per change. -->
    <ContextMenu.Content class="w-[220px]" aria-label="File actions" preventScroll={false}>
      {#each actions as item (item.id)}
        <ContextMenu.Item
          data-testid={`source-control-file-menu-${item.id}`}
          disabled={!item.enabled}
          title={item.enabled ? undefined : item.disabledReason}
          onSelect={() => run(item.id)}>{item.label}</ContextMenu.Item
        >
      {/each}
    </ContextMenu.Content>
  {/if}
</ContextMenu.Root>
