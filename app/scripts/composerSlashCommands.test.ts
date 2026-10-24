/**
 * composerSlashCommands.test.mjs — the slash menu model behind the
 * conversation composer.
 *
 * The composer opens a command menu when the message starts with a slash.
 * These checks pin when the menu is open, what it lists, how the keyboard
 * moves through it, and what the chosen row puts back into the draft.
 *
 * Run: node --experimental-strip-types scripts/composerSlashCommands.test.mjs
 */
import assert from 'node:assert/strict';
import {
  contextMeterPopover,
  contextMeterState,
  draftAfterSlashCommand,
  formatContextTokens,
  moveSlashMenuIndex,
  remainingContextPercent,
  ringDash,
  slashCommandQuery,
  slashMenuState,
  snapshotConversationCommands
} from '../src/lib/shell/conversation/composerSlashCommands.ts';
import { mergeConversationCommandCatalog } from '../src/lib/shell/conversation/conversationCommandCatalog.ts';

const catalog = mergeConversationCommandCatalog(
  [
    { id: 'review', label: 'Review changes' },
    { id: 'status', label: 'Session status' },
    { id: 'compact', label: 'Compact the transcript' }
  ],
  [],
  []
);

// ── When the menu is open ──────────────────────────────────────────────────
assert.equal(slashCommandQuery(''), null, 'an empty composer has no menu');
assert.equal(slashCommandQuery('hello'), null, 'plain text has no menu');
assert.equal(slashCommandQuery('tell me about /review'), null, 'a slash mid-sentence is not a command');
assert.equal(slashCommandQuery('/'), '', 'a bare slash opens the menu with no filter');
assert.equal(slashCommandQuery('  /rev'), 'rev', 'leading spaces still count as the start');
assert.equal(slashCommandQuery('/Review'), 'Review', 'the raw text is returned, not lowercased');
assert.equal(slashCommandQuery('/review please'), null, 'once arguments start the menu closes');
assert.equal(slashCommandQuery('/review\n'), null, 'a newline ends the command word');

const firstCharacterMenu = slashMenuState('/', catalog, { activeIndex: 0, dismissed: false });
assert.equal(firstCharacterMenu.open, true, 'the first slash character opens the menu');

// ── What the menu lists ────────────────────────────────────────────────────
const opened = slashMenuState('/', catalog, { activeIndex: 0, dismissed: false });
assert.equal(opened.open, true);
assert.deepEqual(opened.commands.map((command) => command.name), ['review', 'status', 'compact']);
assert.equal(opened.activeIndex, 0);
assert.equal(opened.emptyText, '');

const filtered = slashMenuState('/st', catalog, { activeIndex: 3, dismissed: false });
assert.deepEqual(filtered.commands.map((command) => command.name), ['status']);
assert.equal(filtered.activeIndex, 0, 'a stale index is clamped to the shortened list');

const noMatch = slashMenuState('/zzz', catalog, { activeIndex: 0, dismissed: false });
assert.equal(noMatch.open, true, 'the menu stays open so the user sees why nothing matched');
assert.deepEqual(noMatch.commands, []);
assert.equal(noMatch.emptyText, 'No commands match');

const noCommands = slashMenuState('/', [], { activeIndex: 0, dismissed: false });
assert.equal(noCommands.open, true);
assert.equal(noCommands.emptyText, 'No commands', 'an agent that advertises nothing says so');

const dismissed = slashMenuState('/rev', catalog, { activeIndex: 0, dismissed: true });
assert.equal(dismissed.open, false, 'Escape keeps the menu shut while the draft is unchanged');

// Name matches come before description-only matches.
const ranked = slashMenuState('/compact', catalog, { activeIndex: 0, dismissed: false });
assert.deepEqual(ranked.commands.map((command) => command.name), ['compact']);

const longProviderCatalog = mergeConversationCommandCatalog(
  Array.from({ length: 14 }, (_, index) => ({
    id: `custom-${index + 1}`,
    label: `Custom ${index + 1}`,
    description: `Description ${index + 1}`
  })),
  [],
  [{ id: 'assembly-built-in', name: 'built-in', label: 'Built-in', description: 'Built-in action' }]
);
assert.equal(longProviderCatalog.length, 15, 'all provider and built-in commands survive the merge');
assert.equal(longProviderCatalog[13].description, 'Description 14');
assert.equal(longProviderCatalog[14].source, 'assembly');

const openCatalog = snapshotConversationCommands(catalog);
catalog[0].label = 'Streamed replacement';
catalog.push({ ...catalog[0], id: 'late', name: 'late' });
assert.equal(openCatalog[0]?.label, 'Review changes');
assert.equal(openCatalog.length, 3, 'an open slash menu does not follow streamed command updates');

// ── Keyboard movement ──────────────────────────────────────────────────────
assert.equal(moveSlashMenuIndex(0, 3, 'ArrowDown'), 1);
assert.equal(moveSlashMenuIndex(2, 3, 'ArrowDown'), 0, 'the list wraps to the top');
assert.equal(moveSlashMenuIndex(0, 3, 'ArrowUp'), 2, 'the list wraps to the bottom');
assert.equal(moveSlashMenuIndex(1, 0, 'ArrowDown'), 0, 'an empty list stays at zero');
assert.equal(moveSlashMenuIndex(1, 3, 'Enter'), 1, 'other keys leave the index alone');

