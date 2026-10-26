import assert from 'node:assert/strict';
import { test } from 'node:test';
import { senderLabel } from '../src/lib/chat/sender.ts';
import type { Session } from '../src/lib/api.ts';

const agents = [{ id: 'claude', name: 'Claude Code' }, { id: 'codex', name: 'Codex' }];
const session = (extra: Partial<Session>): Session => ({ sessionId: 't', agent: 'codex', repoUrl: 'r', title: null, machine: null, updatedAt: 0, ...extra });

test("a message from another session is labelled with its agent and machine, not as the person's", () => {
  assert.equal(senderLabel(session({ parentSessionId: 'p', machine: 'workbox-test' }), agents), 'Helper · Codex on workbox-test');
  assert.equal(senderLabel(session({ parentSessionId: 'p', agent: 'claude' }), agents), 'Helper · Claude Code');
  assert.equal(senderLabel(session({}), agents), 'Session · Codex');
});
