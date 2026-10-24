/**
 * browserAttachmentNote.ts — the words that go with the picture.
 *
 * A marked-up screenshot on its own leaves the reader guessing which page it
 * came from and which of the circles on it is which. These are the lines that
 * go beside it: the address, and one line per numbered place. Nothing else —
 * the image carries the rest, and a label with nothing after it is noise, so an
 * absent field simply has no line.
 *
 * PURE: no DOM, no backend call.
 */
function clean(value: string | null | undefined): string {
  return typeof value === 'string' ? value.trim() : '';
}

/**
 * The element's tag, read off the end of its selector. The picker answers with
 * a selector rather than a tag, and the last step of it is the element itself —
 * `main > section:nth-child(2) h1.title` is an `h1`. Anything that is not a
 * plain tag name (an id, a class on its own) has no tag to show.
 */
export function elementTagFromSelector(selector: string | null | undefined): string | null {
  const last = clean(selector).split(/\s|>/).filter(Boolean).pop() ?? '';
  const tag = last.split(/[.#:[]/)[0]?.toLowerCase() ?? '';
  return /^[a-z][a-z0-9-]*$/.test(tag) ? tag : null;
}

/** One numbered place on the page, as the request needs to name it. */
export interface AnnotationNote {
  number: number;
  tag: string;
  /** The page's own role for the element, when it has one. */
  role?: string | null;
  label: string;
  selector?: string | null;
  accessibleName?: string | null;
  textSnippet?: string | null;
  classes?: readonly string[];
}

/**
 * The words for a turn that carries a numbered, marked-up page.
 *
 * The picture shows numbered circles; this is the key to them. Without it the
 * reader of the message can see that three things were pointed at and has no
 * idea what was asked about any of them — the labels live beside the circles
 * in the panel and nowhere in what gets sent. A place with nothing typed about
 * it still gets its line, naming what it is, because the circle on the picture
 * is there either way and an unexplained number is worse than a bare one.
 */
export function formatAnnotationRequest(input: {
  url: string;
  description: string;
  annotations: readonly AnnotationNote[];
}): string {
  const lines: string[] = [];
  const description = clean(input.description);
  const url = clean(input.url);

  if (description) lines.push(description);
  if (url) lines.push(`Page: ${url}`);
  if (input.annotations.length > 0) {
    lines.push('Marked on the page:');
    for (const note of input.annotations) {
      const label = clean(note.label);
      // The role names the thing the way the page does; the tag is the fallback.
      const kind = clean(note.role) || clean(note.tag) || 'region';
      lines.push(label ? `${note.number}. ${kind} — ${label}` : `${note.number}. ${kind}`);
      const selector = clean(note.selector).replace(/\s+/g, ' ');
      const accessibleName = clean(note.accessibleName).replace(/\s+/g, ' ');
      const textSnippet = clean(note.textSnippet).replace(/\s+/g, ' ');
      const classes = (note.classes ?? []).map((value) => clean(value)).filter(Boolean);
      if (selector) lines.push(`  Selector: ${selector}`);
      if (accessibleName) lines.push(`  Accessible name: ${accessibleName}`);
      if (textSnippet) lines.push(`  Text: ${textSnippet}`);
      if (classes.length > 0) lines.push(`  Classes: ${classes.join(', ')}`);
    }
  }

  return lines.join('\n');
}