// ── Choosing a row ─────────────────────────────────────────────────────────
const review = catalog.find((command) => command.name === 'review');
assert.equal(draftAfterSlashCommand('/rev', review), '/review ');
assert.equal(draftAfterSlashCommand('   /rev', review), '/review ', 'the rewritten draft drops stray leading space');
assert.equal(draftAfterSlashCommand('anything', review), '/review ');

// ── Context math ─────────────────────────────────────────────────────────
assert.equal(remainingContextPercent(120, 400), 70);
assert.equal(remainingContextPercent(401, 400), 0, 'usage over the window clamps to zero');
assert.equal(remainingContextPercent(-1, 400), 100, 'negative usage clamps to the full window');
assert.equal(remainingContextPercent(1, 0), null, 'an absent context window hides the indicator');
assert.equal(remainingContextPercent(null, 400), null);

// ── What the composer shows for context ──────────────────────
assert.deepEqual(
  contextMeterState(50_000, 200_000),
  {
    kind: 'percent',
    remaining: 75,
    used: 25,
    arc: 25,
    warm: false,
    hot: false,
    usedTokens: 50_000,
    contextWindow: 200_000,
    inputTokens: null,
    outputTokens: null
  },
  'a known context window still reports remaining percent'
);
assert.deepEqual(
  contextMeterState(398_832, null),
  { kind: 'unknown' },
  'a missing context window hides the indicator'
);
assert.deepEqual(
  contextMeterState(334_581_117, 237_500),
  { kind: 'unknown' },
  'usage larger than the window is treated as untrustworthy'
);
assert.deepEqual(contextMeterState(null, null), { kind: 'unknown' }, 'nothing usable renders nothing');

/** The percent branch of the meter, or a failed assertion. */
function meter(usedTokens: number, contextWindow: number, tokens?: { inputTokens?: number | null; outputTokens?: number | null }) {
  const state = contextMeterState(usedTokens, contextWindow, tokens);
  assert.equal(state.kind, 'percent');
  if (state.kind !== 'percent') throw new Error('unreachable');
  return state;
}

// ── In and out tokens ride along when the adapter reports them ───────
const withInOut = meter(51_285, 1_000_000, { inputTokens: 48_000, outputTokens: 3_285 });
assert.equal(withInOut.usedTokens, 51_285);
assert.equal(withInOut.contextWindow, 1_000_000);
assert.equal(withInOut.inputTokens, 48_000);
assert.equal(withInOut.outputTokens, 3_285);
assert.equal(withInOut.used, 5, 'the headline percent is the used share');

// ── One-source context meter: ring and text agree (ring_and_text_agree) ──
// The ring and the hint text both read this one object, so they can no
// longer disagree about how much context is left.
assert.equal(meter(36598, 200000).remaining, 82, 'a healthy remaining share is not warm');
assert.equal(meter(36598, 200000).warm, false);
assert.equal(meter(160000, 200000).warm, true, 'a low remaining share is warm');
assert.equal(meter(160000, 200000).hot, false);
assert.equal(meter(190000, 200000).hot, true, 'a nearly spent window is hot as well as warm');
assert.deepEqual(
  contextMeterState(undefined, 200000),
  { kind: 'unknown' },
  'missing usage renders nothing'
);

// ── The arc keeps small usage visible ─────────────────────
// The arc paints what has been used. A ring that paints nothing until a
// tenth of the window is gone reads as broken, so anything above zero and
// under the floor paints the same small sliver, and true proportion takes
// over from there.
assert.equal(meter(0, 200_000).arc, 0, 'an untouched window paints no arc');
assert.equal(meter(200, 200_000).arc, 10, '0.1% of the window still paints the sliver');
assert.equal(meter(10_000, 200_000).arc, 10, '5% paints the sliver, not 5%');
assert.equal(meter(20_000, 200_000).arc, 10, 'the floor and the true share meet at 10%');
assert.equal(meter(100_000, 200_000).arc, 50, 'above the floor the arc is the true share');
assert.equal(meter(200_000, 200_000).arc, 100, 'a spent window paints the whole ring');

// ── What the meter's popup says ───────────────────────
assert.equal(contextMeterPopover({ kind: 'unknown' }, 'Codex'), null, 'nothing to show without a percentage');
assert.deepEqual(
  contextMeterPopover(contextMeterState(51_285, 1_000_000, { inputTokens: 48_000, outputTokens: 3_285 }), 'Codex'),
  {
    headline: '51,285 of 1,000,000 (5%)',
    rows: [
      { label: 'In', value: '48,000' },
      { label: 'Out', value: '3,285' }
    ]
  },
  'the popup spells out the exact numbers'
);
assert.deepEqual(
  contextMeterPopover(contextMeterState(50_000, 200_000), 'Claude'),
  {
    headline: '50,000 of 200,000 (25%)',
    rows: [
      { label: 'In', value: 'not reported by Claude' },
      { label: 'Out', value: 'not reported by Claude' }
    ]
  },
  'an absent field names the provider that did not send it'
);

assert.equal(ringDash(82, 100), 82, 'the ring fills to the remaining share of its circumference');
assert.equal(ringDash(20, 100), 20);
assert.equal(ringDash(0, 100), 0, 'an empty ring at zero remaining');

// ── Token labels ─────────────────────────────────────────────────────────
assert.equal(formatContextTokens(940), '940');
assert.equal(formatContextTokens(9_400), '9.4k');
assert.equal(formatContextTokens(398_832), '399k');
assert.equal(formatContextTokens(1_250_000), '1.2m');

console.log('composerSlashCommands.test.ts passed');
