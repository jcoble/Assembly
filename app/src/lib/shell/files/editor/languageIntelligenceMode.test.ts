import assert from 'node:assert/strict';
import test from 'node:test';

import { workspaceKey } from './languageIntelligenceMode.ts';

test('the same folder written two ways is one project', () => {
  assert.equal(workspaceKey('/projects/one/'), '/projects/one');
  assert.equal(workspaceKey('  /projects/one  '), '/projects/one');
  assert.equal(workspaceKey('/'), '/');
});
