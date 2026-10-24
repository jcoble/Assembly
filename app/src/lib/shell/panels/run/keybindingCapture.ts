/**
 * keybindingCapture.ts — turning a keypress into a saved shortcut, and back.
 *
 * A run action can carry a key combination that starts it from anywhere in the
 * app. Two things need the same answer for that to work: the field the user
 * presses the combination into has to WRITE it the same way every time, and the
 * app-wide key listener has to READ a saved one back and recognise the same
 * press. Both live here, so there is one spelling and one comparison.
 *
 * The written form is `Cmd+Ctrl+Alt+Shift+Key`: modifiers always in that order
 * whichever order they were held in, then the key itself. A single letter is
 * upper-cased, a named key keeps its name (`Enter`, `ArrowUp`, `F5`), and the
 * space bar is written `Space` because a bare space in the middle of a saved
 * string is unreadable.
 *
 * PURE: no DOM beyond the event it is handed, no state, no imports that run.
 * `scripts/runActions.test.ts` exercises it in plain node with plain objects.
 */
import type { StackDefinition } from '../../stacks/stackStore.svelte.ts';

/** The four keys that only ever qualify another one. */
const MODIFIER_KEYS: ReadonlySet<string> = new Set([
  'Shift',
  'Control',
  'Alt',
  'Meta',
  'CapsLock',
  'OS'
]);

/** Modifier names as they are written, in the order they are written. */
const MODIFIER_ORDER = ['Cmd', 'Ctrl', 'Alt', 'Shift'] as const;

/** The spellings a saved value may use for each modifier. */
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

/**
 * True when the event is only a modifier being held, so a capture field should
 * keep waiting rather than saving `Shift` as somebody's shortcut.
 */
export function isModifierOnly(event: KeyboardEvent): boolean {
  return MODIFIER_KEYS.has(event.key);
}

/** The key itself, written the way it is saved. */
function keyName(key: string): string {
  if (key === ' ' || key === 'Spacebar') return 'Space';
  if (key.length === 1) return key.toUpperCase();
  return key;
}

/**
 * A captured combination, normalized — or `null` when there is nothing to
 * capture yet because only modifiers are down.
 */
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

/**
 * A saved combination in the one written form, so `cmd + shift + r` and
 * `Cmd+Shift+R` are recognised as the same shortcut. An unusable value — empty,
 * or nothing but modifiers — comes back as `null` and simply never matches.
 */
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
    // The last non-modifier piece is the key; a value naming two is malformed
    // and the later one is what a reader would expect to win.
    key = keyName(piece);
  }
  if (!key) return null;
  const parts: string[] = MODIFIER_ORDER.filter((modifier) => held.has(modifier));
  parts.push(key);
  return parts.join('+');
}

/**
 * Which saved action a keydown should run, or `null` when none does.
 *
 * First match wins: two actions may end up sharing a combination, and running
 * both would be worse than running the one nearest the top of the list.
 */
export function matchKeybinding(
  event: KeyboardEvent,
  definitions: readonly StackDefinition[]
): StackDefinition | null {
  const pressed = formatKeybinding(event);
  if (!pressed) return null;
  for (const definition of definitions) {
    if (normalizeKeybinding(definition.keybinding) === pressed) return definition;
  }
  return null;
}
