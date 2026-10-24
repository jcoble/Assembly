import assert from 'node:assert/strict';
import { test } from 'node:test';
import { senderLabel } from '../src/lib/chat/sender.ts';
import type { Thread } from '../src/lib/api.ts';

const agents = [{ id: 'claude', name: 'Claude Code' }, { id: 'codex', name: 'Codex' }];
const thread = (extra: Partial<Thread>): Thread => ({ threadId: 't', agent: 'codex', repoUrl: 'r', title: null, machine: null, updatedAt: 0, ...extra });

test("a message from another session is labelled with its agent and machine, not as the person's", () => {
  assert.equal(senderLabel(thread({ parentThreadId: 'p', machine: 'workbox-test' }), agents), 'Helper · Codex on workbox-test');
  assert.equal(senderLabel(thread({ parentThreadId: 'p', agent: 'claude' }), agents), 'Helper · Claude Code');
  assert.equal(senderLabel(thread({}), agents), 'Session · Codex');
});
