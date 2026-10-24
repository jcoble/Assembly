/**
 * fileIcons.ts — which glyph and which colour a file name gets in the explorer.
 *
 * PURE on purpose: no Svelte, no icon imports, no DOM. It answers one question —
 * "given this file name, what should its row look like?" — and `FileIcon.svelte`
 * does the drawing. Keeping the table here means `scripts/explorerFileIcons.test.mjs`
 * can check every mapping in plain Node, which a `.svelte` file could not offer.
 *
 * Keyed on the FILE NAME, not on the scanned record's `language`. The scanner
 * reports `plain` for stylesheets, HTML, images and lock files, so a language-keyed
 * table would draw the same grey page for `app.css`, `index.html` and `pnpm-lock.yaml`.
 * The file name always knows better.
 *
 * Nothing is downloaded to draw these: the glyphs are lucide components already in
 * the bundle and the colours are CSS custom properties defined in `FileIcon.svelte` itself.
 * There is no icon font and no request to any other host — the app's content policy
 * would block one anyway.
 */

/** Which glyph the row draws. One glyph is shared by several file types; the
 * colour is what tells `.ts` from `.js` at a glance. */
export type FileIconKind =
  | 'code'
  | 'flame'
  | 'gear'
  | 'sharp'
  | 'palette'
  | 'markup'
  | 'braces'
  | 'document'
  | 'database'
  | 'terminal'
  | 'sliders'
  | 'image'
  | 'lock'
  | 'bird'
  | 'gem'
  | 'coffee'
  | 'container'
  | 'table'
  | 'binary'
  | 'page';

/** Colour family for the glyph. Each name has a matching `--file-tone-<name>`
 * custom property; an unknown one falls back to the neutral tone. */
export type FileIconTone =
  | 'svelte'
  | 'typescript'
  | 'javascript'
  | 'rust'
  | 'csharp'
  | 'style'
  | 'markup'
  | 'data'
  | 'document'
  | 'database'
  | 'shell'
  | 'config'
  | 'image'
  | 'python'
  | 'go'
  | 'java'
  | 'ruby'
  | 'swift'
  | 'php'
  | 'neutral';

export interface FileIconStyle {
  kind: FileIconKind;
  tone: FileIconTone;
  /** What this file is, in words. Shown in the row's tooltip and read aloud by
   * screen readers, so it has to make sense to someone who does not code. */
  label: string;
}

function style(kind: FileIconKind, tone: FileIconTone, label: string): FileIconStyle {
  return { kind, tone, label };
}

/** Anything the table below has no entry for. */
export const DEFAULT_FILE_ICON: FileIconStyle = style('page', 'neutral', 'File');

/**
 * Whole file names that mean something specific regardless of their extension.
 * Checked before extensions, so `pnpm-lock.yaml` reads as a lock file rather
 * than as settings.
 */
const BY_FILE_NAME: Record<string, FileIconStyle> = {
  'package.json': style('braces', 'data', 'Project manifest'),
  'package-lock.json': style('lock', 'neutral', 'Lock file — the exact versions installed'),
  'pnpm-lock.yaml': style('lock', 'neutral', 'Lock file — the exact versions installed'),
  'yarn.lock': style('lock', 'neutral', 'Lock file — the exact versions installed'),
  'cargo.lock': style('lock', 'neutral', 'Lock file — the exact versions installed'),
  'cargo.toml': style('gear', 'rust', 'Rust project settings'),
  'dockerfile': style('container', 'config', 'Docker build recipe'),
  'makefile': style('terminal', 'shell', 'Make build rules'),
  'procfile': style('terminal', 'shell', 'Process list'),
  '.gitignore': style('sliders', 'config', 'Files git should ignore'),
  '.gitattributes': style('sliders', 'config', 'Git file settings'),
  '.env': style('sliders', 'config', 'Environment settings'),
  '.editorconfig': style('sliders', 'config', 'Editor settings'),
  '.npmrc': style('sliders', 'config', 'Package manager settings'),
  '.prettierrc': style('sliders', 'config', 'Formatting settings'),
  'license': style('document', 'document', 'Licence'),
  'readme.md': style('document', 'document', 'Read me first')
};

