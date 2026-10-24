<!--
  KeybindingField.svelte — press a key combination to save it as a shortcut.

  There is no such control in the kit and no browser control that does this: a
  text box would have someone typing the words "Cmd+Shift+R", which is a spelling
  test rather than a shortcut. So this is a button that listens. Focus it, hold
  the combination, and what was captured is what it shows.

  HOW IT BEHAVES
   - While only modifiers are down it keeps waiting — `Cmd` on its own is not a
     shortcut, and releasing to think about it should not save one.
   - Escape clears the field. That is the only way to remove a shortcut, and it
     is why Escape can never itself be captured.
   - Every keypress while it is focused is swallowed, so capturing `Cmd+W` sets
     a shortcut instead of closing something.
   - Blurring stops the capture; the value stays as it was last captured.

  Usage:
    <KeybindingField id="run-action-keybinding" bind:value={draftKeybinding} />
-->
<script lang="ts">
  import Delete from '@lucide/svelte/icons/delete';

  import { IconButton } from '$lib/components/ui/icon-button/index.js';

  import { formatKeybinding, isModifierOnly } from './keybindingCapture.ts';

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
    event.preventDefault();
    event.stopPropagation();
    if (event.key === 'Escape') {
      value = '';
      return;
    }
    // Tab keeps its job: it is the only way out of a field that swallows keys.
    if (event.key === 'Tab') return;
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
  <p class="text-sm leading-snug text-muted-foreground">
    Press Escape to clear it. Tab moves on without capturing.
  </p>
{/if}
