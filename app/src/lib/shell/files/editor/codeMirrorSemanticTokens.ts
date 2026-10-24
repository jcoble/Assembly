import { StateEffect, StateField, type EditorState, type Extension } from '@codemirror/state';
import { Decoration, EditorView, type DecorationSet } from '@codemirror/view';

import type { SourceSemanticToken } from '$lib/sourceData';

const tokenClass: Record<SourceSemanticToken['tokenType'], string> = {
  namespace: 'cm-semantic-type',
  class: 'cm-semantic-type',
  interface: 'cm-semantic-type',
  type: 'cm-semantic-type',
  enum: 'cm-semantic-type',
  typeParameter: 'cm-semantic-type',
  function: 'cm-semantic-function',
  method: 'cm-semantic-function',
  property: 'cm-semantic-property',
  variable: 'cm-semantic-variable',
  parameter: 'cm-semantic-variable',
  enumMember: 'cm-semantic-number',
  keyword: 'cm-semantic-keyword',
  string: 'cm-semantic-string',
  number: 'cm-semantic-number',
  operator: 'cm-semantic-operator',
  comment: 'cm-semantic-comment'
};

const tokenColor: Record<SourceSemanticToken['tokenType'], string> = {
  namespace: '#ffcb6b', class: '#ffcb6b', interface: '#ffcb6b', type: '#ffcb6b',
  enum: '#ffcb6b', typeParameter: '#ffcb6b', function: '#82aaff', method: '#82aaff',
  property: '#f07178', variable: '#d8dee9', parameter: '#d8dee9', enumMember: '#f78c6c',
  keyword: '#c792ea', string: '#c3e88d', number: '#f78c6c', operator: '#89ddff',
  comment: '#6d7480'
};

export function semanticTokenDecorations(
  state: EditorState,
  tokens: readonly SourceSemanticToken[]
): DecorationSet {
  const ranges = tokens.flatMap((token) => {
    if (token.line < 1 || token.line > state.doc.lines || token.length < 1) return [];
    const line = state.doc.line(token.line);
    const from = line.from + Math.max(0, token.startColumn - 1);
    if (from >= line.to) return [];
    const to = Math.min(line.to, from + token.length);
    return [Decoration.mark({
      class: tokenClass[token.tokenType],
      attributes: {
        style: `color: ${tokenColor[token.tokenType]}${token.tokenType === 'comment' ? '; font-style: italic' : ''}`
      }
    }).range(from, to)];
  });
  return Decoration.set(ranges, true);
}

export const setCodeMirrorSemanticTokens = StateEffect.define<DecorationSet>();

const semanticTokens = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(tokens, transaction) {
    for (const effect of transaction.effects) {
      if (effect.is(setCodeMirrorSemanticTokens)) return effect.value;
    }
    return tokens.map(transaction.changes);
  },
  provide: (field) => EditorView.decorations.from(field)
});

export const codeMirrorSemanticTokens: Extension = semanticTokens;