/** Extension → look. Lower-case, without the dot. */
const BY_EXTENSION: Record<string, FileIconStyle> = {
  svelte: style('flame', 'svelte', 'Svelte component'),

  ts: style('code', 'typescript', 'TypeScript'),
  mts: style('code', 'typescript', 'TypeScript'),
  cts: style('code', 'typescript', 'TypeScript'),
  tsx: style('code', 'typescript', 'TypeScript with markup'),
  js: style('code', 'javascript', 'JavaScript'),
  mjs: style('code', 'javascript', 'JavaScript'),
  cjs: style('code', 'javascript', 'JavaScript'),
  jsx: style('code', 'javascript', 'JavaScript with markup'),

  rs: style('gear', 'rust', 'Rust'),
  cs: style('sharp', 'csharp', 'C#'),
  csproj: style('sliders', 'csharp', 'C# project settings'),
  sln: style('sliders', 'csharp', 'C# solution'),
  fs: style('sharp', 'csharp', 'F#'),
  razor: style('markup', 'csharp', 'Razor page'),
  cshtml: style('markup', 'csharp', 'Razor page'),

  css: style('palette', 'style', 'Stylesheet'),
  scss: style('palette', 'style', 'Stylesheet'),
  sass: style('palette', 'style', 'Stylesheet'),
  less: style('palette', 'style', 'Stylesheet'),

  html: style('markup', 'markup', 'Web page'),
  htm: style('markup', 'markup', 'Web page'),
  xml: style('markup', 'markup', 'XML'),
  xaml: style('markup', 'markup', 'XAML layout'),
  vue: style('markup', 'markup', 'Vue component'),

  json: style('braces', 'data', 'JSON data'),
  jsonc: style('braces', 'data', 'JSON data'),
  json5: style('braces', 'data', 'JSON data'),
  yaml: style('sliders', 'config', 'YAML settings'),
  yml: style('sliders', 'config', 'YAML settings'),
  toml: style('sliders', 'config', 'TOML settings'),
  ini: style('sliders', 'config', 'Settings'),
  conf: style('sliders', 'config', 'Settings'),
  config: style('sliders', 'config', 'Settings'),
  properties: style('sliders', 'config', 'Settings'),
  lock: style('lock', 'neutral', 'Lock file — the exact versions installed'),

  md: style('document', 'document', 'Markdown notes'),
  mdx: style('document', 'document', 'Markdown notes'),
  txt: style('document', 'document', 'Plain text'),
  rst: style('document', 'document', 'Plain text'),
  pdf: style('document', 'document', 'PDF'),

  sql: style('database', 'database', 'SQL'),
  db: style('database', 'database', 'Database file'),
  sqlite: style('database', 'database', 'Database file'),
  csv: style('table', 'data', 'Spreadsheet data'),
  tsv: style('table', 'data', 'Spreadsheet data'),

  sh: style('terminal', 'shell', 'Shell script'),
  bash: style('terminal', 'shell', 'Shell script'),
  zsh: style('terminal', 'shell', 'Shell script'),
  fish: style('terminal', 'shell', 'Shell script'),
  ps1: style('terminal', 'shell', 'PowerShell script'),
  bat: style('terminal', 'shell', 'Batch script'),
  cmd: style('terminal', 'shell', 'Batch script'),

  py: style('code', 'python', 'Python'),
  go: style('code', 'go', 'Go'),
  java: style('coffee', 'java', 'Java'),
  kt: style('coffee', 'java', 'Kotlin'),
  kts: style('coffee', 'java', 'Kotlin'),
  rb: style('gem', 'ruby', 'Ruby'),
  swift: style('bird', 'swift', 'Swift'),
  php: style('code', 'php', 'PHP'),
  c: style('code', 'neutral', 'C'),
  h: style('code', 'neutral', 'C header'),
  cpp: style('code', 'neutral', 'C++'),
  hpp: style('code', 'neutral', 'C++ header'),
  m: style('code', 'neutral', 'Objective-C'),
  mm: style('code', 'neutral', 'Objective-C++'),
  lua: style('code', 'neutral', 'Lua'),
  dart: style('code', 'neutral', 'Dart'),
  graphql: style('braces', 'data', 'GraphQL'),
  gql: style('braces', 'data', 'GraphQL'),
  proto: style('braces', 'data', 'Protocol buffer'),

  png: style('image', 'image', 'Image'),
  jpg: style('image', 'image', 'Image'),
  jpeg: style('image', 'image', 'Image'),
  gif: style('image', 'image', 'Image'),
  webp: style('image', 'image', 'Image'),
  avif: style('image', 'image', 'Image'),
  svg: style('image', 'image', 'Vector image'),
  ico: style('image', 'image', 'Icon'),
  icns: style('image', 'image', 'Icon'),

  wasm: style('binary', 'neutral', 'Compiled program'),
  bin: style('binary', 'neutral', 'Compiled program'),
  zip: style('binary', 'neutral', 'Archive'),
  gz: style('binary', 'neutral', 'Archive'),
  tar: style('binary', 'neutral', 'Archive')
};

/**
 * The look for one file name. Whole-name matches win over extensions, and
 * `.d.ts` is read as TypeScript rather than as an unknown `.ts`-something.
 */
export function fileIconForName(fileName: string): FileIconStyle {
  const name = fileName.trim().toLowerCase();
  if (!name) return DEFAULT_FILE_ICON;

  const byName = BY_FILE_NAME[name];
  if (byName) return byName;

  // A leading dot names the file (`.gitignore`), it does not start an extension,
  // so the search for the last dot skips position zero.
  const dotIndex = name.lastIndexOf('.');
  if (dotIndex <= 0) return DEFAULT_FILE_ICON;

  return BY_EXTENSION[name.slice(dotIndex + 1)] ?? DEFAULT_FILE_ICON;
}
