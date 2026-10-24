/**
 * Which Notion project a session's folder belongs to.
 *
 * Run it with:
 *   node --experimental-strip-types src/lib/shell/notionProjectMatch.test.ts
 */
import assert from 'node:assert/strict';
import test from 'node:test';

import { matchNotionProject } from './notionProjectMatch.ts';

const projects = ['EdiPlatform (RetailReady EDI)', 'HealthAggregator', 'MacCommandBar', 'Portfolio Work', 'Rental Command'];

test('an exact letters-only match wins', () => {
  assert.equal(matchNotionProject('/Users/me/dev/work/mac-command-bar', projects), 'MacCommandBar');
  assert.equal(matchNotionProject('/Users/me/dev/work/EdiPlatform', projects), 'EdiPlatform (RetailReady EDI)');
  assert.equal(matchNotionProject('/Users/me/dev/work/ediplatform', projects), 'EdiPlatform (RetailReady EDI)');
});

test('otherwise the first word of the folder picks the project it starts', () => {
  assert.equal(matchNotionProject('/Users/me/dev/work/rental-management', projects), 'Rental Command');
  assert.equal(matchNotionProject('/Users/me/dev/work/health_sync', projects), 'HealthAggregator');
  assert.equal(matchNotionProject('/Users/me/dev/work/PortfolioSite', projects), 'Portfolio Work');
});

test('no match, or no folder, means every project', () => {
  assert.equal(matchNotionProject('/Users/me/dev/work/test123', projects), '');
  assert.equal(matchNotionProject('/Users/me/dev/work/1234', projects), '');
  assert.equal(matchNotionProject('', projects), '');
  assert.equal(matchNotionProject('/Users/me/dev/work/mac-command-bar', []), '');
});

test('a worktree checkout belongs to the repository it was cut from', () => {
  assert.equal(
    matchNotionProject('/Users/me/dev/work/worktrees/EdiPlatform/session-walmart-all-docs', projects),
    'EdiPlatform (RetailReady EDI)'
  );
  assert.equal(matchNotionProject('/Users/me/dev/work/worktrees/mac-command-bar/tsk-99-lane', projects), 'MacCommandBar');
  assert.equal(matchNotionProject('/Users/me/dev/work/worktrees/health_sync/fix-lane', projects), 'HealthAggregator');
  assert.equal(matchNotionProject('/Users/me/dev/work/worktrees/test123/lane', projects), '');
});
