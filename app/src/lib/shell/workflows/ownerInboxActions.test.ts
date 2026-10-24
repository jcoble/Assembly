// Run: node --experimental-test-module-mocks --experimental-strip-types src/lib/shell/workflows/ownerInboxActions.test.ts
import assert from 'node:assert/strict';
import { beforeEach, mock, test } from 'node:test';

type Impl = (...args: unknown[]) => Promise<unknown>;
const impl: Record<string, Impl> = {};
const fake = (name: string) => mock.fn((...args: unknown[]) => impl[name](...args));
const adoptSession = fake('adoptSession');
const sendStructuredMessage = fake('sendStructuredMessage');
const answerBoardQuestion = fake('answerBoardQuestion');
const ensureConversationSession = mock.fn();

const here = (path: string): string => new URL(path, import.meta.url).href;
mock.module(here('../conversation/conversationService.ts'), { namedExports: { adoptSession, sendStructuredMessage } });
mock.module(here('../conversation/conversationStore.svelte.ts'), { namedExports: { ensureConversationSession } });
mock.module(here('../stores/sessionRailStore.svelte.ts'), { namedExports: { rail: { owned: [{ ownedId: 'lead', agent: 'claude' }] } } });
mock.module(here('../board/boardSource.ts'), { namedExports: { answerBoardQuestion } });
mock.module(here('../components/sessionRowJump.ts'), { namedExports: { sessionRowJump: mock.fn() } });
mock.module(here('./workflowService.ts'), { namedExports: { answerWorkflowPause: mock.fn(), approveWorkflowGate: mock.fn() } });

const { answerQuestion, questionState } = await import('./ownerInboxActions.ts');

let next = 0;
const question = () => ({ machine: 'mac', id: ++next, askedAtMs: 7, ownedId: 'lead' });
const key = (q: { machine: string; id: number; askedAtMs: number }): string => `${q.machine}:${q.id}:${q.askedAtMs}`;
const live = (): AbortSignal => new AbortController().signal;

beforeEach(() => {
  for (const fn of [adoptSession, sendStructuredMessage, answerBoardQuestion, ensureConversationSession]) fn.mock.resetCalls();
  impl.adoptSession = async () => true;
  impl.sendStructuredMessage = async () => ({ accepted: true });
  impl.answerBoardQuestion = async () => true;
});

test('a double click sends and records once', async () => {
  const q = question();
  const first = answerQuestion(q, 'Q: yes', live());
  const second = answerQuestion(q, 'Q: yes', live());
  assert.equal(questionState(key(q)).busy, true);
  await Promise.all([first, second]);
  assert.equal(sendStructuredMessage.mock.callCount(), 1);
  assert.equal(answerBoardQuestion.mock.callCount(), 1);
  assert.deepEqual(ensureConversationSession.mock.calls[0].arguments, ['lead', 'claude']);
  assert.equal(questionState(key(q)).busy, false);
});

test('closing the board after the send still records the sent answer', async () => {
  const q = question();
  const controller = new AbortController();
  impl.sendStructuredMessage = async () => { controller.abort(); return { accepted: true }; };
  await answerQuestion(q, 'Q: yes', controller.signal);
  assert.equal(answerBoardQuestion.mock.callCount(), 1);
  assert.deepEqual(answerBoardQuestion.mock.calls[0].arguments.slice(0, 4), ['mac', q.id, 7, 'Q: yes']);
  assert.deepEqual(questionState(key(q)), { busy: false, unrecorded: undefined });
});

test('closing the board during the session lookup sends and records nothing', async () => {
  const q = question();
  const controller = new AbortController();
  impl.adoptSession = async () => { controller.abort(); return false; };
  assert.equal(await answerQuestion(q, 'Q: yes', controller.signal), undefined);
  assert.equal(sendStructuredMessage.mock.callCount(), 0);
  assert.equal(answerBoardQuestion.mock.callCount(), 0);
  assert.equal(questionState(key(q)).busy, false);
});

test('a session that cannot be found is refused', async () => {
  impl.adoptSession = async () => false;
  await assert.rejects(answerQuestion(question(), 'Q: yes', live()), { message: 'That session is not available yet.' });
  assert.equal(sendStructuredMessage.mock.callCount(), 0);
});

test('a sent answer that was not recorded is kept, and Retry only records it', async () => {
  const q = question();
  impl.answerBoardQuestion = async () => { throw new Error('machine is not reachable'); };
  await assert.rejects(answerQuestion(q, 'Q: yes', live()), { message: 'Sent, but not recorded. Retry records it only.' });
  assert.deepEqual(questionState(key(q)), { busy: false, unrecorded: 'Q: yes' });
  impl.answerBoardQuestion = async () => true;
  await answerQuestion(q, 'Q: something else', live());
  assert.equal(sendStructuredMessage.mock.callCount(), 1);
  assert.deepEqual(answerBoardQuestion.mock.calls[1].arguments.slice(0, 4), ['mac', q.id, 7, 'Q: yes']);
  assert.equal(questionState(key(q)).unrecorded, undefined);
});

test('a question the agent already marked counts as answered', async () => {
  const q = question();
  impl.answerBoardQuestion = async () => false;
  assert.equal(await answerQuestion(q, 'Q: yes', live()), undefined);
  assert.equal(questionState(key(q)).unrecorded, undefined);
});

test('a send failure passes through and records nothing', async () => {
  const q = question();
  impl.sendStructuredMessage = async () => { throw new Error('The terminal writer lease is not confirmed for this session'); };
  await assert.rejects(answerQuestion(q, 'Q: yes', live()), { message: 'The terminal writer lease is not confirmed for this session' });
  assert.equal(answerBoardQuestion.mock.callCount(), 0);
  assert.deepEqual(questionState(key(q)), { busy: false, unrecorded: undefined });
});

test('a send without a receipt is an error', async () => {
  impl.sendStructuredMessage = async () => undefined;
  await assert.rejects(answerQuestion(question(), 'Q: yes', live()), { message: 'The message was not sent.' });
  assert.equal(answerBoardQuestion.mock.callCount(), 0);
});

test('Explain more only sends', async () => {
  const q = question();
  await answerQuestion(q, 'Q: explain more', live(), false);
  assert.equal(sendStructuredMessage.mock.callCount(), 1);
  assert.equal(answerBoardQuestion.mock.callCount(), 0);
  assert.equal(questionState(key(q)).unrecorded, undefined);
});

test('the answer is sent as Q<n> without the composer screenshots and recorded as typed', async () => {
  const q = question();
  await answerQuestion(q, 'x'.repeat(600), live());
  assert.deepEqual(sendStructuredMessage.mock.calls[0].arguments, ['lead', `Q${q.id}: ${'x'.repeat(600)}`, undefined, false]);
  assert.deepEqual(answerBoardQuestion.mock.calls[0].arguments.slice(0, 4), ['mac', q.id, 7, 'x'.repeat(600)]);
});

test('a question asked again starts without the earlier unrecorded answer', async () => {
  const q = question();
  impl.answerBoardQuestion = async () => { throw new Error('machine is not reachable'); };
  await assert.rejects(answerQuestion(q, 'yes', live()));
  assert.equal(questionState(key({ ...q, askedAtMs: 9 })).unrecorded, undefined);
});
