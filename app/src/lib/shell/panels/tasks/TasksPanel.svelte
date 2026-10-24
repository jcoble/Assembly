<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import ChevronDown from '@lucide/svelte/icons/chevron-down';
  import ExternalLink from '@lucide/svelte/icons/external-link';
  import FileText from '@lucide/svelte/icons/file-text';
  import List from '@lucide/svelte/icons/list';
  import ListTodo from '@lucide/svelte/icons/list-todo';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import Search from '@lucide/svelte/icons/search';
  import Settings2 from '@lucide/svelte/icons/settings-2';
  import { onDestroy, onMount, untrack } from 'svelte';

  import { Button } from '$lib/components/ui/button/index.js';
  import { buttonVariants } from '$lib/components/ui/button/index.js';
  import { Chip } from '$lib/components/ui/chip/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { EmptyState } from '$lib/components/ui/empty-state/index.js';
  import { FilterPills } from '$lib/components/ui/filter-pills/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { ScrollArea } from '$lib/components/ui/scroll-area/index.js';
  import * as Select from '$lib/components/ui/select/index.js';
  import {
    clearNotionTaskSettings,
    listNotionTasks,
    readNotionTaskSettings,
    refreshNotionTasks,
    saveNotionTaskSettings,
    splitTaskTitle,
    taskPriorityTone,
    taskStatusTone,
    type NotionTaskRow,
    type NotionTaskSettings
  } from '$lib/shell/notionTasks.ts';
  import { connectNotion } from '$lib/shell/notionOAuth.ts';
  import { requestOpenConversationFile } from '$lib/shell/openFileBus.ts';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte.ts';
  import { registerNotionTaskNavigation } from '$lib/shell/workbenchNavigation.ts';
  import { matchNotionProject } from '$lib/shell/notionProjectMatch.ts';
  import { readAssemblySettingFromTauri, writeAssemblySettingFromTauri } from '$lib/tauriSource.ts';
  import { cn } from '$lib/utils.js';
  import NotionTaskViewer from './NotionTaskViewer.svelte';
  import { normalizeTasksView, projectToShow, TASKS_VIEW_SETTING_KEY } from './tasksView.ts';

  const PAGE_SIZE = 25;
  const TASK_ROW_HEIGHT = 64;
  const TASK_ROW_OVERSCAN = 5;

  interface Props {
    /** The active session's folder; the list follows the Notion project it maps to. */
    root?: string;
    /** The active session; a file link on a task page opens against its folder. */
    ownedId?: string | null;
  }
  let { root = '', ownedId = null }: Props = $props();

  let settings = $state<NotionTaskSettings | null>(null);
  let tasks = $state<NotionTaskRow[]>([]);
  let projects = $state<string[]>([]);
  let statuses = $state<string[]>([]);
  let priorities = $state<string[]>([]);
  /** Selected pill values per group id: `status` and `priority`. Empty means unfiltered. */
  let filters = $state<Record<string, string[]>>({ status: [], priority: [] });
  let searchDraft = $state('');
  let search = $state('');
  let projectFilter = $state('');
  let sortBy = $state('taskNumber');
  let sortDirection = $state('desc');
  /** Until the saved view is read, saving would overwrite it with these defaults. */
  let viewRestored = false;
  let loading = $state(true);
  let refreshing = $state(false);
  let saving = $state(false);
  let error = $state('');
  let hasMore = $state(false);
  let showSetup = $state(false);
  let dataSourceId = $state('');
  let token = $state('');
  let oauthController: AbortController | null = null;
  let selectedTask = $state<NotionTaskRow | null>(null);
  let taskViewport = $state<HTMLElement | null>(null);
  let taskLoadSentinel = $state<HTMLElement | null>(null);
  let taskScrollTop = $state(0);
  let taskViewportHeight = $state(800);
  let generation = 0;
  let readGeneration = 0;

  const firstTaskIndex = $derived(
    Math.max(0, Math.floor(taskScrollTop / TASK_ROW_HEIGHT) - TASK_ROW_OVERSCAN)
  );
  const lastTaskIndex = $derived(
    Math.min(
      tasks.length,
      Math.ceil((taskScrollTop + taskViewportHeight) / TASK_ROW_HEIGHT) + TASK_ROW_OVERSCAN
    )
  );
  const visibleTasks = $derived(tasks.slice(firstTaskIndex, lastTaskIndex));
  const sessionProject = $derived(matchNotionProject(root, projects));
  const filterGroups = $derived(
    [
      { id: 'status', label: 'Status', options: statuses.map((value) => ({ value, label: value })) },
      { id: 'priority', label: 'Priority', options: priorities.map((value) => ({ value, label: value })) }
    ].filter((group) => group.options.length > 0)
  );
  const filtered = $derived(
    Boolean(search || projectFilter || filters.status?.length || filters.priority?.length)
  );

  async function loadCached(owner: number, append = false): Promise<void> {
    const readOwner = append ? readGeneration : ++readGeneration;
    const offset = append ? tasks.length : 0;
    const page = await listNotionTasks(
      offset,
      PAGE_SIZE,
      search,
      projectFilter,
      filters.status ?? [],
      filters.priority ?? [],
      sortBy,
      sortDirection
    );
    if (owner !== generation || readOwner !== readGeneration) return;
    if (!append) {
      // A value the snapshot no longer offers has no pill to show or clear it, so it stops filtering.
      const kept = {
        status: page.statuses.filter((value) => filters.status?.includes(value)),
        priority: page.priorities.filter((value) => filters.priority?.includes(value))
      };
      if (
        kept.status.length !== (filters.status?.length ?? 0) ||
        kept.priority.length !== (filters.priority?.length ?? 0)
      ) {
        setFilters(kept);
        await loadCached(owner);
        return;
      }
    }
    tasks = append ? [...tasks, ...page.tasks] : page.tasks;
    projects = page.projects;
    statuses = page.statuses;
    priorities = page.priorities;
    hasMore = page.hasMore;
  }

  /** The one view every session shares, with the search as typed, submitted or not. */
  function saveView(): void {
    if (!viewRestored) return;
    void writeAssemblySettingFromTauri(TASKS_VIEW_SETTING_KEY, {
      search: searchDraft.trim(),
      filters: { status: [...(filters.status ?? [])], priority: [...(filters.priority ?? [])] },
      projectFilter,
      sortBy,
      sortDirection
    });
  }

  async function reloadCached(): Promise<void> {
    saveView();
    const owner = generation;
    loading = true;
    error = '';
    try {
      await loadCached(owner);
    } catch (cause) {
      if (owner === generation) error = cause instanceof Error ? cause.message : String(cause);
    } finally {
      if (owner === generation) loading = false;
    }
  }

  /** A new selection starts the list again from its first row. */
  function setFilters(next: Record<string, string[]>): void {
    filters = next;
    if (taskViewport) taskViewport.scrollTop = 0;
  }

  function applySearch(event: SubmitEvent): void {
    event.preventDefault();
    search = searchDraft.trim();
    void reloadCached();
  }

  function directionLabel(direction: string): string {
    if (sortBy === 'taskNumber') return direction === 'asc' ? 'Lowest first' : 'Highest first';
    return direction === 'asc' ? 'A to Z' : 'Z to A';
  }

  async function refresh(owner = generation): Promise<void> {
    refreshing = true;
    error = '';
    try {
      await refreshNotionTasks();
      if (owner !== generation) return;
      await loadCached(owner);
    } catch (cause) {
      if (owner === generation) error = cause instanceof Error ? cause.message : String(cause);
    } finally {
      if (owner === generation) refreshing = false;
    }
  }

  async function initialize(owner: number): Promise<void> {
    try {
      const saved = normalizeTasksView(await readAssemblySettingFromTauri(TASKS_VIEW_SETTING_KEY));
      if (owner !== generation) return;
      if (saved) {
        filters = saved.filters;
        searchDraft = search = saved.search;
        projectFilter = projectToShow(sessionProject, saved.projectFilter);
        sortBy = saved.sortBy;
        sortDirection = saved.sortDirection;
      }
      viewRestored = true;
      settings = await readNotionTaskSettings();
      if (owner !== generation) return;
      dataSourceId = settings.dataSourceId;
      showSetup = !settings.dataSourceId || !settings.hasToken;
      await loadCached(owner);
      if (!showSetup) await refresh(owner);
    } catch (cause) {
      if (owner === generation) error = cause instanceof Error ? cause.message : String(cause);
    } finally {
      if (owner === generation) loading = false;
    }
  }

  async function saveSetup(): Promise<void> {
    const owner = generation;
    saving = true;
    error = '';
    try {
      settings = await saveNotionTaskSettings(dataSourceId, token);
      if (owner !== generation) return;
      token = '';
      showSetup = false;
      await refresh(owner);
    } catch (cause) {
      if (owner === generation) error = cause instanceof Error ? cause.message : String(cause);
    } finally {
      if (owner === generation) saving = false;
    }
  }

  async function connectWorkspace(): Promise<void> {
    oauthController?.abort();
    const controller = new AbortController();
    oauthController = controller;
    saving = true;
    error = '';
    try {
      settings = await connectNotion(controller.signal);
      if (controller.signal.aborted || oauthController !== controller) return;
      dataSourceId = settings.dataSourceId;
      showSetup = false;
      await refresh(generation);
    } catch (cause) {
      if (!controller.signal.aborted && oauthController === controller) {
        error = cause instanceof Error ? cause.message : String(cause);
      }
    } finally {
      if (oauthController === controller) {
        oauthController = null;
        saving = false;
      }
    }
  }

  async function openNotionUrl(url: string): Promise<void> {
    error = '';
    try {
      const { openUrl } = await import('@tauri-apps/plugin-opener');
      await openUrl(url);
    } catch (cause) {
      error = cause instanceof Error ? cause.message : String(cause);
    }
  }

  async function disconnect(): Promise<void> {
    const owner = generation;
    saving = true;
    error = '';
    try {
      await clearNotionTaskSettings();
      if (owner !== generation) return;
      settings = { dataSourceId: '', hasToken: false };
      dataSourceId = '';
      token = '';
      showSetup = true;
    } catch (cause) {
      if (owner === generation) error = cause instanceof Error ? cause.message : String(cause);
    } finally {
      if (owner === generation) saving = false;
    }
  }

  async function loadMore(): Promise<void> {
    if (loading || !hasMore) return;
    const owner = generation;
    loading = true;
    try {
      await loadCached(owner, true);
    } catch (cause) {
      if (owner === generation) error = cause instanceof Error ? cause.message : String(cause);
    } finally {
      if (owner === generation) loading = false;
    }
  }

  onMount(() => {
    const owner = ++generation;
    void initialize(owner);
    return registerNotionTaskNavigation((linked) => (selectedTask = linked));
  });

  /** A file link on the task page opens in the editor against this session's folder. */
  function openFileLink(reference: string): void {
    const active = rail.owned.find((session) => session.ownedId === ownedId);
    error = active ? requestOpenConversationFile(active, reference) ?? '' : 'That file link could not be opened.';
  }
  $effect(() => {
    const next = sessionProject;
    untrack(() => {
      const shown = projectToShow(next, projectFilter);
      if (shown === projectFilter) return;
      projectFilter = shown;
      void reloadCached();
    });
  });
  $effect(() => {
    const viewport = taskViewport;
    if (!viewport) return;
    const onScroll = (): void => {
      taskScrollTop = viewport.scrollTop;
      taskViewportHeight = viewport.clientHeight;
    };
    onScroll();
    viewport.addEventListener('scroll', onScroll, { passive: true });
    // The panel can mount while it is still opening and has no height yet.
    const resize = new ResizeObserver(onScroll);
    resize.observe(viewport);
    return () => {
      viewport.removeEventListener('scroll', onScroll);
      resize.disconnect();
    };
  });
  $effect(() => {
    const root = taskViewport;
    const target = taskLoadSentinel;
    if (!root || !target || !hasMore) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) void loadMore();
      },
      { root, rootMargin: `${TASK_ROW_HEIGHT * 3}px 0px` }
    );
    observer.observe(target);
    return () => observer.disconnect();
  });
  onDestroy(() => {
    saveView();
    oauthController?.abort();
    oauthController = null;
    generation += 1;
    readGeneration += 1;
    tasks = [];
    projects = [];
    statuses = [];
    priorities = [];
    token = '';
    selectedTask = null;
    taskViewport = null;
    taskLoadSentinel = null;
  });
