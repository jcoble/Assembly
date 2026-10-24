/**
 * annotationList.ts — the numbered things the reader pointed at.
 *
 * Marking up a page produces two different kinds of thing, and they are not
 * interchangeable. A box — an element the picker found, or a region drawn by
 * hand — is a place the reader wants to say something about, so it earns a
 * number, a label, and a row in the list. A freehand line is ink: it says what
 * it says by being where it is, and numbering it would be numbering a
 * scribble. This module holds the boxes, and knows how to put the two back
 * together in the order they have to be painted.
 *
 * The numbers are positions in the list, not identities. Remove the second of
 * three and the third becomes the second, because the badge on the page and
 * the row in the list have to agree with each other and with what the reader
 * counts when they look.
 *
 * PURE: no DOM, no canvas, no Svelte.
 */
import type { AnnotationShape, PlacedAnnotationShape } from './annotationComposite.ts';

/** The two shapes that mark a place rather than draw on one. */
export type AnnotationBox = Extract<AnnotationShape, { kind: 'region' } | { kind: 'element' }>;

/** One place on the page, and what the reader said about it. */
export interface BrowserAnnotation {
  id: string;
  /** Where it sits, in the marking surface's own pixels. */
  box: AnnotationBox;
  /** Where the numbered pin sits — the spot that was clicked, not the corner. */
  pin: { x: number; y: number };
  /** What the reader typed for this spot. Empty until they say something. */
  label: string;
  /** The element's tag when the page named one, otherwise `region`. */
  tag: string;
  /** Bounded DOM context from the native page inspector, when one was found. */
  selector: string | null;
  role: string | null;
  accessibleName: string | null;
  textSnippet: string | null;
  classes: string[];
}

/**
 * What the chip beside an annotation says it points at. The page's own role is
 * what a reader recognises — a search box says `combobox`, not `input` — so it
 * wins over the tag read off the selector.
 */
export function annotationKind(item: Pick<BrowserAnnotation, 'role' | 'tag'>): string {
  const role = typeof item.role === 'string' ? item.role.trim() : '';
  return role || item.tag || 'region';
}

/** An annotation with the number shown on its badge. */
export interface NumberedAnnotation extends BrowserAnnotation {
  /** 1 for the first one made, counting up. */
  number: number;
}

export function addAnnotation(
  list: readonly BrowserAnnotation[],
  entry: BrowserAnnotation
): BrowserAnnotation[] {
  return [...list, entry];
}

export function removeAnnotation(
  list: readonly BrowserAnnotation[],
  id: string
): BrowserAnnotation[] {
  return list.filter((item) => item.id !== id);
}

/** The same list with one annotation's label replaced. */
export function labelAnnotation(
  list: readonly BrowserAnnotation[],
  id: string,
  label: string
): BrowserAnnotation[] {
  return list.map((item) => (item.id === id ? { ...item, label } : item));
}

/** The list with each annotation's badge number, counting from 1. */
export function numberAnnotations(
  list: readonly BrowserAnnotation[]
): NumberedAnnotation[] {
  return list.map((item, index) => ({ ...item, number: index + 1 }));
}

/**
 * Every mark on the surface, in painting order: the boxes first and the
 * freehand ink over them. A box is a filled rectangle, and a line drawn under
 * one is a line the reader cannot see — which is the whole reason they drew
 * it. One order for the canvas in the panel and for the picture that gets
 * sent, so the two cannot come to disagree.
 */
export function composeMarks(
  list: readonly BrowserAnnotation[],
  freehand: readonly PlacedAnnotationShape[]
): PlacedAnnotationShape[] {
  return [
    ...list.map((item) => ({ id: item.id, shape: item.box as AnnotationShape })),
    ...freehand
  ];
}

/** What the chip on the card says. */
export function annotationCountLabel(count: number): string {
  return count === 1 ? '1 annotation' : `${count} annotations`;
}
