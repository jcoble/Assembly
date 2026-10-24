import { test } from 'node:test';
import assert from 'node:assert/strict';
import { stripTypeScriptTypes } from 'node:module';
import { normalizeProvider } from '../src/lib/shell/ownedSessions.ts';
import * as historyModel from '../src/lib/shell/history/sessionHistoryViewModel.ts';
import { readFileSync } from 'node:fs';

import {
  sessionHistoryLoadOutcome,
  type RepositoryCheckouts
} from '../src/lib/shell/history/sessionHistoryLoad.ts';
import { sessionHistoryActions, sessionTranscriptImport } from '../src/lib/shell/history/components/sessionHistoryActions.ts';
import { buildSessionLibrary, type SessionLibraryRecord } from '../src/lib/shell/history/library/sessionLibraryModel.ts';
import type { AgentSession } from '../src/lib/tauriSource.ts';
import type { OwnedSession } from '../src/lib/shell/ownedSessions.ts';

function record(key: string): SessionLibraryRecord {
  return {
    key,
    source: 'provider',
    machine: 'local',
    ownedId: null,
    provider: 'test',
    nativeSessionId: key,
    canonicalCwd: '/work/project',
    title: key,
    description: null,
    projectGroupKey: 'project:p1',
    projectGroupLabel: 'project',
    model: null,
    state: 'resumable',
    runtimeState: null,
    lastActivity: null,
    updatedAt: null,
    messageCount: null,
    logPath: null,
    firstPrompt: null,
    latestTurns: [],
    subagents: [],
    owned: null,
    available: null
  };
}

function run(name: string, test: () => void): void {
  test();
  console.log(`pass: ${name}`);
}

run('a_failed_checkout_lookup_still_yields_the_sessions', () => {
  const records = [record('one'), record('two')];
  const outcome = sessionHistoryLoadOutcome({
    requestedKeys: new Set(['one', 'two']),
    refreshed: { status: 'fulfilled', value: records },
    checkouts: { status: 'rejected', reason: new Error('git unavailable') }
  });

  assert.equal(outcome.state, 'ready');
  assert.deepEqual(outcome.records, records);
  assert.deepEqual(outcome.checkouts, {});
});

run('a_scan_that_is_behind_reports_what_is_missing', () => {
  const outcome = sessionHistoryLoadOutcome({
    requestedKeys: new Set(['one', 'two']),
    refreshed: { status: 'fulfilled', value: [record('one')] },
    checkouts: { status: 'fulfilled', value: {} }
  });

  assert.equal(outcome.state, 'incomplete');
  assert.deepEqual(outcome.missingKeys, ['two']);
  assert.deepEqual(outcome.records.map(({ key }) => key), ['one']);
});

run('a_failed_refresh_is_the_only_failure', () => {
  const checkouts: RepositoryCheckouts = {
    '/work/project': [{ path: '/work/project', branch: 'main', isMain: true }]
  };
  const outcome = sessionHistoryLoadOutcome({
    requestedKeys: new Set(['one']),
    refreshed: { status: 'rejected', reason: new Error('scan failed') },
    checkouts: { status: 'fulfilled', value: checkouts }
  });

  assert.equal(outcome.state, 'failed');
  assert.deepEqual(outcome.records, []);
});

run('everything_present_is_ready', () => {
  const checkouts: RepositoryCheckouts = {
    '/work/project': [{ path: '/work/project', branch: 'main', isMain: true }]
  };
  const outcome = sessionHistoryLoadOutcome({
    requestedKeys: new Set(['one', 'two']),
    refreshed: { status: 'fulfilled', value: [record('one'), record('two')] },
    checkouts: { status: 'fulfilled', value: checkouts }
  });

  assert.equal(outcome.state, 'ready');
  assert.deepEqual(outcome.missingKeys, []);
  assert.deepEqual(outcome.checkouts, checkouts);
});

run('every_history_row_offers_resume_through_the_import', () => {
  const owned = record('owned-session');
  owned.source = 'owned';
  owned.ownedId = 'owned-session';
  owned.provider = 'codex';
  owned.logPath = '/tmp/provider-transcript.jsonl';
  const scanned = record('scanned-session');
  scanned.provider = 'codex';
  scanned.logPath = '/tmp/other-transcript.jsonl';
  for (const row of [owned, scanned]) {
    const action = sessionHistoryActions(row).find((candidate) => candidate.id === 'resume-assembly');
    assert.equal(action?.label, 'Resume here');
    assert.equal(action?.enabled, true);
  }
});

