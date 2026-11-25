<script lang="ts">
  /**
   * RunPanel.svelte — the Run tab of the right column.
   *
   * The project's saved actions: the commands you keep starting, each with the
   * shortcut that starts it, the page it serves, and what it is doing right
   * now.
   *
   * NOTHING POLLS, and no output subscriptions stay open while this tab is
   * hidden. Saved actions persist to `.NET` SQLite via `preferencesStore` and
   * run through `.NET` `terminal/*` + `/socket/terminal`.
   */
  import Play from '@lucide/svelte/icons/play';
  import Plus from '@lucide/svelte/icons/plus';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';

  import { preferencesStore } from '$lib/application/preferences.svelte';
  import * as AlertDialog from '$lib/ui/alert-dialog/index.js';
  import { Button } from '$lib/ui/button/index.js';
  import * as Card from '$lib/ui/card/index.js';
  import { EmptyState } from '$lib/ui/empty-state/index.js';
  import { IconButton } from '$lib/ui/icon-button/index.js';
  import { PanelHeader } from '$lib/ui/panel-header/index.js';
  import { ScrollArea } from '$lib/ui/scroll-area/index.js';
  import WorkingSpinner from '$lib/ui/spinner/WorkingSpinner.svelte';

  import AddActionDialog from './AddActionDialog.svelte';
  import RunActionRow from './RunActionRow.svelte';
  import RunningProcessRow from './RunningProcessRow.svelte';
  import { watchRunOutput } from './api';
  import {
    allStackRows,
    clearStackNotice,
    handleRunKeydown,
    isStackBusy,
    noteRunTerminalExit,
    openPreviewUrl,
    refreshStacks,
    removeStack,
    restartStack,
    setStackInput,
    stacks,
    startStack,
    stopStack,
    syncRunPreferences
  } from './store.svelte';
  import type { RunOutputWatch, StackDefinition, StackRow } from './types';

  interface Props {
    /** True while this is the tab on screen. */
    visible: boolean;
    /** The active session's working folder, or '' when nothing is selected. */
    root: string;
    /** The active session's id, or null. Unused: actions belong to the project. */
    ownedId: string | null;
  }
  let { visible, root }: Props = $props();

  /**
   * The same card look the Context tab uses, so the tabs of the right column
   * read as one place: a sheet lifted off the panel's surface by a little light
   * rather than by an outline.
   */
  const cardClass = 'gap-2 bg-foreground/8';

  /** The dialog, referred to by the one method this panel calls on it. */
  let dialog = $state<{
    openFor: (action: StackDefinition | null, defaultFolder?: string) => void;
  } | null>(null);
  let dialogOpen = $state(false);

  /** The action whose removal is being confirmed, or `null`. */
  let removing = $state<StackDefinition | null>(null);
  const removeOpen = $derived(removing !== null);

  /** The last lines each running action printed, by terminal id. */
  let tails = $state<Record<string, string[]>>({});

  // Keep default folder aligned with the active session root.
  $effect(() => {
    setStackInput({ activeRoot: root.trim() || null });
  });

  // Restore saved actions and run records from `.NET` SQLite preferences as soon
  // as `preferencesStore` loads or updates.
  $effect(() => {
    if (preferencesStore.loaded) {
      void preferencesStore.values;
      syncRunPreferences();
    }
  });

  // Reconcile running/exited `.NET` terminals once whenever the Run tab becomes visible.
  $effect(() => {
    if (visible && preferencesStore.loaded) {
      void refreshStacks();
    }
  });

  const rows = $derived(allStackRows());
  const running = $derived(rows.filter((row) => row.state === 'running' || row.state === 'starting'));

  /** The folder a new action starts in: the active session's working folder. */
  const folder = $derived((stacks.activeRoot ?? '').trim());

  /**
   * The terminals to follow, as one string, so the watch below is rebuilt when
   * the set of running actions changes and NOT on unrelated state updates.
   */
  const watchedSessions = $derived(
    running
      .map((row) => row.ownedId)
      .filter((id): id is string => id !== null)
      .join(' ')
  );

  /**
   * Follow the output of everything that is running while the tab is on screen.
   * Switching away from the tab stops every `/socket/terminal` subscription.
   */
  $effect(() => {
    const sessions = watchedSessions.split(' ').filter((id) => id !== '');
    if (!visible || sessions.length === 0) return;
    let watch: RunOutputWatch | null = null;
    const owner = { active: true };
    void startRunOutputWatch(owner, sessions, (handle) => {
      watch = handle;
    });
    return () => {
      owner.active = false;
      watch?.stop();
      for (const id of sessions) {
        delete tails[id];
      }
    };
  });

  async function startRunOutputWatch(
    owner: { active: boolean },
    sessions: string[],
    rememberWatch: (handle: RunOutputWatch) => void
  ): Promise<void> {
    try {
      const handle = await watchRunOutput(sessions, {
        onTail: (sessionOwnedId, tail) => {
          if (owner.active && sessions.includes(sessionOwnedId)) tails[sessionOwnedId] = tail;
        },
        onExit: (sessionOwnedId, exitCode) => {
          if (owner.active) noteRunTerminalExit(sessionOwnedId, exitCode);
        }
      });
      rememberWatch(handle);
      if (!owner.active) handle.stop();
    } catch {
      // The watch is opportunistic; run state is still rendered from stacks.
    }
  }

  async function run(definition: StackDefinition): Promise<void> {
    if (definition.cwd.trim().length === 0) return;
    await startStack(definition.id);
  }

  async function restart(row: StackRow): Promise<void> {
    if (row.definition.cwd.trim().length === 0) return;
    await restartStack(row.definition.id);
  }

  async function confirmRemoval(): Promise<void> {
    if (!removing) return;
    await removeStack(removing.id);
    removing = null;
  }
