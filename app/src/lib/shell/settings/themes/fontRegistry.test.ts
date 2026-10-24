import assert from 'node:assert/strict';
import test from 'node:test';
import { getMonoFont, getUiFont, mergeFontChoices, terminalFontStack } from './fontRegistry.ts';

test('installed families keep their exact name and a valid CSS fallback', () => {
  assert.equal(getUiFont('Avenir').stack.startsWith('"Avenir", '), true);
  assert.equal(getMonoFont('MesloLGS NF').stack.startsWith('"MesloLGS NF", '), true);
  assert.equal(getMonoFont('Name "Quoted"').stack.startsWith('"Name \\"Quoted\\"", '), true);
});

test('installed choices append in native order without duplicating presets', () => {
  assert.deepEqual(
    mergeFontChoices([{ value: 'inter', label: 'Inter' }], ['Avenir', 'Inter', 'Avenir']),
    [
      { value: 'inter', label: 'Inter' },
      { value: 'Avenir', label: 'Avenir' }
    ]
  );
  assert.equal(mergeFontChoices([], ['MesloLGS NF']).at(-1)?.value, 'MesloLGS NF');
  assert.equal(terminalFontStack('JetBrains Mono').startsWith('"JetBrains Mono", '), true);
});