run('a_held_remote_row_waits_for_its_machine_scan', () => {
  const held = record('held-remote');
  held.source = 'owned';
  held.ownedId = 'held-remote';
  held.machine = 'box';
  const waiting = sessionHistoryActions(held, true).find((candidate) => candidate.id === 'resume-assembly');
  assert.equal(waiting?.enabled, false);
  assert.equal(waiting?.disabledReason, 'Scanning…');
  assert.equal(sessionHistoryActions(held, false).find((candidate) => candidate.id === 'resume-assembly')?.enabled, true);
  // This Mac's scan gives its held rows their transcripts too, so they wait for it the same way.
  held.machine = 'local';
  assert.equal(sessionHistoryActions(held, true).find((candidate) => candidate.id === 'resume-assembly')?.enabled, false);
});

run('a_row_the_rail_has_open_just_opens', () => {
  const open = record('open-session');
  open.source = 'owned';
  open.ownedId = 'open-session';
  open.provider = 'claude';
  open.logPath = '/tmp/open-transcript.jsonl';
  // Reading it again would need it closed first, so a running session is only selected.
  for (const runtimeState of ['ready', 'working', 'waiting-input'] as const) {
    open.runtimeState = runtimeState;
    assert.equal(sessionTranscriptImport(open), null, runtimeState);
    assert.equal(sessionHistoryActions(open).find((action) => action.id === 'resume-assembly')?.enabled, true, runtimeState);
  }
  open.runtimeState = 'suspended';
  assert.equal(sessionTranscriptImport(open)?.transcriptPath, open.logPath);
});

run('a_session_running_outside_assembly_waits_until_it_stops', () => {
  const row = record('elsewhere');
  row.provider = 'claude';
  row.logPath = '/tmp/elsewhere.jsonl';
  const resume = sessionHistoryActions(row, false, true).find((candidate) => candidate.id === 'resume-assembly');
  assert.deepEqual([resume?.enabled, resume?.disabledReason], [false, 'Running outside Assembly']);
});

// Pins 80db568aa: a cmux-run Codex row is read as the Codex transcript it is, and the card's title travels with the
// import (trimmed, none when blank) so the database, not only the screen, holds the name.
run('an_import_request_reads_cmux_rows_as_their_agent_and_carries_the_title', () => {
  const row = record('cmux-row');
  row.provider = 'cmux-codex';
  row.logPath = ' /logs/cmux-row.jsonl ';
  row.title = '  Fix the rail  ';
  assert.deepEqual(sessionTranscriptImport(row),
    { provider: 'codex', nativeSessionId: 'cmux-row', transcriptPath: '/logs/cmux-row.jsonl', cwd: '/work/project', title: 'Fix the rail' });
  row.title = '   ';
  assert.equal(sessionTranscriptImport(row)?.title, null);
});

// Pins 417f21bbc: a resume that cannot run says which piece is missing, most specific first.
run('a_resume_that_cannot_run_names_what_is_missing', () => {
  const reason = (change: Partial<SessionLibraryRecord>) => {
    const row = { ...record('missing'), provider: 'claude', logPath: '/logs/missing.jsonl', ...change };
    return sessionHistoryActions(row).find((action) => action.id === 'resume-assembly')?.disabledReason;
  };
  assert.equal(reason({}), null);
  assert.equal(reason({ nativeSessionId: ' ', logPath: null }), 'This session has no provider session id to read a transcript for.');
  assert.equal(reason({ logPath: null, canonicalCwd: null }), 'No transcript file was found for this session.');
  assert.equal(reason({ canonicalCwd: ' ' }), 'This session has no folder recorded.');
  assert.equal(reason({ provider: 'opencode' }), 'This app cannot read transcripts written by this agent.');
});

const remoteScan = {
  provider: 'claude', id: 'native-1', title: 'Can you hear me', model: null, projectPath: '/home/me/dev/work/EdiPlatform',
  lastActivity: null, resumeCommands: [], logPath: '/home/me/.claude/projects/edi/native-1.jsonl', machine: 'box'
} as AgentSession;

