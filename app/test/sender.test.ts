import assert from 'node:assert/strict';
import { test } from 'node:test';
import { senderLabel } from '../src/lib/chat/sender.ts';
import type { Session } from '../src/lib/api.ts';

const agents = [{ id: 'claude', name: 'Claude Code' }, { id: 'codex', name: 'Codex' }];
const session = (extra: Partial<Session>): Session => ({ sessionId: 't', agent: 'codex', repoUrl: 'r', title: null, machine: null, updatedAt: 0, ...extra });

test("a message from another session is labelled with its agent and machine, not as the person's", () => {
  assert.equal(senderLabel(session({ parentSessionId: 'p', machine: 'workbox-test' }), agents), 'Helper · Codex on workbox-test');
  assert.equal(senderLabel(session({ parentSessionId: 'p', agent: 'claude' }), agents), 'Helper · Claude Code');
  assert.equal(senderLabel(session({}), agents), 'Session · Codex');
});

test('actual Message sender button keeps captured label separate from open target and plain labels cannot click', async () => {
  const { Window } = await import('happy-dom');
  const { compile } = await import('svelte/compiler');
  const { readFileSync } = await import('node:fs');
  const { clientUrl, discloseUrl, svelteUrl, jsModule } = await import('./runes.ts');
  const source = readFileSync(new URL('../src/lib/chat/Message.svelte', import.meta.url), 'utf8');
  const { js } = compile(source, { generate: 'client', filename: 'Message.svelte' });
  const url = jsModule(js.code.replaceAll("'svelte/internal/client'", `'${clientUrl}'`).replaceAll("'svelte/internal/disclose-version'", `'${discloseUrl}'`).replaceAll("from 'svelte'", `from '${svelteUrl}'`));
  const window = new Window(); const g = globalThis as Record<string, unknown>;
  const names = ['window', 'document', 'Node', 'Element', 'Text', 'Comment', 'DocumentFragment', 'HTMLElement'] as const;
  for (const name of names) g[name] = window[name];
  try {
    const { mount, unmount, flushSync } = await import(svelteUrl) as typeof import('svelte');
    const Message = (await import(url)).default;
    const opened: string[] = [];
    for (const sender of [{ label: 'Captured <title> (peer/x)', open: () => opened.push('peer/x') }, { label: 'Assembly' }, { label: 'Another session', open: () => opened.push('peer/x') }]) {
      const app = mount(Message, { target: window.document.body as unknown as Element, props: { role: 'user', sender } }); flushSync();
      const button = window.document.querySelector('button');
      assert.equal(window.document.querySelector('.role')?.textContent, sender.label);
      assert.equal(button !== null, 'open' in sender);
      button?.click(); assert.equal(window.document.querySelector('title'), null);
      void unmount(app); flushSync();
    }
    assert.deepEqual(opened, ['peer/x', 'peer/x']);
  } finally { for (const name of names) delete g[name]; await window.happyDOM.close(); }
});

test('actual TimelineItem opens the stable From ID, prefers captured labels and leaves system labels plain', async () => {
  const { Window } = await import('happy-dom'); const { compile } = await import('svelte/compiler');
  const { readFileSync } = await import('node:fs'); const { clientUrl, discloseUrl, svelteUrl, jsModule } = await import('./runes.ts');
  const component = (source: string, filename: string, imports: Record<string, string> = {}) => {
    let code = compile(source, { generate: 'client', filename, runes: true }).js.code.replaceAll("'svelte/internal/client'", `'${clientUrl}'`).replaceAll("'svelte/internal/disclose-version'", `'${discloseUrl}'`).replaceAll("from 'svelte'", `from '${svelteUrl}'`);
    for (const [from, to] of Object.entries(imports)) code = code.replaceAll(`from '${from}'`, `from '${to}'`);
    return jsModule(code);
  };
  const message = component(readFileSync(new URL('../src/lib/chat/Message.svelte', import.meta.url), 'utf8'), 'Message.svelte');
  const unused = component('<span />', 'Unused.svelte');
  const tools = jsModule("export const SESSION_OPEN = Symbol('session-open');");
  const source = readFileSync(new URL('../src/lib/session/TimelineItem.svelte', import.meta.url), 'utf8');
  const imports: Record<string, string> = {};
  for (const match of source.matchAll(/from '([^']+\.svelte)'/g)) imports[match[1]!] = match[1] === '../chat/Message.svelte' ? message : unused;
  imports['./turns'] = jsModule('export const checkoutLine = () => null; export const turnLine = () => "";');
  imports['../chat/tools/tool'] = tools; imports['../chat/notices'] = jsModule('export const errorNotice = () => null;');
  const timeline = component(source, 'TimelineItem.svelte', imports);
  const wrapper = component(`<script>import {setContext} from 'svelte'; import Timeline from '${timeline}'; import {SESSION_OPEN} from '${tools}'; let {item,opened}=$props(); setContext(SESSION_OPEN,id=>opened.push(id));</script><Timeline {item} label={true} kept={{}} meta={null} speaker="Agent" senders={{'peer/x':'Legacy fallback'}} stats={{}} now={0} plain="prompts" blocks={undefined} />`, 'SenderTimeline.svelte');
  const window = new Window(); const g = globalThis as Record<string, unknown>; const names = ['window', 'document', 'Node', 'Element', 'Text', 'Comment', 'DocumentFragment', 'HTMLElement'] as const;
  for (const name of names) g[name] = window[name];
  try {
    const { mount, unmount, flushSync } = await import(svelteUrl) as typeof import('svelte'); const opened: string[] = [];
    for (const [extra, label, clickable] of [
      [{from:'peer/x',senderLabel:'Captured <title> (peer/x)'}, 'Captured <title> (peer/x)', true],
      [{senderLabel:'Assembly'}, 'Assembly', false],
      [{from:'peer/x'}, 'Legacy fallback', true],
    ] as const) {
      const app = mount((await import(wrapper)).default, {target:window.document.body as unknown as Element, props:{item:{kind:'user',key:'u',text:'body',at:1,...extra},opened}}); flushSync();
      assert.equal(window.document.querySelector('.role')?.textContent, label); const button = window.document.querySelector('button'); assert.equal(button !== null, clickable); button?.click();
      void unmount(app); flushSync();
    }
    assert.deepEqual(opened, ['peer/x','peer/x']);
  } finally {for (const name of names) delete g[name]; await window.happyDOM.close();}
});
