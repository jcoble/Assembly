/**
 * diffRows.ts — the rows the Changes tab draws for each file. PURE: no IO, no
 * Svelte, no backend.
 *
 * A unified diff carries only a few lines around each change. The rows here
 * fill the space between hunks with a "N unmodified lines" bar whose count
 * comes from the hunk headers alone, and open that bar into real lines when
 * the file's full current text is at hand. Side by side is the same rows,
 * with each removed run paired against the added run that follows it.
 */
import type { SourceGitDiff } from '../../tauriSource.ts';
import { parseUnifiedDiff, type DiffLine, type ParsedDiff } from './parseUnifiedDiff.ts';

export interface GapRow {
  kind: 'gap';
  /** Stable within one file: `<section>:<index>`. */
  key: string;
  /** Unmodified lines hidden here, or null when the end of the file is unknown. */
  count: number | null;
  /** True when opening it can show the lines (the full text is here or can be read). */
  expandable: boolean;
}

export type DiffRow =
  | { kind: 'line'; line: DiffLine }
  | { kind: 'label'; text: string }
  | GapRow;

export type SplitRow =
  | { kind: 'pair'; left: DiffLine | null; right: DiffLine | null }
  | { kind: 'label'; text: string }
  | GapRow;

/** First line a hunk covers on one side; a zero-length side sits after `start`. */
function firstLine(start: number, count: number): number {
  return count === 0 ? start + 1 : start;
}

/**
 * The rows for one file, top to bottom.
 *
 * `fullLines` is the file as it is now, line by line, or null when only the
 * diff text is known. `canLoad` says the caller can fetch that text when a gap
 * is opened. A file with both staged and unstaged work shows each part under
 * its label; its gaps keep their counts but do not open, because the two
 * parts number their lines against different versions of the file.
 */
export function unifiedRows(
  parsed: ParsedDiff,
  fullLines: readonly string[] | null,
  expanded: ReadonlySet<string>,
  canLoad: boolean
): DiffRow[] {
  const rows: DiffRow[] = [];
  const single = parsed.sections.length === 1;
  // A deleted file has no current text to read the hidden lines from.
  const expandable = single && !parsed.isDeletedFile && (fullLines !== null || canLoad);

  parsed.sections.forEach((section, sectionIndex) => {
    if (section.label) rows.push({ kind: 'label', text: section.label });
    let nextNew = 1;
    let nextOld = 1;

    const gap = (index: number, count: number | null, offset: number) => {
      if (count !== null && count <= 0) return;
      const key = `${sectionIndex}:${index}`;
      if (single && fullLines && count !== null && expanded.has(key)) {
        for (let line = nextNew; line < nextNew + count; line += 1) {
          rows.push({
            kind: 'line',
            line: { kind: 'context', text: fullLines[line - 1] ?? '', beforeLine: line + offset, afterLine: line }
          });
        }
        return;
      }
      rows.push({ kind: 'gap', key, count, expandable });
    };

    section.hunks.forEach((hunk, hunkIndex) => {
      const newFirst = firstLine(hunk.afterStart, hunk.afterCount);
      const oldFirst = firstLine(hunk.beforeStart, hunk.beforeCount);
      gap(hunkIndex, newFirst - nextNew, oldFirst - newFirst);
      for (const line of hunk.lines) rows.push({ kind: 'line', line });
      nextNew = newFirst + hunk.afterCount;
      nextOld = oldFirst + hunk.beforeCount;
    });

    const opensBelow = single && section.hunks.length > 0 && !parsed.isNewFile && !parsed.isDeletedFile;
    if (opensBelow) {
      const count = fullLines ? fullLines.length - nextNew + 1 : null;
      gap(section.hunks.length, count, nextOld - nextNew);
    }
  });
  return rows;
}

/** The same rows laid out in two columns: before on the left, after on the right. */
export function splitRows(rows: readonly DiffRow[]): SplitRow[] {
  const out: SplitRow[] = [];
  let removed: DiffLine[] = [];
  let added: DiffLine[] = [];
  // git's "\ No newline" line follows the last line of one side; it is held
  // so it lands right after that line once the two sides are paired.
  let removedNote: DiffLine | null = null;
  let addedNote: DiffLine | null = null;
  let lastSide: 'removed' | 'added' | null = null;

  const flush = () => {
    for (let i = 0; i < Math.max(removed.length, added.length); i += 1) {
      out.push({ kind: 'pair', left: removed[i] ?? null, right: added[i] ?? null });
      const left = i === removed.length - 1 ? removedNote : null;
      const right = i === added.length - 1 ? addedNote : null;
      if (left || right) out.push({ kind: 'pair', left, right });
    }
    removed = [];
    added = [];
    removedNote = null;
    addedNote = null;
    lastSide = null;
  };

  for (const row of rows) {
    if (row.kind === 'line' && row.line.kind === 'removed') {
      if (added.length > 0) flush();
      removed.push(row.line);
      lastSide = 'removed';
      continue;
    }
    if (row.kind === 'line' && row.line.kind === 'added') {
      added.push(row.line);
      lastSide = 'added';
      continue;
    }
    if (row.kind === 'line' && row.line.kind === 'note' && lastSide) {
      if (lastSide === 'added') addedNote = row.line;
      else removedNote = row.line;
      continue;
    }
    flush();
    if (row.kind === 'line') {
      out.push({ kind: 'pair', left: row.line, right: row.line });
    } else {
      out.push(row);
    }
  }
  flush();
  return out;
}

