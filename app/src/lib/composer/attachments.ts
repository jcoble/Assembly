// The Claude Code harness sends only text to the CLI, so an image travels as
// its uploaded path on a reference line after the typed text. The same lines
// are persisted with the message, and the history view reads them back. The
// thumbnail path lets history show the small WebP instead of the original.
export type AttachmentRef = { path: string; name: string; thumbnail?: string };

const PREFIX = 'Attached image: ';
const LINE = /^Attached image: (\/\S+) \((.*)\)(?: thumbnail: (\/\S+))?$/;
// The context key for the conversation's attachment reader: (file name) => Blob.
export const ATTACHMENT_FETCH = Symbol('attachment-fetch');

export function withAttachments(text: string, refs: AttachmentRef[]): string {
  const lines = refs.map(
    (ref) =>
      `${PREFIX}${ref.path} (${ref.name.replace(/[\r\n]+/g, ' ')})${ref.thumbnail ? ` thumbnail: ${ref.thumbnail}` : ''}`,
  );
  return [text, lines.join('\n')].filter(Boolean).join('\n\n');
}

// Splits trailing reference lines off a message's text.
export function splitAttachments(content: string): { text: string; attachments: AttachmentRef[] } {
  if (!content.includes(PREFIX)) return { text: content, attachments: [] };
  const lines = content.split('\n');
  const attachments: AttachmentRef[] = [];
  let end = lines.length;
  for (let match; end > 0 && (match = LINE.exec(lines[end - 1]!)); end--) {
    attachments.unshift({ path: match[1]!, name: match[2]!, ...(match[3] && { thumbnail: match[3] }) });
  }
  return { text: attachments.length ? lines.slice(0, end).join('\n').trimEnd() : content, attachments };
}

// The server enforces the same limits; checking here rejects a file when it is attached.
const IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp'];
const MAX_BYTES = 20 * 1024 * 1024;

export function rejection(type: string, size: number): string | null {
  if (!IMAGE_TYPES.includes(type)) return 'not a supported image';
  return size > MAX_BYTES ? 'larger than 20 MB' : null;
}

// The thumbnail's type: the backend's ImageIO writes JPEG, or PNG for an image
// with alpha; anything else is taken as WebP.
export function thumbnailType(bytes: Uint8Array): string {
  if (bytes[0] === 0xff && bytes[1] === 0xd8) return 'image/jpeg';
  if (bytes[0] === 0x89 && bytes[1] === 0x50) return 'image/png';
  return 'image/webp';
}

export const baseName = (path: string) => path.slice(path.lastIndexOf('/') + 1);

// Dropped files and folders as message text: their absolute paths, separated by spaces, a path with a space in
// double quotes.
export const pathsText = (paths: string[]) => paths.map((path) => (/\s/.test(path) ? `"${path}"` : path)).join(' ');

// `text` put in `draft` at the caret, with a space either side unless the draft already has one there.
export function insertAt(draft: string, caret: number, text: string): { draft: string; caret: number } {
  const before = draft.slice(0, caret);
  const after = draft.slice(caret);
  const inserted = (before && !/\s$/.test(before) ? ' ' : '') + text + (after && !/^\s/.test(after) ? ' ' : '');
  return { draft: before + inserted + after, caret: caret + inserted.length };
}

export function formatBytes(size: number): string {
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / (1024 * 1024)).toFixed(1)} MB`;
}
