<!--
  filter-pills.svelte — a row of grouped, multi-select filter pills, in the
  manner of Spotify's Your Library filters.

  At rest the row shows one pill per group. Pressing a group opens it: the row
  shows a Back button, which closes the group without changing its selection,
  then the group's options, which toggle on and off. Deselecting every option
  clears the group. Escape, a press anywhere outside the row, or ten
  seconds with no press, closes the group and keeps its selection. When the
  keyboard is in the row, opening a group moves focus to its first option and
  closing it moves focus back to the group's pill. A closed
  group with a selection shows the chosen labels joined ("Claude/Codex") as a
  filled pill, or "All" when every option is selected. The X at the far left
  of the resting row clears every group.

  Plain config in, selected values per group id out. The caller owns what the
  values mean and whether they are saved. Needs only Tailwind and the shadcn
  colour slots, so it drops into any shadcn-svelte project.

  Usage:
    <FilterPills label="Filter tasks" groups={GROUPS} value={filters} onChange={(v) => (filters = v)} />
-->
<script lang="ts">
  import { flushSync } from 'svelte';

  interface FilterPillOption {
    value: string;
    label: string;
  }

  interface FilterPillGroup {
    id: string;
    label: string;
    options: readonly FilterPillOption[];
  }

  interface Props {
    groups: readonly FilterPillGroup[];
    /** Selected option values per group id; a missing or empty list means unfiltered. */
    value: Record<string, string[]>;
    onChange(value: Record<string, string[]>): void;
    /** Names the row for a screen reader. */
    label: string;
  }

  let { groups, value, onChange, label }: Props = $props();

  const AUTO_CLOSE_MS = 10_000;
  let openId = $state<string | null>(null);
  let timer: number | undefined;
  let row = $state<HTMLDivElement | null>(null);

  const openGroup = $derived(groups.find((group) => group.id === openId) ?? null);
  const anySelected = $derived(groups.some((group) => selected(group.id).length > 0));

  $effect(() => () => clearTimeout(timer));

  // While a group is open, a press outside the row closes it and keeps its
  // selection. The listener exists only while open; closing or unmounting removes it.
  $effect(() => {
    if (openId === null) return;
    const closeOnOutsidePress = (event: PointerEvent): void => {
      if (!row?.contains(event.target as Node)) close();
    };
    window.addEventListener('pointerdown', closeOnOutsidePress, true);
    return () => window.removeEventListener('pointerdown', closeOnOutsidePress, true);
  });

  function selected(groupId: string): string[] {
    return value[groupId] ?? [];
  }

  function restartTimer(): void {
    clearTimeout(timer);
    timer = window.setTimeout(close, AUTO_CLOSE_MS);
  }

  // The pressed button disappears when the row swaps between pills and
  // options, which would drop focus to the page. If focus was in the row, put it
  // on the matching button in the new row.
  function focusInRow(): boolean {
    return row?.contains(document.activeElement) ?? false;
  }

  function open(groupId: string): void {
    const hadFocus = focusInRow();
    flushSync(() => (openId = groupId));
    restartTimer();
    if (hadFocus) row?.querySelector<HTMLElement>('[aria-pressed]')?.focus();
  }

  function close(): void {
    clearTimeout(timer);
    const hadFocus = focusInRow();
    const groupId = openId ?? groups[0]?.id;
    flushSync(() => (openId = null));
    // A long open group can leave a narrow row scrolled right, hiding the clear button.
    if (row) row.scrollLeft = 0;
    if (hadFocus && groupId) row?.querySelector<HTMLElement>(`[data-group="${CSS.escape(groupId)}"]`)?.focus();
  }

  function closeOnEscape(event: KeyboardEvent): void {
    if (event.key !== 'Escape' || openId === null) return;
    event.preventDefault();
    event.stopPropagation();
    close();
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

  const PILL =
    'inline-flex h-[28px] shrink-0 cursor-pointer items-center justify-center rounded-full border-0 ' +
    'text-[13px] whitespace-nowrap outline-none transition-[background-color,color,filter] ' +
    'focus-visible:ring-ring/50 focus-visible:ring-3';
  const REST = 'bg-secondary text-foreground hover:bg-accent/60 hover:text-foreground';
  const CHOSEN = 'bg-foreground text-background hover:brightness-125';
  const ICON = 'w-[28px] bg-secondary text-muted-foreground hover:bg-accent/60 hover:text-foreground';
</script>

{#snippet closeIcon()}
  <svg
    class="size-3.5"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="2"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <path d="M18 6 6 18M6 6l12 12" />
  </svg>
{/snippet}

<!-- svelte-ignore a11y_no_noninteractive_element_interactions (Escape bubbles up from the row's buttons) -->
<div
  bind:this={row}
  role="group"
  aria-label={label}
  class="flex flex-nowrap items-center gap-2 overflow-x-auto"
  onkeydown={closeOnEscape}
>
  {#if openGroup}
    {@const group = openGroup}
    <button
      type="button"
      class="{PILL} px-3 {REST}"
      aria-label="Back to filters"
      onclick={close}
    >← Back</button>
    {#each group.options as option (option.value)}
      {@const pressed = selected(group.id).includes(option.value)}
      <button
        type="button"
        class="{PILL} px-3 {pressed ? CHOSEN : REST}"
        aria-pressed={pressed}
        onclick={() => toggle(group, option.value)}
      >{option.label}</button>
    {/each}
  {:else}
    {#if anySelected}
      <button
        type="button"
        class="{PILL} {ICON}"
        aria-label="Clear all filters"
        onclick={clearAll}
      >{@render closeIcon()}</button>
    {/if}
    {#each groups as group (group.id)}
      <button
        type="button"
        class="{PILL} px-3 {selected(group.id).length > 0 ? CHOSEN : REST}"
        aria-expanded="false"
        data-group={group.id}
        onclick={() => open(group.id)}
      >{pillLabel(group)}</button>
    {/each}
  {/if}
</div>
