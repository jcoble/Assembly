<script lang="ts">
  /**
   * FileIcon.svelte — the small coloured glyph in front of a file name.
   *
   * Which glyph and which colour is decided by `fileIcons.ts`; this component
   * only draws it. The glyphs are lucide icons that are already part of the app,
   * and the colours are declared right here — nothing is fetched from anywhere,
   * which is what the app's strict content policy requires.
   *
   * Self-contained on purpose: drop it into any panel and the colours come with
   * it, so the explorer tree and the editor's tab strip cannot drift apart.
   */
  import Binary from '@lucide/svelte/icons/binary';
  import Bird from '@lucide/svelte/icons/bird';
  import Braces from '@lucide/svelte/icons/braces';
  import CodeXml from '@lucide/svelte/icons/code-xml';
  import Coffee from '@lucide/svelte/icons/coffee';
  import Cog from '@lucide/svelte/icons/cog';
  import Container from '@lucide/svelte/icons/container';
  import Database from '@lucide/svelte/icons/database';
  import FileGlyph from '@lucide/svelte/icons/file';
  import FileCode from '@lucide/svelte/icons/file-code';
  import FileText from '@lucide/svelte/icons/file-text';
  import Flame from '@lucide/svelte/icons/flame';
  import Gem from '@lucide/svelte/icons/gem';
  import Hash from '@lucide/svelte/icons/hash';
  import ImageGlyph from '@lucide/svelte/icons/image';
  import Lock from '@lucide/svelte/icons/lock';
  import Palette from '@lucide/svelte/icons/palette';
  import SlidersHorizontal from '@lucide/svelte/icons/sliders-horizontal';
  import SquareTerminal from '@lucide/svelte/icons/square-terminal';
  import Table from '@lucide/svelte/icons/table';

  import { fileIconForName, type FileIconKind, type FileIconStyle } from './fileIcons.ts';

  type IconComponent = typeof FileGlyph;

  const GLYPHS: Record<FileIconKind, IconComponent> = {
    code: FileCode,
    flame: Flame,
    gear: Cog,
    sharp: Hash,
    palette: Palette,
    markup: CodeXml,
    braces: Braces,
    document: FileText,
    database: Database,
    terminal: SquareTerminal,
    sliders: SlidersHorizontal,
    image: ImageGlyph,
    lock: Lock,
    bird: Bird,
    gem: Gem,
    coffee: Coffee,
    container: Container,
    table: Table,
    binary: Binary,
    page: FileGlyph
  };

  interface Props {
    /** The file's name, e.g. `ExplorerPanel.svelte`. */
    fileName: string;
    /** Icon width and height in pixels. */
    size?: number;
  }
  let { fileName, size = 14 }: Props = $props();

  const look = $derived<FileIconStyle>(fileIconForName(fileName));
  const Glyph = $derived(GLYPHS[look.kind]);
</script>

<span
  class="file-icon"
  style={`--icon-color: var(--file-tone-${look.tone}); --icon-size: ${size}px`}
  title={look.label}
>
  <Glyph size={size} strokeWidth={1.75} aria-hidden="true" />
</span>

<style>
  /* The file-type palette. Tuned for the shell's dark background; each name is
   * the `tone` that `fileIcons.ts` returns. */
  .file-icon {
    --file-tone-svelte: #ff6b4a;
    --file-tone-typescript: #62aeff;
    --file-tone-javascript: #f2d367;
    --file-tone-rust: #e3a06a;
    --file-tone-csharp: #b58cf0;
    --file-tone-style: #6fd0f5;
    --file-tone-markup: #ff9e64;
    --file-tone-data: #cbe06a;
    --file-tone-document: #a9b8d6;
    --file-tone-database: #56d6c2;
    --file-tone-shell: #8bdc9b;
    --file-tone-config: #9aa3b5;
    --file-tone-image: #ee8fd4;
    --file-tone-python: #7cc0ff;
    --file-tone-go: #63d9e8;
    --file-tone-java: #f08a5d;
    --file-tone-ruby: #ff6d91;
    --file-tone-swift: #ff9366;
    --file-tone-php: #9d8cf5;
    --file-tone-neutral: #767d8c;

    display: inline-flex;
    align-items: center;
    justify-content: center;
    flex: 0 0 auto;
    width: var(--icon-size, 14px);
    height: var(--icon-size, 14px);
    /* An unknown tone leaves `--icon-color` invalid, and the row's own colour
     * shows through rather than nothing at all. */
    color: var(--icon-color, currentColor);
  }
</style>
