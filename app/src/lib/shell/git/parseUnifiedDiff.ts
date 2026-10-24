/**
 * parseUnifiedDiff.ts — turn the text `read_source_git_diff` returns into
 * something a panel can render. PURE: no IO, no Svelte, no backend.
 *
 * What the backend actually hands us (src-tauri/src/main.rs
 * `read_source_git_diff_sync` + `combine_source_git_diffs`):
 *
 * - one file's diff, produced by `git diff` / `git diff --cached`;
 * - when a file has BOTH staged and unstaged work, the two diffs are
 *   concatenated under literal `## Staged` and `## Working tree` marker lines.
 *   Those markers cannot collide with diff content: every line inside a hunk
 *   starts with a space, `+`, `-`, or `\`, so a line starting with `#` is
 *   always ours;
 * - an empty string when the file has no changes at all;
 * - `Binary files a/x and b/x differ` (or `GIT binary patch`) for binary files —
 *   the caller also gets `isBinary` on the backend record, but this parser
 *   detects it independently so it works on any diff text.
 *
 * Reconstruction caveat, stated once and honestly: `before` / `after` are
 * rebuilt from the lines the diff actually carries. A unified diff only carries
 * a few lines of context around each change, so those strings are the VISIBLE
 * portion of the file, not the whole file. They are useful for a side-by-side
 * view of the changed regions; they are not the file on disk.
 */

/** What a single rendered diff line is. `note` is git's `\ No newline…` line. */
export type DiffLineKind = 'context' | 'added' | 'removed' | 'note';

export interface DiffLine {
  kind: DiffLineKind;
  /** Line content with the leading `+`/`-`/space marker removed. */
  text: string;
  /** 1-based line number in the before-text, or null for an added line. */
  beforeLine: number | null;
  /** 1-based line number in the after-text, or null for a removed line. */
  afterLine: number | null;
}

export interface DiffHunk {
  /** The raw `@@ … @@` line, kept for tooltips. */
  header: string;
  /** The trailing text after the closing `@@` (git's enclosing-function hint). */
  heading: string;
  beforeStart: number;
  beforeCount: number;
  afterStart: number;
  afterCount: number;
  lines: DiffLine[];
}

export interface DiffSection {
  /** `Staged`, `Working tree`, or '' when the diff had no marker lines. */
  label: string;
  hunks: DiffHunk[];
  /** The changed region as it was before, rebuilt from context + removed lines. */
  before: string;
  /** The changed region as it is after, rebuilt from context + added lines. */
  after: string;
  addedCount: number;
  removedCount: number;
  isBinary: boolean;
  isNewFile: boolean;
  isDeletedFile: boolean;
  oldPath: string | null;
  newPath: string | null;
}

export interface ParsedDiff {
  /** One entry per `## …` block; a plain diff yields a single unlabeled entry. */
  sections: DiffSection[];
  /** Every section's hunks, in the order they appeared. */
  hunks: DiffHunk[];
  /** First section's before-text (the oldest version the diff describes). */
  before: string;
  /** Last section's after-text (the newest version the diff describes). */
  after: string;
  addedCount: number;
  removedCount: number;
  isBinary: boolean;
  isNewFile: boolean;
  isDeletedFile: boolean;
  oldPath: string | null;
  newPath: string | null;
  /** No hunks and nothing binary: there is nothing to show. */
  isEmpty: boolean;
}

const HUNK_HEADER = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$/;

/** `a/src/x.ts` → `src/x.ts`; also drops a trailing tab-separated timestamp. */
function cleanPath(raw: string): string | null {
  const value = raw.split('\t')[0].trim();
  if (value === '' || value === '/dev/null') return null;
  if (value.startsWith('a/') || value.startsWith('b/')) return value.slice(2);
  return value;
}

function emptySection(label: string): DiffSection {
  return {
    label,
    hunks: [],
    before: '',
    after: '',
    addedCount: 0,
    removedCount: 0,
    isBinary: false,
    isNewFile: false,
    isDeletedFile: false,
    oldPath: null,
    newPath: null
  };
}

/**
 * Parse unified-diff text. Never throws: anything it does not recognise is
 * ignored, so a surprising diff renders as "nothing to show" rather than
 * breaking the panel.
 */
