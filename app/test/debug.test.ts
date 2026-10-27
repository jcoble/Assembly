import assert from 'node:assert/strict';
import { test } from 'node:test';
import { clientUrl, discloseUrl, libModule, runesModule, svelteUrl } from './runes.ts';

test('delay() started inside an effect does not make the effect rerun on the timer counter', async () => {
  const debugUrl = libModule('debug.svelte.ts');
  // The conversation's live clock: once a run is active, an effect starts a
  // counted delay and aborts it on cleanup (Conversation.svelte, tick()).
  const harness = runesModule(
    `
    import { flushSync } from 'svelte';
    import { counters, delay } from '${debugUrl}';
    export function run() {
      let runId = $state(null);
      let runs = 0;
      const stop = $effect.root(() => {
        $effect(() => {
          if (!runId) return;
          runs++;
          const timer = new AbortController();
          delay(1000, timer.signal).catch(() => {});
          return () => timer.abort();
        });
      });
      flushSync();
      runId = 'run-1';
      flushSync();
      const during = { runs, timers: counters.timers };
      stop();
      return { during, after: counters.timers };
    }
    `,
    'harness.svelte.js',
  );
  const { run } = (await import(harness)) as {
    run: () => { during: { runs: number; timers: number }; after: number };
  };
  assert.deepEqual(run(), { during: { runs: 1, timers: 1 }, after: 0 });
});

test('a TrackedWebSocket opened inside an effect does not make the effect rerun on the socket counter', async () => {
  const debugUrl = libModule('debug.svelte.ts');
  const harness = runesModule(
    `
    import { flushSync } from 'svelte';
    import { counters, TrackedWebSocket } from '${debugUrl}';
    export function run() {
      let open = $state(false);
      let runs = 0;
      const stop = $effect.root(() => {
        $effect(() => {
          if (!open) return;
          runs++;
          const ws = new TrackedWebSocket('ws://127.0.0.1:9');
          ws.onerror = () => {};
          return () => ws.close();
        });
      });
      flushSync();
      open = true;
      flushSync();
      const during = { runs, sockets: counters.sockets };
      stop();
      return during;
    }
    `,
    'harness-socket.svelte.js',
  );
  const { run } = (await import(harness)) as { run: () => { runs: number; sockets: number } };
  assert.deepEqual(run(), { runs: 1, sockets: 1 });
});

test('the chats counter holds at one open conversation across session switches', async () => {
  const { Window } = await import('happy-dom');
  const { compile } = await import('svelte/compiler');
  const debugUrl = libModule('debug.svelte.ts');
  const component = (source: string, filename: string) => {
    const { js } = compile(source, { generate: 'client', filename });
    const code = js.code
      .replaceAll("'svelte/internal/client'", `'${clientUrl}'`)
      .replaceAll("'svelte/internal/disclose-version'", `'${discloseUrl}'`)
      .replaceAll("from 'svelte'", `from '${svelteUrl}'`);
    return `data:text/javascript;base64,${Buffer.from(code).toString('base64')}`;
  };
  // A conversation counts itself as Conversation.svelte does; the page keys it by session.
  const chat = component(
    `<script>import { onDestroy } from 'svelte'; import { chatOpened, chatClosed } from '${debugUrl}'; let { id } = $props(); chatOpened(); onDestroy(chatClosed);</script><p>{id}</p>`,
    'Chat.svelte',
  );
  const page = component(
    `<script>import Chat from '${chat}'; let { open } = $props(); let id = $state('a'); open.to = (next) => (id = next);</script>{#key id}<Chat {id} />{/key}`,
    'Page.svelte',
  );

  const window = new Window();
  const g = globalThis as Record<string, unknown>;
  const names = ['window', 'document', 'Node', 'Element', 'Text', 'Comment', 'DocumentFragment', 'HTMLElement'] as const;
  for (const name of names) g[name] = window[name];
  try {
    const { mount, unmount, flushSync } = (await import(svelteUrl)) as typeof import('svelte');
    const { counters } = (await import(debugUrl)) as { counters: { chats: number } };
    const open: { to?: (id: string) => void } = {};
    const app = mount((await import(page)).default, { target: window.document.body as unknown as Element, props: { open } });
    flushSync();
    const seen = [counters.chats];
    for (const id of ['b', 'c', 'd']) {
      open.to!(id);
      flushSync();
      seen.push(counters.chats);
    }
    void unmount(app);
    flushSync();
    assert.deepEqual({ seen, closed: counters.chats }, { seen: [1, 1, 1, 1], closed: 0 });
  } finally {
    for (const name of names) delete g[name];
    await window.happyDOM.close();
  }
});

test('the timers counter holds at one pending delay across session switches', async () => {
  const { Window } = await import('happy-dom');
  const { compile } = await import('svelte/compiler');
  const debugUrl = libModule('debug.svelte.ts');
  const component = (source: string, filename: string) => {
    const { js } = compile(source, { generate: 'client', filename });
    const code = js.code
      .replaceAll("'svelte/internal/client'", `'${clientUrl}'`)
      .replaceAll("'svelte/internal/disclose-version'", `'${discloseUrl}'`)
      .replaceAll("from 'svelte'", `from '${svelteUrl}'`);
    return `data:text/javascript;base64,${Buffer.from(code).toString('base64')}`;
  };
  // A conversation starts a counted delay on mount and aborts it on destroy, as its live clock does.
  const chat = component(
    `<script>import { onDestroy } from 'svelte'; import { delay } from '${debugUrl}'; let { id } = $props(); const owner = new AbortController(); delay(60000, owner.signal).catch(() => {}); onDestroy(() => owner.abort());</script><p>{id}</p>`,
    'Chat.svelte',
  );
  const page = component(
    `<script>import Chat from '${chat}'; let { open } = $props(); let id = $state('a'); open.to = (next) => (id = next);</script>{#key id}<Chat {id} />{/key}`,
    'Page.svelte',
  );

  const window = new Window();
  const g = globalThis as Record<string, unknown>;
  const names = ['window', 'document', 'Node', 'Element', 'Text', 'Comment', 'DocumentFragment', 'HTMLElement'] as const;
  for (const name of names) g[name] = window[name];
  try {
    const { mount, unmount, flushSync } = (await import(svelteUrl)) as typeof import('svelte');
    const { counters } = (await import(debugUrl)) as { counters: { timers: number } };
    const open: { to?: (id: string) => void } = {};
    const app = mount((await import(page)).default, { target: window.document.body as unknown as Element, props: { open } });
    flushSync();
    const seen = [counters.timers];
    for (const id of ['b', 'c', 'd']) {
      open.to!(id);
      flushSync();
      seen.push(counters.timers);
    }
    void unmount(app);
    flushSync();
    assert.deepEqual({ seen, closed: counters.timers }, { seen: [1, 1, 1, 1], closed: 0 });
  } finally {
    for (const name of names) delete g[name];
    await window.happyDOM.close();
  }
});
