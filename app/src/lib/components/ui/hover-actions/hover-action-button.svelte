<!--
  hover-action-button.svelte — one button inside a hover-action cluster.

  It is an IconButton, so it still owes its label as tooltip and accessible
  name. Its hover is the shell's neutral hover fill; a non-default tone only
  turns the glyph its destination's colour.

  `tone` says which kind of destination the action leads to, not which color to
  use: `primary` for the session itself, `info` for a file or editor surface,
  `success` for source control, `attention` for putting something away.
  Anything else stays `default`.
-->
<script lang="ts">
  import type { Snippet } from 'svelte';

  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { cn } from '$lib/utils.js';

  type HoverActionTone = 'default' | 'primary' | 'info' | 'success' | 'attention';

  interface Props {
    label: string;
    children: Snippet;
    tone?: HoverActionTone;
    /** `sm` (28px) is the default, matching the approved mockup; `xs` (24px) is for tight rows. */
    size?: 'xs' | 'sm';
    disabled?: boolean;
    class?: string;
    'data-testid'?: string;
    onclick?: (event: MouseEvent) => void;
  }

  let {
    label,
    children,
    tone = 'default',
    size = 'sm',
    disabled = false,
    class: className,
    'data-testid': dataTestId,
    onclick
  }: Props = $props();

  /**
   * A destination maps to one of the shell's three signal colors, which the
   * glyph turns on hover.
   */
  const TONES: Record<HoverActionTone, 'accent' | 'live' | 'attention'> = {
    default: 'accent',
    primary: 'accent',
    info: 'live',
    success: 'accent',
    attention: 'attention'
  };
</script>

<IconButton
  {label}
  {size}
  {disabled}
  {onclick}
  tone={TONES[tone]}
  side="bottom"
  data-testid={dataTestId}
  class={cn('rounded-full bg-transparent hover:bg-accent hover:shadow-none', className)}
>
  {@render children()}
</IconButton>
