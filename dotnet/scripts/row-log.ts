// The row-position log for the step 3 checks: a thread's rows, read from the backend on this machine
// the two ways the webview reads them.
//   node row-log.ts <threadId|latest> [--follow SECONDS] [--env FILE] [--port 9797]
// 1. Every row in seq order, as a socket opened at 0 gets them, checked for duplicates and a broken prevSeq chain.
//    Seqs may skip: a message's deltas are deleted once the message is saved (RowStore.cs).
//    With --follow, the socket stays open and live rows are logged as they arrive, with their time.
// 2. The thread as the app opens it: pages of 100 items, newest first, stitched oldest first, each item
//    at its first seq (its place on screen). Checked against one load of up to 500 items.
// The token is read from the env file and never printed. Exits 1 if a check fails.
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';

// A row as the backend sends it. The payload's fields depend on the row's kind; describe() reads them.
type Payload = {
  text?: string; state?: string; name?: string; summary?: string; from?: number; blocks?: unknown[];
  done?: boolean; status?: string; durationMs?: number; toolCalls?: number; code?: string; message?: string;
};
type Row = { seq: number; prevSeq?: number; kind: string; itemId?: string; firstSeq: number; payload: Payload };
type Session = { sessionId: string; title?: string; repoUrl: string };
type Page = { rows: Row[]; hasBefore: boolean; before: number };
type Frame = { type: string; row: Row };

const args = process.argv.slice(2);
const option = (name: string, fallback: string): string => (args.includes(name) ? args[args.indexOf(name) + 1] : fallback);
const target = args[0];
if (!target || target.startsWith('--')) {
  console.error('usage: node row-log.ts <sessionId|latest> [--follow SECONDS] [--env FILE] [--port 9797]');
  process.exit(2);
}
const envFile = option('--env', `${homedir()}/.config/assembly-dotnet/env`);
const port = option('--port', '9797');
const followSeconds = Number(option('--follow', '0'));
const token = readFileSync(envFile, 'utf8').match(/^ASSEMBLY_TOKEN=(\w+)/m)![1];
const problems: string[] = [];
const started = Date.now();

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`http://127.0.0.1:${port}${path}`, { headers: { authorization: `Bearer ${token}` } });
  if (!res.ok) throw new Error(`GET ${path} returned ${res.status}`);
  return (await res.json()) as T;
}

function describe(row: Row): string {
  const p = row.payload;
  switch (row.kind) {
    case 'user': return JSON.stringify(p.text);
    case 'turn': return p.state!;
    case 'tool': return `${p.name} ${p.state}: ${p.summary ?? ''}`;
    case 'delta': return `from block ${p.from}, ${p.text!.length} new chars`;
    case 'message': return `${p.blocks!.length} blocks`;
    case 'thinking': return `${p.text!.length} chars${p.done ? ', done' : ''}`;
    case 'stats': return `${p.status}, ${p.durationMs ?? '?'} ms, ${p.toolCalls} tools`;
    case 'error': return `${p.code ?? 'error'}: ${p.message}`;
    default: return JSON.stringify(p).slice(0, 90);
  }
}

const clip = (text: string, max: number): string => (text.length > max ? text.slice(0, max) + '…' : text);
const line = (row: Row): string =>
  `${String(row.seq).padStart(5)}  ${row.kind.padEnd(8)} ${(row.itemId ?? '-').slice(0, 34).padEnd(34)} ${clip(describe(row), 90)}`;

const sessions = (await get<{ sessions: Session[] }>('/sessions?limit=200')).sessions;
const session = target === 'latest' ? sessions[0] : sessions.find((t) => t.sessionId === target);
if (!session) throw new Error(`no session ${target}`);
console.log(`session ${session.sessionId} "${session.title ?? 'untitled'}", repo ${session.repoUrl}`);

console.log('\n1. Every row in seq order (a socket opened at 0)');
let last = 0;
let count = 0;
await new Promise<void>((resolve, reject) => {
  const url = `ws://127.0.0.1:${port}/live?session=${encodeURIComponent(session.sessionId)}&after=0&token=${token}`;
  const socket = new WebSocket(url);
  let following = false;
  let quiet: ReturnType<typeof setTimeout> | undefined;
  // The backlog comes at once; 1.5 s with no row means it's all here.
  const settle = () => {
    clearTimeout(quiet);
    quiet = setTimeout(() => {
      if (followSeconds <= 0) return socket.close();
      following = true;
      console.log(`   -- backlog done at seq ${last}; following live rows for ${followSeconds} s --`);
      setTimeout(() => socket.close(), followSeconds * 1000);
    }, 1500);
  };
  socket.onopen = settle;
  socket.onerror = () => reject(new Error('the socket failed'));
  socket.onclose = () => resolve();
  socket.onmessage = (event) => {
    const frame = JSON.parse(event.data) as Frame;
    if (frame.type !== 'row') return;
    const row = frame.row;
    if (row.seq <= last) problems.push(`seq ${row.seq} came after ${last}`);
    else if (row.prevSeq !== last) problems.push(`seq ${row.seq} says prevSeq ${row.prevSeq}, the last row was ${last}`);
    last = Math.max(last, row.seq);
    count++;
    const time = following ? `+${((Date.now() - started) / 1000).toFixed(1)}s`.padStart(8) : '';
    console.log(time + line(row));
    if (!following) settle();
  };
});
console.log(`   ${count} rows, seqs up to ${last}: ${!problems.length ? 'no duplicates, prevSeq chain unbroken' : 'PROBLEM'}`);

console.log('\n2. The session as the app opens it (pages of 100 items, newest first)');
const pages: Row[][] = [];
let before: number | null = null;
do {
  const page: Page = await get<Page>(`/sessions/${session.sessionId}/rows?${before === null ? '' : `before=${before}&`}limit=100`);
  pages.push(page.rows);
  console.log(`   page ${pages.length}: ${page.rows.length} items, first seqs ${page.rows[0]?.firstSeq}..${page.rows.at(-1)?.firstSeq}`);
  before = page.hasBefore ? page.before : null;
} while (before !== null);
const stitched = pages.reverse().flat();
for (const row of stitched) console.log(`   at ${String(row.firstSeq).padStart(5)} ${line(row)}`);

const key = (row: Row): string => row.itemId ?? `seq ${row.seq}`;
const keys = stitched.map(key);
if (new Set(keys).size !== keys.length) problems.push('an item shows up on more than one page');
if (stitched.some((row, i) => i > 0 && row.firstSeq <= stitched[i - 1].firstSeq)) problems.push('items are out of order');
const whole = (await get<{ rows: Row[] }>(`/sessions/${session.sessionId}/rows?limit=500`)).rows;
if (whole.length < 500 && JSON.stringify(whole.map(key)) !== JSON.stringify(keys))
  problems.push('the stitched pages differ from one load');
console.log(`   ${stitched.length} items on ${pages.length} pages; one load has ${whole.length} items`);

console.log(problems.length ? `\nPROBLEMS:\n  ${problems.join('\n  ')}` : '\nAll checks passed.');
process.exit(problems.length ? 1 : 0);
