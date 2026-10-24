<script lang="ts">
  /**
   * SessionsColumn.svelte — the /next shell's left column.
   *
   * The Working/Done/Settled list is owned by SessionRail. This wrapper keeps
   * the column-width strip and the header controls. Session rows are currently
   * inert while their selection lifecycle is rebuilt one responsibility at a time.
   */
  import Bot from '@lucide/svelte/icons/bot';
  import PanelLeftOpen from '@lucide/svelte/icons/panel-left-open';
  import Plus from '@lucide/svelte/icons/plus';
  import Search from '@lucide/svelte/icons/search';
  import ListFilter from '@lucide/svelte/icons/list-filter';
  import { onMount } from 'svelte';

  import { Button } from '$lib/components/ui/button/index.js';
  import { buttonVariants } from '$lib/components/ui/button/index.js';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu/index.js';
  import { FilterPills } from '$lib/components/ui/filter-pills/index.js';
  import * as Select from '$lib/components/ui/select/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import * as Tooltip from '$lib/components/ui/tooltip/index.js';
  import type { OwnedSession } from '$lib/shell/ownedSessions';
  import { AGENT_ICONS } from '$lib/shell/agentIcons';
  import { openSessionLibrary } from '$lib/shell/history/library/sessionLibraryNavigation';
  import SessionRail from './SessionRail.svelte';
  import { sessionLabel, stripCells } from '$lib/shell/sessionStrip';
  import {
    readAssemblySettingFromTauri,
    writeAssemblySettingFromTauri
  } from '$lib/tauriSource';
  import { cn } from '$lib/utils';
  import {
    DEFAULT_MY_WORK_VIEW_OPTIONS,
    MY_WORK_FILTER_GROUPS,
    matchesMyWorkFilters,
    normalizeMyWorkFilters,
    normalizeMyWorkViewOptions,
    type MyWorkFilters,
    type MyWorkSort,
    type MyWorkSortDirection,
    NATURAL_SORT_DIRECTION,
    myWorkSortDirectionLabel,
    type MyWorkViewOptions
  } from './myWorkViewOptions';

  interface Props {
    owned: OwnedSession[];
    activeOwnedId: string | null;
    collapsed: boolean;
    onNewSession(): void;
    onCollapse(collapsed: boolean): void;
    onSelectSession?(ownedId: string): void | Promise<void>;
    onComplete?(ownedId: string): void;
    onReopen?(ownedId: string): void;
    onSettle?(ownedId: string): void;
    onUnsettle?(ownedId: string): void;
    onPin?(ownedId: string, pinned: boolean): void;
    onMarkOrchestrator?(ownedId: string, marked: boolean): void;
    onRename?(ownedId: string, title: string): void;
    onConnect?(ownedId: string): void;
    onAskRemove?(ownedId: string): void;
  }

  let {
    owned,
    activeOwnedId,
    collapsed,
    onNewSession,
    onCollapse,
    onSelectSession,
    onComplete,
    onReopen,
    onSettle,
    onUnsettle,
    onPin,
    onMarkOrchestrator,
    onRename,
    onConnect,
    onAskRemove
  }: Props = $props();

  const cells = $derived(stripCells(owned, activeOwnedId));
  let viewOptions = $state<MyWorkViewOptions>({ ...DEFAULT_MY_WORK_VIEW_OPTIONS });
  const MY_WORK_VIEW_OPTIONS_SETTING_KEY = 'rail.my-work-view-options';
  let viewOptionsVersion = 0;

  /** Status and Project group independently; None turns both off. */
  const GROUPING_BUTTONS = [
    {
      label: 'None',
      pressed: () => !viewOptions.groupByStatus && !viewOptions.groupByProject,
      press: () => setViewOptions({ groupByStatus: false, groupByProject: false })
    },
    {
      label: 'Status',
      pressed: () => viewOptions.groupByStatus,
      press: () => setViewOptions({ groupByStatus: !viewOptions.groupByStatus })
    },
    {
      label: 'Project',
      pressed: () => viewOptions.groupByProject,
      press: () => setViewOptions({ groupByProject: !viewOptions.groupByProject })
    }
  ];

  /** The filter pills' selection, saved under its own key. */
  let filters = $state<MyWorkFilters>(normalizeMyWorkFilters(null));
  const MY_WORK_FILTERS_SETTING_KEY = 'rail.my-work-filters';
  let filtersVersion = 0;

  /** The order rows were dragged into, as owned ids; read by the Custom order sort. */
  let manualOrder = $state<string[]>([]);
  const MY_WORK_ORDER_SETTING_KEY = 'rail.session-order';
  let manualOrderVersion = 0;

  onMount(() => {
    const owner = { active: true };
    const restoreVersion = viewOptionsVersion;
    void restoreViewOptions(owner, restoreVersion);
    void restoreFilters(owner, filtersVersion);
    void restoreManualOrder(owner, manualOrderVersion);
    return () => {
      owner.active = false;
    };
  });

  async function restoreViewOptions(owner: { active: boolean }, restoreVersion: number): Promise<void> {
    try {
      const stored = await readAssemblySettingFromTauri(MY_WORK_VIEW_OPTIONS_SETTING_KEY);
      if (owner.active && viewOptionsVersion === restoreVersion) {
        viewOptions = normalizeMyWorkViewOptions(stored);
      }
    } catch {
      // View options fall back to defaults when local settings are unavailable.
    }
  }

  function setViewOptions(patch: Partial<MyWorkViewOptions>): void {
    viewOptionsVersion += 1;
    viewOptions = normalizeMyWorkViewOptions({ ...viewOptions, ...patch });
    void persistViewOptions(viewOptions, viewOptionsVersion);
  }

  async function persistViewOptions(options: MyWorkViewOptions, version: number): Promise<void> {
    try {
      await writeAssemblySettingFromTauri(MY_WORK_VIEW_OPTIONS_SETTING_KEY, options);
    } catch {
      if (version !== viewOptionsVersion) return;
    }
  }

  async function restoreFilters(owner: { active: boolean }, restoreVersion: number): Promise<void> {
    try {
      const stored = await readAssemblySettingFromTauri(MY_WORK_FILTERS_SETTING_KEY);
      if (owner.active && filtersVersion === restoreVersion) {
        filters = normalizeMyWorkFilters(stored);
      }
    } catch {
      // Filters fall back to none when local settings are unavailable.
    }
  }

  function setFilters(next: MyWorkFilters): void {
    filtersVersion += 1;
    filters = normalizeMyWorkFilters(next);
    void persistFilters(filters);
  }

  async function persistFilters(next: MyWorkFilters): Promise<void> {
    try {
      await writeAssemblySettingFromTauri(MY_WORK_FILTERS_SETTING_KEY, next);
    } catch {
      // The selection stays in memory when local settings are unavailable.
    }
  }

  async function restoreManualOrder(owner: { active: boolean }, restoreVersion: number): Promise<void> {
    try {
      const stored = await readAssemblySettingFromTauri(MY_WORK_ORDER_SETTING_KEY);
      if (owner.active && manualOrderVersion === restoreVersion && Array.isArray(stored)) {
        manualOrder = stored.filter((ownedId): ownedId is string => typeof ownedId === 'string');
      }
    } catch {
      // Custom order starts empty when local settings are unavailable.
    }
  }

  /** A drop saves the new order and switches the rail to it. */
  function reorder(order: string[]): void {
    manualOrderVersion += 1;
    manualOrder = order;
    if (viewOptions.sortBy !== 'manual') setViewOptions({ sortBy: 'manual' });
    void saveManualOrder(order);
  }

  async function saveManualOrder(order: string[]): Promise<void> {
    try {
      await writeAssemblySettingFromTauri(MY_WORK_ORDER_SETTING_KEY, order);
    } catch {
      // The order stays for this visit when local settings are unavailable.
    }
  }

  /** Searching sessions means the full Session History tab, not a rail popover. */
  export function openFinder(): void {
    openSessionLibrary();
  }

  /** The rail's own filter: a strip under the header, open only while in use. */
  let filterOpen = $state(false);
  let filterText = $state('');
  let filterInput = $state<HTMLInputElement | null>(null);

  const filtered = $derived.by(() => {
    const needle = filterText.trim().toLowerCase();
    const byPills = owned.filter((session) => matchesMyWorkFilters(session, filters));
    if (!needle) return byPills;
    return byPills.filter((session) => {
      const title = sessionLabel(session).toLowerCase();
      const project = session.projectGroupLabel.toLowerCase();
      return title.includes(needle) || project.includes(needle);
    });
  });

  function showFilter(): void {
    filterOpen = true;
    filterInput?.focus();
  }

  function hideFilter(): void {
    filterOpen = false;
    filterText = '';
  }

  async function selectFilteredSession(ownedId: string): Promise<void> {
    await onSelectSession?.(ownedId);
    hideFilter();
  }

  const ACTION_CLASS =
    'text-muted-foreground hover:text-foreground hover:bg-accent';
  const TOOLTIP_CLASS =
    'bg-[var(--color-surface)] text-foreground ring-1 ring-[var(--color-border)] ' +
    'shadow-[var(--shadow-md)] text-[12px] px-2 py-1';
  const TOOLTIP_ARROW_CLASS = 'bg-[var(--color-surface)] fill-[var(--color-surface)]';