</script>

<svelte:window onkeydown={handleRunKeydown} />

<div class="flex h-full min-h-0 flex-col">
  <PanelHeader title="Run" count={rows.length}>
    <span class="font-medium text-foreground">Run</span>
    {#snippet actions()}
      <IconButton
        label={stacks.loading ? 'Reading running processes…' : 'Refresh running processes'}
        disabled={stacks.loading}
        onclick={() => void refreshStacks()}
      >
        {#if stacks.loading}<WorkingSpinner />{:else}<RefreshCw />{/if}
      </IconButton>
    {/snippet}
  </PanelHeader>

  {#if stacks.notice}
    <p class="flex items-start gap-2 border-b px-3 py-2 text-sm leading-snug text-[var(--color-attention)]">
      <span class="min-w-0 flex-1">{stacks.notice}</span>
      <Button variant="ghost" size="xs" class="shrink-0 text-sm" onclick={clearStackNotice}>
        Dismiss
      </Button>
    </p>
  {/if}

  {#if stacks.error}
    <p class="flex items-start gap-2 border-b px-3 py-2 text-sm leading-snug text-destructive">
      <span class="min-w-0 flex-1">{stacks.error}</span>
      <Button variant="ghost" size="xs" class="shrink-0 text-sm" onclick={() => void refreshStacks()}>
        Try again
      </Button>
    </p>
  {/if}

  {#if stacks.unavailableReason}
    <p class="border-b px-3 py-2 text-sm leading-snug text-muted-foreground">
      {stacks.unavailableReason}
    </p>
  {/if}

  <ScrollArea class="min-h-0 flex-1">
    {#if rows.length === 0}
      <EmptyState
        title="No actions saved yet"
        body={stacks.projectName
          ? `An action is one command you keep starting in ${stacks.projectName} — the dev server, the database, the watcher. Save it once and it is a click or a shortcut away.`
          : 'An action is one command you keep starting — the dev server, the database, the watcher. Save it once and it is a click or a shortcut away.'}
      >
        {#snippet icon()}<Play />{/snippet}
        {#snippet actions()}
          <Button size="sm" class="text-[13px]" onclick={() => dialog?.openFor(null, folder)}>
            <Plus class="size-3.5" aria-hidden="true" />
            Add action
          </Button>
        {/snippet}
      </EmptyState>
    {:else}
      <div class="flex flex-col gap-2 p-3">
        <Card.Root size="sm" class={cardClass}>
          <Card.Header hasAction class="items-center">
            <Card.Title class="text-[13px] leading-tight font-semibold">Actions</Card.Title>
            <Card.Action>
              <Button
                variant="ghost"
                size="xs"
                class="text-[13px]"
                onclick={() => dialog?.openFor(null, folder)}
              >
                <Plus class="size-3.5" aria-hidden="true" />
                Add action
              </Button>
            </Card.Action>
          </Card.Header>
          <Card.Content class="px-1">
            <ul class="m-0 flex list-none flex-col gap-0.5 p-0">
              {#each rows as row (row.definition.id)}
                <li>
                  <RunActionRow
                    {row}
                    canStart={row.definition.cwd.trim().length > 0}
                    busy={isStackBusy(row.definition.id)}
                    onRun={() => void run(row.definition)}
                    onStop={() => void stopStack(row.definition.id)}
                    onEdit={() => dialog?.openFor(row.definition)}
                    onRemove={() => (removing = row.definition)}
                    onOpenPreview={() => openPreviewUrl((row.definition.previewUrl ?? '').trim())}
                  />
                </li>
              {/each}
            </ul>
          </Card.Content>
        </Card.Root>

        {#if running.length > 0}
          <Card.Root size="sm" class={cardClass}>
            <Card.Header>
              <Card.Title class="text-[13px] leading-tight font-semibold">Running</Card.Title>
            </Card.Header>
            <Card.Content class="flex flex-col gap-2">
              {#each running as row (row.definition.id)}
                <RunningProcessRow
                  {row}
                  canRestart={row.definition.cwd.trim().length > 0}
                  tail={row.ownedId ? (tails[row.ownedId] ?? []) : []}
                  busy={isStackBusy(row.definition.id)}
                  onStop={() => void stopStack(row.definition.id)}
                  onRestart={() => void restart(row)}
                  onOpenSession={() => {}}
                />
              {/each}
            </Card.Content>
          </Card.Root>
        {/if}
      </div>
    {/if}
  </ScrollArea>
</div>

<AddActionDialog bind:this={dialog} bind:open={dialogOpen} />

<AlertDialog.Root
  open={removeOpen}
  onOpenChange={(next) => {
    if (!next) removing = null;
  }}
>
  <AlertDialog.Content
    class="rounded-lg bg-background text-foreground ring-border shadow-[var(--shadow-lg)]"
  >
    <AlertDialog.Header>
      <AlertDialog.Title class="text-[14px] leading-[1.4] font-semibold">
        Forget “{removing?.name ?? ''}”?
      </AlertDialog.Title>
      <AlertDialog.Description class="text-[13px] leading-[1.5] text-muted-foreground">
        This forgets the saved command and nothing else. If it is running right now, the terminal
        keeps running and stays in your session list — you would stop it there.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer class="bg-transparent">
      <AlertDialog.Cancel size="sm" class="text-[13px]">Keep it</AlertDialog.Cancel>
      <AlertDialog.Action size="sm" variant="destructive" class="text-[13px]" onclick={confirmRemoval}>
        Forget it
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
