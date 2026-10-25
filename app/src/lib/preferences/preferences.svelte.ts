import { call, save, unsavedKey } from '../call';

// Every preference and its default. A feature adds its keys here; the backend keeps the values the page saved.
const defaults = {
  theme: 'system' as 'system' | 'light' | 'dark',
};
export type Preferences = typeof defaults;

// One reactive value per key, so a change redraws only what reads that key.
export const preferences: Preferences = $state({ ...defaults });

// The saved values over the defaults, once at launch. A key changed here before the load answered keeps its new
// value, and a saved key the page no longer has is left out.
export async function loadPreferences(): Promise<void> {
  const saved = await call<Record<string, unknown>>('preferences', 'get', {}, { key: 'preferences/get' });
  if (!saved.ok) return;
  for (const key of Object.keys(defaults))
    if (key in saved.result && !unsavedKey('preferences', key)) (preferences as Record<string, unknown>)[key] = saved.result[key];
}

// Changes one preference now; call.ts saves it 300 ms after the last change.
export function setPreference<K extends keyof Preferences>(key: K, value: Preferences[K]): void {
  preferences[key] = value;
  save('preferences', key, value);
}
