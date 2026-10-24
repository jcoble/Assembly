<!--
  icon-button.svelte — a square button carrying an icon and nothing else, with
  the label it needs in order to be usable.

  An icon on its own is a guess. This component makes the two things that fix
  that impossible to forget: `label` becomes both the accessible name and the
  tooltip text, so every icon button in the shell says what it does on hover
  and to a screen reader. Every icon button is at least 28px square, whichever
  size it asks for, because a smaller square is one a pointer misses.

  It is a round ghost button: at rest only the muted glyph, on hover the
  shared hover fill with the glyph brightened. `tone` picks the colour the
  glyph turns on hover.

  Usage:
    <IconButton label="Close panel" onclick={close}><X /></IconButton>
-->
<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Button } from '$lib/components/ui/button/index.js';
  import type { ButtonVariant } from '$lib/components/ui/button/variants.js';
  import * as Tooltip from '$lib/components/ui/tooltip/index.js';
  import { cn } from '$lib/utils.js';

  type IconButtonTone = 'accent' | 'live' | 'attention';

  /**
   * The props are listed one by one rather than inherited from
   * `HTMLButtonAttributes`: every DOM attribute plus the button's own union of
   * variants is a type large enough that the checker gives up on it, and an
   * icon button only ever needs these. Anything more elaborate should use
   * `Button` directly.
   */
  interface Props {
    /** What the button does, in plain words. Used as the tooltip and the accessible name. */
    label: string;
    children: Snippet;
    /** How much room the glyph gets: `sm` (the default for shell chrome) and
     * `default` are 28px and 32px. `xs` asks for 24px and still gets a 28px
     * hit target, because the floor applies to every size. */
    size?: 'xs' | 'sm' | 'default';
    /** Which colour the glyph turns on hover. Plain text unless there is a reason. */
    tone?: IconButtonTone;
    variant?: ButtonVariant;
    side?: 'top' | 'bottom' | 'left' | 'right';
    /** Turn the tooltip off where the surrounding text already says the same thing. */
    tooltip?: boolean;
    disabled?: boolean;
    class?: string;
    "data-testid"?: string;
    onclick?: (event: MouseEvent) => void;
  }

  let {
    label,
    children,
    size = 'sm',
    tone = 'accent',
    variant = 'ghost',
    side = 'top',
    tooltip = true,
    disabled = false,
    class: className,
    "data-testid": dataTestId,
    onclick
  }: Props = $props();

  const buttonSize = $derived(
    size === 'xs' ? ('icon-xs' as const) : size === 'sm' ? ('icon-sm' as const) : ('icon' as const)
  );

  /**
   * The look itself is the ghost icon button's own: muted glyph, the hover
   * fill on hover, the selected fill while pressed. A tone only changes the
   * colour the glyph turns on hover. Written out in full because Tailwind
   * reads this file as text and only generates classes it sees.
   *
   * The caller's own `class` is applied after these, so a button that already
   * declares its own hover colour keeps it.
   */
  const TONE_CLASSES: Record<IconButtonTone, string> = {
    accent: 'min-h-7 min-w-7',
    live: 'min-h-7 min-w-7 hover:text-[var(--color-live)]',
    attention: 'min-h-7 min-w-7 hover:text-[var(--color-attention)]'
  };

  const buttonClass = $derived(cn(TONE_CLASSES[tone], className));

  /**
   * The tooltip's content is mounted only while it is open. A closed tooltip
   * still leaves an empty portal in `document.body`, and with a thousand of
   * them WebKit took seconds to remove the list they belonged to.
   */
  let open = $state(false);
</script>

{#if tooltip}
    <Tooltip.Root bind:open>
      <Tooltip.Trigger>
        {#snippet child({ props })}
          <Button
            {...props}
            data-slot="icon-button"
            size={buttonSize}
            {variant}
            {disabled}
            {onclick}
            data-testid={dataTestId}
            aria-label={label}
            class={buttonClass}
          >
            {@render children()}
          </Button>
        {/snippet}
      </Tooltip.Trigger>
      {#if open}
        <Tooltip.Content {side} sideOffset={6}>{label}</Tooltip.Content>
      {/if}
    </Tooltip.Root>
{:else}
  <Button
    data-slot="icon-button"
    size={buttonSize}
    {variant}
    {disabled}
    {onclick}
    data-testid={dataTestId}
    aria-label={label}
    class={buttonClass}
  >
    {@render children()}
  </Button>
{/if}
