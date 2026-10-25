<script lang="ts">
  import { onDestroy, setContext, untrack } from 'svelte';
  import Conversation from '../lib/Conversation.svelte';
  import { THREAD_OPEN } from '../lib/chat/tools/tool';
  import { createThread, dropLink, getThread, listAgents, listMachines, listThreads, type Agent, type MemorySample, type Thread } from '../lib/api';
  import { pageCall } from '../lib/host/pageCall';
  import { TOKEN } from '../lib/host/backend';
  import { counters, measures } from '../lib/debug.svelte';
  import { lag } from '../lib/lag.svelte';
  import { onFailure } from '../lib/call';
  import { loadPreferences } from '../lib/preferences/preferences.svelte';
  import { logCounters, runSoak, SEED_REPO, SOAK_REPO, type SoakChat } from '../lib/soak';

  // Page-level work (thread list, new thread) is owned by this signal.
  const page = new AbortController();
  // The shell starts the backend before the page loads and hands it the token.
  const token = TOKEN;
  let threads = $state<Thread[]>([]);
  let openId = $state<string | null>(null);
  const backend = { state: token ? 'up' : 'down' };
  let repoUrl = $state('');
  // '' is this Mac.
  let machines = $state<string[]>([]);
  let machine = $state('');
  let agents = $state<Agent[]>([]);
  let agent = $state('');
  // The picked machine's agents; the first is picked until another is.
  $effect(() => {
    const [tokenNow, on] = [token, machine];
    if (!tokenNow) return;
    listAgents(tokenNow, page.signal, on || undefined).then(
      (listed) => {
        agents = listed.agents;
        if (!agents.some((a) => a.id === agent)) agent = agents[0]?.id ?? '';
      },
      (e) => {
        if (!page.signal.aborted) error = String(e);
      },
    );
  });
  let error = $state('');
  // A subagent's thread isn't in the list: it's fetched when opened from its row.
  let child = $state<Thread | null>(null);
  const open = $derived(threads.find((t) => t.threadId === openId) ?? (child?.threadId === openId ? child : undefined));
  function openThread(threadId: string) {
    openId = threadId;
    if (!token || threads.some((t) => t.threadId === threadId) || child?.threadId === threadId) return;
    getThread(token, threadId, page.signal).then(
      (thread) => (child = thread),
      (e) => {
        if (!page.signal.aborted) error = String(e);
      },
    );
  }
  setContext(THREAD_OPEN, openThread);

  onDestroy(() => page.abort());

  async function refresh(tokenNow: string) {
    try {
      threads = (await listThreads(tokenNow, page.signal)).threads;
      machines = (await listMachines(tokenNow, page.signal)).machines;
      error = '';
    } catch (e) {
      if (!page.signal.aborted) error = String(e);
    }
  }

  if (token) {
    void refresh(token);
    onFailure((message) => (error = message));
    void loadPreferences();
  }

  // Debug controls: the soak drives the same page calls.
  let memory = $state('');
  async function drop() {
    if (!token) return;
    try {
      await dropLink(token, 5000, page.signal);
    } catch (e) {
      if (!page.signal.aborted) error = String(e);
    }
  }
  async function sampleMemory() {
    try {
      const m = await pageCall<MemorySample>('memory.sample');
      memory = `app ${Math.round((m.appRssKb ?? 0) / 1024)} MB · backend ${Math.round((m.backendRssKb ?? 0) / 1024)} MB · WebContent ${m.webContentPid} ${Math.round((m.webContentRssKb ?? 0) / 1024)} MB`;
    } catch (e) {
      error = String(e);
    }
  }

  // Soak mode (?soak=1, ?soak=seed), the loop in lib/soak.ts. Seeding makes a
  // new thread A; the soak takes the newest SEED_REPO thread as A and makes a new B.
  const query = new URLSearchParams(location.search);
  const soakMode = query.get('soak');
  let onchat = $state<(chat: SoakChat) => void>();
  async function soak(tokenNow: string) {
    const seed = soakMode === 'seed';
    const light = soakMode === 'light';
    const switching = soakMode === 'switch';
    const scroll = soakMode === 'scroll';
    try {
      await refresh(tokenNow);
      // Light runs on the two existing threads it is given and makes none.
      const seeded = seed ? undefined : light || scroll || switching ? query.get('a') : threads.find((t) => t.repoUrl === SEED_REPO)?.threadId;
      if (scroll && (!seeded || !query.get('b'))) throw new Error('?soak=scroll needs a=<thread>&b=<thread>');
      if (light && (!seeded || !query.get('b'))) throw new Error('?soak=light needs a=<thread>&b=<thread>');
      if (!seed && !seeded) throw new Error(`no ${SEED_REPO} thread: run ?soak=seed first`);
      const threadA = seeded ?? (await createThread(tokenNow, SEED_REPO, page.signal)).threadId;
      const threadB = seed ? null : light || scroll || switching ? query.get('b') : (await createThread(tokenNow, SOAK_REPO, page.signal)).threadId;
      await refresh(tokenNow);
      await runSoak({
        token: tokenNow,
        seed,
        light,
        switching,
        scroll,
        threadA,
        threadB,
        open: (threadId) =>
          new Promise((resolve) => {
            onchat = resolve;
            openId = threadId;
          }),
        close: () => (openId = null),
        signal: page.signal,
      });
      error = 'soak finished';
    } catch (e) {
      if (!page.signal.aborted) error = `soak failed: ${e}`;
    }
  }
  // ?counters=1: every 2 s, the open conversation's counters go to
  // soak-results/counters-<time>.csv, for a run driven by hand.
  if (query.get('counters') === '1') {
    let chat: SoakChat | undefined;
    onchat = (opened) => (chat = opened);
    logCounters(() => (openId !== null ? chat : undefined), () => openId, page.signal).catch((e) => (error = `counters log stopped: ${e}`));
  }

  let soakStarted = false;
  $effect(() => {
    const tokenNow = token;
    if ((soakMode === '1' || soakMode === 'seed' || soakMode === 'light' || soakMode === 'scroll' || soakMode === 'switch') && !soakStarted && backend.state === 'up' && tokenNow) {
      soakStarted = true;
      untrack(() => void soak(tokenNow));
    }
  });

  async function create(event: SubmitEvent) {
    event.preventDefault();
    if (!token || !repoUrl.trim() || !agent) return;
    try {
      const { threadId } = await createThread(token, repoUrl.trim(), page.signal, { machine: machine || undefined, agent });
      repoUrl = '';
      await refresh(token);
      openId = threadId;
    } catch (e) {
      if (!page.signal.aborted) error = String(e);
    }
  }
