import assert from 'node:assert/strict';
import test from 'node:test';

import {
  enhancedCSharpTokenStyle,
  loadCodeMirrorLanguage
} from './codeMirrorLanguage.ts';

test('every configured CodeMirror language loader resolves', async () => {
  for (const language of [
    'javascript', 'jsx', 'typescript', 'tsx', 'json', 'css', 'html', 'svelte',
    'markdown', 'python', 'rust', 'sql', 'xml', 'yaml', 'java', 'cpp', 'csharp',
    'kotlin', 'dart', 'fsharp', 'go', 'shell', 'powershell', 'ruby', 'lua', 'swift',
    'toml', 'ini', 'scss', 'less', 'dockerfile', 'protobuf'
  ]) {
    assert.notDeepEqual(await loadCodeMirrorLanguage(language), [], `${language} must load`);
  }
});

test('C# lexical highlighting distinguishes types, calls, and member properties', () => {
  assert.equal(enhancedCSharpTokenStyle('variable', 'FormatDetector', 'private ', ' value'), 'type');
  assert.equal(enhancedCSharpTokenStyle('variable', 'FormatDetector', 'new ', '()'), 'type');
  assert.equal(enhancedCSharpTokenStyle('variable', 'DetectAsync', 'detector.', '()'), 'def');
  assert.equal(enhancedCSharpTokenStyle('variable', 'Current', 'detector.', ';'), 'property');
  assert.equal(enhancedCSharpTokenStyle('variable', 'Compute', '', '(value)'), 'def');
  assert.equal(enhancedCSharpTokenStyle('variable', 'value', '', ';'), 'variable');
  assert.equal(enhancedCSharpTokenStyle('string', 'FormatDetector', '', ''), 'string');
});
