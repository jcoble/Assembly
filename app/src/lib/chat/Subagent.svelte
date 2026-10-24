<script lang="ts">
  import { getContext } from 'svelte';
  import type { SubagentPayload } from '../rows/store';
  import { TASK_STOP, THREAD_OPEN } from './tools/tool';
  // A subagent the agent runs as a session of its own: what it was asked, how it
  // stands, a Stop while it works if the agent can stop it, and its own thread to open.
  // Nested (indented) under the tool call that started it, when the agent said which.
  let { sessionId, subagent }: { sessionId: string; subagent: SubagentPayload } = $props();

  const stop = getContext<(taskId: string) => Promise<unknown>>(TASK_STOP);
  const openThread = getContext<(threadId: string) => void>(THREAD_OPEN);
  const label = { running: 'Running', requires_action: 'Waiting for you', idle: 'Finished' } as const;
  const shown = $derived(
    subagent.state === 'idle' && subagent.error ? 'Failed' : subagent.state === 'idle' && subagent.stopReason === 'cancelled' ? 'Stopped' : (label[subagent.state] ?? subagent.state),
  );
  let stopping = $state(false);
  let error = $state('');

  async function onstop() {
    stopping = true;
    error = '';
    try {
      await stop(sessionId);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
      stopping = false;
    }
  }
</script>

<div class="subagent" class:nested={!!subagent.toolCallId}>
  <div class="line">
    <span class="dot {subagent.state}" class:failed={!!subagent.error}></span>
    <button class="name" title="Open the subagent's thread" onclick={() => openThread(subagent.threadId)}>{subagent.title || 'Subagent'}</button>
    <span class="state">{shown}</span>
    {#if subagent.state !== 'idle' && subagent.canStop}
      <button class="stop" disabled={stopping} onclick={onstop}>{stopping ? 'Stopping' : 'Stop'}</button>
    {/if}
  </div>
  {#if subagent.description && subagent.description !== subagent.title}<div class="description">{subagent.description}</div>{/if}
  {#if subagent.error}<div class="error">{subagent.error}</div>{/if}
  {#if error}<div class="error">{error}</div>{/if}
</div>

<style>
  .subagent {
    margin: 0 0 8px;
    font-size: 13px;
  }
  .nested {
    margin-left: 14px;
    padding-left: 10px;
    border-left: 2px solid var(--line);
  }
  .line {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    flex: none;
    background: var(--muted);
  }
  .dot.running,
  .dot.requires_action {
    background: var(--accent);
  }
  .dot.failed {
    background: var(--danger);
  }
  .name {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font: inherit;
    font-weight: 600;
    padding: 0;
    border: 0;
    background: none;
    color: inherit;
    cursor: pointer;
    text-align: left;
  }
  .name:hover {
    color: var(--accent);
  }
  .state {
    color: var(--muted);
    flex: none;
  }
  .stop {
    font: inherit;
    padding: 2px 10px;
    border-radius: 6px;
    border: 1px solid var(--line);
    background: var(--code-bg);
    color: var(--danger);
    cursor: pointer;
    flex: none;
  }
  .stop:disabled {
    opacity: 0.5;
    cursor: default;
  }
  .description {
    color: var(--muted);
    margin: 2px 0 0 15px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .error {
    color: var(--danger);
    margin: 2px 0 0 15px;
  }
</style>
