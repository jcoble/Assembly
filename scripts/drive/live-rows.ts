// Exact row catch-up at the subscription's initial seq; tokens travel only in hello.
import { pathToFileURL } from 'node:url';
import { connectSocket } from './socket.ts';

export type Row = { seq: number; prevSeq?: number; kind: string; runId?: string; itemId?: string;
  firstSeq: number; payload: Record<string, any> };
export type RowPage = { rows: Row[]; hasBefore: boolean; before: number | null };
type Options = { api: string; token: string; session: string; after?: number; signal: AbortSignal;
  followMs?: number; untilRun?: string; onRow?: (row: Row) => void };

// Omit after on subscribe: state.seq is the snapshot cut, not a bounded resume's starting seq.
// Hold concurrent live rows until bulk catch-up reaches the cut. Never infer completion from silence.
export async function readRows(options: Options): Promise<{ rows: Row[]; cut: number }> {
  const { api, token, session } = options;
  const failedStream = new AbortController();
  const signal = AbortSignal.any([options.signal, failedStream.signal]);
  const after = options.after ?? 0;
  if (!Number.isSafeInteger(after) || after < 0) throw new Error('after must be a nonnegative integer');
  const control = await connectSocket(api, token, { signal });
  let bulk: Awaited<ReturnType<typeof connectSocket>> | undefined;
  let subscription: Awaited<ReturnType<typeof control.subscribe>> | undefined;
  let followTimer: ReturnType<typeof setTimeout> | undefined;
  let followingEnded = false;
  let wake: (() => void) | undefined;
  let streamError: unknown;
  let pump: Promise<void> | undefined;
  try {
    subscription = await control.subscribe('rows', session);
    const cut = subscription.state.seq;
    if (!Number.isSafeInteger(cut) || cut < after) throw new Error('invalid rows snapshot seq');
    const held: Row[] = [];
    let heldBytes = 0;
    let liveLast = cut;
    pump = (async () => {
      try {
        for (;;) {
          const frame = await subscription!.next();
          if (frame.t !== 'rows' || !frame.row) throw new Error('expected rows frame');
          const row = frame.row as Row;
          if (!Number.isSafeInteger(row.seq) || row.seq <= liveLast)
            throw new Error(`duplicate or backwards live seq ${row.seq}`);
          if (row.prevSeq !== liveLast) throw new Error(`live seq ${row.seq} says prevSeq ${row.prevSeq}, expected ${liveLast}`);
          heldBytes += Buffer.byteLength(JSON.stringify(row));
          if (heldBytes > 16 * 1024 * 1024) throw new Error('pending live rows exceeded 16 MB');
          liveLast = row.seq; held.push(row); wake?.();
        }
      } catch (error) { streamError = error; failedStream.abort(error); wake?.(); }
    })();
    bulk = await connectSocket(api, token, { signal, endpoint: '/socket/bulk' });
    const rows: Row[] = [];
    let last = after;
    const accept = (row: Row) => {
      if (!Number.isSafeInteger(row.seq) || row.seq <= last) throw new Error(`duplicate or backwards seq ${row.seq}`);
      if (row.prevSeq !== last) throw new Error(`seq ${row.seq} says prevSeq ${row.prevSeq}, expected ${last}`);
      last = row.seq; rows.push(row); options.onRow?.(row);
    };
    while (last < cut) {
      const page = await bulk.call('rows', 'after', { session, after: last, limit: 500 }) as { rows: Row[] };
      if (!Array.isArray(page.rows) || page.rows.length === 0) throw new Error(`rows/after stopped at ${last} before snapshot ${cut}`);
      let previous = last;
      for (const row of page.rows) {
        if (row.seq <= previous || row.prevSeq !== previous) throw new Error(`invalid catch-up chain at seq ${row.seq}`);
        previous = row.seq;
        // A concurrent read may include rows beyond the cut. The subscription owns those rows.
        if (row.seq <= cut) accept(row);
      }
      if (last === cut) break;
      if (previous > cut) throw new Error(`catch-up skipped snapshot ${cut}`);
    }
    if (streamError) throw streamError;
    const ended = () => options.untilRun == null || rows.some(row =>
      row.kind === 'turn' && row.runId === options.untilRun &&
      ['finished', 'failed', 'cancelled'].includes(row.payload.state));
    if (options.followMs != null && options.followMs > 0)
      followTimer = setTimeout(() => { followingEnded = true; wake?.(); }, options.followMs);
    for (;;) {
      if (streamError) throw streamError;
      while (held.length) {
        const row = held.shift()!;
        heldBytes -= Buffer.byteLength(JSON.stringify(row));
        accept(row);
      }
      if (ended() && (!followTimer || followingEnded)) break;
      await new Promise<void>(resolve => { wake = resolve; });
    }
    return { rows, cut };
  } finally {
    clearTimeout(followTimer);
    subscription?.close();
    await Promise.all([control.close(), bulk?.close()]);
    await pump;
  }
}

export async function checkRows(env: NodeJS.ProcessEnv, signal: AbortSignal): Promise<void> {
  if (!env.CHECK4_RUN) throw new Error('CHECK4_RUN is required');
  const options = { api: env.CHECK4_API!, token: env.CHECK4_TOKEN!, session: env.CHECK4_SESSION!, signal };
  await readRows({ ...options, untilRun: env.CHECK4_RUN });
  // A fresh cut after the turn ends accounts for the final message replacing transient deltas.
  const { rows, cut } = await readRows(options);
  if (rows.length === 0) throw new Error('the socket from 0 sent no rows');
  console.log(`ok the socket from 0 sent ${rows.length} rows`);
  const skipped = rows.filter(row => row.seq !== row.prevSeq! + 1).length;
  console.log(`ok every row's prevSeq is the row before it, and seqs only rise (${skipped} skip past deleted deltas)`);
  if (rows.at(-1)!.seq < cut) throw new Error(`the socket stopped before snapshot ${cut}`);
  console.log(`ok the socket reaches the newest row (${rows.at(-1)!.seq})`);
  if (rows.length >= 2) {
    const index = Math.floor(rows.length / 2);
    const middle = rows[index - 1]!.seq;
    const resumed = await readRows({ ...options, after: middle });
    if (resumed.rows[0]?.seq !== rows[index]!.seq || resumed.rows[0]?.prevSeq !== middle)
      throw new Error(`the socket from ${middle} resumed at the wrong row`);
    console.log(`ok the socket from ${middle} starts at ${rows[index]!.seq} with prevSeq ${middle}`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const deadline = new AbortController();
  const timer = setTimeout(() => deadline.abort(new Error('rows check exceeded 90 seconds')), 90_000);
  try {
    if (process.argv[2] === '--check') await checkRows(process.env, deadline.signal);
    else {
      const result = await readRows({ api: process.env.CHECK4_API!, token: process.env.CHECK4_TOKEN!,
        session: process.env.CHECK4_SESSION!, after: Number(process.argv[2] ?? 0), signal: deadline.signal });
      console.log(JSON.stringify(result.rows.map(({ seq, prevSeq }) => ({ seq, prevSeq }))));
    }
  } catch (error) { console.error(`fail rows: ${(error as Error).message}`); process.exitCode = 1; }
  finally { clearTimeout(timer); }
}