run('a_remote_scan_keeps_its_machine_and_can_be_imported', () => {
  const [scanned] = buildSessionLibrary([], [remoteScan]);
  assert.equal(scanned.machine, 'box');
  assert.equal(sessionTranscriptImport(scanned)?.transcriptPath, remoteScan.logPath);
});

run('a_remote_owned_row_takes_its_scanned_transcript_and_keeps_resume', () => {
  const owned = {
    ownedId: 'owned-1', agent: 'claude', viaCmux: false, source: 'fresh', title: 'Can you hear me', projectGroupKey: 'none',
    projectGroupLabel: 'No project', cwd: '/home/me/dev/work/EdiPlatform', resumeCommand: null, nativeSessionId: 'native-1',
    ptySessionId: null, state: 'background', completedAt: null, settledAt: null, branch: null, taskId: null, pullRequest: null,
    messageCount: null, latestTurnPreview: null, lastActivity: null, executionEnvironment: 'remote', remoteProfileId: 'box'
  } as unknown as OwnedSession;
  const records = buildSessionLibrary([owned], [remoteScan]);
  assert.equal(records.length, 1);
  assert.equal(records[0].ownedId, 'owned-1');
  assert.equal(records[0].machine, 'box');
  assert.equal(sessionTranscriptImport(records[0])?.transcriptPath, remoteScan.logPath);
});

run('project_and_detail_reads_have_independent_lifecycles', () => {
  const panel = readFileSync(
    new URL('../src/lib/shell/history/components/HistoryPanel.svelte', import.meta.url),
    'utf8'
  );
  assert.match(panel, /let historyLoadVersion = 0;/);
  assert.match(panel, /let detailLoadVersion = 0;/);
  assert.match(panel, /function releaseDetails[\s\S]*?detailLoadVersion \+= 1;/);
  assert.match(panel, /function stopHistoryLoads[\s\S]*?historyLoadVersion \+= 1;[\s\S]*?loadingProjectKey = null;/);
  assert.match(panel, /async function loadCardDetails[\s\S]*?\+\+detailLoadVersion/);
  assert.doesNotMatch(panel, /\bloadVersion\b/);
});

run('a_rebuilt_resume_does_not_add_a_session_the_rail_already_holds', () => {
  const panel = readFileSync(new URL('../src/lib/shell/history/components/HistoryPanel.svelte', import.meta.url), 'utf8');
  const resume = panel.slice(panel.indexOf('async function resumeAsAssemblySession'), panel.indexOf('async function runAction'));
  assert.match(resume, /if \(!rail\.owned\.some\(\(session\) => session\.ownedId === ownedId\)\) addOwnedSession\(/);
  assert.equal(resume.match(/addOwnedSession\(/g)?.length, 1);
});


test('history import is offered only where a transcript reader exists', () => {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationStore.svelte.ts', import.meta.url), 'utf8');
  const childRules = source.slice(source.indexOf('function normalizeChildProvider('), source.indexOf('function normalizedConversationChildren('));
  const normalizeChild = Function('normalizeProvider', 'hasTranscriptReader', 'asString', 'isRecord',
    stripTypeScriptTypes(childRules, { mode: 'strip' }) + '; return normalizedConversationChild;'
  )(normalizeProvider, historyModel.hasTranscriptReader, (value: unknown) => typeof value === 'string' ? value : null,
    (value: unknown) => value !== null && typeof value === 'object');
  for (const provider of ['codex', 'claude', 'antigravity', 'gemini', 'agy', 'opencode', 'anthropic', 'openai', 'claudecode']) {
    const row = { ...record(provider), provider, logPath: '/work/transcript.jsonl' };
    const canonical = normalizeProvider(provider).agent;
    const expected = canonical === 'codex' || canonical === 'claude';
    const child = normalizeChild({ provider, childId: 'child', parentId: 'root' }, null, 'antigravity', 'root', 1, 1, []);
    assert.equal(child.transcriptAvailable, expected, `${provider} child`);
    assert.equal(sessionTranscriptImport(row) !== null, expected, provider);
    assert.equal(sessionHistoryActions(row).find((action) => action.id === 'resume-assembly')?.enabled, expected, provider);
  }
});