</script>

<div class="app">
  <aside>
    <form onsubmit={create}>
      <select bind:value={agent} aria-label="Agent">
        {#each agents as a (a.id)}<option value={a.id}>{a.name}</option>{/each}
      </select>
      {#if machines.length}
        <select bind:value={machine} aria-label="Machine">
          <option value="">This Mac</option>
          {#each machines as name (name)}<option value={name}>{name}</option>{/each}
        </select>
      {/if}
      <input bind:value={repoUrl} placeholder="https://github.com/owner/repo" />
      <button type="submit" disabled={!token || !repoUrl.trim() || !agent}>New thread</button>
    </form>
    <ul>
      {#each threads as thread (thread.threadId)}
        <li>
          <button class="thread" class:active={thread.threadId === openId} onclick={() => (openId = thread.threadId)}>
            <span class="title">{thread.title ?? thread.repoUrl.replace('https://github.com/', '')}</span>
            <span class="when">{new Date(thread.updatedAt).toLocaleString()}{` · ${thread.agent}`}{thread.machine ? ` · ${thread.machine}` : ''}</span>
          </button>
        </li>
      {/each}
    </ul>
  </aside>

  <main>
    {#if open && token}
      {#key open.threadId}
        <Conversation thread={open} {token} {onchat} onturn={() => void refresh(token!)} onclose={() => (openId = null)} />
      {/key}
    {:else}
      <div class="empty">Pick a thread or start a new one.</div>
    {/if}
  </main>

  <footer class="status">
    <span class="dot {backend.state}"></span>
    <span>backend {backend.state}</span>
    <span>sockets {counters.sockets}</span>
    <span>chats {counters.chats}</span>
    <span>fetches {counters.fetches}</span>
    <span>timers {counters.timers}</span>
    <span>frames {counters.frames}</span>
    <span>rows {counters.rows}</span>
    <span>tauri {counters.tauri}</span>
    {#if query.get('counters') === '1'}<span>lag {lag.worst5s}ms</span><span>keys {lag.keyMedian}/{lag.keyWorst}ms</span>{/if}
    <span>remeasured {measures.moved}/{measures.measured}</span>
    <button class="link" onclick={() => void drop()}>drop sockets</button>
    <button class="link" onclick={() => void sampleMemory()}>memory</button>
    {#if memory}<span>{memory}</span>{/if}
    {#if error}<span class="error">{error}</span>{/if}
  </footer>
</div>

<style>
  :global(:root) {
    --bg: #fbfbfa;
    --panel: #ffffff;
    --side: #f3f3f1;
    --line: #e2e2de;
    --text: #1f2328;
    --muted: #6b6f76;
    --accent: #2f6fde;
    --danger: #c0392b;
    --user-bg: #eef2f8;
    --code-bg: #f4f4f2;
  }
  /* Only the thread list and the conversation scroll; the page never does,
     even when a wheel lands outside them on a short thread. */
  :global(html),
  :global(body) {
    height: 100%;
    overflow: hidden;
  }
  :global(body) {
    margin: 0;
    font: 14px/1.45 -apple-system, BlinkMacSystemFont, 'Helvetica Neue', sans-serif;
    color: var(--text);
    background: var(--bg);
  }
  :global(button) {
    font: inherit;
    padding: 8px 14px;
    border-radius: 8px;
    border: 1px solid var(--accent);
    background: var(--accent);
    color: white;
    cursor: pointer;
  }
  :global(button:disabled) {
    opacity: 0.5;
    cursor: default;
  }
  :global(button.ghost) {
    background: transparent;
    color: var(--text);
    border-color: var(--line);
  }
  .app {
    display: grid;
    grid-template-columns: 300px 1fr;
    grid-template-rows: 1fr auto;
    height: 100vh;
  }
  aside {
    background: var(--side);
    border-right: 1px solid var(--line);
    overflow-y: auto;
    padding: 14px;
  }
  form {
    display: grid;
    gap: 8px;
    margin-bottom: 14px;
  }
  input,
  select {
    font: inherit;
    padding: 7px 10px;
    border: 1px solid var(--line);
    border-radius: 8px;
    background: var(--panel);
    color: inherit;
  }
  ul {
    list-style: none;
    margin: 0;
    padding: 0;
  }
  .thread {
    width: 100%;
    text-align: left;
    display: grid;
    gap: 2px;
    background: transparent;
    color: var(--text);
    border: 1px solid transparent;
    margin-bottom: 2px;
  }
  .thread:hover {
    background: var(--panel);
  }
  .thread.active {
    background: var(--panel);
    border-color: var(--line);
  }
  .title {
    font-weight: 500;
    overflow-wrap: anywhere;
  }
  .when {
    color: var(--muted);
    font-size: 12px;
  }
  main {
    min-height: 0;
    background: var(--bg);
  }
  .empty {
    color: var(--muted);
    display: grid;
    place-items: center;
    height: 100%;
  }
  .status {
    grid-column: 1 / -1;
    display: flex;
    gap: 18px;
    align-items: center;
    padding: 6px 14px;
    border-top: 1px solid var(--line);
    background: var(--side);
    color: var(--muted);
    font-size: 12px;
    font-variant-numeric: tabular-nums;
  }
  .dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--danger);
  }
  .dot.up {
    background: #2e9b57;
  }
  .error {
    color: var(--danger);
  }
  .link {
    background: none;
    border: none;
    padding: 0;
    color: var(--accent);
    font-size: 12px;
  }
</style>