export function parseUnifiedDiff(text: string): ParsedDiff {
  const sections: DiffSection[] = [];
  let section: DiffSection | null = null;
  let hunk: DiffHunk | null = null;
  let beforeLine = 0;
  let afterLine = 0;
  let beforeRemaining = 0;
  let afterRemaining = 0;
  const beforeText: string[] = [];
  const afterText: string[] = [];

  /** Start (or reuse) the section the next line belongs to. */
  function ensureSection(label: string): DiffSection {
    if (section && section.label === label) return section;
    flushSection();
    section = emptySection(label);
    sections.push(section);
    return section;
  }

  function flushSection(): void {
    if (!section) return;
    section.before = beforeText.join('\n');
    section.after = afterText.join('\n');
    beforeText.length = 0;
    afterText.length = 0;
    hunk = null;
  }

  const lines = text.replace(/\r\n/g, '\n').split('\n');

  for (const line of lines) {
    // ── inside a hunk body ──────────────────────────────────────────────────
    if (hunk && (beforeRemaining > 0 || afterRemaining > 0 || line.startsWith('\\'))) {
      const marker = line.charAt(0);
      const body = line.slice(1);

      if (marker === '\\') {
        hunk.lines.push({ kind: 'note', text: line.trim(), beforeLine: null, afterLine: null });
        continue;
      }
      if (marker === '+') {
        afterText.push(body);
        hunk.lines.push({ kind: 'added', text: body, beforeLine: null, afterLine });
        afterLine += 1;
        afterRemaining -= 1;
        section!.addedCount += 1;
        continue;
      }
      if (marker === '-') {
        beforeText.push(body);
        hunk.lines.push({ kind: 'removed', text: body, beforeLine, afterLine: null });
        beforeLine += 1;
        beforeRemaining -= 1;
        section!.removedCount += 1;
        continue;
      }
      if (marker === ' ' || line === '') {
        beforeText.push(body);
        afterText.push(body);
        hunk.lines.push({ kind: 'context', text: body, beforeLine, afterLine });
        beforeLine += 1;
        afterLine += 1;
        beforeRemaining -= 1;
        afterRemaining -= 1;
        continue;
      }
      // Anything else ends the hunk and is re-read as a header line below.
      hunk = null;
      beforeRemaining = 0;
      afterRemaining = 0;
    }

    // ── header / marker lines ───────────────────────────────────────────────
    if (line.startsWith('## ')) {
      ensureSection(line.slice(3).trim());
      continue;
    }

    const hunkMatch = HUNK_HEADER.exec(line);
    if (hunkMatch) {
      const current = section ?? ensureSection('');
      beforeLine = Number(hunkMatch[1]);
      beforeRemaining = hunkMatch[2] === undefined ? 1 : Number(hunkMatch[2]);
      afterLine = Number(hunkMatch[3]);
      afterRemaining = hunkMatch[4] === undefined ? 1 : Number(hunkMatch[4]);
      hunk = {
        header: line,
        heading: (hunkMatch[5] ?? '').trim(),
        beforeStart: beforeLine,
        beforeCount: beforeRemaining,
        afterStart: afterLine,
        afterCount: afterRemaining,
        lines: []
      };
      current.hunks.push(hunk);
      continue;
    }

    if (line.startsWith('diff --git ')) {
      // Stay in whatever section we are already in (`ensureSection` with the
      // same label reuses it); start an unlabeled one only when there is none.
      if (section === null) ensureSection('');
      continue;
    }
    if (line.startsWith('--- ')) {
      const current = section ?? ensureSection('');
      const path = cleanPath(line.slice(4));
      current.oldPath = path;
      if (path === null) current.isNewFile = true;
      continue;
    }
    if (line.startsWith('+++ ')) {
      const current = section ?? ensureSection('');
      const path = cleanPath(line.slice(4));
      current.newPath = path;
      if (path === null) current.isDeletedFile = true;
      continue;
    }
    if (line.startsWith('rename from ')) {
      (section ?? ensureSection('')).oldPath = cleanPath(line.slice('rename from '.length));
      continue;
    }
    if (line.startsWith('rename to ')) {
      (section ?? ensureSection('')).newPath = cleanPath(line.slice('rename to '.length));
      continue;
    }
    if (line.startsWith('new file mode')) {
      (section ?? ensureSection('')).isNewFile = true;
      continue;
    }
    if (line.startsWith('deleted file mode')) {
      (section ?? ensureSection('')).isDeletedFile = true;
      continue;
    }
    if (line.startsWith('Binary files ') || line.startsWith('GIT binary patch')) {
      (section ?? ensureSection('')).isBinary = true;
      continue;
    }
  }

  flushSection();

  // Drop a section that carried nothing at all (e.g. text that was only markers).
  const kept = sections.filter(
    (entry) =>
      entry.hunks.length > 0 ||
      entry.isBinary ||
      entry.isNewFile ||
      entry.isDeletedFile ||
      entry.oldPath !== null ||
      entry.newPath !== null
  );

  const first = kept[0] ?? null;
  const last = kept[kept.length - 1] ?? null;

  return {
    sections: kept,
    hunks: kept.flatMap((entry) => entry.hunks),
    before: first?.before ?? '',
    after: last?.after ?? '',
    addedCount: kept.reduce((total, entry) => total + entry.addedCount, 0),
    removedCount: kept.reduce((total, entry) => total + entry.removedCount, 0),
    isBinary: kept.some((entry) => entry.isBinary),
    isNewFile: kept.some((entry) => entry.isNewFile),
    isDeletedFile: kept.some((entry) => entry.isDeletedFile),
    oldPath: first?.oldPath ?? null,
    newPath: last?.newPath ?? null,
    isEmpty: kept.every((entry) => entry.hunks.length === 0 && !entry.isBinary)
  };
}

function lineWord(count: number): string {
  return count === 1 ? 'line' : 'lines';
}

/** One plain-English line describing what the diff contains. */
export function summarizeParsedDiff(parsed: ParsedDiff): string {
  if (parsed.isBinary) return 'This is a binary file, so there is no line-by-line comparison.';
  if (parsed.isEmpty) return 'No line changes to show.';

  const parts: string[] = [];
  if (parsed.addedCount > 0) {
    parts.push(`${parsed.addedCount} ${lineWord(parsed.addedCount)} added`);
  }
  if (parsed.removedCount > 0) {
    parts.push(`${parsed.removedCount} ${lineWord(parsed.removedCount)} removed`);
  }
  if (parts.length === 0) return 'No line changes to show.';
  if (parsed.isNewFile) parts.push('new file');
  if (parsed.isDeletedFile) parts.push('file deleted');
  return parts.join(' · ');
}
