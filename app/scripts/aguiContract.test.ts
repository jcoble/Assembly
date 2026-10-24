/**
 * AG-UI contract: the backend's chat chunks, fed through the pinned stock chat
 * client (@tanstack/ai-client) and checked against the @ag-ui/core schemas.
 *
 * `fixtures/agui-chunks.jsonl` holds the chunks the backend's own projection
 * writes for its contract scenarios (`cargo test contract_scenarios` with
 * `AGUI_FIXTURE_OUT` set): permission pauses answered both ways, a question
 * with a numeric response schema, and a failed turn. Any other chunk file, one
 * chunk per line (bare or as `{ "chunk": ... }`), can be passed as an argument
 * and runs through the same cases.
 *
 * A fake backend answers each `send()` with the next recorded run, renamed to
 * the run id the client chose, as the real backend does. Every chunk it
 * delivers must pass the AG-UI schemas, and every interrupt the client shows
 * must be bound and resolvable.
 *
 * Run: node --experimental-strip-types scripts/aguiContract.test.ts [chunks.jsonl ...]
 */
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { basename } from 'node:path';
import test from 'node:test';
import { EventSchemas } from '@ag-ui/core/schemas';
import { canonicalInterruptJson, digestInterruptJson, type StreamChunk } from '@tanstack/ai/client';
import { ChatClient, type RunAgentInputContext } from '@tanstack/ai-client';
import { pendingRequestsOf } from '../src/lib/shell/conversation/conversationMessages.ts';

type Chunk = StreamChunk & { runId?: string; parentRunId?: string; threadId?: string; messageId?: string; delta?: string };
interface Run { runId: string; chunks: Chunk[]; paused: boolean }

const BINDING = 'tanstack:interruptBinding';
/** A wait that never ends fails the case instead of hanging the run. */
const GUARD = { timeout: 10_000 };
const fixtures = [new URL('./fixtures/agui-chunks.jsonl', import.meta.url).pathname, ...process.argv.slice(2)];

function readChunks(path: string): Chunk[] {
  return readFileSync(path, 'utf8').split('\n').filter((line) => line.trim()).map((line) => {
    const value = JSON.parse(line);
    return value.chunk ?? value;
  });
}

/**
 * Each run from its RUN_STARTED to its RUN_FINISHED; `paused` when that finish is an interrupt.
 * Once any surface answers, the backend ends the paused run again with a plain RUN_FINISHED,
 * just before the continuation starts; that finish leads the continuation's chunks. Every
 * text message ends once, and before its run does (a pause may leave one open).
 */
function runsOf(chunks: Chunk[]): Run[] {
  const runs: Run[] = [];
  let open: Run | null = null;
  let answered: Chunk[] = [];
  const texts = new Set<string>();
  for (const chunk of chunks) {
    const interrupt = chunk.type === 'RUN_FINISHED' && (chunk as { outcome?: { type?: string } }).outcome?.type === 'interrupt';
    const last = runs.at(-1);
    if (!open && chunk.type === 'RUN_FINISHED' && !interrupt && last?.paused && chunk.runId === last.runId) {
      answered.push(chunk);
      continue;
    }
    if (chunk.type === 'RUN_STARTED') {
      open = { runId: chunk.runId!, chunks: answered, paused: false };
      answered = [];
    }
    assert.ok(open, `${chunk.type} arrived outside a run`);
    open.chunks.push(chunk);
    if (chunk.type === 'TEXT_MESSAGE_START') {
      assert.ok(!texts.has(chunk.messageId!), `text message ${chunk.messageId} started twice`);
      texts.add(chunk.messageId!);
    } else if (chunk.type === 'TEXT_MESSAGE_CONTENT' || chunk.type === 'TEXT_MESSAGE_END') {
      assert.ok(texts.has(chunk.messageId!), `${chunk.type} for text message ${chunk.messageId} that is not open`);
      if (chunk.type === 'TEXT_MESSAGE_END') texts.delete(chunk.messageId!);
    }
    if (chunk.type === 'RUN_FINISHED' || chunk.type === 'RUN_ERROR') {
      open.paused = interrupt;
      if (!interrupt) assert.deepEqual([...texts], [], `run ${open.runId} ended with text messages still open`);
      runs.push(open);
      open = null;
    }
  }
  return runs;
}

