// Run: node --experimental-strip-types scripts/backgroundSessionSend.test.ts
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { generationForSend, sendTargetGeneration } from '../src/lib/shell/conversation/conversationActivation.ts';
import { normalizeProvider } from '../src/lib/shell/ownedSessions.ts';

const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8')
  .match(/export async function sendStructuredMessage\([\s\S]*?\n\}\n\n\/\*\* Stops the active turn/);
assert.ok(source);
const code = stripTypeScriptTypes(source[0].replace(/\n\n\/\*\* Stops the active turn$/, '').replace('export async function', 'async function'), { mode: 'strip' });

await test('a send to a session that is not the selected chat connects it first (a Board answer)', async () => {
  const ownedId = 'background';
  const state = {
    attachments: [], generation: 0, connectionState: null as string | null,
    agentConfig: { reasoningEffort: null, availableApprovalPolicies: [] },
    writerLease: { ownedId, generation: 0, owner: 'structured' }
  };
  const sent: Array<{ generation: number; text: string }> = [];
  const dependencies = {
    getConversationSession: () => state,
    setConversationSending: () => undefined,
    rail: { activeOwnedId: 'selected', owned: [{ ownedId, agent: 'claude', state: 'live', origin: 'app', executionOwner: 'structured', cwd: '/work' }] },
    get: () => ({}), sessionPresenceHistory: {},
    shouldReviveBeforeSend: () => true,
    normalizeProvider,
    decideConversationActivation: () => ({ kind: 'structured', nativeSessionMode: 'resume' }),
    // Like the real one, ensuring a session that is not selected returns its connection without applying it.
    ensureStructuredConversation: async () => ({ ownedId, provider: 'claude', generation: 3, state: 'connected' }),
    setConversationConnection: (connection: { generation: number; state: string }) => {
      state.generation = connection.generation; state.connectionState = connection.state;
    },
    generationForSend, sendTargetGeneration,
    sendSupportsImages: () => false,
    buildConversationPrompt: (text: string) => ({ text, content: [] }),
    updateOwnedSession: () => undefined,
    invoke: async (_command: string, args: { request: { generation: number; text: string } }) => {
      sent.push({ generation: args.request.generation, text: args.request.text });
      return { accepted: true };
    }
  };
  const send = Function(...Object.keys(dependencies), `const preparingSends = new Map();\n${code}\nreturn sendStructuredMessage;`)(...Object.values(dependencies)) as (id: string, text: string) => Promise<unknown>;
  await send(ownedId, 'Q3: yes');
  assert.deepEqual(sent, [{ generation: 3, text: 'Q3: yes' }]);
});
