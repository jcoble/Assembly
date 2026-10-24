/**
 * settingsStore.test.ts — the settings that survive a restart, run in plain node.
 *
 * `settingsStore.svelte.ts` is a runes module, so it is compiled the way vite
 * compiles it (strip types, Svelte compiler) and imported from a scratch
 * folder inside `node_modules`, the same recipe as `playwrightStore.test.ts`.
 * Its one native dependency, the settings row in SQLite, is replaced by a
 * small in-memory stand-in; the theme and font registries are the real ones.
 * Each test imports a fresh copy of the store, because hydration happens once
 * per app start.
 *
 * Run: node --experimental-strip-types src/lib/shell/settings/settingsStore.test.ts
 */
import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import test, { after } from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';
// The client runtime the compiled store uses; plain 'svelte' resolves to the
// server build in node, where flushSync does nothing.
// @ts-expect-error Svelte exposes this runtime entry point without type declarations.
import { flush as flushSync } from 'svelte/internal/client';
import { compileModule } from 'svelte/compiler';

type Backend = {
  read: (key: string) => Promise<unknown>;
  writes: { key: string; value: unknown }[];
};
const testGlobal = globalThis as typeof globalThis & { settingsTestBackend: Backend };

const here = (path: string): string => fileURLToPath(new URL(path, import.meta.url));
const outputDir = here('../../../../node_modules/.mcb-test-settings-store/');
mkdirSync(outputDir, { recursive: true });
after(() => rmSync(outputDir, { recursive: true, force: true }));

