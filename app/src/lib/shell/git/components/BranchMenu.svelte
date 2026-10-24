<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  /**
   * BranchMenu.svelte — which branch this repository is on, and the moves
   * between branches: switch, create, stash and pop.
   *
   * The trigger is the branch name itself, because that is what a person looks
   * at when they want to change it. It also carries the whole name in its hover
   * text: a branch called `codex/outbound-rule-generation` does not fit a narrow
   * panel, and two names cut off at the same point cannot be told apart.
   *
   * The cached branch and stash lists render as soon as the menu opens. A refresh
   * begins only after the menu has had a paint opportunity, so opening never
   * makes the WebView wait on Tauri or git.
   *
   * A `<select>` is deliberately not used here — see the kit's DESIGN.md. It
   * cannot be styled, it comes out as the OS control, and it cannot carry the
   * second line each branch row needs.
   */
  import Check from '@lucide/svelte/icons/check';
  import GitBranch from '@lucide/svelte/icons/git-branch';
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import Archive from '@lucide/svelte/icons/archive';
  import ArchiveRestore from '@lucide/svelte/icons/archive-restore';

  import { buttonVariants } from '$lib/components/ui/button/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import type { GitPanelState } from '$lib/shell/git/gitPanelStore.svelte';
  import type { GitService } from '$lib/shell/git/gitService';
  import type { GitBranchSummary, GitStashEntry } from '$lib/tauriSource';
  import { cn } from '$lib/utils';
  import { afterFloatingSurfacePaint } from '$lib/shell/floatingSurface.ts';

  import {
    branchNameProblem,
    branchWorktreeRoot,
    canCreateBranch,
    describeBranchRow,
    describeCurrentBranch,
    filterBranches
  } from './branchPicker';

  interface Props {
    panel: GitPanelState;
    service: GitService;
    /** False in a browser: it can read the repository but not change it. */
    canWrite: boolean;
    readOnlyReason: string;
    /** Moves the active session to another checkout; absent when it cannot move. */
    onUseSessionCheckout?(root: string): void | Promise<void>;
  }
  let { panel, service, canWrite, readOnlyReason, onUseSessionCheckout }: Props = $props();

  let open = $state(false);
  let branches = $state<GitBranchSummary[]>([]);
  let stashes = $state<GitStashEntry[]>([]);
  let listError = $state('');
  let loading = $state(false);
  let query = $state('');
  let newBranch = $state('');
  let menuBusy = $state(false);
  let menuHasChanges = $state(false);

  const busy = $derived(panel.actionBusy !== '');
  const current = $derived(describeCurrentBranch(panel.status?.branch));
  const ahead = $derived(panel.status?.ahead ?? 0);
  const behind = $derived(panel.status?.behind ?? 0);
  const shown = $derived(filterBranches(branches, query));
  const existingNames = $derived(branches.map((branch) => branch.name));
  const nameProblem = $derived(branchNameProblem(newBranch, existingNames));
  const canCreate = $derived(canCreateBranch(newBranch, existingNames, { canWrite, busy: menuBusy }));
  const hasChanges = $derived((panel.status?.files ?? []).length > 0);

  async function load(): Promise<void> {
    if (!panel.root) return;
    loading = true;
    listError = '';
    try {
      const [branchList, stashList] = await Promise.all([
        service.listBranches(),
        service.listStashes()
      ]);
      branches = branchList?.branches ?? [];
      stashes = stashList ?? [];
    } catch (error) {
      listError = error instanceof Error ? error.message : String(error);
    } finally {
      loading = false;
    }
  }

  function change(next: boolean): void {
    open = next;
    if (next) {
      query = '';
      newBranch = '';
      menuBusy = busy;
      menuHasChanges = hasChanges;
      loading = branches.length === 0 && stashes.length === 0;
      afterFloatingSurfacePaint(() => void load());
    }
  }

  /** A branch another worktree has moves the session there; git would refuse to check it out here. */
  async function switchTo(branch: GitBranchSummary): Promise<void> {
    open = false;
    const worktree = branchWorktreeRoot(branch);
    if (worktree && onUseSessionCheckout) await onUseSessionCheckout(worktree);
    else await service.switchBranch(branch.name);
  }

  async function create(): Promise<void> {
    if (!canCreate) return;
    const name = newBranch.trim();
    newBranch = '';
    open = false;
    await service.createBranch(name, true);
  }

  function createOnEnter(event: KeyboardEvent): void {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    void create();
  }

  async function stash(): Promise<void> {
    open = false;
    await service.stashChanges(true, '');
  }

  async function pop(index: number | null): Promise<void> {
    open = false;
    await service.popStash(index);
  }

  const HEADING =
    'px-2 pt-1.5 pb-0.5 text-[11px] font-medium uppercase tracking-[0.08em] text-[var(--color-text-3)]';
</script>

