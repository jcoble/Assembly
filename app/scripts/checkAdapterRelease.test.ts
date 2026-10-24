import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { releaseConcerns, type NpmRelease } from './checkAdapterRelease.ts';

// Registry documents (trimmed to the fields the check reads) for the releases that were pinned
// before and after the daily update: `npm view <package>@<version> --json`.
const releases: Record<string, NpmRelease> = JSON.parse(
  readFileSync(new URL('./fixtures/adapterReleases.json', import.meta.url), 'utf8'),
);

test('the real release pairs the daily update compares pass', () => {
  for (const [before, after] of [
    ['@agentclientprotocol/codex-acp@1.13.1', '@agentclientprotocol/codex-acp@2.2.2'],
    ['@agentclientprotocol/claude-agent-acp@0.90.0', '@agentclientprotocol/claude-agent-acp@0.94.0'],
  ]) {
    assert.deepEqual(releaseConcerns(releases[before], releases[after]), [], after);
  }
});

test('a real release published by a stranger fails and names the stranger', () => {
  const [before, after] = [releases['@agentclientprotocol/codex-acp@1.13.1'], releases['@agentclientprotocol/codex-acp@2.2.2']];
  const concerns = releaseConcerns(before, { ...after, _npmUser: { name: 'mallory', email: 'm@example.com' } });
  assert.equal(concerns.length, 1);
  assert.match(concerns[0], /published by mallory,/);
  assert.match(concerns[0], /previous publisher \(GitHub Actions\)/);
});

test('a real release with a changed maintainer list fails', () => {
  const [before, after] = [releases['@agentclientprotocol/codex-acp@1.13.1'], releases['@agentclientprotocol/codex-acp@2.2.2']];
  const concerns = releaseConcerns(before, { ...after, maintainers: [...(after.maintainers ?? []), { name: 'mallory' }] });
  assert.equal(concerns.length, 1);
  assert.match(concerns[0], /maintainers changed from \[aguzubiaga, benbrandt, cirwin\] to \[aguzubiaga, benbrandt, cirwin, mallory\]/);
});

const ci = { name: 'GitHub Actions', email: 'npm-oidc-no-reply@github.com' };
const maintainers = [
  { name: 'benbrandt', email: 'benjamin.j.brandt@gmail.com' },
  { name: 'aguzubiaga', email: 'sup@aguz.me' },
  { name: 'cirwin', email: 'conrad.irwin@gmail.com' },
];
const previous: NpmRelease = {
  _npmUser: ci,
  maintainers,
  scripts: { build: 'node build.mjs', prepublishOnly: 'npm run build' },
  dist: { attestations: { url: 'https://registry.npmjs.org/-/npm/v1/attestations/x', provenance: {} } },
};

test('a release from the same publisher and maintainers passes', () => {
  assert.deepEqual(releaseConcerns(previous, { ...previous, maintainers: [...maintainers].reverse() }), []);
});

test('a release by one of the previous maintainers passes', () => {
  assert.deepEqual(releaseConcerns(previous, { ...previous, _npmUser: maintainers[1] }), []);
});

test('a publisher who is neither the previous publisher nor a maintainer fails', () => {
  const concerns = releaseConcerns(previous, { ...previous, _npmUser: { name: 'mallory', email: 'm@example.com' } });
  assert.equal(concerns.length, 1);
  assert.match(concerns[0], /mallory/);
});

test('a changed maintainer list fails', () => {
  const concerns = releaseConcerns(previous, { ...previous, maintainers: [...maintainers, { name: 'mallory', email: 'm@example.com' }] });
  assert.equal(concerns.length, 1);
  assert.match(concerns[0], /maintainers changed/);
});

test('a new install script fails', () => {
  const concerns = releaseConcerns(previous, { ...previous, scripts: { ...previous.scripts, postinstall: 'node x.js' } });
  assert.equal(concerns.length, 1);
  assert.match(concerns[0], /postinstall/);
});

test('an install script the previous release already had passes', () => {
  const withInstall = { ...previous, scripts: { install: 'node-gyp rebuild' } };
  assert.deepEqual(releaseConcerns(withInstall, withInstall), []);
});

test('losing npm provenance fails, but never having it does not', () => {
  for (const dist of [{}, { attestations: { url: 'https://registry.npmjs.org/-/npm/v1/attestations/x' } }]) {
    const concerns = releaseConcerns(previous, { ...previous, dist });
    assert.equal(concerns.length, 1);
    assert.match(concerns[0], /provenance/);
  }
  const unsigned = { ...previous, dist: {} };
  assert.deepEqual(releaseConcerns(unsigned, unsigned), []);
});
