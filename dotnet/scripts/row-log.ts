// Row-position diagnostics through the same control/bulk sockets as the native client.
// node row-log.ts <sessionId|latest> [--follow SECONDS] [--env FILE] [--port 9797]
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { connectSocket } from '../../scripts/features/socket.ts';
import { readRows, type Row, type RowPage } from '../../scripts/features/live-rows.ts';

type Session = { sessionId: string; title?: string; repoUrl: string };
type LogOptions = { api: string; token: string; target: string; followMs: number; signal: AbortSignal;
  log?: (line: string) => void };
function describe(row: Row): string {
  const p = row.payload;
  switch (row.kind) {
    case 'user': return JSON.stringify(p.text);
    case 'turn': return p.state;
    case 'tool': return `${p.name} ${p.state}: ${p.summary ?? ''}`;
    case 'delta': return `from block ${p.from}, ${p.text.length} new chars`;
    case 'message': return `${p.blocks.length} blocks`;
    case 'thinking': return `${p.text.length} chars${p.done ? ', done' : ''}`;
    case 'stats': return `${p.status}, ${p.durationMs ?? '?'} ms, ${p.toolCalls} tools`;
    case 'error': return `${p.code ?? 'error'}: ${p.message}`;
    default: return JSON.stringify(p).slice(0, 90);
  }
}
const clip = (text: string, max: number) => text.length > max ? text.slice(0, max) + '…' : text;
const line = (row: Row) =>
  `${String(row.seq).padStart(5)}  ${row.kind.padEnd(8)} ${(row.itemId ?? '-').slice(0, 34).padEnd(34)} ${clip(describe(row), 90)}`;

export async function logRows(options: LogOptions): Promise<void> {
  const { api, token, target, signal } = options;
  const log = options.log ?? console.log;
  const control = await connectSocket(api, token, { signal });
  let bulk: Awaited<ReturnType<typeof connectSocket>> | undefined;
  let subscription: Awaited<ReturnType<typeof control.subscribe>> | undefined;
  try {
    let session: Session | undefined;
    if (target === 'latest') {
      const page = await control.call('sessions', 'page', { limit: 1 });
      session = page.sessions[0];
    } else {
      subscription = await control.subscribe('sessions', target);
      session = subscription.state.items.find((entry: { key: string }) => entry.key === target)?.item;
      subscription.close(); subscription = undefined;
    }
    if (!session) throw new Error(`no session ${target}`);
    log(`session ${session.sessionId} "${session.title ?? 'untitled'}", repo ${session.repoUrl}`);
    log('\n1. Every row in seq order (a socket opened at 0)');
    const started = Date.now();
    const { rows, cut } = await readRows({ api, token, session: session.sessionId, signal, followMs: options.followMs,
      onRow: row => log((options.followMs > 0 ? `+${((Date.now() - started) / 1000).toFixed(1)}s`.padStart(8) : '') + line(row)) });
    log(`   ${rows.length} rows, seqs up to ${rows.at(-1)?.seq ?? cut}: no duplicates, prevSeq chain unbroken`);
    log('\n2. The session as the app opens it (pages of 100 items, newest first)');
    bulk = await connectSocket(api, token, { signal, endpoint: '/socket/bulk' });
    const pages: Row[][] = [];
    const cursors = new Set<number>();
    let before: number | null = null;
    do {
      const args = { session: session.sessionId, limit: 100, ...(before == null ? {} : { before }) };
      const page = await bulk.call('rows', 'page', args) as RowPage;
      if (!Array.isArray(page.rows)) throw new Error('rows/page omitted rows');
      pages.push(page.rows);
      log(`   page ${pages.length}: ${page.rows.length} items, first seqs ${page.rows[0]?.firstSeq}..${page.rows.at(-1)?.firstSeq}`);
      if (page.hasBefore && (!Number.isSafeInteger(page.before) || page.before! <= 0 || cursors.has(page.before!)))
        throw new Error('rows/page returned a non-progressing cursor');
      before = page.hasBefore ? page.before : null;
      if (before != null) cursors.add(before);
    } while (before != null);
    const stitched = pages.reverse().flat();
    const key = (row: Row) => row.itemId ?? `seq ${row.seq}`;
    const keys = stitched.map(key);
    if (new Set(keys).size !== keys.length) throw new Error('an item shows up on more than one page');
    if (stitched.some((row, index) => index > 0 && row.firstSeq <= stitched[index - 1]!.firstSeq))
      throw new Error('items are out of order');
    for (const row of stitched) log(`   at ${String(row.firstSeq).padStart(5)} ${line(row)}`);
    const whole = await bulk.call('rows', 'page', { session: session.sessionId, limit: 500 }) as RowPage;
    if (whole.rows.length < 500 && JSON.stringify(whole.rows.map(key)) !== JSON.stringify(keys))
      throw new Error('the stitched pages differ from one load');
    log(`   ${stitched.length} items on ${pages.length} pages; one load has ${whole.rows.length} items`);
    log('\nAll checks passed.');
  } finally {
    subscription?.close();
    await Promise.all([control.close(), bulk?.close()]);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const args = process.argv.slice(2);
  const option = (name: string, fallback: string) => args.includes(name) ? args[args.indexOf(name) + 1] : fallback;
  const target = args[0];
  const followSeconds = Number(option('--follow', '0'));
  const port = Number(option('--port', '9797'));
  if (!target || target.startsWith('--') || !Number.isFinite(followSeconds) || followSeconds < 0 ||
      !Number.isSafeInteger(port) || port < 1 || port > 65535) {
    console.error('usage: node row-log.ts <sessionId|latest> [--follow SECONDS] [--env FILE] [--port 9797]');
    process.exitCode = 2;
  } else {
    const deadline = new AbortController();
    const timer = setTimeout(() => deadline.abort(new Error('row-log deadline exceeded')), (followSeconds + 90) * 1000);
    try {
      const envFile = option('--env', join(homedir(), '.config', 'assembly-dotnet', 'env'))!;
      const token = readFileSync(envFile, 'utf8').match(/^ASSEMBLY_TOKEN=(.+)$/m)?.[1]?.trim();
      if (!token) throw new Error('env file is missing ASSEMBLY_TOKEN');
      await logRows({ api: `http://127.0.0.1:${port}`, token, target, followMs: followSeconds * 1000, signal: deadline.signal });
    } catch (error) { console.error(`PROBLEM: ${(error as Error).message}`); process.exitCode = 1; }
    finally { clearTimeout(timer); }
  }
}
