<script lang="ts">
  /**
   * The right panel's "Open another project" control: a popover listing every project on
   * this Mac and each connected machine (with their worktrees), and, while one is shown,
   * the bar that names it and leads back to the session. The page saves the choice.
   */
  import Cloud from '@lucide/svelte/icons/cloud';
  import FolderSearch from '@lucide/svelte/icons/folder-search';
  import Laptop from '@lucide/svelte/icons/laptop';
  import Lock from '@lucide/svelte/icons/lock';
  import { onDestroy } from 'svelte';

  import { Button, buttonVariants } from '$lib/components/ui/button/index.js';
  import * as Command from '$lib/components/ui/command/index.js';
  import * as Popover from '$lib/components/ui/popover/index.js';
  import { projectRegistry } from '$lib/shell/projects/projectRegistry.svelte.ts';
  import { middleTruncatePath, otherProjectGroups, projectMachineLabel } from '$lib/shell/projects/projects.ts';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte';
  import { listRepositoryCheckoutsFromTauri, type RepositoryCheckout } from '$lib/tauriSource';
  import { cn } from '$lib/utils';
  import { parseRemoteWorkspacePath } from '$lib/workspacePaths.ts';

  interface Props {
    sessionRoot: string;
    /** The other project's folder on screen, or null while the session's own is. */
    viewedRoot: string | null;
    readOnlyReason: string | null;
    onView(root: string | null): void;
    onNewSessionHere?(projectId: string, folder: string): void;
  }
  let { sessionRoot, viewedRoot, readOnlyReason, onView, onNewSessionHere }: Props = $props();

  let open = $state(false);
  let checkouts = $state.raw<Record<string, readonly RepositoryCheckout[]>>({});
  /** The worktree listing for the current picker open; closing, reopening or unmounting cancels it. */
  let loading: AbortController | null = null;
  onDestroy(() => loading?.abort());
  const groups = $derived(otherProjectGroups(projectRegistry.projects, rail.remoteProfiles, rail.remoteConnections, checkouts));
  const viewedOption = $derived(groups.flatMap((group) => group.options).find((option) => option.path === viewedRoot) ?? null);
  const viewedName = $derived(viewedOption?.label ?? viewedRoot?.replace(/\/+$/, '').split('/').at(-1) ?? '');
  const viewedMachine = $derived(projectMachineLabel(parseRemoteWorkspacePath(viewedRoot ?? '')?.profileId ?? 'local', rail.remoteProfiles));

  /** One worktree listing per machine; a machine that fails just shows its projects. */
  async function loadWorktrees(): Promise<void> {
    loading?.abort();
    const controller = new AbortController();
    loading = controller;
    const roots = otherProjectGroups(projectRegistry.projects, rail.remoteProfiles, rail.remoteConnections, {})
      .map((group) => group.options.map((option) => option.path));
    const answers = await Promise.allSettled(roots.map((machineRoots) => listRepositoryCheckoutsFromTauri(machineRoots, controller.signal)));
    if (controller.signal.aborted) return;
    checkouts = Object.assign({}, ...answers.map((answer) => (answer.status === 'fulfilled' ? answer.value ?? {} : {})));
  }

  function pick(path: string): void {
    open = false;
    onView(path === sessionRoot ? null : path);
  }
</script>

<div class={cn('mx-3 mt-1 mb-2 flex min-w-0 flex-none flex-col gap-1', viewedRoot && 'rounded-lg bg-secondary p-1')}>
  <div class="flex min-w-0 items-center gap-1">
    <Popover.Root bind:open onOpenChange={(next) => { if (next) void loadWorktrees(); else loading?.abort(); }}>
      <Popover.Trigger class={cn(buttonVariants({ variant: 'ghost', size: 'sm' }), 'min-w-0 shrink justify-start text-[13px]')}>
        <FolderSearch class="size-[14px] text-muted-foreground" aria-hidden="true" />
        <span class="min-w-0 truncate">{viewedRoot ? `Viewing ${viewedName} on ${viewedMachine}` : 'Open another project'}</span>
      </Popover.Trigger>
      <Popover.Content align="start" class="w-[480px] gap-0 p-0">
        <Popover.Header class="px-3 pt-3 pb-2">
          <Popover.Title class="text-[13px] font-medium">Open another project</Popover.Title>
          <Popover.Description class="text-[13px] text-muted-foreground">
            Look at or edit another project's files without starting a session. Git is read-only while
            another session works there, so its checkout is never disturbed.
          </Popover.Description>
        </Popover.Header>
        <Command.Root class="rounded-none! border-t">
          <Command.Input placeholder="Filter projects" />
          <Command.List class="max-h-[400px]">
            <Command.Empty>No project matches.</Command.Empty>
            {#each groups as group (group.machine)}
              <Command.Group heading={group.label}>
                {#each group.options as option (option.path)}
                  <Command.Item class="items-start" value={`${group.label} ${option.label} ${option.path}`} onSelect={() => pick(option.path)}>
                    {#if !option.worktree}{#if group.machine === 'local'}<Laptop class="mt-0.5" />{:else}<Cloud class="mt-0.5" />{/if}{/if}
                    <span class={cn('flex min-w-0 flex-1 flex-col', option.worktree && 'pl-6')}>
                      <span class="text-[13px] break-words">{option.label}</span>
                      <span class="truncate text-[13px] text-muted-foreground" title={option.folder}>{middleTruncatePath(option.detail, 46)}</span>
                    </span>
                  </Command.Item>
                {/each}
              </Command.Group>
            {/each}
          </Command.List>
        </Command.Root>
      </Popover.Content>
    </Popover.Root>
  </div>
  {#if viewedRoot && readOnlyReason}
    <p class="flex min-w-0 items-start gap-2 px-2.5 text-[13px] text-muted-foreground">
      <Lock class="mt-0.5 size-[14px] flex-none" aria-hidden="true" />
      <span class="min-w-0">{readOnlyReason}</span>
    </p>
  {/if}
  {#if viewedRoot}
    <div class="flex min-w-0 flex-wrap items-center justify-end gap-1">
      {#if readOnlyReason && onNewSessionHere && viewedOption}
        {@const here = viewedOption}
        <Button size="sm" variant="outline" class="text-[13px]" onclick={() => onNewSessionHere(here.projectId, here.folder)}>New session here</Button>
      {/if}
      <Button size="sm" variant="ghost" class="text-[13px]" onclick={() => onView(null)}>Back to session</Button>
    </div>
  {/if}
</div>
