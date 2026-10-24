import assert from 'node:assert/strict';
import test from 'node:test';

import { codeMirrorThemeForAppearance } from './codeMirrorTheme.ts';

const appearance = { fontFamily: 'system', fontSize: 14, lineHeight: 22, fontLigatures: false };

// CodeMirror never removes a theme's style rules, so each new theme object adds rules for good.
test('the same appearance reuses one theme extension', () => {
  assert.equal(codeMirrorThemeForAppearance({ ...appearance }), codeMirrorThemeForAppearance({ ...appearance }));
});

test('a different appearance gets its own theme extension', () => {
  assert.notEqual(
    codeMirrorThemeForAppearance(appearance),
    codeMirrorThemeForAppearance({ ...appearance, fontSize: 15 })
  );
});
