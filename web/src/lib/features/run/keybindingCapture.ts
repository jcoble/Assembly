/**
 * keybindingCapture.ts — turning a keypress into a saved shortcut, and back.
 *
 * The written form is `Cmd+Ctrl+Alt+Shift+Key`: modifiers always in that order
 * whichever order they were held in, then the key itself. A single letter is
 * upper-cased, a named key keeps its name (`Enter`, `ArrowUp`, `F5`), and the
 * space bar is written `Space`.
 *
 * Pure module: no DOM beyond the event handed in, no state, type-only imports.
 */

import type { RunDefinition } from './types';

/** The keys that only ever qualify another key. */
const MODIFIER_KEYS: ReadonlySet<string> = new Set([
  'Shift',
  'Control',
  'Alt',
  'Meta',
  'CapsLock',
  'OS'
]);

/** Modifier names in canonical serialization order. */
const MODIFIER_ORDER = ['Cmd', 'Ctrl', 'Alt', 'Shift'] as const;

/** Accepted spellings for each modifier when normalizing saved values. */
const MODIFIER_ALIASES: Record<string, (typeof MODIFIER_ORDER)[number]> = {
  cmd: 'Cmd',
  command: 'Cmd',
  meta: 'Cmd',
  super: 'Cmd',
  ctrl: 'Ctrl',
  control: 'Ctrl',
  alt: 'Alt',
  option: 'Alt',
  opt: 'Alt',
  shift: 'Shift'
};

/** True when the event is only a modifier key being held. */
export function isModifierOnly(event: KeyboardEvent): boolean {
  return MODIFIER_KEYS.has(event.key);
}

function keyName(key: string): string {
  if (key === ' ' || key === 'Spacebar') return 'Space';
  if (key.length === 1) return key.toUpperCase();
  return key;
}

/** Normalized shortcut string for a keydown event, or `null` for modifier-only events. */
export function formatKeybinding(event: KeyboardEvent): string | null {
  if (isModifierOnly(event)) return null;
  const key = keyName(event.key);
  if (!key) return null;
  const parts: string[] = [];
  if (event.metaKey) parts.push('Cmd');
  if (event.ctrlKey) parts.push('Ctrl');
  if (event.altKey) parts.push('Alt');
  if (event.shiftKey) parts.push('Shift');
  parts.push(key);
  return parts.join('+');
}

/** Canonicalize a saved shortcut string so `shift + cmd + r` matches `Cmd+Shift+R`. */
export function normalizeKeybinding(value: string | null | undefined): string | null {
  if (typeof value !== 'string') return null;
  const pieces = value
    .split('+')
    .map((piece) => piece.trim())
    .filter((piece) => piece !== '');
  if (pieces.length === 0) return null;
  const held = new Set<(typeof MODIFIER_ORDER)[number]>();
  let key: string | null = null;
  for (const piece of pieces) {
    const modifier = MODIFIER_ALIASES[piece.toLowerCase()];
    if (modifier) {
      held.add(modifier);
      continue;
    }
    key = keyName(piece);
  }
  if (!key) return null;
  const parts: string[] = MODIFIER_ORDER.filter((modifier) => held.has(modifier));
  parts.push(key);
  return parts.join('+');
}

/** Return the first saved action matching `event`, or `null`. */
export function matchKeybinding(
  event: KeyboardEvent,
  definitions: readonly RunDefinition[]
): RunDefinition | null {
  const pressed = formatKeybinding(event);
  if (!pressed) return null;
  for (const definition of definitions) {
    if (normalizeKeybinding(definition.keybinding) === pressed) return definition;
  }
  return null;
}