function assertSchema(chunk: unknown): void {
  const result = EventSchemas.safeParse(chunk);
  assert.ok(result.success, `AG-UI schema rejected ${JSON.stringify(chunk).slice(0, 200)}: ${JSON.stringify(result.error?.issues)}`);
}

/** A stand-in for the backend: a subscription queue, and a script of runs to answer each send with. */
function backend(script: Run[]) {
  const queue: Chunk[] = [];
  const liveIds = new Map<string, string>();
  const sends: RunAgentInputContext[] = [];
  let wake: (() => void) | null = null;
  let waiting = false;
  let idleWaiters: Array<PromiseWithResolvers<void>> = [];
  let sendWaiters: Array<PromiseWithResolvers<RunAgentInputContext>> = [];
  // A failure inside the client's own send would otherwise leave the test waiting.
  let failure: unknown;
  const fail = (error: unknown): void => {
    failure ??= error;
    for (const waiter of [...idleWaiters.splice(0), ...sendWaiters.splice(0)]) waiter.reject(error);
  };
  const waiter = <T>(waiters: Array<PromiseWithResolvers<T>>): Promise<T> => {
    const next = Promise.withResolvers<T>();
    if (failure) next.reject(failure);
    else waiters.push(next);
    return next.promise;
  };

  const rename = (chunk: Chunk): Chunk => {
    let text = JSON.stringify(chunk);
    for (const [recorded, live] of liveIds) text = text.replaceAll(recorded, live);
    return JSON.parse(text);
  };
  const deliver = (chunks: Chunk[]): void => {
    queue.push(...chunks);
    waiting = false;
    wake?.();
    wake = null;
  };
  const push = (...chunks: Chunk[]): void => {
    const delivered = chunks.map(rename);
    for (const chunk of delivered) assertSchema(chunk);
    deliver(delivered);
  };
  return {
    sends,
    push,
    /** The app folds the backend's page into this snapshot itself; it is not AG-UI wire. */
    snapshot(messages: unknown): void {
      deliver([{ type: 'MESSAGES_SNAPSHOT', messages } as unknown as Chunk]);
    },
    connection: {
      async *subscribe(signal?: AbortSignal): AsyncIterable<StreamChunk> {
        const stop = (): void => wake?.();
        signal?.addEventListener('abort', stop, { once: true });
        try {
          while (!signal?.aborted) {
            const next = queue.shift();
            if (next) {
              yield next;
              continue;
            }
            waiting = true;
            for (const idle of idleWaiters.splice(0)) idle.resolve();
            await new Promise<void>((resolve) => { wake = resolve; });
          }
        } finally {
          signal?.removeEventListener('abort', stop);
        }
      },
      async send(_messages: unknown, _data: unknown, _signal?: AbortSignal, context?: RunAgentInputContext): Promise<void> {
        try {
          assert.ok(context, 'the client sends a run context');
          sends.push(context);
          const run = script.shift();
          assert.ok(run, `no recorded run left for send ${context.runId}`);
          liveIds.set(run.runId, context.runId);
          push(...run.chunks);
          for (const sent of sendWaiters.splice(0)) sent.resolve(context);
        } catch (error) {
          fail(error);
          throw error;
        }
      }
    },
    /** Resolves once the client has taken every queued chunk. */
    idle(): Promise<void> {
      if (waiting && !queue.length && !failure) return Promise.resolve();
      return waiter(idleWaiters);
    },
    nextSend(): Promise<RunAgentInputContext> {
      return waiter(sendWaiters);
    },
    liveId(recorded: string): string {
      return liveIds.get(recorded) ?? recorded;
    }
  };
}

function client(server: ReturnType<typeof backend>, threadId: string) {
  const errors: Error[] = [];
  const chat = new ChatClient({ connection: server.connection, threadId, queue: 'drop', onError: (error) => errors.push(error) });
  chat.subscribe();
  return { chat, errors };
}

