<script lang="ts">
  import type { ConversationDisplayItem } from '$lib/shell/conversation/conversationTimeline.ts';
  let { item }: { item: Extract<ConversationDisplayItem, { kind: 'tasks' }> } = $props();
  const done = $derived(item.tasks.filter((task) => task.state === 'completed').length);
</script>

<section class="timeline-card" data-testid="timeline-task-list-item">
  <div class="card-heading">
    <span class="card-kind">Tasks</span>
    <strong>{item.title}</strong>
    <span class="count">{done}/{item.tasks.length}</span>
  </div>
  <ul>
    {#each item.tasks as task (task.id)}
      <li class:completed={task.state === 'completed'}>
        <span class="task-state" aria-hidden="true">{task.state === 'completed' ? '✓' : '·'}</span>
        <span class="task-title">{task.title}</span>
        <small>{task.state}</small>
      </li>
    {/each}
  </ul>
</section>

<style>
  .timeline-card{padding:12px;border:1px solid color-mix(in srgb,var(--color-border) 70%,transparent);border-radius:10px;background:color-mix(in srgb,var(--color-surface) 45%,var(--color-bg) 55%)}
  .card-heading{display:flex;align-items:center;gap:8px}
  .card-kind{color:var(--color-text-3);font-size:12px;letter-spacing:.06em;text-transform:uppercase}
  .card-heading strong{flex:1;min-width:0;font-size:13px;font-weight:620}
  .count,small{color:var(--color-text-3);font-size:12px}
  ul{display:grid;gap:8px;margin:12px 0 0;padding:0;list-style:none}
  li{display:flex;align-items:center;gap:8px;font-size:13px}
  .task-title{flex:1;min-width:0}
  li.completed .task-title{text-decoration:line-through;color:var(--color-text-3)}
  .task-state{width:14px;color:var(--color-accent);font-weight:700}
</style>