writeFileSync(`${outputDir}tauriSource.mjs`, `
export const readAssemblySettingFromTauri = (key) => globalThis.settingsTestBackend.read(key);
export const writeAssemblySettingFromTauri = async (key, value) => {
  globalThis.settingsTestBackend.writes.push({ key, value: JSON.parse(JSON.stringify(value)) });
};
`);
const storeSource = stripTypeScriptTypes(readFileSync(here('./settingsStore.svelte.ts'), 'utf8'), { mode: 'strip' });
const compiledStore = compileModule(storeSource, { generate: 'client', filename: 'settingsStore.svelte.js' }).js.code
  .replace(/(['"])\.\.\/\.\.\/tauriSource\1/, "'./tauriSource.mjs'")
  .replace(/(['"])\.\/themes\/fontRegistry\1/, `'${pathToFileURL(here('./themes/fontRegistry.ts')).href}'`)
  .replace(/(['"])\.\/themes\/themeRegistry\1/, `'${pathToFileURL(here('./themes/themeRegistry.ts')).href}'`);
writeFileSync(`${outputDir}settingsStore.mjs`, compiledStore);
const memorySource = stripTypeScriptTypes(readFileSync(here('../conversation/agentConfigMemory.ts'), 'utf8'), { mode: 'strip' });

let copies = 0;
/** A fresh store (and the agent-choice memory bound to it), as on app start. */
async function freshStore(read: Backend['read'] = async () => null) {
  const backend: Backend = { read, writes: [] };
  testGlobal.settingsTestBackend = backend;
  copies += 1;
  const store = await import(`${pathToFileURL(`${outputDir}settingsStore.mjs`).href}?copy=${copies}`);
  after(() => store.disposeSettingsStore());
  const memoryPath = `${outputDir}agentConfigMemory-${copies}.mjs`;
  writeFileSync(memoryPath, memorySource.replace(
    /(['"])\.\.\/settings\/settingsStore\.svelte\1/, `'./settingsStore.mjs?copy=${copies}'`
  ));
  const memory = await import(pathToFileURL(memoryPath).href);
  return { store, memory, backend };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

// Protects 085de1d05 (settings live in the native SQLite row) and the merge it uses:
// a stored row from an older or newer build loads what it can and fills the rest from defaults.
test('a stored row keeps its known values and fills missing or wrong-typed ones from defaults', async () => {
  const { store } = await freshStore(async () => ({
    appearance: { themeId: 'dracula', appFontSize: '15', retiredKey: true },
    editor: { fontSize: 16, lineHeight: null },
    terminal: 'not a section',
    unknownSection: { anything: 1 }
  }));
  await store.hydrateSettings();
  const defaults = store.defaultSettings();
  assert.equal(store.settings.appearance.themeId, 'dracula', 'a known value is kept');
  assert.equal(store.settings.appearance.appFontSize, defaults.appearance.appFontSize, 'a string where a number belongs is ignored');
  assert.equal('retiredKey' in store.settings.appearance, false, 'keys the app no longer knows are dropped');
  assert.equal(store.settings.editor.fontSize, 16);
  assert.equal(store.settings.editor.lineHeight, defaults.editor.lineHeight);
  assert.deepEqual(store.settings.terminal, defaults.terminal, 'a broken section loads as defaults');
  assert.equal('unknownSection' in store.settings, false);
});

// Protects 1be262f01 (theme), 5691ace6a (problems location) and 0bf6abb82 (fonts):
// a value no current choice matches must not
// leave the app on a theme, strip or face that does not exist.
test('an unknown theme, problems location or blank font falls back to the default', async () => {
  const { store } = await freshStore(async () => ({
    appearance: { themeId: 'theme-removed-last-year' },
    panels: { problemsLocation: 'right' },
    editor: { fontFamily: '   ' },
    terminal: { fontFamily: '' }
  }));
  await store.hydrateSettings();
  const defaults = store.defaultSettings();
  assert.equal(store.settings.appearance.themeId, defaults.appearance.themeId);
  assert.equal(store.settings.panels.problemsLocation, defaults.panels.problemsLocation);
  assert.equal(store.settings.editor.fontFamily, defaults.editor.fontFamily);
  assert.equal(store.settings.terminal.fontFamily, defaults.terminal.fontFamily);
});

// Protects bc3ed535a: turning one language server off survives a
// restart without switching the others off.
test('each stored language server switch loads on its own and missing ones stay on', async () => {
  const { store } = await freshStore(async () => ({
    intelligence: { languageServers: false, languageServerEnabled: { csharp: false, rust: 'no' } }
  }));
  await store.hydrateSettings();
  assert.equal(store.settings.intelligence.languageServers, false);
  assert.deepEqual(store.settings.intelligence.languageServerEnabled, { csharp: false, typescript: true, rust: true });
});

// Protects 43557b182: the model, effort and access last chosen for an agent
// survive a restart, which is when they were being forgotten.
test('the last choice for each agent survives a restart', async () => {
  const saved = { codex: { model: 'gpt-x', reasoningEffort: 'high', approvalPolicy: 'on-request' } };
  const { store, memory } = await freshStore(async () => ({ agents: { lastChoiceByProvider: saved } }));
  await store.hydrateSettings();
  assert.deepEqual(memory.rememberedAgentConfigChoice('codex'), saved.codex);
  assert.equal(memory.rememberedAgentConfigChoice('claude'), null, 'nothing is invented for another agent');
});

// Protects 43557b182: changing only the effort keeps the model and access already chosen,
// one agent's choice never touches another's, and the change is saved.
test('remembering one field keeps the others and leaves other agents alone', async () => {
  const { store, memory, backend } = await freshStore();
  await store.hydrateSettings();
  memory.rememberAgentConfigChoice('claude', { model: 'opus', reasoningEffort: 'medium', approvalPolicy: 'default' });
  memory.rememberAgentConfigChoice('codex', { model: 'gpt-x' });
  memory.rememberAgentConfigChoice('claude', { reasoningEffort: 'high' });
  memory.rememberAgentConfigChoice('claude', { approvalPolicy: null });
  assert.deepEqual(memory.rememberedAgentConfigChoice('claude'), { model: 'opus', reasoningEffort: 'high', approvalPolicy: null });
  assert.deepEqual(memory.rememberedAgentConfigChoice('codex'), { model: 'gpt-x', reasoningEffort: null, approvalPolicy: null });
  flushSync();
  const last = backend.writes.at(-1);
  assert.equal(last?.key, store.SETTINGS_SETTING_KEY);
  assert.deepEqual((last?.value as { agents: unknown }).agents, {
    lastChoiceByProvider: {
      claude: { model: 'opus', reasoningEffort: 'high', approvalPolicy: null },
      codex: { model: 'gpt-x', reasoningEffort: null, approvalPolicy: null }
    }
  });
});

// Protects 085de1d05 and fd487f8f4: the late SQLite answer must not replace a choice the user
// made while it was loading, and that choice is saved once loading ends.
test('a change made while settings load is kept and saved, not replaced by the stored row', async () => {
  const read = deferred<unknown>();
  const { store, backend } = await freshStore(() => read.promise);
  const hydration = store.hydrateSettings();
  store.updateSettings('editor', { fontSize: 19 });
  flushSync();
  read.resolve({ editor: { fontSize: 11 }, appearance: { themeId: 'dracula' } });
  await hydration;
  flushSync();
  assert.equal(store.settings.editor.fontSize, 19);
  assert.equal(store.settings.appearance.themeId, store.defaultSettings().appearance.themeId);
  assert.equal(backend.writes.length, 1, 'the choice is written once loading finishes');
  assert.equal((backend.writes[0].value as { editor: { fontSize: number } }).editor.fontSize, 19);
});

// Protects 085de1d05 and fd487f8f4: overlapping callers share one read, and loading the row
// does not write the same row straight back.
test('overlapping loads share one read and loading never writes the row back', async () => {
  let reads = 0;
  const read = deferred<unknown>();
  const { store, backend } = await freshStore(() => { reads += 1; return read.promise; });
  const first = store.hydrateSettings();
  const second = store.hydrateSettings();
  read.resolve({ editor: { fontSize: 17 } });
  await Promise.all([first, second]);
  flushSync();
  assert.equal(reads, 1);
  assert.equal(store.settings.editor.fontSize, 17);
  assert.equal(backend.writes.length, 0, 'loading is not a change');
  store.updateSettings('editor', { fontSize: 18 });
  flushSync();
  assert.equal(backend.writes.length, 1, 'a real change after loading is saved');
});

// Protects fd487f8f4: a failed read clears the in-flight load so the next call retries.
test('a failed read can be retried and then loads the stored row', async () => {
  let reads = 0;
  const { store } = await freshStore(async () => {
    reads += 1;
    if (reads === 1) throw new Error('database is locked');
    return { editor: { fontSize: 15 } };
  });
  await assert.rejects(store.hydrateSettings(), /database is locked/);
  await store.hydrateSettings();
  assert.equal(reads, 2);
  assert.equal(store.settings.editor.fontSize, 15);
});
