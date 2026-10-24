/// <reference types="node" />

// Jump to latest keeps the older pages the reader loaded. The real connection
// module runs here against the real store; only the backend reads and the
// TanStack chat are stand-ins.
import assert from 'node:assert/strict';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { compileModule } from 'svelte/compiler';
import { EventType, StreamProcessor } from '@tanstack/ai/client';
import * as conversationMessages from '../src/lib/shell/conversation/conversationMessages.ts';

function compileForTest(path: URL, filename: string): string {
  const stripped = stripTypeScriptTypes(readFileSync(path, 'utf8'), { mode: 'strip' });
  return compileModule(stripped, { generate: 'client', filename }).js.code
    .replaceAll('import.meta.env', '({ DEV: false })');
}

const storeOutput = fileURLToPath(new URL('../src/lib/shell/conversation/.conversationJumpStore.test.mjs', import.meta.url));
const diagnosticsOutput = fileURLToPath(new URL('../src/lib/shell/.conversationJumpDiagnostics.test.mjs', import.meta.url));
writeFileSync(storeOutput, compileForTest(
  new URL('../src/lib/shell/conversation/conversationStore.svelte.ts', import.meta.url), 'conversationStore.svelte.js'
).replace(/\.\.\/resourceDiagnostics\.svelte(\.ts|\.js)/, '../.conversationJumpDiagnostics.test.mjs'));
writeFileSync(diagnosticsOutput, compileForTest(
  new URL('../src/lib/shell/resourceDiagnostics.svelte.ts', import.meta.url), 'resourceDiagnostics.svelte.js'
));
(globalThis as { $state?: unknown }).$state = (value: unknown) => value;
let store: any;
try {
  store = await import(`${storeOutput}?test=${Date.now()}`);
} finally {
  rmSync(storeOutput, { force: true });
  rmSync(diagnosticsOutput, { force: true });
}

const PAGE = 10;

/** Messages first..last, one user message per turn. */
function page(first: number, last: number, hasBefore: boolean) {
  const numbers = Array.from({ length: last - first + 1 }, (_, index) => first + index);
  return {
    items: numbers.map((n) => ({ itemId: `u${n}`, stableId: `u${n}`, firstTimestampMs: n,
      firstSequence: n, lastSequence: n, positionKnown: true, prefixComplete: true })),
    events: [],
    chunks: numbers.flatMap((n) => [
      { type: EventType.TEXT_MESSAGE_START, messageId: `u${n}`, role: 'user', metadata: { itemType: 'user-message', turnId: `t${n}`, startedAtMs: n, firstSequence: n } },
      { type: EventType.TEXT_MESSAGE_CONTENT, messageId: `u${n}`, delta: `message ${n}` },
      { type: EventType.TEXT_MESSAGE_END, messageId: `u${n}` }
    ]),
    turns: numbers.map((n) => ({ turnId: `t${n}` })),
    watermark: last, transferBytes: 0, oversized: false, hasBefore, hasAfter: false,
    hasEarlierTranscript: false, beforeCursor: first, afterCursor: last
  };
}

function loadConnection() {
  const source = readFileSync(new URL('../src/lib/shell/conversation/conversationConnection.ts', import.meta.url), 'utf8');
  const body = source.slice(source.indexOf('const PAGE_BYTES')).replace(/^export /gm, '');
  const createChat = (options: any) => {
    const processor = new StreamProcessor();
    const chat = {
      get messages() { return processor.getMessages(); },
      dispose: () => undefined,
      setMessages: (messages: any[]) => processor.setMessages(messages),
      async sendMessage(message: any) {
        processor.setMessages([...chat.messages, { id: 'optimistic', role: 'user', parts: message.content, metadata: message.metadata }]);
        await options.connection.send(chat.messages, undefined, undefined, { runId: 'run' });
      }
    };
    void (async () => {
      for await (const chunk of options.connection.subscribe()) {
        if (chunk.type === EventType.CUSTOM) options.onCustomEvent(chunk.name, chunk.value);
        else processor.processChunk(chunk);
      }
    })();
    return chat;
  };
  const dependencies = {
    normalizeProvider: (agent: string) => ({ agent }),
    EventType,
    StreamProcessor,
    createChat,
    resumeConversationRun: async () => undefined,
    attachmentDisplayMetadata: (attachment: unknown) => attachment,
    discardRestoredAttachments: () => undefined,
    readNewerSelectedConversationItems: async () => null,
    readOlderSelectedConversationItems: async (_id: string, cursor: number) =>
      page(Math.max(1, cursor - PAGE), cursor - 1, cursor - PAGE > 1),
    readSelectedConversationAttachments: async () => [],
    restoreAttachmentList: async () => [],
    sendStructuredMessage: async () => undefined,
    subscribeSelectedConversation: async (options: any) => {
      const latest = page(21, 30, true);
      options.onSnapshot({ page: latest, chunks: latest.chunks, runs: [], pendingEvents: [], pendingSequence: 30, suspended: false,
        connection: { generation: 1, provider: 'codex', state: 'connected' } });
      return () => undefined;
    },
    ...conversationMessages,
    ...store,
    extendAgentConversationImportFromTauri: async () => null,
    rail: { owned: [] }
  };
  return Function(...Object.keys(dependencies), `${stripTypeScriptTypes(body, { mode: 'strip' })}
    return { selectConversationChat, selectedConversationChatReady, pageSelectedConversation, jumpSelectedConversationToLatest };
  `)(...Object.values(dependencies));
}

/** Lets the chat take what its stream delivered. */
const settle = () => new Promise((resolve) => setImmediate(resolve));
const ids = (messages: { id: string }[]) => messages.map((message) => message.id);
const range = (first: number, last: number, prefix: string) =>
  Array.from({ length: last - first + 1 }, (_, index) => `${prefix}${first + index}`);

test('Jump to latest keeps the older pages already loaded', async () => {
  const api = loadConnection();
  const chat = api.selectConversationChat('chat');
  await api.selectedConversationChatReady('chat');
  await api.pageSelectedConversation('older');
  await settle();
  assert.deepEqual(ids(chat.messages), range(11, 30, 'u'), 'one older page is loaded');

  await api.jumpSelectedConversationToLatest('chat');
  await settle();
  assert.deepEqual(ids(chat.messages), range(11, 30, 'u'), 'the older page stays loaded');
});