<DropdownMenu.Root {open} onOpenChange={change}>
  <DropdownMenu.Trigger
    class={cn(
      buttonVariants({ variant: 'ghost', size: 'xs' }),
      'min-w-0 max-w-full gap-1 px-1 text-[13px] font-normal text-[var(--color-text-2)]'
    )}
    disabled={!panel.activated}
    title={`Checked-out branch: ${current}\nSwitching here changes this working directory.${behind > 0 ? `\n${behind} commit${behind === 1 ? '' : 's'} behind its remote.` : ''}${ahead > 0 ? `\n${ahead} commit${ahead === 1 ? '' : 's'} ahead of its remote.` : ''}`}
    data-testid="branch-menu-trigger"
  >
    <GitBranch class="size-3 shrink-0" aria-hidden="true" />
    <span class="min-w-0 truncate">{current}</span>
    {#if behind > 0}
      <span class="shrink-0 text-[var(--color-attention)]" aria-label={`${behind} commit${behind === 1 ? '' : 's'} behind`}>↓{behind}</span>
    {/if}
    {#if ahead > 0}
      <span class="shrink-0 text-[var(--color-live)]" aria-label={`${ahead} commit${ahead === 1 ? '' : 's'} ahead`}>↑{ahead}</span>
    {/if}
    <ChevronDown class="size-3 shrink-0" aria-hidden="true" />
  </DropdownMenu.Trigger>

  <DropdownMenu.Content class="w-[280px] p-1" align="start" data-testid="branch-menu">
    <div class={HEADING}>Switch branch</div>
    <div class="px-1 pb-1">
      <Input
        class="h-7 text-[13px]"
        placeholder="Filter branches"
        autocomplete="off"
        spellcheck="false"
        bind:value={query}
        data-testid="branch-filter"
      />
    </div>

    <div class="max-h-[220px] overflow-y-auto">
      {#if loading}
        <p class="px-2 py-1.5 text-[13px] leading-[18px] text-[var(--color-text-2)]">
          <WorkingSpinner size={12} /> Reading branches…
        </p>
      {:else if listError}
        <p class="px-2 py-1.5 text-[13px] leading-[18px] text-[var(--color-bad)]" role="alert">
          {listError}
        </p>
      {:else if shown.length === 0}
        <p class="px-2 py-1.5 text-[13px] leading-[18px] text-[var(--color-text-2)]">
          {branches.length === 0 ? 'No local branches were found.' : 'No branch matches that.'}
        </p>
      {:else}
        {#each shown as branch (branch.name)}
          <DropdownMenu.Item
            class="flex-col items-start gap-0"
            disabled={!canWrite || menuBusy || branch.isCurrent}
            onSelect={() => void switchTo(branch)}
          >
            <span class="flex w-full min-w-0 items-center gap-1.5">
              {#if branch.isCurrent}
                <Check class="size-3 shrink-0 text-[var(--color-accent)]" aria-hidden="true" />
              {:else}
                <span class="size-3 shrink-0" aria-hidden="true"></span>
              {/if}
              <span class="min-w-0 truncate text-[13px]">{branch.name}</span>
            </span>
            <span class="w-full truncate pl-[18px] text-[12px] text-[var(--color-text-3)]">
              {describeBranchRow(branch)}
            </span>
          </DropdownMenu.Item>
        {/each}
      {/if}
    </div>

    <DropdownMenu.Separator />

    <div class={HEADING}>New branch</div>
    <div class="flex flex-col gap-1 px-1 pb-1">
      <Input
        class="h-7 text-[13px]"
        placeholder="feature/my-change"
        autocomplete="off"
        spellcheck="false"
        disabled={!canWrite || menuBusy}
        bind:value={newBranch}
        onkeydown={createOnEnter}
        data-testid="new-branch-name"
      />
      {#if nameProblem}
        <p class="text-[12px] leading-[16px] text-[var(--color-bad)]" role="alert">{nameProblem}</p>
      {/if}
      <button
        type="button"
        class={cn(buttonVariants({ variant: 'secondary', size: 'xs' }), 'w-full text-[12px]')}
        disabled={!canCreate}
        title={canWrite ? 'Create this branch and switch to it' : readOnlyReason}
        onclick={() => void create()}
        data-testid="create-branch"
      >
        Create and switch
      </button>
    </div>

    <DropdownMenu.Separator />

    <div class={HEADING}>Stash</div>
    <DropdownMenu.Item
      disabled={!canWrite || menuBusy || !menuHasChanges}
      onSelect={() => void stash()}
    >
      <Archive class="size-3.5 shrink-0" aria-hidden="true" />
      <span class="min-w-0 truncate">
        {menuHasChanges ? 'Stash all changes, including new files' : 'Nothing to stash'}
      </span>
    </DropdownMenu.Item>
    {#if stashes.length > 0}
      <DropdownMenu.Item
          disabled={!canWrite || menuBusy}
        onSelect={() => void pop(null)}
        data-testid="pop-latest-stash"
      >
        <ArchiveRestore class="size-3.5 shrink-0" aria-hidden="true" />
        <span class="min-w-0 truncate">Pop {stashes[0].description || stashes[0].label}</span>
      </DropdownMenu.Item>
      {#if stashes.length > 1}
        <p class="px-2 pb-1 text-[12px] leading-[16px] text-[var(--color-text-3)]">
          {stashes.length} stashes saved. Popping takes the most recent one.
        </p>
      {/if}
    {/if}
  </DropdownMenu.Content>
</DropdownMenu.Root>
