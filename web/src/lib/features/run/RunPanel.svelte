<!--
  RunPanel.svelte — the Run tab of the right panel.

  1:1 port from `mac-command-bar/tauri-svelte-preview/src/lib/shell/panels/run/RunPanel.svelte`:
  - Saved project run configurations persisted via `.NET` `preferences` (`preferencesStore`).
  - Process lifecycle via `.NET` `/socket/terminal` (`terminal/start`, `list`, `read`, `close`).
  - Zero polling at rest: `/socket/terminal` output topic opens only while visible with running
    actions, and unsubscribes immediately on tab switch or panel close.
-->
<script lang="ts">
  import Play from '@lucide/svelte/icons/play';
  import Plus from '@lucide/svelte/icons/plus';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';

  import { connectionStore } from '$lib/application/connection.svelte';
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
  import { normalizePreviewUrl } from './model';
  import { runStore } from './store.svelte';
  import type { RunDefinition, RunRow } from './types';

  interface Props {
    /** True while the Run panel is visible on screen. */
    visible: boolean;
    /** Opens a configured preview URL without disrupting native GPUIKit Chat. */
    onOpenPreview?: (url: string) => void;
    /** Opens the Terminal view when clicking a running process title. */
    onOpenTerminal?: (terminalId: string) => void;
  }
  let { visible, onOpenPreview, onOpenTerminal }: Props = $props();

  const cardClass = 'gap-2 bg-foreground/8';

  let dialog = $state<{
    openFor: (action: RunDefinition | null, defaultFolder?: string) => void;
  } | null>(null);
  let dialogOpen = $state(false);

  let removing = $state<RunDefinition | null>(null);
  const removeOpen = $derived(removing !== null);

  /** Latest bounded output tail per active `.NET` terminal id. */
  let tails = $state<Record<string, string[]>>({});

  // Reconcile live `.NET` terminals once when the Run tab opens and backend is connected.
  $effect(() => {
    if (!visible || connectionStore.status !== 'connected' || !preferencesStore.loaded) return;
    const controller = new AbortController();
    void runStore.refresh(controller.signal);
    return () => {
      controller.abort();
    };
  });

  const rows = $derived(runStore.rows);
  const running = $derived(runStore.running);
  const folder = $derived((runStore.activeRoot ?? '').trim());
  const backendConnected = $derived(connectionStore.status === 'connected');

  /** Active terminal IDs joined as one string so output watching only restarts when the set changes. */
  const watchedTerminals = $derived(
    running
      .map((row) => row.ownedId)
      .filter((id): id is string => id !== null)
      .join(' ')
  );

  // Follow live output on `/socket/terminal` ONLY while visible and at least one action is running.
  $effect(() => {
    const terminalIds = watchedTerminals.split(' ').filter((id) => id !== '');
    if (!visible || !backendConnected || terminalIds.length === 0) return;

    const watch = watchRunOutput(terminalIds, {
      onTail: (terminalId, tail) => {
        if (terminalIds.includes(terminalId)) {
          tails[terminalId] = tail;
        }
      },
      onExit: (terminalId, exitCode) => {
        void runStore.noteTerminalExit(terminalId, exitCode);
      },
      onMissing: (terminalId) => {
        void runStore.noteTerminalMissing(terminalId);
      }
    });

    return () => {
      watch.stop();
      for (const id of terminalIds) {
        delete tails[id];
      }
    };
  });

  function openPreview(url: string | undefined): void {
    const safeUrl = normalizePreviewUrl(url);
    if (!safeUrl) return;
    onOpenPreview?.(safeUrl);
  }

  async function run(definition: RunDefinition): Promise<void> {
    if (!backendConnected || !definition.cwd.trim()) return;
    await runStore.launch(definition.id, onOpenPreview);
  }

  async function restart(row: RunRow): Promise<void> {
    if (!backendConnected || !row.definition.cwd.trim()) return;
    await runStore.restart(row.definition.id, onOpenPreview);
  }

  async function confirmRemoval(): Promise<void> {
    if (!removing) return;
    await runStore.remove(removing.id);
    removing = null;
  }
</script>

<div class="flex h-full min-h-0 flex-col">
  <PanelHeader title="Run" count={rows.length}>
    Run
    {#snippet actions()}
      <IconButton
        label={runStore.loading ? 'Refreshing running actions…' : 'Refresh running actions'}
        disabled={runStore.loading || !backendConnected}
        onclick={() => void runStore.refresh()}
      >
        {#if runStore.loading}<WorkingSpinner size={14} />{:else}<RefreshCw />{/if}
      </IconButton>
    {/snippet}
  </PanelHeader>

  {#if runStore.notice}
    <p
      class="flex items-start gap-2 border-b px-3 py-2 text-xs leading-snug text-[var(--color-attention)]"
    >
      <span class="min-w-0 flex-1">{runStore.notice}</span>
      <Button
        variant="ghost"
        size="xs"
        class="shrink-0 text-xs"
        onclick={() => runStore.clearNotice()}
      >
        Dismiss
      </Button>
    </p>
  {/if}

  {#if runStore.error}
    <p class="flex items-start gap-2 border-b px-3 py-2 text-xs leading-snug text-destructive">
      <span class="min-w-0 flex-1">{runStore.error}</span>
      <Button
        variant="ghost"
        size="xs"
        class="shrink-0 text-xs"
        onclick={() => void runStore.refresh()}
      >
        Try again
      </Button>
    </p>
  {/if}

  {#if !backendConnected}
    <p class="border-b px-3 py-2 text-xs leading-snug text-muted-foreground">
      Waiting for the Assembly backend connection…
    </p>
  {/if}

  <ScrollArea class="min-h-0 flex-1">
    {#if rows.length === 0}
      <EmptyState
        title="No actions saved yet"
        body={runStore.projectName
          ? `An action is one command you keep starting in ${runStore.projectName} — the dev server, the database, the watcher. Save it once and it is a click or a shortcut away.`
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
                    canStart={backendConnected && row.definition.cwd.trim().length > 0}
                    busy={runStore.isBusy(row.definition.id)}
                    onRun={() => void run(row.definition)}
                    onStop={() => void runStore.stop(row.definition.id)}
                    onEdit={() => dialog?.openFor(row.definition)}
                    onRemove={() => (removing = row.definition)}
                    onOpenPreview={() => openPreview(row.definition.previewUrl)}
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
                  canRestart={backendConnected && row.definition.cwd.trim().length > 0}
                  tail={row.ownedId ? (tails[row.ownedId] ?? []) : []}
                  busy={runStore.isBusy(row.definition.id)}
                  onStop={() => void runStore.stop(row.definition.id)}
                  onRestart={() => void restart(row)}
                  onOpenSession={() => {
                    if (row.ownedId) onOpenTerminal?.(row.ownedId);
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
    data-occludes-chat=""
    class="rounded-lg bg-background text-foreground ring-border shadow-[var(--shadow-lg)]"
  >
    <AlertDialog.Header>
      <AlertDialog.Title class="text-[14px] leading-[1.4] font-semibold">
        Forget “{removing?.name ?? ''}”?
      </AlertDialog.Title>
      <AlertDialog.Description class="text-[13px] leading-[1.5] text-muted-foreground">
        This forgets the saved command and closes its terminal if it is still open.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer class="bg-transparent">
      <AlertDialog.Cancel size="sm" class="text-[13px]">Keep it</AlertDialog.Cancel>
      <AlertDialog.Action
        size="sm"
        variant="destructive"
        class="text-[13px]"
        onclick={() => void confirmRemoval()}
      >
        Forget it
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
