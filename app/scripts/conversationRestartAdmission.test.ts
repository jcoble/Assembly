import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
const { ChatClient } = await import(new URL('../../ai-client/dist/esm/index.js', import.meta.resolve('@tanstack/ai-svelte')).href);
import { EventType } from '@tanstack/ai/client';

const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
const block = source.slice(source.indexOf('function snapshotChunks('), source.indexOf('function admitInitialSnapshot('));

// The backend ends the chat's run in the snapshot when its turn ended unseen
// (a restart, a lost connection); a turn that goes on keeps the send busy.
for (const [name, ended] of [
  ['a snapshot that ends the chat run admits the next send', true],
  ['a snapshot whose turn goes on keeps the send busy', false]
] as const) {
  await test(name, async () => {
    const chunks: any[] = [];
    let wake: (() => void) | undefined;
    let observed = Promise.withResolvers<void>();
    const selection: any = { workspaceOwnedId: 'session', historyOwnedId: 'session', chat: { messages: [] } };
    const dependencies = {
      EventType, hasOlderHistory: () => false, messagesFromPage: () => [],
      getConversationSession: () => null, historyPosition: () => undefined,
      resetMessageBytes: () => {}, applySelectedConversationSnapshotState: () => {},
      pushMessagesSnapshot: () => {}, restorePageAttachments: () => {}, pageAttachments: () => new Map()
    };
    const snapshotChunks = Function(...Object.keys(dependencies), `${stripTypeScriptTypes(block, { mode: 'strip' })}; return snapshotChunks;`)(...Object.values(dependencies));
    let sends = 0;
    let runId = '';
    const client = new ChatClient({ id: 'session', queue: 'drop', live: true, connection: {
      async *subscribe(signal: AbortSignal) {
        signal.addEventListener('abort', () => wake?.(), { once: true });
        while (!signal.aborted) {
          if (chunks.length) yield chunks.shift();
          else await new Promise<void>(resolve => { wake = resolve; });
        }
      },
      async send(_messages: unknown, _data: unknown, _signal: unknown, context: { runId: string }) {
        sends++;
        runId = context.runId;
        observed.resolve();
        if (sends > 1) {
          chunks.push({ type: EventType.RUN_FINISHED, runId: context.runId });
          wake?.();
        }
      }
    }, onChunk: (chunk: any) => { if (chunk.name === 'barrier') observed.resolve(); } });
    try {
      const first = client.sendMessage('Long running turn');
      await observed.promise;
      observed = Promise.withResolvers<void>();
      const runs = ended ? [{ type: EventType.RUN_FINISHED, threadId: 'session', runId }] : [];
      snapshotChunks(selection, { connection: { generation: 1 }, pendingSequence: 10, page: { turns: [], events: [] }, pendingEvents: [], chunks: [], runs },
        (chunk: unknown) => chunks.push(chunk));
      chunks.push({ type: EventType.CUSTOM, name: 'barrier' });
      wake?.();
      await observed.promise;
      assert.equal(client.getIsLoading(), !ended);
      if (ended) await first;
      await client.sendMessage('RESTART_RECOVERED');
      assert.equal(sends, ended ? 2 : 1, 'a backend restart must release the client send gate');
    } finally { client.dispose(); }
  });
}
