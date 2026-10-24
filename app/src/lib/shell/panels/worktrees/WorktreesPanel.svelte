<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  /**
   * WorktreesPanel.svelte — the Worktrees tab of the right column.
   *
   * What this panel is FOR: deciding what to do with the folders a repository
   * has scattered around the disk. Every worktree is somebody's unfinished work
   * until proven otherwise, so the panel is built around not losing any.
   *
   * It reads `worktreeManager` and never the machine: nothing loads until the
   * shell calls `worktreeManagerService.activate(...)`, which it does when this
   * tab comes into view, so a cold launch reads no repositories at all.
   *
   * THE ONE THING THAT CHANGED: the panel used to print three commands per
   * worktree for the reader to paste into a terminal, because nothing in the app
   * could be trusted to look before it deleted. Now the two removals start a
   * session in that folder — visible in the sessions list like any other — that
   * reads the worktree, explains what it found, warns about anything that would
   * be lost, and only then acts. The commands did not go away; they became the
   * substance of what that session is told to do.
   *
   * Inspect is the exception, and starts nothing: a whole session in a folder is
   * a lot to set up for a question about facts this panel already has. It hands
   * those facts to the app's small utility model and shows the answer under the
   * row. So an answer here is about what the list last read, not about the
   * folder as it is this second — the removals are the ones that go and look.
   *
   * So this panel removes nothing itself. The one exception is the quiet "clear
   * this entry" on a row whose folder is already gone, which touches no files
   * and still asks first — it used to act on the first click, and on an app
   * build that clears folder-gone rows together it took another row with it,
   * which nobody had been told.
   *
   * The rows themselves are worked out by `worktreeManagerRows.ts` (pure and
   * tested); this file arranges them and nothing else.
   */
  import FolderGit2 from '@lucide/svelte/icons/folder-git-2';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import Search from '@lucide/svelte/icons/search';

  import { EmptyState } from '$lib/components/ui/empty-state/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { PanelHeader } from '$lib/components/ui/panel-header/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import { sessionRowJump } from '$lib/shell/components/sessionRowJump';
  import { startWorkbenchSession } from '$lib/shell/workbenchNavigation';
  import { runHelperJobFromTauri } from '$lib/tauriSource';
  import {
    buildWorktreeManagerRows,
    describeRemovalQuestion,
    filterWorktreeRows,
    otherFolderGoneBranches,
    primaryCheckoutPath,
    summarizeWorktreeManager,
    type WorktreeManagerRow
  } from '$lib/shell/worktrees/worktreeManagerRows';
  import { clearWorktreeEntry, refresh } from '$lib/shell/worktrees/worktreeManagerService';
  import {
    toggleWorktreeDetail,
    worktreeManager
  } from '$lib/shell/worktrees/worktreeManagerStore.svelte';

  import ConfirmWorktreeDialog from './ConfirmWorktreeDialog.svelte';
  import WorktreeDetail from './WorktreeDetail.svelte';
  import WorktreeRow from './WorktreeRow.svelte';
  import {
    describeWorktreeAgentQuestion,
    worktreeAgentActions,
    worktreeAgentPrompt,
    worktreeInspectFacts,
    type WorktreeAgentActionId,
    type WorktreeSessionActionId
  } from './worktreeAgentPrompts.ts';

  interface Props {
    visible: boolean;
    root: string;
    ownedId: string | null;
  }
  // The panel is pointed at a repository by `worktreeManagerService.activate`,
  // which the shell calls with the same selection; reading the props here would
  // be a second, competing source for the same answer.
  let {}: Props = $props();

  /** What is being asked about, and which question it is. */
  let asking = $state<
    | { kind: 'clear'; row: WorktreeManagerRow }
    | { kind: 'agent'; row: WorktreeManagerRow; action: WorktreeSessionActionId }
    | null
  >(null);

  /** The path of the row a session is being started for, or ''. */
  let starting = $state('');
  /** What the last started session was, in one sentence. '' when there is none. */
  let startedMessage = $state('');
  /** Why a session could not be started. '' when the last one was. */
  let startError = $state('');

  /** The path of the row the utility model is being asked about, or ''. */
  let inspecting = $state('');
  /**
   * What the utility model said about a worktree, by folder. It stays on the row it
   * belongs to for as long as the row is listed, so opening another row and
   * coming back does not lose the answer.
   */
  let inspected = $state<Record<string, { text: string; failed: boolean }>>({});

  /** What the backend says when no key is stored, word for word. */
  const HELPER_OFF = 'No key — utility model off';

  const rows = $derived(
    buildWorktreeManagerRows({
      worktrees: worktreeManager.worktrees,
      repositories: worktreeManager.repositories,
      sessions: worktreeManager.sessions,
      primaryPath: worktreeManager.root
    })
  );
  const shown = $derived(filterWorktreeRows(rows, worktreeManager.filter));
  const summary = $derived(summarizeWorktreeManager(rows));
  const panelBusy = $derived(worktreeManager.busyAction !== '' || starting !== '');
  /** The filter is hiding rows, and saying so beats an unexplained short list. */
  const hiddenByFilter = $derived(rows.length - shown.length);
  /** The main checkout: the folder every removal command has to be run from. */
  const primaryPath = $derived(
    primaryCheckoutPath(worktreeManager.worktrees, worktreeManager.root)
  );

  /** The open question, written out by whichever pure function owns its wording. */
  const question = $derived(
    asking === null
      ? null
      : asking.kind === 'clear'
        ? describeRemovalQuestion(asking.row, {
            kind: 'clear',
            pruneSingleRow: worktreeManager.pruneSingleRowSupport,
            otherFolderGoneBranches: otherFolderGoneBranches(rows, asking.row)
          })
        : describeWorktreeAgentQuestion(asking.action, asking.row)
  );

  function actionsFor(row: WorktreeManagerRow) {
    return worktreeAgentActions(row);
  }

  /**
   * Do one action. Inspect asks the helper and shows the answer under the row;
   * it starts nothing, so it is not worth a dialog — a dialog in front of a read
   * is a dialog people learn to dismiss without reading. The two that can remove
   * work ask first.
   */
  function act(row: WorktreeManagerRow, id: WorktreeAgentActionId): void {
    if (id === 'inspect') {
      void inspect(row);
      return;
    }
    const action = worktreeAgentActions(row).find((entry) => entry.id === id);
    if (!action || !action.enabled) return;
    asking = { kind: 'agent', row, action: id };
  }

  /**
   * Ask the helper about one worktree. The answer belongs under the row it is
   * about, so a row asked about from its closed state is opened first — the
   * button is on the closed row too, and an answer nobody can see is a button
   * that appears to do nothing.
   */
  async function inspect(row: WorktreeManagerRow): Promise<void> {
    if (inspecting !== '') return;
    if (worktreeManager.selectedPath !== row.path) toggleWorktreeDetail(row.path);
    inspecting = row.path;
    try {
      const answer = await runHelperJobFromTauri('inspect', worktreeInspectFacts(row));
      inspected = {
        ...inspected,
        [row.path]: {
          text: answer.trim() || 'The utility model had nothing to say about this worktree.',
          failed: false
        }
      };
    } catch (error) {
      const sentence = error instanceof Error ? error.message : String(error);
      inspected = {
        ...inspected,
        [row.path]: {
          text:
            sentence === HELPER_OFF
              ? 'Utility model unavailable — add a key in Settings → Utility model'
              : sentence,
          failed: true
        }
      };
    } finally {
      inspecting = '';
    }
  }

  async function startFor(row: WorktreeManagerRow, id: WorktreeSessionActionId): Promise<void> {
    startedMessage = '';
    startError = '';
    starting = row.path;
    try {
      const request = worktreeAgentPrompt(id, row, primaryPath || null);
      const ownedId = await startWorkbenchSession(request);
      if (ownedId) {
        startedMessage = `Started “${request.title}”. Watch it in the sessions list — it reports before it changes anything.`;
      } else {
        startError = 'No session could be started here. Sessions only start in the desktop app.';
      }
    } catch (error) {
      startError = `The session did not start: ${
        error instanceof Error ? error.message : String(error)
      }`;
    } finally {
      starting = '';
    }
  }

  /** Do the thing that was asked about, whichever of the two it was. */
  function confirmAsked(): void {
    const open = asking;
    asking = null;
    if (!open) return;
    if (open.kind === 'clear') void clearWorktreeEntry(open.row.path, open.row.branch);
    else void startFor(open.row, open.action);
  }
