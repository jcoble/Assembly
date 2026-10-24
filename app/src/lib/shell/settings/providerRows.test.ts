// Settings → Plugins and Providers: one row per provider descriptor.
import assert from 'node:assert/strict';
import test from 'node:test';
import { providerRow, type ProviderSummary } from './providerRows.ts';

const claude: ProviderSummary = {
  id: 'claude', displayName: 'Claude', adapterId: 'claude-agent-acp', adapterVersion: '0.93.0',
  readOnly: true, proofHome: true, permissionHandler: false
};
const antigravity: ProviderSummary = {
  id: 'antigravity', displayName: 'Antigravity', adapterId: 'agy-acp', adapterVersion: '1.2.1',
  readOnly: false, proofHome: false, permissionHandler: true
};
const config = (availableModels: string[], modelLabels: Record<string, string> = {}) => ({
  model: availableModels[0] ?? null, availableModels, modelLabels, reasoningEffort: null, availableEfforts: [],
  approvalPolicy: null, availableApprovalPolicies: []
});

test('flags come from the descriptor: no read-only mode or proof login means it cannot prove', () => {
  const row = providerRow(antigravity, { config: config(['gemini-3']) });
  assert.deepEqual(row.flags, ["Can't prove: no read-only mode", "Tool approval: Assembly's permission handler"]);
  assert.deepEqual(providerRow({ ...claude, proofHome: false }, undefined).flags,
    ["Can't prove: no separate proof login", 'Tool approval: its own launch settings']);
  // With both present the matrix decides, which nothing records yet, so no prove label is shown.
  assert.deepEqual(providerRow(claude, undefined).flags, ['Tool approval: its own launch settings']);
});

test('sign-in state and models come from the provider check', () => {
  assert.deepEqual(providerRow(claude, undefined), {
    id: 'claude', name: 'Claude', adapter: 'claude-agent-acp 0.93.0', state: 'Checking…', tone: 'neutral',
    detail: '', models: [], flags: ['Tool approval: its own launch settings']
  });
  const ready = providerRow(claude, { config: config(['default', 'opus'], { opus: 'Opus 5.5' }) });
  assert.equal(ready.state, 'Ready');
  assert.equal(ready.tone, 'good');
  assert.deepEqual(ready.models, ['default', 'Opus 5.5']);
  const signIn = providerRow(claude, { error: 'Claude needs you to sign in. Sign in with the Claude CLI where this session runs, then send again.' });
  assert.equal(signIn.state, 'Needs sign-in');
  assert.equal(signIn.tone, 'attention');
  const failed = providerRow(claude, { error: 'Provider catalog request timed out' });
  assert.deepEqual([failed.state, failed.tone, failed.detail], ["Couldn't start", 'bad', 'Provider catalog request timed out']);
});

test('a provider with no models says so', () => {
  const row = providerRow(claude, { config: config([]) });
  assert.equal(row.state, 'Ready');
  assert.deepEqual(row.models, []);
  assert.equal(row.detail, 'No models listed');
});

test('a provider whose adapter is not installed is not checked', () => {
  const row = providerRow({ ...claude, adapterVersion: null }, undefined);
  assert.deepEqual([row.adapter, row.state, row.tone], ['claude-agent-acp', 'Not installed', 'neutral']);
});

test('a remote adapter whose version could not be read is still checked', () => {
  const unread = { ...claude, adapterVersion: undefined };
  assert.deepEqual([providerRow(unread, undefined).adapter, providerRow(unread, undefined).state], ['claude-agent-acp', 'Checking…']);
  assert.equal(providerRow(unread, { config: config(['opus']) }).state, 'Ready');
});

test('an unknown provider id still gets a row from its descriptor alone', () => {
  const grok: ProviderSummary = {
    id: 'grok', displayName: 'Grok Build', adapterId: 'grok-acp', adapterVersion: '0.1.0',
    readOnly: true, proofHome: false, permissionHandler: false
  };
  const row = providerRow(grok, { config: config(['grok-4.5']) });
  assert.deepEqual([row.id, row.name, row.adapter, row.models], ['grok', 'Grok Build', 'grok-acp 0.1.0', ['grok-4.5']]);
  assert.deepEqual(row.flags, ["Can't prove: no separate proof login", 'Tool approval: its own launch settings']);
});