function interruptsOf(chat: ChatClient) {
  return chat.getSnapshot().interruptState.interrupts;
}

/** The composer waits while the chat holds any interrupt, for steers and new turns alike. */
function locked(chat: ChatClient): boolean {
  return interruptsOf(chat).length > 0;
}

/** Every interrupt shown is bound to this backend and can be answered. */
function assertResolvable(chat: ChatClient): void {
  const interrupts = interruptsOf(chat);
  assert.ok(interrupts.length > 0, 'the pause shows an interrupt');
  for (const interrupt of interrupts) {
    assert.equal(interrupt.kind, 'generic', `interrupt ${interrupt.id} is bound`);
    assert.equal(interrupt.status, 'pending', `interrupt ${interrupt.id} is pending`);
    assert.equal(interrupt.canResolve, true, `interrupt ${interrupt.id} can be resolved (binding and schema hash match)`);
  }
}

function firstOption(chat: ChatClient): { optionId: string } {
  const schema = interruptsOf(chat)[0].responseSchema as { properties?: { optionId?: { enum?: string[] } } };
  const optionId = schema.properties?.optionId?.enum?.[0];
  assert.ok(optionId, 'the permission schema lists its options');
  return { optionId };
}

function resolveFirst(chat: ChatClient, payload: { optionId: string }): void {
  const [first] = interruptsOf(chat);
  assert.ok(first && first.kind === 'generic' && 'resolveInterrupt' in first, 'the first interrupt can be answered');
  first.resolveInterrupt(payload);
}

function assistantText(chat: ChatClient, messageId: string): string {
  const message = chat.getSnapshot().messages.find((candidate) => candidate.id === messageId);
  return (message?.parts ?? []).flatMap((part) => part.type === 'text' ? [part.content] : []).join('');
}

function recordedText(run: Run, messageId: string): string {
  return run.chunks.flatMap((chunk) => chunk.type === 'TEXT_MESSAGE_CONTENT' && chunk.messageId === messageId ? [chunk.delta ?? ''] : []).join('');
}

function firstTextMessageId(run: Run): string {
  const start = run.chunks.find((chunk) => chunk.type === 'TEXT_MESSAGE_START');
  assert.ok(start?.messageId, `continuation ${run.runId} streams a reply`);
  return start.messageId;
}

/** The first paused run and the run that continues it. */
function pauseAndContinuation(chunks: Chunk[]): { pause: Run; continuation: Run; threadId: string } {
  const runs = runsOf(chunks);
  const pause = runs.find((run) => run.paused);
  assert.ok(pause, 'the recording has a paused run');
  const continuation = runs.find((run) => run.chunks.find((chunk) => chunk.type === 'RUN_STARTED')?.parentRunId === pause.runId);
  assert.ok(continuation, `the recording continues run ${pause.runId}`);
  return { pause, continuation, threadId: pause.chunks[0].threadId! };
}

/** Starts a turn and plays the recorded pause; the client ends up waiting on its interrupt. */
async function pausedTurn(chunks: Chunk[], script: (pause: Run, continuation: Run) => Run[]) {
  const { pause, continuation, threadId } = pauseAndContinuation(chunks);
  const server = backend(script(pause, continuation));
  const { chat, errors } = client(server, threadId);
  const sent = server.nextSend();
  void chat.sendMessage('Write the file');
  await sent;
  await server.idle();
  return { server, chat, errors, pause, continuation, threadId };
}

