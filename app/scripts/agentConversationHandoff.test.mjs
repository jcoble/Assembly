import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {
  applyConversationHandoffReceipt,
  createConversationHandoffRequest,
  handoffActionLabel,
  handoffGenerationMatches,
  restoreConversationHandoffAfterFailure
} from '../src/lib/shell/conversation/conversationTypes.ts';

function testTypedCommandAndGeneration() {
  const request = createConversationHandoffRequest({
    ownedId: 'owned-a',
    generation: 7,
    direction: 'structured-to-terminal',
    mode: 'same-session',
    phase: 'prepare',
    nativeSessionId: 'native-a',
    ptySessionId: null,
    historyBoundary: { firstSequence: 1, lastSequence: 12 },
    processTree: { checked: false }
  });

  assert.equal(request.ownedId, 'owned-a');
  assert.equal(request.generation, 7);
  assert.equal(request.phase, 'prepare');
  assert.equal(request.historyBoundary.lastSequence, 12);
  assert.equal(request.processTree.checked, false);
}

function testReceiptWorkspaceRoundTrip() {
  const receipt = {
    ownedId: 'owned-a',
    generation: 7,
    direction: 'structured-to-terminal',
    mode: 'same-session',
    phase: 'committed',
    previousOwner: 'structured',
    owner: 'terminal',
    nativeSessionId: 'native-a',
    ptySessionId: 'pty-a',
    historyBoundary: { firstSequence: 1, lastSequence: 12 },
    processTree: { checked: true, writerCount: 1, ptyCount: 1, sidecarCount: 0 },
    rollbackAvailable: true,
    message: 'Native terminal owns this conversation.'
  };
  const workspace = {
    mode: 'structured',
    draft: 'keep me',
    generation: 7,
    owner: 'structured',
    writerLease: { ownedId: 'owned-a', generation: 7, owner: 'structured' },
    writerLeaseTransition: null,
    lastSequence: 12
  };
  const restored = applyConversationHandoffReceipt(workspace, JSON.parse(JSON.stringify(receipt)));
  assert.equal(restored.mode, 'raw', 'terminal ownership restores the raw terminal view');
  assert.equal(restored.owner, 'terminal');
  assert.equal(restored.writerLease.owner, 'terminal');
  assert.equal(restored.draft, 'keep me', 'handoff never loses the draft');

  assert.equal(handoffGenerationMatches(receipt, 'owned-a', 7), true);
  assert.equal(handoffGenerationMatches(receipt, 'owned-a', 8), false);
  assert.equal(handoffGenerationMatches(receipt, 'owned-b', 7), false);

  const failed = restoreConversationHandoffAfterFailure({
    ...workspace,
    writerLeaseTransition: { from: 'structured', to: 'terminal', state: 'requested' }
  });
  assert.equal(failed.writerLeaseTransition, null, 'failed handoff clears the pending transition');
  assert.equal(failed.draft, 'keep me', 'failed handoff preserves the draft');
}

function testDistinctUiLabels() {
  assert.equal(handoffActionLabel('structured-to-terminal', 'same-session'), 'Open in native CLI');
  assert.equal(handoffActionLabel('structured-to-terminal', 'fork'), 'Fork to native CLI');
  assert.equal(handoffActionLabel('terminal-to-structured', 'same-session'), 'Return to structured');
}

function testNoPassiveProjectionTrigger() {
  const surfacePath = path.join(process.cwd(), 'src/lib/shell/conversation/ConversationSurface.svelte');
  const surface = fs.readFileSync(surfacePath, 'utf8');
  assert.equal(
    surface.includes('startConversationTerminalProjection({'),
    false,
    'terminal projection starts only after an explicit handoff receipt'
  );
}

testTypedCommandAndGeneration();
testReceiptWorkspaceRoundTrip();
testDistinctUiLabels();
testNoPassiveProjectionTrigger();

console.log('agentConversationHandoff.test.mjs passed');
