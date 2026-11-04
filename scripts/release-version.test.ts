import assert from 'node:assert/strict';
import { test } from 'node:test';
import { releaseVersion, requireBump } from './release-version.ts';

test('the tag naming the declared version releases it', () => {
  assert.equal(releaseVersion('refs/tags/assembly-v1.2.3', '1.2.3'), '1.2.3');
});

test('a tag naming any other version is refused', () => {
  assert.throws(() => releaseVersion('refs/tags/assembly-v1.2.4', '1.2.3'), /only from the tag refs\/tags\/assembly-v1\.2\.3/);
});

test('a branch, including main, never releases', () => {
  assert.throws(() => releaseVersion('refs/heads/main', '1.2.3'), /only from the tag/);
  assert.throws(() => releaseVersion('', '1.2.3'), /only from the tag/);
});

test('a version above every published release may release, whatever else is published', () => {
  requireBump('1.2.4', ['assembly-v1.2.3', 'assembly-v1.1.9', 'assembly-providers-41673229351-1']);
  requireBump('1.10.0', ['assembly-v1.9.9']);
  requireBump('0.1.0', []);
});

test('a release with no bump since the last one is refused', () => {
  assert.throws(() => requireBump('1.2.3', ['assembly-v1.2.3']), /not above the published assembly-v1\.2\.3: bump/);
});

test('a version below a published release is refused', () => {
  assert.throws(() => requireBump('1.2.3', ['assembly-v1.10.0']), /not above the published assembly-v1\.10\.0/);
});
