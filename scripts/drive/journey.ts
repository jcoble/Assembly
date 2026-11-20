// Runs one journey for one case (an agent on a machine) in the bench's window, and writes what a person needs to judge
// it in a minute: report.md (each step, ok or failed, and for a failure what was expected and what the rows showed),
// result.json, the case's video, and on a failure a screenshot and the end of the app log.
//   node scripts/journeys/<name>.ts <claude|codex> <local|workbox-test> <output folder>
// It reads the bench's state (bench.sh up). A provider failure is BLOCKED, never FAIL. Whatever happens, a run this
// journey started is stopped and its end awaited before the sockets close.
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import * as ax from './ax.ts';
import { startVideo, shot, type Video } from './record.ts';
import { describe, openRows, type Row, type RowFeed } from './rows.ts';
import { connectSocket } from './socket.ts';
import { isLimit, providerBlock } from './verdict.ts';

export type Agent = 'claude' | 'codex';
export type Machine = 'local' | 'workbox-test';
export type BenchState = {
  sourceCommit: string; servedCommit?: string; api: string; tokenFile: string; display: string; dbusAddress: string; appLog: string;
  projects: Record<Machine, { id: string; path: string }>; blocked: Record<string, string>;
};
type Step = { what: string; ok: boolean; ms: number; seen?: string };
export type Result = { case: string; state: 'PASS' | 'FAIL' | 'BLOCKED'; reason: string; steps: Step[] };

export type Journey = {
  agent: Agent; machine: Machine; marker: string;
  /** Makes a session for this case's project, opens it in the window and gives the chat focus; its rows follow. */
  openSession(): Promise<RowFeed>;
  type(id: string, text: string): Promise<void>;
  click(id: string): Promise<void>;
  expectVisible(id: string, seconds?: number): Promise<void>;
  expectGone(id: string, seconds?: number): Promise<void>;
  step<T>(what: string, act: () => Promise<T>): Promise<T>;
  check(what: string, ok: boolean, seen: string): void;
};

class Failed extends Error {
  readonly seen: string;
  constructor(what: string, seen: string) { super(`${what}: ${seen}`); this.seen = seen; }
}

export function benchState(path = process.env.BENCH_STATE): BenchState {
  if (!path) throw new Error('BENCH_STATE is not set; run scripts/bench.sh up');
  return JSON.parse(readFileSync(path, 'utf8'));
}

const atomic = (file: string, text: string) => { writeFileSync(file + '.new', text); renameSync(file + '.new', file); };

function report(name: string, result: Result, rows: Row[]): string {
  const lines = [`# ${name}: ${result.case}`, '', `**${result.state}**${result.reason ? `: ${result.reason}` : ''}`, '', '## Steps', ''];
  for (const step of result.steps) {
    lines.push(`- ${step.ok ? 'ok  ' : 'FAIL'} ${step.what} (${(step.ms / 1000).toFixed(1)} s)`);
    if (step.seen) lines.push('', '```', step.seen, '```', '');
  }
  lines.push('', '## Rows of the session', '', '```', ...rows.map(describe), '```');
  if (result.state === 'FAIL') lines.push('', 'Also here: fail.png (the window when it failed), app-log.txt (the last 30 lines of the app log), video.mp4.');
  return lines.join('\n') + '\n';
}

