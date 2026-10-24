<!--
  AgentRow.svelte — one subagent in the Agents panel.

  Name and status on the first line, what it is doing on the second, and how
  many messages its transcript holds when that is known. A count only exists
  for an agent whose transcript has been read, so the rest show nothing at all
  rather than a zero that would be a guess.
-->
<script lang="ts">
  import { Chip } from '$lib/components/ui/chip/index.js';
  import { ListRow } from '$lib/components/ui/list-row/index.js';

  import { AGENT_STATUS_TONE, AGENT_STATUS_WORD, type AgentActivityRow } from './agentActivityModel.ts';

  interface Props {
    row: AgentActivityRow;
    selected: boolean;
    onselect: (childId: string) => void;
  }
  let { row, selected, onselect }: Props = $props();
</script>

<ListRow
  {selected}
  actionsLabel="Agent actions"
  onclick={() => onselect(row.childId)}
  data-testid="agents-panel-row"
>
  <span class="flex min-w-0 flex-1 flex-col gap-0.5 py-0.5">
    <span class="flex min-w-0 items-center gap-2">
      <span class="min-w-0 flex-1 truncate text-[13px] leading-tight">{row.label}</span>
      <Chip tone={AGENT_STATUS_TONE[row.status]}>{AGENT_STATUS_WORD[row.status]}</Chip>
      {#if row.messageCount !== null}
        <Chip tone="count">{row.messageCount} msgs</Chip>
      {/if}
    </span>
    {#if row.activity}
      <span class="min-w-0 truncate text-sm leading-tight text-muted-foreground">{row.activity}</span>
    {/if}
  </span>

</ListRow>
