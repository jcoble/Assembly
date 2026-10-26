<script lang="ts">
  import { getContext } from 'svelte';
  import type { Thread } from '../api';
  import { THREAD_OPEN } from '../chat/tools/tool';

  // The conversation's header: the repo, the agent, the thread's short id and a link to its parent thread.
  let { thread, speaker, onclose }: { thread: Thread; speaker: string; onclose: () => void } = $props();
  const openThread = getContext<(threadId: string) => void>(THREAD_OPEN);
</script>

<header>
  <div>
    <div class="repo">{thread.repoUrl}</div>
    <div class="meta">{speaker} · {thread.threadId.slice(0, 8)}{#if thread.parentThreadId}{' · '}<button class="link" onclick={() => openThread(thread.parentThreadId!)}>Parent thread</button>{/if}</div>
  </div>
  <button class="ghost" onclick={onclose}>Close</button>
</header>

<style>
  header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 14px 28px;
    border-bottom: 1px solid var(--line);
  }
  .repo {
    font-weight: 600;
  }
  .meta {
    color: var(--muted);
    font-size: 12px;
  }
  .link {
    font: inherit;
    padding: 0;
    border: 0;
    background: none;
    color: var(--accent);
    cursor: pointer;
  }
</style>
