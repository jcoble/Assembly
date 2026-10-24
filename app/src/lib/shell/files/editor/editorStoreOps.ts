/**
 * editorStoreOps.ts — the editor store's rules, as pure functions.
 *
 * Lives apart from `editorStore.svelte.ts` for ONE reason: a runes module
 * cannot be imported by a plain Node test (`$state` is a compiler keyword, not
 * a function), so every decision worth testing — where a newly opened file
 * lands in the strip, which file becomes active when one is closed, how a
 * "reveal this line" request is delivered to Monaco — lives here and is
 * covered by `scripts/editorStore.test.ts`. The runes module is then a thin
 * shell: hold state, call these, assign the result.
 *
 * No IO, no Svelte, no mutation of the arrays passed in: every function
 * returns a new array.
 */
import type { SourceLanguage, SourcePreview, SourceRecord } from '../../../sourceData.ts';

/** One file open in the editor strip. */
export interface OpenEditorFile {
  /** Absolute path; the identity of the entry. */
  path: string;
  fileName: string;
  relativePath: string;
  language: SourceLanguage;
  /** File contents once read. `null` until the read finishes. */
  preview: SourcePreview | null;
  /** Current editor text. It can differ from `preview.content` until saved. */
  draftContent: string | null;
  /** Whether `draftContent` differs from the last content read or saved. */
  dirty: boolean;
  /** Disk revision the draft was edited from; kept with a stored draft. */
  baseRevision: string | null;
  /** A single-click file tab that becomes permanent on edit, double-click or pin. */
  previewTab: boolean;
  /** A dirty draft and the file on disk both changed since the last clean baseline. */
  conflict: string | null;
  /** A native write is in flight. */
  saving: boolean;
  /** A read is in flight. */
  loading: boolean;
  /** Why the last read failed, in plain words, or `null`. */
  error: string | null;
  /** 1-based line Monaco should scroll to, or `null` for "wherever it was". */
  targetLine: number | null;
  /**
   * Bumped every time a reveal is requested. Monaco re-scrolls when this
   * number changes, so asking for the SAME line twice still scrolls.
   */
  targetLineRequestId: number;
}

/** Fields of an open file that a load or a reveal may change. */
export interface OpenEditorFilePatch {
  preview?: SourcePreview | null;
  draftContent?: string | null;
  dirty?: boolean;
  baseRevision?: string | null;
  conflict?: string | null;
  saving?: boolean;
  loading?: boolean;
  error?: string | null;
  targetLine?: number | null;
  previewTab?: boolean;
}

export interface OpenEditorFileOptions {
  /** Reuse the current unpinned preview tab instead of opening a permanent tab. */
  preview?: boolean;
  /** Make the tab permanent even when it was opened as a preview before. */
  pin?: boolean;
}

/** A fresh, not-yet-read entry for `record`. */
export function openEditorFileFromRecord(record: SourceRecord): OpenEditorFile {
  return {
    path: record.path,
    fileName: record.fileName,
    relativePath: record.relativePath,
    language: record.language,
    preview: null,
    draftContent: null,
    dirty: false,
    baseRevision: null,
    previewTab: false,
    conflict: null,
    saving: false,
    loading: false,
    error: null,
    targetLine: null,
    targetLineRequestId: 0
  };
}

/** The entry for `path`, or `null`. */
export function findOpenFile(
  files: readonly OpenEditorFile[],
  path: string
): OpenEditorFile | null {
  return files.find((file) => file.path === path) ?? null;
}

/** Is `path` already open? */
export function isFileOpen(files: readonly OpenEditorFile[], path: string): boolean {
  return files.some((file) => file.path === path);
}

/**
 * Add `record` to the strip if it is not there yet. Re-opening a file that is
 * already open keeps its place AND its loaded contents — reopening must never
 * cost a second read.
 */
export function upsertOpenFile(
  files: readonly OpenEditorFile[],
  record: SourceRecord,
  options: OpenEditorFileOptions = {}
): OpenEditorFile[] {
  const existing = findOpenFile(files, record.path);
  if (existing) {
    return options.pin || !options.preview
      ? patchOpenFile(files, record.path, { previewTab: false })
      : [...files];
  }
  const entry = {
    ...openEditorFileFromRecord(record),
    previewTab: Boolean(options.preview && !options.pin)
  };
  if (!entry.previewTab) return [...files, entry];
  const previewIndex = files.findIndex((file) => file.previewTab);
  if (previewIndex < 0) return [...files, entry];
  return files.map((file, index) => (index === previewIndex ? entry : file));
}

/** Apply `patch` to the entry for `path`; unknown paths change nothing. */
export function patchOpenFile(
  files: readonly OpenEditorFile[],
  path: string,
  patch: OpenEditorFilePatch
): OpenEditorFile[] {
  const draftMutation = patch.draftContent !== undefined && patch.preview === undefined;
  return files.map((file) => file.path === path
    ? { ...file, ...patch, previewTab: file.previewTab && draftMutation ? false : patch.previewTab ?? file.previewTab }
    : file);
}

/**
 * Ask Monaco to scroll `path` to `line`. The request id always moves, so a
 * repeat request for the same line scrolls again (a second click on the same
 * search hit has to go somewhere).
 */
export function revealLineInOpenFile(
  files: readonly OpenEditorFile[],
  path: string,
  line: number | null
): OpenEditorFile[] {
  return files.map((file) =>
    file.path === path
      ? { ...file, targetLine: line, targetLineRequestId: file.targetLineRequestId + 1 }
      : file
  );
}

/** Drop `path` from the strip. */
export function closeOpenFile(
  files: readonly OpenEditorFile[],
  path: string
): OpenEditorFile[] {
  return files.filter((file) => file.path !== path);
}

/** A clean closed tab can release its Monaco model; a draft cannot. */
export function modelPathToDisposeOnClose(
  file: Pick<OpenEditorFile, 'path' | 'dirty'> | null
): string | null {
  return file && !file.dirty ? file.path : null;
}

/**
 * Which file is shown after `closedPath` is closed. `files` is the strip as it
 * stands BEFORE the close, so position is still known. Closing an inactive file
 * leaves the active one alone; closing the active one falls to its right-hand
 * neighbour, then its left-hand one, then nothing.
 */
export function activePathAfterClose(
  files: readonly OpenEditorFile[],
  closedPath: string,
  activePath: string | null
): string | null {
  if (activePath !== closedPath) {
    return activePath !== null && isFileOpen(files, activePath) ? activePath : null;
  }
  const closedIndex = files.findIndex((file) => file.path === closedPath);
  if (closedIndex < 0) return activePath;
  const remaining = closeOpenFile(files, closedPath);
  return remaining[closedIndex]?.path ?? remaining[closedIndex - 1]?.path ?? null;
}

/** Does this file still need reading? (Never read, not reading, no error.) */
export function needsRead(file: OpenEditorFile): boolean {
  return file.preview === null && !file.loading && file.error === null;
}
