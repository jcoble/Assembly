/**
 * sourceRecordFromPath.ts — the "open this file by path" bridge for /next.
 *
 * PURE: no IO, no runes, no Svelte. Turns a bare absolute path (what the
 * open-file bus carries) plus the project root into the `SourceRecord` the
 * backend read wrapper needs. `readSourceFromTauri` re-overlays
 * `relativePath` / `language` / `byteCount` from the record it is handed, so
 * the record must always be built FIRST — otherwise the preview comes back
 * with the wrong language and the editor highlights it as plain text.
 *
 * Ported from the old shell's `sourceRecordFromRestoredPath`
 * (`src/routes/+page.svelte:6769`). Behaviour is unchanged, with two
 * differences that are deliberate:
 *  - the already-scanned record list is an optional argument instead of a
 *    page-local (`/next` has no project scan of its own yet), and
 *  - a missing project root is tolerated: the record then carries just the
 *    file name as its relative path.
 */
import { normalizeProjectPath, type SourceLanguage, type SourceRecord } from '../../../sourceData.ts';

/** Last path segment, or the word "file" when the path has no segments. */
export function fileNameFromPath(path: string): string {
  return path.split('/').filter(Boolean).at(-1) ?? 'file';
}

/**
 * Language id for a path, by extension. Kept as its own copy of the old
 * shell's mapping rather than `sourceLanguageForPath` from `sourceData`,
 * because that one does not know `.mjs`, `.css` or `.html` and would open
 * those files as plain text.
 */
export function sourceLanguageForOpenPath(path: string): SourceLanguage {
  const normalizedPath = path.toLowerCase();
  if (normalizedPath.endsWith('.cs')) return 'csharp';
  if (normalizedPath.endsWith('.tsx')) return 'tsx';
  if (normalizedPath.endsWith('.ts')) return 'typescript';
  if (normalizedPath.endsWith('.jsx')) return 'jsx';
  if (normalizedPath.endsWith('.js') || normalizedPath.endsWith('.mjs')) return 'javascript';
  if (normalizedPath.endsWith('.svelte')) return 'svelte';
  if (normalizedPath.endsWith('.rs')) return 'rust';
  if (normalizedPath.endsWith('.swift')) return 'swift';
  if (normalizedPath.endsWith('.json')) return 'json';
  if (normalizedPath.endsWith('.md') || normalizedPath.endsWith('.mdx')) return 'markdown';
  if (normalizedPath.endsWith('.toml')) return 'toml';
  if (normalizedPath.endsWith('.yaml') || normalizedPath.endsWith('.yml')) return 'yaml';
  if (normalizedPath.endsWith('.css')) return 'css';
  if (normalizedPath.endsWith('.html')) return 'html';
  if (normalizedPath.endsWith('.xml')) return 'xml';
  if (normalizedPath.endsWith('.sh') || normalizedPath.endsWith('.zsh')) return 'shell';
  return 'plain';
}

/**
 * Build the record for `path`. When the same file is already in
 * `knownRecords` (a project scan produced it) that record wins, so its real
 * byte count and relative path survive.
 */
export function sourceRecordFromPath(
  projectRoot: string | null | undefined,
  path: string,
  knownRecords: readonly SourceRecord[] = []
): SourceRecord {
  const normalizedPath = normalizeProjectPath(path);
  const existingRecord = knownRecords.find(
    (record) => normalizeProjectPath(record.path) === normalizedPath
  );
  if (existingRecord) return existingRecord;

  const normalizedRoot = projectRoot ? normalizeProjectPath(projectRoot) : '';
  const relativePath =
    normalizedRoot.length === 0 || normalizedPath === normalizedRoot
      ? fileNameFromPath(normalizedPath)
      : normalizedPath.startsWith(`${normalizedRoot}/`)
        ? normalizedPath.slice(normalizedRoot.length + 1)
        : fileNameFromPath(normalizedPath);

  return {
    path: normalizedPath,
    relativePath,
    fileName: fileNameFromPath(relativePath),
    language: sourceLanguageForOpenPath(normalizedPath),
    byteCount: 0
  };
}
