<!--
  FilterPills.svelte — Shared grouped multi-select filter pills in the manner of
  Spotify's Your Library filters (1:1 port of the old app's shared filter-pills).

  At rest the row shows one pill per group. Clicking a group drills into it inline:
  the row swaps to `← Back` plus each option pill, which toggles immediately on click.
  Escape, clicking outside the row, or 10 seconds with no click closes the group and
  keeps its selection. A closed group with a selection displays the chosen labels joined
  with "/" ("Claude/Codex") as a filled pill, or "All" when every option is chosen.
  When any group is active, a round `×` pill at the far left clears every group.
-->
<script module lang="ts">
  export interface FilterPillOption {
    value: string;
    label: string;
  }

  export interface FilterPillGroup {
    id: string;
    label: string;
    options: readonly FilterPillOption[];
  }
</script>

<script lang="ts">
  import { flushSync } from 'svelte';
  import { X } from '@lucide/svelte';

  interface Props {
    groups: readonly FilterPillGroup[];
    /** Selected option values per group id; a missing or empty list means unfiltered. */
    value: Record<string, string[]>;
    onChange: (value: Record<string, string[]>) => void;
    /** Accessible name for the filter pill group. */
    label: string;
  }

  let { groups, value, onChange, label }: Props = $props();

  const AUTO_CLOSE_MS = 10_000;
  let openId = $state<string | null>(null);
  let autoCloseTimer: ReturnType<typeof setTimeout> | null = null;
  let row = $state<HTMLDivElement | null>(null);

  const openGroup = $derived(groups.find((group) => group.id === openId) ?? null);
  const anySelected = $derived(groups.some((group) => selected(group.id).length > 0));

  function cancelAutoClose(): void {
    if (autoCloseTimer !== null) {
      clearTimeout(autoCloseTimer);
      autoCloseTimer = null;
    }
  }

  $effect(() => () => cancelAutoClose());

  // Listeners exist only while a group is drilled into; closing or unmounting removes them.
  // Handling Escape on window ensures it works in macOS WebKit where mouse clicks don't focus buttons.
  $effect(() => {
    if (openId === null) return;
    const closeOnOutsidePress = (event: PointerEvent): void => {
      if (!row?.contains(event.target as Node)) close();
    };
    const closeOnWindowEscape = (event: KeyboardEvent): void => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      event.stopPropagation();
      close();
    };
    window.addEventListener('pointerdown', closeOnOutsidePress, true);
    window.addEventListener('keydown', closeOnWindowEscape, true);
    return () => {
      window.removeEventListener('pointerdown', closeOnOutsidePress, true);
      window.removeEventListener('keydown', closeOnWindowEscape, true);
    };
  });

  function selected(groupId: string): string[] {
    return value[groupId] ?? [];
  }

  function restartTimer(): void {
    cancelAutoClose();
    autoCloseTimer = setTimeout(() => {
      autoCloseTimer = null;
      close();
    }, AUTO_CLOSE_MS);
  }

  function focusInRow(): boolean {
    return row?.contains(document.activeElement) ?? false;
  }

  function open(groupId: string): void {
    const hadFocus = focusInRow();
    flushSync(() => {
      openId = groupId;
    });
    restartTimer();
    if (hadFocus) {
      row?.querySelector<HTMLElement>('[aria-pressed]')?.focus();
    }
  }

  function close(): void {
    cancelAutoClose();
    const hadFocus = focusInRow();
    const groupId = openId ?? groups[0]?.id;
    flushSync(() => {
      openId = null;
    });
    if (row) row.scrollLeft = 0;
    if (hadFocus && groupId) {
      row?.querySelector<HTMLElement>(`[data-group="${CSS.escape(groupId)}"]`)?.focus();
    }
  }

  function toggle(group: FilterPillGroup, optionValue: string): void {
    const current = selected(group.id);
    const next = group.options
      .map((option) => option.value)
      .filter((candidate) =>
        candidate === optionValue ? !current.includes(candidate) : current.includes(candidate)
      );
    onChange({ ...value, [group.id]: next });
    restartTimer();
  }

  function clearAll(): void {
    onChange(Object.fromEntries(groups.map((group) => [group.id, []])));
    close();
  }

  function pillLabel(group: FilterPillGroup): string {
    const chosen = group.options.filter((option) => selected(group.id).includes(option.value));
    if (chosen.length > 0 && chosen.length === group.options.length) return 'All';
    return chosen.length > 0 ? chosen.map((option) => option.label).join('/') : group.label;
  }
</script>

<div
  bind:this={row}
  role="group"
  aria-label={label}
  class="filter-pills-row"
>
  {#if openGroup}
    {@const group = openGroup}
    <button
      type="button"
      class="pill rest"
      aria-label="Back to filters"
      onclick={close}
    >← Back</button>

    {#if group.options.length === 0}
      <span class="empty-hint">None yet</span>
    {:else}
      {#each group.options as option (option.value)}
        {@const pressed = selected(group.id).includes(option.value)}
        <button
          type="button"
          class="pill"
          class:chosen={pressed}
          class:rest={!pressed}
          aria-pressed={pressed}
          onclick={() => toggle(group, option.value)}
        >{option.label}</button>
      {/each}
    {/if}
  {:else}
    {#if anySelected}
      <button
        type="button"
        class="pill icon-pill"
        aria-label="Clear all filters"
        title="Clear all filters"
        onclick={clearAll}
      >
        <X size={14} strokeWidth={2} />
      </button>
    {/if}

    {#each groups as group (group.id)}
      {@const hasSelection = selected(group.id).length > 0}
      <button
        type="button"
        class="pill"
        class:chosen={hasSelection}
        class:rest={!hasSelection}
        aria-expanded="false"
        data-group={group.id}
        onclick={() => open(group.id)}
      >{pillLabel(group)}</button>
    {/each}
  {/if}
</div>

<style>
  .filter-pills-row {
    display: flex;
    flex-wrap: nowrap;
    align-items: center;
    gap: 8px;
    overflow-x: auto;
    scrollbar-width: none;
  }

  .filter-pills-row::-webkit-scrollbar {
    display: none;
  }

  .pill {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    height: 28px;
    padding: 0 12px;
    flex-shrink: 0;
    border: none;
    border-radius: 999px;
    font-family: inherit;
    font-size: 13px;
    font-weight: 400;
    line-height: 1;
    white-space: nowrap;
    cursor: pointer;
    outline: none;
    transition: background-color 110ms ease, color 110ms ease, filter 110ms ease;
  }

  .pill:focus-visible {
    box-shadow: 0 0 0 2px rgba(255, 255, 255, 0.35);
  }

  .pill.rest {
    background: var(--color-elevated);
    color: var(--color-text);
  }

  .pill.rest:hover {
    background: rgba(255, 255, 255, 0.14);
    color: var(--color-text);
  }

  .pill.chosen {
    background: var(--color-text);
    color: var(--color-canvas);
    font-weight: 500;
  }

  .pill.chosen:hover {
    filter: brightness(0.92);
  }

  .pill.icon-pill {
    width: 28px;
    padding: 0;
    background: var(--color-elevated);
    color: var(--color-text-2);
  }

  .pill.icon-pill:hover {
    background: rgba(255, 255, 255, 0.14);
    color: var(--color-text);
  }

  .empty-hint {
    font-size: 12px;
    color: var(--color-text-3);
    padding: 0 4px;
    white-space: nowrap;
  }
</style>
