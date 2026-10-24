import assert from 'node:assert/strict';

import {
  adoptAgentSession,
  createFreshSession,
  parseStoredOwnedSessions,
  reconcileOwnedSessions
} from '../src/lib/shell/ownedSessions.ts';
import { configOptionPlacement } from '../src/lib/shell/conversation/conversationTypes.ts';
import { normalizeWorkspaceSnapshot } from '../src/lib/shell/sessionWorkspaces.ts';

const scanRecord = {
  provider: 'codex', id: 'native-a', title: 'Runtime fixture', description: null,
  model: null, projectPath: '/tmp/runtime-fixture', lastActivity: null,
  resumeCommands: ['codex resume native-a'], branchHint: null, taskId: null,
  pullRequestHint: null, sourceLabel: 'Codex', messageCount: 2,
  latestTurnPreview: 'Agent: retained'
};

// A saved session from before runtime ownership existed migrates to its live
// terminal and remains readable after the new representation is saved again.
{
  const current = {
    ...adoptAgentSession(scanRecord, () => 'owned-a'),
    ptySessionId: 'pty-a',
    state: 'background'
  };
  const {
    executionOwner, runtimeState, providerInstanceId, activeTurnId,
    capabilityRevision, lastRuntimeError, ...legacyOwned
  } = current;
  const workspace = {
    openPaths: ['/tmp/runtime-fixture/a.ts'], activePath: '/tmp/runtime-fixture/a.ts',
    conversation: {
      mode: 'structured', draft: 'draft-a', attachmentIds: ['attachment-a'],
      unknownLegacyField: { retained: true }
    }
  };
  const fixture = {
    owned: legacyOwned,
    transcript: [{ itemId: 'message-a', text: 'retained transcript' }],
    workspace,
    attachments: [{ id: 'attachment-a', path: '/managed/a.png' }]
  };

  const migratedOwned = parseStoredOwnedSessions(JSON.stringify([fixture.owned]))[0];
  const reconciled = reconcileOwnedSessions([migratedOwned], [
    { sessionId: 'pty-a', exited: false }
  ]).owned[0];
  assert.equal(reconciled.executionOwner, 'terminal');
  assert.equal(reconciled.runtimeState, 'ready');
  assert.equal(reconciled.ptySessionId, 'pty-a');
  assert.equal(reconciled.nativeSessionId, 'native-a');
  assert.deepEqual(parseStoredOwnedSessions(JSON.stringify([reconciled])), [reconciled]);

  const restored = normalizeWorkspaceSnapshot(fixture.workspace);
  assert.ok(restored);
  assert.equal('draft' in restored.conversation, false);
  assert.deepEqual(restored.conversation.attachmentIds, ['attachment-a']);
  assert.equal(restored.conversation.unknownLegacyField.retained, true);
  assert.deepEqual(fixture.transcript, [{ itemId: 'message-a', text: 'retained transcript' }]);
  assert.deepEqual(fixture.attachments, [{ id: 'attachment-a', path: '/managed/a.png' }]);

  const failed = reconcileOwnedSessions([reconciled], []).owned[0];
  assert.equal(failed.executionOwner, 'stopped');
  assert.equal(failed.ptySessionId, 'pty-a');
  assert.equal(failed.nativeSessionId, 'native-a');
}

// Provider-defined capability categories and metadata survive unchanged while
// only their presentation location is interpreted.
{
  const capabilities = {
    revision: 7,
    provider: 'codex',
    implementation: { name: 'fixture', version: '1' },
    session: { list: true, load: true, resume: true, close: true, steering: true },
    prompt: { text: true, image: true, embeddedContext: true, resourceLinks: true },
    interaction: {
      permissions: true, structuredUserInput: true, toolTerminals: true,
      plans: true, tasks: true, subagents: true
    },
    configOptions: [{
      id: 'future', label: 'Future option', category: 'provider.future/category',
      value: { nested: ['kept', 3] }, providerMetadata: { provider_field: 'kept' }
    }],
    commands: []
  };
  const roundTrip = JSON.parse(JSON.stringify(capabilities));
  assert.deepEqual(roundTrip, capabilities);
  assert.equal(configOptionPlacement(roundTrip.configOptions[0].category), 'more-options');
}

// App-owned agent sessions are structured-only; an adopted/external session is
// the identity that may retain raw-mode workspace state.
{
  const appSession = {
    ...createFreshSession({ cwd: '/tmp/app-owned' }, () => 'owned-app'),
    agent: 'codex',
    origin: 'app'
  };
  const externalSession = adoptAgentSession(scanRecord, () => 'owned-external');
  assert.equal(appSession.origin, 'app');
  assert.equal(externalSession.origin, 'external');

  const snapshots = {
    'owned-a': {
      openPaths: [], activePath: null, selectedPath: null,
      scrollTop: 0, diffPath: null, diffRoot: null,
      conversation: {
        mode: 'structured', draft: 'a', generation: 3, owner: 'terminal', sequence: 9,
        writerLease: { ownedId: 'owned-a', generation: 3, owner: 'terminal' }
      }
    },
    'owned-b': {
      openPaths: [], activePath: null, selectedPath: null,
      scrollTop: 0, diffPath: null, diffRoot: null,
      conversation: {
        mode: 'raw', draft: 'b', generation: 8, owner: 'stopped', sequence: 2,
        writerLease: { ownedId: 'owned-b', generation: 8, owner: 'none' }
      }
    }
  };
  const restored = Object.fromEntries(
    Object.entries(snapshots).map(([ownedId, snapshot]) => [
      ownedId,
      normalizeWorkspaceSnapshot(snapshot)!
    ])
  );
  assert.equal(restored['owned-a'].conversation.mode, 'structured', 'app-owned sessions stay structured');
  assert.equal(restored['owned-b'].conversation.mode, 'raw', 'external sessions retain raw mode');
  assert.equal(restored['owned-a'].conversation.writerLease.ownedId, 'owned-a');
  assert.equal(restored['owned-b'].conversation.writerLease.ownedId, 'owned-b');
  assert.notEqual(
    restored['owned-a'].conversation.generation,
    restored['owned-b'].conversation.generation
  );
}

console.log('agent runtime contract tests passed');
