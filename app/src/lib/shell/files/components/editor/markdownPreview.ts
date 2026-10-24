/**
 * markdownPreview.ts — which of the two Markdown views the editor shows first.
 *
 * The editor can show a Markdown file two ways: the raw source in the code
 * editor, or the rendered document. A file reached from a diff or a jump opens
 * rendered, because the reader asked to read it. Picking the file in the
 * open-files strip keeps the editor on source, because the reader is working in
 * it. Either way the toggle switches the active file at any time.
 */

import { parseRemoteWorkspacePath, remoteWorkspacePath } from '../../../../workspacePaths.ts';

export type MarkdownView = 'rendered' | 'raw';

/** Why the file was opened. */
export type MarkdownOpenOrigin = 'jump' | 'strip';

const MARKDOWN_EXTENSIONS = new Set(['md', 'mdx', 'markdown', 'mdown', 'mkd']);

export function isMarkdownFile(fileName: string | null | undefined): boolean {
  const name = (fileName ?? '').trim().toLocaleLowerCase();
  const dot = name.lastIndexOf('.');
  if (dot < 1) return false;
  return MARKDOWN_EXTENSIONS.has(name.slice(dot + 1));
}

/** An HTML page reads the same two ways: its source, or the page rendered in a
 *  sandboxed frame. It always opens on source. */
export function isHtmlFile(fileName: string | null | undefined): boolean {
  const name = (fileName ?? '').trim().toLocaleLowerCase();
  return name.endsWith('.html') || name.endsWith('.htm');
}

export function markdownPreviewDefault(
  fileName: string | null | undefined,
  origin: MarkdownOpenOrigin
): MarkdownView {
  if (!isMarkdownFile(fileName)) return 'raw';
  return origin === 'strip' ? 'raw' : 'rendered';
}

const FRONTMATTER = /^---\r?\n(?:.*\r?\n)*?(?:---|\.\.\.)[ \t]*(?:\r?\n|$)(?:[ \t]*\r?\n)*/;

/** A YAML front matter block at the very start of a file, with the blank lines
 *  after it, and the rest. Preview renders only the rest, because Markdown
 *  reads the block as a rule and a heading. */
export function splitFrontmatter(markdown: string): { frontmatter: string; body: string } {
  const frontmatter = FRONTMATTER.exec(markdown)?.[0] ?? '';
  return { frontmatter, body: markdown.slice(frontmatter.length) };
}

/** The top-level keys of a front matter block, each with its value as written.
 *  Indented lines under a key (a list or a map) join that key's value. */
