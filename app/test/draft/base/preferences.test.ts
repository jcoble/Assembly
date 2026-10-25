import assert from 'node:assert/strict';
import { test } from 'node:test';
import { jsModule, libModule } from '../../runes.ts';

type Call = { feature: string; method: string; body: Record<string, unknown> };
type Preferences = {
  preferences: Record<string, unknown>;
  loadPreferences: (signal: AbortSignal) => Promise<void>;
  setPreference: (key: string, value: unknown) => void;
};

// The preferences module with a fake api.ts that records each call and answers `saved` to preferences/get.
async function load(saved: Record<string, unknown> = {}) {
  const calls: Call[] = [];
  (globalThis as { preferenceCalls?: Call[] }).preferenceCalls = calls;
  const api = jsModule(`
    export async function call(feature, method, body = {}) {
      globalThis.preferenceCalls.push({ feature, method, body });
      return method === 'get' ? ${JSON.stringify(saved)} : null;
    }`);
  const module = (await import(libModule('preferences/preferences.svelte.ts', { '../debug.svelte': libModule('debug.svelte.ts'), '../api': api }))) as Preferences;
  return { calls, ...module };
}

const settle = () => new Promise<void>((resolve) => setImmediate(resolve));

// Pins: base#7
test('changes save once, 300 ms after the last change, not on every keystroke', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { calls, preferences, setPreference } = await load();
  setPreference('theme', 'dark');
  t.mock.timers.tick(200);
  setPreference('theme', 'light');
  t.mock.timers.tick(299);
  await settle();
  assert.equal(preferences.theme, 'light');
  assert.deepEqual(calls, []);
  t.mock.timers.tick(1);
  await settle();
  assert.deepEqual(calls, [{ feature: 'preferences', method: 'set', body: { values: { theme: 'light' } } }]);
});

// Pins: base#7
test('saved values replace the defaults when the app loads', async () => {
  const { preferences, loadPreferences } = await load({ theme: 'dark' });
  assert.equal(preferences.theme, 'system');
  await loadPreferences(new AbortController().signal);
  assert.equal(preferences.theme, 'dark');
});
