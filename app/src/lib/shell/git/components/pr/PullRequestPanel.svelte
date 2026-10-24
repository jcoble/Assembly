<script lang="ts">
  import { onMount } from 'svelte';

  import Check from '@lucide/svelte/icons/check';
  import ExternalLink from '@lucide/svelte/icons/external-link';
  import GitBranch from '@lucide/svelte/icons/git-branch';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import Sparkles from '@lucide/svelte/icons/sparkles';
  import X from '@lucide/svelte/icons/x';

  import { buttonVariants } from '$lib/components/ui/button/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import {
    createPullRequestFromTauri,
    generatePullRequestDetailsFromTauri,
    listGitBranchesFromTauri,
    readPullRequestContextFromTauri,
    readPullRequestStatusFromTauri,
    type ProjectGitStatus,
    type PullRequestContext
  } from '$lib/tauriSource';
  import { canChangeRepository, READ_ONLY_IN_BROWSER_MESSAGE } from '$lib/shell/git/gitBackendExtra';
  import {
    beginPullRequestGeneration,
    beginPullRequestPush,
    canCreatePullRequest,
    createPullRequestFlowModel,
    failPullRequest,
    finishPullRequestCreation,
    finishPullRequestGeneration,
    noAgentMessage,
    pullRequestActionLabel,
    setPullRequestChecks,
    setPullRequestContext,
    type PullRequestFlowModel
  } from './prFlow';
  import { cn } from '$lib/utils';
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';

  interface AgentIdentity {
    ownedId: string;
    generation: number;
  }

  interface Props {
    root: string | null;
    status: ProjectGitStatus | null;
    activeAgent: AgentIdentity | null;
    canWrite?: boolean;
    onClose?: () => void;
  }

  let {
    root,
    status,
    activeAgent,
    canWrite = canChangeRepository(),
    onClose
  }: Props = $props();

  let model = $state<PullRequestFlowModel>(createPullRequestFlowModel());
  let context = $state<PullRequestContext | null>(null);
  /** The branches this pull request could be opened against. Read once, on open. */
  let baseChoices = $state<string[]>([]);
  let baseMenuChoices = $state<string[]>([]);
  let baseMenuValue = $state('');
  let disposed = false;

  const busy = $derived(model.state === 'generating' || model.state === 'pushing');
  const canCreate = $derived(canCreatePullRequest(model, canWrite));

  onMount(() => {
    void initialize();
    return () => {
      disposed = true;
    };
  });

  async function initialize(): Promise<void> {
    if (!root) {
      model = failPullRequest(model, 'No repository is selected.');
      return;
    }
    try {
      const loaded = await readPullRequestContextFromTauri(root);
      if (!loaded) throw new Error(READ_ONLY_IN_BROWSER_MESSAGE);
      context = loaded;
      model = setPullRequestContext(model, loaded);
      void loadBaseChoices(loaded.base);
      await generateDetails();
    } catch (error) {
      model = failPullRequest(model, error);
    }
  }

  /**
   * The base is picked from the repository's own branches rather than typed.
   * A native `<select>` cannot be styled and comes out as the OS control, so
   * the kit's menu does the job — see the kit's DESIGN.md.
   */
  async function loadBaseChoices(base: string): Promise<void> {
    if (!root) return;
    try {
      const list = await listGitBranchesFromTauri(root);
      const names = (list?.branches ?? []).map((branch) => branch.name);
      baseChoices = names.includes(base) ? names : [base, ...names];
    } catch {
      // A branch list that cannot be read is not worth an error here: the base
      // git worked out is already in the model, and it stays the only choice.
      baseChoices = [base];
    }
  }

  async function generateDetails(): Promise<void> {
    if (!root) return;
    if (!activeAgent) {
      model = failPullRequest(model, noAgentMessage());
      return;
    }
    model = beginPullRequestGeneration(model);
    try {
      const details = await generatePullRequestDetailsFromTauri({
        root,
        ownedId: activeAgent.ownedId,
        generation: activeAgent.generation
      });
      if (!details) throw new Error(READ_ONLY_IN_BROWSER_MESSAGE);
      model = finishPullRequestGeneration(model, details);
    } catch (error) {
      model = failPullRequest(model, error);
    }
  }

  async function createPullRequest(): Promise<void> {
    if (!root || !canCreate) return;
    model = beginPullRequestPush(model);
    try {
      const created = await createPullRequestFromTauri({
        root,
        title: model.title,
        description: model.description,
        base: model.base,
        draft: model.draft
      });
      if (!created) throw new Error(READ_ONLY_IN_BROWSER_MESSAGE);
      model = finishPullRequestCreation(model, created);
      await pollStatus();
    } catch (error) {
      model = failPullRequest(model, error);
    }
  }

  async function pollStatus(): Promise<void> {
    if (disposed || !root || !model.branch) return;
    try {
      const statusResult = await readPullRequestStatusFromTauri(root, model.branch);
      if (!statusResult) throw new Error(READ_ONLY_IN_BROWSER_MESSAGE);
      model = setPullRequestChecks(model, statusResult);
    } catch (error) {
      // A created PR remains useful even while checks are temporarily
      // unavailable; show the exact gh reason without losing the URL.
      model = { ...model, error: error instanceof Error ? error.message : String(error) };
    }
  }

  function updateTitle(event: Event): void {
    model.title = (event.currentTarget as HTMLInputElement).value;
  }

  function updateDescription(event: Event): void {
    model.description = (event.currentTarget as HTMLTextAreaElement).value;
  }

  function updateBase(base: string): void {
    model.base = base;
  }

  function snapshotBaseMenu(open: boolean): void {
    if (!open) return;
    baseMenuValue = model.base;
    baseMenuChoices = baseChoices.length > 0 ? [...baseChoices] : [model.base];
  }

  function toggleDraft(event: Event): void {
    model.draft = (event.currentTarget as HTMLInputElement).checked;
  }

  const panelButton =
    'inline-flex items-center justify-center gap-1.5 rounded-[7px] px-2.5 py-1.5 text-[12px] ' +
    'border-0 font-medium transition-colors focus-visible:ring-3 focus-visible:ring-ring/50 outline-none ' +
    'disabled:pointer-events-none disabled:opacity-50';