</script>

<div class="flex h-full w-full min-w-0 flex-col text-foreground">
  <PanelHeader
    title={worktreeManager.projectName || 'Worktrees'}
    count={worktreeManager.activated ? rows.length : null}
  >
    {#snippet actions()}
      <IconButton
        label={worktreeManager.loading ? 'Reading the worktrees…' : 'Read the worktrees again'}
        disabled={!worktreeManager.activated || worktreeManager.loading}
        onclick={() => void refresh()}
      >
        {#if worktreeManager.loading}<WorkingSpinner />{:else}<RefreshCw aria-hidden="true" />{/if}
      </IconButton>
    {/snippet}
    {#if worktreeManager.projectName}{worktreeManager.projectName} · {/if}{worktreeManager.activated ? summary : 'Not looked yet'}
  </PanelHeader>

  {#if !worktreeManager.activated}
    <EmptyState
      title="No repository picked yet"
      body="Pick a session and this shows the worktrees of the repository it belongs to."
    >
      {#snippet icon()}<FolderGit2 aria-hidden="true" />{/snippet}
    </EmptyState>
  {:else if worktreeManager.unavailableReason}
    <EmptyState title="Nothing to show" body={worktreeManager.unavailableReason}>
      {#snippet icon()}<FolderGit2 aria-hidden="true" />{/snippet}
    </EmptyState>
  {:else if worktreeManager.error}
    <div class="flex flex-col items-start gap-2 px-(--space-4) py-(--space-3)">
      <p class="m-0 text-(length:--text-body) leading-normal text-[var(--color-bad)]">
        {worktreeManager.error}
      </p>
      <IconButton label="Try reading the worktrees again" onclick={() => void refresh()}>
        <RefreshCw aria-hidden="true" />
      </IconButton>
    </div>
  {:else}
    <div class="relative shrink-0 px-2 pb-2">
      <Search class="pointer-events-none absolute top-1/2 left-5.5 size-4 -translate-y-[calc(50%+4px)] text-muted-foreground" aria-hidden="true" />
      <Input
        class="h-8 rounded-full border-transparent bg-muted dark:bg-muted pl-8 text-(length:--text-body)"
        placeholder="Filter by branch, task, or folder"
        autocomplete="off"
        spellcheck="false"
        bind:value={worktreeManager.filter}
      />
    </div>

    {#if worktreeManager.actionError || startError}
      <p class="m-0 px-(--space-4) pb-(--space-3) text-(length:--text-quiet) leading-normal text-[var(--color-bad)]">
        {worktreeManager.actionError || startError}
      </p>
    {:else if worktreeManager.actionMessage || startedMessage}
      <p class="m-0 px-(--space-4) pb-(--space-3) text-(length:--text-quiet) leading-normal text-muted-foreground">
        {worktreeManager.actionMessage || startedMessage}
      </p>
    {/if}

    <ScrollArea class="min-h-0 flex-1">
      <div class="px-(--space-3) pb-(--space-4)">
        {#if rows.length === 0}
          <EmptyState
            title={worktreeManager.loading ? 'Looking for worktrees…' : 'Only the main checkout'}
            body={worktreeManager.loading
              ? 'Reading this repository now.'
              : 'This repository has no worktrees beside its main checkout. One appears here as soon as you create it.'}
          >
            {#snippet icon()}<FolderGit2 aria-hidden="true" />{/snippet}
          </EmptyState>
        {:else}
          <ul class="m-0 flex list-none flex-col gap-1 p-0">
            {#each shown as row (row.path)}
              {@const agentActions = actionsFor(row)}
              <WorktreeRow
                {row}
                {agentActions}
                {panelBusy}
                expanded={worktreeManager.selectedPath === row.path}
                busy={starting === row.path}
                onToggle={() => toggleWorktreeDetail(row.path)}
                onAction={(id) => act(row, id)}
                onAskClear={() => (asking = { kind: 'clear', row })}
              >
                {#snippet detail()}
                  <WorktreeDetail
                    {row}
                    actions={agentActions}
                    {panelBusy}
                    busy={starting === row.path}
                    inspecting={inspecting === row.path}
                    inspectMessage={inspected[row.path]?.text ?? ''}
                    inspectFailed={inspected[row.path]?.failed ?? false}
                    onAction={(id) => act(row, id)}
                    onOpenSession={(id) => void sessionRowJump(id, 'session')}
                  />
                {/snippet}
              </WorktreeRow>
            {/each}
          </ul>
          {#if hiddenByFilter > 0}
            <p class="m-0 px-(--space-2) pt-(--space-3) text-(length:--text-quiet) leading-normal text-muted-foreground">
              {hiddenByFilter}
              {hiddenByFilter === 1 ? 'worktree is' : 'worktrees are'} hidden by the filter.
            </p>
          {/if}
        {/if}
      </div>
    </ScrollArea>
  {/if}
</div>

<ConfirmWorktreeDialog
  {question}
  folderName={asking?.row.folderName ?? ''}
  open={asking !== null}
  onOpenChange={(open) => {
    if (!open) asking = null;
  }}
  onConfirm={confirmAsked}
/>
