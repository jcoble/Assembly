import assert from 'node:assert/strict';
import { test } from 'node:test';
import { releaseVersion } from './release-version.ts';

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