</script>

<section class="flex max-h-[min(620px,72vh)] min-h-0 shrink-0 flex-col border-b border-[var(--color-border)] bg-[var(--color-surface)]" data-testid="pr-panel" aria-label="New pull request">
  <header class="flex shrink-0 items-center gap-2 border-b border-[var(--color-border)]/70 px-3 py-2">
    <GitBranch class="size-4 shrink-0 text-[var(--color-accent)]" aria-hidden="true" />
    <div class="min-w-0 flex-1">
      <h2 class="truncate text-[14px] font-semibold">New pull request</h2>
      <p class="truncate text-[12px] text-[var(--color-text-2)]">Let the active agent prepare the handoff.</p>
    </div>
    <button
      type="button"
      class={buttonVariants({ variant: 'ghost', size: 'icon-xs' })}
      aria-label="Close new pull request"
      title="Close new pull request"
      onclick={() => onClose?.()}
    >
      <X class="size-3.5" aria-hidden="true" />
    </button>
  </header>

  <div class="min-h-0 overflow-y-auto px-3 py-2.5">
    <div class="flex items-center gap-2 text-[12px]" data-testid="pr-branch-base-row">
      <span class="min-w-0 flex-1 truncate rounded-[7px] bg-[var(--color-bg)] px-2.5 py-2 font-mono text-[var(--color-text)]" title={model.branch}>
        {model.branch || status?.branch || 'current branch'}
      </span>
      <span class="shrink-0 text-[var(--color-text-3)]" aria-hidden="true">→</span>
      <DropdownMenu.Root onOpenChange={snapshotBaseMenu}>
        <DropdownMenu.Trigger
          class="min-w-0 flex-1 truncate rounded-[7px] bg-[var(--color-bg)] px-2.5 py-2 text-left font-mono text-[13px] text-[var(--color-text)] outline-none focus-visible:ring-3 focus-visible:ring-ring/50 disabled:opacity-60"
          disabled={busy || model.state === 'created'}
          aria-label="Base branch"
          title="Which branch this pull request merges into"
          data-testid="pr-base-select"
        >
          {model.base}
        </DropdownMenu.Trigger>
        <DropdownMenu.Content class="max-h-[240px] w-[240px] overflow-y-auto" align="end">
          {#each baseMenuChoices.length > 0 ? baseMenuChoices : [baseMenuValue] as choice (choice)}
            <DropdownMenu.Item onSelect={() => updateBase(choice)}>
              <span class="min-w-0 truncate font-mono">{choice}</span>
            </DropdownMenu.Item>
          {/each}
        </DropdownMenu.Content>
      </DropdownMenu.Root>
    </div>

    {#if model.state === 'generating'}
      <div class="mt-2 flex items-center gap-2 rounded-[7px] bg-[var(--color-elevated)] px-2.5 py-2 text-[12px] text-[var(--color-text-2)]" data-testid="pr-generating-state" aria-live="polite">
        <WorkingSpinner size={14} />
        <span>Generating title &amp; description...</span>
      </div>
    {/if}

    <div class="mt-2 flex flex-col gap-2">
      <label class="block">
        <span class="mb-1 block text-[11px] font-medium uppercase tracking-[0.08em] text-[var(--color-text-3)]">Title</span>
        <input
          class="w-full rounded-[7px] border border-transparent bg-[var(--color-bg)] px-2.5 py-2 text-[13px] text-[var(--color-text)] outline-none placeholder:text-[var(--color-text-3)] focus-visible:border-(color:--focus-border) disabled:opacity-60"
          value={model.title}
          oninput={updateTitle}
          placeholder="Pull request title"
          disabled={busy || model.state === 'created'}
          data-testid="pr-title-input"
        />
      </label>
      <label class="block">
        <span class="mb-1 block text-[11px] font-medium uppercase tracking-[0.08em] text-[var(--color-text-3)]">Description</span>
        <textarea
          class="min-h-[104px] w-full resize-y rounded-[7px] border border-transparent bg-[var(--color-bg)] px-2.5 py-2 text-[13px] leading-[18px] text-[var(--color-text)] outline-none placeholder:text-[var(--color-text-3)] focus-visible:border-(color:--focus-border) disabled:opacity-60"
          value={model.description}
          oninput={updateDescription}
          placeholder="What changed and how it was checked"
          disabled={busy || model.state === 'created'}
          data-testid="pr-description-input"
        ></textarea>
      </label>
    </div>

    <label class="mt-2 flex items-center gap-2 rounded-[7px] bg-[var(--color-bg)] px-2.5 py-2 text-[12px] text-[var(--color-text-2)]">
      <input type="checkbox" checked={model.draft} onchange={toggleDraft} disabled={busy || model.state === 'created'} data-testid="pr-draft-checkbox" />
      <span>Create as draft</span>
    </label>

    {#if context?.commits}
      <p class="mt-2 truncate text-[11px] text-[var(--color-text-3)]" title={context.commits}>Based on {context.commits.split('\n').length} branch commit{context.commits.includes('\n') ? 's' : ''}.</p>
    {/if}

    {#if model.state !== 'created'}
      <div class="mt-2 flex gap-2">
        <button
          type="button"
          class={cn(panelButton, 'bg-[var(--color-elevated)] text-[var(--color-text-2)] hover:bg-[var(--color-hover)] hover:text-[var(--color-text)]')}
          disabled={busy || !activeAgent || !root}
          title={!activeAgent ? noAgentMessage() : 'Ask the active agent to regenerate the title and description'}
          onclick={() => void generateDetails()}
          data-testid="pr-regenerate"
        >
          <Sparkles class="size-3.5" aria-hidden="true" />
          Regenerate
        </button>
        <button
          type="button"
          class={cn(panelButton, 'flex-1 bg-[var(--color-accent)] text-[var(--color-on-accent)] hover:brightness-110')}
          disabled={!canCreate || busy}
          title={!canWrite ? READ_ONLY_IN_BROWSER_MESSAGE : canCreate ? 'Push this branch and create the pull request' : 'Generate a title and description first'}
          onclick={() => void createPullRequest()}
          data-testid="pr-create"
        >
          {#if busy}<WorkingSpinner size={14} />{/if}
          {pullRequestActionLabel(model)}
        </button>
      </div>
    {:else}
      <div class="mt-2 flex items-center gap-2 rounded-[7px] bg-[var(--color-live-bg)] px-2.5 py-2 text-[12px]" data-testid="pr-status-card">
        <Check class="size-3.5 shrink-0 text-[var(--color-good)]" aria-hidden="true" />
        <div class="min-w-0 flex-1">
          <p class="font-medium text-[var(--color-text)]">PR #{model.number ?? '—'} created</p>
          <p class="truncate text-[var(--color-text-2)]">{model.checkSummary || 'Checking status…'}</p>
        </div>
        {#if model.url}
          <a class={buttonVariants({ variant: 'ghost', size: 'icon-xs' })} href={model.url} target="_blank" rel="noreferrer" aria-label="Open pull request" title={model.url}>
            <ExternalLink class="size-3.5" aria-hidden="true" />
          </a>
        {/if}
        <button type="button" class={buttonVariants({ variant: 'ghost', size: 'icon-xs' })} onclick={() => void pollStatus()} title="Refresh pull request checks" aria-label="Refresh pull request checks">
          <RefreshCw class="size-3.5" aria-hidden="true" />
        </button>
      </div>
    {/if}

    {#if model.error}
      <p class="mt-2 rounded-[7px] bg-[var(--color-bad-bg)] px-2.5 py-2 text-[12px] leading-[16px] text-[var(--color-bad)]" role="alert" data-testid="pr-error">{model.error}</p>
    {/if}
  </div>
</section>
