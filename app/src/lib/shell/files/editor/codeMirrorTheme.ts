import type { Extension } from '@codemirror/state';
import { HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { EditorView } from '@codemirror/view';
import { tags } from '@lezer/highlight';

import { getMonoFont } from '../../settings/themes/fontRegistry.ts';

const syntax = HighlightStyle.define([
  { tag: [tags.keyword, tags.modifier], color: '#54b9ff' },
  { tag: [tags.name, tags.variableName], color: '#4bf3c8' },
  { tag: [tags.definition(tags.name), tags.function(tags.variableName)], color: '#00daef' },
  { tag: [tags.typeName, tags.className, tags.namespace], color: '#acafff' },
  { tag: [tags.string, tags.regexp], color: '#ffd493' },
  { tag: tags.number, color: '#ffd493' },
  { tag: [tags.bool, tags.null], color: '#54b9ff' },
  { tag: [tags.comment, tags.meta], color: '#8f9199', fontStyle: 'italic' },
  { tag: [tags.operator, tags.punctuation], color: '#eef0f9' },
  { tag: [tags.propertyName, tags.attributeName], color: '#4bf3c8' },
  { tag: tags.invalid, color: '#f06788', textDecoration: 'underline' }
]);

export interface CodeMirrorAppearance {
  fontFamily: string;
  fontSize: number;
  lineHeight: number;
  fontLigatures: boolean;
}

const defaultAppearance: CodeMirrorAppearance = {
  fontFamily: 'system',
  fontSize: 13,
  lineHeight: 21,
  fontLigatures: false
};

// CodeMirror adds a theme's style rules to the page and never removes them, so
// build each distinct appearance once and hand back the same theme after that.
const themesByAppearance = new Map<string, Extension>();

/** Build the editor chrome from current settings so an open editor repaints live. */
export function codeMirrorThemeForAppearance(
  appearance: CodeMirrorAppearance = defaultAppearance
): Extension {
  const key = `${appearance.fontFamily}|${appearance.fontSize}|${appearance.lineHeight}|${appearance.fontLigatures}`;
  const cached = themesByAppearance.get(key);
  if (cached) return cached;
  const editorFontFamily = getMonoFont(appearance.fontFamily).stack;
  const theme = [
  EditorView.theme(
    {
      '&': {
        height: '100%',
        backgroundColor: 'var(--card)',
        color: 'var(--foreground)',
        fontSize: `${appearance.fontSize}px`
      },
      '.cm-scroller': {
        overflow: 'auto',
        fontFamily: editorFontFamily,
        fontVariantLigatures: appearance.fontLigatures ? 'normal' : 'none',
        lineHeight: `${appearance.lineHeight}px`
      },
      '.cm-content': { caretColor: 'var(--color-accent, #82aaff)' },
      '.cm-cursor, .cm-dropCursor': {
        borderLeftColor: 'var(--color-accent, #82aaff)'
      },
      '.cm-selectionBackground, ::selection': {
        backgroundColor: 'var(--color-text-selection) !important'
      },
      // Translucent so the selection layer underneath still shows on the cursor's line.
      '.cm-activeLine': { backgroundColor: 'color-mix(in srgb, var(--color-text) 5%, transparent)' },
      '.cm-gutters': {
        backgroundColor: 'var(--card)',
        color: '#545864',
        borderRight: 'none'
      },
      '.cm-activeLineGutter': {
        backgroundColor: '#23262d',
        color: '#858b98'
      },
      '.cm-tooltip': {
        backgroundColor: 'var(--color-elevated, #20242c)',
        color: 'var(--color-text, #d8dee9)',
        border: '1px solid var(--color-border, #303641)'
      },
      '.cm-tooltip-autocomplete > ul > li[aria-selected]': {
        backgroundColor: 'color-mix(in srgb, var(--color-accent, #82aaff) 22%, transparent)',
        color: 'var(--color-text, #d8dee9)'
      },
      '.cm-panels': {
        backgroundColor: 'var(--color-elevated, #20242c)',
        color: 'var(--color-text, #d8dee9)'
      }
    },
    { dark: true }
  ),
  syntaxHighlighting(syntax)
  ];
  themesByAppearance.set(key, theme);
  return theme;
}

export const codeMirrorTheme = codeMirrorThemeForAppearance();

type ThemeMirrorModule = typeof import('thememirror');
type ThemeLoader = (themes: ThemeMirrorModule) => Extension;

const themeLoaders: Record<string, ThemeLoader> = {
  amy: ({ amy }) => amy,
  'ayu-light': ({ ayuLight }) => ayuLight,
  cobalt: ({ cobalt }) => cobalt,
  dracula: ({ dracula }) => dracula,
  'rose-pine-dawn': ({ rosePineDawn }) => rosePineDawn,
  tomorrow: ({ tomorrow }) => tomorrow
};

/** Resolve the selected CodeMirror extension, keeping Houston as the fallback. */
export async function loadCodeMirrorTheme(
  themeId: unknown,
  appearance: CodeMirrorAppearance = defaultAppearance
): Promise<Extension> {
  const base = codeMirrorThemeForAppearance(appearance);
  const loader = themeLoaders[typeof themeId === 'string' ? themeId : ''];
  if (!loader) return base;
  // Keep ThemeMirror, including its catalog, out of the startup chunk.
  const themes = await import('thememirror');
  // CodeMirror reverses theme style modules when it mounts them. Put the
  // selected theme first so its equal-specificity rules are mounted last.
  return [loader(themes), base];
}