</script>

<!-- Every icon-only control in this column is the same kit button at the same
     size, so they all say what they do on hover and light up the same way. -->
{#snippet action(tip: string, Icon: typeof Bot, run: () => void, extraClass = '')}
  <IconButton
    label={tip}
    size="sm"
    side="bottom"
    class={cn(ACTION_CLASS, extraClass)}
    onclick={(event: MouseEvent) => {
      event.stopPropagation();
      run();
    }}
  >
    <Icon class="size-[16px]" aria-hidden="true" />
  </IconButton>
{/snippet}

  {#if collapsed}
    <div class="flex h-full w-full flex-col items-center gap-1 overflow-hidden py-2">
      <Tooltip.Provider delayDuration={0}>
        {@render action('Open the sessions column', PanelLeftOpen, () => onCollapse(false))}
      </Tooltip.Provider>

      <div class="mt-1 flex min-h-0 w-full flex-1 flex-col items-center gap-1 overflow-y-auto">
        {#each cells as cell (cell.ownedId)}
          {@const CellIcon = AGENT_ICONS[cell.agent]}
          <div
            class={cn(
              'relative flex size-9 shrink-0 items-center justify-center rounded-md border',
              'border-transparent text-[var(--color-text-2)]',
              cell.done && 'opacity-55',
              cell.active &&
                'border-primary/45 bg-[var(--color-elevated)] text-foreground opacity-100'
            )}
            aria-label={cell.label}
          >
            <CellIcon class="size-4" aria-hidden="true" />
            <span class="state-dot absolute right-1 bottom-1" data-state={cell.state} aria-hidden="true"></span>
          </div>
        {/each}
      </div>
    </div>
  {:else}
    <div data-testid="sessions-column" class="sessions-column flex h-full min-h-0 flex-col text-[var(--color-text)]">
      <Tooltip.Provider delayDuration={0}>
        <header class="sessions-header">
        <h2 class="text-[16px] font-bold text-foreground">Sessions</h2>
        <div class="header-actions ml-auto flex items-center gap-2">
          <IconButton
            label="Search sessions"
            size="sm"
            side="bottom"
            class={ACTION_CLASS}
            onclick={showFilter}
          >
            <Search class="size-[16px]" aria-hidden="true" />
          </IconButton>
          <DropdownMenu.Root>
            <Tooltip.Root>
              <Tooltip.Trigger>
                {#snippet child({ props })}
                  <DropdownMenu.Trigger
                    {...props}
                    data-testid="my-work-view-options-trigger"
                    class={cn(buttonVariants({ variant: 'ghost', size: 'icon-sm' }), ACTION_CLASS)}
                    aria-label="View session options"
                  >
                    <ListFilter aria-hidden="true" />
                  </DropdownMenu.Trigger>
                {/snippet}
              </Tooltip.Trigger>
              <Tooltip.Content side="bottom" class={TOOLTIP_CLASS} arrowClasses={TOOLTIP_ARROW_CLASS}>
                View session options
              </Tooltip.Content>
            </Tooltip.Root>

            <!-- The sheet keeps the shared menu surface rather than the popover
                 colour it used to name: that colour is the same grey the rail
                 behind it is painted in, so an open menu vanished into the
                 column and only its hairline gave it away. Padding and the gaps
                 between rows come from the shell's spacing steps. -->
            <DropdownMenu.Content
              align="end"
              sideOffset={7}
              class="flex w-[304px]! flex-col gap-[var(--space-3)] p-[var(--space-4)] text-foreground"
            >
              <div class="flex flex-col gap-[var(--space-2)]">
                <span class="text-[13px] text-foreground">Group by</span>
                <div class="grid grid-cols-3 gap-1" role="group" aria-label="Group My Work sessions">
                  {#each GROUPING_BUTTONS as grouping (grouping.label)}
                    {@const pressed = grouping.pressed()}
                    <Button
                      size="sm"
                      variant="ghost"
                      class={cn(
                        'min-w-0 px-1 text-[13px] font-normal',
                        pressed
                          ? 'bg-secondary text-foreground hover:bg-secondary'
                          : 'text-[var(--color-text-3)]'
                      )}
                      aria-pressed={pressed}
                      style={pressed ? undefined : 'color: var(--color-text)'}
                      onclick={grouping.press}
                    >
                      <span class="truncate">{grouping.label}</span>
                    </Button>
                  {/each}
                </div>
              </div>

              <div class="flex min-h-8 items-center justify-between gap-3">
                <span class="text-[13px] text-foreground">Sort</span>
                <Select.Root
                  type="single"
                  value={viewOptions.sortBy}
                  onValueChange={(value) => setViewOptions({ sortBy: value as MyWorkSort })}
                >
                  <Select.Trigger size="sm" class="min-w-[132px]" aria-label="Sort My Work sessions">
                    {{ recent: 'Recent activity', name: 'Name', manual: 'Custom order' }[viewOptions.sortBy]}
                  </Select.Trigger>
                  <Select.Content>
                    <Select.Item value="recent" label="Recent activity" />
                    <Select.Item value="name" label="Name" />
                    <Select.Item value="manual" label="Custom order" />
                  </Select.Content>
                </Select.Root>
              </div>

              <!-- Custom order is the dragged order; it has no direction to turn. -->
              {#if viewOptions.sortBy !== 'manual'}
              <div class="flex min-h-8 items-center justify-between gap-3">
                <span class="text-[13px] text-foreground">Direction</span>
                <Select.Root
                  type="single"
                  value={viewOptions.sortDirection}
                  onValueChange={(value) =>
                    setViewOptions({ sortDirection: value as MyWorkSortDirection })}
                >
                  <Select.Trigger size="sm" class="min-w-[132px]" aria-label="Sort direction">
                    {myWorkSortDirectionLabel(viewOptions.sortBy, viewOptions.sortDirection)}
                  </Select.Trigger>
                  <Select.Content>
                    <Select.Item
                      value={NATURAL_SORT_DIRECTION[viewOptions.sortBy]}
                      label={myWorkSortDirectionLabel(
                        viewOptions.sortBy,
                        NATURAL_SORT_DIRECTION[viewOptions.sortBy]
                      )}
                    />
                    <Select.Item
                      value={NATURAL_SORT_DIRECTION[viewOptions.sortBy] === 'desc' ? 'asc' : 'desc'}
                      label={myWorkSortDirectionLabel(
                        viewOptions.sortBy,
                        NATURAL_SORT_DIRECTION[viewOptions.sortBy] === 'desc' ? 'asc' : 'desc'
                      )}
                    />
                  </Select.Content>
                </Select.Root>
              </div>
              {/if}
            </DropdownMenu.Content>
          </DropdownMenu.Root>
          {@render action('New session', Plus, onNewSession)}
        </div>
      </header>
      </Tooltip.Provider>

      <div class="filter-pills">
        <FilterPills
          label="Filter sessions"
          groups={MY_WORK_FILTER_GROUPS}
          value={filters}
          onChange={setFilters}
        />
      </div>

      {#if filterOpen}
        <div class="filter-strip">
          <Input
            bind:ref={filterInput}
            bind:value={filterText}
            data-testid="session-rail-filter"
            placeholder="Filter sessions"
            aria-label="Filter sessions by title or project"
            onkeydown={(event: KeyboardEvent) => { if (event.key === 'Escape') hideFilter(); }}
          />
        </div>
      {/if}

      <div class="min-h-0 flex-1 overflow-hidden">
        <SessionRail
          sessions={filtered}
          options={viewOptions}
          {manualOrder}
          onReorder={reorder}
          {activeOwnedId}
          onSelectSession={selectFilteredSession}
          {onComplete}
          {onReopen}
          {onSettle}
          {onUnsettle}
          {onPin}
          {onMarkOrchestrator}
          {onRename}
          {onConnect}
          {onAskRemove}
        />
      </div>
    </div>
  {/if}

<style>
  .state-dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--color-text-3);
  }
  .state-dot[data-state='live'],
  .state-dot[data-state='background'] { background: var(--color-live); }
  .state-dot[data-state='exited'] { background: transparent; box-shadow: inset 0 0 0 1px var(--color-text-2); }

  /* No background here, and none on the folded rail above either. The panel
     this column is mounted into already paints the shell's card — the surface
     colour and the light along its top edge — so a fill of our own only covers
     it up, which is what made this column read as a hole cut in the backdrop
     while the other two read as cards. */
  .sessions-column {
    box-sizing: border-box;
    overflow: hidden;
    font-size: 13px;
    line-height: 19.5px;
  }

  /* The 44px header band every panel shares, set 4px down from the card's top. */
  .sessions-header {
    display: flex;
    flex: 0 0 44px;
    margin-top: 4px;
    align-items: center;
    gap: 4px;
    padding: 0 12px 0 16px;
    font-size: 13px;
    line-height: 19.5px;
  }

  .sessions-header :global(h2) {
    flex: 1 1 auto;
    font-size: 16px;
    letter-spacing: -0.01em;
    line-height: 19.5px;
  }

  /* Search, view options and new session are three plain icon buttons on the
     header band, not a grouped track: they are three things you do, not one
     choice out of three. */
  .sessions-header .header-actions {
    gap: 4px;
  }

  .filter-pills {
    flex: 0 0 auto;
    padding: 4px 16px 12px;
  }

  .filter-strip {
    flex: 0 0 auto;
    padding: 0 12px 8px 16px;
  }
</style>
