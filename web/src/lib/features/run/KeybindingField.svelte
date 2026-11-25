<!--
  KeybindingField.svelte — press a key combination to save it as a shortcut.

  1:1 port from mac-command-bar:
   - While only modifiers are held it keeps waiting.
   - Escape clears the field.
   - Tab leaves the field without capturing.
   - Every other keypress while focused is swallowed and captured.
-->
<script lang="ts">
  import Delete from '@lucide/svelte/icons/delete';

  import { IconButton } from '$lib/ui/icon-button/index.js';

  import { formatKeybinding, isModifierOnly } from './keybindingCapture';

  interface Props {
    /** The captured combination, or '' when none is set. */
    value: string;
    /** Ties the field to its label. */
    id?: string;
    disabled?: boolean;
  }
  let { value = $bindable(''), id, disabled = false }: Props = $props();

  /** True while the field has focus and is listening. */
  let capturing = $state(false);

  function capture(event: KeyboardEvent): void {
    if (disabled) return;
    if (event.key === 'Tab') return;
    event.preventDefault();
    event.stopPropagation();
    if (event.key === 'Escape') {
      value = '';
      return;
    }
    if (isModifierOnly(event)) return;
    const captured = formatKeybinding(event);
    if (captured) value = captured;
  }
</script>

<div class="flex items-center gap-2">
  <button
    {id}
    type="button"
    {disabled}
    aria-label="Shortcut. Press the key combination you want."
    class="flex h-8 min-w-0 flex-1 items-center gap-2 rounded-lg border border-input bg-background
           px-2 text-left text-[13px] leading-tight text-foreground outline-none transition-colors
           hover:bg-accent/60 focus-visible:ring-3 focus-visible:ring-ring/50
           disabled:pointer-events-none disabled:opacity-50"
    onkeydown={capture}
    onfocus={() => (capturing = true)}
    onblur={() => (capturing = false)}
  >
    {#if value}
      <span class="font-mono">{value}</span>
    {:else if capturing}
      <span class="text-muted-foreground">Press the combination…</span>
    {:else}
      <span class="text-muted-foreground">Click here, then press a combination</span>
    {/if}
  </button>

  {#if value}
    <IconButton
      label="Remove this shortcut"
      size="sm"
      {disabled}
      onclick={() => (value = '')}
    >
      <Delete />
    </IconButton>
  {/if}
</div>

{#if capturing}
  <p class="text-xs leading-snug text-muted-foreground">
    Press Escape to clear it. Tab moves on without capturing.
  </p>
{/if}
