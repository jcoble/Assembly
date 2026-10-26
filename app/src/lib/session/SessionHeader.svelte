<script lang="ts">
  import { getContext } from 'svelte';
  import type { Session } from '../api';
  import { SESSION_OPEN } from '../chat/tools/tool';

  // The session's header: the repo, the agent, the session's short id and a link to its parent session.
  let { session, speaker, onclose }: { session: Session; speaker: string; onclose: () => void } = $props();
  const openSession = getContext<(sessionId: string) => void>(SESSION_OPEN);
</script>

<header>
  <div>
    <div class="repo">{session.repoUrl}</div>
    <div class="meta">{speaker} · {session.sessionId.slice(0, 8)}{#if session.parentSessionId}{' · '}<button class="link" onclick={() => openSession(session.parentSessionId!)}>Parent session</button>{/if}</div>
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