</script>

<div class="relative h-full min-h-0 overflow-hidden">
<section
  class="absolute inset-0 flex min-h-0 flex-col transition-transform duration-200 motion-reduce:transition-none {selectedTask ? '-translate-x-full' : 'translate-x-0'}"
  aria-hidden={selectedTask !== null}
  inert={selectedTask !== null}
>
  <!-- The Codex file-pane header: a row of pill controls, then the search. -->
  <header class="flex flex-none flex-col gap-2 px-2 pt-1.5 pb-2" aria-label="Tasks">
    <div class="flex min-w-0 items-center gap-1">
      <DropdownMenu.Root>
        <DropdownMenu.Trigger
          class={cn(
            buttonVariants({ variant: 'secondary', size: 'sm' }),
            'h-7 min-w-0 rounded-full px-3 text-[13px] [&_svg]:size-[16px]',
            projectFilter && 'text-[var(--color-accent)]'
          )}
          aria-label="View task options"
        >
          <List aria-hidden="true" />
          <span class="truncate">{projectFilter || 'All projects'}</span>
          <ChevronDown aria-hidden="true" />
        </DropdownMenu.Trigger>
        <DropdownMenu.Content
          align="start"
          sideOffset={7}
          class="flex w-[296px]! flex-col gap-[var(--space-3)] p-[var(--space-4)] text-foreground"
        >
          <div class="flex min-h-8 items-center justify-between gap-3">
            <span class="text-[13px] text-foreground">Sort</span>
            <Select.Root
              type="single"
              value={sortBy}
              onValueChange={(value) => {
                sortBy = value;
                void reloadCached();
              }}
            >
              <Select.Trigger size="sm" class="min-w-[132px]" aria-label="Sort tasks">
                {sortBy === 'taskNumber' ? 'Task number' : 'Title'}
              </Select.Trigger>
              <Select.Content>
                <Select.Item value="taskNumber" label="Task number" />
                <Select.Item value="title" label="Title" />
              </Select.Content>
            </Select.Root>
          </div>
          <div class="flex min-h-8 items-center justify-between gap-3">
            <span class="text-[13px] text-foreground">Direction</span>
            <Select.Root
              type="single"
              value={sortDirection}
              onValueChange={(value) => {
                sortDirection = value;
                void reloadCached();
              }}
            >
              <Select.Trigger size="sm" class="min-w-[132px]" aria-label="Sort direction">
                {directionLabel(sortDirection)}
              </Select.Trigger>
              <Select.Content>
                <Select.Item value="asc" label={directionLabel('asc')} />
                <Select.Item value="desc" label={directionLabel('desc')} />
              </Select.Content>
            </Select.Root>
          </div>
          <div class="flex min-h-8 items-center justify-between gap-3">
            <span class="text-[13px] text-foreground">Project</span>
            <Select.Root
              type="single"
              value={projectFilter || 'all'}
              onValueChange={(value) => {
                projectFilter = value === 'all' ? '' : value;
                void reloadCached();
              }}
            >
              <Select.Trigger size="sm" class="min-w-[132px]" aria-label="Filter tasks by project">
                {projectFilter || 'All projects'}
              </Select.Trigger>
              <Select.Content>
                <Select.Item value="all" label="All projects" />
                {#each projects as project}<Select.Item value={project} label={project} />{/each}
              </Select.Content>
            </Select.Root>
          </div>
        </DropdownMenu.Content>
      </DropdownMenu.Root>
      <span class="flex-1"></span>
      <IconButton class="size-7" label="Task settings" side="bottom" onclick={() => (showSetup = !showSetup)}>
        <Settings2 class="size-[16px]" aria-hidden="true" />
      </IconButton>
      <IconButton
        class="size-7"
        label="Refresh tasks"
        side="bottom"
        disabled={refreshing || showSetup}
        onclick={() => void refresh()}
      >
        <RefreshCw class="size-[16px]" aria-hidden="true" />
      </IconButton>
    </div>
    <form class="relative" onsubmit={applySearch}>
      <Search class="pointer-events-none absolute top-1/2 left-3 size-[16px] -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
      <Input bind:value={searchDraft} class="h-8 rounded-full border-transparent bg-muted dark:bg-muted pr-12 pl-9 text-[13px]" placeholder="Search tasks" aria-label="Search tasks" />
      <span class="pointer-events-none absolute top-1/2 right-3 -translate-y-1/2 text-[12px] text-muted-foreground tabular-nums">{tasks.length}</span>
    </form>
  </header>

  {#if filterGroups.length > 0}
    <div class="tasks-pills px-2 pb-(--space-2)">
      <FilterPills
        label="Filter tasks"
        groups={filterGroups}
        value={filters}
        onChange={(next) => {
          setFilters(next);
          void reloadCached();
        }}
      />
    </div>
  {/if}

  {#if projects.length > 0 && root && !sessionProject && !projectFilter}
    <p class="mx-2 mb-2 text-xs text-muted-foreground">No Notion project matches this session. Showing all projects.</p>
  {/if}

  {#if showSetup}
    <div class="mx-2 mb-3 grid gap-2 rounded-lg border border-border bg-card p-3">
      <p class="text-sm font-medium text-foreground">Notion workspace</p>
      <p class="text-xs leading-snug text-muted-foreground">
        Sign in through your default browser, choose the pages Assembly can read, then return here.
      </p>
      <Button disabled={saving} onclick={() => void connectWorkspace()}>
        {saving ? 'Waiting for Notion…' : 'Connect Notion'}
        {#if !saving}<ExternalLink class="size-3.5" />{/if}
      </Button>
      <details class="grid gap-2 text-xs text-muted-foreground">
        <summary class="cursor-pointer select-none py-1">Developer token setup</summary>
        <div class="grid gap-2 pt-1">
          <p class="leading-snug">
            For local development only. Production users will not need a token or data source ID.
          </p>
          <Button
            variant="secondary"
            onclick={() => void openNotionUrl('https://developers.notion.com/guides/get-started/personal-access-tokens#create-a-pat')}
          >
            Open developer token setup <ExternalLink class="size-3.5" />
          </Button>
          <Input bind:value={token} type="password" placeholder={settings?.hasToken ? 'Token already stored' : 'Paste developer token'} aria-label="Notion developer token" />
          <Input bind:value={dataSourceId} placeholder="Optional data source ID" aria-label="Notion data source ID" />
          <div class="flex justify-end gap-2">
            {#if settings?.hasToken}
              <Button variant="ghost" disabled={saving} onclick={() => void disconnect()}>Disconnect</Button>
            {/if}
            <Button disabled={saving || (!settings?.hasToken && !token.trim())} onclick={() => void saveSetup()}>
              {#if saving}<WorkingSpinner size={14} />Connecting…{:else}Connect developer token{/if}
            </Button>
          </div>
        </div>
      </details>
    </div>
  {/if}

  {#if error}
    <p class="mx-2 mb-2 rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-xs text-destructive">{error}</p>
  {/if}

  <ScrollArea class="min-h-0 flex-1" bind:viewportRef={taskViewport}>
    {#if loading && tasks.length === 0}
      <EmptyState title="Loading tasks…" body="Reading the last successful local snapshot.">
        {#snippet icon()}<WorkingSpinner size={24} />{/snippet}
      </EmptyState>
    {:else if tasks.length === 0}
      <EmptyState
        title={showSetup
          ? 'Connect Notion to see tasks'
          : filtered
            ? 'No matching tasks'
            : 'No tasks in the snapshot'}
        body={showSetup
          ? 'Configuration stays local to this Mac.'
          : filtered
            ? 'Try a different search or filter.'
            : 'Refresh to ask Notion for the latest task list.'}
      >
        {#snippet icon()}<ListTodo />{/snippet}
      </EmptyState>
    {:else}
      <div class="relative mx-2" style={`height: ${tasks.length * TASK_ROW_HEIGHT}px`}>
        {#each visibleTasks as task, index (task.sourceTaskId)}
          {@const title = splitTaskTitle(task.title)}
          {@const statusTone = taskStatusTone(task.status)}
          <button
            type="button"
            class="absolute inset-x-0 grid h-[60px] w-full grid-cols-[16px_minmax(0,1fr)] items-center gap-(--space-3) rounded-(--radius-md) px-(--space-2) text-left hover:bg-[var(--menu-row-hover)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            style={`transform: translateY(${(firstTaskIndex + index) * TASK_ROW_HEIGHT}px)`}
            onclick={() => (selectedTask = task)}
          >
            <FileText
              class="size-[16px] stroke-[1.8]"
              style={`color: var(--color-${statusTone === 'neutral' ? 'idle' : statusTone})`}
              aria-hidden="true"
            />
            <span class="min-w-0">
              <span class="line-clamp-2 text-[13px] leading-[18px] font-medium text-foreground">{title.text}</span>
              <span class="mt-0.5 flex min-w-0 items-center gap-1.5 overflow-hidden text-[12px] leading-[16px] text-muted-foreground">
                {#if title.key}<span class="shrink-0 tabular-nums">{title.key}</span>{/if}
                <Chip tone={statusTone} class="text-[12px]">{task.status || 'Unspecified'}</Chip>
                {#if task.priority}<Chip tone={taskPriorityTone(task.priority)} class="text-[12px]">{task.priority}</Chip>{/if}
                {#if !projectFilter}<span class="min-w-0 truncate">{task.project || 'Unspecified project'}</span>{/if}
              </span>
            </span>
          </button>
        {/each}
        <span bind:this={taskLoadSentinel} class="absolute right-0 bottom-0 size-px" aria-hidden="true"></span>
      </div>
    {/if}
  </ScrollArea>
</section>

<section
  class="absolute inset-0 transition-transform duration-200 motion-reduce:transition-none {selectedTask ? 'translate-x-0' : 'translate-x-full'}"
  aria-hidden={selectedTask === null}
>
  {#if selectedTask}
    {#key selectedTask.sourceTaskId}
      <NotionTaskViewer
        task={selectedTask}
        {statuses}
        {priorities}
        onBack={() => (selectedTask = null)}
        onOpenExternal={(url) => void openNotionUrl(url)}
        onFileLink={openFileLink}
        onTaskUpdated={async (changes) => {
          selectedTask = selectedTask ? { ...selectedTask, ...changes } : null;
          await reloadCached();
        }}
      />
    {/key}
  {/if}
</section>
</div>

<style>
  /* This panel sits on the elevated card, the same layer the kit's secondary and
     accent slots name, so a pill filled with either would vanish into it. Both
     controls take the shell's pill surface instead, one step above the card. */
  .tasks-pills {
    --secondary: var(--pill-surface);
    --accent: var(--pill-surface-hover);
  }
  /* An open Status group is wider than the panel. The row's own scrollbar
     must not exist: WebKitGTK otherwise hit-tests its lower 20px as the
     scrollbar and the pills under it ignore clicks. */
  .tasks-pills :global([role='group']) {
    scrollbar-width: none;
  }
</style>
