<script lang="ts">
  import { getContext } from 'svelte';
  import type { TaskPayload } from '../rows/store';
  import { TASK_STOP } from './tools/tool';
  // Background work the agent runs past its turn: what it is, how it stands, and
  // a Stop button while it runs, if the agent can stop it.
  let { taskId, task }: { taskId: string; task: TaskPayload } = $props();

  const stop = getContext<(taskId: string) => Promise<unknown>>(TASK_STOP);
  const label = { running: 'Running', completed: 'Finished', failed: 'Failed', stopped: 'Stopped' } as const;
  let stopping = $state(false);
  let error = $state('');

  async function onstop() {
    stopping = true;
    error = '';
    try {
      await stop(taskId);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
      stopping = false;
    }
  }
</script>

<div class="task">
  <span class="dot {task.state}"></span>
  <span class="name">{task.name || 'Background task'}</span>
  <span class="state">{label[task.state] ?? task.state}</span>
  {#if task.state === 'running' && task.canStop}
    <button disabled={stopping} onclick={onstop}>{stopping ? 'Stopping' : 'Stop'}</button>
  {/if}
</div>
{#if error}<div class="error">{error}</div>{/if}

<style>
  .task {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 0 0 8px;
    font-size: 13px;
  }
  .dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    flex: none;
    background: var(--muted);
  }
  .dot.running {
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
    font-family: ui-monospace, monospace;
  }
  .state {
    color: var(--muted);
    flex: none;
  }
  button {
    font: inherit;
    padding: 2px 10px;
    border-radius: 6px;
    border: 1px solid var(--line);
    background: var(--code-bg);
    color: var(--danger);
    cursor: pointer;
    flex: none;
  }
  button:disabled {
    opacity: 0.5;
    cursor: default;
  }
  .error {
    color: var(--danger);
    font-size: 13px;
    margin: -4px 0 8px;
  }
</style>
