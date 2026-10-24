/**
 * browserAnnotationList.test.ts — the numbered annotations on a marked-up page.
 *
 * What the badge on the page says, what the row in the list says, and what
 * ends up painted into the picture that gets sent all come from these
 * functions, so they are the place the three are held to agreeing.
 *
 * Run: node --experimental-strip-types scripts/browserAnnotationList.test.ts
 */
import assert from 'node:assert/strict';

import {
  addAnnotation,
  annotationCountLabel,
  annotationKind,
  composeMarks,
  labelAnnotation,
  numberAnnotations,
  removeAnnotation,
  type BrowserAnnotation
} from '../src/lib/shell/browser/components/annotationList.ts';

function element(id: string, tag: string, label = '', role: string | null = null): BrowserAnnotation {
  return {
    id,
    box: { kind: 'element', x: 10, y: 20, width: 100, height: 40, tag },
    pin: { x: 44, y: 38 },
    label,
    tag,
    selector: `main > ${tag}.target`,
    role,
    accessibleName: 'Target element',
    textSnippet: 'Nearby words',
    classes: ['target']
  };
}

function region(id: string, label = ''): BrowserAnnotation {
  return {
    id,
    box: { kind: 'region', x: 5, y: 5, width: 50, height: 50 },
    pin: { x: 5, y: 5 },
    label,
    tag: 'region',
    selector: null,
    role: null,
    accessibleName: null,
    textSnippet: null,
    classes: []
  };
}

// The chip beside an annotation says what the page calls the element. A search
// box is a `combobox` to anyone reading it, and an `input` only to the selector.
assert.equal(annotationKind(element('k', 'input', '', 'combobox')), 'combobox');
assert.equal(annotationKind(element('k', 'button')), 'button');
assert.equal(annotationKind(element('k', 'button', '', '  ')), 'button');
assert.equal(annotationKind(region('k')), 'region');

// Annotations keep the order they were made in, and adding never touches the
// list that was there — the panel hands the old one straight back to Svelte.
const first = element('a', 'h1');
const empty: BrowserAnnotation[] = [];
const one = addAnnotation(empty, first);
const two = addAnnotation(one, region('b'));
assert.deepEqual(empty, []);
assert.deepEqual(one.map((item) => item.id), ['a']);
assert.deepEqual(two.map((item) => item.id), ['a', 'b']);

// Numbers are positions, counting from 1.
assert.deepEqual(numberAnnotations(two).map((item) => item.number), [1, 2]);
assert.deepEqual(numberAnnotations(empty), []);

// Removing renumbers what is left. The badge on the page and the row in the
// list both read from here, so a gap in the numbers would show up twice.
const three = addAnnotation(two, element('c', 'button'));
const afterRemove = removeAnnotation(three, 'b');
assert.deepEqual(afterRemove.map((item) => item.id), ['a', 'c']);
assert.deepEqual(
  numberAnnotations(afterRemove).map((item) => [item.id, item.number]),
  [['a', 1], ['c', 2]]
);

// Removing something that is not there changes nothing.
assert.deepEqual(removeAnnotation(three, 'missing').map((item) => item.id), ['a', 'b', 'c']);

// Labelling replaces one annotation's words and leaves the rest alone.
const labelled = labelAnnotation(three, 'b', 'make this wider');
assert.equal(labelled.find((item) => item.id === 'b')?.label, 'make this wider');
assert.equal(labelled.find((item) => item.id === 'a')?.label, '');
assert.equal(three.find((item) => item.id === 'b')?.label, '');

// A label can be cleared, and a label for an annotation that is gone is a
// no-op rather than an entry appearing from nowhere.
assert.equal(labelAnnotation(labelled, 'b', '').find((item) => item.id === 'b')?.label, '');
assert.equal(labelAnnotation(three, 'missing', 'x').length, 3);

// Painting order: every box first, then the freehand ink over them. A line
// drawn inside a region has to stay visible, and a box is a filled rectangle.
const ink = [
  { id: 'ink-1', shape: { kind: 'stroke' as const, points: [{ x: 0, y: 0 }, { x: 9, y: 9 }] } }
];
const marks = composeMarks(three, ink);
assert.deepEqual(marks.map((item) => item.id), ['a', 'b', 'c', 'ink-1']);
assert.deepEqual(marks.map((item) => item.shape.kind), ['element', 'region', 'element', 'stroke']);
assert.equal(numberAnnotations(three)[0].selector, 'main > h1.target');
assert.deepEqual(numberAnnotations(three)[0].classes, ['target']);

// Each mark keeps its annotation's id, so an erase click on a box knows which
// annotation to take out of the list.
assert.equal(marks[1].shape, three[1].box);
assert.deepEqual(composeMarks(empty, ink).map((item) => item.id), ['ink-1']);
assert.deepEqual(composeMarks(empty, []), []);

// The chip counts in plain words, and says "1 annotation" rather than "1
// annotations".
assert.equal(annotationCountLabel(0), '0 annotations');
assert.equal(annotationCountLabel(1), '1 annotation');
assert.equal(annotationCountLabel(4), '4 annotations');

console.log('browserAnnotationList: all checks passed');
