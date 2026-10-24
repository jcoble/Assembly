/**
 * editorLanguage.ts — the last word on "which language is this file?" for the
 * /next editor, for the file types the existing mapping does not know.
 *
 * WHY THIS EXISTS
 * `sourceRecordFromPath.ts` decides a file's language from its extension, and
 * anything it does not recognise comes back as `plain` — which the editor shows
 * with no colouring at all. Its list stops short of a good number of file types
 * the code editor can already highlight perfectly well, and `.sql` was the one
 * that gave it away: SQL files opened as flat grey text even though the editor
 * has had the SQL rules loaded the whole time.
 *
 * So this is a SECOND CHANCE, not a replacement. It is only consulted for files
 * that came back as `plain`, so every file that already opened correctly keeps
 * opening exactly as it did.
 *
 * HONEST BY CONSTRUCTION
 * The source record keeps the precise language name even when CodeMirror has
 * no dedicated parser for it. `codeMirrorLanguage.ts` is the single place that
 * decides which of those names has syntax colouring.
 *
 * PURE: no Svelte, no DOM, no backend — covered by
 * `scripts/explorerFileIcons.test.mjs`.
 */
import type { SourceLanguage } from '../../../../sourceData.ts';

/** What the caller passes in and gets back when nothing here recognises the file. */
export const UNKNOWN_LANGUAGE: SourceLanguage = 'plain';

/**
 * Whole file names that name their own type. Checked first, so `Dockerfile`
 * works even though it has no extension at all.
 */
const BY_FILE_NAME: Record<string, SourceLanguage> = {
  dockerfile: 'dockerfile',
  containerfile: 'dockerfile',
  // The editor has no rules for Make. The shell rules are the closest honest
  // match — they get the comments, the strings and the `$(VAR)` references, and
  // leave the rule syntax uncoloured.
  makefile: 'shell',
  '.env': 'ini',
  '.gitignore': 'ini',
  '.npmrc': 'ini',
  '.editorconfig': 'ini'
};

/**
 * Extension → language, for extensions `sourceRecordFromPath` does not cover.
 * The values are the editor's own language names, so they can be handed
 * straight to it.
 */
const BY_EXTENSION: Record<string, SourceLanguage> = {
  sql: 'sql',

  bash: 'shell',
  fish: 'shell',
  ps1: 'powershell',
  psm1: 'powershell',

  cjs: 'javascript',
  mts: 'typescript',
  cts: 'typescript',

  scss: 'scss',
  sass: 'scss',
  less: 'less',
  htm: 'html',
  vue: 'html',
  astro: 'html',
  svg: 'xml',
  xaml: 'xml',
  csproj: 'xml',
  fsproj: 'xml',
  vbproj: 'xml',
  props: 'xml',
  targets: 'xml',
  plist: 'xml',
  razor: 'razor',
  cshtml: 'razor',

  py: 'python',
  pyi: 'python',
  go: 'go',
  java: 'java',
  kt: 'kotlin',
  kts: 'kotlin',
  rb: 'ruby',
  php: 'php',
  fs: 'fsharp',
  fsx: 'fsharp',
  lua: 'lua',
  dart: 'dart',
  c: 'cpp',
  h: 'cpp',
  hh: 'cpp',
  hpp: 'cpp',
  hxx: 'cpp',
  cc: 'cpp',
  cpp: 'cpp',
  cxx: 'cpp',
  m: 'cpp',
  mm: 'cpp',

  graphql: 'graphql',
  gql: 'graphql',
  proto: 'protobuf',
  tf: 'hcl',
  tfvars: 'hcl',
  hcl: 'hcl',

  ini: 'ini',
  env: 'ini',
  cfg: 'ini',
  conf: 'ini',
  properties: 'ini',

  jsonc: 'json',
  json5: 'json',
  markdown: 'markdown',
  mdx: 'mdx'
};

/**
 * The language for a file the usual mapping gave up on, or `plain` when this
 * one does not know it either.
 */
export function editorLanguageForPath(path: string): SourceLanguage {
  const fileName = path.split('/').filter(Boolean).at(-1)?.toLowerCase() ?? '';
  if (!fileName) return UNKNOWN_LANGUAGE;

  const byName = BY_FILE_NAME[fileName];
  if (byName) return byName;

  // A leading dot names the file rather than starting an extension.
  const dotIndex = fileName.lastIndexOf('.');
  if (dotIndex <= 0) return UNKNOWN_LANGUAGE;

  return BY_EXTENSION[fileName.slice(dotIndex + 1)] ?? UNKNOWN_LANGUAGE;
}

/**
 * A file's language, preferring whatever the caller already worked out. Only a
 * file that came back with no language at all is looked up again.
 */
export function upgradeUnknownLanguage(path: string, language: SourceLanguage): SourceLanguage {
  return language === UNKNOWN_LANGUAGE ? editorLanguageForPath(path) : language;
}
