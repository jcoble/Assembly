import assert from 'node:assert/strict';
import test from 'node:test';
import { backendReleaseVersion } from './backendReleaseVersion.ts';

const tags = ['0.1.9', '0.1.14', '0.1.14^{}', '0.2.1-rc.1', 'invalid']
  .map((version) => `abc123\trefs/tags/assembly-backend-v${version}`).join('\n');

test('main advances the highest stable remote backend tag numerically', () => {
  assert.equal(backendReleaseVersion('refs/heads/main', tags), '0.1.15');
  assert.equal(backendReleaseVersion('refs/heads/main', tags + '\nabc refs/tags/assembly-backend-v1.0.0'), '1.0.1');
  assert.equal(backendReleaseVersion('refs/heads/main', ''), '0.1.0');
});
test('explicit tags preserve the requested existing version', () => {
  assert.equal(backendReleaseVersion('refs/tags/assembly-backend-v0.1.9', tags), '0.1.9');
});
test('unrelated branches, missing tags and malformed versions cannot publish', () => {
  for (const ref of ['refs/heads/feature', 'refs/tags/assembly-backend-v0.1.15',
    'refs/tags/assembly-backend-vinvalid', 'refs/tags/assembly-backend-v0.2.1-rc.1']) {
    assert.throws(() => backendReleaseVersion(ref, tags));
  }
});
