import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { compile, compileModule } from 'svelte/compiler';

// Svelte's client runtime, as the webview loads it (node would pick the server build for 'svelte').
const svelteDir = new URL('../node_modules/svelte/src/', import.meta.url).href;
const lib = new URL('../src/lib/', import.meta.url);
const dataUrl = (code: string) => `data:text/javascript;base64,${Buffer.from(code).toString('base64')}`;

// Points a compiled module's imports at files node can load: the client
// runtime, the virtualizer's build, stubs for the network and child
// components, compiled runes modules, and the plain .ts modules as they are.
function wire(code: string, stubs: Record<string, string>): string {
  return code.replace(/(from |import )'([^']+)'/g, (_, kw: string, spec: string) => {
    const to =
      spec === 'svelte'
        ? `${svelteDir}index-client.js`
        : spec === 'svelte/internal/client'
          ? `${svelteDir}internal/client/index.js`
          : spec === 'svelte/internal/disclose-version'
            ? `${svelteDir}internal/disclose-version.js`
            : spec === 'svelte/reactivity'
              ? `${svelteDir}reactivity/index-client.js`
              : spec === '@tanstack/svelte-virtual'
                ? new URL('../node_modules/@tanstack/svelte-virtual/dist/index.js', import.meta.url).href
                : (stubs[spec] ?? (spec.endsWith('.svelte') ? stubs.component : new URL(`${spec.replace(/\.ts$/, '')}.ts`, lib).href));
    return `${kw}'${to}'`;
  });
}
const runes = (file: string, stubs: Record<string, string>) =>
  dataUrl(wire(compileModule(stripTypeScriptTypes(readFileSync(new URL(file, lib), 'utf8')), { generate: 'client', filename: file }).js.code, stubs));

test('Conversation runs its whole script before its template, reading nothing before it is declared', async () => {
  const stubs: Record<string, string> = {
    // A child component draws nothing; the conversation's first page never lands.
    component: dataUrl('export default function () {}'),
    './api': dataUrl(
      ['rowsPage', 'sendText', 'stopRun', 'toolDetail', 'fetchAttachment', 'listAgents', 'answerAsk', 'stopTask', 'getSession'].map((n) => `export const ${n} = () => new Promise(() => {});`).join('\n'),
    ),
    './rows/client': dataUrl('export const openLive = () => {};'),
    './host/pageCall': dataUrl('export const pageCall = () => new Promise(() => {});'),
  };
  stubs['./debug.svelte'] = runes('debug.svelte.ts', stubs);
  stubs['./rows/kept.svelte'] = runes('rows/kept.svelte.ts', stubs);
  const source = readFileSync(new URL('Conversation.svelte', lib), 'utf8');
  const { js } = compile(source, { generate: 'client', filename: 'Conversation.svelte' });
  const { default: Conversation } = (await import(dataUrl(wire(js.code, stubs)))) as { default: (anchor: unknown, props: unknown) => void };
  const { effect_root } = (await import(`${svelteDir}internal/client/index.js`)) as { effect_root: (fn: () => void) => () => void };

  // The template is the first thing that needs a document; reaching it means the script ran to its end.
  const TEMPLATE = new Error('template reached');
  const g = globalThis as { document?: unknown };
  g.document = new Proxy({}, { get: () => { throw TEMPLATE; } });
  let thrown: unknown;
  try {
    const props = { session: { sessionId: 't-1', repoUrl: 'https://github.com/o/r', agent: 'claude' }, token: 'x', onclose() {} };
    effect_root(() => {
      try {
        Conversation({}, props);
      } catch (error) {
        thrown = error;
      }
    })();
  } finally {
    delete g.document;
  }
  assert.equal(thrown, TEMPLATE);
});