export function frontmatterRows(frontmatter: string): [string, string][] {
  const rows: [string, string][] = [];
  for (const line of frontmatter.split(/\r?\n/).slice(1)) {
    if (/^(?:---|\.\.\.)[ \t]*$/.test(line)) break;
    if (!line.trim() || line.startsWith('#')) continue;
    const key = /^([^\s:#][^:]*):(?:[ \t]+(.*)|[ \t]*)$/.exec(line);
    if (key) rows.push([key[1].trim(), (key[2] ?? '').trim()]);
    else if (rows.length > 0) {
      const row = rows[rows.length - 1];
      row[1] = row[1] ? `${row[1]}\n${line.trim()}` : line.trim();
    }
  }
  return rows;
}

/** The parts of a Markdown token the checkbox walk reads. */
export type MarkdownBlockToken = {
  type: string;
  raw?: string;
  task?: boolean;
  block?: boolean;
  tokens?: readonly MarkdownBlockToken[];
  items?: readonly MarkdownBlockToken[];
};

/** Blocks whose lines are shown as written, so a task-looking line in them is
 *  not a task. Inline HTML (no `block` flag) sits inside a line and is skipped. */
const VERBATIM_BLOCKS = new Set(['code', 'html', 'mermaid', 'blockKatex']);

/** Where each rendered task checkbox is in `source`, in the order the Preview
 *  draws them: the offset of the character between "[" and "]". The tasks come
 *  from `tokens`, the parse the Preview rendered, and each is placed on the next
 *  source line that ends with its first line (the rest of that line may only be
 *  indentation and ">" quote marks). Verbatim blocks are stepped over the same
 *  way. A task that cannot be placed ends the list, so the count no longer
 *  matches the boxes on screen and the click is refused. */
export function taskCheckboxOffsets(source: string, tokens: readonly MarkdownBlockToken[]): number[] {
  const lines: { start: number; text: string }[] = [];
  for (const match of source.matchAll(/[^\r\n]*(?:\r\n|\r|\n|$)/g)) {
    if (match[0] === '' && match.index === source.length && lines.length > 0) break;
    lines.push({ start: match.index, text: match[0].replace(/[\r\n]+$/, '') });
  }
  const offsets: number[] = [];
  let next = 0;
  const place = (raw: string): number => {
    const first = raw.split('\n', 1)[0];
    for (let index = next; index < lines.length; index++) {
      const { text } = lines[index];
      if (text.endsWith(first) && /^[ \t>]*$/.test(text.slice(0, text.length - first.length))) return index;
    }
    return -1;
  };
  const walk = (list: readonly MarkdownBlockToken[]): boolean => {
    for (const token of list) {
      if (token.type === 'list_item' && token.raw) {
        const index = place(token.raw);
        if (index < 0) return false;
        if (token.task) {
          const line = lines[index];
          offsets.push(line.start + line.text.length - token.raw.split('\n', 1)[0].length + token.raw.indexOf('[') + 1);
        }
        next = index + 1;
      } else if (VERBATIM_BLOCKS.has(token.type) && (token.type !== 'html' || token.block) && token.raw?.trim()) {
        const index = place(token.raw.replace(/^\n+/, ''));
        if (index < 0) return false;
        next = index + token.raw.replace(/^\n+/, '').replace(/\n+$/, '').split('\n').length;
        continue;
      }
      if (token.items && !walk(token.items)) return false;
      if (token.tokens && !walk(token.tokens)) return false;
    }
    return true;
  };
  walk(tokens);
  return offsets;
}

/** The file with one task checkbox flipped: " " becomes "x", "x" or "X"
 *  becomes " ". Nothing else in the file changes. */
export function toggleTaskCheckbox(markdown: string, offset: number): string {
  const mark = markdown[offset];
  if (markdown[offset - 1] !== '[' || markdown[offset + 1] !== ']' || !/^[ xX]$/.test(mark ?? '')) {
    throw new Error(`No task checkbox at offset ${offset}`);
  }
  return markdown.slice(0, offset) + (mark === ' ' ? 'x' : ' ') + markdown.slice(offset + 1);
}

/** The file after a click on the index-th task box on screen, which now shows
 *  `nowChecked`, given where the rendered tasks are (`taskCheckboxOffsets`);
 *  null when the boxes on screen and those places disagree, so the click is
 *  refused rather than changing the wrong box. */
export function toggleTaskAt(markdown: string, offsets: readonly number[], index: number, boxesOnScreen: number, nowChecked: boolean): string | null {
  const offset = offsets[index];
  if (offsets.length !== boxesOnScreen || offset === undefined || (markdown[offset] !== ' ') === nowChecked) return null;
  return toggleTaskCheckbox(markdown, offset);
}

export type MarkdownImageTarget =
  | { kind: 'file'; path: string }
  | { kind: 'https' | 'http'; url: string };

/** Where an image in a Markdown file comes from: a file inside the project
 *  (a relative path, resolved from the Markdown file's folder) or a web
 *  address. Absolute paths, paths that climb out of the project, and any local
 *  image when there is no project give null, so the alt text shows instead. */
export function markdownImageTarget(markdownPath: string, projectRoot: string | null | undefined, href: string | undefined): MarkdownImageTarget | null {
  const value = href?.trim() ?? '';
  if (/^https:/i.test(value)) return { kind: 'https', url: value };
  if (/^http:/i.test(value)) return { kind: 'http', url: value };
  if (!value || value.startsWith('#') || /^[a-z][a-z0-9+.-]*:/i.test(value)) return null;
  let path = value.replace(/[?#].*$/, '');
  try {
    path = decodeURI(path);
  } catch {
    // A malformed escape stays as written.
  }
  if (path.startsWith('/') || !projectRoot) return null;
  // A remote file and its project must be on the same machine; the folders are
  // worked out on the machine's own paths, then the machine is put back.
  const remoteDoc = parseRemoteWorkspacePath(markdownPath);
  const remoteRoot = parseRemoteWorkspacePath(projectRoot);
  if (remoteDoc?.profileId !== remoteRoot?.profileId) return null;
  const parts = (remoteDoc?.path ?? markdownPath).split('/').slice(0, -1);
  for (const part of path.split('/')) {
    if (part === '..') parts.pop();
    else if (part && part !== '.') parts.push(part);
  }
  const file = `/${parts.filter(Boolean).join('/')}`;
  const rootDir = `${(remoteRoot?.path ?? projectRoot).replace(/\/+$/, '')}/`;
  if (!file.startsWith(rootDir)) return null;
  return { kind: 'file', path: remoteDoc ? remoteWorkspacePath(remoteDoc.profileId, file) : file };
}

/** Raw HTML attributes that make the webview fetch an address on their own:
 *  srcset picks more image files, style can load url(...), and background
 *  paints an image on a table or cell. The Preview drops them from every tag. */
const FETCHING_ATTRIBUTES = new Set(['srcset', 'style', 'background']);

export function withoutFetchingAttributes(attributes: Record<string, string>): Record<string, string> {
  return Object.fromEntries(Object.entries(attributes).filter(([name]) => !FETCHING_ATTRIBUTES.has(name.toLowerCase())));
}

/** The image type of a file the app shows as a picture, from its name. */
export function rasterImageMimeType(fileName: string | null | undefined): string | null {
  const extension = fileName?.split('.').at(-1)?.toLowerCase();
  if (extension === 'png') return 'image/png';
  if (extension === 'jpg' || extension === 'jpeg') return 'image/jpeg';
  if (extension === 'gif') return 'image/gif';
  if (extension === 'webp') return 'image/webp';
  if (extension === 'bmp') return 'image/bmp';
  if (extension === 'ico') return 'image/x-icon';
  if (extension === 'avif') return 'image/avif';
  return null;
}
