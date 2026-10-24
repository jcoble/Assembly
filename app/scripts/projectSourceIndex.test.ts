import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const explorerService = readFileSync(
  new URL('../src/lib/shell/files/explorer/explorerService.ts', import.meta.url),
  'utf8'
);
const sourceIntelligence = readFileSync(
  new URL('../src/lib/shell/files/editor/sourceIntelligence.ts', import.meta.url),
  'utf8'
);

assert.doesNotMatch(
  explorerService,
  /projectSourceIndex|setProjectSourceRecords/,
  'Explorer must not retain a second project-wide copy of every scanned source record'
);
assert.doesNotMatch(
  sourceIntelligence,
  /projectSourceIndex|projectSourceRecords|indexedDefinitions|indexedReferences/,
  'source intelligence must not restore the removed project-wide source index fallback'
);
assert.match(
  sourceIntelligence,
  /findSourceLspDefinitionsFromTauri/,
  'definition lookup stays on the bounded language-server path'
);
assert.match(
  sourceIntelligence,
  /findSourceLspReferencesFromTauri/,
  'reference lookup stays on the bounded language-server path'
);

console.log('projectSourceIndex: removed index stays removed');
