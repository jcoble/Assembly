/**
 * utilityStrip.ts — the two shapes the bottom strip and the overlay layer both
 * need.
 *
 * Resources and Usage are drawn as buttons at the bottom of the right panel,
 * but the surfaces they open are mounted at the page root, above the layout, so
 * a card cannot be clipped by the column it was opened from. The strip
 * therefore hands the overlay layer the exact rectangle of the button that was
 * pressed, and the overlay layer positions its surface against it.
 *
 * PURE: no DOM, no Svelte, no backend call.
 */

export type UtilityId = 'resources' | 'usage';

/** A button's rectangle on screen, in the shape `position: fixed` wants. */
export interface UtilityAnchor {
  left: number;
  top: number;
  width: number;
  height: number;
}

export function utilityAnchorFor(
  rect: Pick<DOMRectReadOnly, 'left' | 'top' | 'width' | 'height'>
): UtilityAnchor {
  return { left: rect.left, top: rect.top, width: rect.width, height: rect.height };
}

export function utilityAnchorStyle(anchor: UtilityAnchor): string {
  return `left: ${anchor.left}px; top: ${anchor.top}px; width: ${anchor.width}px; height: ${anchor.height}px;`;
}
