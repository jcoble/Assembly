/**
 * browserAnnotationComposite.test.ts — the rules behind marking up a page.
 *
 * The drawing surface is a canvas and the burn-in step needs one, so the parts
 * that decide *what* is drawn — which marks are still live, which one an erase
 * click removes, how marks land on the captured image's pixel grid, and what
 * the note beside the picture says — are plain functions. Those are what this
 * test covers; `compositeAnnotations` itself needs a real canvas and is left to
 * the app.
 *
 * Run: node --experimental-strip-types scripts/browserAnnotationComposite.test.ts
 */
import assert from 'node:assert/strict';

import {
  liveShapes,
  scaleShapes,
  shapeAtPoint,
  type AnnotationShape
} from '../src/lib/shell/browser/components/annotationComposite.ts';
import { formatAnnotationRequest } from '../src/lib/shell/browser/components/browserAttachmentNote.ts';

// ── liveShapes ───────────────────────────────────────────────────────────────

const drawn: { id: string; shape: AnnotationShape }[] = [
  { id: 'a', shape: { kind: 'region', x: 10, y: 10, width: 100, height: 60 } },
  { id: 'b', shape: { kind: 'region', x: 200, y: 10, width: 0.4, height: 60 } },
  { id: 'c', shape: { kind: 'stroke', points: [{ x: 5, y: 5 }] } },
  { id: 'd', shape: { kind: 'stroke', points: [{ x: 5, y: 5 }, { x: 5, y: 90 }] } },
  { id: 'e', shape: { kind: 'element', x: 300, y: 40, width: 80, height: 30, tag: 'svg' } }
];

assert.deepEqual(
  liveShapes(drawn, new Set(['e'])).map((item) => item.id),
  ['a', 'd'],
  'erased ids and sub-pixel marks both drop out'
);
assert.deepEqual(
  liveShapes(drawn, new Set()).map((item) => item.id),
  ['a', 'd', 'e']
);
// A stroke straight down has no width at all and must still survive.
assert.equal(liveShapes([drawn[3]], new Set()).length, 1);

// ── shapeAtPoint ─────────────────────────────────────────────────────────────

const stacked: { id: string; shape: AnnotationShape }[] = [
  { id: 'under', shape: { kind: 'region', x: 0, y: 0, width: 200, height: 200 } },
  { id: 'over', shape: { kind: 'region', x: 50, y: 50, width: 50, height: 50 } },
  { id: 'line', shape: { kind: 'stroke', points: [{ x: 300, y: 20 }, { x: 380, y: 20 }] } }
];

assert.equal(shapeAtPoint(stacked, 60, 60), 'over', 'the topmost mark is the one erased');
assert.equal(shapeAtPoint(stacked, 10, 10), 'under');
assert.equal(shapeAtPoint(stacked, 900, 900), null, 'a click on bare page erases nothing');
assert.equal(shapeAtPoint(stacked, 340, 22), 'line', 'a stroke is hit within its tolerance');
assert.equal(shapeAtPoint(stacked, 340, 90), null, 'and missed outside it');

// ── scaleShapes ──────────────────────────────────────────────────────────────

const layer = { width: 400, height: 300 };

assert.deepEqual(
  scaleShapes(
    [
      { kind: 'region', x: 20, y: 30, width: 100, height: 60 },
      { kind: 'stroke', points: [{ x: 10, y: 10 }, { x: 40, y: 80 }] }
    ],
    layer,
    { width: 800, height: 600 }
  ),
  [
    { kind: 'region', x: 40, y: 60, width: 200, height: 120 },
    { kind: 'stroke', points: [{ x: 20, y: 20 }, { x: 80, y: 160 }] }
  ],
  'a doubled image doubles both axes'
);

// The captured image need not share the layer's aspect ratio — a device pixel
// ratio only on one axis is normal. Each axis takes its own factor.
assert.deepEqual(
  scaleShapes([{ kind: 'element', x: 20, y: 30, width: 100, height: 60, tag: 'svg' }], layer, {
    width: 800,
    height: 300
  }),
  [{ kind: 'element', x: 40, y: 30, width: 200, height: 60, tag: 'svg' }]
);

// ── formatAnnotationRequest ──────────────────────────────────────────────────

// The picture that goes with this carries numbered circles. The words have to
// say what each number was about, or the numbers mean nothing to whoever reads
// the turn.
const request = formatAnnotationRequest({
  url: 'https://example.com/pricing',
  description: 'Tighten these up.',
  annotations: [
    {
      number: 1,
      tag: 'h1',
      label: 'two lines on mobile',
      selector: 'main > h1.hero',
      accessibleName: 'Plans and pricing',
      textSnippet: 'Choose the plan that works for you',
      classes: ['hero', 'wide']
    },
    { number: 2, tag: 'region', label: '' }
  ]
});
assert.equal(request.split('\n')[0], 'Tighten these up.');
assert.equal(request.includes('https://example.com/pricing'), true);
assert.equal(request.includes('1. h1 — two lines on mobile'), true);
assert.equal(request.includes('  Selector: main > h1.hero'), true);
assert.equal(request.includes('  Accessible name: Plans and pricing'), true);
assert.equal(request.includes('  Text: Choose the plan that works for you'), true);
assert.equal(request.includes('  Classes: hero, wide'), true);

// A place with nothing typed about it still gets its line: the circle is on the
// picture either way.
assert.equal(request.includes('2. region'), true);
assert.equal(request.includes('2. region —'), false);

// The page's own role names the element the way a reader recognises it, so it
// wins over the tag read off the selector.
assert.equal(
  formatAnnotationRequest({
    url: '',
    description: '',
    annotations: [{ number: 1, tag: 'input', role: 'combobox', label: 'widen this' }]
  }).includes('1. combobox — widen this'),
  true
);
assert.equal(
  formatAnnotationRequest({
    url: '',
    description: '',
    annotations: [{ number: 1, tag: 'button', role: '  ', label: '' }]
  }).includes('1. button'),
  true
);

// A tag that never arrived still reads as something.
assert.equal(
  formatAnnotationRequest({
    url: '',
    description: '',
    annotations: [{ number: 1, tag: '   ', label: 'this bit' }]
  }).includes('1. region — this bit'),
  true
);

// Nothing marked and nothing typed is nothing said, not a heading on its own.
assert.equal(formatAnnotationRequest({ url: '', description: '', annotations: [] }), '');
assert.equal(
  formatAnnotationRequest({ url: '', description: '  ', annotations: [] }).includes('Marked'),
  false
);

for (const line of request.split('\n')) {
  assert.equal(/—\s*$/.test(line), false, `dangling label: ${JSON.stringify(line)}`);
}

console.log('browserAnnotationComposite: all checks passed');
