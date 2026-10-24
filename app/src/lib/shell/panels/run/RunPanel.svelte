<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  /**
   * RunPanel.svelte — the Run tab of the right column.
   *
   * The project's saved actions: the commands you keep starting, each with the
   * shortcut that starts it, the page it serves, and what it is doing right
   * now. It replaced the play button that used to sit in the top bar, so this
   * panel and the shortcuts are the only two ways an action runs.
   *
   * WHAT IT OWNS AND WHAT IT ASKS FOR. Nothing here spawns or kills a process.
   * The page owns the session rail and the terminals, so starting and stopping
   * go through `stackService`, which calls the handlers the page registered.
   * Reading which ports are open is the service's job too. The one thing this
   * panel does reach out for itself is the output tail under a running action,
   * and that is a service as well (`runOutputService`).
   *
   * NOTHING POLLS, and nothing runs at mount. The panel is mounted for the whole
   * session and parked off-screen; the page tells the service when the tab is
   * shown. The output watch is opened when the tab is on screen and closed the
   * moment it is not, so a panel nobody is looking at costs nothing.
   *
   * The word "stack" in the imports is the old internal name for a run action;
   * renaming it would rename the keys people's saved actions are filed under.
   * See the note at the top of `stackStore.svelte.ts`.
   */
  import Plus from '@lucide/svelte/icons/plus';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import Play from '@lucide/svelte/icons/play';

  import * as AlertDialog from '$lib/components/ui/alert-dialog/index.js';
  import { Button } from '$lib/components/ui/button/index.js';
  import * as Card from '$lib/components/ui/card/index.js';
  import { EmptyState } from '$lib/components/ui/empty-state/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { PanelHeader } from '$lib/components/ui/panel-header/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import { watchRunOutput, type RunOutputWatch } from '$lib/shell/stacks/runOutputService';
  import {
    isStackBusy,
    refreshStacks,
    selectStackSession,
    startStack,
    stopStack
  } from '$lib/shell/stacks/stackService';
  import {
    clearStackNotice,
    removeStack,
    allStackRows,
    hydrateStacks,
    stacks,
    type StackDefinition,
    type StackRow
  } from '$lib/shell/stacks/stackStore.svelte';
  import { openUrlInBrowser } from '$lib/shell/workbenchNavigation';

  import AddActionDialog from './AddActionDialog.svelte';
  import RunActionRow from './RunActionRow.svelte';
  import RunningProcessRow from './RunningProcessRow.svelte';
  import { matchKeybinding } from './keybindingCapture.ts';

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
   * The same card look the Context tab uses, so the two tabs of the right
   * column read as one place: a sheet lifted off the panel's surface by a
   * little light rather than by an outline.
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

  /** The last lines each running action printed, by session id. */
  let tails = $state<Record<string, string[]>>({});

  // Read what is saved before showing anything. The store was only read once a
  // session had been picked, so opening this tab first showed "No actions
  // saved yet" over a full list on disk.
  void hydrateSavedStacks();

  async function hydrateSavedStacks(): Promise<void> {
    try {
      await hydrateStacks();
    } catch (error) {
      stacks.error = `Could not load run configurations: ${error instanceof Error ? error.message : String(error)}`;
    }
  }

  // Every saved action, whichever folder it belongs to. The list was filtered
  // to the active session's working folder, which is not a project root — a
  // session sitting one directory down hid every action saved at the top, and
  // the count read 0 over a list that had just been added to. It "saved once
  // in a while": only when the folder typed happened to match the session's.
  const rows = $derived(allStackRows());
  const running = $derived(rows.filter((row) => row.state === 'running' || row.state === 'starting'));

  /** The folder a new action starts in: the project the shell is on. */
  const folder = $derived((stacks.activeRoot ?? '').trim());
  const rootAvailable = $derived(root.trim().length > 0);

  /**
   * The sessions to follow, as one string, so the watch below is rebuilt when
   * the set of running actions changes and NOT every time a port is re-read.
   */
  const watchedSessions = $derived(
    running
      .map((row) => row.ownedId)
      .filter((id): id is string => id !== null)
      .join(' ')
  );

  /**
   * Which actions a shortcut may start: this project's first, then the rest.
   * A shortcut is a property of the action, not of the panel, so it keeps
   * working while another tab is in front — but when two projects have given
   * the same combination away, the project on screen is the one meant.
   */
  const shortcutOrder = $derived([
    ...rows.map((row) => row.definition),
    ...stacks.definitions.filter(
      (definition) => !rows.some((row) => row.definition.id === definition.id)
    )
  ]);

  /**
   * Follow the output of everything that is running, while the tab is on
   * screen. Closing the tab stops the subscription — that is the whole of the
   * lifecycle, and there is no timer in it.
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
      // Drop the tails this watch collected. Without this, the tail of every
      // session the tab has ever watched stays in the record for as long as the
      // tab is open. The next watch repopulates whatever is still running, and
      // deleting by the captured id list — rather than reading `tails` — keeps
      // this cleanup from touching state the effect would then depend on.
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
      const handle = await watchRunOutput(sessions, (sessionOwnedId, tail) => {
        if (owner.active && sessions.includes(sessionOwnedId)) tails[sessionOwnedId] = tail;
      });
      rememberWatch(handle);
      if (!owner.active) handle.stop();
    } catch (_error) {
      // The watch is opportunistic; run state is still rendered from stacks.
    }
  }

  /** Start an action, and show its page when it was set up to do that. */
  async function run(definition: StackDefinition): Promise<void> {
    if (!rootAvailable) return;
    await startStack(definition.id);
    const url = (definition.previewUrl ?? '').trim();
    if (definition.openPreviewOnRun === true && url) await openUrlInBrowser({ url });
  }

  /** Stop an action, then start it again with whatever it is saved as now. */
  async function restart(row: StackRow): Promise<void> {
    await stopStack(row.definition.id);
    await run(row.definition);
  }

  async function confirmRemoval(): Promise<void> {
    if (!removing) return;
    await removeStack(removing.id);
    removing = null;
  }

  /** True when the keypress belongs to something the reader is typing into. */
  function isTyping(target: EventTarget | null): boolean {
    if (!(target instanceof HTMLElement)) return false;
    if (target.isContentEditable) return true;
    const tag = target.tagName;
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || tag === 'BUTTON';
  }

  /**
   * A saved shortcut starts its action from anywhere in the app — except while
   * the reader is typing, where the combination belongs to what they are typing
   * in. The capture field in the dialog is a button and swallows its own keys,
   * so it can never reach this.
   */
  function onKeydown(event: KeyboardEvent): void {
    if (!rootAvailable || event.defaultPrevented || isTyping(event.target)) return;
    const matched = matchKeybinding(event, shortcutOrder);
    if (!matched) return;
    event.preventDefault();
    void run(matched);
  }
</script>

<svelte:window onkeydown={onKeydown} />

<div class="flex h-full min-h-0 flex-col">
  <PanelHeader title="Run" count={rows.length}>
    {#snippet actions()}
      <IconButton
        label={stacks.loading ? 'Reading which ports are open…' : 'Look again at which ports are open'}
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
                    canStart={rootAvailable}
                    busy={isStackBusy(row.definition.id)}
                    onRun={() => void run(row.definition)}
                    onStop={() => void stopStack(row.definition.id)}
                    onEdit={() => dialog?.openFor(row.definition)}
                    onRemove={() => (removing = row.definition)}
                    onOpenPreview={() =>
                      void openUrlInBrowser({ url: (row.definition.previewUrl ?? '').trim() })}
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
                  canRestart={rootAvailable}
                  tail={row.ownedId ? (tails[row.ownedId] ?? []) : []}
                  busy={isStackBusy(row.definition.id)}
                  onStop={() => void stopStack(row.definition.id)}
                  onRestart={() => void restart(row)}
                  onOpenSession={() => {
                    if (row.ownedId) void selectStackSession(row.ownedId);
                  }}
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
