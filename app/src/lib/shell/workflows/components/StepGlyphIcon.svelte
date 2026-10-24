<!-- One step's state as a glyph, the same on the Board and the Map. Static: a running step is a plain ring. -->
<script lang="ts">
  import Circle from '@lucide/svelte/icons/circle';
  import CircleCheck from '@lucide/svelte/icons/circle-check';
  import CircleDashed from '@lucide/svelte/icons/circle-dashed';
  import CircleHelp from '@lucide/svelte/icons/circle-help';
  import CircleMinus from '@lucide/svelte/icons/circle-minus';
  import CircleStop from '@lucide/svelte/icons/circle-stop';
  import CircleX from '@lucide/svelte/icons/circle-x';
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';

  import type { StepGlyph } from '../workflowBoard.ts';

  let { glyph }: { glyph: StepGlyph } = $props();

  const ICONS = {
    passed: [CircleCheck, 'text-[var(--color-good)]'],
    running: [Circle, 'text-[var(--color-live)]'],
    'sent-back': [RotateCcw, 'text-muted-foreground'],
    failed: [CircleX, 'text-[var(--color-bad)]'],
    stopped: [CircleStop, 'text-muted-foreground'],
    'needs-you': [CircleHelp, 'text-[var(--color-attention)]'],
    waiting: [CircleDashed, 'text-muted-foreground'],
    skipped: [CircleMinus, 'text-muted-foreground']
  } as const;
  const LABELS: Record<StepGlyph, string> = {
    passed: 'passed', running: 'running', 'sent-back': 'sent back', failed: 'failed',
    stopped: 'stopped', 'needs-you': 'needs you', waiting: 'waiting', skipped: 'skipped'
  };
  const Icon = $derived(ICONS[glyph][0]);
</script>

<Icon class="size-4 shrink-0 {ICONS[glyph][1]}" aria-label={LABELS[glyph]} role="img" />
