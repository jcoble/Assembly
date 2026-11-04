// One bounded four-way turn using the same control/topics and bulk reads as the native app.
import { spawn, type ChildProcess } from 'node:child_process';
import { mkdir, rename, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { newSession } from './features/new-session.ts';
import { connectSocket, type Frame } from './features/socket.ts';

type Row = { sessionId: string; seq: number; prevSeq: number; runId: string; kind: string; payload: any };
type OwnerPromptAccepted = { sessionId: string; messageId: string; clientId: string; state: 'queued'; messageVersion: number; seq: number; epoch: number; runId: null };
function acceptedPrompt(value: any, session: string, clientId: string): OwnerPromptAccepted {
  if (value?.sessionId !== session || value.messageId !== `prompt/${session}/${clientId}` || value.clientId !== clientId ||
      value.state !== 'queued' || value.runId !== null ||
      !Number.isSafeInteger(value.messageVersion) || value.messageVersion < 1 ||
      !Number.isSafeInteger(value.seq) || value.seq < 1 || !Number.isSafeInteger(value.epoch) || value.epoch < 0)
    throw new Error('invalid committed owner prompt acknowledgment');
  return value;
}
type Feature = { name: string; pass: boolean; exitCode: number; steps: string[] };
export type CaseReceipt = { session: string; run: string; state: string; reply: string; acpSessionId: string | null; reason: string; features: Feature[] };
type Connection = Awaited<ReturnType<typeof connectSocket>>;

async function save(path: string, content: string) {
  const temporary = `${path}.${process.pid}.new`;
  await writeFile(temporary, content);
  await rename(temporary, path);
}
function text(value: unknown): string[] {
  if (Array.isArray(value)) return value.flatMap(text);
  if (!value || typeof value !== 'object') return [];
  return Object.entries(value).flatMap(([key, item]) => key === 'value' && typeof item === 'string' ? [item] : text(item));
}

export async function runCase(env: NodeJS.ProcessEnv): Promise<CaseReceipt> {
  const receipt: CaseReceipt = { session: '', run: '', state: '', reply: '', acpSessionId: null, reason: '', features: [] };
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(new Error('case did not complete within 240 seconds')), 240_000);
  const cancelled = new Promise<never>((_, reject) => controller.signal.addEventListener('abort', () => reject(controller.signal.reason), { once: true }));
  cancelled.catch(() => {});
  const interrupted = () => controller.abort(new Error('case interrupted'));
  process.on('SIGINT', interrupted);
  process.on('SIGTERM', interrupted);
  const connections: Connection[] = [];
  const children: { process: ChildProcess; done: Promise<Feature>; finished: () => boolean }[] = [];
  const tasks: Promise<unknown>[] = [];
  const rows: Row[] = [];
  const out = env.CHECK4_OUT!;
  const name = env.CHECK4_CASE!;
  const clean = (message: string) => [env.CHECK4_TOKEN, env.CHECK4_HOST_TOKEN].reduce<string>((s, token) => token ? s.replaceAll(token, '[redacted]') : s, message);
  const failed = (error: unknown) => {
    if (!controller.signal.aborted) controller.abort(error instanceof Error ? error : new Error(String(error)));
  };
  const observe = <T>(task: Promise<T>): Promise<T> => { tasks.push(task); task.catch(failed); return task; };
  try {
    await mkdir(out, { recursive: true });
    const features = JSON.parse(env.CHECK4_FEATURES ?? '[]') as string[];
    if (!Array.isArray(features) || features.some(f => typeof f !== 'string' || !/^[a-zA-Z0-9_-]+$/.test(f))) throw new Error('invalid feature scenario list');
    if (!/^[0-9a-f]{40}$/i.test(env.CHECK4_COMMIT ?? '')) throw new Error('case requires the checked build commit');
    const control = await connectSocket(env.CHECK4_API!, env.CHECK4_TOKEN!, { signal: controller.signal, commit: env.CHECK4_COMMIT });
    connections.push(control);
    const bulk = await connectSocket(env.CHECK4_API!, env.CHECK4_TOKEN!, { signal: controller.signal, endpoint: '/socket/bulk', commit: env.CHECK4_COMMIT });
    connections.push(bulk);
    const created = await newSession(control, env.CHECK4_AGENT!, env.CHECK4_REPO!, env.CHECK4_MACHINE === 'local' ? null : 'workbox-test');
    if (typeof created?.sessionId !== 'string' || !created.sessionId) throw new Error('the session was not created');
    receipt.session = created.sessionId;
    // Remote session identity belongs to its host, rather than the desktop's cached session item.
    let owner = control;
    if (env.CHECK4_MACHINE !== 'local') {
      owner = await connectSocket(env.CHECK4_HOST_API!, env.CHECK4_HOST_TOKEN!, {
        signal: controller.signal, machine: env.CHECK4_HOST_NAME, commit: env.CHECK4_HOST_COMMIT ?? env.CHECK4_COMMIT
      });
      connections.push(owner);
    }
    const sessions = await owner.subscribe('sessions', receipt.session);
    const stream = await control.subscribe('rows', receipt.session, 0);
    let last = 0;
    const accept = (row: Row) => {
      if (row.sessionId !== receipt.session || !Number.isSafeInteger(row.seq) || row.seq <= last || row.prevSeq !== last)
        throw new Error(`rows have a gap, duplicate or invalid session after ${last}`);
      last = row.seq;
      rows.push(row);
    };
    const cut = stream.state.seq;
    if (!Number.isSafeInteger(cut) || cut < 0) throw new Error('rows state has an invalid sequence');
    while (last < cut) {
      const page = await bulk.call('rows', 'after', { session: receipt.session, after: last, limit: 500 });
      const before = last;
      for (const row of page.rows ?? []) { if (row.seq <= cut) accept(row); }
      if (last === before) throw new Error('rows catch-up did not reach its snapshot cut');
    }
    const clientId = crypto.randomUUID();
    acceptedPrompt(await control.call('send', 'send', {
      session: receipt.session, text: 'Run the shell command hostname, then reply with only its output and, on the next line, the word four-way-ok.', clientId
    }), receipt.session, clientId);
    const identity = observe((async () => {
      // This freshly created case must receive its owner publication after send, not an idle pre-send snapshot.
      let frame: Frame = await sessions.next();
      for (;;) {
        const items = frame.t === 'state' ? (frame.items ?? []).map((entry: any) => entry.item) : [frame.item];
        for (const item of items) {
          if (item?.sessionId === receipt.session && typeof item.acpSessionId === 'string' && item.acpSessionId && item.activeRunId == null) {
            receipt.acpSessionId = item.acpSessionId;
            return;
          }
        }
        frame = await sessions.next();
      }
    })());
    // The committed acknowledgment never becomes a run receipt. Only the pushed user row
    // for this client submission binds the real provider run, even when rows precede the ACK.
    while (!receipt.run) {
      const delivered = rows.find(row => row.kind === 'user' && row.payload?.clientId === clientId);
      if (delivered) {
        if (typeof delivered.runId !== 'string' || !delivered.runId) throw new Error('the delivered prompt has no run identity');
        receipt.run = delivered.runId;
        break;
      }
      const frame = await stream.next();
      if (frame.t !== 'rows') throw new Error('rows topic sent an unexpected frame');
      accept(frame.row);
    }
    const ended = observe((async () => {
      for (;;) {
        for (const row of rows) if (row.runId === receipt.run && row.kind === 'turn' && row.payload.state !== 'started') {
          receipt.state = row.payload.state;
          if (receipt.state !== 'finished') throw new Error(`the turn ended ${receipt.state}`);
          return;
        }
        const frame = await stream.next();
        if (frame.t !== 'rows') throw new Error('rows topic sent an unexpected frame');
        accept(frame.row);
      }
    })());
    for (const feature of features) {
      const child = spawn(`scripts/features/${feature}.sh`, [], { detached: true, env: { ...env, CHECK4_SESSION: receipt.session, CHECK4_RUN: receipt.run }, stdio: ['ignore', 'pipe', 'pipe'] });
      let log = '', finished = false;
      child.stdout!.on('data', data => { log += String(data); });
      child.stderr!.on('data', data => { log += String(data); });
      const done = new Promise<Feature>(resolve => {
        let spawnError = '';
        child.on('error', error => { spawnError = error.message; });
        child.on('close', code => {
          finished = true;
          const result = { name: feature, pass: code === 0 && !spawnError, exitCode: code ?? 1, steps: clean(log + spawnError).split('\n').filter(Boolean) };
          resolve(result);
        });
      }).then(async result => { await save(join(out, `${name}.${feature}.log`), `${result.steps.join('\n')}\n`); return result; });
      children.push({ process: child, done, finished: () => finished });
    }
    const featureResults = observe(Promise.all(children.map(child => child.done)));
    await Promise.race([Promise.all([ended, identity, featureResults]), cancelled]);
    receipt.features = await featureResults;
    receipt.reply = rows.filter(row => row.runId === receipt.run && row.kind === 'message').flatMap(row => text(row.payload)).join('\n');
    if (receipt.state !== 'finished') throw new Error(`the turn ended ${receipt.state}`);
    if (!receipt.reply.includes(env.CHECK4_HOST_NAME!)) throw new Error('the reply does not show the owning machine hostname');
    if (!receipt.reply.includes('four-way-ok')) throw new Error('the reply does not contain four-way-ok');
    const bad = receipt.features.find(feature => !feature.pass);
    if (bad) throw new Error(`the ${bad.name} scenario failed (see ${name}.${bad.name}.log)`);
  } catch (error) {
    receipt.reason = clean(error instanceof Error ? error.message : String(error));
  } finally {
    clearTimeout(timeout);
    process.off('SIGINT', interrupted);
    process.off('SIGTERM', interrupted);
    controller.abort(new Error('case completed'));
    const held = children.filter(child => !child.finished());
    const signalGroups = (signal: NodeJS.Signals) => {
      for (const child of held) if (!child.finished() && child.process.pid) {
        try { process.kill(-child.process.pid, signal); }
        catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ESRCH') throw error; }
      }
    };
    signalGroups('SIGTERM');
    const escalation = setTimeout(() => signalGroups('SIGKILL'), 1000);
    try { receipt.features = await Promise.all(children.map(child => child.done)); }
    catch (error) { receipt.reason ||= clean(error instanceof Error ? error.message : String(error)); }
    finally { clearTimeout(escalation); }
    await Promise.allSettled(connections.map(connection => connection.close()));
    await Promise.allSettled(tasks);
    receipt.reply = rows.filter(row => row.runId === receipt.run && row.kind === 'message').flatMap(row => text(row.payload)).join('\n');
    await save(join(out, `${name}.rows.json`), JSON.stringify(rows.filter(row => !receipt.run || row.runId === receipt.run), null, 2));
  }
  return receipt;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const receipt = await runCase(process.env);
  console.log(JSON.stringify(receipt));
  process.exitCode = receipt.reason ? 1 : 0;
}
