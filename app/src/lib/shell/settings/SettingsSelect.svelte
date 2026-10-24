<script lang="ts">
  /**
   * The dropdown used across the settings screen.
   *
   * Not the shadcn/bits-ui select: that one opens on pointerdown and is then
   * dismissed by the same click's pointerup in this app's webview, so it never
   * stayed open. This one opens on click and behaves.
   */
  import Select from 'svelte-select';

  type Item = { value: string; label: string };

  interface Props {
    items: Item[];
    value: string;
    ariaLabel: string;
    onChange: (value: string) => void;
    searchable?: boolean;
  }

  let { items, value, ariaLabel, onChange, searchable = false }: Props = $props();
</script>

<div class="settings-select">
  <Select
    {items}
    {value}
    valueMode="id"
    {searchable}
    clearable={false}
    showChevron
    inputAttributes={{ 'aria-label': ariaLabel }}
    onchange={(next) => {
      if (typeof next === 'string' && next !== value) onChange(next);
    }}
  />
</div>

<style>
  /* svelte-select is themed through these; they map onto the app's own tokens
     so the control matches every other surface. */
  .settings-select {
    width: 100%;

    --height: 32px;
    --font-size: 13px;
    --border-radius: 8px;
    --background: transparent;
    --border: 1px solid var(--color-border);
    --border-hover: 1px solid var(--color-border);
    --border-focused: 1px solid var(--color-focus-solid);
    --input-color: var(--color-text);
    --placeholder-color: var(--color-text-3);
    --chevron-color: var(--color-text-3);
    --chevron-height: 16px;
    --chevron-width: 16px;

    --list-background: var(--color-elevated);
    --list-border: 1px solid var(--color-border);
    --list-border-radius: 8px;
    --list-shadow: 0 12px 32px rgba(0, 0, 0, 0.45);
    --list-z-index: 300;
    --item-color: var(--color-text);
    --item-hover-bg: var(--color-hover);
    --item-hover-color: var(--color-text);
    --item-is-active-bg: var(--color-selected);
    --item-is-active-color: var(--color-text);
    --item-height: 30px;
    --item-line-height: 30px;
    --item-first-border-radius: 8px 8px 0 0;
    --item-padding: 0 10px;
  }

  .settings-select :global(.svelte-select:hover) {
    background: var(--color-hover);
  }

  .settings-select :global(.svelte-select.focused) {
    box-shadow: 0 0 0 3px var(--color-focus);
  }
</style>