async function once(name: string, agent: Agent, machine: Machine, out: string, body: (j: Journey) => Promise<void>): Promise<Result> {
  const bench = benchState();
  const token = readFileSync(bench.tokenFile, 'utf8').match(/^ASSEMBLY_TOKEN=(.*)$/m)?.[1];
  if (!token) throw new Error('no backend token in the bench token file');
  const kase = `${agent} on ${machine}`;
  const result: Result = { case: kase, state: 'PASS', reason: '', steps: [] };
  const control = new AbortController();
  const stop = () => control.abort(new Error('interrupted'));
  process.once('SIGINT', stop); process.once('SIGTERM', stop);
  // The socket outlives an interruption: Stop goes through it after the journey is stopped.
  const socket = await connectSocket(bench.api, token, { signal: new AbortController().signal });
  let video: Video | undefined;
  let feed: RowFeed | undefined;
  let session: string | undefined;
  const interrupted = new Promise<never>((_, reject) => control.signal.addEventListener('abort', () => reject(new Failed('interrupted', 'the journey was stopped (signal)')), { once: true }));
  interrupted.catch(() => {});
  const j: Journey = {
    agent, machine, marker: `GUAVA-${randomBytes(2).toString('hex').toUpperCase()}`,
    async openSession() {
      const project = bench.projects[machine];
      const created = await socket.call('sessions', 'create', { agent, clientId: randomUUID(), projectId: project.id, ...(machine === 'local' ? {} : { machine }) });
      session = created?.sessionId;
      if (typeof session !== 'string') throw new Failed('the session was made', JSON.stringify(created));
      feed = openRows(bench.api, token, session);
      await ax.focusSessions();
      // The rail draws only the rows on screen, so the search narrows it to the bench project's sessions.
      await ax.type('sessions.search', 'notes');
      await ax.click(`rows.session.${session}`);
      await ax.focusChat();
      return feed;
    },
    type: (id, text) => ax.type(id, text),
    click: id => ax.click(id),
    async expectVisible(id, seconds = 15) { await ax.waitFor(id, true, false, seconds); },
    async expectGone(id, seconds = 15) { await ax.waitFor(id, false, false, seconds); },
    async step(what, act) {
      const started = Date.now();
      try {
        const value = await Promise.race([act(), interrupted]);
        result.steps.push({ what, ok: true, ms: Date.now() - started });
        return value;
      } catch (error) {
        const seen = error instanceof Failed ? error.seen : (error as Error).message;
        result.steps.push({ what, ok: false, ms: Date.now() - started, seen });
        throw new Failed(what, seen);
      }
    },
    check(what, ok, seen) {
      result.steps.push({ what, ok, ms: 0, ...(ok ? {} : { seen }) });
      if (!ok) throw new Failed(what, seen);
    }
  };
  try {
    video = await startVideo(join(out, 'video.mp4'), bench.display);
    await body(j);
  } catch (error) {
    const blocked = providerBlock(feed?.all() ?? []);
    if (blocked) { result.state = 'BLOCKED'; result.reason = `provider: ${blocked}`; }
    else { result.state = 'FAIL'; result.reason = (error as Error).message.split('\n')[0]!; }
    if (result.state === 'FAIL') {
      try { shot(join(out, 'fail.png')); } catch {}
      try { writeFileSync(join(out, 'app-log.txt'), readFileSync(bench.appLog, 'utf8').split('\n').slice(-31).join('\n')); } catch {}
    }
  } finally {
    // Never leave the agent working: stop a run still going and wait for its end before closing anything.
    const rows = feed?.all() ?? [];
    const ended = (run: string) => rows.some(row => row.kind === 'turn' && row.runId === run && row.payload?.state !== 'started');
    const running = [...new Set(rows.filter(row => row.kind === 'turn' && row.payload?.state === 'started').map(row => row.runId!))].filter(run => !ended(run));
    for (const run of running) {
      try {
        await socket.call('send', 'stop', { runId: run, session });
        await feed!.waitFor(`run ${run} to end after Stop`, row => row.kind === 'turn' && row.runId === run && row.payload?.state !== 'started', 60);
        result.steps.push({ what: `stopped run ${run} left going`, ok: true, ms: 0 });
      } catch (error) { result.steps.push({ what: `stop run ${run} left going`, ok: false, ms: 0, seen: (error as Error).message }); }
    }
    await video?.stop();
    await feed?.close();
    await socket.close();
    process.off('SIGINT', stop); process.off('SIGTERM', stop);
    atomic(join(out, 'result.json'), JSON.stringify(result, null, 2) + '\n');
    atomic(join(out, 'report.md'), report(name, result, feed?.all() ?? []));
  }
  return result;
}

/** The journey's entry: one case from the command line, run once more when a rate or usage limit blocked it. */
export async function journey(name: string, body: (j: Journey) => Promise<void>): Promise<void> {
  const [agent, machine, out] = process.argv.slice(2) as [Agent, Machine, string];
  if (!['claude', 'codex'].includes(agent) || !['local', 'workbox-test'].includes(machine) || !out)
    throw new Error('usage: <journey>.ts <claude|codex> <local|workbox-test> <output folder>');
  mkdirSync(out, { recursive: true });
  let result = await once(name, agent, machine, out, body);
  if (result.state === 'BLOCKED' && isLimit(result.reason)) result = await once(name, agent, machine, out, body);
  console.log(`${result.state} ${result.case}${result.reason ? `: ${result.reason}` : ''}`);
  process.exitCode = result.state === 'PASS' ? 0 : result.state === 'BLOCKED' ? 3 : 1;
}
