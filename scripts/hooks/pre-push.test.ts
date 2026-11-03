import test from 'node:test';
import assert from 'node:assert/strict';
import { updates } from './pre-push.ts';
const sha = 'a'.repeat(40), zero = '0'.repeat(40);
test('outgoing updates include every reference and forbid direct default branch writes', () => {
  assert.equal(updates(`refs/heads/cdx/a ${sha} refs/heads/cdx/a ${zero}\nrefs/heads/cdx/b ${sha} refs/heads/cdx/b ${sha}`).length, 2);
  assert.throws(() => updates(`refs/heads/cdx/a ${sha} refs/heads/main ${zero}`), /default-branch/);
  assert.throws(() => updates(`refs/heads/cdx/a ${sha} refs/heads/master ${sha}`), /default-branch/);
  assert.throws(() => updates('malformed'), /Invalid/);
  assert.deepEqual(updates(''), []);
});