export interface TreeEntry {
  kind: 'folder' | 'file';
  /** What the row shows; a folder chain with nothing else in it reads `src/lib/shell`. */
  name: string;
  /** Repository-relative path of the file, or of the deepest folder in the chain. */
  path: string;
  depth: number;
}

interface FolderNode {
  folders: Map<string, FolderNode>;
  files: string[];
}

/** The changed paths as an indented tree: folders first, then files, by name. */
export function changedFileTree(paths: readonly string[]): TreeEntry[] {
  const root: FolderNode = { folders: new Map(), files: [] };
  for (const path of paths) {
    const parts = path.split('/').filter(Boolean);
    let node = root;
    for (const part of parts.slice(0, -1)) {
      let next = node.folders.get(part);
      if (!next) {
        next = { folders: new Map(), files: [] };
        node.folders.set(part, next);
      }
      node = next;
    }
    if (parts.length > 0) node.files.push(parts[parts.length - 1]);
  }

  const out: TreeEntry[] = [];
  const walk = (node: FolderNode, prefix: string, depth: number) => {
    for (const name of [...node.folders.keys()].sort()) {
      let folder = node.folders.get(name)!;
      let label = name;
      let path = prefix + name;
      while (folder.files.length === 0 && folder.folders.size === 1) {
        const [childName, child] = [...folder.folders][0];
        label += `/${childName}`;
        path += `/${childName}`;
        folder = child;
      }
      out.push({ kind: 'folder', name: label, path, depth });
      walk(folder, `${path}/`, depth + 1);
    }
    for (const name of [...node.files].sort()) {
      out.push({ kind: 'file', name, path: prefix + name, depth });
    }
  };
  walk(root, '', 0);
  return out;
}

/** The diff text for a file. An untracked file comes with its content and no
 * diff, so it is written out as the "new file" diff git would print. */
export function diffTextOf(diff: SourceGitDiff): string {
  if (diff.diff || diff.status !== 'untracked' || !diff.modifiedContent) return diff.diff;
  const lines = diff.modifiedContent.replace(/\r\n/g, '\n').split('\n');
  if (lines.at(-1) === '') lines.pop();
  return [
    `diff --git a/${diff.relativePath} b/${diff.relativePath}`,
    'new file mode 100644',
    '--- /dev/null',
    `+++ b/${diff.relativePath}`,
    `@@ -0,0 +1,${lines.length} @@`,
    ...lines.map((line) => `+${line}`)
  ].join('\n');
}

const parsedCache = new WeakMap<SourceGitDiff, ParsedDiff>();

/** A file's parsed diff, worked out once per record the backend returned. */
export function parsedDiffOf(file: SourceGitDiff): ParsedDiff {
  let parsed = parsedCache.get(file);
  if (!parsed) {
    parsed = parseUnifiedDiff(diffTextOf(file));
    parsedCache.set(file, parsed);
  }
  return parsed;
}

/** Cut one multi-file `git diff` into a record per file, in the shape every
 * other diff read returns. Paths come from the `+++`/`---` lines, or from the
 * `diff --git` line when a file has no text changes (a binary or a rename). */
export function splitDiffByFile(text: string): SourceGitDiff[] {
  const files: SourceGitDiff[] = [];
  for (const chunk of text.split(/^(?=diff --git )/m)) {
    if (!chunk.startsWith('diff --git ')) continue;
    const after = /^\+\+\+ b\/(.*)$/m.exec(chunk)?.[1];
    const before = /^--- a\/(.*)$/m.exec(chunk)?.[1];
    const header = / b\/(.*)$/.exec(chunk.split('\n', 1)[0])?.[1];
    const relativePath = (after ?? before ?? header ?? '').trim();
    if (!relativePath) continue;
    const status = /^new file mode/m.test(chunk)
      ? 'added'
      : /^deleted file mode/m.test(chunk)
        ? 'deleted'
        : /^rename to /m.test(chunk)
          ? 'renamed'
          : 'modified';
    files.push({
      relativePath,
      status,
      diff: chunk.trimEnd(),
      isBinary: /^(Binary files |GIT binary patch)/m.test(chunk),
      originalContent: null,
      modifiedContent: null
    });
  }
  return files;
}
