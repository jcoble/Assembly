import assert from 'node:assert/strict';
import { test } from 'node:test';
import { jsModule, libModule } from '../../runes.ts';

type Preferences = {
  preferences: Record<string, unknown>;
  loadPreferences: () => Promise<void>;
  setPreference: (key: string, value: unknown) => void;
};

// A fresh preferences module on a fake call.ts that records saves and answers `saved` to preferences/get.
let copies = 0;
async function load(saved: Record<string, unknown> = {}) {
  const saves: unknown[][] = [];
  (globalThis as { preferenceSaves?: unknown[][] }).preferenceSaves = saves;
  const fake = jsModule(`
    export const call = async (feature, method) => (method === 'get' ? ${JSON.stringify(saved)} : undefined);
    export const save = (...args) => globalThis.preferenceSaves.push(args);
    // copy ${++copies}`);
  const module = (await import(libModule('preferences/preferences.svelte.ts', { '../call': fake }))) as Preferences;
  return { saves, ...module };
}

// Pins: base#7
test('a change shows at once and goes to the shared save', async () => {
  const { preferences, setPreference, saves } = await load();
  setPreference('theme', 'dark');
  assert.equal(preferences.theme, 'dark');
  assert.deepEqual(saves, [['preferences', 'theme', 'dark']]);
});

// Pins: base#7
test('saved values replace the defaults when the app loads', async () => {
  const { preferences, loadPreferences } = await load({ theme: 'dark', gone: 1 });
  assert.equal(preferences.theme, 'system');
  await loadPreferences();
  assert.equal(preferences.theme, 'dark');
  assert.equal('gone' in preferences, false);
});