for (const path of fixtures) {
  const name = basename(path);
  const chunks = readChunks(path);

  test(`${name}: every recorded chunk passes the AG-UI schemas`, GUARD, () => {
    for (const chunk of chunks) assertSchema(chunk);
    assert.ok(runsOf(chunks).length > 0);
  });

  test(`${name}: every recorded pause is a resolvable interrupt and locks the composer`, GUARD, async () => {
    for (const pause of runsOf(chunks).filter((run) => run.paused)) {
      const server = backend([]);
      const { chat, errors } = client(server, pause.chunks[0].threadId!);
      assert.equal(locked(chat), false, 'nothing is open before the pause');
      server.push(...pause.chunks);
      await server.idle();
      assertResolvable(chat);
      assert.equal(locked(chat), true, 'an open interrupt locks the composer');
      const { approvals, inputs } = pendingRequestsOf(interruptsOf(chat), pause.chunks[0].threadId!);
      assert.equal(approvals.length + inputs.length, interruptsOf(chat).length, 'every interrupt is an approval or a question');
      const permissions = interruptsOf(chat).filter((interrupt) => 'optionId' in ((interrupt.responseSchema as { properties?: object }).properties ?? {}));
      for (const [index, interrupt] of permissions.entries()) {
        const schema = interrupt.responseSchema as { properties: { optionId: { enum: string[] } } };
        assert.deepEqual(approvals[index].options.map((option) => option.optionId), schema.properties.optionId.enum,
          'the approval panel offers the options the interrupt accepts');
        assert.equal(approvals[index].toolTitle, interrupt.message);
      }
      assert.deepEqual(errors, []);
      chat.dispose();
    }
  });

  test(`${name}: resolving the interrupt resumes the paused run and unlocks the composer`, GUARD, async () => {
    const { server, chat, errors, pause, continuation } = await pausedTurn(chunks, (p, c) => [p, c]);
    assertResolvable(chat);
    const answer = firstOption(chat);
    const interruptId = interruptsOf(chat)[0].id;
    const resumed = server.nextSend();
    resolveFirst(chat, answer);
    const context = await resumed;
    assert.equal(context.parentRunId, server.liveId(pause.runId), 'the continuation names the paused run');
    assert.deepEqual(context.resume, [{ interruptId, status: 'resolved', payload: answer }]);
    await server.idle();
    assert.equal(interruptsOf(chat).length, 0, 'the answered interrupt is gone');
    assert.equal(locked(chat), false);
    const messageId = firstTextMessageId(continuation);
    assert.equal(assistantText(chat, messageId), recordedText(continuation, messageId), 'the continuation reply streams in');
    assert.deepEqual(errors, []);
    chat.dispose();
  });

  test(`${name}: reopening replays the snapshot, then the re-sent pause, then live deltas`, GUARD, async () => {
    const first = await pausedTurn(chunks, (p) => [p]);
    const snapshot = first.chat.getSnapshot().messages;
    first.chat.dispose();
    const { continuation, threadId, pause } = first;
    const server = backend([continuation]);
    const { chat, errors } = client(server, threadId);
    server.snapshot(snapshot);
    await server.idle();
    assert.deepEqual(chat.getSnapshot().messages.map((message) => message.id), snapshot.map((message) => message.id));
    server.push(pause.chunks.at(-1)!);
    await server.idle();
    assertResolvable(chat);
    assert.equal(locked(chat), true);
    const resumed = server.nextSend();
    resolveFirst(chat, firstOption(chat));
    assert.equal((await resumed).parentRunId, pause.runId);
    await server.idle();
    const ids = chat.getSnapshot().messages.map((message) => message.id);
    assert.equal(new Set(ids).size, ids.length, 'no message is drawn twice');
    const messageId = firstTextMessageId(continuation);
    assert.equal(assistantText(chat, messageId), recordedText(continuation, messageId));
    assert.equal(locked(chat), false);
    assert.deepEqual(errors, []);
    chat.dispose();
  });

  test(`${name}: reopening mid-reply keeps the partial reply`, GUARD, async () => {
    const { continuation, threadId } = pauseAndContinuation(chunks);
    const messageId = firstTextMessageId(continuation);
    const deltas = continuation.chunks.filter((chunk) => chunk.type === 'TEXT_MESSAGE_CONTENT' && chunk.messageId === messageId);
    assert.ok(deltas.length >= 2, 'the reply arrives in more than one delta');
    const cut = continuation.chunks.indexOf(deltas[Math.floor(deltas.length / 2)]);
    const live = backend([]);
    const first = client(live, threadId);
    live.push(...continuation.chunks.slice(0, cut));
    await live.idle();
    const partial = assistantText(first.chat, messageId);
    assert.ok(partial.length > 0 && partial !== recordedText(continuation, messageId), 'the cut lands mid-reply');
    const snapshot = first.chat.getSnapshot().messages;
    first.chat.dispose();

    const server = backend([]);
    const { chat, errors } = client(server, threadId);
    server.snapshot(snapshot);
    await server.idle();
    assert.equal(assistantText(chat, messageId), partial, 'the reopened chat shows the partial reply');
    server.push(...continuation.chunks.slice(cut));
    await server.idle();
    assert.equal(assistantText(chat, messageId), recordedText(continuation, messageId), 'later deltas extend it, nothing lost or repeated');
    assert.deepEqual(errors, []);
    chat.dispose();
  });

  test(`${name}: a request answered on another surface clears the interrupt and unlocks the composer`, GUARD, async () => {
    const { server, chat, errors, pause, continuation, threadId } = await pausedTurn(chunks, (p) => [p]);
    assert.equal(locked(chat), true);
    // The backend ends the paused run as soon as any surface answers, then continues under it.
    const start = continuation.chunks.findIndex((chunk) => chunk.type === 'RUN_STARTED');
    let answer = continuation.chunks.slice(0, start);
    if (!answer.length) {
      answer = [{ type: 'RUN_FINISHED', threadId, runId: pause.runId } as Chunk];
      console.log(`${name}: no answer finish recorded; checked a synthesized one`);
    }
    server.push(...answer);
    await server.idle();
    assert.equal(interruptsOf(chat).length, 0, 'the answered pause is gone');
    assert.equal(locked(chat), false, 'the composer unlocks');
    server.push(...continuation.chunks.slice(start));
    await server.idle();
    assert.equal(interruptsOf(chat).length, 0);
    const messageId = firstTextMessageId(continuation);
    assert.equal(assistantText(chat, messageId), recordedText(continuation, messageId));
    assert.equal(server.sends.length, 1, 'this surface sent nothing for the answer');
    assert.deepEqual(errors, []);
    chat.dispose();
  });

  test(`${name}: a response schema with a non-integer and a very large number stays bound`, GUARD, async () => {
    const isNumeric = (schema: unknown): boolean => {
      const numbers = JSON.stringify(schema).match(/-?\d+(\.\d+)?([eE][+-]?\d+)?/g)?.map(Number) ?? [];
      return numbers.some((value) => !Number.isInteger(value)) && numbers.some((value) => Math.abs(value) >= 1e16);
    };
    const pauses = runsOf(chunks).filter((run) => run.paused);
    let pause = pauses.find((run) => {
      const interrupts = (run.chunks.at(-1) as { outcome: { interrupts: Array<{ responseSchema?: unknown }> } }).outcome.interrupts;
      return interrupts.some((interrupt) => isNumeric(interrupt.responseSchema));
    });
    if (!pause) {
      // No such schema was recorded: add one to a recorded pause, hashed as the client hashes.
      const recorded = structuredClone(pauses[0]);
      const finish = recorded.chunks.at(-1) as unknown as { outcome: { interrupts: Array<{ responseSchema: { properties: Record<string, unknown> }; metadata: Record<string, Record<string, unknown>> }> } };
      const interrupt = finish.outcome.interrupts[0];
      interrupt.responseSchema.properties.budget = { type: 'number', minimum: 0.25, maximum: 1e20, multipleOf: 0.25 };
      interrupt.metadata[BINDING].responseSchemaHash = digestInterruptJson(canonicalInterruptJson(interrupt.responseSchema));
      pause = recorded;
      console.log(`${name}: no numeric response schema recorded; checked a synthesized one`);
    }
    const server = backend([]);
    const { chat, errors } = client(server, pause.chunks[0].threadId!);
    server.push(...pause.chunks);
    await server.idle();
    assertResolvable(chat);
    const numeric = interruptsOf(chat).find((interrupt) => isNumeric(interrupt.responseSchema));
    assert.ok(numeric, 'the numeric schema arrives intact');
    assert.equal(numeric.canResolve, true, 'its schema hash matches after the JSON round trip');
    assert.deepEqual(errors, []);
    chat.dispose();
  });
}
